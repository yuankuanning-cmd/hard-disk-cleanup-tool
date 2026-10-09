using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

internal sealed record IndexRow(long Id, long? ParentId, string Path, string Root, string Name,
    bool IsDirectory, int Depth, long Logical, long RawAllocated, long Allocated, long FileCount,
    long Modified, int Attributes, string Identity, uint Links, string Extension,
    bool OwnIncomplete, bool Incomplete, bool Excluded, long Seen, string FileReference,
    string? Fingerprint, bool ForceRefresh, long? PreviousLogical, long? PreviousAllocated, long? PreviousTime);

internal sealed record IndexedFileRecord(string Path, long ParentId, string Root, string Name,
    bool IsDirectory, int Depth, long Logical, long Allocated, long Modified, int Attributes,
    string Identity, uint Links, string Extension, bool Incomplete, bool Excluded, string FileReference,
    bool Reused, bool QueueDirectory, DirectoryScanJob? Job);

internal sealed class DirectoryScanJob(IndexRow row)
{
    public IndexRow Row { get; } = row;
    public int Outstanding;
    public volatile bool EnumerationEnded;
    public volatile bool Failed;
    public bool Finalized;
}

/// <summary>One serialized writer, WAL readers, transactional checkpoints. No full-file dictionary.</summary>
internal sealed class SqliteScanIndex : IDisposable
{
    internal const int SchemaVersion = 2;
    internal const string Columns = "id,parent_id,path,root_path,name,is_dir,depth,logical,raw_allocated,allocated,file_count,modified,attributes,identity,links,extension,own_incomplete,incomplete,excluded,seen,file_ref,fingerprint,force_refresh,prev_logical,prev_allocated,prev_time";
    private readonly SqliteConnection _writer;
    private readonly object _sync = new();
    private SqliteTransaction? _transaction;
    private SqliteCommand? _reuseCommand;
    public string Path { get; }
    public long Generation { get; private set; }
    public long RemovedEntries { get; private set; }
    public SqliteScanIndex(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _writer = Open(Path, false);
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA cache_size=-8192; PRAGMA temp_store=FILE;");
        Execute("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entries(
              id INTEGER PRIMARY KEY,parent_id INTEGER REFERENCES entries(id) ON DELETE CASCADE,
              path TEXT NOT NULL,path_key TEXT NOT NULL UNIQUE,root_path TEXT NOT NULL,name TEXT NOT NULL,
              is_dir INTEGER NOT NULL,depth INTEGER NOT NULL,logical INTEGER NOT NULL DEFAULT 0,
              raw_allocated INTEGER NOT NULL DEFAULT 0,allocated INTEGER NOT NULL DEFAULT 0,file_count INTEGER NOT NULL DEFAULT 0,
              modified INTEGER NOT NULL,attributes INTEGER NOT NULL,identity TEXT NOT NULL DEFAULT '',links INTEGER NOT NULL DEFAULT 1,
              extension TEXT NOT NULL DEFAULT '',own_incomplete INTEGER NOT NULL DEFAULT 0,incomplete INTEGER NOT NULL DEFAULT 0,
              excluded INTEGER NOT NULL DEFAULT 0,seen INTEGER NOT NULL DEFAULT 0,file_ref TEXT NOT NULL DEFAULT '',
              fingerprint TEXT,force_refresh INTEGER NOT NULL DEFAULT 0,prev_logical INTEGER,prev_allocated INTEGER,prev_time INTEGER);
            CREATE TABLE IF NOT EXISTS work(entry_id INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,state INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_parent_area ON entries(parent_id,allocated DESC,name,id);
            CREATE INDEX IF NOT EXISTS ix_parent_kind_area ON entries(parent_id,is_dir,allocated DESC,id);
            CREATE INDEX IF NOT EXISTS ix_parent_name ON entries(parent_id,name);
            CREATE INDEX IF NOT EXISTS ix_parent_logical ON entries(parent_id,logical DESC,name,id);
            CREATE INDEX IF NOT EXISTS ix_parent_count ON entries(parent_id,file_count DESC,name,id);
            CREATE INDEX IF NOT EXISTS ix_parent_time ON entries(parent_id,modified DESC,name,id);
            CREATE INDEX IF NOT EXISTS ix_largest ON entries(allocated DESC,id,logical) WHERE is_dir=0 AND excluded=0;
            CREATE INDEX IF NOT EXISTS ix_depth ON entries(is_dir,depth);
            CREATE INDEX IF NOT EXISTS ix_physical ON entries(identity,path_key) WHERE links>1 AND is_dir=0;
            CREATE INDEX IF NOT EXISTS ix_ref ON entries(file_ref);
            CREATE INDEX IF NOT EXISTS ix_size ON entries(is_dir,excluded,logical);
            CREATE INDEX IF NOT EXISTS ix_work_state ON work(state,entry_id);
            CREATE TABLE IF NOT EXISTS extension_totals(extension TEXT PRIMARY KEY,logical INTEGER,allocated INTEGER,file_count INTEGER);
            """);
        var version = GetMeta("schema");
        if (version is not null && version != SchemaVersion.ToString()) throw new IOException("索引版本不匹配，请使用新的索引目录重新扫描。");
        SetMeta("schema", SchemaVersion.ToString());
        Generation = long.TryParse(GetMeta("generation"), out var generation) ? generation : 0;
    }
    internal static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA cache_size=-4096; PRAGMA temp_store=FILE;" + (readOnly ? "PRAGMA query_only=ON;" : "PRAGMA foreign_keys=ON;");
        command.ExecuteNonQuery(); return connection;
    }
    public string? GetMeta(string key)
    {
        lock (_sync)
        {
            using var command = Command("SELECT value FROM meta WHERE key=$key", ("$key", key));
            return command.ExecuteScalar() as string;
        }
    }
    public void SetMeta(string key, string value)
    {
        lock (_sync) Execute("INSERT INTO meta VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
    }
    public void Begin(IReadOnlyList<string> roots, IReadOnlyList<string> exclusions, bool approximate, bool resume)
    {
        lock (_sync)
        {
            if (resume)
            {
                if (Generation == 0 || (PendingCount == 0 && GetMeta("status") is not ("running" or "partial"))) throw new InvalidOperationException("此范围没有未完成的扫描检查点。");
                Execute("UPDATE work SET state=0 WHERE state=1");
            }
            else
            {
                using var transaction = _writer.BeginTransaction();
                _transaction = transaction;
                if (GetMeta("status") is "complete" or "completed_partial" && long.TryParse(GetMeta("completed_ticks"), out var ticks))
                    Execute("UPDATE entries SET prev_logical=logical,prev_allocated=allocated,prev_time=$time WHERE is_dir=1 AND incomplete=0 AND excluded=0", ("$time", ticks));
                Execute("DELETE FROM work");
                Generation++;
                SetMeta("generation", Generation.ToString());
                SetMeta("started_ticks", DateTimeOffset.UtcNow.UtcTicks.ToString());
                transaction.Commit();
                _transaction = null;
            }
            SetMeta("roots", JsonSerializer.Serialize(roots)); SetMeta("exclusions", JsonSerializer.Serialize(exclusions));
            SetMeta("approximate", approximate ? "1" : "0"); SetMeta("status", "running");
        }
    }
    public IndexRow? Find(string path)
    {
        lock (_sync)
        {
            using var command = Command($"SELECT {Columns} FROM entries WHERE path_key=$path", ("$path", Key(path)));
            using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null;
        }
    }
    public long SeedRoot(string path, bool excluded, bool queue = true)
    {
        lock (_sync)
        {
            var row = new IndexedFileRecord(path, 0, path, new DirectoryInfo(path).Name, true, 0, 0, 0,
                Directory.GetLastWriteTimeUtc(path).Ticks, (int)FileAttributes.Directory, "", 1, "", false, excluded,
                WindowsFileStorageService.GetDirectoryReference(path).ToString("X16"), false, false, null);
            var id = Upsert(row);
            if (!excluded && queue) Queue(id);
            return id;
        }
    }
    public void Queue(long id) { lock (_sync) Execute("INSERT OR IGNORE INTO work(entry_id,state) VALUES($id,0)", ("$id", id)); }
    public IndexRow? Claim()
    {
        lock (_sync)
        {
            using var command = Command($"SELECT {string.Join(',', Columns.Split(',').Select(c => "e." + c))} FROM work w INDEXED BY ix_work_state JOIN entries e ON e.id=w.entry_id WHERE w.state=0 AND e.is_dir=1 AND e.excluded=0 ORDER BY w.entry_id LIMIT 1");
            IndexRow? result;
            using (var reader = command.ExecuteReader()) result = reader.Read() ? Read(reader) : null;
            if (result is not null) Execute("UPDATE work SET state=1 WHERE entry_id=$id", ("$id", result.Id));
            return result;
        }
    }
    public long PendingCount { get { lock (_sync) { using var cmd = Command("SELECT COUNT(*) FROM work WHERE state IN (0,1)"); return (long)cmd.ExecuteScalar()!; } } }
    public void WriteBatch(IReadOnlyList<IndexedFileRecord> records, bool incremental)
    {
        lock (_sync)
        {
            using var transaction = _writer.BeginTransaction();
            _transaction = transaction;
            var parents = new Dictionary<long, bool>();
            foreach (var record in records)
            {
                if (!parents.TryGetValue(record.ParentId, out var parentExists))
                {
                    using var check = Command("SELECT 1 FROM entries WHERE id=$id AND is_dir=1", ("$id", record.ParentId));
                    parents[record.ParentId] = parentExists = check.ExecuteScalar() is not null;
                }
                if (parentExists)
                {
                    var id = Upsert(record);
                    if (record.IsDirectory && !record.Excluded && (!incremental || record.QueueDirectory)) Queue(id);
                }
                if (record.Job is { } job) Interlocked.Decrement(ref job.Outstanding);
            }
            foreach (var job in records.Select(r => r.Job).Where(j => j is not null).Distinct()) FinishJob(job!);
            transaction.Commit();
            _transaction = null;
        }
    }
    public void FinishJob(DirectoryScanJob job)
    {
        lock (_sync)
        {
            if (job.Finalized || !job.EnumerationEnded || Volatile.Read(ref job.Outstanding) != 0) return;
            job.Finalized = true;
            Execute("UPDATE entries SET own_incomplete=$failed,modified=$modified WHERE id=$id", ("$failed", job.Failed ? 1 : 0),
                ("$modified", SafeDirectoryTime(job.Row.Path, job.Row.Modified)), ("$id", job.Row.Id));
            if (!job.Failed)
            {
                using var count = Command("""
                    WITH RECURSIVE removed(id) AS (SELECT id FROM entries WHERE parent_id=$id AND seen<>$g
                    UNION ALL SELECT e.id FROM entries e JOIN removed r ON e.parent_id=r.id) SELECT COUNT(*) FROM removed
                    """, ("$id", job.Row.Id), ("$g", Generation));
                RemovedEntries += (long)count.ExecuteScalar()!;
                Execute("DELETE FROM entries WHERE parent_id=$id AND seen<>$g", ("$id", job.Row.Id), ("$g", Generation));
            }
            Execute("UPDATE work SET state=2 WHERE entry_id=$id", ("$id", job.Row.Id));
        }
    }
    private long Upsert(IndexedFileRecord record)
    {
        if (record.Reused && !record.IsDirectory)
        {
            _reuseCommand ??= Command("UPDATE entries SET seen=$g WHERE path_key=$key RETURNING id", ("$g", Generation), ("$key", Key(record.Path)));
            _reuseCommand.Transaction = _transaction;
            _reuseCommand.Parameters["$g"].Value = Generation; _reuseCommand.Parameters["$key"].Value = Key(record.Path);
            if (_reuseCommand.ExecuteScalar() is long reusedId) return reusedId;
        }
        using var command = Command("""
            INSERT INTO entries(parent_id,path,path_key,root_path,name,is_dir,depth,logical,raw_allocated,allocated,file_count,modified,
              attributes,identity,links,extension,own_incomplete,incomplete,excluded,seen,file_ref)
            VALUES($parent,$path,$key,$root,$name,$dir,$depth,$logical,$allocated,$allocated,$count,$modified,$attrs,$identity,$links,$ext,$bad,$bad,$excluded,$g,$ref)
            ON CONFLICT(path_key) DO UPDATE SET parent_id=excluded.parent_id,name=excluded.name,is_dir=excluded.is_dir,
              depth=excluded.depth,logical=excluded.logical,raw_allocated=excluded.raw_allocated,allocated=excluded.allocated,
              file_count=excluded.file_count,modified=excluded.modified,attributes=excluded.attributes,identity=excluded.identity,
              links=excluded.links,extension=excluded.extension,own_incomplete=excluded.own_incomplete,incomplete=excluded.incomplete,
              excluded=excluded.excluded,seen=excluded.seen,file_ref=excluded.file_ref,
              fingerprint=CASE WHEN entries.logical=excluded.logical AND entries.modified=excluded.modified AND entries.attributes=excluded.attributes
                AND entries.identity=excluded.identity AND entries.force_refresh=0 AND excluded.own_incomplete=0 THEN entries.fingerprint ELSE NULL END,
              force_refresh=0
            RETURNING id
            """, ("$parent", record.ParentId == 0 ? DBNull.Value : record.ParentId), ("$path", record.Path), ("$key", Key(record.Path)),
            ("$root", record.Root), ("$name", record.Name), ("$dir", record.IsDirectory ? 1 : 0), ("$depth", record.Depth),
            ("$logical", record.Logical), ("$allocated", record.Allocated), ("$count", !record.IsDirectory && !record.Excluded ? 1 : 0),
            ("$modified", record.Modified), ("$attrs", record.Attributes), ("$identity", record.Identity), ("$links", (long)record.Links),
            ("$ext", record.Extension), ("$bad", record.Incomplete ? 1 : 0), ("$excluded", record.Excluded ? 1 : 0), ("$g", Generation), ("$ref", record.FileReference));
        var id = (long)command.ExecuteScalar()!;
        if (record.IsDirectory)
            Execute("UPDATE entries SET prev_logical=0,prev_allocated=0,prev_time=(SELECT prev_time FROM entries WHERE id=$parent) WHERE id=$id AND prev_time IS NULL AND seen=$g AND $parent<>0", ("$parent", record.ParentId), ("$id", id), ("$g", Generation));
        if (!record.IsDirectory)
        { Execute("DELETE FROM entries WHERE parent_id=$id", ("$id", id)); Execute("DELETE FROM work WHERE entry_id=$id", ("$id", id)); }
        return id;
    }
    public void Invalidate(ulong reference, string? path)
    {
        lock (_sync) Execute("UPDATE entries SET force_refresh=1,fingerprint=NULL WHERE (file_ref=$ref AND file_ref<>'0000000000000000') OR path_key=$path",
            ("$ref", reference.ToString("X16")), ("$path", path is null ? "" : Key(path)));
    }
    public void Aggregate(bool partial, CancellationToken token = default)
    {
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            using var transaction = _writer.BeginTransaction();
            _transaction = transaction;
            Execute("UPDATE entries SET allocated=raw_allocated,incomplete=own_incomplete WHERE is_dir=0 AND (allocated<>raw_allocated OR incomplete<>own_incomplete)");
            Execute("""
                UPDATE entries SET allocated=0 WHERE id IN (SELECT id FROM
                 (SELECT id,ROW_NUMBER() OVER(PARTITION BY identity ORDER BY path_key) AS position FROM entries
                   WHERE is_dir=0 AND links>1 AND identity<>'' AND excluded=0) WHERE position>1)
                """);
            using var depthQuery = Command("SELECT COALESCE(MAX(depth),0) FROM entries WHERE is_dir=1");
            var maximumDepth = Convert.ToInt32(depthQuery.ExecuteScalar());
            for (var depth = maximumDepth; depth >= 0; depth--)
            {
                token.ThrowIfCancellationRequested();
                Execute("""
                    UPDATE entries AS directory SET (logical,allocated,file_count,incomplete)=
                    (SELECT COALESCE(SUM(logical),0),COALESCE(SUM(allocated),0),COALESCE(SUM(file_count),0),
                      MAX(directory.own_incomplete,COALESCE(MAX(incomplete),0),
                        CASE WHEN EXISTS(SELECT 1 FROM work WHERE entry_id=directory.id AND state<>2) THEN 1 ELSE 0 END)
                    FROM entries WHERE parent_id=directory.id)
                    WHERE directory.is_dir=1 AND directory.excluded=0 AND directory.depth=$depth
                    """, ("$depth", depth));
            }
            using var bad = Command("SELECT COUNT(*) FROM entries WHERE parent_id IS NULL AND incomplete=1");
            var hasErrors = (long)bad.ExecuteScalar()! > 0;
            SetMeta("status", partial ? "partial" : hasErrors ? "completed_partial" : "complete");
            Execute("DELETE FROM extension_totals; INSERT INTO extension_totals SELECT extension,SUM(logical),SUM(allocated),SUM(file_count) FROM entries WHERE is_dir=0 AND excluded=0 GROUP BY extension;");
            SetMeta("scanned_ticks", DateTimeOffset.UtcNow.UtcTicks.ToString());
            if (!partial) SetMeta("completed_ticks", GetMeta("scanned_ticks")!);
            transaction.Commit();
            _transaction = null;
        }
    }
    public void StoreFingerprint(IndexRow row, string hash)
    {
        lock (_sync) Execute("UPDATE entries SET fingerprint=$hash WHERE id=$id AND logical=$size AND modified=$time AND own_incomplete=0",
            ("$hash", hash), ("$id", row.Id), ("$size", row.Logical), ("$time", row.Modified));
    }
    public long EligibleHashCount(bool cached)
    {
        lock (_sync)
        {
            using var command = Command($"SELECT COUNT(*) FROM entries WHERE is_dir=0 AND excluded=0 AND own_incomplete=0 AND logical>0 AND fingerprint IS {(cached ? "NOT " : "")}NULL AND logical IN (SELECT logical FROM entries WHERE is_dir=0 AND excluded=0 AND logical>0 GROUP BY logical HAVING COUNT(*)>1)");
            return (long)command.ExecuteScalar()!;
        }
    }
    public void SaveMetadata<T>(string key, T data) => SetMeta(key, JsonSerializer.Serialize(data));
    public static string Key(string path) => path.ToUpperInvariant();
    internal SqliteCommand Command(string sql, params (string Key, object Value)[] parameters)
    {
        var command = _writer.CreateCommand(); command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command;
    }
    private void Execute(string sql, params (string Key, object Value)[] parameters)
    { using var command = Command(sql, parameters); command.ExecuteNonQuery(); }
    internal static IndexRow Read(SqliteDataReader r) => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetInt32(5) != 0, r.GetInt32(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11),
        r.GetInt32(12), r.GetString(13), (uint)r.GetInt64(14), r.GetString(15), r.GetInt32(16) != 0, r.GetInt32(17) != 0,
        r.GetInt32(18) != 0, r.GetInt64(19), r.GetString(20), r.IsDBNull(21) ? null : r.GetString(21), r.GetInt32(22) != 0,
        r.IsDBNull(23) ? null : r.GetInt64(23), r.IsDBNull(24) ? null : r.GetInt64(24), r.IsDBNull(25) ? null : r.GetInt64(25));
    private static long SafeDirectoryTime(string path, long fallback)
    { try { return Directory.GetLastWriteTimeUtc(path).Ticks; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return fallback; } }
    public void Dispose() { lock (_sync) { _reuseCommand?.Dispose(); _writer.Dispose(); } }
}
