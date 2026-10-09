using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

internal sealed class IndexedScanEngine(string directory, IStorageDeviceProbe? probe = null, IUsnJournalProvider? journal = null)
{
    private readonly IStorageDeviceProbe _probe = probe ?? new StorageDeviceProbe();
    private readonly IUsnJournalProvider _journal = journal ?? new UsnJournalService();
    private readonly object _activeLock = new();
    private SqliteScanIndex? _active;
    private ScanEngineStatistics? _statistics;
    private long _minimum;
    private int _maximum;
    private static readonly EnumerationOptions Enumeration = new() { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
    private sealed record Candidate(DirectoryScanJob Job, string Path, string Name, int Attributes, long Time, long Size, bool Excluded, IndexRow? Cached);
    public string IndexPath(IReadOnlyList<string> roots, IReadOnlyList<string> exclusions, bool fast)
    {
        var key = string.Join('\n', roots.Select(SqliteScanIndex.Key).Order()) + "\n--\n" + string.Join('\n', exclusions.Select(SqliteScanIndex.Key).Order()) + "\n" + fast;
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".sqlite");
    }
    public Task<DiskAnalysisResult> RunAsync(IReadOnlyList<string> roots, IReadOnlyList<string> exclusions, long minimum, int maximum,
        bool fast, ScanEngineOptions options, ScanSessionControl control, CancellationToken token, IProgress<ScanProgressInfo>? progress, List<string> warnings) =>
        Task.Run(() => RunCore(roots, exclusions, minimum, maximum, fast, options, control, token, progress, warnings), token);

