using System.Windows;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool;

public partial class MainWindow
{
    private readonly SpaceRecycleService _spaceRecycleService = new();
    private bool _synchronizingSpaceExclusions;

    private async void SpaceActionRequested(DiskTreeNode node, SpaceAction action)
    {
        if (_isBusy) return;
        try
        {
            switch (action)
            {
                case SpaceAction.OpenLocation:
                    RevealPath(node.FullPath);
                    break;
                case SpaceAction.Exclude:
                    _exclusionService.SetExcluded(node.FullPath, true);
                    ProtectExistingCandidates();
                    AnalysisExplorer.SetExclusions(_exclusionService.GetPaths());
                    AnalysisSummaryTextBlock.Text = "已永久排除此项目及其子项；重新扫描后会从空间统计中扣除。可在“排除管理”恢复。";
                    break;
                case SpaceAction.Rescan:
                    var directory = node.IsDirectory ? node.FullPath : node.Parent!.FullPath;
                    if (_exclusionService.Contains(directory))
                    {
                        MessageBox.Show(this, "此目录已排除。请先在“排除管理”移除排除项。", "目录受保护");
                        return;
                    }
                    await AnalyzeSpaceAsync(directory);
                    break;
                case SpaceAction.Recycle:
                    await RecycleSpaceNodeAsync(node);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "空间分析操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveSpaceExclusion(string path)
    {
        if (_isBusy) return;
        try
        {
            if (_duplicateReferences.GetPaths().Contains(path, StringComparer.OrdinalIgnoreCase))
            { MessageBox.Show(this, "这是参考原件目录，请在“重复文件中心 → 原件目录保护”解除。", "原件保护"); return; }
            _exclusionService.SetExcluded(path, false);
            ProtectExistingCandidates();
            AnalysisExplorer.SetExclusions(_exclusionService.GetPaths());
            AnalysisSummaryTextBlock.Text = "已移除排除项。请重新扫描以更新空间统计和其他清理候选。";
        }
        catch (Exception ex)
        { MessageBox.Show(this, ex.Message, "排除项保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ProtectExistingCandidates()
    {
        _synchronizingSpaceExclusions = true;
        try
        {
            foreach (var file in Categories.SelectMany(category => category.Files))
                file.IsExcluded = _exclusionService.Contains(file.FullPath);
            foreach (var file in CodexSmartFiles)
            {
                file.IsExcluded = _exclusionService.Contains(file.FullPath);
                if (file.IsExcluded) file.IsSelected = false;
            }
            DuplicateCenterView.UpdateExclusions(_exclusionService.GetPaths().ToArray());
        }
        finally { _synchronizingSpaceExclusions = false; }
        UpdateSelectedSummary(); UpdateCodexSmartSelection(); UpdateDuplicateSelection();
    }

    private bool RefreshReferenceProtection(bool showError = false)
    {
        try
        {
            _exclusionService.SetReferenceRoots(_duplicateReferences.GetPaths());
            ProtectExistingCandidates(); AnalysisExplorer.SetExclusions(_exclusionService.GetPaths()); return true;
        }
        catch (Exception ex)
        {
            _exclusionService.SetReferenceReadFailed();
            if (showError) MessageBox.Show(this, "原件配置无法读取，清理已禁用：" + ex.Message, "保护配置异常");
            return false;
        }
    }

    private async Task RecycleSpaceNodeAsync(DiskTreeNode node)
    {
        if (!RefreshReferenceProtection(showError: true)) return;
        var dryRun = IsDryRunEnabled;
        var message = $"{node.FullPath}\n\n包含 {node.FileCount:N0} 个文件，逻辑大小 {node.LogicalSize}，" +
                      $"{(AnalysisExplorer.Result?.IsApproximate == true ? "近似占用" : "实际占用")} {node.AllocatedSize}。\n\n" +
                      "这只是空间分析结果，并不代表内容没有用途。扫描后的变化、排除项和系统关键路径会再次校验。\n" +
                      "移入回收站不会立即释放磁盘空间；需要您之后手动清空回收站。\n\n" +
                      (dryRun ? "当前为模拟模式，只校验，不删除。继续吗？" : "确认将此项目移入回收站？");
        if (MessageBox.Show(this, message, dryRun ? "模拟回收站操作" : "确认回收站操作", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (node.IsDirectory && !dryRun && MessageBox.Show(this,
                "这是整个目录的操作，包含其所有子文件和空目录。请确认您已检查内容。\n\n仍然继续吗？",
                "再次确认整个目录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        BeginOperation(dryRun ? "正在模拟并校验回收站操作…" : "正在校验完整目录并移入回收站…");
        var completed = false;
        try
        {
            await _spaceRecycleService.RecycleAsync(node, _exclusionService.GetPaths().ToArray(), dryRun, _operationCancellation!.Token);
            completed = true;
            if (!dryRun && AnalysisExplorer.Result is { } result)
            {
                var parent = node.Parent!;
                if (node.Source is { } source) source.Invalidate();
                else
                {
                    parent.Remove(node);
                    for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent) ancestor.Finish();
                }
                var updated = result with
                {
                    ScannedLogicalBytes = result.Roots.Sum(n => n.LogicalBytes),
                    ScannedAllocatedBytes = result.Roots.Sum(n => n.AllocatedBytes),
                    ScannedFileCount = (int)result.Roots.Sum(n => n.FileCount),
                    LargestFiles = result.LargestFiles.Where(n => !DiskAnalysisService.IsWithin(n.FullPath, node.FullPath)).ToArray(),
                    GrowthDirectories = [],
                    NeedsRescan = true,
                    ComparisonNote = "已执行回收站操作；请重新扫描，更新增长对比。",
                    Extensions = node.Source is null ? SubtractExtensions(result.Extensions, node) : result.Extensions
                };
                AnalysisExplorer.SetResult(updated, _exclusionService.GetPaths());
                AnalysisExplorer.Navigate(parent);
                AnalysisItems.ReplaceAll(result.Items.Where(n => !DiskAnalysisService.IsWithin(n.FullPath, node.FullPath)));
            }
            try
            {
                // Recycle does not yet reclaim bytes on the volume. Do not log it as reclaimed space.
                await _logService.AppendOperationAsync(dryRun ? "空间树模拟回收" : "空间树移入回收站（尚未释放空间）",
                    (int)Math.Min(node.FileCount, int.MaxValue), 0, 0, dryRun);
                await RefreshHistoryAsync();
            }
            catch (Exception ex)
            { AnalysisSummaryTextBlock.Text = "回收站操作已完成，但历史保存失败：" + ex.Message; }
            AnalysisSummaryTextBlock.Text = dryRun ? "模拟校验通过，文件未修改。" : "已移入回收站；缓存索引已过期并停止清理，请重新扫描确认占用。清空回收站后才会真正释放空间。";
        }
        catch (OperationCanceledException)
        { AnalysisSummaryTextBlock.Text = "回收站操作已取消；请重新扫描确认当前状态。"; }
        catch (Exception ex)
        {
            MessageBox.Show(this, completed ? "操作已完成，但界面更新失败，请重新扫描：\n" + ex.Message :
                "未能完成操作，请重新扫描确认当前状态：\n" + ex.Message, "回收站操作", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { EndOperation(); }
    }

    private static IReadOnlyList<DiskExtensionSummary> SubtractExtensions(IReadOnlyList<DiskExtensionSummary> totals, DiskTreeNode removed)
    {
        var removedTotals = removed.DescendantsAndSelf().Where(n => !n.IsDirectory)
            .GroupBy(n => n.Extension).ToDictionary(g => g.Key,
                g => (Logical: g.Sum(n => n.LogicalBytes), Allocated: g.Sum(n => n.AllocatedBytes), Count: g.Sum(n => n.FileCount)));
        return totals.Select(n => removedTotals.TryGetValue(n.Extension, out var delta)
                ? n with { LogicalBytes = n.LogicalBytes - delta.Logical, AllocatedBytes = n.AllocatedBytes - delta.Allocated,
                    FileCount = n.FileCount - delta.Count } : n)
            .Where(n => n.FileCount > 0).OrderByDescending(n => n.AllocatedBytes).ToArray();
    }
}
