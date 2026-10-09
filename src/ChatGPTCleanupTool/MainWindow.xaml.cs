using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Win32;

namespace ChatGPTCleanupTool;

public partial class MainWindow : Window
{
    private static readonly Regex NonDigitRegex = new(@"\D+", RegexOptions.Compiled);
    private readonly CleanupService _cleanupService = new();
    private readonly CodexSmartCleanupService _codexSmartCleanupService = new();
    private readonly ExclusionService _exclusionService = new();
    private readonly CleanupLogService _logService = new();
    private readonly DiskAnalysisService _diskAnalysisService = new();
    private readonly ReferenceDirectoryService _duplicateReferences = new();
    private readonly DuplicateOperationLogService _duplicateLog = new();
    private readonly DuplicateFileService _duplicateFileService;
    private Task? _duplicateRun;
    private Task? _mediaRun;
    private readonly MediaAnalysisService _mediaService = new();
    private bool _duplicateResultsStale;
    private DataGrid DuplicateGrid => DuplicateCenterView.ResultGrid;
    private readonly ExternalStorageService _externalStorageService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly CsvReportService _csvReportService = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private bool _dockerRunning;
    private bool IsFastModeEnabled => FastModeCheckBox.IsChecked == true;
    private bool IsDryRunEnabled => DryRunCheckBox.IsChecked == true;

    public MainWindow()
    {
        InitializeComponent();
        _duplicateFileService = new(referenceDirectories: _duplicateReferences, operationLog: _duplicateLog);
        DuplicateCenterView.Configure(_duplicateReferences, _duplicateLog);
        DuplicateCenterView.SelectionPlanChanged += (_, _) => UpdateDuplicateSelection();
        DataContext = this;
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        AnalysisPathTextBox.Text = systemDrive;
        DuplicatePathTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        CodexSmartRootTextBox.Text = _cleanupService.CodexRoot;
        MediaCenterView.RootPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        MediaCenterView.ScanRequested += MediaScanRequested;
        MediaCenterView.ReferencesRequested += (_, _) => MainTabControl.SelectedIndex = 3;
        ConfigureDevelopment();
        ConfigureSoftware();
        DuplicateCenterView.ReferenceProtectionChanged += (_, _) =>
        {
            try { MediaCenterView.UpdateReferenceProtection(_duplicateReferences.GetPaths()); }
            catch (Exception) { MediaCenterView.UpdateReferenceProtection(MediaCenterView.Result?.Items.Select(row => row.FullPath).ToArray() ?? []); }
        };
        ApplySettings(_settingsService.Load());
        ConfigureResultFilters();
        AnalysisExplorer.ActionRequested += SpaceActionRequested;
        AnalysisExplorer.RemoveExclusionRequested += RemoveSpaceExclusion;
        DuplicateCenterView.ReferenceProtectionChanged += (_, _) => RefreshReferenceProtection(showError: true);
        RefreshReferenceProtection();
        ConfigureContextMenus();
    }

