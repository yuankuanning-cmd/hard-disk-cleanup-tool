using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Controls;

public sealed class SoftwareActionEventArgs(bool dryRun) : EventArgs { public bool DryRun { get; } = dryRun; }
public partial class SoftwareCenter : UserControl
{
    private string _search = "";
    public SoftwareCenter() { InitializeComponent(); }
    public event EventHandler? ScanRequested;
    public event EventHandler? RestoreRequested;
    public event EventHandler? RecordsRequested;
    public event EventHandler<SoftwareActionEventArgs>? UninstallRequested;
    public SoftwareResult? Result { get; private set; }
    public bool IncludeStore { get => StoreBox.IsChecked == true; set => StoreBox.IsChecked = value; }
    public bool IncludeGames { get => GameBox.IsChecked == true; set => GameBox.IsChecked = value; }
    public bool FindResidues { get => ResidueBox.IsChecked == true; set => ResidueBox.IsChecked = value; }
    public bool MeasureDirectories { get => MeasureBox.IsChecked == true; set => MeasureBox.IsChecked = value; }
    public IReadOnlyList<SoftwareItem> Selected => Result?.Items.Where(i => i.IsSelected).ToArray() ?? [];
    public void SetResult(SoftwareResult result)
    {
        if (Result is not null) foreach (var item in Result.Items) item.PropertyChanged -= ItemChanged;
        Result = result; foreach (var item in result.Items) item.PropertyChanged += ItemChanged;
        ResultGrid.ItemsSource = result.Items;
        Summary.Text = $"{result.Items.Count:N0} 个软件/内容/提示 · 可卸载入口 {result.Items.Count(i => i.CanUninstall):N0} · 疑似残留 {result.Items.Count(i => i.Registration.Kind is SoftwareKind.DirectoryResidue or SoftwareKind.RegistryResidue):N0}" + (result.Limited ? " · 部分测量" : "");
        WarningText.Text = string.Join("\n\n", result.Warnings); RefreshFilter(); SortBySize(); UpdateSelection();
    }
    public void SetRecords(IReadOnlyList<SoftwareOperation> records) => RecordGrid.ItemsSource = records;
    public void ShowOutcome(SoftwareBatchResult result)
    {
        Summary.Text = (result.DryRun ? "模拟检查完成" : "本批操作结束") + "；请查看逐项记录，实际卸载后重新扫描。未删除任何残留。";
        foreach (var item in Selected) item.IsSelected = false;
    }
    public void SetSearchText(string text) { _search = text; RefreshFilter(); }
    private void RefreshFilter()
    {
        if (ResultGrid?.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(ResultGrid.ItemsSource);
        view.Filter = row => row is SoftwareItem i && (FilterBox.SelectedIndex switch
        { 1 => i.Registration.Kind == SoftwareKind.Desktop, 2 => i.Registration.Kind == SoftwareKind.Store, 3 => i.Registration.Kind is SoftwareKind.Steam or SoftwareKind.Epic,
            4 => i.Registration.Kind == SoftwareKind.Orphan || i.Registration.Kind == SoftwareKind.Desktop && i.Status.Contains("缺失/指令不支持"), 5 => i.Registration.Kind is SoftwareKind.DirectoryResidue or SoftwareKind.RegistryResidue,
            6 => i.Registration.Protected || i.Status.Contains("保护"), _ => true }) &&
            (_search.Length == 0 || new[] { i.Name, i.FullPath, i.Publisher, i.Status, i.Evidence }.Any(s => s.Contains(_search, StringComparison.OrdinalIgnoreCase)));
        view.Refresh();
    }
    private void SortBySize()
    { if (ResultGrid.ItemsSource is null) return; var view = CollectionViewSource.GetDefaultView(ResultGrid.ItemsSource); view.SortDescriptions.Clear(); view.SortDescriptions.Add(new(nameof(SoftwareItem.SortBytes), System.ComponentModel.ListSortDirection.Descending)); }
    private void UpdateSelection() => SelectionText.Text = $"已选 {Selected.Count} 项 · 卸载将删除软件，可能影响配置/存档；还原点不是个人数据备份。";
    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateSelection();
    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke(this, EventArgs.Empty);
    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);
    private void Records_Click(object sender, RoutedEventArgs e) => RecordsRequested?.Invoke(this, EventArgs.Empty);
    private void DryRun_Click(object sender, RoutedEventArgs e) => UninstallRequested?.Invoke(this, new(true));
    private void Uninstall_Click(object sender, RoutedEventArgs e) => UninstallRequested?.Invoke(this, new(false));
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void Sort_Click(object sender, RoutedEventArgs e) => SortBySize();
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var item in Result?.Items ?? []) item.IsSelected = false; }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    { if (ResultGrid.SelectedItem is SoftwareItem item) EvidenceText.Text = $"{item.Name} · {item.FullPath}\n{item.Status} · {item.SizeBasis}\n{item.Evidence}\n卸载登记：{item.Registration.UninstallCommand}"; }
    private void Settings_Click(object sender, RoutedEventArgs e)
    { try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("打开设置失败：" + ex.Message); } }
    private void Copy_Click(object sender, RoutedEventArgs e) { if (ResultGrid.SelectedItem is SoftwareItem item) Clipboard.SetText(item.FullPath); }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is not SoftwareItem item || item.Registration.Kind == SoftwareKind.RegistryResidue) return;
        try { if (Directory.Exists(item.FullPath)) Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }
}
