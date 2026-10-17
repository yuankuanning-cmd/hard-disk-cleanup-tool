using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ChatGPTCleanupTool.Models;
using Microsoft.Win32;

namespace ChatGPTCleanupTool.Controls;

public sealed class NpsActionEventArgs(bool dryRun) : EventArgs { public bool DryRun { get; } = dryRun; }
public partial class NpsCenter : UserControl
{
    private string _search = "";
    private IReadOnlyList<NpsCleanupItem> _plan = [];
    public NpsCenter() { InitializeComponent(); }
    private bool _bulkSelection;
    public event EventHandler? ScanRequested, PreviousRequested, RecordsRequested;
    public event EventHandler<NpsActionEventArgs>? RecycleRequested;
    public NpsScanResult? Result { get; private set; }
    public bool AllDisks { get => ScopeBox.SelectedIndex == 0; set => ScopeBox.SelectedIndex = value ? 0 : 1; }
    public bool IncludeRemovable { get => RemovableBox.IsChecked == true; set => RemovableBox.IsChecked = value; }
    public string RootPath { get => RootBox.Text; set => RootBox.Text = value; }
    public IReadOnlyList<NpsProjectItem> SelectedProjects => ProjectGrid.SelectedItems.Cast<NpsProjectItem>().ToArray();
    public IReadOnlyList<NpsCleanupItem> SelectedPlan => _plan.Where(p => p.IsSelected).ToArray();
    public void SetResult(NpsScanResult result, bool historical = false)
    {
        Result = result; ProjectGrid.ItemsSource = result.Projects; SetPlan(result.Projects.Select(p => p.Cleanup).OfType<NpsCleanupItem>().ToArray());
        Summary.Text = $"工程 {result.Projects.Count(p => p.Recognized && p.Kind is not NpsProjectKind.Recovery and not NpsProjectKind.StandaloneModel):N0} · 候选/线索 {result.Projects.Count(p => !p.Recognized || p.Kind == NpsProjectKind.StandaloneModel):N0} · 恢复快照 {result.Projects.Count(p => p.Kind == NpsProjectKind.Recovery):N0} · 跳过 {result.Skipped:N0}" +
            (historical ? " · 上次结果（大小已自动更新）" : result.Complete ? " · 扫描结束" : " · 部分结果");
        Warnings.Text = $"发现时间UTC：{result.ScannedUtc:O}\n范围：{result.Scope}\n扫描策略：{result.Scheduling}\n\n{string.Join("\n\n", result.Warnings)}";
        RefreshFilter();
        var view = CollectionViewSource.GetDefaultView(ProjectGrid.ItemsSource);
        view.SortDescriptions.Clear(); view.SortDescriptions.Add(new(nameof(NpsProjectItem.LogicalBytes), System.ComponentModel.ListSortDirection.Descending));
        ProjectGrid.Columns[2].SortDirection = System.ComponentModel.ListSortDirection.Descending;
    }
    public void SetPlan(IReadOnlyList<NpsCleanupItem> plan)
    {
        foreach (var item in _plan) item.PropertyChanged -= ItemChanged;
        _plan = plan; foreach (var item in plan) item.PropertyChanged += ItemChanged;
        var byProject = plan.ToDictionary(p => p.ProjectId, StringComparer.OrdinalIgnoreCase);
        foreach (var project in Result?.Projects ?? []) project.Cleanup = byProject.GetValueOrDefault(project.Id);
        ProjectGrid.Items.Refresh(); UpdateSelection();
    }
    public void SetRecords(IReadOnlyList<NpsOperation> records) => RecordGrid.ItemsSource = records;
    public void SetSearchText(string search) { _search = search; RefreshFilter(); }
    private void RefreshFilter()
    {
        if (ProjectGrid?.ItemsSource is null) return;
        CollectionViewSource.GetDefaultView(ProjectGrid.ItemsSource).Filter = value => value is NpsProjectItem item &&
            (TypeBox.SelectedIndex switch { 1 => item.Recognized && item.Kind is not NpsProjectKind.Recovery and not NpsProjectKind.StandaloneModel, 2 => item.Kind == NpsProjectKind.Backup, 3 => item.Kind == NpsProjectKind.Recovery,
                4 => item.Kind == NpsProjectKind.StandaloneModel, 5 => item.Kind == NpsProjectKind.Suspected, _ => true }) &&
            (_search.Length == 0 || (item.Name + " " + item.FullPath + " " + item.KindText + " " + item.Evidence).Contains(_search, StringComparison.OrdinalIgnoreCase));
    }
    private void Type_Changed(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void Project_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (!_bulkSelection) UpdateSelection(); }
    private void UpdateSelection()
    {
        if (SelectionText is null) return;
        var selected = SelectedPlan;
        SelectionText.Text = $"已勾选 {selected.Count:N0} 项 · 合计大小 {Services.CleanupService.FormatBytes(selected.Sum(p => p.LogicalBytes))} · 实际占用 {Services.CleanupService.FormatBytes(selected.Sum(p => p.AllocatedBytes))} · 无每批数量限制；回收站清空后才释放空间";
        if (DeleteButton is not null) DeleteButton.IsEnabled = selected.Count > 0;
    }
    public void SetHighlightedSelection(bool selected) => SetSelection(SelectedProjects, selected);
    public void SelectVisible() => SetSelection(ProjectGrid.Items.OfType<NpsProjectItem>(), true);
    public void ClearSelection() => SetSelection(Result?.Projects ?? [], false);
    private void SetSelection(IEnumerable<NpsProjectItem> rows, bool selected)
    {
        _bulkSelection = true;
        try { foreach (var row in rows) if (row.Cleanup is { } item) item.IsSelected = selected; }
        finally { _bulkSelection = false; UpdateSelection(); }
    }
    public void RefreshRows() { ProjectGrid.Items.Refresh(); UpdateSelection(); }
    private void Browse_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "选择NPS工程查找目录" }; if (Directory.Exists(RootPath)) dialog.InitialDirectory = RootPath; if (dialog.ShowDialog(Window.GetWindow(this)) == true) { RootPath = dialog.FolderName; AllDisks = false; } }
    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke(this, EventArgs.Empty);
    private void Previous_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);
    private void Records_Click(object sender, RoutedEventArgs e) => RecordsRequested?.Invoke(this, EventArgs.Empty);
    private void Recycle_Click(object sender, RoutedEventArgs e) => RecycleRequested?.Invoke(this, new(false));
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectVisible();
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearSelection();
}
