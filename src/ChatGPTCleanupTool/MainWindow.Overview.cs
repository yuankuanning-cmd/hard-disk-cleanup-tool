using System.Windows;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool;

public partial class MainWindow
{
    private readonly StorageOverviewService _overviewService = new();
    private readonly CancellationTokenSource _overviewCancellation = new();
    private Task<StorageOverview>? _overviewRead;
    private void ScanScopeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ScopeExpander is null || ScanScopeWhitelistTextBox is null) return;
        int count = ParseScanScopeValues(ScanScopeWhitelistTextBox.Text).Count;
        ScopeExpander.Header = count == 0 ? "扫描范围白名单：未启用，使用各页路径（点击设置）" : $"扫描范围白名单已启用：{count} 项（点击展开查看）";
        ScopeExpander.Foreground = (System.Windows.Media.Brush)FindResource(count == 0 ? "MutedBrush" : "WarningBrush");
    }
    private void ConfigureOverview()
    {
        OverviewCenterView.RefreshRequested += async (_, _) => await RefreshOverviewAsync();
        OverviewCenterView.NavigateRequested += async (_, e) =>
        {
            if (_isBusy) return;
            int page = OverviewPageIndex(e.Action); MainTabControl.SelectedIndex = page;
            if (e.Action == "huge") MinimumSizeTextBox.Text = "1024";
            if (e.Action == "analyze" && e.Path is { Length: > 0 } root)
            { AnalysisPathTextBox.Text = root; await AnalyzeSpaceAsync(root); }
        };
        OverviewCenterView.FavoritesChanged += (_, _) =>
        {
            try { _settingsService.Save(CaptureSettings()); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { MessageBox.Show(this, "目录收藏尚未保存：" + ex.Message); }
        };
    }
    internal static int OverviewPageIndex(string action) => action switch { "quick" => 0, "codex" => 7, "duplicates" => 3, "media" => 6, "software" => 8, "nps" => 9, _ => 2 };
    private async Task RefreshOverviewAsync()
    {
        if (_overviewRead is { IsCompleted: false } || _overviewCancellation.IsCancellationRequested) return;
        OverviewCenterView.SetLoading(true);
        try { _overviewRead = _overviewService.ReadAsync(_overviewCancellation.Token); OverviewCenterView.SetSnapshot(await _overviewRead); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OverviewCenterView.SetSnapshot(new([], ["读取磁盘状态失败：" + ex.Message], DateTime.UtcNow)); }
        finally { OverviewCenterView.SetLoading(false); }
    }
    private async void ExportOverviewFavorites()
    {
        if (_isBusy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出目录收藏", Filter = "CSV文件|*.csv", FileName = "常用扫描目录.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _csvReportService.ExportAsync(dialog.FileName, ["目录"], OverviewCenterView.FavoritePaths.Select(p => (IReadOnlyList<string>)[p])); }
        catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message); }
    }
}
