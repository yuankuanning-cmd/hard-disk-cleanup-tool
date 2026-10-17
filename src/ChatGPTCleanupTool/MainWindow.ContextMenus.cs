using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool;

public partial class MainWindow
{
    private void ConfigureContextMenus()
    {
        void Attach(FrameworkElement owner, string page, bool records = false) =>
            ResultContextMenu.Attach(owner, menu => BuildPageMenu(menu, page, records), () => _isBusy);
        Attach(CategoryGrid, "快速清理");
        Attach(DetailGrid, "文件明细");
        Attach(CodexSmartGrid, "Codex 智能建议");
        Attach(DuplicateCenterView.ResultGrid, "重复文件中心");
        Attach((ListBox)DuplicateCenterView.FindName("ReferencesList"), "原件保护");
        Attach((DataGrid)DuplicateCenterView.FindName("RecordsGrid"), "重复文件中心", true);
        Attach(ExternalStorageGrid, "Docker / WSL");
        Attach(HistoryGrid, "清理历史", true);
        Attach(MediaCenterView.ResultGrid, "相似媒体");
        Attach((DataGrid)DevelopmentCenterView.FindName("ResultGrid"), "开发环境");
        Attach((DataGrid)DevelopmentCenterView.FindName("RecordGrid"), "开发环境", true);
        Attach((DataGrid)SoftwareCenterView.FindName("ResultGrid"), "软件占用");
        Attach((DataGrid)SoftwareCenterView.FindName("RecordGrid"), "软件占用", true);
        Attach((DataGrid)NpsCenterView.FindName("ProjectGrid"), "NPS 工程");
        Attach((DataGrid)NpsCenterView.FindName("RecordGrid"), "NPS 工程", true);
        Attach((ListBox)OverviewCenterView.FindName("DriveCards"), "磁盘概览");
        Attach((ListBox)OverviewCenterView.FindName("FavoriteItems"), "目录收藏");
        AnalysisExplorer.MenuBlocked = () => _isBusy;
        AnalysisExplorer.IsMenuPathProtected = _exclusionService.Contains;
        AnalysisExplorer.ExportRequested = () => ExportCurrentResultsButton_Click(this, new RoutedEventArgs());
        foreach (TabItem tab in MainTabControl.Items)
            if (tab.Content is FrameworkElement page) Attach(page, tab.Header.ToString()!);
    }

