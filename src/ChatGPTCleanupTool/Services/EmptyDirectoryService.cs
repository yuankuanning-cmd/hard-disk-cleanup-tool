using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

using System.IO;

public sealed class EmptyDirectoryService
{
    public Task<EmptyDirectoryScanResult> AnalyzeAsync(
        string rootPath,
        IReadOnlyList<string>? scanScopeRoots = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(rootPath, scanScopeRoots, cancellationToken), cancellationToken);

    public Task<EmptyDirectoryDeletionResult> DeleteAsync(
        IEnumerable<EmptyDirectoryItem> directories,
        bool dryRun,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Delete(directories.ToArray(), dryRun, cancellationToken), cancellationToken);

    private static EmptyDirectoryScanResult Analyze(
        string rootPath,
        IReadOnlyList<string>? scanScopeRoots,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var activeRoots = ResolveActiveRoots(rootPath, scanScopeRoots, warnings);
        if (activeRoots.Count == 0)
        {
            throw new DirectoryNotFoundException(
                "未找到可用扫描根目录。请检查扫描路径或白名单是否有效。");
        }

        var emptyDirectories = new List<EmptyDirectoryItem>();
        var scannedDirectoryCount = 0;
        var pending = new Stack<string>(activeRoots);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            try
            {
                var attrs = File.GetAttributes(directory);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                AddWarning(warnings, $"{directory}：{ex.Message}");
                continue;
            }

            scannedDirectoryCount++;

            var isEmpty = true;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    isEmpty = false;
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (
                        ex is FileNotFoundException or DirectoryNotFoundException or
                        UnauthorizedAccessException or IOException)
                    {
                        AddWarning(warnings, $"{entry}：{ex.Message}");
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(entry);
                    }
                }
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                AddWarning(warnings, $"{directory}：{ex.Message}");
                continue;
            }

            if (isEmpty)
            {
                var info = new DirectoryInfo(directory);
                emptyDirectories.Add(new EmptyDirectoryItem
                {
                    FullPath = directory,
                    LastWriteTime = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    IsSelected = false
                });
            }
            continue;
        }

        return new EmptyDirectoryScanResult(
            activeRoots[0],
            scannedDirectoryCount,
            emptyDirectories.Count,
            emptyDirectories.OrderBy(dir => dir.FullPath).ToArray(),
            warnings);
    }

    private static EmptyDirectoryDeletionResult Delete(
        IReadOnlyList<EmptyDirectoryItem> directories,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var deleted = 0;
        var failed = 0;
        var deletedLogicalBytes = 0L;

        var selected = directories
            .Where(dir => dir.IsSelected)
            .OrderByDescending(dir => dir.FullPath.Length)
            .ToArray();

        foreach (var dir in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RecoveryFileProtection.EnsureNotManaged(dir.FullPath);
                if (!Directory.Exists(dir.FullPath))
                {
                    failures.Add($"{dir.FullPath}：目录不存在");
                    failed++;
                    continue;
                }

                var isNotEmpty = false;
                foreach (var _ in Directory.EnumerateFileSystemEntries(dir.FullPath))
                {
                    isNotEmpty = true;
                    break;
                }
                if (isNotEmpty)
                {
                    failures.Add($"{dir.FullPath}：当前不为空，已跳过");
                    failed++;
                    continue;
                }

                if (dryRun)
                {
                    deleted++;
                    deletedLogicalBytes += 1;
                    continue;
                }

                Directory.Delete(dir.FullPath);
                deleted++;
                deletedLogicalBytes += 1;
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or NotSupportedException or
                DirectoryNotFoundException or InvalidOperationException)
            {
                failures.Add($"{dir.FullPath}：{ex.Message}");
                failed++;
            }
        }

        return new EmptyDirectoryDeletionResult(deleted, failed, deletedLogicalBytes, failures);
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

        throw new DirectoryNotFoundException($"未找到空目录扫描目录：{Path.GetFullPath(requestedRoot)}");
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 50)
        {
            warnings.Add(warning);
        }
    }
}
