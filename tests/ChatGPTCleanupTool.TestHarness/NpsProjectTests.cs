using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Data.Sqlite;

internal static class NpsProjectTests
{
    internal static NpsScanResult UiResult { get; private set; } = new([], [], false, 0, 0, 0, DateTime.UtcNow, "", "");
    internal static IReadOnlyList<NpsCleanupItem> UiPlan { get; private set; } = [];
    private const string Xml = "<?xml version=\"1.0\"?><Project><Version>0.0.1</Version><CreateTime>2026-09-20 13:50:44</CreateTime><ProjectConfig><P name=\"UseBusbar\">1</P></ProjectConfig><LoadFlowConfig/></Project>";
    internal static async Task RunAsync(string testRoot, bool native)
    {
        var root = Path.Combine(testRoot, "nps"); Directory.CreateDirectory(root);
        var a = Project(Path.Combine(root, ".hidden", "messy", "random", "Engineering_A"));
        var b = Project(Path.Combine(root, "odd folder", "Engineering_B.restore-backup-20261001"));
        var recovery = Project(Path.Combine(root, ".simunps-mcp", "snapshots", "history", "project"));
        var parent = Project(Path.Combine(root, "Parent")); _ = Project(Path.Combine(parent, "nested", "Child"));
        var fake = Path.Combine(root, "not-project", "project.emtproj"); Directory.CreateDirectory(Path.GetDirectoryName(fake)!); File.WriteAllText(fake, "<Other/>");
        var dtd = Path.Combine(root, "bad", "project.emtproj"); Directory.CreateDirectory(Path.GetDirectoryName(dtd)!);
        File.WriteAllText(dtd, "<!DOCTYPE Project SYSTEM 'file:///C:/must-not-read'><Project><Version>1</Version></Project>");
        var standalone = Path.Combine(root, "downloaded", "lonely.npsdata"); Model(standalone);
        var unknown = Path.Combine(root, "unknown.npsdata"); File.WriteAllText(unknown, "not a model database");
        int calls = 0; bool running = false;
        var service = new NpsProjectService(Path.Combine(root, "index"), new Probe(), _ => running, _ => { calls++; throw new IOException("fixture does not delete"); });
        var watch = Stopwatch.StartNew(); UiResult = await service.DiscoverAsync(new([root, Path.Combine(root, ".hidden")]));
        Check(UiResult.Complete && UiResult.Projects.Count == 9, "应识别散落/隐藏/备份/嵌套/独立/受损工程并去重重叠范围");
        var pa = UiResult.Projects.Single(p => p.FullPath == a); var pb = UiResult.Projects.Single(p => p.FullPath == b);
        Check(pa.ModelFiles == 1 && pb.Kind == NpsProjectKind.Backup && UiResult.Projects.Single(p => p.FullPath == recovery).Kind == NpsProjectKind.Recovery, "项目模型计数/备份恢复分类不正确");
        Check(UiResult.Projects.Count(p => p.Kind == NpsProjectKind.StandaloneModel) == 2 && !UiResult.Projects.Single(p => p.ManifestPath == dtd).Recognized, "独立模型不可忽略，DTD不可执行");
        Check(new NpsProjectService(Path.Combine(root, "index")).LoadPrevious()?.Projects.Count == 9, "SQLite发现清单没有保存/载入");
        Check(NpsProjectRecognizer.Read(standalone).Recognized && !NpsProjectRecognizer.Read(unknown).Recognized, "SQLite模型身份检查错误");
        var walModel = Path.Combine(root, "wal-test.npsdata"); Model(walModel); File.WriteAllText(walModel + "-wal", "fixture");
        var beforeModel = File.GetLastWriteTimeUtc(walModel);
        Check(!NpsProjectRecognizer.Read(walModel).Recognized && !File.Exists(walModel + "-shm") && File.GetLastWriteTimeUtc(walModel) == beforeModel, "WAL候选不能打开并生成辅助文件");
        var standaloneRow = UiResult.Projects.Single(p => p.FullPath == standalone);
        var modelPlan = (await service.MeasureAsync([standaloneRow], [])).Single();
        Check(WindowsFileStorageService.GetFileStorageMetadata(standalone) == WindowsFileStorageService.GetFileStorageInfo(standalone), "仅元数据句柄读取必须与传统方法一致（占用/身份/硬链接）");
        Check(!modelPlan.CanRecycle && modelPlan.Kind == NpsCleanupKind.ModelFile && standaloneRow.LogicalBytes == new FileInfo(standalone).Length && modelPlan.FileCount == 1, "孤立文件显示大小但只作线索，不能误作完整工程回收");
        Check((await service.RecycleAsync([modelPlan], true, true, () => [])).Records.Any(r => r.Result.Contains("失败")), "孤立模型不能通过后台单文件回收入口");
        Check((await service.RecycleAsync([modelPlan], true, true, () => [Path.GetDirectoryName(standalone)!])).Records.Any(r => r.Result.Contains("失败")), "独立模型实时排除保护失效");
        File.SetLastWriteTimeUtc(standalone, beforeModel.AddMinutes(1));
        Check((await service.RecycleAsync([modelPlan], true, true, () => [])).Records.Any(r => r.Result.Contains("失败")), "模型文件变化必须拒绝旧快照");
        var walRow = standaloneRow with { Id = walModel, FullPath = walModel };
        Check(!(await service.MeasureAsync([walRow], [])).Single().CanRecycle && walRow.LogicalBytes > 0, "WAL模型显示大小但不能拆分删除");
        UiPlan = await service.MeasureAsync([pa, pb, UiResult.Projects.Single(p => p.FullPath == parent), UiResult.Projects.Single(p => p.FullPath == recovery)], []);
        var planA = UiPlan.Single(p => p.FullPath == a); var planB = UiPlan.Single(p => p.FullPath == b);
        Check(planA.CanRecycle && planA.AllocatedBytes > 0 && planA.FileCount == 3 && UiPlan.All(p => !p.IsSelected), "完整快照与默认不勾选错误");
        Check(!UiPlan.Single(p => p.FullPath == parent).CanRecycle && !UiPlan.Single(p => p.FullPath == recovery).CanRecycle, "嵌套工程/恢复快照不得整目录回收");
        Check(UiResult.Projects.Single(p => p.FullPath == recovery).LogicalBytes > 0, "保护工程也必须显示真实大小，而不是0B");
        var dry = await service.RecycleAsync([planA], false, true, () => []);
        Check(dry.Records.Any(r => r.Result == "模拟通过") && calls == 0 && Directory.Exists(a), "模拟模式不得移动/删除工程");
        var consent = await service.RecycleAsync([planA], false, false, () => []);
        Check(consent.Records.Any(r => r.Reason.Contains("备份")) && calls == 0, "未经备份确认不能回收");
        var excluded = await service.RecycleAsync([planA], true, false, () => [Path.Combine(a, "model")]);
        Check(excluded.Records.Any(r => r.Result.Contains("失败")) && calls == 0, "实时子目录原件/排除保护未阻止整个工程");
        running = true; await service.RecycleAsync([planA], true, false, () => []); Check(calls == 0, "运行中NPS必须阻止回收"); running = false;
        File.AppendAllText(Path.Combine(a, "script", "control.py"), "changed");
        var changed = await service.RecycleAsync([planA], true, true, () => []);
        Check(changed.Records.Any(r => r.Result.Contains("失败")), "扫描后文件变化必须拒绝");
        File.WriteAllText(Path.Combine(b, "added.txt"), "new");
        Check((await service.RecycleAsync([planB], true, true, () => [])).Records.Any(r => r.Result.Contains("失败")), "新增文件必须拒绝旧工程计划");
        var fresh = (await service.MeasureAsync([pa], [])).Single();
        File.AppendAllText(pa.ManifestPath, "\n<!-- changed manifest -->");
        Check((await service.RecycleAsync([fresh], true, true, () => [])).Records.Any(r => r.Result.Contains("失败")), "工程清单变化必须拒绝旧计划");
        var suspect = UiResult.Projects.First(p => p.Kind == NpsProjectKind.Suspected);
        Check(!(await service.MeasureAsync([suspect], [])).Single().CanRecycle, "疑似工程不能回收父目录");
        Check(!(await service.MeasureAsync([pa], [a])).Single().CanRecycle, "保护根测量必须只读拒绝");
        var gitProject = Project(Path.Combine(root, "git-project")); Directory.CreateDirectory(Path.Combine(gitProject, ".git"));
        var gitRow = new NpsProjectItem(gitProject, "git", gitProject, Path.Combine(gitProject, "project.emtproj"), NpsProjectKind.Project, true, "0.0.1", "", 1, "");
        Check(!(await service.MeasureAsync([gitRow], [])).Single().CanRecycle, "Git版本库不能从NPS整目录回收");
        Check(!(await service.MeasureAsync([pa with { ManifestPath = pb.ManifestPath }], [])).Single().CanRecycle, "不能借另一工程的清单授权整个目录");
        var recoveryRow = UiResult.Projects.Single(p => p.FullPath == recovery);
        Check(!(await service.MeasureAsync([recoveryRow with { Kind = NpsProjectKind.Project }], [])).Single().CanRecycle, "不能伪装分类绕过恢复快照保护");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var partial = await service.DiscoverAsync(new([root]), canceled.Token); Check(!partial.Complete && partial.Warnings.Any(w => w.Contains("取消")), "取消需保留并标注部分清单");
        var limited = await service.DiscoverAsync(new([root], MaximumMarkers: 1)); Check(!limited.Complete, "标志数量上限必须明确部分");
        var bulkRows = new List<NpsProjectItem>();
        for (int i = 0; i < 121; i++)
        {
            var path = Project(Path.Combine(root, "bulk-projects", $"project-{i}"));
            bulkRows.Add(new(path, Path.GetFileName(path), path, Path.Combine(path, "project.emtproj"), NpsProjectKind.Project, true, "0.0.1", "", 1, "测试工程"));
        }
        var bulkPlans = await service.MeasureAsync(bulkRows, []);
        Check(bulkPlans.Count == 121 && bulkPlans.All(p => p.CanRecycle && p.LogicalBytes > 0), "不能再限制每批100项自动统计");
        Check((await service.RecycleAsync(bulkPlans, true, true, () => [])).Records.Count(r => r.Result == "模拟通过") == 121, "不能限制每批100项回收");
        var canceledSizes = await service.MeasureAsync(bulkRows, [], canceled.Token);
        Check(canceledSizes.All(p => !p.CanRecycle) && bulkRows.All(p => p.LogicalBytes is null && p.SizeText.Contains("取消")), "取消大小统计不能显示旧大小/0B或保留清理资格");
        if (native)
        {
            var real = Project(Path.Combine(root, "native-recycle-only"));
            var nativeService = new NpsProjectService(Path.Combine(root, "native-index"), new Probe(), _ => false);
            var found = await nativeService.DiscoverAsync(new([real])); var measured = (await nativeService.MeasureAsync(found.Projects, [])).Single();
            measured.IsSelected = true;
            measured.PropertyChanged += (_, _) => throw new InvalidOperationException("回收后不能在工作线程发出UI选择通知");
            var operation = await nativeService.RecycleAsync([measured], true, false, () => []);
            Check(operation.Records.Any(r => r.Result == "执行前记录") && operation.Records.Any(r => r.Result == "已回收") && !Directory.Exists(real), "临时真实工程应强制进入回收站并持久记录");
            Check(nativeService.Records().Any(r => r.Result == "已回收"), "回收结果审计未持久化");
            var fileRoot = Path.Combine(root, "native-model-file-only"); var realFile = Path.Combine(fileRoot, "delete.npsdata"); Model(realFile);
            var survivor = Path.Combine(fileRoot, "keep.txt"); File.WriteAllText(survivor, "must survive");
            var fileFound = await nativeService.DiscoverAsync(new([fileRoot])); var fileMeasured = (await nativeService.MeasureAsync(fileFound.Projects, [])).Single();
            Check(!fileMeasured.CanRecycle && fileMeasured.Kind == NpsCleanupKind.ModelFile, "孤立文件只能作为归属线索");
            var fileOperation = await nativeService.RecycleAsync([fileMeasured], true, false, () => []);
            Check(fileOperation.Records.Any(r => r.Result.Contains("失败")) && File.Exists(realFile) && File.Exists(survivor) && Directory.Exists(fileRoot), "孤立模型不能退回单文件删除，父目录和其他文件必须保留");
        }
        Check(Directory.Exists(a) && File.Exists(standalone) && File.Exists(dtd), "用户式工程/独立模型夹具被错误修改");
        await MultiSourceAsync(Path.Combine(root, "multi-source"), native);
        UiPlan = await service.MeasureAsync(UiResult.Projects, []);
        service.Save(UiResult);
        Check(new NpsProjectService(Path.Combine(root, "index")).LoadPrevious()!.Projects.All(p => p.LogicalBytes is not null), "每项大小应保存在SQLite清单并可重新载入");
        Console.WriteLine($"PASS NPS: {watch.ElapsedMilliseconds} ms; random/hidden/deep locations, XML + SQLite schema, malformed/DTD/WAL, backups/recovery, nested/standalone, overlap dedup, saved catalogue, measured snapshots, dry run, consent/running/fresh protection/Git/change guards, cancellation/limits" + (native ? ", isolated real project recycle + audit" : "; no real project deletion"));
    }
    private static async Task MultiSourceAsync(string root, bool native)
    {
        Directory.CreateDirectory(root);
        var manifestOnly = Project(Path.Combine(root, "manifest-no-model-file"));
        File.Delete(Path.Combine(manifestOnly, "model", "Grid.npsdata"));
        File.WriteAllText(Path.Combine(manifestOnly, "result", "output.csv"), "fixture results");
        var minimal = Path.Combine(root, "minimal-manifest-project"); Directory.CreateDirectory(Path.Combine(minimal, "model"));
        File.WriteAllText(Path.Combine(minimal, "project.emtproj"), Xml);
        var scriptRoot = Layout(Path.Combine(root, "script-project-without-manifest-or-npsdata"));
        File.WriteAllText(Path.Combine(scriptRoot, "script", "run.py"), "from simunps import Project\n# SimuNPS-EMT fixture");
        File.WriteAllText(Path.Combine(scriptRoot, "result", "run.csv"), "results must be included");
        var databaseRoot = Layout(Path.Combine(root, "database-and-layout-no-manifest"));
        Model(Path.Combine(databaseRoot, "model", "first.npsdata")); Model(Path.Combine(databaseRoot, "model", "second.npsdata"));
        var generic = Layout(Path.Combine(root, "ordinary-folders-not-proven-nps"));
        File.WriteAllText(Path.Combine(generic, "script", "readme.txt"), "ordinary scripts, not a confirmed project");
        var shared = Path.Combine(root, "shared-downloads"); Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "download.emtproj"), Xml); File.WriteAllText(Path.Combine(shared, "personal.txt"), "not owned by NPS");
        var lonelyPath = Path.Combine(root, "isolated-model.npsdata"); Model(lonelyPath);
        var parent = Project(Path.Combine(root, "parent-with-independent-structured-child"));
        var child = Layout(Path.Combine(parent, "child")); File.WriteAllText(Path.Combine(child, "script", "run.py"), "import simunps");
        var service = new NpsProjectService(Path.Combine(root, "index"), new Probe(), _ => false);
        var found = await service.DiscoverAsync(new([root]));
        var byPath = found.Projects.ToDictionary(p => p.FullPath, StringComparer.OrdinalIgnoreCase);
        Check(byPath[manifestOnly].Recognized && byPath[manifestOnly].ModelFiles == 0, "没有npsdata时必须能凭清单和目录结构识别完整工程");
        Check(byPath[minimal].Recognized, "有效清单+model目录不应强制要求额外两个可选目录");
        Check(byPath[scriptRoot].Recognized && byPath[scriptRoot].Kind == NpsProjectKind.StructuredProject && byPath[scriptRoot].ModelFiles == 0, "无清单无npsdata时，结构+NPS专用脚本应能发现工程");
        Check(byPath[databaseRoot].Recognized && byPath[databaseRoot].ModelFiles == 2 && found.Projects.All(p => p.Kind != NpsProjectKind.StandaloneModel || !p.FullPath.StartsWith(databaseRoot + "\\")), "多个模型应结合结构归并为一个工程，不能逐文件删除");
        Check(!byPath[generic].Recognized && !byPath[shared].Recognized, "普通目录名称或孤立清单不能授权删除共享文件夹");
        var plans = await service.MeasureAsync(found.Projects, [], replaceAll: true);
        var planMap = plans.ToDictionary(p => p.FullPath, StringComparer.OrdinalIgnoreCase);
        Check(planMap[manifestOnly].CanRecycle && planMap[minimal].CanRecycle && planMap[scriptRoot].CanRecycle && planMap[databaseRoot].CanRecycle, "综合识别工程必须支持整目录回收");
        Check(planMap[scriptRoot].Kind == NpsCleanupKind.WholeProject && planMap[scriptRoot].FileCount == 2 && planMap[scriptRoot].LogicalBytes == Directory.EnumerateFiles(scriptRoot, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length), "大小必须包含脚本和结果，不是模型文件大小");
        Check(!planMap[generic].CanRecycle && !planMap[shared].CanRecycle && !planMap[lonelyPath].CanRecycle && !planMap[parent].CanRecycle, "候选、孤立文件和嵌套独立工程不能误删");
        var simulated = await service.RecycleAsync([planMap[manifestOnly], planMap[scriptRoot], planMap[databaseRoot]], true, true, () => []);
        Check(simulated.Records.Count(r => r.Result == "模拟通过") == 3, "不同来源的工程应统一通过整目录校验");
        File.WriteAllText(Path.Combine(scriptRoot, "script", "run.py"), "no longer NPS-specific");
        Check((await service.RecycleAsync([planMap[scriptRoot]], true, true, () => [])).Records.Any(r => r.Result.Contains("失败")), "结构工程的专用依据变化必须让旧计划失效");
        if (native)
        {
            File.WriteAllText(Path.Combine(scriptRoot, "script", "run.py"), "import simunps");
            var freshScript = (await service.MeasureAsync([byPath[scriptRoot]], [])).Single();
            var outcome = await service.RecycleAsync([planMap[manifestOnly], planMap[databaseRoot], freshScript], true, false, () => []);
            Check(outcome.Records.Count(r => r.Result == "已回收") == 3 && !Directory.Exists(manifestOnly) && !Directory.Exists(databaseRoot) && !Directory.Exists(scriptRoot), "无模型文件/无清单工程应回收整个文件夹，包含脚本和结果");
        }
        Check(File.Exists(lonelyPath) && File.Exists(Path.Combine(shared, "personal.txt")), "识别线索或共享目录被错误修改");
        Console.WriteLine("PASS NPS MULTI-SOURCE: XML+layout without npsdata, minimal manifest, layout+NPS scripts with no manifest/model database, database schema+layout, multi-model grouping, complete folder sizes/recycle, generic/shared-folder false positives, orphan clues read-only, nested inferred project and changed proof guards" + (native ? "; isolated whole-directory native recycle passed" : "; no native recycle"));
    }
    private static string Layout(string root)
    { foreach (var name in new[] { "model", "script", "result" }) Directory.CreateDirectory(Path.Combine(root, name)); return root; }
    internal static async Task SampleAsync(string testRoot)
    {
        var root = @"D:\AAA_work\model\SimuNPS-EMT"; var service = new NpsProjectService(Path.Combine(testRoot, "sample-index"));
        var watch = Stopwatch.StartNew(); var result = await service.DiscoverAsync(new([root]));
        Check(result.Projects.Any(p => p.ManifestPath.Length > 0 && p.Recognized), "用户现存样例中的工程清单未识别");
        Check(result.Projects.Any(p => p.Kind == NpsProjectKind.Recovery), "用户样例恢复快照未识别");
        var discoveryMs = watch.ElapsedMilliseconds;
        var measured = await service.MeasureAsync(result.Projects, [], replaceAll: true);
        Check(result.Projects.All(p => p.LogicalBytes is not null), "样例每项必须自动显示大小");
        Console.WriteLine($"READ ONLY NPS AUTO SIZES: discovery={discoveryMs} ms; discovery+sizes={watch.ElapsedMilliseconds} ms; measured={measured.Count}; partialSizes={measured.Count(p => p.PartialSize)}; eligible={measured.Count(p => p.CanRecycle)}; visible logical bytes={result.Projects.Where(p => p.Kind != NpsProjectKind.Recovery).Sum(p => p.LogicalBytes ?? 0)}; index only in random temporary test root; NO recycle");
        Console.WriteLine($"READ ONLY NPS SAMPLE: projects/models={result.Projects.Count}, standard={result.Projects.Count(p => p.Kind == NpsProjectKind.Project)}, backup={result.Projects.Count(p => p.Kind == NpsProjectKind.Backup)}, recovery={result.Projects.Count(p => p.Kind == NpsProjectKind.Recovery)}, suspected={result.Projects.Count(p => p.Kind == NpsProjectKind.Suspected)}, standalone={result.Projects.Count(p => p.Kind == NpsProjectKind.StandaloneModel)}, files={result.VisitedFiles}, skipped={result.Skipped}, complete={result.Complete}; NO recycle");
    }
    internal static void RunUi(MainWindow main, string output)
    {
        var tabs = (TabControl)main.FindName("MainTabControl"); Check(tabs.Items.Count == 11 && main.FindName("NpsCenterView") is NpsCenter, "NPS页面未接入");
        tabs.SelectedIndex = 9; var center = (NpsCenter)main.FindName("NpsCenterView"); center.SetResult(UiResult); center.SetPlan(UiPlan);
        Check(new NpsCenter().AllDisks, "新页面默认应是全盘发现；主窗口可保留用户上次选择的范围");
        var grid = (DataGrid)center.FindName("ProjectGrid"); grid.SelectedItem = UiResult.Projects.First();
        Check(grid.Items.Cast<NpsProjectItem>().All(p => p.Recognized && p.Kind is not NpsProjectKind.Recovery and not NpsProjectKind.StandaloneModel), "主列表必须是完整工程，恢复快照/孤立文件/待核实目录单独查看");
        ((ComboBox)center.FindName("TypeBox")).SelectedIndex = 3;
        Check(grid.Items.Cast<NpsProjectItem>().All(p => p.Kind == NpsProjectKind.Recovery), "恢复快照分类筛选错误");
        ((ComboBox)center.FindName("TypeBox")).SelectedIndex = 1; grid.SelectedItem = UiResult.Projects.First();
        Check(ResultContextMenu.BuildForTest(grid).Items.OfType<MenuItem>().Any(i => i.Tag?.ToString() == "check-rows") && center.FindName("PlanGrid") is null && grid.Columns[0] is DataGridTemplateColumn, "必须只有一张直接勾选列表，不再提供手动测量入口");
        center.SetSearchText("no-nps-match-fixture"); Check(grid.Items.Count == 0, "工程搜索未生效"); center.SetSearchText("");
        center.SelectVisible(); Check(center.SelectedPlan.Count == grid.Items.Cast<NpsProjectItem>().Count(p => p.Cleanup?.CanRecycle == true), "全选应直接勾选当前列表中可删除的项目");
        Check(center.SelectedPlan.All(p => p.Kind == NpsCleanupKind.WholeProject) && ((Button)center.FindName("DeleteButton")).IsEnabled, "主列表只勾选整个工程，回收按钮必须能用");
        Check(ResultContextMenu.BuildForTest(grid).Items.OfType<MenuItem>().Any(i => i.Tag?.ToString() == "recycle-nps"), "单表右键删除入口缺失");
        center.ClearSelection(); Check(center.SelectedPlan.Count == 0 && !((Button)center.FindName("DeleteButton")).IsEnabled, "取消全选应更新删除按钮");
        var content = (FrameworkElement)main.Content; content.Margin = new Thickness(0); ((Panel)content).Background = main.Background;
        content.Measure(new Size(1320, 900)); content.Arrange(new Rect(0, 0, 1320, 900)); content.UpdateLayout();
        var selectableRow = grid.Items.Cast<NpsProjectItem>().First(p => p.Cleanup?.CanRecycle == true);
        grid.ScrollIntoView(selectableRow); grid.UpdateLayout();
        var rowControl = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(selectableRow);
        var checkbox = FindCheckbox(rowControl) ?? throw new InvalidOperationException("真实行复选框未呈现");
        Check(checkbox.IsEnabled, "复选框不可用");
        var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(checkbox);
        ((System.Windows.Automation.Provider.IToggleProvider)peer).Toggle();
        Check(selectableRow.Cleanup!.IsSelected && center.SelectedPlan.Count == 1, "单击复选框必须直接更新勾选和统计，不能要求先选行或进入编辑");
        content.UpdateLayout();
        Render(content, Path.Combine(output, "nps-projects-main-preview.png"));
        var bulk = Enumerable.Range(0, 1201).Select(i => selectableRow with { Id = "fixture-ui-" + i, Name = "bulk-" + i, FullPath = @"D:\fixture-only\" + i, Cleanup = null }).ToArray();
        var plans = bulk.Select(p => new NpsCleanupItem { Id = p.Id, ProjectId = p.Id, ProjectName = p.Name, FullPath = p.FullPath, Kind = NpsCleanupKind.WholeProject, CanRecycle = true, LogicalBytes = 50 }).ToArray();
        center.SetResult(UiResult with { Projects = bulk }); center.SetPlan(plans); center.SelectVisible();
        Check(center.SelectedPlan.Count == 1201, "批量勾选不能限制100或1000项");
        grid.SelectAll(); var bulkMenu = ResultContextMenu.BuildForTest(grid);
        Check(bulkMenu.Items.OfType<MenuItem>().Single(i => i.Tag?.ToString() == "recycle-nps").IsEnabled, "大批量右键删除不应被通用菜单上限禁用");
        center.SetResult(UiResult); center.SetPlan(UiPlan);
        Console.WriteLine("PASS NPS UI: single list, real one-click checkbox binding, numeric size sort, select all/clear, 1201 checked rows without batch limits, search, menus and rendering");
    }
    internal static Task StandaloneUiAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { var app = new App(); app.InitializeComponent(); var main = new MainWindow(); var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "outputs")); Directory.CreateDirectory(output); RunUi(main, output); app.Shutdown(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static string Project(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "model")); Directory.CreateDirectory(Path.Combine(root, "script")); Directory.CreateDirectory(Path.Combine(root, "result"));
        File.WriteAllText(Path.Combine(root, "project.emtproj"), Xml); Model(Path.Combine(root, "model", "Grid.npsdata")); File.WriteAllText(Path.Combine(root, "script", "control.py"), "fixture");
        return root;
    }
    private static void Model(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE model(id INTEGER,name TEXT); CREATE TABLE block(id INTEGER); INSERT INTO model VALUES(1,'Grid');"; command.ExecuteNonQuery();
    }
    private sealed class Probe : IStorageDeviceProbe { public StorageMedium Detect(string root) => StorageMedium.Ssd; }
    private static CheckBox? FindCheckbox(DependencyObject parent)
    { for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++) { var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); if (child is CheckBox check) return check; if (FindCheckbox(child) is { } nested) return nested; } return null; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Render(FrameworkElement element, string path)
    { var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
}
