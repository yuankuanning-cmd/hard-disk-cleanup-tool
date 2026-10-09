using ChatGPTCleanupTool.Models;
using System.IO;
using System.IO.Enumeration;

namespace ChatGPTCleanupTool.Services;

public sealed class CleanupService
{
    private readonly string _codexRoot;
    private readonly string _tempRoot;
    private readonly string _localAppData;
    private readonly string _programData;

    public CleanupService(
        string? codexRoot = null,
        string? tempRoot = null,
        string? localAppData = null,
        string? programData = null)
    {
        _codexRoot = NormalizePath(codexRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex"));
        _tempRoot = NormalizePath(tempRoot ?? Path.GetTempPath());
        _localAppData = NormalizePath(localAppData ?? Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));
        _programData = NormalizePath(programData ?? Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData));
    }

    public string CodexRoot => _codexRoot;

    public Task<IReadOnlyList<CleanupCategoryItem>> ScanAsync(
        int olderThanDays,
        CancellationToken cancellationToken = default) =>
        ScanAsync(new CleanupScanOptions(olderThanDays), cancellationToken);

    public Task<IReadOnlyList<CleanupCategoryItem>> ScanAsync(
        CleanupScanOptions options,
        IReadOnlyList<string>? scanScopeRoots = null,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgressInfo>? progress = null) =>
        Task.Run(() => Scan(options, scanScopeRoots, cancellationToken, progress), cancellationToken);

    public Task<IReadOnlyList<CleanupCategoryItem>> ScanAsync(
        CleanupScanOptions options,
        CancellationToken cancellationToken) =>
        ScanAsync(options, null, cancellationToken);

    public Task<CleanupResult> CleanupAsync(
        IEnumerable<string> categoryIds,
        int olderThanDays,
        CancellationToken cancellationToken = default) =>
        CleanupAsync(
            categoryIds,
            new CleanupScanOptions(olderThanDays),
            [],
            null,
            false,
            cancellationToken);

    public Task<CleanupResult> CleanupAsync(
        IEnumerable<string> categoryIds,
        CleanupScanOptions options,
        IEnumerable<string> excludedPaths,
        CancellationToken cancellationToken = default) =>
        CleanupAsync(categoryIds, options, excludedPaths, null, false, cancellationToken);

    public Task<CleanupResult> CleanupAsync(
        IEnumerable<string> categoryIds,
        CleanupScanOptions options,
        IEnumerable<string> excludedPaths,
        IReadOnlyList<string>? scanScopeRoots,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var selectedIds = categoryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exclusions = excludedPaths
            .Select(TryNormalizePath)
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Task.Run(
            () => Cleanup(selectedIds, options, exclusions, scanScopeRoots, dryRun, cancellationToken),
            cancellationToken);
    }

    public Task<CleanupResult> CleanupFilesAsync(
        IEnumerable<CleanupFileItem> files,
        CleanupScanOptions options,
        bool dryRun = false,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? protectedPaths = null)
    {
        var plan = files.ToArray();
        return Task.Run(
            () => CleanupFiles(plan, options, dryRun, cancellationToken, protectedPaths ?? []),
            cancellationToken);
    }

    public Task<CleanupResult> CleanupFilesAsync(
        IEnumerable<CleanupFileItem> files,
        CleanupScanOptions options,
        CancellationToken cancellationToken) =>
        CleanupFilesAsync(files, options, false, cancellationToken);

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):N2} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):N2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):N2} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):N2} KB";
        return $"{bytes} B";
    }

    private IReadOnlyList<CleanupCategoryItem> Scan(
        CleanupScanOptions options,
        IReadOnlyList<string>? scanScopeRoots,
        CancellationToken cancellationToken,
        IProgress<ScanProgressInfo>? progress)
    {
        ValidateOptions(options);
        var reporter = new ScanProgressReporter(progress);
        var thresholdUtc = DateTime.UtcNow.AddDays(-options.OlderThanDays);
        var results = new List<CleanupCategoryItem>();
        var assignedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in CreateDefinitions(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reporter.SetStage($"正在扫描：{definition.Name}");
            var files = EnumerateCandidates(
                    definition, thresholdUtc, scanScopeRoots, cancellationToken, reporter)
                .Where(candidate => assignedFiles.Add(candidate.FullPath))
                .Select(candidate => new CleanupFileItem
                {
                    CategoryId = definition.Id,
                    Root = candidate.Root,
                    FullPath = candidate.FullPath,
                    Name = Path.GetFileName(candidate.FullPath),
                    Bytes = candidate.Length,
                    DisplaySize = FormatBytes(candidate.Length),
                    LastWriteTime = candidate.LastWriteTimeUtc.ToLocalTime()
                })
                .OrderByDescending(file => file.Bytes)
                .ToList();
            var bytes = files.Sum(file => file.Bytes);

            results.Add(new CleanupCategoryItem
            {
                Id = definition.Id,
                Group = definition.Group,
                Name = definition.Name,
                Description = definition.Description,
                Risk = definition.Risk,
                DefaultSelected = definition.DefaultSelected,
                IsSelected = definition.DefaultSelected && files.Count > 0,
                FileCount = files.Count,
                Bytes = bytes,
                DisplaySize = FormatBytes(bytes),
                Files = files
            });
        }

        reporter.Complete();
        return results;
    }

    private CleanupResult Cleanup(
        HashSet<string> categoryIds,
        CleanupScanOptions options,
        HashSet<string> excludedPaths,
        IReadOnlyList<string>? scanScopeRoots,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        var thresholdUtc = DateTime.UtcNow.AddDays(-options.OlderThanDays);
        var deletedFiles = new List<DeletedFile>();
        var failures = new List<CleanupFailure>();
        var candidateRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var handledFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in CreateDefinitions(options).Where(item => categoryIds.Contains(item.Id)))
        {
            foreach (var candidate in EnumerateCandidates(
                         definition,
                         thresholdUtc,
                         scanScopeRoots,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (RecoveryFileProtection.IsManagedPath(candidate.FullPath) || !handledFiles.Add(candidate.FullPath) ||
                    excludedPaths.Any(p => DiskAnalysisService.IsWithin(candidate.FullPath, p)))
                {
                    continue;
                }

                if (!IsInside(candidate.FullPath, candidate.Root))
                {
                    failures.Add(new CleanupFailure(candidate.FullPath, "路径安全检查未通过"));
                    continue;
                }

                try
                {
                    if (!dryRun)
                    {
                        File.Delete(candidate.FullPath);
                    }

                    deletedFiles.Add(new DeletedFile(
                        definition.Id,
                        candidate.FullPath,
                        candidate.Length));
                    candidateRoots.Add(candidate.Root);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    failures.Add(new CleanupFailure(candidate.FullPath, ex.Message));
                }
            }
        }

        foreach (var root in candidateRoots)
        {
            if (!dryRun) RemoveEmptyChildDirectories(root, excludedPaths.ToArray());
        }

        return new CleanupResult(deletedFiles, failures);
    }

    private CleanupResult CleanupFiles(
        IReadOnlyList<CleanupFileItem> plan,
        CleanupScanOptions options,
        bool dryRun,
        CancellationToken cancellationToken,
        IReadOnlyList<string> protectedPaths)
    {
        ValidateOptions(options);
        var definitions = CreateDefinitions(options)
            .ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);
        var deletedFiles = new List<DeletedFile>();
        var failures = new List<CleanupFailure>();
        var candidateRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var handledFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsExcluded || RecoveryFileProtection.IsManagedPath(item.FullPath) || ReferenceDirectoryService.Contains(item.FullPath, protectedPaths) || !handledFiles.Add(item.FullPath))
            {
                continue;
            }

            if (!definitions.TryGetValue(item.CategoryId, out var definition) ||
                !IsAuthorizedPlanItem(item, definition))
            {
                failures.Add(new CleanupFailure(item.FullPath, "清理计划的路径授权检查未通过"));
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(item.FullPath);
                if (attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    failures.Add(new CleanupFailure(item.FullPath, "对象类型在扫描后发生变化"));
                    continue;
                }

                var current = new FileInfo(item.FullPath);
                var timeDifference = Math.Abs(
                    (current.LastWriteTimeUtc - item.LastWriteTime.ToUniversalTime()).TotalSeconds);
                if (current.Length != item.Bytes || timeDifference > 2)
                {
                    failures.Add(new CleanupFailure(item.FullPath, "文件在扫描后发生变化，已跳过"));
                    continue;
                }

                if (!dryRun)
                {
                    File.Delete(item.FullPath);
                }

                deletedFiles.Add(new DeletedFile(item.CategoryId, item.FullPath, item.Bytes));
                candidateRoots.Add(item.Root);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add(new CleanupFailure(item.FullPath, ex.Message));
            }
        }

        foreach (var root in candidateRoots)
        {
            if (!dryRun) RemoveEmptyChildDirectories(root, protectedPaths);
        }

        return new CleanupResult(deletedFiles, failures);
    }

    private static bool IsAuthorizedPlanItem(
        CleanupFileItem item,
        CleanupRuleDefinition definition)
    {
        var itemRoot = TryNormalizePath(item.Root);
        var itemPath = TryNormalizePath(item.FullPath);
        if (itemRoot is null || itemPath is null || !IsInside(itemPath, itemRoot))
        {
            return false;
        }

        if (definition.SearchDirectoryNames.Count == 0)
        {
            return definition.Paths
                .Select(TryNormalizePath)
                .Any(path => path is not null && IsInside(itemRoot, path));
        }

        return definition.Paths
                   .Select(TryNormalizePath)
                   .Any(path => path is not null && IsInside(itemRoot, path)) &&
               definition.SearchDirectoryNames.Contains(
                   Path.GetFileName(itemRoot),
                   StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<CandidateFile> EnumerateCandidates(
        CleanupRuleDefinition definition,
        DateTime thresholdUtc,
        IReadOnlyList<string>? scanScopeRoots,
        CancellationToken cancellationToken,
        ScanProgressReporter? reporter = null)
    {
        var roots = definition.SearchDirectoryNames.Count == 0
            ? ResolveDefinitionRoots(definition.Paths, scanScopeRoots)
            : EnumerateMatchingDirectories(
                definition.Paths,
                definition.SearchDirectoryNames,
                cancellationToken,
                reporter).ToArray();

        if (scanScopeRoots is { Count: > 0 })
        {
            roots = ResolveDefinitionRoots(roots, scanScopeRoots).ToArray();
        }

        foreach (var configuredPath in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = TryNormalizePath(configuredPath);
            if (root is null)
            {
                continue;
            }

            FileAttributes rootAttributes;
            try
            {
                rootAttributes = File.GetAttributes(root);
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or DirectoryNotFoundException or
                UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (!rootAttributes.HasFlag(FileAttributes.Directory))
            {
                var info = TryCreateCandidate(root, root, thresholdUtc, definition.FilePatterns);
                if (info is not null)
                {
                    reporter?.File(info.FullPath, info.Length);
                    yield return info;
                }

                continue;
            }

            foreach (var candidate in EnumerateFiles(
                root,
                thresholdUtc,
                definition.FilePatterns,
                cancellationToken,
                reporter))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateMatchingDirectories(
        IReadOnlyList<string> searchRoots,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken,
        ScanProgressReporter? reporter = null)
    {
        var nameSet = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredRoot in searchRoots)
        {
            var root = TryNormalizePath(configuredRoot);
            if (root is null || !Directory.Exists(root))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                reporter?.Directory(directory);
                foreach (var child in EnumerateDirectoriesSafely(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSafeDirectory(child, root))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(child);
                    if (nameSet.Contains(name))
                    {
                        yield return child;
                        continue;
                    }

                    if (!IsTraversalExcluded(name))
                    {
                        pending.Push(child);
                    }
                }
            }
        }
    }

    private static IReadOnlyList<string> ResolveDefinitionRoots(
        IEnumerable<string> configuredRoots,
        IReadOnlyList<string>? scanScopeRoots)
    {
        var configured = configuredRoots
            .Select(TryNormalizePath)
            .Where(path => path is not null)
            .Cast<string>()
            .ToList();

        if (scanScopeRoots is null or { Count: 0 })
        {
            return configured;
        }

        var scopes = scanScopeRoots
            .Select(TryNormalizePath)
            .Where(path => path is not null)
            .Cast<string>()
            .ToList();
        if (scopes.Count == 0)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredPath in configured)
        {
            foreach (var scope in scopes)
            {
                if (IsInside(scope, configuredPath))
                {
                    result.Add(configuredPath);
                }
                else if (IsInside(configuredPath, scope))
                {
                    result.Add(scope);
                }
            }
        }

        return result.ToList();
    }

    private static List<CandidateFile> EnumerateFiles(
        string root,
        DateTime thresholdUtc,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken,
        ScanProgressReporter? reporter = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var candidates = new List<CandidateFile>();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            reporter?.Directory(directory);
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (
                        ex is FileNotFoundException or DirectoryNotFoundException or
                        UnauthorizedAccessException or IOException)
                    {
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.ReparsePoint) || !IsInside(entry, root))
                    {
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(entry);
                        continue;
                    }

                    var candidate = TryCreateCandidate(root, entry, thresholdUtc, patterns);
                    if (candidate is not null)
                    {
                        reporter?.File(candidate.FullPath, candidate.Length);
                        candidates.Add(candidate);
                    }
                }
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                continue;
            }
        }

        return candidates;
    }

    private static CandidateFile? TryCreateCandidate(
        string root,
        string path,
        DateTime thresholdUtc,
        IReadOnlyList<string> patterns)
    {
        try
        {
            if (patterns.Count > 0 &&
                !patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(
                    pattern,
                    Path.GetFileName(path),
                    ignoreCase: true)))
            {
                return null;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.LastWriteTimeUtc >= thresholdUtc)
            {
                return null;
            }

            return new CandidateFile(
                root,
                fileInfo.FullName,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or UnauthorizedAccessException or
            IOException or NotSupportedException)
        {
            return null;
        }
    }

    private IReadOnlyList<CleanupRuleDefinition> CreateDefinitions(CleanupScanOptions options)
    {
        var rules = new List<CleanupRuleDefinition>
        {
            Rule(
                "codex-temporary",
                "Codex",
                "Codex 临时文件",
                "临时目录也可能包含唯一补丁/成果。年龄不证明无用；建议到开发环境中心检查归属。",
                "高",
                false,
                [
                    Path.Combine(_codexRoot, ".tmp"),
                    Path.Combine(_codexRoot, "tmp"),
                    Path.Combine(_tempRoot, "codex")
                ]),
            Rule(
                "codex-cache",
                "Codex",
                "Codex 下载与运行缓存",
                "缓存名称不证明可重建；请检查任务归属及来源后人工确认。",
                "中",
                false,
                [Path.Combine(_codexRoot, "cache")]),
            Rule(
                "codex-visualizations",
                "Codex",
                "Codex 可视化产物",
                "图表和报告预览文件，删除后旧任务中的预览可能无法打开。",
                "中",
                false,
                [Path.Combine(_codexRoot, "visualizations")]),
            Rule(
                "codex-generated-images",
                "Codex",
                "Codex 生成图片",
                "请先保存仍需使用的生成图片。",
                "中",
                false,
                [Path.Combine(_codexRoot, "generated_images")]),
            Rule(
                "codex-archived-sessions",
                "Codex",
                "Codex 已归档会话",
                "清理后可能无法恢复这些任务的完整本地内容。",
                "高",
                false,
                [Path.Combine(_codexRoot, "archived_sessions")]),
            Rule(
                "codex-old-sessions",
                "Codex",
                "Codex 历史会话",
                "只应在确认不再需要历史任务时选择。",
                "高",
                false,
                [Path.Combine(_codexRoot, "sessions")]),
            Rule(
                "codex-project-copies",
                "Codex",
                "Codex 项目工作副本",
                "清理前请确认原项目文件已妥善保存。",
                "高",
                false,
                [Path.Combine(_codexRoot, ".chatgpt-projects")]),
            Rule(
                "windows-temp",
                "Windows",
                "用户临时文件",
                "当前用户临时目录中未在使用的旧文件。",
                "低",
                true,
                [_tempRoot]),
            Rule(
                "windows-crash-dumps",
                "Windows",
                "崩溃转储与错误报告",
                "程序崩溃诊断文件；如近期无需排查故障可清理。",
                "低",
                true,
                [
                    Path.Combine(_localAppData, "CrashDumps"),
                    Path.Combine(_programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(_programData, "Microsoft", "Windows", "WER", "ReportQueue")
                ]),
            Rule(
                "windows-shader-cache",
                "Windows",
                "DirectX 着色器缓存",
                "游戏和图形应用可以重新生成，首次启动可能稍慢。",
                "低",
                true,
                [Path.Combine(_localAppData, "D3DSCache")]),
            Rule(
                "windows-thumbnail-cache",
                "Windows",
                "缩略图缓存",
                "资源管理器会重新生成；正在使用的缓存文件会自动跳过。",
                "低",
                false,
                [Path.Combine(_localAppData, "Microsoft", "Windows", "Explorer")],
                filePatterns: ["thumbcache_*.db"]),
            Rule(
                "chrome-cache",
                "浏览器缓存",
                "Chrome 缓存",
                "浏览器离线缓存和缓存脚本，建议在关闭浏览器后清理。",
                "低",
                false,
                [Path.Combine(_localAppData, "Google", "Chrome", "User Data")],
                searchDirectoryNames:
                [
                    "Cache", "Code Cache", "Application Cache", "GPUCache",
                    "Service Worker", "Service Worker\\CacheStorage"
                ]),
            Rule(
                "edge-cache",
                "浏览器缓存",
                "Edge 缓存",
                "浏览器本地缓存与离线资源，建议在退出 Edge 后清理。",
                "低",
                false,
                [Path.Combine(_localAppData, "Microsoft", "Edge", "User Data")],
                searchDirectoryNames: ["Cache", "Code Cache", "DIPS", "GPUCache"]),
            Rule(
                "firefox-cache",
                "浏览器缓存",
                "Firefox 缓存",
                "Firefox profiles 下的磁盘缓存。",
                "低",
                false,
                [Path.Combine(_localAppData, "Mozilla", "Firefox", "Profiles")],
                searchDirectoryNames: ["cache2"]),
            Rule(
                "cargo-cache",
                "开发缓存",
                "Cargo 编译缓存与注册表",
                "Cargo 二进制构建产物，重新构建后会自动补齐。",
                "低",
                false,
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "registry"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "git"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rustup", "downloads")
                ]),
            Rule(
                "java-build-cache",
                "开发缓存",
                "Gradle / Maven 本地仓库",
                "语言依赖会在下一次构建时拉取或重新下载。",
                "低",
                false,
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".m2", "repository"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle", "caches"),
                    Path.Combine(_localAppData, "gradle", "caches")
                ]),
            Rule(
                "nuget-cache",
                "开发缓存",
                "NuGet 下载缓存",
                "包含 NuGet HTTP、临时和插件缓存；需要时会重新下载。",
                "低",
                false,
                [
                    Path.Combine(_localAppData, "NuGet", "v3-cache"),
                    Path.Combine(_localAppData, "NuGet", "plugins-cache"),
                    Path.Combine(_tempRoot, "NuGetScratch"),
                    Path.Combine(_tempRoot, "NuGetScratchroot")
                ]),
            Rule(
                "pip-cache",
                "开发缓存",
                "pip 下载与 Wheel 缓存",
                "Python 包缓存，需要时会重新下载或重新构建。",
                "低",
                false,
                [Path.Combine(_localAppData, "pip", "Cache")]),
            Rule(
                "npm-cache",
                "开发缓存",
                "npm 下载缓存",
                "npm 的可再生下载缓存，需要时会重新下载。",
                "低",
                false,
                [Path.Combine(_localAppData, "npm-cache")]),
            Rule(
                "yarn-cache",
                "开发缓存",
                "Yarn 下载缓存",
                "Yarn 的可再生包缓存，需要时会重新下载。",
                "低",
                false,
                [Path.Combine(_localAppData, "Yarn", "Cache")])
        };

        var projectRoot = TryNormalizePath(options.ProjectRoot);
        if (projectRoot is not null && Directory.Exists(projectRoot))
        {
            rules.Add(Rule(
                "dotnet-build-output",
                "开发项目",
                ".NET 与 Visual Studio 构建产物",
                "项目中的 bin、obj、.vs 和测试结果，可通过重新构建生成。",
                "中",
                false,
                [projectRoot],
                searchDirectoryNames: ["bin", "obj", ".vs", "TestResults"]));
            rules.Add(Rule(
                "web-python-build-output",
                "开发项目",
                "前端与 Python 构建缓存",
                "常见框架输出和测试缓存；请确认项目可正常重新构建。",
                "中",
                false,
                [projectRoot],
                searchDirectoryNames:
                [
                    "dist", ".next", ".nuxt", ".angular", "coverage",
                    ".pytest_cache", "__pycache__"
                ]));
            rules.Add(Rule(
                "node-modules",
                "开发项目",
                "Node.js 项目依赖",
                "node_modules 通常可根据锁定文件重新安装，但重新安装可能耗时。",
                "高",
                false,
                [projectRoot],
                searchDirectoryNames: ["node_modules"]));
        }

        return rules;
    }

    private static CleanupRuleDefinition Rule(
        string id,
        string group,
        string name,
        string description,
        string risk,
        bool defaultSelected,
        IReadOnlyList<string> paths,
        IReadOnlyList<string>? searchDirectoryNames = null,
        IReadOnlyList<string>? filePatterns = null) =>
        new(
            id,
            group,
            name,
            description,
            risk,
            defaultSelected,
            paths,
            searchDirectoryNames ?? [],
            filePatterns ?? []);

    private static IEnumerable<string> EnumerateDirectoriesSafely(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static bool IsSafeDirectory(string path, string root)
    {
        try
        {
            return IsInside(path, root) &&
                   !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTraversalExcluded(string directoryName) =>
        directoryName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(".next", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(".nuxt", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(".angular", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals("coverage", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(".venv", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals("venv", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals("packages", StringComparison.OrdinalIgnoreCase);

    private static void RemoveEmptyChildDirectories(string root, IReadOnlyList<string> protectedPaths)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }
        }
        catch
        {
            return;
        }

        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in EnumerateDirectoriesSafely(directory))
            {
                if (!IsSafeDirectory(child, root) || RecoveryFileProtection.IsManagedPath(child) || protectedPaths.Any(p => DiskAnalysisService.IsWithin(child, p) || DiskAnalysisService.IsWithin(p, child)))
                {
                    continue;
                }

                directories.Add(child);
                pending.Push(child);
            }
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                // 空目录清理失败不影响文件清理结果。
            }
        }
    }

    private static bool IsInside(string childPath, string parentPath)
    {
        var child = NormalizePath(childPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = NormalizePath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = parent + Path.DirectorySeparatorChar;

        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return NormalizePath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void ValidateOptions(CleanupScanOptions options)
    {
        if (options.OlderThanDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "保留天数必须在 1 到 3650 之间。");
        }
    }

    private sealed record CleanupRuleDefinition(
        string Id,
        string Group,
        string Name,
        string Description,
        string Risk,
        bool DefaultSelected,
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> SearchDirectoryNames,
        IReadOnlyList<string> FilePatterns);

    private sealed record CandidateFile(
        string Root,
        string FullPath,
        long Length,
        DateTime LastWriteTimeUtc);
}

public sealed record CleanupScanOptions(int OlderThanDays, string? ProjectRoot = null);

public sealed record DeletedFile(string CategoryId, string Path, long Bytes);

public sealed record CleanupFailure(string Path, string Error);

public sealed record CleanupResult(
    IReadOnlyList<DeletedFile> DeletedFiles,
    IReadOnlyList<CleanupFailure> Failures)
{
    public int DeletedCount => DeletedFiles.Count;
    public long DeletedBytes => DeletedFiles.Sum(file => file.Bytes);
    public string DisplaySize => CleanupService.FormatBytes(DeletedBytes);
    public int FailureCount => Failures.Count;
}
