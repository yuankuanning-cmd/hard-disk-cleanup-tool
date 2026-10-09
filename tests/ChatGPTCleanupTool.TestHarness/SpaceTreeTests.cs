using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

internal static class SpaceTreeTests
{
    public static async Task RunAsync(string testRoot, bool testRecycle = false)
    {
        var fixture = Path.Combine(testRoot, "space-tree");
        var deep = Path.Combine(fixture, "Codex生成内容", "图片", "历史版本");
        Directory.CreateDirectory(Path.Combine(deep, "空目录"));
        var original = Write(Path.Combine(deep, "diagram.png"), 16384);
        var document = Write(Path.Combine(fixture, "文档", "report.docx"), 8192);
        _ = Write(Path.Combine(fixture, "文档", "draft.txt"), 300);
        _ = Write(Path.Combine(fixture, "downloads", "installer.exe"), 32768);
        _ = Write(Path.Combine(fixture, "root.json"), 24);
        var link = Path.Combine(deep, "diagram-copy.png");
        Check(CreateHardLink(link, original, IntPtr.Zero), "硬链接测试夹具创建失败");
        var service = new DiskAnalysisService(new DiskSnapshotService(Path.Combine(testRoot, "tree-auto-snapshots")));
        var first = await service.AnalyzeAsync(fixture, 10000);
        var root = first.Roots.Single();
        Check(root.DescendantsAndSelf().Any(n => n.FullPath == Path.Combine(deep, "空目录")), "未保留深层空目录");
        Check(root.FileCount == 6 && root.LogicalBytes == 74052, "完整目录树文件数或逻辑汇总不正确");
        Check(root.DescendantsAndSelf().Count(n => !n.IsDirectory) == 6, "小于阈值的文件不应从目录树丢失");
        Check(root.DescendantsAndSelf().Where(n => !n.IsDirectory).Sum(n => n.AllocatedBytes) == root.AllocatedBytes,
            "父子实际占用不守恒");
        var links = root.DescendantsAndSelf().Where(n => n.FullPath == original || n.FullPath == link).ToArray();
        Check(links.Length == 2 && links.Count(n => n.AllocatedBytes > 0) == 1, "硬链接实际占用应只归属一次");
        Check(first.LargestFiles.Count == 3, "大文件阈值不应影响完整树索引");
        Check(first.Extensions.Sum(n => n.FileCount) == 6 && first.Extensions.Sum(n => n.AllocatedBytes) == root.AllocatedBytes,
            "扩展名统计与目录树不一致");
        var sharedScope = Path.Combine(testRoot, "stable-link-scope");
        var scopeA = Path.Combine(sharedScope, "a");
        var scopeB = Path.Combine(sharedScope, "b");
        var sharedOriginal = Write(Path.Combine(scopeA, "original.psd"), 4096);
        Directory.CreateDirectory(scopeB);
        Check(CreateHardLink(Path.Combine(scopeB, "alias.png"), sharedOriginal, IntPtr.Zero), "跨根目录硬链接创建失败");
        var forward = await service.AnalyzeAsync(scopeA, 1, scanScopeRoots: [scopeA, scopeB]);
        var reversed = await service.AnalyzeAsync(scopeB, 1, scanScopeRoots: [scopeB, scopeA]);
        Check(forward.Roots.Single(n => n.FullPath == scopeA).AllocatedBytes == reversed.Roots.Single(n => n.FullPath == scopeA).AllocatedBytes &&
              reversed.Roots.Single(n => n.FullPath == scopeB).AllocatedBytes == 0 && reversed.GrowthDirectories.Count == 0,
            "改变多根扫描顺序不应转移硬链接归属或产生假增长");
        Check(reversed.Extensions.Single(n => n.Extension == ".psd").AllocatedBytes == reversed.ScannedAllocatedBytes,
            "硬链接归属调整未同步扩展名统计");
        Check(DiskAnalysisService.NormalizePath(@"C:\") == @"C:\", "盘符根路径不能截成 C:");
        Check(DiskAnalysisService.NormalizePath("C:") == @"C:\", "旧白名单的裸盘符应恢复为磁盘根目录");
        var overlap = await service.AnalyzeAsync(fixture, 1, scanScopeRoots: [deep, fixture, fixture]);
        Check(overlap.Roots.Count == 1 && overlap.ScannedFileCount == first.ScannedFileCount, "重叠白名单必须去重");

        var exclusionService = new ExclusionService(Path.Combine(testRoot, "tree-exclusions.json"));
        exclusionService.SetExcluded(Path.GetDirectoryName(document)!, true);
        Check(exclusionService.Contains(document), "目录排除应覆盖子文件");
        Check(!exclusionService.Contains(Path.GetDirectoryName(document)! + "-other\\report.docx"), "排除前缀必须按路径边界判断");
        var excluded = await service.AnalyzeAsync(fixture, 1, exclusionPaths: exclusionService.GetPaths().ToArray());
        Check(excluded.ScannedFileCount == 4, "排除目录不应计入空间统计");
        Check(excluded.Roots.Single().Directories.Any(n => n.IsExcluded && n.FileCount == 0), "排除目录应保留可见标记");
        Check(excluded.GrowthDirectories.Count == 0, "不同排除范围不能复用增长基线");

        var manualSnapshots = new DiskSnapshotService(Path.Combine(testRoot, "tree-deterministic-snapshots"));
        var before = DateTimeOffset.UtcNow.AddDays(-2);
        manualSnapshots.CompareAndSave(first with { ScannedAt = before }, []);
        _ = Write(Path.Combine(fixture, "文档", "new.pdf"), 12288);
        _ = Write(Path.Combine(fixture, "新目录", "fresh.zip"), 4096);
        var second = await service.AnalyzeAsync(fixture, 1);
        var compared = manualSnapshots.CompareAndSave(second with { ScannedAt = before.AddDays(2) }, []);
        var documents = compared.Roots.Single().Directories.Single(n => n.Name == "文档");
        Check(documents.LogicalGrowthBytes == 12288 && documents.GrowthBytes > 0, "目录增长对比错误");
        Check(Math.Abs(documents.GrowthBytesPerDay - documents.GrowthBytes!.Value / 2d) < 0.01, "增长速率计算错误");
        Check(compared.GrowthDirectories.Any(n => n.Name == "新目录"), "上次完整扫描后新增的目录应出现在增长排行");
        Check(compared.GrowthDirectories.SequenceEqual(compared.GrowthDirectories.OrderByDescending(n => n.GrowthBytesPerDay)),
            "增长排行未按速率排序");
        var fast = await service.AnalyzeAsync(fixture, 1, fastMode: true);
        Check(fast.IsApproximate && fast.GrowthDirectories.Count == 0 && fast.ScannedAllocatedBytes == fast.ScannedLogicalBytes,
            "快速/完整模式不能混用增长基线");

        var documentNode = compared.Roots.Single().DescendantsAndSelf().Single(n => n.FullPath == document);
        await new SpaceRecycleService().RecycleAsync(documentNode, [], dryRun: true);
        Check(File.Exists(document), "模拟回收不能改动文件");
        ExpectRejected(() => SpaceRecycleService.Validate(documentNode, [Path.GetDirectoryName(document)!]), "排除项不得回收");
        ExpectRejected(() => SpaceRecycleService.Validate(compared.Roots.Single(), []), "扫描根目录不得回收");
        var changed = Write(Path.Combine(fixture, "文档", "report.docx"), 8193);
        ExpectRejected(() => SpaceRecycleService.Validate(documentNode, []), "扫描后变化必须阻止回收");
        Check(File.Exists(changed), "变化保护不能删除文件");
        var directoryNode = compared.Roots.Single().Directories.Single(n => n.Name == "新目录");
        _ = Write(Path.Combine(directoryNode.FullPath, "after-scan.txt"), 100);
        ExpectRejected(() => SpaceRecycleService.Validate(directoryNode, []), "新增子项必须阻止整个目录回收");
        using (var locked = new FileStream(document, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var partial = await service.AnalyzeAsync(fixture, 1);
            Check(partial.Roots.Single().IsIncomplete && partial.Warnings.Count > 0, "不可读实际占用必须标记部分结果");
            Check(partial.Roots.Single().GrowthBytes is null, "不完整统计不得进入增长比较");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await service.AnalyzeAsync(fixture, 1, cancellationToken: canceled.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { }
        }

        var many = new DiskTreeNode { Name = "many", FullPath = fixture, IsDirectory = true };
        for (var i = 0; i < 100000; i++)
            many.Add(new DiskTreeNode { Name = i + ".bin", LogicalBytes = i + 1, AllocatedBytes = i + 1, FileCount = 1 });
        many.Finish();
        var stopwatch = Stopwatch.StartNew();
        var tiles = TreemapLayout.Build(many, new Size(1000, 500), true);
        stopwatch.Stop();
        Check(tiles.Count < 1800 && tiles.Any(n => n.AggregatedCount > 0), "大目录树图应合并密集小项而非生成海量控件");
        Check(Math.Abs(tiles.Sum(n => n.Bounds.Width * n.Bounds.Height) - 500000) < 1,
            "树图面积应完整覆盖，不得丢失合并项大小");
        Check(tiles.All(n => !n.Bounds.IsEmpty && n.Bounds.X >= 0 && n.Bounds.Y >= 0 && n.Bounds.Right <= 1000.001 && n.Bounds.Bottom <= 500.001),
            "树图矩形越界");
        var branching = new DiskTreeNode { Name = "branching", FullPath = fixture, IsDirectory = true };
        for (var i = 0; i < 100; i++)
        {
            var branch = new DiskTreeNode { Name = i.ToString(), FullPath = Path.Combine(fixture, i.ToString()), IsDirectory = true };
            for (var j = 0; j < 100; j++) branch.Add(new DiskTreeNode { Name = j + ".txt", AllocatedBytes = 100, LogicalBytes = 100, FileCount = 1 });
            branch.Finish(); branching.Add(branch);
        }
        branching.Finish();
        Check(TreemapLayout.Build(branching, new Size(10000, 5000), true, 450).Count <= 450, "递归树图必须遵守绘制预算");
        Console.WriteLine($"PASS: 100000 个内存索引文件的树图布局 {stopwatch.Elapsed.TotalMilliseconds:N1} ms，绘制 {tiles.Count} 块。");
        await RunUiAsync(compared);
        if (testRecycle)
        {
            var recycleFixture = Path.Combine(testRoot, "recycle-fixture");
            var disposable = Write(Path.Combine(recycleFixture, "disposable-test.bin"), 128);
            var disposableDirectory = Path.Combine(recycleFixture, "disposable-tree");
            _ = Write(Path.Combine(disposableDirectory, "nested", "only-test.bin"), 64);
            var scanned = await service.AnalyzeAsync(recycleFixture, 1);
            var candidate = scanned.Roots.Single().Children.Single(n => n.FullPath == disposable);
            var volume = Path.GetPathRoot(disposable)!;
            var beforeCount = RecycleCount(volume);
            await new SpaceRecycleService().RecycleAsync(candidate, [], dryRun: false);
            Check(!File.Exists(disposable) && RecycleCount(volume) > beforeCount, "实际 Shell 操作未验证进入回收站");
            var directoryCandidate = scanned.Roots.Single().Directories.Single(n => n.FullPath == disposableDirectory);
            var directoryBeforeCount = RecycleCount(volume);
            await new SpaceRecycleService().RecycleAsync(directoryCandidate, [], dryRun: false);
            Check(!Directory.Exists(disposableDirectory) && RecycleCount(volume) > directoryBeforeCount,
                "整个测试目录未验证进入回收站");
            Console.WriteLine("PASS: 独立临时测试文件和整个目录已通过 Windows Shell 进入回收站（数量增量已核对）。");
        }
        Console.WriteLine("PASS: 完整深层目录树、空目录、硬链接去重、多范围去重、目录排除、增长/新增目录、模式隔离、部分结果与回收变化保护、WPF 渲染/导航/筛选。");
    }

    private static Task RunUiAsync(DiskAnalysisResult result)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent();
                var control = new SpaceExplorer();
                control.SetResult(result, []);
                control.Measure(new Size(1200, 560));
                control.Arrange(new Rect(0, 0, 1200, 560)); control.UpdateLayout();
                var map = (DiskTreemap)control.FindName("Map");
                Check(map.Tiles.Count > 0, "WPF 树图未生成可见色块");
                var tile = map.Tiles.Last(n => !n.IsHeader && n.AggregatedCount == 0);
                Check(map.HitTestTile(new Point(tile.Bounds.X + tile.Bounds.Width / 2, tile.Bounds.Y + tile.Bounds.Height / 2)) is not null,
                    "色块点击命中检测失败");
                var directory = result.Roots.Single().Directories.First();
                control.Navigate(directory);
                Check(map.Root == directory && control.SelectedNode == directory, "目录下钻未同步树图和选择");
                control.Navigate(result.Roots.Single());
                var entries = (DataGrid)control.FindName("Entries");
                var search = (TextBox)control.FindName("EntrySearch");
                search.Text = "no-such-entry-xyz";
                Check(CollectionViewSource.GetDefaultView(entries.ItemsSource).IsEmpty, "明细筛选未生效");
                search.Clear();
                Check(!CollectionViewSource.GetDefaultView(entries.ItemsSource).IsEmpty, "清除筛选后应恢复明细");
                var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "outputs"));
                Directory.CreateDirectory(output);
                DuplicateCenterTests.RunUi(output);
                MediaAnalysisTests.RunUi(output);
                DevelopmentTests.RunUi(output);
                SoftwareTests.RunUi(output);
                Render(control, Path.Combine(output, "space-explorer-preview.png"));
                ((TabControl)control.FindName("Views")).SelectedIndex = 2;
                control.UpdateLayout();
                Render(control, Path.Combine(output, "space-growth-preview.png"));
                var main = new MainWindow();
                ((TabControl)main.FindName("MainTabControl")).SelectedIndex = 2;
                ((SpaceExplorer)main.FindName("AnalysisExplorer")).SetResult(result, []);
                var content = (FrameworkElement)main.Content;
                content.Margin = new Thickness(0); // Render the content as a standalone root, without the window's exterior margin offset.
                ((System.Windows.Controls.Panel)content).Background = main.Background;
                content.Measure(new Size(1280, 800)); content.Arrange(new Rect(0, 0, 1280, 800)); content.UpdateLayout();
                Render(content, Path.Combine(output, "scan-engine-preview.png"));
                Check(main.FindName("BusyPauseButton") is Button && main.FindName("SpaceStopButton") is Button, "扫描引擎操作控件未编译入主窗口");
                ((TabControl)main.FindName("MainTabControl")).SelectedIndex = 3;
                var center = (DuplicateCenter)main.FindName("DuplicateCenterView");
                center.KeepPolicy = ChatGPTCleanupTool.Models.DuplicateKeepPolicy.Newest;
                center.OnlyCrossDirectory = false; center.OnlyBackupOverlap = false; center.OnlyReferenceMatched = false;
                content.UpdateLayout(); Render(content, Path.Combine(output, "duplicate-center-main-preview.png"));
                Check(main.FindName("HardLinkDuplicatesButton") is Button && center.ResultGrid is DataGrid, "重复中心未接入主窗口");
                ((TabControl)main.FindName("MainTabControl")).SelectedIndex = 6;
                Check(main.FindName("MediaCenterView") is MediaCenter, "相似媒体页未编译入主窗口");
                content.UpdateLayout(); Render(content, Path.Combine(output, "similar-media-main-preview.png"));
                ((TabControl)main.FindName("MainTabControl")).SelectedIndex = 7;
                ((DevelopmentCenter)main.FindName("DevelopmentCenterView")).SetResult(DevelopmentTests.UiResult);
                content.UpdateLayout(); Render(content, Path.Combine(output, "development-center-main-preview.png"));
                ((TabControl)main.FindName("MainTabControl")).SelectedIndex = 8;
                ((SoftwareCenter)main.FindName("SoftwareCenterView")).SetResult(SoftwareTests.UiResult);
                content.UpdateLayout(); Render(content, Path.Combine(output, "software-center-main-preview.png"));
                ContextMenuTests.RunUi(main, output);
                app.Shutdown(); completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static string Write(string path, int bytes)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[bytes]); return path; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectRejected(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr attributes);
    private static long RecycleCount(string volume)
    {
        var info = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
        Marshal.ThrowExceptionForHR(SHQueryRecycleBin(volume, ref info));
        return info.Count;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleInfo { public uint Size; public long Bytes; public long Count; }
    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string volume, ref RecycleInfo info);
}