    public BulkObservableCollection<CleanupCategoryItem> Categories { get; } = [];
    public BulkObservableCollection<CleanupFileItem> DetailFiles { get; } = [];
    public BulkObservableCollection<DiskAnalysisItem> AnalysisItems { get; } = [];
    public BulkObservableCollection<DuplicateFileItem> DuplicateFiles { get; } = [];
    public BulkObservableCollection<ExternalStorageItem> ExternalStorageItems { get; } = [];
    public BulkObservableCollection<CodexSmartFileItem> CodexSmartFiles { get; } = [];
    public BulkObservableCollection<CleanupHistoryItem> HistoryItems { get; } = [];

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshHistoryAsync();
        await DuplicateCenterView.RefreshRecordsAsync();
        await ScanAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_engineCloseReady && _softwareRun is { IsCompleted: false })
        {
            e.Cancel = true; _operationCancellation?.Cancel();
            try { await _softwareRun; } catch { }
            _engineCloseReady = true; Close(); return;
        }
        if (!_engineCloseReady && _developmentRun is { IsCompleted: false })
        {
            e.Cancel = true; _operationCancellation?.Cancel();
            try { await _developmentRun; } catch { }
            _engineCloseReady = true; Close(); return;
        }
        if (!_engineCloseReady && _mediaRun is { IsCompleted: false })
        {
            e.Cancel = true; _operationCancellation?.Cancel();
            try { await _mediaRun; } catch { }
            _engineCloseReady = true; Close(); return;
        }
        if (!_engineCloseReady && _duplicateRun is { IsCompleted: false })
        {
            e.Cancel = true; _operationCancellation?.Cancel();
            try { await _duplicateRun; } catch { }
            _engineCloseReady = true; Close(); return;
        }
        if (!_engineCloseReady && _engineRun is { IsCompleted: false })
        {
            e.Cancel = true;
            _spaceSession?.Resume();
            _operationCancellation?.Cancel();
            try { await _engineRun; } catch { /* Checkpoint transactions already committed remain recoverable. */ }
            _engineCloseReady = true; Close(); return;
        }
        _operationCancellation?.Cancel();
        try
        {
            _settingsService.Save(CaptureSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 退出时设置保存失败不阻止窗口关闭。
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_isBusy || !TryGetScanOptions(out var options))
        {
            return;
        }

        BeginOperation("正在扫描可清理文件…");
        try
        {
            var scanScopeRoots = ParseScanScopeWhitelist();
            if (scanScopeRoots is null)
            {
                return;
            }

            var results = await _cleanupService.ScanAsync(
                options,
                scanScopeRoots,
                _operationCancellation!.Token,
                CreateScanProgress());
            UnsubscribeCurrentItems();

            foreach (var category in results)
            {
                category.PropertyChanged += Category_PropertyChanged;
                foreach (var file in category.Files)
                {
                    file.IsExcluded = _exclusionService.Contains(file.FullPath);
                    file.PropertyChanged += CleanupFile_PropertyChanged;
                }
            }

            Categories.ReplaceAll(results);
            DetailFiles.ReplaceAll([]);

            CategoryGrid.SelectedItem = Categories.FirstOrDefault(
                category => category.FileCount > 0);
            UpdateSelectedSummary();
        }
        catch (OperationCanceledException)
        {
            SummaryTextBlock.Text = "扫描已取消；原扫描结果保持不变。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"扫描失败：\n\n{ex.Message}",
                "扫描失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SummaryTextBlock.Text = "扫描失败";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void CleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !TryGetScanOptions(out var options))
        {
            return;
        }

        if (!RefreshReferenceProtection(showError: true)) return;
        var selectedCategories = Categories
            .Where(category => category.IsSelected)
            .ToList();
        var plan = selectedCategories
            .SelectMany(category => category.Files)
            .Where(file => !file.IsExcluded && !_exclusionService.Contains(file.FullPath))
            .ToArray();
        if (plan.Length == 0)
        {
            return;
        }

        var hasHighRisk = selectedCategories.Any(category =>
            category.Risk == "高" && category.Files.Any(file => !file.IsExcluded));
        var bytes = plan.Sum(file => file.Bytes);
        var categoryBreakdown = string.Join(
            Environment.NewLine,
            selectedCategories.Select(category =>
            {
                var categoryFiles = category.Files.Where(file => !file.IsExcluded).ToArray();
                return $"• {category.Name}（风险：{category.Risk}）：{categoryFiles.Length:N0} 个，" +
                       CleanupService.FormatBytes(categoryFiles.Sum(file => file.Bytes));
            }));
        var warning = hasHighRisk
            ? "已选择会话、项目副本或依赖目录等高风险内容。执行后可能无法恢复。"
            : "将清理扫描结果中的已选文件。执行前会再次核对文件大小和修改时间。";
        var answer = MessageBox.Show(
            this,
            $"{warning}\n\n计划{(IsDryRunEnabled ? "预演" : "执行")}{(IsDryRunEnabled ? "清理" : "删除")} " +
            $"{plan.Length:N0} 个文件，预计释放 " +
            $"{CleanupService.FormatBytes(bytes)}。\n\n{categoryBreakdown}\n\n确定继续吗？",
            IsDryRunEnabled ? "确认模拟清理" : "确认清理",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (hasHighRisk && !IsDryRunEnabled)
        {
            var highRiskCategories = string.Join("、", selectedCategories
                .Where(category => category.Risk == "高")
                .Select(category => category.Name));
            var secondAnswer = MessageBox.Show(
                this,
                $"这是第二次确认。高风险类别“{highRiskCategories}”可能包含仍有价值的会话或构建环境。\n\n" +
                "这些文件将直接删除而不是移入回收站。仍要继续吗？",
                "再次确认高风险清理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop,
                MessageBoxResult.No);
            if (secondAnswer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        BeginOperation(
            $"正在{(IsDryRunEnabled ? "预演" : "执行")}已确认的清理计划…");
        try
        {
            var result = await _cleanupService.CleanupFilesAsync(
                plan,
                options,
                IsDryRunEnabled,
                _operationCancellation!.Token,
                protectedPaths: _exclusionService.GetPaths().ToArray());
            if (!IsDryRunEnabled)
            {
                await _logService.AppendAsync(
                    result,
                    options,
                    _operationCancellation.Token);
            }
            else
            {
                await _logService.AppendOperationAsync(
                    "快速清理",
                    result.DeletedCount,
                    result.DeletedBytes,
                    result.FailureCount,
                    dryRun: true,
                    _operationCancellation.Token);
            }

            var message =
                $"{(IsDryRunEnabled ? "模拟完成：预演将删除" : "清理完成：删除")} {result.DeletedCount:N0} 个文件，释放 {result.DisplaySize}。";
            if (result.FailureCount > 0)
            {
                message +=
                    $"\n\n有 {result.FailureCount:N0} 个文件因正在使用、权限不足或扫描后发生变化而被跳过。";
            }
            if (IsDryRunEnabled)
            {
                message += "\n\n当前为模拟模式，未实际删除任何文件。";
            }

            MessageBox.Show(
                this,
                message,
                IsDryRunEnabled ? "模拟清理完成" : "清理完成",
                MessageBoxButton.OK,
                result.FailureCount == 0
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(
                this,
                "清理已取消。取消前已经完成的文件删除不会回滚。",
                "清理已取消",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"清理失败：\n\n{ex.Message}",
                "清理失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }

        await ScanAsync();
    }

    private void CategoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryGrid.SelectedItem is not CleanupCategoryItem category)
        {
            DetailFiles.ReplaceAll([]);
            DetailTitleTextBlock.Text = "选择一个清理类别查看文件明细";
            return;
        }

        DetailFiles.ReplaceAll(category.Files);

        var excludedCount = category.Files.Count(file => file.IsExcluded);
        DetailTitleTextBlock.Text =
            $"{category.Name}：{category.FileCount:N0} 个文件" +
            (excludedCount > 0 ? $"，其中 {excludedCount:N0} 个已永久排除" : string.Empty);
    }

    private void Category_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCategoryItem.IsSelected))
        {
            UpdateSelectedSummary();
        }
    }

    private void CleanupFile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_synchronizingSpaceExclusions) return;
        if (sender is not CleanupFileItem file ||
            e.PropertyName != nameof(CleanupFileItem.IsExcluded))
        {
            return;
        }

        try
        {
            _exclusionService.SetExcluded(file.FullPath, file.IsExcluded);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"无法保存排除设置：\n\n{ex.Message}",
                "排除设置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        CategoryGrid_SelectionChanged(CategoryGrid, null!);
        UpdateSelectedSummary();
    }

    private void UpdateSelectedSummary()
    {
        var selectedCount = 0;
        var selectedBytes = 0L;
        var excludedCount = 0;

        foreach (var category in Categories)
        {
            foreach (var file in category.Files)
            {
                if (file.IsExcluded)
                {
                    excludedCount++;
                    continue;
                }

                if (category.IsSelected)
                {
                    selectedCount++;
                    selectedBytes += file.Bytes;
                }
            }
        }

        SummaryTextBlock.Text =
            $"已选择 {selectedCount:N0} 个文件，预计释放 " +
            $"{CleanupService.FormatBytes(selectedBytes)}" +
            (excludedCount > 0 ? $"；已保护 {excludedCount:N0} 个文件" : string.Empty);
        CleanupButton.IsEnabled = !_isBusy && selectedCount > 0;
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e) => await AnalyzeSpaceAsync();

    private async Task AnalyzeSpaceAsync(string? scopedDirectory = null, bool resumeCheckpoint = false)
    {
        if (_isBusy)
        {
            return;
        }

        var path = scopedDirectory ?? AnalysisPathTextBox.Text.Trim();
        if (!int.TryParse(MinimumSizeTextBox.Text, out var minimumMegabytes) ||
            minimumMegabytes is < 1 or > 1_048_576)
        {
            MessageBox.Show(
                this,
                "请输入 1 到 1048576 之间的大文件阈值（MB）。",
                "阈值不正确",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var scanModeText = IsFastModeEnabled
            ? "（快速模式：实际占用按逻辑大小近似）"
            : "（完整扫描）";
        var scanScopeRoots = scopedDirectory is null ? ParseScanScopeWhitelist() : [];
        if (scanScopeRoots is null)
        {
            return;
        }

        BeginOperation($"正在分析目录占用和大文件…{scanModeText}");
        _spaceSession = new();
        SpacePauseButton.IsEnabled = true; SpaceStopButton.IsEnabled = true;
        BusyPauseButton.Visibility = Visibility.Visible;
        EngineModeBox.IsEnabled = false; IndexHashCheckBox.IsEnabled = false;
        try
        {
            _engineRun = _diskAnalysisService.AnalyzeAsync(
                path,
                minimumMegabytes * 1024L * 1024L,
                IsFastModeEnabled,
                scanScopeRoots: scanScopeRoots,
                cancellationToken: _operationCancellation!.Token,
                progress: CreateScanProgress(),
                exclusionPaths: _exclusionService.GetPaths().ToArray(),
                engineOptions: new ScanEngineOptions { Scheduling = (ScanScheduling)EngineModeBox.SelectedIndex, ComputeFingerprints = IndexHashCheckBox.IsChecked == true, ResumeCheckpoint = resumeCheckpoint },
                control: _spaceSession);
            var result = await _engineRun;
            DisplayEngineResult(result);
        }
        catch (OperationCanceledException)
        {
            AnalysisSummaryTextBlock.Text = "空间分析已取消。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"空间分析失败：\n\n{ex.Message}",
                "空间分析失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _spaceSession?.Resume(); _spaceSession = null; _engineRun = null;
            SpacePauseButton.IsEnabled = false; SpacePauseButton.Content = "暂停"; SpaceStopButton.IsEnabled = false;
            BusyPauseButton.Visibility = Visibility.Collapsed; BusyPauseButton.IsEnabled = true;
            EngineModeBox.IsEnabled = true; IndexHashCheckBox.IsEnabled = true;
            EndOperation();
        }
    }

    private void BrowseProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = BrowseForFolder(ProjectPathTextBox.Text, "选择开发项目目录");
        if (selected is not null)
        {
            ProjectPathTextBox.Text = selected;
        }
    }

    private void BrowseAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = BrowseForFolder(AnalysisPathTextBox.Text, "选择要分析的磁盘或目录");
        if (selected is not null)
        {
            AnalysisPathTextBox.Text = selected;
        }
    }

    private void BrowseDuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = BrowseForFolder(DuplicatePathTextBox.Text, "选择重复文件扫描目录");
        if (selected is not null) DuplicatePathTextBox.Text = selected;
    }

    private async void DuplicateScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || DuplicateCenterView.IsPlanning) return;
        if (!RefreshReferenceProtection(showError: true)) return;
        if (!int.TryParse(DuplicateMinimumTextBox.Text, out var minimumMegabytes) ||
            minimumMegabytes is < 1 or > 1_048_576)
        {
            MessageBox.Show(this, "请输入 1 到 1048576 之间的最小文件大小（MB）。",
                "阈值不正确", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var duplicateModeText = IsFastModeEnabled
            ? "（快速模式：按大小 + 采样哈希）"
            : "（大小 + 采样哈希 + 完整哈希）";
        var scanScopeRoots = ParseScanScopeWhitelist();
        if (scanScopeRoots is null)
        {
            return;
        }

        BeginOperation($"正在查找重复文件…{duplicateModeText}");
        try
        {
            var result = await _duplicateFileService.AnalyzeAsync(
                DuplicatePathTextBox.Text.Trim(),
                minimumMegabytes * 1024L * 1024L,
                IsFastModeEnabled,
                scanScopeRoots,
                _operationCancellation!.Token,
                CreateScanProgress());
            foreach (var oldItem in DuplicateFiles)
                oldItem.PropertyChanged -= DuplicateFile_PropertyChanged;
            foreach (var file in result.Files)
            {
                file.PropertyChanged += DuplicateFile_PropertyChanged;
            }
            DuplicateFiles.ReplaceAll(result.Files);
            _duplicateResultsStale = false;
            DuplicateCenterView.SetFiles(DuplicateFiles, _exclusionService.GetPaths().ToArray());
            DuplicateSummaryTextBlock.Text =
                $"已扫描 {result.ScannedFileCount:N0} 个文件，{(IsFastModeEnabled ? "发现候选" : "确认")} {result.GroupCount:N0} 组；" +
                $"理论待释放 {CleanupService.FormatBytes(DuplicateDecisionService.PotentialBytes(DuplicateFiles))}" +
                (IsFastModeEnabled ? "；快速模式下同组删除前会重验完整哈希" : "；已完成完整哈希确认") +
                (result.Warnings.Count > 0 ? $"；{result.Warnings.Count:N0} 个项目无法读取" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            DuplicateSummaryTextBlock.Text = "重复文件扫描已取消。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"重复文件扫描失败：\n\n{ex.Message}", "扫描失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private void DuplicateFile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DuplicateFileItem.IsSelected) && !DuplicateCenterView.IsPlanning) UpdateDuplicateSelection();
    }

    private void DuplicateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateDuplicateSelection();

    private void UpdateDuplicateSelection()
    {
        var selectedCount = 0;
        var selectedBytes = 0L;
        foreach (var file in DuplicateFiles)
        {
            if (file.IsSelected)
            {
                selectedCount++;
                selectedBytes += file.TheoreticalReclaimableBytes;
            }
        }

        DeleteDuplicatesButton.IsEnabled = !_isBusy && !DuplicateCenterView.IsPlanning && !_duplicateResultsStale && DuplicateCenterView.ProtectionReady && selectedCount > 0;
        HardLinkDuplicatesButton.IsEnabled = DeleteDuplicatesButton.IsEnabled;
        if (selectedCount > 0)
            DuplicateSummaryTextBlock.Text =
                $"已选 {selectedCount:N0} 项 · 理论待释放 {CleanupService.FormatBytes(selectedBytes)} · 回收站暂未释放" + (_duplicateResultsStale ? " · 请重新扫描" : "");
        else if (DuplicateSummaryTextBlock.Text.StartsWith("已选", StringComparison.Ordinal))
            DuplicateSummaryTextBlock.Text = _duplicateResultsStale ? "旧结果已过期，请重新扫描。" : $"当前 {DuplicateFiles.Select(f => f.GroupNumber).Distinct().Count():N0} 组 · 未选择处理项。";
    }

    private async void DeleteDuplicatesButton_Click(object sender, RoutedEventArgs e) => await ExecuteDuplicateActionAsync(DuplicateAction.Recycle);
    private async void HardLinkDuplicatesButton_Click(object sender, RoutedEventArgs e) => await ExecuteDuplicateActionAsync(DuplicateAction.HardLink);

    private async Task ExecuteDuplicateActionAsync(DuplicateAction action)
    {
        if (_isBusy || DuplicateCenterView.IsPlanning || _duplicateResultsStale || !DuplicateCenterView.ProtectionReady) return;
        var selected = DuplicateFiles.Where(f => f.IsSelected && f.IsSelectable && !_exclusionService.Contains(f.FullPath)).ToArray();
        if (selected.Length == 0) return;
        if (DuplicateFiles.GroupBy(f => f.GroupNumber).Any(g => g.All(f => f.IsSelected)))
        { MessageBox.Show(this, "每个重复组必须至少保留一份。", "请保留副本"); return; }
        var hardLink = action == DuplicateAction.HardLink;
        var description = hardLink ? "替换为同卷 NTFS 硬链接" : "移入回收站";
        if (MessageBox.Show(this, $"将{(IsDryRunEnabled ? "模拟" : "")}{description} {selected.Length:N0} 项。\n\n理论待释放 {CleanupService.FormatBytes(selected.Sum(f => f.TheoreticalReclaimableBytes))}。双方都会重算完整哈希；无法校验则跳过。\n回收站内容在手动清空前仍占空间。\n\n继续？",
            "确认处理重复文件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (hardLink && MessageBox.Show(this, "硬链接不再是独立备份：修改任一路径会影响所有链接，权限、时间和数据共享；应用通过另存/替换方式写入时可能打断链接。\n\n仅支持同一本地 NTFS 固定卷；含额外数据流、加密/占位文件或参考原件会被拒绝。\n\n我理解风险，继续？",
            "硬链接共享风险", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var dryRun = IsDryRunEnabled;
        BeginOperation("正在重验双方完整哈希，并逐项记录…");
        try
        {
            var run = _duplicateFileService.ApplyActionAsync(DuplicateFiles.ToArray(), selected, new(action, dryRun, hardLink), _exclusionService.GetPaths().ToArray(), _operationCancellation!.Token);
            _duplicateRun = run; var result = await run;
            foreach (var file in selected) file.IsSelected = false;
            if (!dryRun && result.Records.Any(r => r.Status == "成功" || r.BackupPath.Length > 0 || r.Status.Contains("操作完成"))) _duplicateResultsStale = true;
            await DuplicateCenterView.RefreshRecordsAsync();
            try { await _logService.AppendOperationAsync("重复文件 " + description, result.DeletedCount, result.ReclaimedAllocatedBytes, result.Failures.Count, dryRun, CancellationToken.None); }
            catch (Exception ex) { MessageBox.Show(this, "总历史写入失败，逐项记录已保存：" + ex.Message, "记录提示"); }
            DuplicateSummaryTextBlock.Text = $"{(dryRun ? "模拟通过" : "成功")} {result.DeletedCount:N0} 项 · 理论 {CleanupService.FormatBytes(result.TheoreticalBytes)} · 已释放 {CleanupService.FormatBytes(result.ReclaimedAllocatedBytes)}" + (_duplicateResultsStale ? " · 请重新扫描" : "");
            MessageBox.Show(this, DuplicateSummaryTextBlock.Text + (result.WasCanceled ? "\n已取消，已完成项不会撤回。" : "") +
                "\n\n" + string.Join("\n", result.VolumeObservations.Select(v => v.Description)) +
                (result.Failures.Count > 0 ? $"\n\n跳过/失败 {result.Failures.Count} 项，原因及恢复路径见“操作记录 / 恢复”。\n" + string.Join("\n", result.Failures.Take(3)) : "") +
                (action == DuplicateAction.Recycle ? "\n\n移入回收站尚未实际释放内容空间；不会自动清空回收站。" : "") +
                (dryRun ? "\n模拟模式未修改文件。" : ""), "重复文件处理完成", MessageBoxButton.OK, result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "处理失败；请查看操作记录并重新扫描。\n\n" + ex.Message, "无法处理", MessageBoxButton.OK, MessageBoxImage.Error); _duplicateResultsStale = true; }
        finally { _duplicateRun = null; EndOperation(); }
    }

    private void OpenSelectedDuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (DuplicateGrid.SelectedItem is DuplicateFileItem file) RevealPath(file.FullPath);
    }

    private async void CodexSmartScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var root = CodexSmartRootTextBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show(
                this,
                "所选 Codex 根目录不存在。",
                "路径不存在",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(CodexSmartMinimumTextBox.Text, out var minimumMegabytes) ||
            minimumMegabytes is < 1 or > 1_048_576)
        {
            MessageBox.Show(
                this,
                "请输入 1 到 1048576 之间的最小文件大小（MB）。",
                "参数不正确",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(CodexSmartAgeTextBox.Text, out var olderThanDays) ||
            olderThanDays is < 1 or > 3650)
        {
            MessageBox.Show(
                this,
                "请输入 1 到 3650 之间的未访问天数。",
                "参数不正确",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var scanScopeRoots = ParseScanScopeWhitelist();
        if (scanScopeRoots is null)
        {
            return;
        }

        BeginOperation(
            $"正在扫描 Codex 智能清理候选…{(IsFastModeEnabled ? "（快速模式）" : "（完整扫描）")}");
        try
        {
            foreach (var old in CodexSmartFiles)
            {
                old.PropertyChanged -= CodexSmartFile_PropertyChanged;
            }

            var result = await _codexSmartCleanupService.AnalyzeAsync(
                root,
                olderThanDays,
                minimumMegabytes * 1024L * 1024L,
                CodexSmartDetectDuplicatesCheckBox.IsChecked == true,
                IsFastModeEnabled,
                scanScopeRoots,
                _operationCancellation!.Token,
                CreateScanProgress());
            foreach (var file in result.Items)
            {
                file.IsExcluded = _exclusionService.Contains(file.FullPath);
                file.PropertyChanged += CodexSmartFile_PropertyChanged;
            }
            CodexSmartFiles.ReplaceAll(result.Items);

            CodexSmartSummaryTextBlock.Text =
                $"扫描 {result.Root}；共见 {result.ScannedFileCount:N0} 个文件，" +
                $"候选可清理 {result.CandidateCount:N0} 个，建议回收 {CleanupService.FormatBytes(result.EstimatedReclaimBytes)}。" +
                (IsFastModeEnabled ? "；快速模式下实际占用为近似值" : string.Empty) +
                (result.Warnings.Count > 0 ? $"；{result.Warnings.Count:N0} 个提示" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            CodexSmartSummaryTextBlock.Text = "Codex 智能建议扫描已取消。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Codex 智能建议扫描失败：\n\n{ex.Message}",
                "扫描失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void CodexSmartDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!RefreshReferenceProtection(showError: true)) return;
        var selected = CodexSmartFiles
            .Where(file => file.IsSelected && !file.IsExcluded && !_exclusionService.Contains(file.FullPath))
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var duplicateFixed = EnsureDuplicateGroupsKeepOneSelected(selected);
        selected = CodexSmartFiles
            .Where(file => file.IsSelected && !file.IsExcluded && !_exclusionService.Contains(file.FullPath))
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(
                this,
                "重复组保留策略生效后没有可删除文件。",
                "未选中可删项",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var safePolicyHint = duplicateFixed > 0
            ? $"\n\n已为重复组保留最新文件，自动取消了 {duplicateFixed:N0} 项选中。"
            : string.Empty;

        var unsafeFiles = selected.Where(file => !file.IsSafeToDelete).ToArray();
        if (unsafeFiles.Length > 0)
        {
            MessageBox.Show(
                this,
                "列表中包含未标记为可安全删除的文件，请先取消勾选后再继续。",
                "存在保留建议",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"将{(IsDryRunEnabled ? "预演" : "移入回收站")} {selected.Length:N0} 个文件，理论可释放 " +
            $"{CleanupService.FormatBytes(selected.Sum(file => file.AllocatedBytes))}。{safePolicyHint}\n\n继续吗？",
            IsDryRunEnabled ? "确认模拟清理" : "确认清理",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        BeginOperation($"正在{(IsDryRunEnabled ? "模拟清理" : "清理")} Codex 候选文件…");
        try
        {
            var result = await _codexSmartCleanupService.DeleteToRecycleBinAsync(
                selected,
                IsDryRunEnabled,
                _operationCancellation!.Token);
            await _logService.AppendOperationAsync(
                "Codex 智能清理",
                result.DeletedCount,
                result.ReclaimedBytes,
                result.Failures.Count,
                IsDryRunEnabled,
                _operationCancellation.Token);

            foreach (var deleted in selected.ToArray())
            {
                if (IsDryRunEnabled)
                {
                    if (deleted.IsSelected)
                    {
                        deleted.IsSelected = false;
                    }

                    continue;
                }

                if (!File.Exists(deleted.FullPath))
                {
                    deleted.PropertyChanged -= CodexSmartFile_PropertyChanged;
                    CodexSmartFiles.Remove(deleted);
                }
            }

            MessageBox.Show(
                this,
                $"{(IsDryRunEnabled ? "模拟完成" : "已移入回收站")} {result.DeletedCount:N0} 个文件，实际占用 {CleanupService.FormatBytes(result.ReclaimedBytes)}。" +
                (IsDryRunEnabled ? "\n\n当前为模拟模式，不会实际删除文件。" : string.Empty) +
                (result.Failures.Count > 0 ? $"\n\n{result.Failures.Count:N0} 个文件被跳过。" : string.Empty),
                IsDryRunEnabled ? "Codex 模拟处理完成" : "处理完成",
                MessageBoxButton.OK,
                result.Failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(
                this,
                "已取消删除，已操作文件不会回滚。",
                "已取消",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"清理失败：\n\n{ex.Message}",
                "处理失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private int EnsureDuplicateGroupsKeepOneSelected(IReadOnlyList<CodexSmartFileItem> selectedSnapshot)
    {
        var changed = 0;
        var groupsToCheck = selectedSnapshot
            .Where(file => file.DuplicateGroup > 0 && !file.IsExcluded)
            .GroupBy(file => file.DuplicateGroup)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groupsToCheck)
        {
            var candidateFiles = CodexSmartFiles
                .Where(file => file.DuplicateGroup == group.Key && !file.IsExcluded)
                .ToArray();
            if (candidateFiles.Length <= 1)
            {
                continue;
            }

            var selectedCount = candidateFiles.Count(file => file.IsSelected);
            if (selectedCount < candidateFiles.Length)
            {
                continue;
            }

            var keepNewest = candidateFiles
                .OrderByDescending(file => file.LastAccessTime)
                .ThenByDescending(file => file.LastWriteTime)
                .ThenByDescending(file => file.AllocatedBytes)
                .FirstOrDefault();

            if (keepNewest is null || !keepNewest.IsSelected)
            {
                continue;
            }

            keepNewest.IsSelected = false;
            changed++;
        }

        if (changed > 0)
        {
            UpdateCodexSmartSelection();
        }

        return changed;
    }

    private void CodexSmartGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCodexSmartSelection();

    private void CodexSmartFile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_synchronizingSpaceExclusions) return;
        if (e.PropertyName == nameof(CodexSmartFileItem.IsSelected))
        {
            UpdateCodexSmartSelection();
            return;
        }

        if (e.PropertyName == nameof(CodexSmartFileItem.IsExcluded) && sender is CodexSmartFileItem file)
        {
            _exclusionService.SetExcluded(file.FullPath, file.IsExcluded);
            if (file.IsExcluded && file.IsSelected)
            {
                file.IsSelected = false;
            }

            UpdateCodexSmartSelection();
        }
    }

    private void UpdateCodexSmartSelection()
    {
        var selectedCount = 0;
        var selectedBytes = 0L;
        foreach (var file in CodexSmartFiles)
        {
            if (file.IsSelected && !file.IsExcluded)
            {
                selectedCount++;
                selectedBytes += file.AllocatedBytes;
            }
        }

        CodexSmartDeleteButton.IsEnabled = !_isBusy && selectedCount > 0;
        if (selectedCount > 0)
        {
            CodexSmartSummaryTextBlock.Text =
                $"已选 {selectedCount:N0} 个项，预计释放 {CleanupService.FormatBytes(selectedBytes)}";
        }
        else if (CodexSmartFiles.Count > 0)
        {
            var excluded = CodexSmartFiles.Count(file => file.IsExcluded);
            var protectText = excluded > 0 ? $"；{excluded:N0} 个已保留" : string.Empty;
            CodexSmartSummaryTextBlock.Text =
                $"候选可清理 {CodexSmartFiles.Count:N0} 个，未选择删除项{protectText}。";
        }
    }

    private void BrowseCodexSmartRootButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = BrowseForFolder(
            CodexSmartRootTextBox.Text,
            "选择要进行 Codex 智能建议扫描的目录");
        if (selected is not null)
        {
            CodexSmartRootTextBox.Text = selected;
        }
    }

    private void OpenSelectedCodexSmartItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (CodexSmartGrid.SelectedItem is CodexSmartFileItem file)
        {
            RevealPath(file.FullPath);
        }
    }

    private void CodexSmartSelectPriorityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        foreach (var file in CodexSmartFiles)
        {
            file.IsSelected = !file.IsExcluded && file.RecommendationScore >= 55;
        }

        UpdateCodexSmartSelection();
    }

    private void CodexSmartSelectDuplicateDeletableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var touched = 0;
        var duplicateGroups = CodexSmartFiles
            .Where(file => file.DuplicateGroup > 0)
            .GroupBy(file => file.DuplicateGroup)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            var candidates = group
                .Where(file => !file.IsExcluded)
                .ToList();
            if (candidates.Count <= 1)
            {
                continue;
            }

            var keepNewest = candidates
                .OrderByDescending(file => file.LastAccessTime)
                .ThenByDescending(file => file.LastWriteTime)
                .ThenByDescending(file => file.AllocatedBytes)
                .First();

            foreach (var file in candidates)
            {
                if (ReferenceEquals(file, keepNewest))
                {
                    if (file.IsSelected)
                    {
                        file.IsSelected = false;
                        touched++;
                    }

                    continue;
                }

                if (!file.IsSelected)
                {
                    file.IsSelected = true;
                    touched++;
                }
            }
        }

        if (touched == 0)
        {
            MessageBox.Show(
                this,
                "未发现有可处理的重复组；请先开启“扫描后识别重复文件”后再试。",
                "无需处理",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        UpdateCodexSmartSelection();
    }

    private void CodexSmartSelectRebuildableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var touched = 0;
        foreach (var file in CodexSmartFiles)
        {
            if (file.IsExcluded)
            {
                if (file.IsSelected)
                {
                    file.IsSelected = false;
                    touched++;
                }

                continue;
            }

            var shouldSelect = file.Recommendation == "建议清理" &&
                               IsRebuildableCodexFolder(file.FolderHint);
            if (file.IsSelected != shouldSelect)
            {
                file.IsSelected = shouldSelect;
                touched++;
            }
        }

        if (touched == 0)
        {
            MessageBox.Show(
                this,
                "未找到“可重建目录”建议清理项（可重建目录为 .tmp、cache、generated_images、visualizations、archived_sessions）。",
                "无需处理",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        UpdateCodexSmartSelection();
    }

    private static bool IsRebuildableCodexFolder(string folderHint) =>
        folderHint is ".tmp" or "tmp" or "cache" or "generated_images" or "visualizations" or "archived_sessions";

    private void CodexSmartSelectVeryHighPriorityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        foreach (var file in CodexSmartFiles)
        {
            file.IsSelected = !file.IsExcluded && file.RecommendationScore >= 80;
        }

        UpdateCodexSmartSelection();
    }

    private async void ExternalAnalyzeButton_Click(object sender, RoutedEventArgs e) =>
        await AnalyzeExternalStorageAsync();

    private async Task AnalyzeExternalStorageAsync()
    {
        if (_isBusy) return;
        BeginOperation("正在读取 Docker、WSL 和虚拟磁盘占用…");
        try
        {
            var result = await _externalStorageService.AnalyzeAsync(_operationCancellation!.Token);
            ExternalStorageItems.ReplaceAll(result.Items);
            _dockerRunning = result.DockerRunning;
            ExternalSummaryTextBlock.Text =
                $"检测到 {ExternalStorageItems.Count:N0} 个存储项目" +
                (result.DockerRunning ? "；Docker 引擎正在运行" : "；Docker 引擎未运行") +
                (result.Warnings.Count > 0 ? $"；{result.Warnings.Count:N0} 条提示" : string.Empty);
        }
        catch (OperationCanceledException) { ExternalSummaryTextBlock.Text = "检测已取消。"; }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Docker / WSL 检测失败：\n\n{ex.Message}", "检测失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async void DockerPruneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !_dockerRunning) return;
        var answer = MessageBox.Show(this,
            "将调用 Docker 官方安全清理：删除已停止容器、未使用网络、悬空镜像和构建缓存。\n\n" +
            "不会使用 --volumes，因此不会删除 Docker 卷。确定继续吗？",
            "确认清理 Docker", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        BeginOperation("正在执行 Docker 安全清理…");
        try
        {
            var output = await _externalStorageService.PruneDockerAsync(_operationCancellation!.Token);
            await _logService.AppendOperationAsync(
                "Docker 安全清理",
                1,
                0,
                0,
                dryRun: false,
                _operationCancellation.Token);
            MessageBox.Show(this, string.IsNullOrWhiteSpace(output) ? "Docker 清理已完成。" : output,
                "Docker 清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MessageBox.Show(this, ex.Message, "Docker 清理失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
        await AnalyzeExternalStorageAsync();
    }

    private void OpenSelectedExternalItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExternalStorageGrid.SelectedItem is not ExternalStorageItem item) return;
        if (File.Exists(item.PathOrDetail) || Directory.Exists(item.PathOrDetail))
            RevealPath(item.PathOrDetail);
    }

    private IProgress<ScanProgressInfo> CreateScanProgress() =>
        new Progress<ScanProgressInfo>(progress =>
        {
            BusyTextBlock.Text = progress.Stage;
            var path = string.IsNullOrWhiteSpace(progress.CurrentPath)
                ? string.Empty
                : $" · {progress.CurrentPath}";
            BusyDetailTextBlock.Text =
                $"已处理 {progress.ProcessedFiles:N0} 个文件、{progress.ProcessedDirectories:N0} 个目录，" +
                $"读取 {CleanupService.FormatBytes(progress.ProcessedBytes)}{path}";
        });

    private void ConfigureResultFilters()
    {
        foreach (var source in new object[]
                 {
                     Categories, DetailFiles, AnalysisItems,
                     CodexSmartFiles, ExternalStorageItems, HistoryItems
                 })
        {
            CollectionViewSource.GetDefaultView(source).Filter = MatchesResultSearch;
        }
    }

    private bool MatchesResultSearch(object item)
    {
        var query = ResultSearchTextBox.Text.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return item switch
        {
            CleanupCategoryItem value => Contains(value.Name, query) ||
                                         Contains(value.Group, query) ||
                                         Contains(value.Risk, query) ||
                                         Contains(value.Description, query),
            CleanupFileItem value => Contains(value.Name, query) || Contains(value.FullPath, query),
            DiskAnalysisItem value => Contains(value.Kind, query) ||
                                      Contains(value.Name, query) ||
                                      Contains(value.FullPath, query) ||
                                      Contains(value.StorageNote, query),
            DuplicateFileItem value => Contains(value.FullPath, query) ||
                                       value.GroupNumber.ToString().Contains(query, StringComparison.OrdinalIgnoreCase),
            CodexSmartFileItem value => Contains(value.FullPath, query) ||
                                        Contains(value.Group, query) ||
                                        Contains(value.Extension, query) ||
                                        Contains(value.FolderHint, query) ||
                                        Contains(value.RecommendationReason, query),
            ExternalStorageItem value => Contains(value.Source, query) ||
                                         Contains(value.Name, query) ||
                                         Contains(value.Status, query) ||
                                         Contains(value.PathOrDetail, query),
            CleanupHistoryItem value => Contains(value.Operation, query) || Contains(value.Status, query),
            _ => true
        };
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private void ResultSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshResultFilters();

    private void ClearResultSearchButton_Click(object sender, RoutedEventArgs e) =>
        ResultSearchTextBox.Clear();

    private void RefreshResultFilters()
    {
        AnalysisExplorer?.SetSearchText(ResultSearchTextBox.Text);
        DuplicateCenterView?.SetSearchText(ResultSearchTextBox.Text);
        MediaCenterView?.SetSearchText(ResultSearchTextBox.Text);
        DevelopmentCenterView?.SetSearchText(ResultSearchTextBox.Text);
        SoftwareCenterView?.SetSearchText(ResultSearchTextBox.Text);
        foreach (var source in new object[]
                 {
                     Categories, DetailFiles, AnalysisItems,
                     CodexSmartFiles, ExternalStorageItems, HistoryItems
                 })
        {
            CollectionViewSource.GetDefaultView(source).Refresh();
        }
    }

    private async void MediaScanRequested(object? sender, EventArgs e)
    {
        if (_isBusy) return;
        if (!MediaCenterView.ImagesEnabled && !MediaCenterView.VideosEnabled && !MediaCenterView.AudioEnabled)
        { MessageBox.Show(this, "请至少选择一种媒体类型。", "相似媒体"); return; }
        var scopes = ParseScanScopeWhitelist(); if (scopes is null) return;
        IReadOnlyList<string> references, exclusions;
        try { references = _duplicateReferences.GetPaths().ToArray(); exclusions = _exclusionService.GetPaths().ToArray(); }
        catch (Exception ex) { MessageBox.Show(this, "无法读取保护规则，请先修复参考目录设置：\n" + ex.Message, "保护规则不可用"); return; }
        var options = new MediaAnalysisOptions(MediaCenterView.RootPath.Trim(), MediaCenterView.ImagesEnabled,
            MediaCenterView.VideosEnabled, MediaCenterView.AudioEnabled, MediaCenterView.ImageDistance, scopes, exclusions, references);
        BeginOperation("正在建立相似媒体指纹；只读分析，不删除文件…");
        try
        {
            var run = _mediaService.AnalyzeAsync(options, _operationCancellation!.Token, CreateScanProgress());
            _mediaRun = run; var result = await run; MediaCenterView.SetResult(result);
        }
        catch (Exception ex) { MessageBox.Show(this, "媒体分析失败：\n" + ex.Message, "相似媒体", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _mediaRun = null; EndOperation(); }
    }

    private async void ExportCurrentResultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if ((MainTabControl.SelectedItem as TabItem)?.Header?.ToString() == "空间分析")
        { await ExportSpaceIndexAsync(); return; }

        var export = GetCurrentExportData();
        if (export.Rows.Count == 0)
        {
            MessageBox.Show(this, "当前页面还没有可导出的结果。", "导出结果",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出扫描结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"磁盘空间助手-{export.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _csvReportService.ExportAsync(dialog.FileName, export.Headers, export.Rows);
            MessageBox.Show(this, $"已导出 {export.Rows.Count:N0} 条结果：\n\n{dialog.FileName}",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"导出失败：\n\n{ex.Message}", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ExportData GetCurrentExportData()
    {
        var header = (MainTabControl.SelectedItem as TabItem)?.Header?.ToString() ?? string.Empty;
        return header switch
        {
            "快速清理" => new ExportData(
                "快速清理",
                ["类别", "名称", "大小", "修改时间", "是否排除", "完整路径"],
                Categories.SelectMany(category => category.Files.Select(file => (IReadOnlyList<string>)
                    [category.Name, file.Name, file.DisplaySize, file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                     file.IsExcluded ? "是" : "否", file.FullPath])).ToList()),
            "Codex 智能建议" => new ExportData(
                "Codex智能建议",
                ["优先级", "分值", "建议", "逻辑大小", "实际占用", "重复组", "清理依据", "完整路径"],
                CodexSmartFiles.Select(file => (IReadOnlyList<string>)
                    [file.Group, file.RecommendationScore.ToString(), file.Recommendation, file.LogicalSize,
                     file.AllocatedSize, file.DuplicateGroup.ToString(), file.RecommendationReason, file.FullPath]).ToList()),
            "空间分析" => new ExportData(
                "空间分析",
                ["类型", "名称", "逻辑字节", "占用字节", "文件数", "占用增长字节", "逻辑增长字节", "占用是否近似", "状态", "完整路径"],
                (AnalysisExplorer.Result?.Roots.SelectMany(root => root.DescendantsAndSelf()).Where(node => node.IsDirectory)
                    .Concat(AnalysisExplorer.Result.LargestFiles) ?? [])
                    .Select(item => (IReadOnlyList<string>)
                    [item.Kind, item.Name, item.LogicalBytes.ToString(), item.AllocatedBytes.ToString(),
                     item.FileCount.ToString(), item.GrowthBytes?.ToString() ?? "", item.LogicalGrowthBytes?.ToString() ?? "",
                     AnalysisExplorer.Result?.IsApproximate == true ? "是" : "否", item.State, item.FullPath]).ToList()),
            "重复文件中心" => new ExportData(
                "重复文件",
                ["组", "副本数", "逻辑大小", "实际占用", "完整哈希确认", "修改时间", "完整路径"],
                DuplicateFiles.Select(file => (IReadOnlyList<string>)
                    [file.GroupNumber.ToString(), file.Copies.ToString(), file.LogicalSize, file.AllocatedSize,
                     file.IsFullHashVerified ? "是" : "否", file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), file.FullPath]).ToList()),
            "Docker / WSL" => new ExportData(
                "Docker-WSL",
                ["来源", "项目", "逻辑大小", "实际占用", "可回收", "状态", "位置或详情"],
                ExternalStorageItems.Select(item => (IReadOnlyList<string>)
                    [item.Source, item.Name, item.LogicalSize, item.AllocatedSize,
                     item.ReclaimableSize, item.Status, item.PathOrDetail]).ToList()),
            "相似媒体" => new ExportData("相似媒体",
                ["候选组", "类型", "来源推断", "名称", "逻辑字节", "分辨率", "时长秒", "指纹相似度", "建议", "参考目录保护", "拍摄日期", "隐私字段", "EXIF同字段组", "完整路径"],
                (MediaCenterView.Result?.Items ?? []).Select(row => (IReadOnlyList<string>)
                    [row.Group.ToString(), row.KindText, row.OriginText, row.Name, row.Fingerprint.Length.ToString(), row.Dimensions,
                     row.Fingerprint.Duration.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), row.SimilarityText,
                     row.Advice, row.IsReference ? "是" : "否", row.TakenText, row.PrivacyText, row.MetadataGroup.ToString(), row.FullPath]).ToList()),
            "清理历史" => new ExportData(
                "清理历史",
                ["时间", "操作", "项目数", "释放空间", "失败数", "结果"],
                HistoryItems.Select(item => (IReadOnlyList<string>)
                    [item.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), item.Operation, item.ItemCount.ToString(),
                     item.ReclaimedSize, item.FailureCount.ToString(), item.Status]).ToList()),
            "开发环境" => new ExportData("开发环境",
                ["分类", "类型", "名称", "项目", "任务状态", "Git状态", "逻辑字节", "实际占用字节", "文件数", "最近修改UTC", "操作条件", "依据", "跨工作树副本", "路径"],
                (DevelopmentCenterView.Result?.Items ?? []).Select(i => (IReadOnlyList<string>)
                    [i.SafetyText, i.KindText, i.Name, i.Project, i.TaskState, i.GitState, i.LogicalBytes.ToString(), i.AllocatedBytes.ToString(), i.FileCount.ToString(), i.LastModifiedUtc.ToString("O"), i.ActionText, i.Evidence, i.DuplicateHint, i.FullPath]).ToList()),
            "软件占用" => new ExportData("软件占用",
                ["软件", "类型", "发布者", "版本", "状态", "登记字节", "测量逻辑字节", "测量占用字节", "大小依据", "文件数", "可卸载", "依据", "路径"],
                (SoftwareCenterView.Result?.Items ?? []).Select(i => (IReadOnlyList<string>)
                    [i.Name, i.KindText, i.Publisher, i.Version, i.Status, i.Registration.ReportedBytes.ToString(), i.LogicalBytes?.ToString() ?? "", i.AllocatedBytes?.ToString() ?? "", i.SizeBasis, i.FileCount.ToString(), i.CanUninstall ? "是" : "否", i.Evidence, i.FullPath]).ToList()),
            _ => new ExportData("结果", [], [])
        };
    }

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshHistoryAsync();

    private async Task RefreshHistoryAsync()
    {
        try
        {
            var history = await _logService.ReadHistoryAsync();
            HistoryItems.ReplaceAll(history);
            HistorySummaryTextBlock.Text = history.Count == 0
                ? "还没有清理记录；模拟清理和实际清理都会显示在这里。"
                : $"共显示最近 {history.Count:N0} 条记录，累计记录空间 " +
                  CleanupService.FormatBytes(history.Sum(item => item.ReclaimedBytes)) + "。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HistorySummaryTextBlock.Text = $"读取历史失败：{ex.Message}";
        }
    }

    private void OpenRawLogButton_Click(object sender, RoutedEventArgs e) => OpenRawLog();

    private void OpenRawLog()
    {
        if (!File.Exists(_logService.LogPath))
        {
            MessageBox.Show(this, "还没有清理记录。", "清理历史",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = _logService.LogPath, UseShellExecute = true });
    }

    private void ApplySettings(AppSettings settings)
    {
        FastModeCheckBox.IsChecked = settings.FastMode;
        DryRunCheckBox.IsChecked = settings.DryRun;
        DaysTextBox.Text = settings.Days;
        ProjectPathTextBox.Text = settings.ProjectPath ?? string.Empty;
        CodexSmartRootTextBox.Text = settings.CodexRoot ?? _cleanupService.CodexRoot;
        CodexSmartMinimumTextBox.Text = settings.CodexMinimumMb;
        CodexSmartAgeTextBox.Text = settings.CodexAgeDays;
        CodexSmartDetectDuplicatesCheckBox.IsChecked = settings.CodexDetectDuplicates;
        AnalysisPathTextBox.Text = settings.AnalysisPath ?? AnalysisPathTextBox.Text;
        MinimumSizeTextBox.Text = settings.AnalysisMinimumMb;
        EngineModeBox.SelectedIndex = Math.Clamp(settings.EngineMode, 0, 2);
        IndexHashCheckBox.IsChecked = settings.IndexFingerprints;
        DuplicatePathTextBox.Text = settings.DuplicatePath ?? DuplicatePathTextBox.Text;
        DuplicateMinimumTextBox.Text = settings.DuplicateMinimumMb;
        DuplicateCenterView.KeepPolicy = (DuplicateKeepPolicy)Math.Clamp(settings.DuplicateKeepPolicy, 0, 4);
        DuplicateCenterView.OnlyCrossDirectory = settings.DuplicateOnlyCrossDirectory;
        DuplicateCenterView.OnlyBackupOverlap = settings.DuplicateOnlyBackupOverlap;
        DuplicateCenterView.OnlyReferenceMatched = settings.DuplicateOnlyReferenceMatched;
        MediaCenterView.RootPath = settings.MediaPath ?? MediaCenterView.RootPath;
        MediaCenterView.ImagesEnabled = settings.MediaImages;
        MediaCenterView.VideosEnabled = settings.MediaVideos;
        MediaCenterView.AudioEnabled = settings.MediaAudio;
        MediaCenterView.Sensitivity = settings.MediaSensitivity;
        DevelopmentCenterView.RootPath = settings.DevelopmentPath ?? DevelopmentCenterView.RootPath;
        DevelopmentCenterView.CodexHome = settings.DevelopmentCodexHome ?? DevelopmentCenterView.CodexHome;
        DevelopmentCenterView.IncludeCodex = settings.DevelopmentCodex;
        DevelopmentCenterView.IncludeCaches = settings.DevelopmentCaches;
        DevelopmentCenterView.IncludeDocker = settings.DevelopmentDocker;
        DevelopmentCenterView.CompareBuilds = settings.DevelopmentCompare;
        DevelopmentCenterView.MergeTarget = settings.DevelopmentMergeTarget;
        SoftwareCenterView.IncludeStore = settings.SoftwareStore;
        SoftwareCenterView.IncludeGames = settings.SoftwareGames;
        SoftwareCenterView.FindResidues = settings.SoftwareResidues;
        SoftwareCenterView.MeasureDirectories = settings.SoftwareMeasure;
        ScanScopeWhitelistTextBox.Text = settings.ScanScopeWhitelist;
        MainTabControl.SelectedIndex = Math.Clamp(
            settings.SelectedTabIndex, 0, Math.Max(0, MainTabControl.Items.Count - 1));
    }

    private AppSettings CaptureSettings() => new()
    {
        FastMode = IsFastModeEnabled,
        DryRun = IsDryRunEnabled,
        Days = DaysTextBox.Text,
        ProjectPath = ProjectPathTextBox.Text,
        CodexRoot = CodexSmartRootTextBox.Text,
        CodexMinimumMb = CodexSmartMinimumTextBox.Text,
        CodexAgeDays = CodexSmartAgeTextBox.Text,
        CodexDetectDuplicates = CodexSmartDetectDuplicatesCheckBox.IsChecked == true,
        AnalysisPath = AnalysisPathTextBox.Text,
        AnalysisMinimumMb = MinimumSizeTextBox.Text,
        DuplicatePath = DuplicatePathTextBox.Text,
        DuplicateMinimumMb = DuplicateMinimumTextBox.Text,
        ScanScopeWhitelist = ScanScopeWhitelistTextBox.Text,
        SelectedTabIndex = MainTabControl.SelectedIndex,
        EngineMode = EngineModeBox.SelectedIndex,
        IndexFingerprints = IndexHashCheckBox.IsChecked == true,
        DuplicateKeepPolicy = (int)DuplicateCenterView.KeepPolicy,
        DuplicateOnlyCrossDirectory = DuplicateCenterView.OnlyCrossDirectory,
        DuplicateOnlyBackupOverlap = DuplicateCenterView.OnlyBackupOverlap,
        DuplicateOnlyReferenceMatched = DuplicateCenterView.OnlyReferenceMatched,
        MediaPath = MediaCenterView.RootPath,
        MediaImages = MediaCenterView.ImagesEnabled,
        MediaVideos = MediaCenterView.VideosEnabled,
        MediaAudio = MediaCenterView.AudioEnabled,
        MediaSensitivity = MediaCenterView.Sensitivity,
        DevelopmentPath = DevelopmentCenterView.RootPath,
        DevelopmentCodexHome = DevelopmentCenterView.CodexHome,
        DevelopmentCodex = DevelopmentCenterView.IncludeCodex,
        DevelopmentCaches = DevelopmentCenterView.IncludeCaches,
        DevelopmentDocker = DevelopmentCenterView.IncludeDocker,
        DevelopmentCompare = DevelopmentCenterView.CompareBuilds,
        DevelopmentMergeTarget = DevelopmentCenterView.MergeTarget,
        SoftwareStore = SoftwareCenterView.IncludeStore,
        SoftwareGames = SoftwareCenterView.IncludeGames,
        SoftwareResidues = SoftwareCenterView.FindResidues,
        SoftwareMeasure = SoftwareCenterView.MeasureDirectories
    };

    private static string? BrowseForFolder(string initialPath, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void AddScanScopeWhitelistButton_Click(object sender, RoutedEventArgs e)
    {
        var initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(ScanScopeWhitelistTextBox.Text))
        {
            var firstLine = ParseScanScopeValues(ScanScopeWhitelistTextBox.Text).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                initial = firstLine;
            }
        }

        var selected = BrowseForFolder(initial, "选择白名单目录");
        if (selected is null)
        {
            return;
        }

        var normalized = DiskAnalysisService.NormalizePath(selected);

        var existing = ParseScanScopeValues(ScanScopeWhitelistTextBox.Text)
            .Select(item =>
            {
                try
                {
                    return DiskAnalysisService.NormalizePath(item);
                }
                catch
                {
                    return item
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            })
            .ToList();

        if (existing.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "该目录已在白名单中。",
                "重复目录",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(ScanScopeWhitelistTextBox.Text))
        {
            ScanScopeWhitelistTextBox.Text = normalized;
            return;
        }

        ScanScopeWhitelistTextBox.Text = $"{ScanScopeWhitelistTextBox.Text.TrimEnd()}\r\n{normalized}";
    }

    private void ClearScanScopeWhitelistButton_Click(object sender, RoutedEventArgs e) =>
        ScanScopeWhitelistTextBox.Clear();

    private List<string>? ParseScanScopeWhitelist()
    {
        var values = ParseScanScopeValues(ScanScopeWhitelistTextBox.Text);
        if (values.Count == 0)
        {
            return [];
        }

        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var value in values)
        {
            var normalized = Path.TrimEndingDirectorySeparator(value);
            try
            {
                var full = DiskAnalysisService.NormalizePath(normalized);
                if (Directory.Exists(full))
                {
                    valid.Add(full);
                }
                else
                {
                    invalid.Add(value);
                }
            }
            catch
            {
                invalid.Add(value);
            }
        }

        valid = [.. new HashSet<string>(valid, StringComparer.OrdinalIgnoreCase)];
        if (invalid.Count > 0)
        {
            MessageBox.Show(
                this,
                $"以下白名单目录无效或不存在：\r\n{string.Join(Environment.NewLine, invalid.Distinct())}",
                "路径校验",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return valid.Count > 0 ? valid : null;
        }

        return valid;
    }

    private static List<string> ParseScanScopeValues(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Replace(';', '\n')
            .Replace('；', '\n')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OpenCodexFolderButton_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_cleanupService.CodexRoot);

    private void OpenSelectedCleanupFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DetailGrid.SelectedItem is CleanupFileItem file)
        {
            RevealPath(file.FullPath);
        }
    }

    private void OpenSelectedAnalysisItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (AnalysisExplorer.SelectedNode is { } item && !string.IsNullOrEmpty(item.FullPath))
        {
            RevealPath(item.FullPath);
        }
    }

    private async void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedItem = HistoryTabItem;
        await RefreshHistoryAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    { _spaceSession?.Resume(); _operationCancellation?.Cancel(); }

    private bool TryGetScanOptions(out CleanupScanOptions options)
    {
        options = new CleanupScanOptions(7);
        if (!int.TryParse(DaysTextBox.Text, out var days) || days is < 1 or > 3650)
        {
            MessageBox.Show(
                this,
                "请输入 1 到 3650 之间的保留天数。",
                "天数不正确",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DaysTextBox.Focus();
            DaysTextBox.SelectAll();
            return false;
        }

        var projectRoot = string.IsNullOrWhiteSpace(ProjectPathTextBox.Text)
            ? null
            : ProjectPathTextBox.Text.Trim();
        if (projectRoot is not null && !Directory.Exists(projectRoot))
        {
            MessageBox.Show(
                this,
                "所选开发项目目录不存在，请重新选择或清空该输入框。",
                "项目目录不存在",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        if (projectRoot is not null)
        {
            var normalizedProjectRoot = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var driveRoot = Path.GetPathRoot(normalizedProjectRoot)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalizedProjectRoot.Equals(driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    "开发项目扫描不能直接选择整个磁盘。请指定包含项目的文件夹；整盘统计请使用“空间分析”。",
                    "项目范围过大",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }
        }

        options = new CleanupScanOptions(days, projectRoot);
        return true;
    }

    private void BeginOperation(string message)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _isBusy = true;
        BusyTextBlock.Text = message;
        BusyDetailTextBlock.Text = "正在准备…";
        BusyOverlay.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        ScanButton.IsEnabled = false;
        AnalyzeButton.IsEnabled = false;
        DuplicateScanButton.IsEnabled = false;
        CodexSmartScanButton.IsEnabled = false;
        CodexSmartDeleteButton.IsEnabled = false;
        ExternalAnalyzeButton.IsEnabled = false;
        DockerPruneButton.IsEnabled = false;
        DeleteDuplicatesButton.IsEnabled = false;
        HardLinkDuplicatesButton.IsEnabled = false;
        CleanupButton.IsEnabled = false;
        FastModeCheckBox.IsEnabled = false;
        DryRunCheckBox.IsEnabled = false;
        MainTabControl.IsEnabled = false;
    }

    private void EndOperation()
    {
        _isBusy = false;
        BusyOverlay.Visibility = Visibility.Collapsed;
        BusyDetailTextBlock.Text = string.Empty;
        CancelButton.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;
        AnalyzeButton.IsEnabled = true;
        DuplicateScanButton.IsEnabled = true;
        CodexSmartScanButton.IsEnabled = true;
        ExternalAnalyzeButton.IsEnabled = true;
        DockerPruneButton.IsEnabled = _dockerRunning;
        FastModeCheckBox.IsEnabled = true;
        DryRunCheckBox.IsEnabled = true;
        MainTabControl.IsEnabled = true;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        UpdateSelectedSummary();
        UpdateDuplicateSelection();
        UpdateCodexSmartSelection();
    }

    private void UnsubscribeCurrentItems()
    {
        foreach (var category in Categories)
        {
            category.PropertyChanged -= Category_PropertyChanged;
            foreach (var file in category.Files)
            {
                file.PropertyChanged -= CleanupFile_PropertyChanged;
            }
        }

        foreach (var file in CodexSmartFiles)
        {
            file.PropertyChanged -= CodexSmartFile_PropertyChanged;
        }
    }

    private void ScanOptions_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isBusy)
        {
            return;
        }

        CleanupButton.IsEnabled = false;
        SummaryTextBlock.Text = "扫描条件已变化，请重新扫描。";
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = NonDigitRegex.IsMatch(e.Text);

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show(
                $"找不到目录：\n\n{path}",
                "目录不存在",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private static void RevealPath(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
    }

    private sealed record ExportData(
        string Name,
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> Rows);
}
