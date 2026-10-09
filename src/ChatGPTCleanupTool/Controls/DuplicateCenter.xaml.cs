using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Win32;

namespace ChatGPTCleanupTool.Controls;

public partial class DuplicateCenter : UserControl
{
    private ReferenceDirectoryService? _references;
    private DuplicateOperationLogService? _log;
    private IReadOnlyList<string> _exclusions = [];
    private IList _files = Array.Empty<DuplicateFileItem>();
    private ListCollectionView? _view;
    private string _search = "";
    private CancellationTokenSource? _previewCancellation;
    public DataGrid ResultGrid => FilesGrid;
    public bool IsPlanning { get; private set; }
    public bool ProtectionReady { get; private set; }
    public event EventHandler? SelectionPlanChanged;
    public event EventHandler? ReferenceProtectionChanged;
    public DuplicateKeepPolicy KeepPolicy { get => (DuplicateKeepPolicy)Math.Max(0, PolicyBox.SelectedIndex); set => PolicyBox.SelectedIndex = (int)value; }
    public bool OnlyCrossDirectory { get => CrossBox.IsChecked == true; set => CrossBox.IsChecked = value; }
    public bool OnlyBackupOverlap { get => BackupBox.IsChecked == true; set => BackupBox.IsChecked = value; }
    public bool OnlyReferenceMatched { get => ReferenceBox.IsChecked == true; set => ReferenceBox.IsChecked = value; }
    public DuplicateCenter() { InitializeComponent(); Unloaded += (_, _) => _previewCancellation?.Cancel(); }
    public void Configure(ReferenceDirectoryService references, DuplicateOperationLogService log)
    { _references = references; _log = log; RefreshProtection(); }
    public void SetFiles(IList files, IReadOnlyList<string> exclusions)
    {
        _previewCancellation?.Cancel(); PreviewList.ItemsSource = null;
        _files = files; _exclusions = exclusions; _view = new ListCollectionView(files); FilesGrid.ItemsSource = _view;
        RefreshProtection();
        DirectoryBox.ItemsSource = Members.GroupBy(f => f.DirectoryPath, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).Select(g => new DuplicateDirectoryChoice(g.Key, g.Count())).ToArray();
        DirectoryBox.SelectedIndex = 0; RefreshFilters();
    }
    private IEnumerable<DuplicateFileItem> Members => _files.Cast<DuplicateFileItem>();
    public void SetSearchText(string search) { _search = search; RefreshFilters(); }
    public void RefreshProtection()
    {
        if (_references is null) return;
        var wasPlanning = IsPlanning; IsPlanning = true;
        try
        {
            var roots = _references.GetPaths(); ReferencesList.ItemsSource = roots;
            DuplicateDecisionService.Annotate(Members, roots, _exclusions); ProtectionReady = true;
            ProtectionMessage.Text = $"已保护 {roots.Count} 个参考目录。解除保护不会删除文件。";
        }
        catch (Exception ex)
        {
            ProtectionReady = false; foreach (var f in Members) f.IsSelected = false;
            ProtectionMessage.Text = "保护配置无法读取，所有处理已禁用：" + ex.Message;
        }
        finally { IsPlanning = wasPlanning; }
        RefreshFilters(); SelectionPlanChanged?.Invoke(this, EventArgs.Empty);
    }
    public void UpdateExclusions(IReadOnlyList<string> paths) { _exclusions = paths; RefreshProtection(); }
    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshFilters();
    public void RefreshFilters()
    {
        if (_view is null) return;
        var allowed = Members.GroupBy(f => f.GroupNumber).Where(g =>
            (!OnlyCrossDirectory || g.Any(f => f.IsCrossDirectory)) && (!OnlyBackupOverlap || g.Any(f => f.HasBackupWorkOverlap)) &&
            (!OnlyReferenceMatched || g.Any(f => f.GroupHasReference)) && (string.IsNullOrWhiteSpace(_search) || g.Any(f => (f.FullPath + " " + f.Role + " " + f.GroupNumber).Contains(_search, StringComparison.OrdinalIgnoreCase))))
            .Select(g => g.Key).ToHashSet();
        _view.Filter = o => o is DuplicateFileItem f && allowed.Contains(f.GroupNumber); _view.Refresh();
        ViewSummary.Text = $"可见 {allowed.Count:N0} 组 / {_view.Count:N0} 项（隐藏的已选项仍计入底部合计）";
    }
    private async void ApplyPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_view is null || IsPlanning || !ProtectionReady) return;
        IsPlanning = true; IsEnabled = false; SelectionPlanChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var visible = _view.Cast<DuplicateFileItem>().ToArray(); var policy = KeepPolicy;
            if (policy is DuplicateKeepPolicy.HighestResolution or DuplicateKeepPolicy.HighestQuality)
            {
                await DuplicateDecisionService.ReadImageInformationAsync(visible);
                foreach (var f in visible) f.NotifyImage();
            }
            DuplicateDecisionService.Apply(visible, policy);
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "生成选择计划失败"); }
        finally { IsPlanning = false; IsEnabled = true; SelectionPlanChanged?.Invoke(this, EventArgs.Empty); }
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Plan(() => { foreach (var f in Members) f.IsSelected = false; });
    private void SelectDirectory_Click(object sender, RoutedEventArgs e) => SelectDirectory(true);
    private void UnselectDirectory_Click(object sender, RoutedEventArgs e) => SelectDirectory(false);
    private void SelectDirectory(bool value)
    {
        if (_view is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(DirectoryBox.Text)) return;
            var path = DiskAnalysisService.NormalizePath(DirectoryBox.Text.Trim());
            Plan(() => DuplicateDecisionService.SelectDirectory(_view.Cast<DuplicateFileItem>(), path, value));
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "目录路径无效"); }
    }
    private void Plan(Action action)
    { if (IsPlanning) return; IsPlanning = true; try { action(); } finally { IsPlanning = false; SelectionPlanChanged?.Invoke(this, EventArgs.Empty); } }
    internal void SelectFromMenu(IEnumerable<DuplicateFileItem> selected, bool value)
    {
        if (!ProtectionReady || IsPlanning) return;
        var valid = Members.ToHashSet();
        Plan(() =>
        {
            foreach (var file in selected.Where(valid.Contains)) file.IsSelected = value;
            foreach (var group in Members.GroupBy(f => f.GroupNumber))
                if (group.All(f => f.IsSelected)) DuplicateDecisionService.ChooseKeeper(group, KeepPolicy).IsSelected = false;
        });
    }
    internal void KeepFromMenu(DuplicateFileItem keeper)
    {
        if (!ProtectionReady || !Members.Contains(keeper)) return;
        Plan(() => { foreach (var file in Members.Where(f => f.GroupNumber == keeper.GroupNumber)) file.IsSelected = file != keeper && file.IsSelectable; });
    }
    internal void RemoveReferenceFromMenu(string root)
    { ReferencesList.SelectedItem = root; RemoveReference_Click(this, new RoutedEventArgs()); }
    private async void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = new(); var token = _previewCancellation.Token;
        if (FilesGrid.SelectedItem is not DuplicateFileItem file) { PreviewList.ItemsSource = null; return; }
        var group = Members.Where(f => f.GroupNumber == file.GroupNumber).ToArray();
        PreviewLabel.Text = $"第 {file.GroupNumber} 组 · {group.Length} 个独立副本 · 最多预览 8 项";
        try
        {
            var result = await DuplicatePreviewService.LoadGroupAsync(group.Select(f => f.FullPath), token).ConfigureAwait(false);
            if (!token.IsCancellationRequested && !Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) PreviewList.ItemsSource = result; });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => PreviewLabel.Text = "预览失败：" + ex.Message); }
    }
    private void AddReference_Click(object sender, RoutedEventArgs e)
    {
        if (_references is null) return;
        var dialog = new OpenFolderDialog { Title = "选择永不删除的原件目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { _references.Add(dialog.FolderName); RefreshProtection(); ReferenceProtectionChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "无法保存原件保护"); }
    }
    private void RemoveReference_Click(object sender, RoutedEventArgs e)
    {
        if (_references is null || ReferencesList.SelectedItem is not string root) return;
        if (MessageBox.Show(Window.GetWindow(this), "解除此目录的原件保护？\n\n" + root + "\n\n不会删除文件，之后可进入选择计划。", "解除保护", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _references.Remove(root); RefreshProtection(); ReferenceProtectionChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "解除失败"); }
    }
    public async Task RefreshRecordsAsync()
    { if (_log is null) return; try { RecordsGrid.ItemsSource = await _log.ReadRecentAsync(); } catch (Exception ex) { ViewSummary.Text = "记录读取失败：" + ex.Message; } }
    private async void RefreshRecords_Click(object sender, RoutedEventArgs e) => await RefreshRecordsAsync();
    private void ExportRecords_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出重复文件操作记录", Filter = "CSV 文件|*.csv", FileName = "重复文件操作记录.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        static string Cell(string text) => "\"" + (text.StartsWith('=') || text.StartsWith('+') || text.StartsWith('-') || text.StartsWith('@') ? "'" : "") + text.Replace("\"", "\"\"") + "\"";
        try
        {
            var lines = new List<string> { "时间,操作,状态,理论字节,已释放字节,处理路径,保留路径,原因,恢复路径" };
            foreach (var r in RecordsGrid.Items.Cast<DuplicateOperationRecord>()) lines.Add(string.Join(",", new[] { r.LocalTime.ToString("O"), r.Action, r.Status, r.TheoreticalBytes.ToString(), r.ActualBytes.ToString(), r.FilePath, r.KeeperPath, r.Reason, r.BackupPath }.Select(Cell)));
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "导出失败"); }
    }
}
