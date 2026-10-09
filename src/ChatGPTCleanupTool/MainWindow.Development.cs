using System.IO;
using System.Windows;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool;

public partial class MainWindow
{
    private readonly DevelopmentCleanupService _developmentService = new();
    private Task? _developmentRun;
    private void ConfigureDevelopment()
    {
        DevelopmentCenterView.RootPath = Directory.Exists(@"D:\GitHub") ? @"D:\GitHub" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DevelopmentCenterView.CodexHome = _cleanupService.CodexRoot;
        DevelopmentCenterView.ScanRequested += DevelopmentScanRequested;
        DevelopmentCenterView.ActionRequested += DevelopmentActionRequested;
        DevelopmentCenterView.RecordsRequested += (_, _) => RefreshDevelopmentRecords();
        DevelopmentCenterView.ExcludeRequested += (_, item) =>
        {
            try { if (item.Kind != DevelopmentKind.Docker) { _exclusionService.SetExcluded(item.FullPath, true); RefreshReferenceProtection(); MessageBox.Show(this, "已加入清理排除；后续操作会重新读取规则。", "开发环境"); } }
            catch (Exception ex) { MessageBox.Show(this, "排除设置失败：" + ex.Message); }
        };
    }
    private async void DevelopmentScanRequested(object? sender, EventArgs e)
    {
        if (_isBusy) return;
        var scopes = ParseScanScopeWhitelist(); if (scopes is null) return;
        IReadOnlyList<string> exclusions;
        try { exclusions = _exclusionService.GetPaths().ToArray(); }
        catch (Exception ex) { MessageBox.Show(this, "保护规则不可读取，停止扫描：" + ex.Message); return; }
        IReadOnlyList<string> roots = scopes.Count > 0 ? scopes : string.IsNullOrWhiteSpace(DevelopmentCenterView.RootPath) ? [] : new[] { DevelopmentCenterView.RootPath.Trim() };
        var options = new DevelopmentOptions(roots, DevelopmentCenterView.CodexHome.Trim(), DevelopmentCenterView.IncludeCodex,
            DevelopmentCenterView.IncludeCaches, DevelopmentCenterView.IncludeDocker, DevelopmentCenterView.CompareBuilds, DevelopmentCenterView.MergeTarget, exclusions);
        BeginOperation("正在分析开发环境；只读检查任务归属和可恢复性…");
        try
        {
            var progress = new Progress<string>(text => BusyDetailTextBlock.Text = text);
            var run = _developmentService.AnalyzeAsync(options, _operationCancellation!.Token, progress); _developmentRun = run;
            DevelopmentCenterView.SetResult(await run); RefreshDevelopmentRecords();
        }
        catch (OperationCanceledException) { MessageBox.Show(this, "已取消分析，未修改文件；清理前请重新扫描。", "开发环境"); }
        catch (Exception ex) { MessageBox.Show(this, "开发环境分析失败：\n" + ex.Message, "开发环境", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _developmentRun = null; EndOperation(); }
    }
    private async void DevelopmentActionRequested(object? sender, DevelopmentActionEventArgs e)
    {
        if (_isBusy) return;
        var selected = DevelopmentCenterView.Selected;
        if (selected.Count == 0) { MessageBox.Show(this, "请先勾选分析项；未知或受保护项会被跳过。", "开发环境"); return; }
        bool dryRun = e.DryRun || IsDryRunEnabled;
        if (e.ManualArchive && !dryRun && !ConfirmArchiveBackup()) return;
        if (!dryRun)
        {
            var warning = e.Worktrees ? "将由 Git 移除所选工作树的本地 checkout，不进入回收站。仅处理已合并、干净、无忽略文件、非 Codex 托管且无任务引用的工作树。分支与提交保留，可重新检出。\n\n" : e.ManualArchive ? "这些归档产物不保证能够重新生成。仅接受任务归属和归档状态明确、无活动引用的图片/可视化；回收后旧任务的预览可能失效。\n\n" : "将所选可重建目录移入 Windows 回收站。依赖下载源失效、离线依赖或本地改包时，请保留相关目录。\n\n";
            if (MessageBox.Show(this, warning + $"已选 {selected.Count} 项，执行前会再次检查。是否继续？", "确认开发环境清理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        BeginOperation(dryRun ? "正在模拟并重新验证…" : "正在重新验证并记录操作…");
        try
        {
            var run = _developmentService.ExecuteAsync(selected, e.Worktrees, dryRun, () => _exclusionService.GetPaths().ToArray(), _operationCancellation!.Token, e.ManualArchive); _developmentRun = run;
            var result = await run; DevelopmentCenterView.ShowOutcome(result); RefreshDevelopmentRecords();
            await _logService.AppendOperationAsync("开发环境" + (e.Worktrees ? "工作树移除" : "回收站清理"), result.Succeeded, 0, result.Failed, dryRun);
            await RefreshHistoryAsync();
            if (result.Failed > 0) MessageBox.Show(this, "部分项目已跳过；请在‘操作记录’中查看具体原因。", "开发环境");
        }
        catch (Exception ex) { MessageBox.Show(this, "操作停止：\n" + ex.Message, "开发环境", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _developmentRun = null; EndOperation(); }
    }
    private void RefreshDevelopmentRecords()
    {
        try { DevelopmentCenterView.SetRecords(_developmentService.ReadRecords()); }
        catch (Exception ex) { MessageBox.Show(this, "操作记录不可读取：" + ex.Message, "开发环境"); }
    }
    private bool ConfirmArchiveBackup()
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "归档不等于无用。请先另存需要保留的图片/可视化。\n确认已备份、无需旧任务继续打开后，输入：我已备份", TextWrapping = TextWrapping.Wrap });
        var input = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 14, 0, 14), Height = 32 }; panel.Children.Add(input);
        var dialog = new Window { Owner = this, Title = "归档成果人工确认", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel };
        var accept = new System.Windows.Controls.Button { Content = "已备份，继续检查", IsEnabled = false, Padding = new Thickness(12, 6, 12, 6) }; panel.Children.Add(accept);
        input.TextChanged += (_, _) => accept.IsEnabled = input.Text.Trim() == "我已备份";
        accept.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true;
    }
}
