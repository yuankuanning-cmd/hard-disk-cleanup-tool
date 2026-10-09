using ChatGPTCleanupTool.Models;
using System.IO;

namespace ChatGPTCleanupTool.Services;

public sealed class DiskAnalysisService
{
    private readonly IndexedScanEngine _engine;
    public DiskAnalysisService(DiskSnapshotService? snapshots = null, IStorageDeviceProbe? probe = null, IUsnJournalProvider? journal = null)
    {
        var storage = snapshots?.StorageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanupAssistant", "space");
        _engine = new(Path.Combine(storage, "indices-v2"), probe, journal);
    }
    public Task<DiskAnalysisResult> AnalyzeAsync(string rootPath, long minimumFileBytes, bool fastMode = false, int maximumFiles = 200,
        IReadOnlyList<string>? scanScopeRoots = null, CancellationToken cancellationToken = default, IProgress<ScanProgressInfo>? progress = null,
        IReadOnlyList<string>? exclusionPaths = null, ScanEngineOptions? engineOptions = null, ScanSessionControl? control = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFileBytes); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        var warnings = new List<string>(); var roots = new List<string>();
        foreach (var candidate in scanScopeRoots is { Count: > 0 } ? scanScopeRoots : new[] { rootPath })
        {
            var path = NormalizePath(candidate.Trim());
            if (!Directory.Exists(path)) { warnings.Add(path + "：目录不存在"); continue; }
            if (HasReparsePointAncestor(path)) { warnings.Add(path + "：不扫描链接"); continue; }
            if (roots.Any(r => IsWithin(path, r))) continue;
            roots.RemoveAll(r => IsWithin(r, path)); roots.Add(path);
        }
        if (roots.Count == 0) throw new DirectoryNotFoundException("没有可用的扫描目录，请检查路径或白名单。");
        var exclusions = (exclusionPaths ?? []).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return _engine.RunAsync(roots, exclusions, minimumFileBytes, maximumFiles, fastMode, engineOptions ?? new(), control ?? new(), cancellationToken, progress, warnings);
    }
    public Task<DiskAnalysisResult> ReadPausedAsync() => _engine.ReadPausedAsync();
    public Task<DiskAnalysisResult> OpenSnapshotAsync(string path, long minimum = 0) => Task.Run(() => new IndexedNodeSource(path, true).ReadResult(minimum));
    public Task SaveSnapshotAsync(DiskAnalysisResult result, string destination) => Task.Run(() =>
    {
        if (result.IndexPath is null) throw new InvalidOperationException("此结果没有可保存的持久化索引。");
        IndexedNodeSource.SaveSnapshot(result.IndexPath, destination);
    });
    public Task<DiskAnalysisResult> OpenLastAsync(string root, IReadOnlyList<string>? scope, IReadOnlyList<string>? exclusions, bool fast, long minimum = 0)
    {
        var roots = new List<string>();
        foreach (var p in (scope is { Count: > 0 } ? scope : new[] { root }).Select(NormalizePath))
        { if (roots.Any(r => IsWithin(p, r))) continue; roots.RemoveAll(r => IsWithin(r, p)); roots.Add(p); }
        var excluded = (exclusions ?? []).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return OpenSnapshotAsync(_engine.IndexPath(roots, excluded, fast), minimum);
    }
    public static string NormalizePath(string path)
    {
        if (path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') path += Path.DirectorySeparatorChar;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool HasReparsePointAncestor(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }
}
