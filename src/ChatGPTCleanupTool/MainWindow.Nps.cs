using System.IO;
using System.Windows;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool;

public partial class MainWindow
{
    private readonly NpsProjectService _npsService = new();
    private Task? _npsRun;
    private void ConfigureNps()
    {
        NpsCenterView.RootPath = Directory.Exists(@"D:\AAA_work\model\SimuNPS-EMT") ? @"D:\AAA_work\model\SimuNPS-EMT" : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        NpsCenterView.ScanRequested += NpsScanRequested;
        NpsCenterView.RecycleRequested += NpsRecycleRequested;
        NpsCenterView.RecordsRequested += (_, _) => RefreshNpsRecords();
        NpsCenterView.PreviousRequested += NpsPreviousRequested;
    }
    private async Task<NpsScanResult> PrepareNpsAsync(NpsScanResult result, CancellationToken token, IProgress<string> progress)
    {
        await _npsService.MeasureAsync(result.Projects, _exclusionService.GetPaths().ToArray(), token, progress, replaceAll: true);
        if (token.IsCancellationRequested) result = result with { Complete = false, Warnings = result.Warnings.Concat(["大小统计已取消，保留已完成大小；未统计项不可勾选，重新扫描即可。"]).ToArray() };
        _npsService.Save(result);
        return result;
    }
    private async Task<NpsScanResult> ScanNpsAsync(IReadOnlyList<string> roots, CancellationToken token, IProgress<string> progress)
    {
        var result = await _npsService.DiscoverAsync(new(roots), token, progress);
        return await PrepareNpsAsync(result, token, progress);
    }
    private async void NpsScanRequested(object? sender, EventArgs e)
    {
        if (_isBusy) return;
        try
        {
            IReadOnlyList<string> roots;
            if (NpsCenterView.AllDisks) roots = NpsProjectService.DiskRoots(NpsCenterView.IncludeRemovable);
            else { var whitelist = ParseScanScopeWhitelist(); if (whitelist is null) return; roots = whitelist.Count > 0 ? whitelist : ParseScanScopeValues(NpsCenterView.RootPath); }
            BeginOperation("正在扫描NPS工程并自动统计大小…");
            NpsCenterView.SetPlan([]);
            var run = ScanNpsAsync(roots, _operationCancellation!.Token, new Progress<string>(text => BusyDetailTextBlock.Text = text)); _npsRun = run;
            NpsCenterView.SetResult(await run); RefreshNpsRecords();
        }
        catch (Exception ex) { MessageBox.Show(this, "NPS扫描未完成：" + ex.Message); }
        finally { _npsRun = null; if (_isBusy) EndOperation(); }
    }
    private async void NpsPreviousRequested(object? sender, EventArgs e)
    {
        if (_isBusy) return;
        try
        {
            if (_npsService.LoadPrevious() is not { } result) { MessageBox.Show(this, "还没有保存的NPS扫描结果。"); return; }
            BeginOperation("正在自动更新上次工程的大小…");
            var run = PrepareNpsAsync(result, _operationCancellation!.Token, new Progress<string>(text => BusyDetailTextBlock.Text = text)); _npsRun = run;
            NpsCenterView.SetResult(await run, true); RefreshNpsRecords();
        }
        catch (Exception ex) { MessageBox.Show(this, "读取上次结果失败：" + ex.Message); }
        finally { _npsRun = null; if (_isBusy) EndOperation(); }
    }
    private async void NpsRecycleRequested(object? sender, NpsActionEventArgs e)
    {
        if (_isBusy) return; var selected = NpsCenterView.SelectedPlan;
        if (selected.Count == 0) { MessageBox.Show(this, "请勾选工程列表左侧的复选框。"); return; }
        bool dryRun = e.DryRun || IsDryRunEnabled;
        if (!dryRun)
        {
            var preview = string.Join("\n", selected.Take(6).Select(p => p.FullPath)) + (selected.Count > 6 ? $"\n…及其他 {selected.Count - 6} 项" : "");
            if (MessageBox.Show(this, $"回收已勾选的 {selected.Count:N0} 个完整工程文件夹？\n合计大小 {CleanupService.FormatBytes(selected.Sum(p => p.LogicalBytes))}。\n\n{preview}\n\n文件夹内的模型、脚本、仿真结果、编译产物和备份等全部内容会一起移入回收站，不是只删除npsdata。请确认需要的内容已备份，并关闭NPS。", "确认回收整个工程", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        BeginOperation(dryRun ? "模拟检查勾选项…" : "正在删除勾选项到回收站…");
        try
        {
            var run = _npsService.RecycleAsync(selected, !dryRun, dryRun, () => _exclusionService.GetPaths().ToArray(), _operationCancellation!.Token,
                new Progress<string>(text => BusyDetailTextBlock.Text = text)); _npsRun = run;
            var outcome = await run; RefreshNpsRecords();
            int success = outcome.Records.Count(r => r.Result is "已回收" or "模拟通过"), failed = outcome.Records.Count(r => r.Result.Contains("失败"));
            await _logService.AppendOperationAsync("NPS工程/模型回收", success, 0, failed, dryRun);
            await RefreshHistoryAsync();
            NpsCenterView.ClearSelection(); NpsCenterView.RefreshRows();
            MessageBox.Show(this, $"{(dryRun ? "模拟通过" : "已移入回收站")} {success:N0} 项，失败/跳过 {failed:N0} 项。" + (failed > 0 ? "\n失败原因请查看回收记录。" : "") + "\n回收站清空后才释放磁盘空间。", "NPS工程清理");
        }
        catch (Exception ex) { MessageBox.Show(this, "删除停止：" + ex.Message); }
        finally { _npsRun = null; EndOperation(); }
    }
    private void RefreshNpsRecords()
    { try { NpsCenterView.SetRecords(_npsService.Records()); } catch (Exception ex) { MessageBox.Show(this, "工程回收记录读取失败：" + ex.Message); } }
}