    private void BuildPageMenu(ResultMenuSession menu, string page, bool records)
    {
        var rows = menu.Rows; var current = menu.Current;
        var paths = rows.Select(r => r.Path).Where(ResultContextMenu.IsFilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        menu.Separator();
        if (!records && paths.Length > 0 && page is not "原件保护" and not "磁盘概览" and not "目录收藏")
        {
            menu.Add("exclude", $"加入永久排除（{paths.Length}项）…", () => AddMenuExclusions(paths), changesState: true,
                hint: "文件按路径保护；目录包含子项。不会删除文件，保护同步到其他清理页。");
            if (page is "重复文件中心" or "相似媒体")
                menu.Add("protect-original", "将所在目录设为原件保护…", () => AddMenuReferences(paths), changesState: true,
                    hint: "保护整个所在目录及子目录；受保护原件不会进入回收/硬链接计划。");
        }
        switch (page)
        {
            case "磁盘概览": case "目录收藏":
                if (current?.Path is { Length: > 0 } overviewPath)
                {
                    menu.Add("analyze-overview", "分析此磁盘 / 目录的空间", async () => { if (_isBusy) return; MainTabControl.SelectedIndex = 2; AnalysisPathTextBox.Text = overviewPath; await AnalyzeSpaceAsync(overviewPath); }, changesState: true);
                    if (page == "目录收藏") menu.Add("remove-bookmark", "移除目录收藏（不删除文件）", () => OverviewCenterView.RemoveFavorite(overviewPath), changesState: true);
                    else menu.Add("pin-overview", "收藏为常用扫描目录", () => OverviewCenterView.AddFavorite(overviewPath), changesState: true);
                }
                break;
            case "快速清理":
                PlanEntries<CleanupCategoryItem>(menu, c => c.FileCount > 0, (c, selected) => c.IsSelected = selected, "清理类别");
                menu.Add("process-plan", "处理已勾选类别…", () => CleanupButton_Click(this, new()), changesState: true,
                    available: () => CleanupButton.IsEnabled, hint: "包含此前勾选的类别，不只当前高亮行；沿用原有预览/风险确认与模拟设置。");
                if (current?.Item is CleanupCategoryItem category)
                {
                    menu.Add("show-files", "查看此类别文件明细", () => { CategoryGrid.SelectedItem = category; DetailGrid.Focus(); });
                    if (category.Files.FirstOrDefault()?.Root is { Length: > 0 } rootPath)
                        menu.Add("open-example-root", "打开此类别示例根目录", () => ResultContextMenu.OpenLocation(rootPath), ResultContextMenu.IsFilePath(rootPath));
                }
                break;
            case "文件明细":
                if (current?.Item is CleanupFileItem file)
                    menu.Add("find-category", "定位所属清理类别", () => CategoryGrid.SelectedItem = Categories.FirstOrDefault(c => c.Id == file.CategoryId));
                // Never add a new direct file deletion route to the generic cleanup engine.
                break;
            case "Codex 智能建议":
                PlanEntries<CodexSmartFileItem>(menu, f => f.IsSafeToDelete && !f.IsExcluded, (f, selected) => f.IsSelected = selected, "可操作文件");
                menu.Add("development-center", "到开发环境核实任务归属", () => MainTabControl.SelectedIndex = 7);
                menu.Add("process-plan", "处理已勾选文件…", () => CodexSmartDeleteButton_Click(this, new()), changesState: true,
                    available: () => CodexSmartDeleteButton.IsEnabled, hint: "未知任务产物不能凭年龄/分数删除。沿用原有校验、确认和模拟设置。");
                break;
            case "重复文件中心" when !records:
                menu.Add("check-rows", "勾选所选可操作副本", () => DuplicateCenterView.SelectFromMenu(rows.Select(r => r.Item).OfType<DuplicateFileItem>(), true),
                    rows.Any(r => r.Item is DuplicateFileItem { IsSelectable: true }), true, "保护原件/排除项，组内至少保留一份。",
                    () => DuplicateCenterView.ProtectionReady && !_duplicateResultsStale && !DuplicateCenterView.IsPlanning);
                menu.Add("uncheck-rows", "取消所选副本的勾选", () => DuplicateCenterView.SelectFromMenu(rows.Select(r => r.Item).OfType<DuplicateFileItem>(), false),
                    rows.Count > 0, true, available: () => !DuplicateCenterView.IsPlanning);
                if (current?.Item is DuplicateFileItem duplicate)
                    menu.Add("keep-this", "保留当前副本，勾选同组其他副本", () => DuplicateCenterView.KeepFromMenu(duplicate), changesState: true,
                        available: () => DuplicateCenterView.ProtectionReady && !_duplicateResultsStale && !DuplicateCenterView.IsPlanning);
                menu.Add("recycle-plan", "将已勾选副本移入回收站…", () => DeleteDuplicatesButton_Click(this, new()), changesState: true,
                    available: () => DeleteDuplicatesButton.IsEnabled, hint: "包含所有已勾选副本；执行前完整重哈希，仍有确认，不直接删除右键行。");
                menu.Add("hardlink-plan", "已勾选副本替换为硬链接…", () => HardLinkDuplicatesButton_Click(this, new()), changesState: true,
                    available: () => HardLinkDuplicatesButton.IsEnabled, hint: "同卷NTFS限定，原件保护、完整哈希和两次确认不变。");
                break;
            case "原件保护":
                if (current?.Item is string root)
                    menu.Add("remove-reference", "解除当前目录的原件保护…", () => DuplicateCenterView.RemoveReferenceFromMenu(root), changesState: true,
                        hint: "需要再次确认。不会删除文件；解除后可进入处理计划。");
                break;
            case "相似媒体":
                if (current?.Item is MediaResultItem media)
                {
                    menu.Add("preview-media", "查看当前项与同组图片对比", () => { MediaCenterView.ResultGrid.SelectedItem = media; MediaCenterView.ResultGrid.Focus(); });
                    menu.Add("media-group", "仅查看当前相似组", () => { ResultSearchTextBox.Clear(); MediaCenterView.SetContextGroup(media.Group); }, media.Group > 0);
                }
                menu.Add("clear-media-group", "恢复所有相似组", () => MediaCenterView.SetContextGroup(null));
                menu.Add("reference-settings", "原件目录保护设置", () => MainTabControl.SelectedIndex = 3);
                break;
            case "开发环境" when !records:
                PlanEntries<DevelopmentItem>(menu, d => d.CanRecycle || d.CanRemoveWorktree || d.CanManuallyRecycle, (d, selected) => d.IsSelected = selected, "有操作资格的项目");
                menu.Add("simulate-plan", "模拟检查已勾选项目", () => DevelopmentActionRequested(this, new(false, true)), changesState: true,
                    available: () => DevelopmentCenterView.Selected.Count > 0);
                menu.Add("recycle-plan", "回收已勾选可重建目录…", () => DevelopmentActionRequested(this, new(false, false)), changesState: true,
                    available: () => DevelopmentCenterView.Selected.Any(d => d.CanRecycle));
                menu.Add("git-plan", "由 Git 移除已勾选工作树…", () => DevelopmentActionRequested(this, new(true, false)), changesState: true,
                    available: () => DevelopmentCenterView.Selected.Any(d => d.CanRemoveWorktree), hint: "不进入回收站；保留分支，沿用Git/任务引用/归档检查和确认。");
                menu.Add("archive-plan", "已备份的归档成果人工回收…", () => DevelopmentActionRequested(this, new(false, false, true)), changesState: true,
                    available: () => DevelopmentCenterView.Selected.Any(d => d.CanManuallyRecycle), hint: "仍需输入备份确认并核对任务归属，不能凭旧或归档就删除。");
                break;
            case "软件占用" when !records:
                PlanEntries<SoftwareItem>(menu, s => s.CanUninstall, (s, selected) => s.IsSelected = selected, "可卸载软件");
                menu.Add("simulate-plan", "模拟检查已勾选软件", () => SoftwareUninstallRequested(this, new(true)), changesState: true,
                    available: () => SoftwareCenterView.Selected.Any(s => s.CanUninstall));
                menu.Add("uninstall-plan", "建立还原点并卸载已勾选软件…", () => SoftwareUninstallRequested(this, new(false)), changesState: true,
                    available: () => SoftwareCenterView.Selected.Any(s => s.CanUninstall), hint: "包含所有已勾选软件；仍需用户确认，新还原点失败则停止。残留从不删除。");
                menu.Add("windows-apps", "Windows 已安装应用", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }));
                break;
            case "Docker / WSL":
                menu.Add("docker-prune", "安全清理 Docker 缓存（全局）…", () => DockerPruneButton_Click(this, new()), changesState: true,
                    available: () => _dockerRunning && DockerPruneButton.IsEnabled, hint: "不是删除右键项或WSL虚拟盘；仍显示原有全局Docker清理确认。");
                break;
            case "NPS 工程" when !records:
                if (rows.Count > 0)
                {
                    menu.Add("check-rows", "勾选所选完整工程", () => NpsCenterView.SetHighlightedSelection(true), rows.Any(r => r.Item is NpsProjectItem { Cleanup.CanRecycle: true }), true, allowLargeSelection: true);
                    menu.Add("uncheck-rows", "取消所选行勾选", () => NpsCenterView.SetHighlightedSelection(false), changesState: true, allowLargeSelection: true);
                }
                menu.Add("select-all-nps", "全选当前列表", NpsCenterView.SelectVisible, changesState: true, allowLargeSelection: true);
                menu.Add("clear-nps", "取消全部勾选", NpsCenterView.ClearSelection, changesState: true, allowLargeSelection: true);
                menu.Add("simulate-nps", "模拟检查已勾选工程", () => NpsRecycleRequested(this, new(true)), changesState: true, available: () => NpsCenterView.SelectedPlan.Count > 0);
                menu.Add("recycle-nps", "删除已勾选项到回收站…", () => NpsRecycleRequested(this, new(false)), changesState: true,
                    hint: "一次确认；回收整个工程文件夹，包括模型、脚本、结果和其他内容。孤立模型仅作线索。", available: () => NpsCenterView.SelectedPlan.Count > 0, allowLargeSelection: true);
                break;
        }
        if (records)
        {
            if (current?.Item is DuplicateOperationRecord { KeeperPath.Length: > 0 } duplicateRecord)
                menu.Add("open-keeper", "打开记录中保留副本的位置", () => ResultContextMenu.OpenLocation(duplicateRecord.KeeperPath));
            if (current?.Item is DuplicateOperationRecord { BackupPath.Length: > 0 } recoveryRecord)
                menu.Add("open-recovery", "定位异常恢复文件（不要删除）", () => ResultContextMenu.OpenLocation(recoveryRecord.BackupPath));
        }
        menu.Separator();
        if (page == "清理历史")
            menu.Add("open-log", "打开原始历史日志", OpenRawLog);
        menu.Add("refresh", records ? "刷新操作记录" : "重新扫描本页", () => RefreshFromMenu(page, records), changesState: true, allowLargeSelection: true);
        if (page is not "原件保护" and not "文件明细")
            menu.Add("export", records ? "导出本页操作记录…" : "导出本页结果…", () => { if (page == "目录收藏") ExportOverviewFavorites(); else if (records) ExportMenuRecords(menu.Owner); else ExportCurrentResultsButton_Click(this, new()); }, changesState: true,
                hint: "使用CSV导出，包含本页结果；不是仅导出高亮行。", allowLargeSelection: true);
    }
    private void PlanEntries<T>(ResultMenuSession menu, Func<T, bool> allowed, Action<T, bool> set, string label)
    {
        var items = menu.Rows.Select(r => r.Item).OfType<T>().ToArray();
        if (items.Length == 0) return;
        menu.Add("check-rows", "勾选所选" + label, () => { foreach (var item in items.Where(allowed)) set(item, true); }, items.Any(allowed), true,
            "只更改所选行的勾选状态，不执行清理；其他已勾选项保持不变。");
        menu.Add("uncheck-rows", "取消所选行的勾选", () => { foreach (var item in items) set(item, false); }, changesState: true);
    }
    private void RefreshFromMenu(string page, bool records)
    {
        switch (page)
        {
            case "快速清理": case "文件明细": ScanButton_Click(this, new()); break;
            case "Codex 智能建议": CodexSmartScanButton_Click(this, new()); break;
            case "空间分析": AnalyzeButton_Click(this, new()); break;
            case "重复文件中心": if (records) _ = DuplicateCenterView.RefreshRecordsAsync(); else DuplicateScanButton_Click(this, new()); break;
            case "原件保护": DuplicateCenterView.RefreshProtection(); break;
            case "Docker / WSL": ExternalAnalyzeButton_Click(this, new()); break;
            case "清理历史": RefreshHistoryButton_Click(this, new()); break;
            case "相似媒体": MediaScanRequested(this, EventArgs.Empty); break;
            case "开发环境": if (records) RefreshDevelopmentRecords(); else DevelopmentScanRequested(this, EventArgs.Empty); break;
            case "软件占用": if (records) RefreshSoftwareRecords(); else SoftwareScanRequested(this, EventArgs.Empty); break;
            case "NPS 工程": if (records) RefreshNpsRecords(); else NpsScanRequested(this, EventArgs.Empty); break;
            case "磁盘概览": case "目录收藏": _ = RefreshOverviewAsync(); break;
        }
    }
    private void AddMenuExclusions(IReadOnlyList<string> paths)
    {
        if (_isBusy) return;
        if (MessageBox.Show(this, "将以下项目加入永久排除；目录包含所有子项。不会删除文件。\n\n" + string.Join("\n", paths.Take(12)) +
            (paths.Count > 12 ? $"\n另有{paths.Count - 12}项" : ""), "加入永久排除", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _exclusionService.SetExcludedPaths(paths, true);
        RefreshReferenceProtection(showError: true);
        foreach (var item in DevelopmentCenterView.Result?.Items ?? [])
            if (ResultContextMenu.IsFilePath(item.FullPath) && paths.Any(p => DevelopmentCodexCatalog.Overlap(item.FullPath, p)))
            { item.IsSelected = false; item.Safety = DevelopmentSafety.Protected; item.CanRecycle = false; item.CanRemoveWorktree = false; item.CanManuallyRecycle = false; }
        ((DataGrid)DevelopmentCenterView.FindName("ResultGrid")).Items.Refresh();
        foreach (var item in (NpsCenterView.Result?.Projects ?? []).Select(p => p.Cleanup).OfType<NpsCleanupItem>())
            if (paths.Any(p => DevelopmentCodexCatalog.Overlap(item.FullPath, p)))
            { item.CanRecycle = false; item.IsSelected = false; item.Evidence = "已加入永久排除，不可删除。"; }
        NpsCenterView.RefreshRows();
        foreach (var item in SoftwareCenterView.Result?.Items ?? [])
            if (item.Registration.InstallPath.Length > 0 && _exclusionService.Contains(item.Registration.InstallPath)) { item.IsSelected = false; item.CanUninstall = false; item.Status = "参考/排除目录保护"; }
        ((DataGrid)SoftwareCenterView.FindName("ResultGrid")).Items.Refresh();
    }
    private void AddMenuReferences(IReadOnlyList<string> paths)
    {
        var roots = paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)).Where(p => p is not null && Directory.Exists(p)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) throw new IOException("所在目录不存在。");
        if (MessageBox.Show(this, "保护以下整个目录及子项，不仅是右键文件。不会删除文件。\n\n" + string.Join("\n", roots.Take(12)), "设为原件保护", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var root in roots) _duplicateReferences.Add(root);
        DuplicateCenterView.RefreshProtection(); RefreshReferenceProtection(showError: true);
        MediaCenterView.UpdateReferenceProtection(_duplicateReferences.GetPaths());
    }
    private async void ExportMenuRecords(FrameworkElement owner)
    {
        if (_isBusy || owner is not DataGrid grid) return;
        var rows = grid.Items.Cast<object>().Take(10000).Select(MenuTarget.From).ToArray();
        if (rows.Length == 0) { MessageBox.Show(this, "没有可导出的操作记录。"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出所显示操作记录", Filter = "CSV文件|*.csv", FileName = $"操作记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Safe(string value) => value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') ? "'" + value : value;
        try { await _csvReportService.ExportAsync(dialog.FileName, ["名称", "路径", "记录详情"], rows.Select(r => (IReadOnlyList<string>)[Safe(r.Name), Safe(r.Path), Safe(r.Details)])); }
        catch (Exception ex) { MessageBox.Show(this, "记录导出失败：" + ex.Message); }
    }
}
