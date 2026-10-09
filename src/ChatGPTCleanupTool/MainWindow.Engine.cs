using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Win32;

namespace ChatGPTCleanupTool;

public partial class MainWindow
{
    private ScanSessionControl? _spaceSession;
    private Task<DiskAnalysisResult>? _engineRun;
    private bool _engineCloseReady;
    private void DisplayEngineResult(DiskAnalysisResult result)
    {
        AnalysisItems.ReplaceAll(result.Items); AnalysisExplorer.SetResult(result, _exclusionService.GetPaths());
        AnalysisExplorer.SetSearchText(ResultSearchTextBox.Text);
        var stats = result.EngineStatistics;
        AnalysisSummaryTextBlock.Text = $"{result.ScannedFileCount:N0} 个文件 · 逻辑 {CleanupService.FormatBytes(result.ScannedLogicalBytes)} · " +
            $"{(result.IsApproximate ? "近似占用" : "实际占用")} {CleanupService.FormatBytes(result.ScannedAllocatedBytes)}" +
            (stats is null ? "" : $"\n{stats.Method} · {stats.DevicePolicy} · 本次复用 {stats.ReusedFiles:N0} 个 · {stats.Status}") +
            (result.HasCheckpoint ? " · 有恢复检查点" : "") + (result.IsHistoricalSnapshot ? " · 只读快照" : "");
        AnalysisSummaryTextBlock.ToolTip = string.Join(Environment.NewLine, result.Warnings) + Environment.NewLine + result.ComparisonNote +
            (stats is null ? "" : $"\n枚举 {stats.EnumeratedFiles:N0} / 读取占用 {stats.MetadataReads:N0} / 新指纹 {stats.HashedFiles:N0} / 复用指纹 {stats.ReusedHashes:N0} / 移除过期记录 {stats.RemovedEntries:N0}");
    }
    private async void SpacePause_Click(object sender, RoutedEventArgs e)
    {
        var session = _spaceSession; if (session is null) return;
        if (session.IsPaused)
        {
            SpacePauseButton.Content = "暂停"; BusyOverlay.Visibility = Visibility.Visible; MainTabControl.IsEnabled = false;
            session.Resume(); return;
        }
        SpacePauseButton.IsEnabled = false; BusyPauseButton.IsEnabled = false;
        try
        {
            BusyTextBlock.Text = "正在保存暂停检查点…";
            await session.PauseAsync();
            if (_spaceSession != session) return;
            var result = await _diskAnalysisService.ReadPausedAsync();
            DisplayEngineResult(result); MainTabControl.IsEnabled = true; BusyOverlay.Visibility = Visibility.Collapsed;
            SpacePauseButton.Content = "继续扫描";
        }
        catch (Exception ex)
        { session.Resume(); MessageBox.Show(this, ex.Message, "暂停未完成"); }
        finally { SpacePauseButton.IsEnabled = _spaceSession is not null; BusyPauseButton.IsEnabled = true; }
    }
    private void SpaceSnapshots_Click(object sender, RoutedEventArgs e)
    {
        var canBrowse = !_isBusy || _spaceSession?.IsPaused == true;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        Add("保存当前快照…", async () =>
        {
            if (AnalysisExplorer.Result is not { } result) return;
            var dialog = new SaveFileDialog { Title = "保存完整空间索引快照", Filter = "空间快照 (*.sqlite)|*.sqlite", DefaultExt = ".sqlite", AddExtension = true, FileName = "空间快照-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
            if (dialog.ShowDialog(this) == true) { await _diskAnalysisService.SaveSnapshotAsync(result, dialog.FileName); AnalysisSummaryTextBlock.Text = "快照已保存，包含完整目录和文件索引。"; }
        }, canBrowse && AnalysisExplorer.Result?.IndexPath is not null);
        Add("打开保存的快照…（只读）", async () =>
        {
            var dialog = new OpenFileDialog { Title = "打开空间快照", Filter = "空间快照 (*.sqlite)|*.sqlite" };
            if (dialog.ShowDialog(this) == true) DisplayEngineResult(await _diskAnalysisService.OpenSnapshotAsync(dialog.FileName));
        }, !_isBusy);
        Add("直接打开当前范围上次结果（只读）", async () =>
        {
            var scope = ParseScanScopeWhitelist(); if (scope is null) return;
            DisplayEngineResult(await _diskAnalysisService.OpenLastAsync(AnalysisPathTextBox.Text.Trim(), scope, _exclusionService.GetPaths().ToArray(), IsFastModeEnabled));
        }, !_isBusy);
        Add("继续当前范围未完成的扫描", async () => await AnalyzeSpaceAsync(resumeCheckpoint: true), !_isBusy);
        menu.Items.Add(new Separator());
        Add("第三方组件许可", () => { var assembly = typeof(MainWindow).Assembly; using var stream = assembly.GetManifestResourceStream("ChatGPTCleanupTool.THIRD_PARTY_NOTICES.txt"); using var reader = new System.IO.StreamReader(stream!); var text = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) }; new Window { Title = "第三方组件许可", Owner = this, Width = 760, Height = 580, Content = text }.ShowDialog(); return Task.CompletedTask; }, true);
        menu.IsOpen = true;
        void Add(string label, Func<Task> action, bool enabled)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += async (_, _) => { try { await action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message + "\n\n恢复扫描需要保持相同路径、白名单、排除规则和快速/完整模式。", "快照 / 恢复"); } };
            menu.Items.Add(item);
        }
    }
    private async Task ExportSpaceIndexAsync()
    {
        if (AnalysisExplorer.Result is not { } result) return;
        var dialog = new SaveFileDialog { Title = "流式导出完整空间索引", Filter = "CSV 文件 (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, FileName = "空间索引-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog(this) != true) return;
        BeginOperation("正在流式导出索引，不会把全部结果加载到内存…");
        try
        {
            await Task.Run(() => _csvReportService.ExportAsync(dialog.FileName,
                ["类型", "名称", "逻辑字节", "占用字节", "文件数", "状态", "完整路径"],
                result.Roots.SelectMany(r => r.DescendantsAndSelf()).Select(n => (IReadOnlyList<string>)[n.Kind, n.Name, n.LogicalBytes.ToString(), n.AllocatedBytes.ToString(), n.FileCount.ToString(), n.State, n.FullPath]),
                _operationCancellation!.Token));
            AnalysisSummaryTextBlock.Text = "完整空间索引已导出。";
        }
        catch (OperationCanceledException) { AnalysisSummaryTextBlock.Text = "导出已取消，目标文件可能包含部分结果。"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出未完成"); }
        finally { EndOperation(); }
    }
}
