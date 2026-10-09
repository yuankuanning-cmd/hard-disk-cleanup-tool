using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

internal static class ScanEngineTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Probe(StorageMedium kind) : IStorageDeviceProbe { public StorageMedium Detect(string root) => kind; }
    private sealed class NoJournal : IUsnJournalProvider
    {
        public bool TryQuery(string v, out UsnCheckpoint c, out string reason) { c = new(0, 0, 0); reason = "test unavailable"; return false; }
        public IEnumerable<UsnChange> Read(string v, UsnCheckpoint a, UsnCheckpoint b, CancellationToken t) => throw new NotSupportedException();
        public string? ResolveDirectory(string v, ulong r) => null;
    }
    private sealed class Journal(string root) : IUsnJournalProvider
    {
        public UsnCheckpoint Head = new(42, 100, 0);
        public List<UsnChange> Changes = [];
        public bool FailRead;
        public bool TryQuery(string v, out UsnCheckpoint c, out string reason) { c = Head; reason = ""; return true; }
        public IEnumerable<UsnChange> Read(string v, UsnCheckpoint a, UsnCheckpoint b, CancellationToken t)
        {
            if (FailRead || a.JournalId != b.JournalId || a.NextUsn < b.FirstUsn) throw new IOException("wrapped journal");
            return Changes.Where(c => c.Usn >= a.NextUsn && c.Usn < b.NextUsn);
        }
        public string? ResolveDirectory(string v, ulong r) => root;
    }
    private sealed class Observer(Action<ScanProgressInfo> action) : IProgress<ScanProgressInfo>
    { public void Report(ScanProgressInfo value) => action(value); }

    public static async Task RunAsync(string testRoot, bool million)
    {
        Check(StorageDevicePolicy.Choose([testRoot], new(), new Probe(StorageMedium.Hdd)).Workers == 1, "机械硬盘必须顺序");
        Check(StorageDevicePolicy.Choose([testRoot], new(), new Probe(StorageMedium.Unknown)).Workers == 1, "未知介质必须保守");
        Check(StorageDevicePolicy.Choose([testRoot], new() { MaximumParallelism = 999 }, new Probe(StorageMedium.Ssd)).Workers <= 8, "并行上限无效");
        var fixture = Path.Combine(testRoot, "engine-files"); Directory.CreateDirectory(fixture);
        for (var i = 0; i < 600; i++)
        {
            var parent = Path.Combine(fixture, "dir-" + i % 30); Directory.CreateDirectory(parent);
            File.WriteAllBytes(Path.Combine(parent, i + ".bin"), new byte[512 + i]);
        }
        File.WriteAllBytes(Path.Combine(fixture, "copy-a.dat"), new byte[100]);
        File.WriteAllBytes(Path.Combine(fixture, "copy-b.dat"), new byte[100]);
        var service = new DiskAnalysisService(new DiskSnapshotService(Path.Combine(testRoot, "engine-index")), new Probe(StorageMedium.Ssd), new NoJournal());
        var options = new ScanEngineOptions { ComputeFingerprints = true };
        var first = await service.AnalyzeAsync(fixture, 0, engineOptions: options);
        Check(first.ScannedFileCount == 602 && first.EngineStatistics is { MetadataReads: 602, HashedFiles: >= 2 }, "首次索引/哈希流水线不完整");
        var watch = Stopwatch.StartNew(); var second = await service.AnalyzeAsync(fixture, 0, engineOptions: options); watch.Stop();
        Check(second.EngineStatistics is { ReusedFiles: 602, MetadataReads: 0, HashedFiles: 0, ReusedHashes: >= 2 }, "第二次扫描未复用元数据/指纹");
        File.Delete(Path.Combine(fixture, "dir-0", "0.bin"));
        File.WriteAllBytes(Path.Combine(fixture, "dir-1", "1.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(fixture, "fresh.dat"), new byte[120]);
        var changed = await service.AnalyzeAsync(fixture, 0, engineOptions: options);
        Check(changed.ScannedFileCount == 602 && changed.EngineStatistics is { MetadataReads: 2, RemovedEntries: 1 }, "新增/修改/删除核对错误");
        var snapshot = Path.Combine(testRoot, "saved-snapshot.sqlite"); await service.SaveSnapshotAsync(changed, snapshot);
        var opened = await service.OpenSnapshotAsync(snapshot);
        Check(opened.ScannedLogicalBytes == changed.ScannedLogicalBytes && opened.IsHistoricalSnapshot && !opened.Roots.Single().Directories[0].CanRecycle, "快照内容/只读保护错误");
        var last = await service.OpenLastAsync(fixture, [], [], false); Check(last.ScannedFileCount == 602, "无法直接加载上次结果");
        Console.WriteLine($"PASS: SQLite 索引 602 文件；第二次核对 {watch.Elapsed.TotalMilliseconds:F0} ms，读取占用 0 次；新增/修改 2 条、删除 1 条；快照保存/加载/只读保护。");

        var control = new ScanSessionControl();
        var pauseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pause = null;
        var observer = new Observer(p =>
        {
            if (p.Stage.Contains("元数据复用") && pause is null) { pause = control.PauseAsync(); pauseSignal.TrySetResult(); }
        });
        var running = service.AnalyzeAsync(fixture, 0, engineOptions: options, control: control, progress: observer);
        await pauseSignal.Task.WaitAsync(TimeSpan.FromSeconds(15)); await pause!;
        Check(control.IsPaused && !running.IsCompleted, "暂停没有停止流水线");
        var partial = await service.ReadPausedAsync(); Check(partial.HasCheckpoint && partial.EngineStatistics!.IsPartial, "暂停未保留检查点/部分结果");
        control.Resume(); var continued = await running.WaitAsync(TimeSpan.FromSeconds(30)); Check(continued.ScannedFileCount == 602, "同进程继续丢失记录");
        using var cancel = new CancellationTokenSource();
        var stopped = await service.AnalyzeAsync(fixture, 0, cancellationToken: cancel.Token, progress: new Observer(p => { if (p.Stage.Contains("元数据复用")) cancel.Cancel(); }));
        Check(stopped.HasCheckpoint, "停止未保留恢复检查点");
        var reopenedService = new DiskAnalysisService(new DiskSnapshotService(Path.Combine(testRoot, "engine-index")), new Probe(StorageMedium.Hdd), new NoJournal());
        var resumed = await reopenedService.AnalyzeAsync(fixture, 0, engineOptions: options with { ResumeCheckpoint = true });
        Check(resumed.ScannedFileCount == 602 && !resumed.HasCheckpoint && resumed.EngineStatistics!.WorkerLimit == 1, "重新打开检查点恢复错误");
        using (var crashed = new SqliteScanIndex(resumed.IndexPath!))
        { crashed.SetMeta("status", "running"); crashed.SetMeta("phase", "hashing"); crashed.SetMeta("compute_hash", "1"); }
        var crashedResult = await reopenedService.OpenLastAsync(fixture, [], [], false);
        Check(crashedResult.HasCheckpoint, "枚举完成后的哈希崩溃检查点未识别");
        var finishedHash = await reopenedService.AnalyzeAsync(fixture, 0, engineOptions: new() { ResumeCheckpoint = true });
        Check(finishedHash.ScannedFileCount == 602 && !finishedHash.HasCheckpoint, "无待枚举目录的哈希阶段未能恢复");
        Console.WriteLine("PASS: 流水线暂停屏障、部分结果、同进程继续、停止与重开服务后检查点恢复。");

        var usnRoot = Path.Combine(testRoot, "journal-files"); Directory.CreateDirectory(usnRoot); var path = Path.Combine(usnRoot, "same-time.bin"); File.WriteAllBytes(path, new byte[256]);
        var fakeJournal = new Journal(usnRoot);
        var usn = new DiskAnalysisService(new DiskSnapshotService(Path.Combine(testRoot, "usn-index")), new Probe(StorageMedium.Ssd), fakeJournal);
        var u1 = await usn.AnalyzeAsync(usnRoot, 0);
        fakeJournal.Head = new(42, 200, 0);
        var u2 = await usn.AnalyzeAsync(usnRoot, 0);
        Check(u2.EngineStatistics is { UsedUsn: true, EnumeratedFiles: 0 } && u2.ScannedLogicalBytes == 256, "无变化 USN 扫描不应遍历所有文件");
        var oldTime = File.GetLastWriteTimeUtc(path); File.WriteAllBytes(path, Enumerable.Repeat((byte)7, 256).ToArray()); File.SetLastWriteTimeUtc(path, oldTime);
        var reference = WindowsFileStorageService.GetFileStorageInfo(path).FileReference;
        fakeJournal.Changes.Add(new(reference, 1, 210, 1, FileAttributes.Normal, "same-time.bin")); fakeJournal.Head = new(42, 300, 0);
        var u3 = await usn.AnalyzeAsync(usnRoot, 0); Check(u3.EngineStatistics is { UsedUsn: true, MetadataReads: 1 }, "USN 必须强制刷新同大小同时间的修改");
        File.Delete(path); fakeJournal.Changes.Add(new(reference, 1, 310, 0x200, FileAttributes.Normal, "same-time.bin")); fakeJournal.Head = new(42, 400, 0);
        var u4 = await usn.AnalyzeAsync(usnRoot, 0); Check(u4.ScannedFileCount == 0 && u4.EngineStatistics is { UsedUsn: true, RemovedEntries: 1 }, "USN 删除未更新索引");
        File.WriteAllBytes(path, new byte[300]); fakeJournal.FailRead = true; fakeJournal.Head = new(43, 500, 450);
        var fallback = await usn.AnalyzeAsync(usnRoot, 0); Check(fallback.ScannedLogicalBytes == 300 && !fallback.EngineStatistics!.UsedUsn, "USN 失效未安全退回全核对");
        var record = new byte[80]; BitConverter.GetBytes(72).CopyTo(record, 8); BitConverter.GetBytes((ushort)2).CopyTo(record, 12);
        BitConverter.GetBytes((ushort)8).CopyTo(record, 64); BitConverter.GetBytes((ushort)60).CopyTo(record, 66); Encoding.Unicode.GetBytes("test").CopyTo(record, 68);
        Check(UsnJournalService.ParseBuffer(record, 80).Single().Name == "test", "真实 V2 USN 字节解析错误");
        record[12] = 3; try { UsnJournalService.ParseBuffer(record, 80); throw new Exception("V3 不应静默遗漏"); } catch (IOException) { }
        var realAvailable = new UsnJournalService().TryQuery(Path.GetPathRoot(testRoot)!, out _, out var reason);
        Console.WriteLine($"PASS: USN 模拟无变化、同时间修改、删除、日志重建回退与 V2 解析。实际机器 USN 可用={realAvailable} {reason}");
        await TestPagedIndex(testRoot, million ? 1000000 : 100000);
    }
    private static async Task TestPagedIndex(string testRoot, int count)
    {
        var path = Path.Combine(testRoot, "large-index.sqlite");
        using (var index = new SqliteScanIndex(path))
        {
            index.Begin([testRoot], [], false, false); var root = index.SeedRoot(testRoot, false);
            using var insert = index.Command("""
                WITH RECURSIVE numbers(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM numbers WHERE x<$count)
                INSERT INTO entries(parent_id,path,path_key,root_path,name,is_dir,depth,logical,raw_allocated,allocated,file_count,modified,attributes,seen)
                SELECT $parent,$root||'\virtual\'||x||'.bin',UPPER($root||'\virtual\'||x||'.bin'),$root,printf('%08d.bin',x),0,1,x,x,x,1,$time,0,1 FROM numbers
                """, ("$count", count), ("$parent", root), ("$root", testRoot), ("$time", DateTime.UtcNow.Ticks));
            insert.ExecuteNonQuery(); using var done = index.Command("UPDATE work SET state=2"); done.ExecuteNonQuery(); index.Aggregate(false);
            using var sqlite = index.Command("SELECT sqlite_version()"); Check(Version.Parse((string)sqlite.ExecuteScalar()!) >= new Version(3, 50, 2), "内置 SQLite 版本存在已知漏洞");
        }
        var before = GC.GetTotalMemory(true); var source = new IndexedNodeSource(path, true); var watch = Stopwatch.StartNew();
        var result = source.ReadResult(0); var rootNode = result.Roots.Single();
        var openedMs = watch.Elapsed.TotalMilliseconds;
        Check(rootNode.Children.Count == count && rootNode.FileCount == count, "分页索引数目错误");
        var last = rootNode.Children[count - 1]; var first = rootNode.Children[0];
        Check(last.Name == "00000001.bin" && first.LogicalBytes == count, "分页顺序/最后一页读取错误");
        var exact = source.GetChildren(rootNode, terms: ["00000001.bin"]); Check(exact.Count == 1 && exact[0].LogicalBytes == 1, "SQL 搜索没有按索引筛选");
        var ascending = source.GetChildren(rootNode, sort: "logical ASC,name,id"); Check(ascending[0].LogicalBytes == 1, "SQL 排序错误");
        var tiles = TreemapLayout.Build(rootNode, new Size(1000, 500), true); Check(tiles.Count <= 351 && tiles.Any(t => t.AggregatedCount > 0), "分页树图没有合并小项");
        await TestVirtualizedUi(result, source, count);
        watch.Stop(); var memory = GC.GetTotalMemory(true) - before;
        Check(source.LoadedRows < 5000 && memory < 80 * 1024 * 1024, "大索引被 WPF 全部加载进内存");
        Console.WriteLine($"PASS: {count:N0} 条 SQLite 索引打开 {openedMs:F0} ms；分页/搜索/排序/树图/WPF 明细全流程 {watch.Elapsed.TotalSeconds:F2}s；实际加载节点 {source.LoadedRows:N0}，托管内存增长 {memory / 1024d / 1024:F1} MiB。");
    }
    private static Task TestVirtualizedUi(DiskAnalysisResult result, IndexedNodeSource source, int count)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var explorer = new SpaceExplorer(); explorer.SetResult(result, []);
                ((TabControl)explorer.FindName("Views")).SelectedIndex = 1;
                explorer.Measure(new Size(1200, 560)); explorer.Arrange(new Rect(0, 0, 1200, 560)); explorer.UpdateLayout();
                var entries = (DataGrid)explorer.FindName("Entries"); Check(entries.Items.Count == count, "WPF 明细计数错误");
                ((TextBox)explorer.FindName("EntrySearch")).Text = "00000001.bin"; explorer.UpdateLayout(); Check(entries.Items.Count == 1, "WPF SQL 筛选错误");
                Check(source.LoadedRows < 5000, "WPF 明细枚举了完整索引"); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
