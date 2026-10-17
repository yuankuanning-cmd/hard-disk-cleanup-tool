using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

internal static class OverviewTests
{
    internal static readonly StorageOverview Fixture = new([
        new(@"C:\", "Windows", "NTFS", 512L << 30, 156L << 30),
        new(@"D:\", "Projects", "NTFS", 1024L << 30, 230L << 30),
        new(@"E:\", "Archive", "NTFS", 512L << 30, 30L << 30)], [], DateTime.UtcNow);
    internal static async Task RunAsync(string root)
    {
        Check(Fixture.Drives[0].UsedPercent > 69 && Fixture.Drives[0].SpaceStatus == "空间充足" && Fixture.Drives[2].SpaceStatus == "空间紧张", "磁盘容量计算/提示错误");
        var service = new StorageOverviewService(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await service.ReadAsync(canceled.Token); throw new InvalidOperationException("容量刷新取消未生效"); } catch (OperationCanceledException) { }
        var settings = new AppSettingsService(Path.Combine(root, "overview-settings.json"));
        settings.Save(new() { InterfaceVersion = 3, FavoriteScanPaths = [@"D:\projects", @"D:\models"], NpsAllDisks = false, DryRun = true });
        var loaded = settings.Load(); Check(loaded.FavoriteScanPaths.Length == 2 && loaded.DryRun && !loaded.NpsAllDisks, "收藏持久化破坏旧偏好");
        Check(MainWindow.OverviewPageIndex("nps") == 9 && MainWindow.OverviewPageIndex("analyze") == 2 && MainWindow.OverviewPageIndex("codex") == 7, "首页路由改变原页面顺序");
        Console.WriteLine("PASS OVERVIEW: capacity/pressure states, cancellation, bookmark persistence with existing safety settings, exact page routing; volume query only, no scanning/deletion");
    }
    internal static void RunUi(MainWindow main, string output)
    {
        var tabs = (TabControl)main.FindName("MainTabControl"); Check(tabs.Items.Count == 11, "首页未加入");
        var center = (OverviewCenter)main.FindName("OverviewCenterView"); tabs.SelectedIndex = 10;
        var savedFavorites = center.FavoritePaths.ToArray();
        center.SetSnapshot(Fixture); center.SetFavoritePaths([@"D:\AAA_work\model\SimuNPS-EMT", @"D:\GitHub", @"D:\GitHub"]);
        ((TextBlock)center.FindName("UpdatedLabel")).Text = "界面预览 · 以下为示例磁盘数据，非当前电脑实际容量";
        Check(center.FavoritePaths.Count == 2 && !center.AddFavorite(@"\\server\share") && !center.AddFavorite("relative"), "收藏应去重并避免启动时访问网络/相对目录");
        center.AddFavorite(@"D:\temporary-fixture", notify: false); Check(center.RemoveFavorite(@"D:\temporary-fixture", notify: false), "移除收藏失败");
        var normal = (SolidColorBrush)new StatusBrushConverter().Convert("已回收", typeof(Brush), null!, CultureInfo.InvariantCulture);
        var danger = (SolidColorBrush)new StatusBrushConverter().Convert("失败", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Check(normal.Color != danger.Color && normal.IsFrozen, "状态色必须分明且可安全跨线程共享");
        CheckSingleClick();
        center.SetCandidates([]);
        var content = (FrameworkElement)main.Content; content.Margin = new Thickness(0); ((Panel)content).Background = main.Background;
        content.Measure(new Size(1440, 960)); content.Arrange(new Rect(0, 0, 1440, 960)); content.UpdateLayout();
        var nav = Descendants<NavigationPanel>(tabs).Single();
        Check(((TabItem)tabs.Items[10]).TranslatePoint(new Point(), nav).Y < ((TabItem)tabs.Items[0]).TranslatePoint(new Point(), nav).Y, "首页应显示在侧栏首位，旧索引保持不变");
        Check(Descendants<DiskGauge>(center).Count() == 3, "三个磁盘环图没有呈现");
        Check(((DataGrid)main.FindName("CategoryGrid")).FontWeight == FontWeights.Normal, "表格不能继承导航粗体");
        var drives = (ListBox)center.FindName("DriveCards"); drives.SelectedIndex = 0;
        Check(ResultContextMenu.BuildForTest(drives).Items.OfType<MenuItem>().Any(i => i.Tag?.ToString() == "analyze-overview"), "磁盘卡片菜单入口缺失");
        var bookmarks = (ListBox)center.FindName("FavoriteItems"); bookmarks.SelectedIndex = 0;
        Check(ResultContextMenu.BuildForTest(bookmarks).Items.OfType<MenuItem>().Any(i => i.Tag?.ToString() == "remove-bookmark"), "收藏菜单缺失");
        Render(content, Path.Combine(output, "overview-modern-preview.png"));
        foreach (int index in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })
        { tabs.SelectedIndex = index; content.UpdateLayout(); Render(content, Path.Combine(output, $"modern-page-{index}.png")); }
        tabs.SelectedIndex = 10; content.UpdateLayout();
        center.SetFavoritePaths(savedFavorites);
        Console.WriteLine("PASS MODERN UI: 11-page sidebar, overview-first without index changes, capacity gauges, bookmarks and menus, normal-weight virtualized tables, semantic colors, all page renders");
    }
    internal static Task StandaloneUiAsync()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { var app = new App(); app.InitializeComponent(); var main = new MainWindow(); var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "outputs")); Directory.CreateDirectory(output); RunUi(main, output); app.Shutdown(); source.SetResult(); }
            catch (Exception ex) { source.SetException(ex); }
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return source.Task;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private sealed class SelectionFixture(bool allowed)
    {
        public bool Allowed { get; } = allowed;
        private bool _selected;
        public bool IsSelected { get => _selected; set => _selected = value && Allowed; }
    }
    private static void CheckSingleClick()
    {
        var yes = new SelectionFixture(true); var no = new SelectionFixture(false);
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, ItemsSource = new[] { yes, no }, Width = 300, Height = 180 };
        var enabledStyle = new Style(typeof(CheckBox)); enabledStyle.Setters.Add(new Setter(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(nameof(SelectionFixture.Allowed))));
        grid.Columns.Add(new DataGridCheckBoxColumn { Binding = new System.Windows.Data.Binding(nameof(SelectionFixture.IsSelected)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 70, ElementStyle = enabledStyle, EditingElementStyle = enabledStyle });
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden checkbox test") { Width = 300, Height = 180, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = grid; grid.UpdateLayout();
        DataGridCell Cell(int i) => Descendants<DataGridCell>((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(i)).First();
        void Click(DataGridCell cell) => grid.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = cell });
        Click(Cell(0)); Check(yes.IsSelected, $"普通表格复选框应一次点击即可勾选: attached={GridCheckBehavior.GetSingleClick(grid)}, readonly={Cell(0).IsReadOnly}, editing={Cell(0).IsEditing}, content={Cell(0).Content?.GetType().Name}");
        Click(Cell(0)); Check(!yes.IsSelected, "普通表格再次点击应取消勾选");
        Click(Cell(1)); Check(!no.IsSelected, "单击优化不能绕过禁用的受保护项目");
        grid.IsReadOnly = true; Click(Cell(0)); Check(!yes.IsSelected, "只读表格不能借单击优化更改状态");
    }
    private static void Render(FrameworkElement element, string path)
    { var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