    private async Task<DiskAnalysisResult> RunCore(IReadOnlyList<string> roots, IReadOnlyList<string> exclusions, long minimum, int maximum,
        bool fast, ScanEngineOptions options, ScanSessionControl control, CancellationToken token, IProgress<ScanProgressInfo>? progress, List<string> warnings)
    {
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
        var path = IndexPath(roots, exclusions, fast);
        using var ownership = new FileStream(path + ".scan.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var index = new SqliteScanIndex(path);
        var policy = StorageDevicePolicy.Choose(roots, options, _probe);
        var reporter = new ScanProgressReporter(progress); var reportLock = new object();
        void Warn(string message) { lock (warnings) { if (warnings.Count < 50) warnings.Add(message); } }
        var allExclusions = exclusions.Concat(new[] { DiskAnalysisService.NormalizePath(directory) }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Excluded(string p)
        {
            // O(path depth), not O(number of exclusions) for every one of a million entries.
            for (string? candidate = p; candidate is not null; candidate = Path.GetDirectoryName(candidate))
                if (allExclusions.Contains(candidate)) return true;
            return false;
        }
        var heads = new Dictionary<string, UsnCheckpoint>(StringComparer.OrdinalIgnoreCase);
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incremental = false; var forceRefresh = false; var oldStatus = index.GetMeta("status");
        reporter.SetStage("检查磁盘策略与增量检查点");
        using (await control.EnterAsync(token))
        {
            foreach (var volume in roots.Select(r => Path.GetPathRoot(r)!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (options.PreferUsn && _journal.TryQuery(volume, out var head, out _)) heads[volume] = head;
                else if (options.PreferUsn) Warn(volume + "：USN 不可用，使用目录核对；不会自动申请提权。");
            }
            if (!options.ResumeCheckpoint && options.PreferUsn && (oldStatus is "complete" or "completed_partial") && heads.Count > 0)
            {
                try
                {
                    var checkpoints = JsonSerializer.Deserialize<Dictionary<string, UsnCheckpoint>>(index.GetMeta("usn") ?? "{}")!;
                    if (roots.Select(r => Path.GetPathRoot(r)!).Any(v => !heads.ContainsKey(v) || !checkpoints.ContainsKey(v))) throw new IOException("缺少可靠的上次 USN 游标");
                    foreach (var root in roots)
                    {
                        var old = index.Find(root); var reference = WindowsFileStorageService.GetDirectoryReference(root);
                        if (old is null || reference == 0 || old.FileReference != reference.ToString("X16")) throw new IOException("扫描根目录已替换或无法核实身份");
                    }
                    var changes = 0; var resolvedParents = new Dictionary<(string Volume, ulong Reference), string?>();
                    foreach (var (volume, head) in heads)
                    foreach (var change in _journal.Read(volume, checkpoints[volume], head, token))
                    {
                        if (++changes > 200000) throw new IOException("变化日志过多，使用完整目录核对");
                        if (change.Name.Contains('\\') || change.Name.Contains('/') || change.Name is "." or "..") throw new IOException("变化日志包含无效名称");
                        var parentKey = (volume, change.ParentReference);
                        if (!resolvedParents.TryGetValue(parentKey, out var parent))
                        {
                            if (resolvedParents.Count >= 4096) resolvedParents.Clear();
                            resolvedParents[parentKey] = parent = _journal.ResolveDirectory(volume, change.ParentReference);
                        }
                        if (parent is null) throw new IOException("变化目录无法解析，使用完整目录核对");
                        var changedPath = DiskAnalysisService.NormalizePath(Path.Combine(parent, change.Name));
                        if (Excluded(changedPath) || !roots.Any(r => DiskAnalysisService.IsWithin(changedPath, r) || DiskAnalysisService.IsWithin(r, changedPath))) continue;
                        index.Invalidate(change.FileReference, changedPath);
                        foreach (var root in roots)
                        {
                            var target = DiskAnalysisService.IsWithin(parent, root) ? parent : DiskAnalysisService.IsWithin(root, changedPath) ? root : null;
                            if (target is null) continue;
                            if (change.Attributes.HasFlag(FileAttributes.Directory) && Directory.Exists(changedPath) && DiskAnalysisService.IsWithin(changedPath, root)) dirty.Add(changedPath);
                            dirty.Add(target);
                        }
                        if (dirty.Count > 10000) throw new IOException("变化目录过多，使用完整目录核对");
                    }
                    using var retry = index.Command("SELECT path FROM entries WHERE is_dir=1 AND own_incomplete=1 UNION SELECT p.path FROM entries e JOIN entries p ON e.parent_id=p.id WHERE e.is_dir=0 AND e.own_incomplete=1 LIMIT 10001");
                    using (var reader = retry.ExecuteReader()) while (reader.Read()) dirty.Add(reader.GetString(0));
                    if (dirty.Count > 10000) throw new IOException("未完成目录过多"); incremental = true;
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or JsonException or UnauthorizedAccessException)
                { Warn("USN 后备：" + ex.Message); dirty.Clear(); forceRefresh = true; }
            }
            index.Begin(roots, exclusions, fast, options.ResumeCheckpoint);
            if (!options.ResumeCheckpoint)
            {
                index.SaveMetadata("session_usn", heads);
                index.SetMeta("compute_hash", options.ComputeFingerprints ? "1" : "0"); index.SetMeta("phase", "enumerating");
            }
            else
            {
                // Resuming may happen days later: never advance past changes in already-completed directories.
                var originalHeads = JsonSerializer.Deserialize<Dictionary<string, UsnCheckpoint>>(index.GetMeta("session_usn") ?? "{}");
                heads = originalHeads ?? new();
                options = options with { ComputeFingerprints = index.GetMeta("compute_hash") == "1" };
            }
            if (!options.ResumeCheckpoint)
            {
                if (forceRefresh) { using var reset = index.Command("UPDATE entries SET force_refresh=1,fingerprint=NULL WHERE is_dir=0"); reset.ExecuteNonQuery(); }
                foreach (var root in roots) if (!incremental || index.Find(root) is null) index.SeedRoot(root, Excluded(root), !incremental);
                if (incremental) foreach (var changed in dirty)
                {
                    var target = changed; IndexRow? row;
                    while ((row = index.Find(target)) is null && roots.Any(r => DiskAnalysisService.IsWithin(target, r)))
                    { var parent = Path.GetDirectoryName(target); if (parent is null || parent == target) break; target = parent; }
                    if (row is { IsDirectory: true, Excluded: false }) index.Queue(row.Id);
                }
            }
        }
        var method = options.ResumeCheckpoint ? "检查点恢复" : incremental ? "USN 变化目录增量" : "目录核对 + 元数据复用";
        long enumerated = 0, metadata = 0, reused = 0, hashes = 0, reusedHashes = 0, inFlight = 0;
        var partial = false;
        ScanEngineStatistics Stats(string state) => new(method, policy.Description, Interlocked.Read(ref enumerated), Interlocked.Read(ref metadata),
            Interlocked.Read(ref reused), Interlocked.Read(ref hashes), reusedHashes, index.RemovedEntries, policy.Workers, partial, incremental, state);
        lock (_activeLock) { _active = index; _minimum = minimum; _maximum = maximum; _statistics = Stats("扫描中"); }
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token); var scanToken = abort.Token;
        var candidates = Channel.CreateBounded<Candidate>(512); var records = Channel.CreateBounded<IndexedFileRecord>(512);
        // All stages share this budget: HDD => one active filesystem operation, SSD => bounded concurrency.
        using var diskSlots = new SemaphoreSlim(policy.Workers);
        async Task<IDisposable> DiskLease()
        {
            var pause = await control.EnterAsync(scanToken);
            try { await diskSlots.WaitAsync(scanToken); return new ActionLease(() => { diskSlots.Release(); pause.Dispose(); }); }
            catch { pause.Dispose(); throw; }
        }
        async Task Guard(Func<Task> action) { try { await action(); } catch { abort.Cancel(); throw; } }
        async Task Enumerate()
        {
            using var lookupConnection = SqliteScanIndex.Open(path, true);
            using var lookup = lookupConnection.CreateCommand(); lookup.CommandText = $"SELECT {SqliteScanIndex.Columns} FROM entries WHERE path_key=$key";
            var key = lookup.Parameters.Add("$key", SqliteType.Text); lookup.Prepare();
            while (true)
            {
                scanToken.ThrowIfCancellationRequested(); IndexRow? directoryRow;
                using (await control.EnterAsync(scanToken)) directoryRow = index.Claim();
                if (directoryRow is null)
                { if (index.PendingCount == 0 && Interlocked.Read(ref inFlight) == 0) break; await Task.Delay(15, scanToken); continue; }
                var job = new DirectoryScanJob(directoryRow); lock (reportLock) reporter.Directory(directoryRow.Path);
                IEnumerator<FileSystemInfo>? iterator = null;
                try
                {
                    using (await DiskLease())
                    {
                        if (DiskAnalysisService.HasReparsePointAncestor(directoryRow.Path)) throw new IOException("目录已变成链接，跳过");
                        iterator = new DirectoryInfo(directoryRow.Path).EnumerateFileSystemInfos("*", Enumeration).GetEnumerator();
                    }
                    while (true)
                    {
                        Candidate? candidate = null; bool more;
                        using (await DiskLease())
                        {
                            more = iterator.MoveNext();
                            if (more)
                            {
                                var entry = iterator.Current;
                                try
                                {
                                    var attrs = entry.Attributes;
                                    if (attrs.HasFlag(FileAttributes.ReparsePoint)) { job.Failed = true; Warn(entry.FullName + "：链接/云占位文件已跳过"); }
                                    else
                                    {
                                        var excluded = Excluded(entry.FullName); var size = !excluded && entry is FileInfo file ? file.Length : 0;
                                        key.Value = SqliteScanIndex.Key(entry.FullName);
                                        IndexRow? old; using (var reader = lookup.ExecuteReader()) old = reader.Read() ? SqliteScanIndex.Read(reader) : null;
                                        candidate = new(job, entry.FullName, entry.Name, (int)attrs, entry.LastWriteTimeUtc.Ticks, size, excluded, old);
                                    }
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { job.Failed = true; Warn(entry.FullName + "：" + ex.Message); }
                            }
                        }
                        if (!more) break; if (candidate is null) continue;
                        Interlocked.Increment(ref job.Outstanding); Interlocked.Increment(ref inFlight);
                        await candidates.Writer.WriteAsync(candidate, scanToken);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { job.Failed = true; Warn(directoryRow.Path + "：" + ex.Message); }
                finally
                {
                    iterator?.Dispose(); job.EnumerationEnded = true;
                    if (!scanToken.IsCancellationRequested) using (await control.EnterAsync(scanToken)) index.FinishJob(job);
                }
            }
        }
        async Task Metadata()
        {
            await foreach (var c in candidates.Reader.ReadAllAsync(scanToken))
            {
                var isDir = ((FileAttributes)c.Attributes).HasFlag(FileAttributes.Directory);
                var allocated = c.Size; var identity = ""; uint links = 1; ulong reference = 0; var bad = false;
                var canReuse = c.Cached is { OwnIncomplete: false, ForceRefresh: false } old && old.IsDirectory == isDir && old.Excluded == c.Excluded &&
                    old.Modified == c.Time && old.Attributes == c.Attributes && (isDir || old.Logical == c.Size);
                using (await DiskLease())
                {
                    if (canReuse)
                    {
                        allocated = c.Cached!.RawAllocated; identity = c.Cached.Identity; links = c.Cached.Links;
                        _ = ulong.TryParse(c.Cached.FileReference, System.Globalization.NumberStyles.HexNumber, null, out reference);
                        if (!isDir && !c.Excluded) Interlocked.Increment(ref reused);
                    }
                    else if (!c.Excluded)
                    {
                        try
                        {
                            if (isDir) reference = WindowsFileStorageService.GetDirectoryReference(c.Path);
                            else if (!fast)
                            {
                                var storage = WindowsFileStorageService.GetFileStorageInfo(c.Path);
                                allocated = storage.AllocatedBytes; identity = storage.Identity; links = storage.LinkCount; reference = storage.FileReference;
                                Interlocked.Increment(ref metadata);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { allocated = 0; bad = true; Warn(c.Path + "：" + ex.Message); }
                    }
                }
                if (!isDir && !c.Excluded) { Interlocked.Increment(ref enumerated); lock (reportLock) reporter.File(c.Path, c.Size); }
                var record = new IndexedFileRecord(c.Path, c.Job.Row.Id, c.Job.Row.Root, c.Name, isDir, c.Job.Row.Depth + 1,
                    isDir ? (canReuse ? c.Cached!.Logical : 0) : c.Size, isDir ? (canReuse ? c.Cached!.Allocated : 0) : allocated,
                    c.Time, c.Attributes, identity, links, Path.GetExtension(c.Name).ToLowerInvariant(), bad, c.Excluded, reference.ToString("X16"),
                    canReuse, c.Cached is null || !c.Cached.IsDirectory || c.Cached.ForceRefresh, c.Job);
                await records.Writer.WriteAsync(record, scanToken);
            }
        }
        async Task Write()
        {
            var batch = new List<IndexedFileRecord>(128);
            while (await records.Reader.WaitToReadAsync(scanToken))
            {
                // Coalesce short bursts; avoid a separate disk transaction for every small file.
                await Task.Delay(4, scanToken);
                batch.Clear(); while (batch.Count < 128 && records.Reader.TryRead(out var item)) batch.Add(item);
                using (await control.EnterAsync(scanToken))
                {
                    index.WriteBatch(batch, incremental && !options.ResumeCheckpoint); Interlocked.Add(ref inFlight, -batch.Count);
                    lock (_activeLock) _statistics = Stats("枚举 → 元数据 → 索引");
                }
            }
        }
        try
        {
            reporter.SetStage(method + " · " + policy.Description);
            var writer = Guard(Write);
            var readers = Enumerable.Range(0, policy.Workers).Select(_ => Guard(Metadata)).ToArray();
            var enumerators = Enumerable.Range(0, policy.Workers).Select(_ => Guard(Enumerate)).ToArray();
            var closeCandidates = Task.Run(async () => { try { await Task.WhenAll(enumerators); } finally { candidates.Writer.TryComplete(); } });
            var closeRecords = Task.Run(async () => { try { await Task.WhenAll(readers); } finally { records.Writer.TryComplete(); } });
            await Task.WhenAll(closeCandidates, closeRecords, writer);
            if (options.ComputeFingerprints && !fast)
            {
                using (await control.EnterAsync(scanToken)) index.SetMeta("phase", "hashing");
                reporter.SetStage("哈希阶段：只读取同大小候选，复用已验证指纹"); reusedHashes = index.EligibleHashCount(true);
                long lastId = 0;
                while (true)
                {
                    IndexRow[] rows;
                    using (await control.EnterAsync(scanToken))
                    using (var connection = SqliteScanIndex.Open(path, true))
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT {SqliteScanIndex.Columns} FROM entries WHERE id>$id AND is_dir=0 AND excluded=0 AND own_incomplete=0 AND fingerprint IS NULL AND logical>0 AND logical IN (SELECT logical FROM entries WHERE is_dir=0 AND excluded=0 AND logical>0 GROUP BY logical HAVING COUNT(*)>1) ORDER BY id LIMIT 128";
                        cmd.Parameters.AddWithValue("$id", lastId); using var reader = cmd.ExecuteReader(); var list = new List<IndexRow>(); while (reader.Read()) list.Add(SqliteScanIndex.Read(reader)); rows = list.ToArray();
                    }
                    if (rows.Length == 0) break;
                    foreach (var row in rows)
                    {
                        lastId = row.Id; scanToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); FileStream stream;
                            using (await DiskLease())
                            {
                                if (DiskAnalysisService.HasReparsePointAncestor(Path.GetDirectoryName(row.Path)!) || File.GetAttributes(row.Path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("指纹候选已变成链接");
                                stream = new(row.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                            }
                            using (stream)
                            {
                                var buffer = new byte[65536]; int count;
                                do { using (await DiskLease()) count = stream.Read(buffer); if (count > 0) hash.AppendData(buffer, 0, count); } while (count > 0);
                                using (await DiskLease())
                                {
                                    var info = new FileInfo(row.Path);
                                    if (info.Length != row.Logical || info.LastWriteTimeUtc.Ticks != row.Modified) throw new IOException("计算指纹期间文件发生变化");
                                    index.StoreFingerprint(row, Convert.ToHexString(hash.GetHashAndReset())); Interlocked.Increment(ref hashes);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(row.Path + "：指纹未保存，" + ex.Message); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && options.RetainPartialResults) { partial = true; }
        finally { lock (_activeLock) _active = null; }
        if (partial && index.PendingCount == 0)
        {
            // A stop during hashing still needs a resumable checkpoint.
            using var retry = index.Command("UPDATE work SET state=0 WHERE entry_id IN (SELECT id FROM entries WHERE parent_id IS NULL AND excluded=0)"); retry.ExecuteNonQuery();
        }
        partial |= index.PendingCount > 0;
        reporter.SetStage(partial ? "保存部分结果和恢复检查点" : "聚合目录、扩展名与增长基准");
        if (incremental && dirty.Count == 0 && !partial)
        { index.SetMeta("status", oldStatus!); index.SetMeta("scanned_ticks", DateTimeOffset.UtcNow.UtcTicks.ToString()); }
        else index.Aggregate(partial);
        // The head captured BEFORE scanning is saved; changes made during scanning are replayed next time.
        if (!partial && heads.Count > 0) index.SaveMetadata("usn", heads);
        if (!partial) index.SetMeta("completed_ticks", index.GetMeta("scanned_ticks")!);
        using (var bad = index.Command("SELECT COUNT(*) FROM entries WHERE parent_id IS NULL AND incomplete=1")) partial |= (long)bad.ExecuteScalar()! > 0;
        index.SaveMetadata("statistics", Stats(partial ? "部分结果已保留" : "扫描完成")); index.SaveMetadata("warnings", warnings);
        if (!partial) index.SetMeta("phase", "complete");
        reporter.Complete(roots[0]); return new IndexedNodeSource(path).ReadResult(minimum, maximum);
    }
    public Task<DiskAnalysisResult> ReadPausedAsync() => Task.Run(() =>
    {
        lock (_activeLock)
        {
            if (_active is null) throw new InvalidOperationException("扫描已经结束。");
            _active.Aggregate(true);
            if (_statistics is { } stats) _active.SaveMetadata("statistics", stats with { IsPartial = true, Status = "已暂停，可继续或保存快照" });
            return new IndexedNodeSource(_active.Path, true).ReadResult(_minimum, _maximum);
        }
    });
    private sealed class ActionLease(Action action) : IDisposable
    { private Action? _action = action; public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke(); }
}
