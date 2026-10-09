using System.Windows;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool;

public partial class MainWindow
{
    private readonly SoftwareAnalysisService _softwareService = new();
    private Task? _softwareRun;
    private void ConfigureSoftware()
    {
        SoftwareCenterView.ScanRequested += SoftwareScanRequested;
        SoftwareCenterView.UninstallRequested += SoftwareUninstallRequested;
        SoftwareCenterView.RestoreRequested += SoftwareRestoreRequested;
        SoftwareCenterView.RecordsRequested += (_, _) => RefreshSoftwareRecords();
    }
    private async void SoftwareScanRequested(object? sender, EventArgs e)
    {
        if (_isBusy) return;
        var scopes = ParseScanScopeWhitelist(); if (scopes is null) return;
        IReadOnlyList<string> exclusions; try { exclusions = _exclusionService.GetPaths().ToArray(); } catch (Exception ex) { MessageBox.Show(this, "保护规则无法读取：" + ex.Message); return; }
        var options = new SoftwareOptions(SoftwareCenterView.IncludeStore, SoftwareCenterView.IncludeGames, SoftwareCenterView.FindResidues, SoftwareCenterView.MeasureDirectories, scopes, exclusions);
        BeginOperation("正在只读分析软件占用与疑似残留…");
        try { var run = _softwareService.AnalyzeAsync(options, _operationCancellation!.Token, new Progress<string>(text => BusyDetailTextBlock.Text = text)); _softwareRun = run; SoftwareCenterView.SetResult(await run); RefreshSoftwareRecords(); }
        catch (OperationCanceledException) { MessageBox.Show(this, "已取消只读扫描，未卸载软件或删除残留。", "软件占用"); }
        catch (Exception ex) { MessageBox.Show(this, "软件分析失败：\n" + ex.Message, "软件占用"); }
        finally { _softwareRun = null; EndOperation(); }
    }
    private async void SoftwareUninstallRequested(object? sender, SoftwareActionEventArgs e)
    {
        if (_isBusy) return; var selected = SoftwareCenterView.Selected;
        if (selected.Count == 0) { MessageBox.Show(this, "请手动勾选需要卸载的软件。残留和受保护项不能卸载。", "批量卸载"); return; }
        bool dryRun = e.DryRun || IsDryRunEnabled;
        if (!dryRun)
        {
            var names = string.Join("\n", selected.Take(20).Select(i => "• " + i.Name));
            if (MessageBox.Show(this, "将先请求管理员权限建立本批操作的新还原点。失败则停止，不绕过系统保护或24小时限制。\n\n随后逐个运行厂商/Windows卸载程序，可能弹出确认或管理员窗口。Steam会交给客户端并停止队列，Epic仅分析。系统还原不能替代配置、游戏存档和其他个人文件备份；本工具不自动删除残留。\n\n" + names + (selected.Count > 20 ? $"\n另有 {selected.Count - 20} 项" : "") + "\n\n确认已备份并开始卸载？", "确认批量卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        BeginOperation(dryRun ? "正在模拟检查卸载计划（不启动程序）…" : "正在建立还原点并按顺序卸载…");
        try
        {
            var token = _operationCancellation!.Token;
            var progress = new Progress<string>(text => BusyDetailTextBlock.Text = text);
            var run = Task.Run(() => _softwareService.UninstallAsync(selected, dryRun, () => _exclusionService.GetPaths().ToArray(), token, progress)); _softwareRun = run;
            var result = await run; SoftwareCenterView.ShowOutcome(result); RefreshSoftwareRecords();
            await _logService.AppendOperationAsync("软件卸载", result.Records.Count(r => r.Result.StartsWith("登记已移除") || r.Result == "模拟通过"), 0, result.Records.Count(r => r.Result.Contains("失败") || r.Result.Contains("跳过")), dryRun);
            await RefreshHistoryAsync();
            MessageBox.Show(this, "操作结束，请查看‘卸载与还原点记录’。卸载完成后需要重新扫描；停止等待不会强制终止正在运行的卸载程序。", "软件卸载");
        }
        catch (Exception ex) { MessageBox.Show(this, "队列停止：\n" + ex.Message, "软件卸载"); }
        finally { _softwareRun = null; EndOperation(); }
    }
    private async void SoftwareRestoreRequested(object? sender, EventArgs e)
    {
        if (_isBusy) return;
        if (IsDryRunEnabled) { MessageBox.Show(this, "当前是模拟模式，不建立真实还原点。需要创建时请先关闭全局模拟，再单独确认。", "建立还原点"); return; }
        if (MessageBox.Show(this, "将请求管理员权限建立系统还原点，不卸载软件。系统保护关闭、权限不足或频率受限时可能失败；不会更改系统保护配置。是否继续？", "建立还原点", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BeginOperation("正在建立并确认新的系统还原点…");
        try { var run = _softwareService.CreateRestorePointAsync(_operationCancellation!.Token); _softwareRun = run; var point = await run; MessageBox.Show(this, point.Success ? $"已确认新还原点 {point.Sequence}：{point.Description}" : "未建立还原点：" + point.Error, "还原点"); RefreshSoftwareRecords(); }
        catch (Exception ex) { MessageBox.Show(this, "还原点操作未完成：" + ex.Message); }
        finally { _softwareRun = null; EndOperation(); }
    }
    private void RefreshSoftwareRecords()
    { try { SoftwareCenterView.SetRecords(_softwareService.ReadRecords()); } catch (Exception ex) { MessageBox.Show(this, "卸载记录不可读取：" + ex.Message); } }
}
