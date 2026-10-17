using ChatGPTCleanupTool.Services;
using System.IO;

var testRoot = Path.Combine(
    Path.GetTempPath(),
    "disk-cleanup-assistant-test-" + Guid.NewGuid().ToString("N"));
var codexRoot = Path.Combine(testRoot, ".codex");
var tempRoot = Path.Combine(testRoot, "temp");
var localAppData = Path.Combine(testRoot, "local-app-data");
var programData = Path.Combine(testRoot, "program-data");
var projectRoot = Path.Combine(testRoot, "project");

try
{
    if (args.Contains("--overview-only")) { await OverviewTests.RunAsync(testRoot); await OverviewTests.StandaloneUiAsync(); return 0; }
    if (args.Contains("--nps-sample")) { await NpsProjectTests.SampleAsync(testRoot); return 0; }
    if (args.Contains("--nps-only")) { await NpsProjectTests.RunAsync(testRoot, args.Contains("--test-recycle")); await NpsProjectTests.StandaloneUiAsync(); return 0; }
    if (args.Contains("--software-inventory-only")) { SoftwareTests.ReadRealInventory(); return 0; }
    if (args.Contains("--software-only")) { await SoftwareTests.RunAsync(testRoot); await SoftwareTests.RunStandaloneUiAsync(); return 0; }
    if (args.Contains("--development-only"))
    { await DevelopmentTests.RunAsync(testRoot, args.Contains("--test-recycle")); await DevelopmentTests.RunStandaloneUiAsync(); return 0; }
    if (args.Contains("--media-only"))
    { await MediaAnalysisTests.RunAsync(testRoot); await MediaAnalysisTests.RunStandaloneUiAsync(); return 0; }
    var oldTemp = CreateFile(Path.Combine(codexRoot, ".tmp", "old.tmp"), 1024, 10);
    var newTemp = CreateFile(Path.Combine(codexRoot, ".tmp", "new.tmp"), 512, 1);
    var oldImage = CreateFile(
        Path.Combine(codexRoot, "generated_images", "old.png"),
        2048,
        10);
    var oldDump = CreateFile(
        Path.Combine(localAppData, "CrashDumps", "old.dmp"),
        4096,
        20);
    var pipCache = CreateFile(
        Path.Combine(localAppData, "pip", "Cache", "wheel.bin"),
        3072,
        20);
    var buildOutput = CreateFile(
        Path.Combine(projectRoot, "src", "Sample", "bin", "Release", "app.dll"),
        8192,
        20);
    var unrelated = CreateFile(Path.Combine(testRoot, "keep.txt"), 100, 30);
    var duplicateA = CreateContentFile(
        Path.Combine(testRoot, "duplicates", "copy-a.bin"), "confirmed duplicate content");
    var duplicateB = CreateContentFile(
        Path.Combine(testRoot, "duplicates", "copy-b.bin"), "confirmed duplicate content");
    _ = CreateContentFile(
        Path.Combine(testRoot, "duplicates", "same-size-not-duplicate.bin"), "different duplicate content");

    var service = new CleanupService(codexRoot, tempRoot, localAppData, programData);
    var options = new CleanupScanOptions(7, projectRoot);
    var summary = await service.ScanAsync(options);

    var temporary = summary.Single(item => item.Id == "codex-temporary");
    var images = summary.Single(item => item.Id == "codex-generated-images");
    var dumps = summary.Single(item => item.Id == "windows-crash-dumps");
    var pip = summary.Single(item => item.Id == "pip-cache");
    var builds = summary.Single(item => item.Id == "dotnet-build-output");
    Assert(temporary.FileCount == 1, "旧 Codex 临时文件数量应为 1");
    Assert(images.FileCount == 1, "旧生成图片数量应为 1");
    Assert(dumps.FileCount == 1, "崩溃转储数量应为 1");
    Assert(pip.FileCount == 1, "pip 缓存数量应为 1");
    Assert(builds.FileCount == 1, "项目构建产物数量应为 1");

    var exclusionPath = Path.Combine(testRoot, "settings", "exclusions.json");
    var exclusions = new ExclusionService(exclusionPath);
    exclusions.SetExcluded(oldImage, true);
    Assert(exclusions.Contains(oldImage), "永久排除项保存失败");
    exclusions.SetExcluded(oldImage, false);
    Assert(!exclusions.Contains(oldImage), "永久排除项移除失败");
    exclusions.SetExcludedPaths([oldImage, oldDump], true);
    var batchReloaded = new ExclusionService(exclusionPath);
    Assert(batchReloaded.Contains(oldImage) && batchReloaded.Contains(oldDump), "右键批量排除没有完整保存");
    exclusions.SetExcludedPaths([oldImage, oldDump], false);
    Assert(!exclusions.Contains(oldImage) && !exclusions.Contains(oldDump), "批量解除排除失败");
    var blockedConfigRoot = Path.Combine(testRoot, "batch-exclusion-blocker");
    File.WriteAllText(blockedConfigRoot, "fixture blocks directory creation");
    var blockedExclusions = new ExclusionService(Path.Combine(blockedConfigRoot, "exclusions.json"));
    try { blockedExclusions.SetExcludedPaths([oldImage, oldDump], true); throw new InvalidOperationException("排除保存失败未报告"); }
    catch (IOException) { Assert(blockedExclusions.GetPaths().Count == 0, "保存失败不应保留半个内存排除计划"); }

    var cleanup = await service.CleanupFilesAsync(temporary.Files, options);
    Assert(cleanup.DeletedCount == 1, "精确清理计划应删除 1 个旧临时文件");
    Assert(!File.Exists(oldTemp), "旧临时文件仍然存在");
    Assert(File.Exists(newTemp), "新的临时文件不应被删除");
    Assert(File.Exists(oldImage), "未选择的旧图片不应被删除");
    Assert(File.Exists(oldDump), "未选择的崩溃转储不应被删除");
    Assert(File.Exists(pipCache), "未选择的 pip 缓存不应被删除");
    Assert(File.Exists(buildOutput), "未选择的构建产物不应被删除");
    Assert(File.Exists(unrelated), "清理范围外文件不应被删除");

    var changeCandidate = CreateFile(
        Path.Combine(codexRoot, ".tmp", "changed.tmp"),
        512,
        20);
    var changedPlan = (await service.ScanAsync(options))
        .Single(item => item.Id == "codex-temporary")
        .Files
        .Single(item => item.FullPath == changeCandidate);
    File.AppendAllText(changeCandidate, "changed after scan");
    var changedResult = await service.CleanupFilesAsync([changedPlan], options);
    Assert(changedResult.DeletedCount == 0, "扫描后变化的文件不应被删除");
    Assert(changedResult.FailureCount == 1, "扫描后变化应记录一次跳过原因");
    Assert(File.Exists(changeCandidate), "扫描后变化的文件仍应存在");

    var analyzer = new DiskAnalysisService(new DiskSnapshotService(Path.Combine(testRoot, "general-snapshots")));
    var progressEvents = new List<ChatGPTCleanupTool.Models.ScanProgressInfo>();
    var analysis = await analyzer.AnalyzeAsync(
        testRoot,
        1024,
        maximumFiles: 20,
        progress: new InlineProgress<ChatGPTCleanupTool.Models.ScanProgressInfo>(progressEvents.Add));
    Assert(analysis.ScannedFileCount >= 7, "空间分析扫描文件数异常");
    Assert(analysis.Items.Any(item => item.FullPath == buildOutput), "未发现大文件");
    Assert(analysis.Items.Any(item => item.Kind == "一级目录"), "未生成一级目录汇总");
    Assert(analysis.ScannedLogicalBytes >= analysis.ScannedAllocatedBytes ||
           analysis.ScannedAllocatedBytes > 0, "实际占用统计异常");
    var analyzedBuild = analysis.Items.Single(item => item.FullPath == buildOutput);
    Assert(analyzedBuild.LogicalBytes == 8192, "逻辑文件大小统计错误");
    Assert(analyzedBuild.AllocatedBytes >= 0, "磁盘实际占用统计错误");
    Assert(progressEvents.Count > 0, "空间分析未报告扫描进度");
    Assert(progressEvents.Any(item => item.ProcessedFiles > 0), "扫描进度未包含文件数量");

    var duplicateService = new DuplicateFileService(new FileHashCacheService(Path.Combine(testRoot, "base-duplicate-cache.json")),
        new ReferenceDirectoryService(Path.Combine(testRoot, "base-duplicate-references.json")), new DuplicateOperationLogService(Path.Combine(testRoot, "base-duplicate-audit.sqlite")));
    var duplicates = await duplicateService.AnalyzeAsync(testRoot, 1);
    var confirmedGroup = duplicates.Files
        .Where(item => item.FullPath == duplicateA || item.FullPath == duplicateB)
        .ToArray();
    Assert(duplicates.GroupCount >= 1, "未发现重复文件组");
    Assert(confirmedGroup.Length == 2, "内容相同的两个文件未被归入同组");
    Assert(confirmedGroup.Select(item => item.Hash).Distinct().Count() == 1,
        "重复文件完整哈希不一致");
    Assert(confirmedGroup.Select(item => item.FileIdentity).Distinct().Count() == 2,
        "不同文件的物理标识不应相同");

    var logPath = Path.Combine(testRoot, "logs", "history.jsonl");
    var logService = new CleanupLogService(logPath);
    await logService.AppendAsync(cleanup, options);
    await logService.AppendOperationAsync("测试模拟清理", 2, 2048, 0, dryRun: true);
    Assert(File.Exists(logPath), "清理日志未生成");
    Assert(new FileInfo(logPath).Length > 0, "清理日志为空");
    var history = await logService.ReadHistoryAsync();
    Assert(history.Count == 2, "统一清理历史读取数量错误");
    Assert(history.Any(item => item.Operation == "测试模拟清理" && item.Status == "模拟执行"),
        "模拟清理历史状态错误");

    var hashCachePath = Path.Combine(testRoot, "settings", "hash-cache.json");
    var hashCache = new FileHashCacheService(hashCachePath);
    var duplicateInfo = new FileInfo(duplicateA);
    hashCache.StoreSample(duplicateA, duplicateInfo.Length, duplicateInfo.LastWriteTimeUtc, "SAMPLE");
    hashCache.StoreFull(duplicateA, duplicateInfo.Length, duplicateInfo.LastWriteTimeUtc, "FULL");
    Assert(hashCache.TrySave(out _), "哈希缓存保存失败");
    var reloadedHashCache = new FileHashCacheService(hashCachePath);
    Assert(reloadedHashCache.TryGetSample(
        duplicateA, duplicateInfo.Length, duplicateInfo.LastWriteTimeUtc, out var sampleHash) &&
        sampleHash == "SAMPLE", "采样哈希缓存复用失败");
    Assert(reloadedHashCache.TryGetFull(
        duplicateA, duplicateInfo.Length, duplicateInfo.LastWriteTimeUtc, out var fullHash) &&
        fullHash == "FULL", "完整哈希缓存复用失败");
    Assert(!reloadedHashCache.TryGetFull(
        duplicateA, duplicateInfo.Length + 1, duplicateInfo.LastWriteTimeUtc, out _),
        "文件大小变化后哈希缓存应失效");

    var settingsPath = Path.Combine(testRoot, "settings", "app-settings.json");
    var settingsService = new AppSettingsService(settingsPath);
    settingsService.Save(new AppSettings
    {
        FastMode = false,
        Days = "30",
        ScanScopeWhitelist = testRoot,
        SelectedTabIndex = 3
    });
    var loadedSettings = settingsService.Load();
    Assert(!loadedSettings.FastMode && loadedSettings.Days == "30" &&
           loadedSettings.ScanScopeWhitelist == testRoot && loadedSettings.SelectedTabIndex == 3,
        "应用设置持久化失败");

    var csvPath = Path.Combine(testRoot, "reports", "scan.csv");
    Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
    await new CsvReportService().ExportAsync(
        csvPath,
        ["名称", "路径"],
        new List<IReadOnlyList<string>> { new[] { "含,逗号", duplicateA } });
    var csvText = await File.ReadAllTextAsync(csvPath);
    Assert(csvText.Contains("\"含,逗号\""), "CSV 特殊字符转义失败");

    var protectedEmpty = Path.Combine(codexRoot, ".tmp", "protected-empty"); Directory.CreateDirectory(protectedEmpty);
    var dryEmpty = Path.Combine(codexRoot, ".tmp", "dry-empty"); Directory.CreateDirectory(dryEmpty);
    var guardCandidate = CreateFile(Path.Combine(codexRoot, ".tmp", "guard.tmp"), 1024, 20);
    var guardPlan = (await service.ScanAsync(options)).Single(c => c.Id == "codex-temporary").Files.Single(f => f.FullPath == guardCandidate);
    await service.CleanupFilesAsync([guardPlan], options, true);
    Assert(Directory.Exists(dryEmpty), "模拟清理不应删除空目录");
    await service.CleanupFilesAsync([guardPlan], options, protectedPaths: [guardCandidate, protectedEmpty]);
    Assert(File.Exists(guardCandidate) && Directory.Exists(protectedEmpty), "业务层保护路径不得删除文件或空目录");
    await service.CleanupFilesAsync([guardPlan], options, protectedPaths: [protectedEmpty]);
    Assert(!File.Exists(guardCandidate) && Directory.Exists(protectedEmpty), "清理后空目录修剪应避开受保护目录");
    var recoveryFile = CreateFile(Path.Combine(codexRoot, ".tmp", ".diskcleanup-backup-test.tmp"), 1024, 20);
    var recoveryPlan = (await service.ScanAsync(options)).Single(c => c.Id == "codex-temporary").Files.Single(f => f.FullPath == recoveryFile);
    Assert(exclusions.Contains(recoveryFile), "异常恢复文件应在其他页面默认受保护");
    var recoveryCleanup = await service.CleanupFilesAsync([recoveryPlan], options);
    Assert(recoveryCleanup.DeletedCount == 0 && File.Exists(recoveryFile), "其他清理引擎必须拒绝删除恢复文件");
    await DuplicateCenterTests.RunAsync(testRoot, args.Contains("--test-recycle"));
    await MediaAnalysisTests.RunAsync(testRoot);
    await DevelopmentTests.RunAsync(testRoot, args.Contains("--test-recycle"));
    await SoftwareTests.RunAsync(testRoot);
    await NpsProjectTests.RunAsync(testRoot, args.Contains("--test-recycle"));
    await OverviewTests.RunAsync(testRoot);
    await SpaceTreeTests.RunAsync(testRoot, args.Contains("--test-recycle"));
    await ScanEngineTests.RunAsync(testRoot, args.Contains("--million"));

    Console.WriteLine(
        "PASS: 通用规则、精确清理、排除项、变化保护、逻辑/实际占用、重复文件三级哈希和日志均正常。");
    return 0;
}
finally
{
    if (Directory.Exists(testRoot))
    {
        var resolvedTestRoot = Path.GetFullPath(testRoot);
        var resolvedTempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!resolvedTestRoot.StartsWith(resolvedTempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolvedTestRoot).StartsWith("disk-cleanup-assistant-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理临时夹具范围外的目录。");
        // Git creates read-only loose objects. This is limited to the validated fixture root above.
        foreach (var path in Directory.EnumerateFiles(resolvedTestRoot, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        Directory.Delete(resolvedTestRoot, recursive: true);
    }
}

static string CreateFile(string path, int bytes, int ageDays)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new byte[bytes]);
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
    return Path.GetFullPath(path);
}

static string CreateContentFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    return Path.GetFullPath(path);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
