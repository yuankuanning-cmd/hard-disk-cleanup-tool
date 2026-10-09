using System.Collections;
using System.IO;
using System.Text.Json;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

/// <summary>Bounded page caches over the persistent index. No million-node object graph.</summary>
internal sealed class IndexedNodeSource(string path, bool readOnly = false)
{
    private readonly Dictionary<long, DiskTreeNode> _directories = [];
    private readonly Queue<long> _directoryOrder = new();
    private readonly object _cacheSync = new();
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public bool CanRecycle { get; private set; }
    internal long LoadedRows { get; private set; }
    public void Invalidate() => CanRecycle = false;
    internal SqliteConnection Open() => SqliteScanIndex.Open(Path, true);
    private static SqliteCommand Query(SqliteConnection connection, string sql, params (string Key, object Value)[] args)
    {
        var cmd = connection.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }
    internal DiskTreeNode MakeNode(IndexRow row)
    {
        lock (_cacheSync)
        {
        if (row.IsDirectory && _directories.TryGetValue(row.Id, out var cached)) return cached;
        LoadedRows++;
        var node = new DiskTreeNode
        {
            Source = this, IndexId = row.Id, ParentIndexId = row.ParentId,
            FileIdentity = row.Identity,
            Name = string.IsNullOrEmpty(row.Name) ? row.Path : row.Name, FullPath = row.Path,
            IsDirectory = row.IsDirectory, IsScanRoot = row.ParentId is null,
            LogicalBytes = row.Logical, AllocatedBytes = row.Allocated, FileCount = row.FileCount,
            LastWriteTimeUtc = new DateTime(row.Modified, DateTimeKind.Utc), Extension = row.Extension,
            IsIncomplete = row.Incomplete, IsExcluded = row.Excluded,
            StorageNote = row.OwnIncomplete ? "读取不完整" : row.Links > 1 ? $"硬链接（{row.Links} 个名称，共享占用仅归属一次）" :
                ((FileAttributes)row.Attributes).HasFlag(FileAttributes.SparseFile) ? "稀疏文件" :
                ((FileAttributes)row.Attributes).HasFlag(FileAttributes.Compressed) ? "NTFS 压缩" : ""
        };
        if (row.IsDirectory && !row.Incomplete && !row.Excluded && row.PreviousTime is { } time &&
            row.PreviousAllocated is { } bytes && row.PreviousLogical is { } logical)
        {
            node.GrowthBytes = row.Allocated - bytes; node.LogicalGrowthBytes = row.Logical - logical;
            node.ComparedAt = new DateTimeOffset(time, TimeSpan.Zero);
            node.GrowthBytesPerDay = node.GrowthBytes.Value / Math.Max(1d / 86400, (DateTimeOffset.UtcNow - node.ComparedAt.Value).TotalDays);
        }
        if (row.IsDirectory)
        {
            _directories[row.Id] = node; _directoryOrder.Enqueue(row.Id);
            while (_directories.Count > 512) _directories.Remove(_directoryOrder.Dequeue());
        }
        return node;
        }
    }
    public DiskTreeNode? GetNode(long id)
    {
        lock (_cacheSync) if (_directories.TryGetValue(id, out var node)) return node;
        using var conn = Open(); using var cmd = Query(conn, $"SELECT {SqliteScanIndex.Columns} FROM entries WHERE id=$id", ("$id", id));
        using var reader = cmd.ExecuteReader(); return reader.Read() ? MakeNode(SqliteScanIndex.Read(reader)) : null;
    }
    public IndexRow? Find(string path)
    {
        using var conn = Open(); using var cmd = Query(conn, $"SELECT {SqliteScanIndex.Columns} FROM entries WHERE path_key=$key", ("$key", SqliteScanIndex.Key(path)));
        using var reader = cmd.ExecuteReader(); return reader.Read() ? SqliteScanIndex.Read(reader) : null;
    }
    public PagedNodeList GetChildren(DiskTreeNode parent, bool directoriesOnly = false, string[]? terms = null, string sort = "allocated DESC,name,id")
    {
        var clause = "parent_id=$parent" + (directoriesOnly ? " AND is_dir=1" : "");
        var args = new List<(string Key, object Value)> { ("$parent", parent.IndexId) };
        for (var i = 0; i < (terms?.Length ?? 0); i++)
        {
            // Every direct child has the same parent path. Avoid reading a million full paths for a filename search.
            if (parent.FullPath.Contains(terms![i], StringComparison.OrdinalIgnoreCase)) continue;
            var field = terms[i].Contains('\\') || terms[i].Contains('/') ? "path" : "name";
            clause += $" AND {field} LIKE $q{i} ESCAPE '!'";
            args.Add(($"$q{i}", "%" + terms![i].Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%"));
        }
        if (sort is not ("allocated DESC,name,id" or "allocated ASC,name,id" or "logical DESC,name,id" or "logical ASC,name,id" or "name ASC,id" or "name DESC,id" or "file_count DESC,name,id" or "file_count ASC,name,id" or "modified DESC,name,id" or "modified ASC,name,id")) throw new ArgumentException("无效排序");
        return new(this, clause, sort, args.ToArray());
    }
    public IReadOnlyList<DiskTreeNode> TopChildren(DiskTreeNode parent, bool allocated, int count = 351) =>
        ReadNodes($"SELECT {SqliteScanIndex.Columns} FROM entries WHERE parent_id=$id AND {(allocated ? "allocated" : "logical")}>0 ORDER BY {(allocated ? "allocated" : "logical")} DESC,id LIMIT $limit", ("$id", parent.IndexId), ("$limit", count));
    private DiskTreeNode[] ReadNodes(string sql, params (string Key, object Value)[] args)
    {
        using var conn = Open(); using var cmd = Query(conn, sql, args); using var reader = cmd.ExecuteReader();
        var result = new List<DiskTreeNode>(); while (reader.Read()) result.Add(MakeNode(SqliteScanIndex.Read(reader))); return result.ToArray();
    }
    public IEnumerable<DiskTreeNode> EnumerateSubtree(long id)
    {
        using var conn = Open(); using var cmd = Query(conn, $"""
            WITH RECURSIVE descendants(id) AS (SELECT $id UNION ALL SELECT e.id FROM entries e JOIN descendants d ON e.parent_id=d.id)
            SELECT {string.Join(',', SqliteScanIndex.Columns.Split(',').Select(c => "e." + c))} FROM entries e JOIN descendants d ON e.id=d.id
            """, ("$id", id));
        using var reader = cmd.ExecuteReader(); while (reader.Read()) yield return MakeNode(SqliteScanIndex.Read(reader));
    }
    public DiskAnalysisResult ReadResult(long minimum, int maximum = 200)
    {
        using var conn = Open();
        string? Meta(string key) { using var cmd = Query(conn, "SELECT value FROM meta WHERE key=$key", ("$key", key)); return cmd.ExecuteScalar() as string; }
        if (Meta("schema") != SqliteScanIndex.SchemaVersion.ToString()) throw new IOException("不是支持的空间扫描快照。");
        var status = Meta("status");
        CanRecycle = !readOnly && status == "complete";
        var roots = ReadNodes($"SELECT {SqliteScanIndex.Columns} FROM entries WHERE parent_id IS NULL ORDER BY path_key");
        var largest = ReadNodes($"SELECT {SqliteScanIndex.Columns} FROM entries INDEXED BY ix_largest WHERE is_dir=0 AND excluded=0 AND logical>=$min ORDER BY allocated DESC,id LIMIT $max", ("$min", minimum), ("$max", maximum));
        var growth = ReadNodes($"SELECT {SqliteScanIndex.Columns} FROM entries WHERE is_dir=1 AND incomplete=0 AND excluded=0 AND prev_time IS NOT NULL AND allocated>prev_allocated ORDER BY (allocated-prev_allocated)*1.0/MAX(1,($now-prev_time)) DESC LIMIT 100", ("$now", DateTimeOffset.UtcNow.UtcTicks));
        var extensions = new List<DiskExtensionSummary>();
        using (var cmd = Query(conn, "SELECT extension,logical,allocated,file_count FROM extension_totals ORDER BY allocated DESC LIMIT 200"))
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) extensions.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        using var pending = Query(conn, "SELECT COUNT(*) FROM work WHERE state<>2");
        var hasCheckpoint = (long)pending.ExecuteScalar()! > 0 || status is "running" or "partial";
        var warnings = JsonSerializer.Deserialize<string[]>(Meta("warnings") ?? "[]") ?? [];
        var stats = JsonSerializer.Deserialize<ScanEngineStatistics>(Meta("statistics") ?? "null");
        var time = long.TryParse(Meta("scanned_ticks"), out var ticks) ? new DateTimeOffset(ticks, TimeSpan.Zero) : DateTimeOffset.UtcNow;
        var items = roots.SelectMany(n => TopChildren(n, true, 30)).Where(n => n.IsDirectory).Take(30).Concat(largest).Select(n =>
            new DiskAnalysisItem(n.IsDirectory ? "一级目录" : "文件", n.Name, n.FullPath, n.LogicalBytes, n.LogicalSize, n.AllocatedBytes, n.AllocatedSize, n.State, n.LastWriteTimeUtc.ToLocalTime())).ToArray();
        return new(roots.FirstOrDefault()?.FullPath ?? "", roots.Sum(r => r.LogicalBytes), roots.Sum(r => r.AllocatedBytes), (int)Math.Min(int.MaxValue, roots.Sum(r => r.FileCount)), items, warnings)
        {
            Roots = roots, LargestFiles = largest, GrowthDirectories = growth, Extensions = extensions,
            IndexPath = Path, IsHistoricalSnapshot = readOnly, HasCheckpoint = hasCheckpoint, EngineStatistics = stats,
            IsApproximate = Meta("approximate") == "1", ScannedAt = time,
            ComparisonNote = "同范围、排除规则和计量模式的上次完成扫描作为增长基准；未读完整的目录不参与比较。" + (readOnly ? " 当前为只读快照，清理前请重新扫描。" : "")
        };
    }
    public static void SaveSnapshot(string source, string destination)
    {
        destination = System.IO.Path.GetFullPath(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("快照不能覆盖正在使用的索引。");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = SqliteScanIndex.Open(source, true))
            using (var output = SqliteScanIndex.Open(temporary, false)) input.BackupDatabase(output);
            SqliteConnection.ClearAllPools();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal sealed class PagedNodeList : IList, IReadOnlyList<DiskTreeNode>
    {
        private readonly IndexedNodeSource _source;
        private readonly string _clause, _sort;
        private readonly (string Key, object Value)[] _args;
        private readonly Dictionary<int, DiskTreeNode[]> _pages = [];
        private readonly Queue<int> _order = new();
        public int Count { get; }
        public PagedNodeList(IndexedNodeSource source, string clause, string sort, (string Key, object Value)[] args)
        {
            _source = source; _clause = clause; _sort = sort; _args = args;
            using var conn = source.Open(); using var cmd = Query(conn, "SELECT COUNT(*) FROM entries WHERE " + clause, args);
            Count = checked((int)(long)cmd.ExecuteScalar()!);
        }
        public DiskTreeNode this[int index]
        {
            get
            {
                if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                var page = index / 128;
                if (!_pages.TryGetValue(page, out var rows))
                {
                    var where = _args.Length > 1 ? $"id IN (SELECT id FROM entries WHERE {_clause})" : _clause;
                    rows = _source.ReadNodes($"SELECT {SqliteScanIndex.Columns} FROM entries WHERE {where} ORDER BY {_sort} LIMIT 128 OFFSET $offset", [.. _args, ("$offset", page * 128)]);
                    _pages[page] = rows; _order.Enqueue(page);
                    while (_pages.Count > 8) _pages.Remove(_order.Dequeue());
                }
                return rows[index % 128];
            }
        }
        object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
        public bool IsReadOnly => true; public bool IsFixedSize => true; public bool IsSynchronized => false; public object SyncRoot => this;
        public IEnumerator<DiskTreeNode> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Contains(object? value) => value is DiskTreeNode n && n.Source == _source && _source.Find(n.FullPath) is { } row && Matches(row.Id);
        private bool Matches(long id) { using var conn = _source.Open(); using var cmd = Query(conn, $"SELECT 1 FROM entries WHERE {_clause} AND id=$id", [.. _args, ("$id", id)]); return cmd.ExecuteScalar() is not null; }
        public int IndexOf(object? value)
        {
            if (value is not DiskTreeNode node || node.Source != _source) return -1;
            using var conn = _source.Open(); using var cmd = Query(conn, $"SELECT position FROM (SELECT id,ROW_NUMBER() OVER(ORDER BY {_sort})-1 position FROM entries WHERE {_clause}) WHERE id=$id", [.. _args, ("$id", node.IndexId)]);
            return cmd.ExecuteScalar() is long result ? checked((int)result) : -1;
        }
        public void CopyTo(Array array, int index) { foreach (var item in this) array.SetValue(item, index++); }
        public int Add(object? value) => throw new NotSupportedException(); public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException(); public void Remove(object? value) => throw new NotSupportedException(); public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
