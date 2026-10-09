using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

internal sealed class DevelopmentManifest : IDisposable
{
    private readonly SqliteConnection _db;
    internal string Path { get; }
    internal DevelopmentManifest(string path)
    {
        Path = path; Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS entries(item TEXT,path TEXT COLLATE NOCASE,dir INTEGER,length INTEGER,ticks INTEGER,identity TEXT,allocation INTEGER,links INTEGER,hash TEXT,PRIMARY KEY(item,path)); CREATE INDEX IF NOT EXISTS sizes ON entries(length) WHERE dir=0;";
        command.ExecuteNonQuery();
    }

    internal void Scan(DevelopmentItem item, IReadOnlyList<string> exclusions, int maximum, CancellationToken token)
    {
        item.Complete = false;
        if (!Directory.Exists(item.FullPath) && !File.Exists(item.FullPath)) { item.Evidence += "；路径已失效（仅报告，不自动 prune）"; return; }
        using var transaction = _db.BeginTransaction();
        using var command = _db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO entries VALUES($item,$path,$dir,$length,$ticks,$identity,$allocation,$links,NULL)";
        foreach (var name in new[] { "$item", "$path", "$dir", "$length", "$ticks", "$identity", "$allocation", "$links" }) command.Parameters.AddWithValue(name, "");
        var stack = new Stack<string>(); stack.Push(item.FullPath); int count = 0;
        try
        {
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var path = stack.Pop(); if (++count > maximum) throw new IOException("条目达到扫描上限");
                if (exclusions.Any(p => DevelopmentCodexCatalog.Overlap(path, p)) || RecoveryFileProtection.IsManagedPath(path) || DiskAnalysisService.HasReparsePointAncestor(path)) throw new IOException("包含保护目录或链接");
                var attributes = File.GetAttributes(path); bool dir = attributes.HasFlag(FileAttributes.Directory);
                if ((attributes & (FileAttributes.Offline | (FileAttributes)0x400000)) != 0) throw new IOException("包含离线或云占位文件，不主动下载");
                if (item.Kind is DevelopmentKind.Build or DevelopmentKind.Cache && System.IO.Path.GetFileName(path) is ".git" or ".hg" or ".svn") throw new IOException("目录包含独立版本库，可能是唯一源码或依赖修改");
                var info = dir ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                long length = 0, allocation = 0; uint links = 1; string identity;
                if (dir)
                { var reference = WindowsFileStorageService.GetDirectoryReference(path); if (reference == 0) throw new IOException("无法确认目录身份"); identity = reference.ToString("X16"); }
                else { var file = (FileInfo)info; length = file.Length; var storage = WindowsFileStorageService.GetFileStorageInfo(path); allocation = storage.AllocatedBytes; links = storage.LinkCount; identity = storage.Identity; item.LogicalBytes += length; item.FileCount++;
                    if (links > 1 && DuplicateMutationService.HasReferenceAlias(path, exclusions)) throw new IOException("包含参考/排除目录文件的硬链接别名"); }
                item.LastModifiedUtc = info.LastWriteTimeUtc > item.LastModifiedUtc ? info.LastWriteTimeUtc : item.LastModifiedUtc;
                command.Parameters["$item"].Value = item.Id; command.Parameters["$path"].Value = path; command.Parameters["$dir"].Value = dir ? 1 : 0;
                command.Parameters["$length"].Value = length; command.Parameters["$ticks"].Value = info.LastWriteTimeUtc.Ticks;
                command.Parameters["$identity"].Value = identity; command.Parameters["$allocation"].Value = allocation; command.Parameters["$links"].Value = (long)links; command.ExecuteNonQuery();
                if (dir) foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }))
                {
                    // The main .git object database is shared, not checkout disk usage; it is never removable here.
                    if (item.Kind == DevelopmentKind.Worktree && System.IO.Path.GetFileName(child) == ".git" && Directory.Exists(child)) continue;
                    stack.Push(child);
                }
            }
            item.Complete = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { item.Evidence += "；扫描不完整：" + ex.Message; }
        transaction.Commit();
        item.EntryCount = count;
        using var sum = _db.CreateCommand(); sum.CommandText = "SELECT coalesce(sum(allocation),0) FROM (SELECT max(allocation) allocation FROM entries WHERE item=$item AND dir=0 GROUP BY identity)"; sum.Parameters.AddWithValue("$item", item.Id);
        item.AllocatedBytes = Convert.ToInt64(sum.ExecuteScalar());
        sum.CommandText = "SELECT coalesce(sum(allocation),0) FROM (SELECT max(allocation) allocation FROM entries WHERE item=$item AND dir=0 GROUP BY identity HAVING count(*)>=max(links))";
        item.TheoreticalBytes = Convert.ToInt64(sum.ExecuteScalar());
    }

    internal void Clear()
    { using var command = _db.CreateCommand(); command.CommandText = "DELETE FROM entries"; command.ExecuteNonQuery(); }

    internal void Validate(DevelopmentItem item, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        using var expected = _db.CreateCommand(); expected.CommandText = "SELECT count(*) FROM entries WHERE item=$item"; expected.Parameters.AddWithValue("$item", item.Id);
        long expectedCount = Convert.ToInt64(expected.ExecuteScalar()), count = 0;
        using var lookup = _db.CreateCommand(); lookup.CommandText = "SELECT dir,length,ticks,identity,links FROM entries WHERE item=$item AND path=$path";
        lookup.Parameters.AddWithValue("$item", item.Id); lookup.Parameters.AddWithValue("$path", ""); lookup.Prepare();
        var stack = new Stack<string>(); stack.Push(item.FullPath);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var path = stack.Pop(); count++;
            if (DiskAnalysisService.HasReparsePointAncestor(path)) throw new IOException("扫描后出现链接。");
            var attributes = File.GetAttributes(path); bool dir = attributes.HasFlag(FileAttributes.Directory);
            var info = dir ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            lookup.Parameters["$path"].Value = path;
            using (var row = lookup.ExecuteReader())
            {
                if (!row.Read() || (row.GetInt32(0) != 0) != dir || row.GetInt64(2) != info.LastWriteTimeUtc.Ticks) throw new IOException("扫描后有新增、修改或替换；请重新扫描。");
                if (dir) { if (row.GetString(3) != WindowsFileStorageService.GetDirectoryReference(path).ToString("X16")) throw new IOException("目录身份已变化。"); }
                else { var storage = WindowsFileStorageService.GetFileStorageInfo(path); if (row.GetInt64(1) != ((FileInfo)info).Length || row.GetString(3) != storage.Identity || row.GetInt64(4) != storage.LinkCount) throw new IOException("文件身份、大小或硬链接已变化。");
                    if (storage.LinkCount > 1 && DuplicateMutationService.HasReferenceAlias(path, exclusions)) throw new IOException("文件的另一硬链接位于参考或排除目录。"); }
            }
            if (dir) foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 })) stack.Push(child);
        }
        if (count != expectedCount) throw new IOException("扫描后有内容消失；请重新扫描。");
    }

    internal void CompareBuilds(IReadOnlyList<DevelopmentItem> items, IReadOnlyDictionary<string, DevelopmentGitState?> projects, CancellationToken token)
    {
        // SQLite holds the complete manifest; only a bounded hash group is loaded, never the full file list.
        var builds = items.Where(i => i.Kind == DevelopmentKind.Build && i.Complete && projects.GetValueOrDefault(i.Project) is not null).ToDictionary(i => i.Id);
        using var sizes = _db.CreateCommand(); sizes.CommandText = "SELECT length FROM entries WHERE dir=0 AND length>0 GROUP BY length HAVING count(*)>1 ORDER BY length DESC LIMIT 5000";
        var lengths = new List<long>(); using (var reader = sizes.ExecuteReader()) while (reader.Read()) lengths.Add(reader.GetInt64(0));
        int hashed = 0; long hashedBytes = 0;
        foreach (var length in lengths)
        {
            token.ThrowIfCancellationRequested(); using var command = _db.CreateCommand(); command.CommandText = "SELECT item,path,identity FROM entries WHERE dir=0 AND length=$size LIMIT 5001"; command.Parameters.AddWithValue("$size", length);
            var files = new List<(DevelopmentItem Item, string Path, string Identity)>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) if (builds.TryGetValue(reader.GetString(0), out var item)) files.Add((item, reader.GetString(1), reader.GetString(2)));
            foreach (var repository in files.GroupBy(f => projects[f.Item.Project]!.CommonDirectory, StringComparer.OrdinalIgnoreCase))
            {
                if (repository.Select(f => f.Item.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
                var matches = new Dictionary<string, List<(DevelopmentItem Item, string Identity)>>();
                foreach (var file in repository)
                {
                    if (++hashed > 10000 || (hashedBytes += length) > 2L * 1024 * 1024 * 1024) { foreach (var b in builds.Values) b.DuplicateHint += "（比对达到上限）"; return; }
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[64 * 1024]; int read;
                        while ((read = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hasher.AppendData(buffer, 0, read); }
                        var hash = Convert.ToHexString(hasher.GetHashAndReset());
                        if (!matches.TryGetValue(hash, out var list)) matches[hash] = list = []; list.Add((file.Item, file.Identity));
                    }
                    catch (IOException) { file.Item.DuplicateHint += "；有文件无法比对"; }
                }
                foreach (var group in matches.Values.Where(g => g.Select(x => x.Item.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 && g.Select(x => x.Identity).Distinct().Count() > 1))
                    foreach (var item in group.Select(g => g.Item).Distinct()) item.DuplicateHint += $"跨工作树相同内容 {CleanupService.FormatBytes(length)}；";
            }
        }
    }
    public void Dispose() => _db.Dispose();
}
