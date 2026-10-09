using ChatGPTCleanupTool.Models;
using Microsoft.VisualBasic.FileIO;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace ChatGPTCleanupTool.Services;

public sealed class CodexSmartCleanupService
{
    private const int SampleBytes = 64 * 1024;
    private static readonly EnumerationOptions ScanEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };
    private readonly FileHashCacheService _hashCache;

    public CodexSmartCleanupService(
        string? codexRoot = null,
        FileHashCacheService? hashCache = null)
    {
        _ = Path.GetFullPath(
            codexRoot ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        _hashCache = hashCache ?? new FileHashCacheService();
    }

    public Task<CodexSmartAnalysisResult> AnalyzeAsync(
        string rootPath,
        int olderThanDays,
        long minimumBytes,
        bool detectDuplicates = false,
        bool fastMode = false,
        IReadOnlyList<string>? scanScopeRoots = null,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgressInfo>? progress = null) =>
        Task.Run(
            () => Analyze(
                rootPath,
                olderThanDays,
                minimumBytes,
                detectDuplicates,
                fastMode,
                scanScopeRoots,
                cancellationToken,
                progress),
            cancellationToken);

    public Task<CodexSmartDeletionResult> DeleteToRecycleBinAsync(
        IEnumerable<CodexSmartFileItem> files,
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => DeleteToRecycleBin(files.ToArray(), dryRun, cancellationToken),
            cancellationToken);

    private CodexSmartAnalysisResult Analyze(
        string rootPath,
        int olderThanDays,
        long minimumBytes,
        bool detectDuplicates,
        bool fastMode,
        IReadOnlyList<string>? scanScopeRoots,
        CancellationToken cancellationToken,
        IProgress<ScanProgressInfo>? progress)
    {
        if (olderThanDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThanDays));
        }

        if (minimumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumBytes));
        }

        var now = DateTime.UtcNow;
        var warnings = new List<string>();
        warnings.Add("此页仅按年龄/体积排序待核实文件，不证明可删除；需要清理时请到开发环境中心检查任务归属、引用和 Git 状态。");
        var reporter = new ScanProgressReporter(progress);
        reporter.SetStage("正在枚举 Codex 文件");
        var activeRoots = ResolveActiveRoots(rootPath, scanScopeRoots, warnings);
        if (activeRoots.Count == 0)
        {
            throw new DirectoryNotFoundException(
                "未找到可用扫描根目录。请检查扫描路径或白名单是否有效。");
        }

        var pending = new Stack<(string ScanRoot, string Directory)>();
        foreach (var activeRoot in activeRoots)
        {
            pending.Push((activeRoot, activeRoot));
        }

        var scoredCandidates = new List<ScoredCandidate>();
        var scannedFiles = 0;
        var scannedLogicalBytes = 0L;
        var scannedAllocatedBytes = 0L;
        var estimatedReclaimBytes = 0L;
        var root = activeRoots[0];

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = pending.Pop();
            var scanRoot = state.ScanRoot;
            var directory = state.Directory;
            reporter.Directory(directory);
            try
            {
                foreach (var entryInfo in new DirectoryInfo(directory)
                             .EnumerateFileSystemInfos("*", ScanEnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = entryInfo.FullName;
                    FileAttributes attributes;
                    try
                    {
                        attributes = entryInfo.Attributes;
                    }
                    catch (Exception ex) when (
                        ex is UnauthorizedAccessException or IOException or
                        FileNotFoundException or DirectoryNotFoundException)
                    {
                        AddWarning(warnings, $"{entry}：{ex.Message}");
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!IsTraversalAllowed(entry))
                        {
                            continue;
                        }

                        pending.Push((scanRoot, entry));
                        continue;
                    }

                    try
                    {
                        var file = entryInfo as FileInfo ?? new FileInfo(entry);
                        reporter.File(file.FullName, file.Length);
                        if (file.Length < minimumBytes)
                        {
                            continue;
                        }

                        scannedFiles++;
                        scannedLogicalBytes = checked(scannedLogicalBytes + file.Length);
                        var storage = fastMode
                            ? new FileStorageInfo(file.Length, file.FullName, 1)
                            : WindowsFileStorageService.GetFileStorageInfo(file.FullName);
                        scannedAllocatedBytes = checked(scannedAllocatedBytes + storage.AllocatedBytes);

                        var recommendation = EvaluateCandidate(scanRoot, file, storage, now, olderThanDays);
                        if (recommendation.Score < 35 && file.Length < 100L * 1024L * 1024L)
                        {
                            continue;
                        }

                        var candidate = new ScoredCandidate(
                            recommendation.Group,
                            file.FullName,
                            file.Length,
                            storage.AllocatedBytes,
                            CleanupService.FormatBytes(file.Length),
                            CleanupService.FormatBytes(storage.AllocatedBytes),
                            recommendation.Recommendation,
                            recommendation.Score,
                            recommendation.Reason,
                            file.LastWriteTime,
                            file.LastAccessTime,
                            recommendation.IsSafeToDelete,
                            file.Extension?.ToLowerInvariant() ?? string.Empty,
                            GetFolderHint(scanRoot, file.FullName),
                            storage.Identity);

                        if (recommendation.IsSafeToDelete)
                        {
                            estimatedReclaimBytes = checked(estimatedReclaimBytes + storage.AllocatedBytes);
                        }

                        scoredCandidates.Add(candidate);
                    }
                    catch (Exception ex) when (
                        ex is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        AddWarning(warnings, $"{entry}：{ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                AddWarning(warnings, $"{directory}：{ex.Message}");
                continue;
            }
        }

        var duplicateGroups = detectDuplicates
            ? FindDuplicateGroups(scoredCandidates, cancellationToken, warnings, reporter)
            : new Dictionary<string, (int Group, int Count)>(StringComparer.OrdinalIgnoreCase);

        var items = scoredCandidates
            .Select(candidate =>
            {
                var duplicate = duplicateGroups.GetValueOrDefault(candidate.FullPath);
                return new CodexSmartFileItem
                {
                    Group = candidate.Priority,
                    FullPath = candidate.FullPath,
                    LogicalBytes = candidate.LogicalBytes,
                    AllocatedBytes = candidate.AllocatedBytes,
                    LogicalSize = candidate.LogicalSize,
                    AllocatedSize = candidate.AllocatedSize,
                    Recommendation = candidate.Recommendation,
                    RecommendationScore = candidate.Score,
                    RecommendationReason = candidate.Reason,
                    LastWriteTime = candidate.LastWriteTime,
                    LastAccessTime = candidate.LastAccessTime,
                    Extension = candidate.Extension,
                    FolderHint = candidate.FolderHint,
                    DuplicateGroup = duplicate.Group,
                    DuplicateCount = duplicate.Count,
                    IsSafeToDelete = candidate.IsSafeToDelete
                };
            })
            .OrderByDescending(item => item.RecommendationScore)
            .ThenByDescending(item => item.AllocatedBytes)
            .ToArray();

        if (!_hashCache.TrySave(out var cacheError))
        {
            AddWarning(warnings, $"哈希缓存保存失败：{cacheError}");
        }

        reporter.Complete(root);
        return new CodexSmartAnalysisResult(
            root,
            scannedFiles,
            items.Length,
            scannedLogicalBytes,
            scannedAllocatedBytes,
            estimatedReclaimBytes,
            items,
            warnings);
    }

    private Dictionary<string, (int Group, int Count)> FindDuplicateGroups(
        IReadOnlyList<ScoredCandidate> candidates,
        CancellationToken cancellationToken,
        List<string> warnings,
        ScanProgressReporter reporter)
    {
        var duplicateGroups = new Dictionary<string, (int Group, int Count)>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count < 2)
        {
            return duplicateGroups;
        }

        var sameSize = candidates
            .GroupBy(candidate => candidate.LogicalBytes)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (sameSize.Length == 0)
        {
            return duplicateGroups;
        }

        var sampled = new List<(ScoredCandidate Candidate, string SampleHash)>();
        reporter.SetStage("正在计算 Codex 文件快速指纹");
        foreach (var sizeGroup in sameSize)
        {
            foreach (var candidate in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    reporter.Current(candidate.FullPath);
                    var modifiedUtc = candidate.LastWriteTime.ToUniversalTime();
                    if (!_hashCache.TryGetSample(
                            candidate.FullPath, candidate.LogicalBytes, modifiedUtc, out var sampleHash))
                    {
                        sampleHash = ComputeSampleHash(candidate.FullPath, candidate.LogicalBytes);
                        _hashCache.StoreSample(
                            candidate.FullPath, candidate.LogicalBytes, modifiedUtc, sampleHash);
                    }

                    sampled.Add((candidate, sampleHash));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddWarning(warnings, $"{candidate.FullPath}：{ex.Message}");
                }
            }
        }

        var fullyHashed = new List<(ScoredCandidate Candidate, string FullHash)>();
        reporter.SetStage("正在完整校验 Codex 重复内容");
        foreach (var sampleGroup in sampled
                     .GroupBy(item => (item.Candidate.LogicalBytes, item.SampleHash))
                     .Where(group => group.Count() > 1))
        {
            foreach (var item in sampleGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    reporter.Current(item.Candidate.FullPath);
                    var modifiedUtc = item.Candidate.LastWriteTime.ToUniversalTime();
                    if (!_hashCache.TryGetFull(
                            item.Candidate.FullPath,
                            item.Candidate.LogicalBytes,
                            modifiedUtc,
                            out var fullHash))
                    {
                        fullHash = ComputeFullHash(item.Candidate.FullPath, cancellationToken);
                        _hashCache.StoreFull(
                            item.Candidate.FullPath,
                            item.Candidate.LogicalBytes,
                            modifiedUtc,
                            fullHash);
                    }

                    fullyHashed.Add((item.Candidate, fullHash));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddWarning(warnings, $"{item.Candidate.FullPath}：{ex.Message}");
                }
            }
        }

        var groupNumber = 0;
        foreach (var hashGroup in fullyHashed
                     .GroupBy(item => (item.Candidate.LogicalBytes, item.FullHash))
                     .Where(group => group.Select(item => item.Candidate.Identity)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Count() > 1)
                     .OrderByDescending(group => group.Key.LogicalBytes))
        {
            var files = hashGroup
                .GroupBy(item => item.Candidate.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            if (files.Length <= 1)
            {
                continue;
            }

            groupNumber++;
            foreach (var item in files)
            {
                duplicateGroups[item.Candidate.FullPath] = (groupNumber, files.Length);
            }
        }

        return duplicateGroups;
    }

    private static List<string> ResolveActiveRoots(
        string requestedRoot,
        IReadOnlyList<string>? scanScopeRoots,
        List<string> warnings)
    {
        var roots = new List<string>();
        var candidates = (scanScopeRoots is { Count: > 0 })
            ? scanScopeRoots
            : new[] { requestedRoot };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var normalized = Path.GetFullPath(candidate.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(normalized))
                {
                    AddWarning(warnings, $"{normalized}：目录不存在");
                    continue;
                }

                if (File.GetAttributes(normalized).HasFlag(FileAttributes.ReparsePoint))
                {
                    AddWarning(warnings, $"{normalized}：不允许扫描链接或重解析点");
                    continue;
                }

                if (!roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(normalized);
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                AddWarning(warnings, $"{candidate}：{ex.Message}");
            }
        }

        if (roots.Count > 0)
        {
            return roots;
        }

        if (scanScopeRoots is not null && scanScopeRoots.Count > 0)
        {
            return [];
        }

        throw new DirectoryNotFoundException($"找不到要分析的 Codex 目录：{Path.GetFullPath(requestedRoot)}");
    }

    private static CodexSmartDeletionResult DeleteToRecycleBin(
        IReadOnlyList<CodexSmartFileItem> files,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var deleted = 0;
        var reclaimed = 0L;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.IsSafeToDelete || RecoveryFileProtection.IsManagedPath(file.FullPath))
            {
                failures.Add($"{file.FullPath}：当前策略标记为不建议删除");
                continue;
            }

            try
            {
                var info = new FileInfo(file.FullPath);
                if (!info.Exists || info.Length != file.LogicalBytes)
                {
                    failures.Add($"{file.FullPath}：文件已变化，跳过");
                    continue;
                }

                if (!dryRun)
                {
                    FileSystem.DeleteFile(
                        file.FullPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin);
                }

                deleted++;
                reclaimed += file.AllocatedBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{file.FullPath}：{ex.Message}");
            }
        }

        return new CodexSmartDeletionResult(deleted, reclaimed, failures);
    }

    private static CandidateRecommendation EvaluateCandidate(
        string root,
        FileInfo info,
        FileStorageInfo storage,
        DateTime nowUtc,
        int olderThanDays)
    {
        var score = 10;
        var reasons = new List<string>();
        var extension = (info.Extension ?? string.Empty).ToLowerInvariant();
        var path = info.FullName;
        var relative = Path.GetRelativePath(root, path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToLowerInvariant();
        var lastAccessAgeDays = (nowUtc - info.LastAccessTimeUtc).TotalDays;
        var lastWriteAgeDays = (nowUtc - info.LastWriteTimeUtc).TotalDays;

        if (lastAccessAgeDays > olderThanDays)
        {
            score += 40;
            reasons.Add($"已超过 {olderThanDays} 天未访问");
        }
        else if (lastAccessAgeDays > olderThanDays / 2.0)
        {
            score += 20;
        }

        if (lastWriteAgeDays > olderThanDays)
        {
            score += 20;
            reasons.Add("很久未修改");
        }

        if (relative.Contains("\\.tmp\\") || relative.Contains("\\tmp\\"))
        {
            score += 20;
            reasons.Add("位于临时目录");
        }
        if (relative.Contains("\\cache\\"))
        {
            score += 18;
            reasons.Add("位于缓存目录");
        }
        if (relative.Contains("\\generated_images\\") || relative.Contains("\\visualizations\\"))
        {
            score += 16;
            reasons.Add("可能是唯一生成成果，不能证明可重建");
        }
        if (relative.Contains("\\archived_sessions\\") || relative.Contains("\\sessions\\"))
        {
            score += 5;
        }

        if (relative.Contains("\\.chatgpt-projects\\") ||
            relative.Contains("\\workspace\\") ||
            relative.Contains("\\project\\"))
        {
            score -= 6;
            reasons.Add("可能是工作区/项目副本");
        }

        if (extension is ".log" or ".tmp" or ".temp" or ".bin")
        {
            score += 20;
            reasons.Add("临时或日志文件特征");
        }

        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" ||
            extension is ".mp4" or ".mov" or ".webm")
        {
            score += 14;
            reasons.Add("多媒体/图片文件常为生成产物");
        }

        if (storage.AllocatedBytes > 200L * 1024L * 1024L)
        {
            score += 16;
            reasons.Add("单文件体积大");
        }
        else if (storage.AllocatedBytes > 50L * 1024L * 1024L)
        {
            score += 8;
            reasons.Add("体积较大");
        }

        if (extension is ".cs" or ".csproj" or ".sln" or ".md" or ".txt" or ".json" ||
            extension is ".yml" or ".yaml" or ".py" or ".js" or ".tsx" or ".ts")
        {
            score -= 15;
            reasons.Add("可能是源码/文档，通常保留");
        }

        if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            score -= 10;
            reasons.Add("只读文件，通常保留");
        }

        if (storage.AllocatedBytes > 500L * 1024L * 1024L)
        {
            score += 6;
            reasons.Add("超大占用文件");
        }

        score = Math.Clamp(score, 0, 100);
        // This legacy score measures review priority only, not usefulness or task ownership.
        // Cleanup now requires the development center's explicit provenance and Git checks.
        var isSafeToDelete = false;

        var group = score >= 80
            ? "高优先"
            : score >= 65
                ? "中高优先"
                : "中优先";
        var recommendation = "待核实归属 · 保留";
        reasons.Add("年龄和大小仅用于排查排序，不授权删除；请到开发环境中心核实");
        if (reasons.Count == 0)
        {
            reasons.Add("缺少强相关特征，建议确认后再处理");
        }

        return new CandidateRecommendation(
            group,
            recommendation,
            score,
            string.Join("；", reasons),
            isSafeToDelete);
    }

    private static string GetFolderHint(string root, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(root, fullPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (relative.StartsWith("..", StringComparison.OrdinalIgnoreCase))
            {
                return "未知区域";
            }

            var separatorIndex = relative.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            return separatorIndex <= 0
                ? "根目录"
                : relative[..separatorIndex];
        }
        catch
        {
            return "未知目录";
        }
    }

    private static string ComputeSampleHash(string path, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = OpenRead(path);
        var bufferLength = (int)Math.Min(SampleBytes, Math.Max(1, length));
        var buffer = new byte[bufferLength];
        var read = stream.Read(buffer);
        hash.AppendData(buffer, 0, read);

        if (length > SampleBytes)
        {
            stream.Seek(Math.Max(0, length - SampleBytes), SeekOrigin.Begin);
            read = stream.Read(buffer);
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeFullHash(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = OpenRead(path);
        var buffer = new byte[1024 * 1024];

        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        1024 * 1024,
        FileOptions.SequentialScan);

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 50)
        {
            warnings.Add(warning);
        }
    }

    private static bool IsTraversalAllowed(string path)
    {
        var name = Path.GetFileName(path);
        return !string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(name, ".svn", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(name, "vendor", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CandidateRecommendation(
        string Group,
        string Recommendation,
        int Score,
        string Reason,
        bool IsSafeToDelete);

    private sealed record ScoredCandidate(
        string Priority,
        string FullPath,
        long LogicalBytes,
        long AllocatedBytes,
        string LogicalSize,
        string AllocatedSize,
        string Recommendation,
        int Score,
        string Reason,
        DateTime LastWriteTime,
        DateTime LastAccessTime,
        bool IsSafeToDelete,
        string Extension,
        string FolderHint,
        string Identity);
}
