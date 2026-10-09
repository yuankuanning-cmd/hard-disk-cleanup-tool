using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Win32;

namespace ChatGPTCleanupTool.Controls;

public sealed class DevelopmentActionEventArgs(bool worktrees, bool dryRun, bool manualArchive = false) : EventArgs
{ public bool Worktrees { get; } = worktrees; public bool DryRun { get; } = dryRun; public bool ManualArchive { get; } = manualArchive; }

public partial class DevelopmentCenter : UserControl
{
    private string _search = "";
    public DevelopmentCenter() { InitializeComponent(); }
    public event EventHandler? ScanRequested;
    public event EventHandler? RecordsRequested;
    public event EventHandler<DevelopmentActionEventArgs>? ActionRequested;
    public event EventHandler<DevelopmentItem>? ExcludeRequested;
    public DevelopmentResult? Result { get; private set; }
    public string RootPath { get => RootBox.Text; set => RootBox.Text = value; }
    public string CodexHome { get => CodexBox.Text; set => CodexBox.Text = value; }
    public string MergeTarget { get => MergeBox.Text; set => MergeBox.Text = value; }
    public bool IncludeCodex { get => CodexCheck.IsChecked == true; set => CodexCheck.IsChecked = value; }
    public bool IncludeCaches { get => CacheCheck.IsChecked == true; set => CacheCheck.IsChecked = value; }
    public bool IncludeDocker { get => DockerCheck.IsChecked == true; set => DockerCheck.IsChecked = value; }
    public bool CompareBuilds { get => CompareCheck.IsChecked == true; set => CompareCheck.IsChecked = value; }
    public IReadOnlyList<DevelopmentItem> Selected => Result?.Items.Where(i => i.IsSelected).ToArray() ?? [];

    public void SetResult(DevelopmentResult result)
    {
        if (Result is not null) foreach (var item in Result.Items) item.PropertyChanged -= ItemChanged;
        Result = result;
        foreach (var item in result.Items) item.PropertyChanged += ItemChanged;
        ResultGrid.ItemsSource = result.Items;
        Summary.Text = $"{result.ProjectCount:N0} 个项目 · {result.TaskCount:N0} 个任务 · {result.Items.Count:N0} 个分析项" + (result.Limited ? " · 部分结果" : "");
        WarningBox.Text = "范围提示：项目目录服从下方白名单；勾选 Codex 或用户缓存后会单独纳入指定目录。\n逻辑大小与实际占用分别统计；工作树与其构建子目录有重叠，不将两者相加。\n最近修改是该项及其子文件的最大修改时间，不等于项目源代码最后提交时间。\n非归档任务仅视为仍可能使用，不表示该任务正在运行。\nDocker 仅分析当前 builder，构建记录可以反映项目来源，但共享缓存大小无法可靠分摊。\n\n" + (result.Warnings.Count == 0 ? "本次未出现额外检查错误。" : string.Join("\n\n", result.Warnings));
        RefreshFilter(); UpdateSelection();
    }
    public void SetRecords(IReadOnlyList<DevelopmentOperation> records) => RecordGrid.ItemsSource = records;
    public void ShowOutcome(DevelopmentOperationResult result)
    {
        Summary.Text = $"{(result.DryRun ? "模拟完成" : "操作完成")} · 通过 {result.Succeeded} · 跳过/失败 {result.Failed} · 实际释放记为 0（未清空回收站 / 未核实卷空闲差值）";
        foreach (var item in Selected) item.IsSelected = false;
    }
    public void SetSearchText(string text) { _search = text; RefreshFilter(); }
    private void RefreshFilter()
    {
        if (ResultGrid?.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(ResultGrid.ItemsSource);
        view.Filter = obj => obj is DevelopmentItem i && (FilterBox.SelectedIndex switch
        { 1 => i.Safety == DevelopmentSafety.Regenerable, 2 => i.Safety == DevelopmentSafety.Uncommitted, 3 => i.Safety == DevelopmentSafety.Unknown, 4 => i.Safety is DevelopmentSafety.InUse or DevelopmentSafety.Protected, _ => true }) &&
            (_search.Length == 0 || new[] { i.Name, i.FullPath, i.Project, i.Evidence, i.TaskState, i.SafetyText }.Any(s => s.Contains(_search, StringComparison.OrdinalIgnoreCase)));
        view.Refresh();
    }
    private void UpdateSelection()
    {
        if (SelectionSummary is null) return;
        var selected = Selected;
        SelectionSummary.Text = $"已选 {selected.Count} 项 · 理论回收占用 {CleanupService.FormatBytes(selected.Where(i => i.CanRecycle || i.CanManuallyRecycle).Sum(i => i.TheoreticalBytes))} · 排除仍在目录外保留的硬链接 · 回收站未清空时实际释放为 0";
    }
    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateSelection();
    private void Browse_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "选择项目或项目集合目录" }; if (Directory.Exists(RootPath)) dialog.InitialDirectory = RootPath; if (dialog.ShowDialog() == true) RootPath = dialog.FolderName; }
    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke(this, EventArgs.Empty);
    private void Records_Click(object sender, RoutedEventArgs e) => RecordsRequested?.Invoke(this, EventArgs.Empty);
    private void DryRun_Click(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, new(false, true));
    private void Recycle_Click(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, new(false, false));
    private void Worktree_Click(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, new(true, false));
    private void ManualArchive_Click(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, new(false, false, true));
    private void Exclude_Click(object sender, RoutedEventArgs e) { if (ResultGrid.SelectedItem is DevelopmentItem item) ExcludeRequested?.Invoke(this, item); }
    private void SelectSafe_Click(object sender, RoutedEventArgs e)
    { foreach (DevelopmentItem item in ResultGrid.Items) item.IsSelected = item.CanRecycle; }
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var item in Result?.Items ?? []) item.IsSelected = false; }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    { if (ResultGrid.SelectedItem is DevelopmentItem item) EvidenceText.Text = $"{item.FullPath}\n{item.SafetyText} · {item.ActionText}\n{item.TaskState}\n{item.Evidence}\n{item.DuplicateHint}"; }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is not DevelopmentItem item || item.Kind == DevelopmentKind.Docker) return;
        try { var target = Directory.Exists(item.FullPath) ? item.FullPath : Path.GetDirectoryName(item.FullPath); if (target is not null && Directory.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }
}
