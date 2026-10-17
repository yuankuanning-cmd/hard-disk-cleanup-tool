using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

internal static class SoftwareTests
{
    internal static SoftwareResult UiResult { get; private set; } = new([], [], false, DateTime.UtcNow);
    public static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "software"); Directory.CreateDirectory(root);
        var install = Path.Combine(root, "Installed Example"); Write(Path.Combine(install, "app.bin"), new string('a', 10000));
        var uninstall = Write(Path.Combine(install, "uninstaller.exe"), "fixture only - never executed");
        var desktop = new SoftwareRegistration("Fake/Desktop", "Fake", SoftwareKind.Desktop, "Example Editor", "Fixture Publisher", "1.0", install, 500000,
            "\"" + uninstall + "\" /uninstall \"argument with spaces\"", RegistryPath: @"HKEY_CURRENT_USER\Software\Fixture");
        var missing = desktop with { Id = "Fake/Missing", Name = "Missing Uninstaller", InstallPath = Path.Combine(root, "missing"), UninstallCommand = "\"C:\\missing-fixture-only\\uninstall.exe\"" };
        var store = new SoftwareRegistration("Store/Fixture_1.0_x64__abc123", "Store", SoftwareKind.Store, "Fixture.Store", "Fixture", "1.0", "", 0, PackageName: "Fixture_1.0_x64__abc123");
        var framework = store with { Id = "Store/Framework", Name = "Shared Framework", Protected = true };
        var residue = desktop with { Id = "Residue", Kind = SoftwareKind.DirectoryResidue, Name = "Old app data", UninstallCommand = "", InstallPath = Path.Combine(root, "old-data") };
        Write(Path.Combine(residue.InstallPath, "important-save.txt"), "saved preferences");
        var fake = new FakeInventory([desktop, missing, store, framework], [residue]); var restore = new FakeRestore(); var runner = new FakeRunner(fake);
        var service = new SoftwareAnalysisService(Path.Combine(root, "catalog"), fake, restore, runner);
        var watch = Stopwatch.StartNew(); var result = await service.AnalyzeAsync(new(Measure: true)); UiResult = result;
        SoftwareItem Row(string id) => result.Items.Single(i => i.Id == id);
        var measured = Row(desktop.Id); Check(measured.LogicalBytes == 10000 + new FileInfo(uninstall).Length && measured.AllocatedBytes > 0 && measured.FileCount == 2, "安装目录大小测量错误");
        Check(Row(missing.Id).Status.Contains("缺失") && !Row(missing.Id).CanUninstall && !Row(framework.Id).CanUninstall && !Row(residue.Id).CanUninstall, "孤立/共享/残留必须受保护");
        Check(result.Items.All(i => !i.IsSelected), "软件不得默认进入卸载计划");
        var plan = SoftwareWindowsCommands.Plan(desktop)!; Check(plan.Arguments.Count == 2 && plan.Arguments[1] == "argument with spaces", "卸载参数安全解析失败");
        Check(SoftwareWindowsCommands.Plan(desktop with { UninstallCommand = uninstall + " /uninstall" }) is null, "未加引号的空格路径不能猜测执行");
        foreach (var host in new[] { "cmd.exe", "powershell.exe", "rundll32.exe", "mshta.exe" })
            Check(SoftwareWindowsCommands.Plan(desktop with { UninstallCommand = "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), host) + "\" /c dangerous" }) is null, "不能执行脚本宿主卸载字符串");
        var msi = desktop with { ProductCode = Guid.NewGuid().ToString("B") }; var msiPlan = SoftwareWindowsCommands.Plan(msi)!;
        Check(msiPlan.Arguments[0] == "/x" && msiPlan.Arguments.Last() == "/norestart" && !msiPlan.Arguments.Contains("/qn"), "MSI 必须构造交互卸载而非任意注册表参数");
        Check(SoftwareWindowsCommands.Plan(store with { PackageName = "evil'; Remove-Item x" }) is null, "应用包身份注入未阻止");
        var parsedStore = WindowsSoftwareInventory.ParseStore("""[{"Name":"Fixture","PackageFullName":"Fixture_1.0_x64__abc","Publisher":"CN=Fixture","Version":"1.0","InstallLocation":"","NonRemovable":false,"IsFramework":true,"IsResourcePackage":false}]""");
        Check(parsedStore.Count == 1 && parsedStore[0].Protected, "商店框架保护解析失败");
        Check(!WindowsSoftwareInventory.AliasMatch("Microsoft", desktop with { Publisher = "Microsoft" }) && !WindowsSoftwareInventory.AliasMatch("Ex", desktop), "共享厂商/短名字不得作残留依据");
        TestSteam(root);
        var dry = await service.UninstallAsync([measured, Row(framework.Id), Row(residue.Id)], true, () => []);
        Check(dry.Records.Count(r => r.Result == "模拟通过") == 1 && runner.Count == 0 && restore.Count == 0, "模拟不得建立还原点/启动卸载，残留不得进入队列");
        var exclusion = await service.UninstallAsync([measured], true, () => [install]);
        Check(exclusion.Records.Any(r => r.Result == "已跳过"), "新增参考排除未阻止卸载");
        fake.Current[desktop.Id] = desktop with { Version = "2.0" };
        var changed = await service.UninstallAsync([measured], true, () => []); Check(changed.Records.Any(r => r.Result == "已跳过"), "软件升级后旧计划未失效");
        fake.Current[desktop.Id] = desktop; File.AppendAllText(uninstall, "changed");
        var executableChanged = await service.UninstallAsync([measured], true, () => []); Check(executableChanged.Records.Any(r => r.Result == "已跳过"), "卸载程序变更未拒绝");
        result = await service.AnalyzeAsync(new());
        restore.Result = new(false, 0, "", "fixture system protection disabled");
        var restoreFailed = await service.UninstallAsync([Row(desktop.Id)], false, () => []);
        Check(restoreFailed.Records.Any(r => r.Result.Contains("队列停止")) && runner.Count == 0, "还原点失败仍启动卸载");
        restore.Result = new(true, 0, "not confirmed", ""); await service.UninstallAsync([Row(desktop.Id)], false, () => []);
        Check(runner.Count == 0, "没有新还原点序号不能继续");
        Check(!(await service.CreateRestorePointAsync()).Success, "单独建立还原点也必须确认有效新序号");
        restore.Result = new(true, 123, "fixture restore only", "");
        var removed = await service.UninstallAsync([Row(desktop.Id), Row(store.Id)], false, () => []);
        Check(runner.Count == 2 && runner.MaximumConcurrent == 1 && removed.Records.Count(r => r.Result == "登记已移除") == 2, "卸载须串行并检查实际登记消失");
        Check(File.Exists(Path.Combine(residue.InstallPath, "important-save.txt")) && File.Exists(uninstall), "模拟测试不得删残留或运行夹具 exe");
        var afterOne = await service.AnalyzeAsync(new()); Check(fake.LastHistory.Any(h => h.Registration.Id == desktop.Id && h.AbsentScans == 1), "缺少消失登记历史");
        await service.AnalyzeAsync(new()); Check(fake.LastHistory.Any(h => h.Registration.Id == desktop.Id && h.AbsentScans == 2), "须记录连续两次完整来源缺失");
        fake.Complete = false; await service.AnalyzeAsync(new()); Check(fake.LastHistory.Single(h => h.Registration.Id == desktop.Id).AbsentScans == 2, "来源不完整不能累计卸载残留证据");
        Check(service.ReadRecords().Any(r => r.Result == "执行前记录") && service.ReadRecords().Any(r => r.Action == "建立还原点"), "没有持久卸载意图/还原点记录");
        // Bootstrapper returning without removing registration must stop the remainder of a batch.
        var secondFake = new FakeInventory([desktop, store], []); var pendingRunner = new FakeRunner(secondFake) { RemoveRegistration = false };
        var second = new SoftwareAnalysisService(Path.Combine(root, "pending"), secondFake, restore, pendingRunner);
        var secondResult = await second.AnalyzeAsync(new()); var pending = await second.UninstallAsync(secondResult.Items, false, () => []);
        Check(pendingRunner.Count == 1 && pending.Records.Any(r => r.Result == "未确认 / 失败"), "未确认卸载时不应继续并发启动下一软件");
        var partial = await new SoftwareAnalysisService(Path.Combine(root, "partial"), new FakeInventory([desktop], []), restore, pendingRunner).AnalyzeAsync(new(Measure: true, MaximumEntries: 1));
        Check(partial.Limited && partial.Items[0].SizeBasis.Contains("部分"), "测量上限必须提示部分结果");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await second.UninstallAsync(secondResult.Items, false, () => [], canceled.Token); throw new InvalidOperationException("取消未生效"); } catch (OperationCanceledException) { }
        Console.WriteLine($"PASS SOFTWARE: {watch.ElapsedMilliseconds} ms; isolated inventory, actual sizes, safe command plans, Store protection, Steam libraries, history, fresh rules/version/executable checks, restore failure stops, sequential fake uninstall, persistent audit, partial limits; NO real app uninstall or restore point creation");
    }
    private static void TestSteam(string root)
    {
        var steam = Path.Combine(root, "Steam"); var library = Path.Combine(root, "Other Library");
        Write(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" \"" + steam.Replace("\\", "\\\\") + "\" } \"1\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" } }");
        Write(Path.Combine(library, "steamapps", "appmanifest_1234.acf"), "\"AppState\" { \"appid\" \"1234\" \"name\" \"Fixture Game\" \"installdir\" \"Game Folder\" \"SizeOnDisk\" \"999999\" }");
        var games = WindowsSoftwareInventory.ReadSteam(steam, CancellationToken.None); Check(games.Count == 1 && games[0].ReportedBytes == 999999 && games[0].InstallPath == Path.Combine(library, "steamapps", "common", "Game Folder"), "多Steam库内容解析错误");
        Check(SoftwareWindowsCommands.Plan(games[0])?.Executable == "steam://uninstall/1234", "Steam卸载必须交给平台");
        Write(Path.Combine(library, "steamapps", "appmanifest_1234.acf"), "\"appid\" \"4321\" \"installdir\" \"Game Folder\"");
        try { WindowsSoftwareInventory.ReadSteam(steam, CancellationToken.None); throw new InvalidOperationException("Steam身份不一致未拒绝"); } catch (IOException) { }
        Write(Path.Combine(library, "steamapps", "appmanifest_1234.acf"), "\"appid\" \"1234\" \"installdir\" \"../../escape\"");
        try { WindowsSoftwareInventory.ReadSteam(steam, CancellationToken.None); throw new InvalidOperationException("越界平台路径未拒绝"); } catch (IOException) { }
    }
    public static void RunUi(string output)
    {
        var control = new SoftwareCenter(); Check(!control.MeasureDirectories, "默认不应深度测量大目录"); control.SetResult(UiResult);
        control.Measure(new Size(1500, 720)); control.Arrange(new Rect(0, 0, 1500, 720)); control.UpdateLayout();
        control.SetSearchText("no-match-software-test-xyz"); Check(CollectionViewSource.GetDefaultView(control.ResultGrid.ItemsSource).IsEmpty, "软件搜索未生效"); control.SetSearchText("");
        ((ComboBox)control.FindName("FilterBox")).SelectedIndex = 5; Check(control.ResultGrid.Items.Cast<SoftwareItem>().All(i => i.Registration.Kind is SoftwareKind.DirectoryResidue or SoftwareKind.RegistryResidue), "残留过滤错误");
        ((ComboBox)control.FindName("FilterBox")).SelectedIndex = 0; control.ResultGrid.SelectedItem = UiResult.Items.First(); control.UpdateLayout();
        Check(((TextBlock)control.FindName("EvidenceText")).Text.Contains("卸载登记"), "卸载证据未显示");
        ((ComboBox)control.FindName("FilterBox")).SelectedIndex = 4;
        Check(control.ResultGrid.Items.Cast<SoftwareItem>().Any(i => i.Name == "Missing Uninstaller") && !control.ResultGrid.Items.Cast<SoftwareItem>().Any(i => i.Registration.Protected), "缺少卸载入口筛选不能混入受保护系统组件");
        ((ComboBox)control.FindName("FilterBox")).SelectedIndex = 0;
        Render(control, Path.Combine(output, "software-center-preview.png")); Console.WriteLine("PASS SOFTWARE UI: WPF rendering, defaults, search/filter/evidence and virtualized table");
    }
    public static Task RunStandaloneUiAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { var app = new App(); app.InitializeComponent(); var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "outputs")); Directory.CreateDirectory(output); RunUi(output);
            var main = new MainWindow(); var tabs = (TabControl)main.FindName("MainTabControl"); Check(tabs.Items.Count == 11 && main.FindName("SoftwareCenterView") is SoftwareCenter, "软件页未接入主窗口"); tabs.SelectedIndex = 8;
            ((SoftwareCenter)main.FindName("SoftwareCenterView")).SetResult(UiResult); var content = (FrameworkElement)main.Content; content.Margin = new Thickness(0); ((Panel)content).Background = main.Background;
            content.Measure(new Size(1320, 860)); content.Arrange(new Rect(0, 0, 1320, 860)); content.UpdateLayout(); Render(content, Path.Combine(output, "software-center-main-preview.png")); ContextMenuTests.RunUi(main, output); app.Shutdown(); completion.SetResult();
        } catch (Exception ex) { completion.SetException(ex); } }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    public static void ReadRealInventory()
    {
        var watch = Stopwatch.StartNew(); var snapshot = new WindowsSoftwareInventory().Read(true, true, CancellationToken.None);
        Check(snapshot.Entries.Any(e => e.Kind == SoftwareKind.Desktop) && snapshot.CompleteSources.Count >= 2, "真实系统登记只读查询失败");
        Check(snapshot.Entries.Where(e => e.Kind == SoftwareKind.Store).All(e => e.Version.Length == 0 || Version.TryParse(e.Version, out _)), "商店版本必须是可读的版本字符串");
        Console.WriteLine($"READ ONLY SOFTWARE: {watch.ElapsedMilliseconds} ms; desktop={snapshot.Entries.Count(e => e.Kind == SoftwareKind.Desktop)}, Store={snapshot.Entries.Count(e => e.Kind == SoftwareKind.Store)}, games={snapshot.Entries.Count(e => e.Kind is SoftwareKind.Steam or SoftwareKind.Epic)}, completeSources={snapshot.CompleteSources.Count}, warnings={string.Join(" | ", snapshot.Warnings)}. No user app uninstall, restore point or registry writes.");
    }
    private static void Render(FrameworkElement element, string path)
    { var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
    private static string Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed class FakeInventory(IReadOnlyList<SoftwareRegistration> entries, IReadOnlyList<SoftwareRegistration> residues) : ISoftwareInventory
    {
        internal readonly Dictionary<string, SoftwareRegistration> Current = entries.ToDictionary(e => e.Id);
        internal IReadOnlyList<SoftwareHistory> LastHistory = []; internal bool Complete = true;
        public SoftwareSnapshot Read(bool store, bool games, CancellationToken token) => new(Current.Values.ToArray(), Complete ? new HashSet<string> { "Fake", "Store" } : new HashSet<string>(), []);
        public SoftwareRegistration? Refresh(SoftwareRegistration original, CancellationToken token) => Current.GetValueOrDefault(original.Id);
        public IReadOnlyList<SoftwareRegistration> FindDirectories(IReadOnlyList<SoftwareRegistration> installed, IReadOnlyList<SoftwareHistory> history, SoftwareOptions options, CancellationToken token) { LastHistory = history; return residues; }
    }
    private sealed class FakeRestore : ISoftwareRestorePoint
    { internal int Count; internal SoftwareRestorePoint Result = new(true, 123, "fixture restore", ""); public Task<SoftwareRestorePoint> CreateAsync(CancellationToken token) { Count++; return Task.FromResult(Result); } }
    private sealed class FakeRunner(FakeInventory inventory) : ISoftwareUninstaller
    {
        internal int Count, MaximumConcurrent; private int _active; internal bool RemoveRegistration = true;
        public async Task<SoftwareLaunchResult> RunAsync(SoftwareRegistration entry, CancellationToken token)
        { Count++; _active++; MaximumConcurrent = Math.Max(MaximumConcurrent, _active); await Task.Delay(5, token); if (RemoveRegistration) inventory.Current.Remove(entry.Id); _active--; return new(0, 12345); }
    }
}
