using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Controls;

public enum SpaceAction { OpenLocation, Exclude, Rescan, Recycle }

public partial class SpaceExplorer : UserControl
{
    private DiskTreeNode? _home;
    private DiskTreeNode? _current;
    private bool _approximate;
    private bool _syncingTree;
    private string _globalSearch = string.Empty;
    private readonly Stack<DiskTreeNode> _back = new();
    private string _sort = "allocated DESC,name,id";
    public DiskTreeNode? SelectedNode { get; private set; }
    public DiskAnalysisResult? Result { get; private set; }
    public event Action<DiskTreeNode, SpaceAction>? ActionRequested;
    public event Action<string>? RemoveExclusionRequested;
    internal Func<bool> MenuBlocked { get; set; } = () => false;
    internal Func<string, bool> IsMenuPathProtected { get; set; } = _ => false;
    internal Action? ExportRequested { get; set; }

    public SpaceExplorer()
    {
        InitializeComponent();
        Entries.Sorting += Entries_Sorting;
        Map.NodeClicked += node => { Map.Focus(); Select(node); if (node.IsDirectory) Navigate(node); };
        Map.AggregatedClicked += node => { Navigate(node); Views.SelectedIndex = 1; };
        Map.ContextRequested += node => { Map.Focus(); Select(node); ShowMenu(node, Map); };
        foreach (FrameworkElement element in new FrameworkElement[] { Entries, LargestGrid, GrowthGrid, DirectoryTree })
            ResultContextMenu.Attach(element, BuildSpaceMenu, () => MenuBlocked());
        ResultContextMenu.Attach(ExclusionsList, menu =>
        {
            menu.Separator();
            if (menu.Current?.Item is string path)
                menu.Add("remove-exclusion", "移除当前排除项…", () =>
                {
                    if (MessageBox.Show(Window.GetWindow(this), "移除以下排除项？不会删除文件，但会影响后续扫描/清理保护。\n\n" + path,
                        "移除排除", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) RemoveExclusionRequested?.Invoke(path);
                }, changesState: true,
                    hint: "原件目录必须在重复文件中心解除保护；移除后需重扫。");
        }, () => MenuBlocked());
        Map.Focusable = true;
        ResultContextMenu.Attach(Map, BuildSpaceMenu, () => MenuBlocked(), () => SelectedNode);
    }

    public void SetResult(DiskAnalysisResult result, IEnumerable<string> exclusions)
    {
        Result = result;
        _approximate = result.IsApproximate;
        _back.Clear();
        if (result.Roots.Count == 1) _home = result.Roots[0];
        else
        {
            _home = new DiskTreeNode { Name = "多目录扫描总览", FullPath = "", IsDirectory = true, IsScanRoot = true };
            foreach (var root in result.Roots) _home.Add(root);
            _home.Finish();
        }
        DirectoryTree.ItemsSource = _home is null ? Array.Empty<DiskTreeNode>() : new[] { _home };
        GrowthGrid.ItemsSource = result.GrowthDirectories;
        LargestGrid.ItemsSource = result.LargestFiles;
        Legend.ItemsSource = result.Extensions.Take(10);
        GrowthNote.Text = result.ComparisonNote + (result.IsApproximate ? " 快速模式比较的是近似占用。" : "");
        if (result.GrowthDirectories.Count == 0 && result.Roots.Any(n => n.ComparedAt is not null))
            GrowthNote.Text += " 可比目录没有正增长。";
        SetExclusions(exclusions);
        ((ComboBoxItem)MetricBox.Items[0]).Content = result.IsApproximate ? "面积：近似占用" : "面积：实际占用";
        foreach (var grid in new[] { Entries, LargestGrid })
            grid.Columns.First(c => c.Header?.ToString() is "实际占用" or "近似占用").Header =
                result.IsApproximate ? "近似占用" : "实际占用";
        Navigate(_home, false);
        DirectoryTree.UpdateLayout();
        if (DirectoryTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem container) container.IsExpanded = true;
    }
    public void SetExclusions(IEnumerable<string> paths) => ExclusionsList.ItemsSource = paths.OrderBy(n => n).ToArray();
    public void SetSearchText(string query) { _globalSearch = query.Trim(); RefreshEntryFilter(); }

    public void Navigate(DiskTreeNode? node, bool remember = true)
    {
        if (node is null) return;
        if (!node.IsDirectory) { Navigate(node.Parent, remember); Select(node); Entries.SelectedItem = node; return; }
        if (remember && _current is not null && _current != node) { if (_back.Count >= 64) _back.Clear(); _back.Push(_current); }
        _current = node;
        Map.Root = node;
        CurrentPath.Text = string.IsNullOrEmpty(node.FullPath) ? node.Name : node.FullPath;
        CurrentPath.ToolTip = CurrentPath.Text;
        CurrentStats.Text = $"逻辑大小 {node.LogicalSize}    {(_approximate ? "近似占用" : "实际占用")} {node.AllocatedSize}    " +
            $"文件 {node.FileCount:N0} 个    子目录 {node.Directories.Count:N0} 个" +
            (Result?.NeedsRescan == true ? "    缓存汇总已过期，请重新扫描确认当前占用" : "") +
            (node.IsIncomplete ? "    ⚠ 部分位置未能读取，统计不完整" : node.IsExcluded ? "    已排除" : "");
        Entries.ItemsSource = node.Children;
        RefreshEntryFilter();
        Select(node);
        SyncTreeSelection(node);
    }
    private void SyncTreeSelection(DiskTreeNode node)
    {
        if (_syncingTree) return;
        _syncingTree = true;
        try
        {
            var path = new Stack<DiskTreeNode>();
            for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent) path.Push(ancestor);
            ItemsControl owner = DirectoryTree;
            while (path.TryPop(out var ancestor))
            {
                owner.UpdateLayout();
                if (owner.ItemContainerGenerator.ContainerFromItem(ancestor) is not TreeViewItem container) break;
                if (ancestor == node) { container.IsSelected = true; container.BringIntoView(); }
                else { container.IsExpanded = true; owner = container; }
            }
        }
        finally { _syncingTree = false; }
    }
    private void Select(DiskTreeNode node)
    {
        SelectedNode = node;
        Map.SelectedNode = node;
        SelectionStats.Text = $"{node.Name}  |  逻辑 {node.LogicalSize}  |  {(_approximate ? "近似占用" : "实际占用")} {node.AllocatedSize}" +
                              $"  |  {node.FileCount:N0} 个文件  |  {node.State}";
        SelectionStats.ToolTip = node.FullPath;
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (_back.TryPop(out var node)) Navigate(node, false); }
    private void Up_Click(object sender, RoutedEventArgs e) => Navigate(_current?.Parent);
    private void Home_Click(object sender, RoutedEventArgs e) => Navigate(_home);
    private void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (!_syncingTree && e.NewValue is DiskTreeNode node) Navigate(node); }
    private void Metric_Changed(object sender, SelectionChangedEventArgs e)
    { if (Map is not null) Map.UseAllocated = MetricBox.SelectedIndex == 0; }
    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshEntryFilter();
    private void RefreshEntryFilter()
    {
        if (Entries?.ItemsSource is null || EntrySearch is null) return;
        var query = (EntrySearch.Text + " " + _globalSearch).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (_current?.Source is { } source)
        {
            Entries.ItemsSource = source.GetChildren(_current, terms: query, sort: _sort);
            return;
        }
        CollectionViewSource.GetDefaultView(Entries.ItemsSource).Filter = query.Length == 0 ? null : item =>
            item is DiskTreeNode node && query.All(term => node.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                node.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
    private void Entries_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_current?.Source is null) return;
        e.Handled = true;
        var descending = e.Column.SortDirection != System.ComponentModel.ListSortDirection.Descending;
        foreach (var column in Entries.Columns) column.SortDirection = null;
        e.Column.SortDirection = descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
        var field = e.Column.SortMemberPath switch { "Name" => "name", "LogicalSize" or "LogicalBytes" => "logical", "FileCount" => "file_count", "LastWriteTimeUtc" => "modified", _ => "allocated" };
        _sort = field + (descending ? " DESC" : " ASC") + (field == "name" ? ",id" : ",name,id");
        RefreshEntryFilter();
    }
    private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (sender is DataGrid { SelectedItem: DiskTreeNode node }) Select(node); }
    private void Entries_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not DiskTreeNode node) return;
        if (node.IsDirectory) { Navigate(node); Views.SelectedIndex = 0; }
        else ActionRequested?.Invoke(node, SpaceAction.OpenLocation);
    }
    private void ShowMenu(DiskTreeNode node, FrameworkElement target)
    {
        var menu = ResultContextMenu.NewMenu(); menu.PlacementTarget = target; menu.Placement = PlacementMode.MousePoint;
        var row = MenuTarget.From(node);
        var session = new ResultMenuSession(menu, target, [row], row, false, () => MenuBlocked());
        ResultContextMenu.Populate(session); BuildSpaceMenu(session);
        menu.IsOpen = true;
    }
    private void BuildSpaceMenu(ResultMenuSession menu)
    {
        menu.Separator();
        if (menu.Current?.Item is DiskTreeNode node && node.FullPath.Length > 0)
        {
            menu.Add("navigate", node.IsDirectory ? "进入此目录的空间视图" : "定位到所在目录的空间视图", () => Navigate(node));
            menu.Add("exclude", "加入永久排除（包含子项）", () => ActionRequested?.Invoke(node, SpaceAction.Exclude), changesState: true,
                available: () => !IsMenuPathProtected(node.FullPath));
            menu.Add("rescan-directory", node.IsDirectory ? "只重新扫描此目录" : "重新扫描所在目录", () => ActionRequested?.Invoke(node, SpaceAction.Rescan), changesState: true,
                available: () => !IsMenuPathProtected(node.FullPath));
            menu.Separator();
            menu.Add("recycle", node.IsDirectory ? "整个目录移入回收站…" : "文件移入回收站…", () => ActionRequested?.Invoke(node, SpaceAction.Recycle),
                changesState: true, available: () => node.CanRecycle && !IsMenuPathProtected(node.FullPath) && Result?.NeedsRescan != true && Result?.IsHistoricalSnapshot != true,
                hint: "目录包含全部子项；仍需确认与一致性检查。历史快照、过期/不完整/保护结果不能回收。");
        }
        if (ExportRequested is not null) menu.Add("export", "导出空间分析结果…", ExportRequested, changesState: true, allowLargeSelection: true);
    }
    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    { if (ExclusionsList.SelectedItem is string path) RemoveExclusionRequested?.Invoke(path); }
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
