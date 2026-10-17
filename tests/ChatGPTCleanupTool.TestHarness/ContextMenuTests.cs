using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

internal static class ContextMenuTests
{
    internal static void RunUi(MainWindow main, string output)
    {
        var tabs = (TabControl)main.FindName("MainTabControl");
        Check(tabs.Items.Count == 11, "十一页菜单覆盖缺失");
        foreach (TabItem tab in tabs.Items)
        {
            var page = (FrameworkElement)tab.Content;
            Check(page.ContextMenu is not null, tab.Header + "空白处菜单缺失");
            var menu = ResultContextMenu.BuildForTest(page, null, true);
            Check(Find(menu, "refresh") is not null && Find(menu, "export") is not null, tab.Header + "缺少刷新/导出入口");
            Check(Find(menu, "open-location") is null, "空白处不能复用旧行路径");
        }
        var grids = new List<FrameworkElement>
        {
            (DataGrid)main.FindName("CategoryGrid"), (DataGrid)main.FindName("DetailGrid"), (DataGrid)main.FindName("CodexSmartGrid"),
            (DataGrid)main.FindName("ExternalStorageGrid"), (DataGrid)main.FindName("HistoryGrid")
        };
        foreach (var (controlName, names) in new[]
        {
            ("DuplicateCenterView", new[] { "FilesGrid", "RecordsGrid", "ReferencesList" }),
            ("MediaCenterView", new[] { "Results" }),
            ("DevelopmentCenterView", new[] { "ResultGrid", "RecordGrid" }),
            ("SoftwareCenterView", new[] { "ResultGrid", "RecordGrid" }),
            ("NpsCenterView", new[] { "ProjectGrid", "RecordGrid" }),
            ("OverviewCenterView", new[] { "DriveCards", "FavoriteItems" }),
            ("AnalysisExplorer", new[] { "Entries", "LargestGrid", "GrowthGrid", "DirectoryTree", "ExclusionsList", "Map" })
        })
            foreach (var name in names) grids.Add((FrameworkElement)((FrameworkElement)main.FindName(controlName)).FindName(name));
        Check(grids.Count == 23 && grids.All(g => g.ContextMenu is not null), "23个结果/列表/树图/审计区域必须都有菜单");

        var software = (SoftwareCenter)main.FindName("SoftwareCenterView");
        var a = Item("菜单夹具A", @"C:\fixture-only\a.bin", true);
        var b = Item("菜单夹具B", @"C:\fixture-only\b.bin", true);
        var protectedItem = Item("受保护项", @"HKEY_CURRENT_USER\Software\Fixture", false);
        software.SetResult(new([a, b, protectedItem], [], false, DateTime.UtcNow));
        var grid = (DataGrid)software.FindName("ResultGrid");
        grid.SelectedItems.Add(a); grid.SelectedItems.Add(b);
        var selectedMenu = ResultContextMenu.BuildForTest(grid, b, true);
        Check(grid.SelectedItems.Count == 2 && selectedMenu.Items.OfType<MenuItem>().First().Header.ToString()!.Contains("菜单夹具B"), "右键已多选行须保留多选且指向实际右键项");
        Click(selectedMenu, "check-rows"); Check(a.IsSelected && b.IsSelected, "所选软件勾选操作错误");
        Click(ResultContextMenu.BuildForTest(grid, b, true), "uncheck-rows"); Check(!a.IsSelected && !b.IsSelected, "取消勾选应只影响所选行");
        var protectedMenu = ResultContextMenu.BuildForTest(grid, protectedItem, true);
        Check(grid.SelectedItems.Count == 1 && grid.SelectedItem == protectedItem && !Find(protectedMenu, "check-rows")!.IsEnabled, "未选中行的右键目标或保护门禁错误");
        Click(protectedMenu, "check-rows"); Check(!protectedItem.IsSelected, "禁用项不得绕过保护");
        Check(!Find(protectedMenu, "open-location")!.IsEnabled && Find(protectedMenu, "copy-paths")!.IsEnabled, "注册表可以复制但不是本地文件");
        var blank = ResultContextMenu.BuildForTest(grid, null, true);
        Check(Find(blank, "open-location") is null && Find(blank, "check-rows") is null && Find(blank, "exclude") is null, "空白处不得误用之前选中的路径或勾选目标");

        // Exercise the actual pointer handler with a realized row, not only the snapshot helper.
        tabs.SelectedIndex = 8;
        var content = (FrameworkElement)main.Content;
        content.Measure(new Size(1320, 860)); content.Arrange(new Rect(0, 0, 1320, 860)); content.UpdateLayout();
        grid.SelectedItem = a;
        if (grid.ItemContainerGenerator.ContainerFromItem(b) is DataGridRow row)
        {
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = row });
            Check(grid.SelectedItem == b, "实际右键路由没有选择命中的行");
        }
        else throw new InvalidOperationException("测试行未呈现，不能假称实际路由验证");

        bool blocked = false; int calls = 0;
        var safeGrid = new DataGrid { ItemsSource = new[] { a }, SelectionMode = DataGridSelectionMode.Extended };
        ResultContextMenu.Attach(safeGrid, menu =>
        {
            menu.Add("test-state", "状态测试", () => calls++, changesState: true);
            menu.Add("test-export", "导出测试", () => { }, changesState: true, allowLargeSelection: true);
        }, () => blocked);
        safeGrid.SelectedItem = a;
        var beforeBusy = ResultContextMenu.BuildForTest(safeGrid);
        blocked = true; Click(beforeBusy, "test-state"); Check(calls == 0, "菜单打开后变忙，旧入口不能执行");
        Check(!Find(ResultContextMenu.BuildForTest(safeGrid), "test-state")!.IsEnabled, "忙时应禁用变更操作");
        blocked = false;
        var keyboardGrid = new DataGrid { ItemsSource = new[] { a }, Width = 400, Height = 200 };
        ResultContextMenu.Attach(keyboardGrid, _ => { }); keyboardGrid.SelectedItem = a;
        using (var keyboardSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden context menu test")
        { Width = 400, Height = 200, WindowStyle = unchecked((int)0x80000000) }))
        {
            keyboardSource.RootVisual = keyboardGrid; keyboardGrid.UpdateLayout();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            keyboardGrid.RaiseEvent(key);
            Check(key.Handled && keyboardGrid.ContextMenu!.IsOpen && Find(keyboardGrid.ContextMenu, "copy-paths") is not null, "键盘菜单键应打开所选项菜单");
            keyboardGrid.ContextMenu!.IsOpen = false;
        }
        var many = Enumerable.Range(0, 1001).Select(i => Item("row" + i, @"C:\fixture-only\" + i, true)).ToArray();
        safeGrid.ItemsSource = many; foreach (var item in many) safeGrid.SelectedItems.Add(item);
        Check(!Find(ResultContextMenu.BuildForTest(safeGrid), "test-state")!.IsEnabled, "超过上限不得静默处理前1000行");
        Check(Find(ResultContextMenu.BuildForTest(safeGrid), "test-export")!.IsEnabled, "超过多选上限时应仍允许导出完整结果");

        var lazy = new GuardedMillionList(a); var lazyGrid = new DataGrid { ItemsSource = lazy };
        ResultContextMenu.Attach(lazyGrid, _ => { }); lazyGrid.SelectedItem = a;
        lazy.ForbidEnumeration = true;
        var watch = Stopwatch.StartNew(); var lazyMenu = ResultContextMenu.BuildForTest(lazyGrid);
        Check(Find(lazyMenu, "copy-paths") is not null && watch.ElapsedMilliseconds < 2000, "菜单不得枚举百万文件源"); watch.Stop();

        var center = (DuplicateCenter)main.FindName("DuplicateCenterView");
        var copies = new[] { Duplicate("a"), Duplicate("b") };
        center.Configure(new ReferenceDirectoryService(Path.Combine(output, "context-test-missing-references.json")), new DuplicateOperationLogService(Path.Combine(output, "context-test-unused-audit.sqlite")));
        center.SetFiles(copies, []);
        center.SelectFromMenu(copies, true); Check(copies.Count(f => f.IsSelected) == 1, "重复副本右键多选必须留一份");
        center.KeepFromMenu(copies[1]); Check(!copies[1].IsSelected && copies[0].IsSelected, "保留此副本策略错误");
        copies[0].IsReference = true; center.SelectFromMenu(copies, true); Check(!copies[0].IsSelected, "参考原件不能右键勾选");

        var media = (MediaCenter)main.FindName("MediaCenterView");
        var m1 = new MediaResultItem { Fingerprint = new() { Path = @"C:\fixture-only\m1.mp4", Kind = MediaKind.Video }, Group = 1 };
        var m2 = new MediaResultItem { Fingerprint = new() { Path = @"C:\fixture-only\m2.mp4", Kind = MediaKind.Video }, Group = 2 };
        media.SetResult(new([m1, m2], [], 0, 0, 2, false, false));
        media.SetContextGroup(1); Check(media.ResultGrid.Items.Count == 1, "相似组右键筛选错误");
        media.SetContextGroup(null); Check(media.ResultGrid.Items.Count == 2, "相似组恢复错误");
        Check(!ResultContextMenu.IsFilePath(@"\\server\share") && !ResultContextMenu.IsFilePath("docker system df"), "菜单不得将网络/命令详情当文件");

        software.SetResult(SoftwareTests.UiResult);
        content.UpdateLayout();
        Render(ResultContextMenu.BuildForTest(grid, SoftwareTests.UiResult.Items.FirstOrDefault(), true), Path.Combine(output, "context-menu-software-preview.png"));
        Console.WriteLine($"PASS CONTEXT MENUS: all 11 pages + 23 result/list/tree/map/audit surfaces; actual right-click target, keyboard menu key, preserved multi-select, blank/stale protection, protected items, late busy guard, generic 1000 row cap, million-source no-enumeration ({watch.ElapsedMilliseconds} ms), duplicate keeper/reference and media group; no cleanup/uninstall/restore/clipboard writes");
    }
    private static SoftwareItem Item(string name, string path, bool allowed) => new() { Registration = new(name, "fixture", SoftwareKind.Desktop, name, "", "", path, 1), CanUninstall = allowed, Status = allowed ? "测试项" : "受保护" };
    private static DuplicateFileItem Duplicate(string leaf) => new() { GroupNumber = 1, Copies = 2, FullPath = @"C:\fixture-only\" + leaf, LogicalBytes = 1, AllocatedBytes = 4096, LogicalSize = "1 B", AllocatedSize = "4 KiB", Hash = "fixture", FileIdentity = leaf, IsFullHashVerified = true, LastWriteTime = DateTime.UtcNow };
    private static MenuItem? Find(ContextMenu menu, string id) => menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag?.ToString() == id);
    private static void Click(ContextMenu menu, string id) => (Find(menu, id) ?? throw new InvalidOperationException("缺少菜单：" + id)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static void Render(ContextMenu menu, string path)
    {
        menu.Measure(new Size(530, 760)); menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(menu);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class GuardedMillionList(object item) : IList
    {
        internal bool ForbidEnumeration;
        public int Count => 1_000_000;
        public object? this[int index] { get => item; set => throw new NotSupportedException(); }
        public bool IsReadOnly => true; public bool IsFixedSize => true; public bool IsSynchronized => false; public object SyncRoot => this;
        public bool Contains(object? value) => value == item; public int IndexOf(object? value) => value == item ? 0 : -1;
        public IEnumerator GetEnumerator() { if (ForbidEnumeration) throw new InvalidOperationException("菜单枚举了百万源"); return new[] { item }.GetEnumerator(); }
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public int Add(object? value) => throw new NotSupportedException(); public void Clear() => throw new NotSupportedException(); public void Insert(int index, object? value) => throw new NotSupportedException(); public void Remove(object? value) => throw new NotSupportedException(); public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
