using ChatGPTCleanupTool.Models;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;

namespace ChatGPTCleanupTool.Services;

public sealed class DuplicateFileService
{
    private const int SampleBytes = 64 * 1024;
    private readonly FileHashCacheService _hashCache;
    private readonly ReferenceDirectoryService _references;
    private readonly DuplicateMutationService _mutations;
    private static readonly EnumerationOptions Enumeration = new() { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
    public DuplicateFileService(FileHashCacheService? hashCache = null, ReferenceDirectoryService? referenceDirectories = null, DuplicateOperationLogService? operationLog = null)
    {
        _hashCache = hashCache ?? new(); _references = referenceDirectories ?? new();
        _mutations = new(_references, operationLog ?? new());
    }
    public Task<DuplicateAnalysisResult> AnalyzeAsync(string rootPath, long minimumBytes, bool fastMode = false,
        IReadOnlyList<string>? scanScopeRoots = null, CancellationToken cancellationToken = default, IProgress<ScanProgressInfo>? progress = null)
        => Task.Run(() => Analyze(rootPath, minimumBytes, fastMode, scanScopeRoots, cancellationToken, progress), cancellationToken);
    public Task<DuplicateDeletionResult> DeleteToRecycleBinAsync(IReadOnlyList<DuplicateFileItem> allFiles, IEnumerable<DuplicateFileItem> files,
        bool fastMode, bool dryRun = false, CancellationToken cancellationToken = default)
        => ApplyActionAsync(allFiles, files, new(DuplicateAction.Recycle, dryRun), cancellationToken: cancellationToken);
    public Task<DuplicateDeletionResult> ApplyActionAsync(IReadOnlyList<DuplicateFileItem> allFiles, IEnumerable<DuplicateFileItem> files,
        DuplicateActionOptions options, IReadOnlyList<string>? exclusions = null, CancellationToken cancellationToken = default)
        => _mutations.ExecuteAsync(allFiles, files, options, exclusions, cancellationToken);

    private DuplicateAnalysisResult Analyze(string rootPath, long minimumBytes, bool fastMode, IReadOnlyList<string>? scope,
        CancellationToken token, IProgress<ScanProgressInfo>? progress)
    {
        if (minimumBytes < 1) throw new ArgumentOutOfRangeException(nameof(minimumBytes));
        var references = _references.GetPaths();
        var warnings = new List<string>(); var reporter = new ScanProgressReporter(progress);
        reporter.SetStage("正在枚举重复候选（包含参考原件目录）");
        var roots = ResolveRoots(rootPath, scope, references, warnings);
        var pending = new Stack<string>(roots); var candidates = new List<Candidate>(); var scanned = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested(); reporter.Directory(directory);
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Enumeration))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.Name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith(".diskcleanup-", StringComparison.OrdinalIgnoreCase)) continue;
                        if (entry.Attributes.HasFlag(FileAttributes.Directory)) { pending.Push(entry.FullName); continue; }
                        var info = (FileInfo)entry; scanned++; reporter.File(info.FullName, info.Length);
                        if (info.Length < minimumBytes || info.Attributes.HasFlag(FileAttributes.Offline)) continue;
                        candidates.Add(new(info.FullName, info.Length, 0, "", 1, info.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (Expected(ex)) { Warn(warnings, entry.FullName + "：" + ex.Message); }
                }
            }
            catch (Exception ex) when (Expected(ex)) { Warn(warnings, directory + "：" + ex.Message); }
        }
        reporter.SetStage("正在计算采样指纹");
        var samples = new List<(Candidate File, string Hash)>();
        foreach (var sizeGroup in candidates.GroupBy(f => f.Logical).Where(g => g.Count() > 1))
        {
            var physical = new List<Candidate>();
            foreach (var candidate in sizeGroup)
            {
                token.ThrowIfCancellationRequested(); reporter.Current(candidate.Path);
                try
                {
                    // Unique sizes cannot be duplicates: avoid opening them for allocation/identity metadata.
                    var storage = WindowsFileStorageService.GetFileStorageInfo(candidate.Path);
                    var enriched = candidate with { Allocated = storage.AllocatedBytes, Identity = storage.Identity, Links = storage.LinkCount };
                    ValidateCandidate(enriched); physical.Add(enriched);
                }
                catch (Exception ex) when (Expected(ex)) { Warn(warnings, candidate.Path + "：" + ex.Message); }
            }
            // Hash one representative per physical file; reference-directory aliases always win.
            var independent = physical.GroupBy(f => f.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(f => ReferenceDirectoryService.Contains(f.Path, references)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First()).ToArray();
            if (independent.Length < 2) continue;
            foreach (var f in independent)
            {
                token.ThrowIfCancellationRequested(); reporter.Current(f.Path);
                try
                {
                    ValidateCandidate(f);
                    if (!_hashCache.TryGetSample(f.Path, f.Logical, f.Written, out var hash, f.Identity))
                    { hash = Sample(f); ValidateCandidate(f); _hashCache.StoreSample(f.Path, f.Logical, f.Written, hash, f.Identity); }
                    samples.Add((f, hash));
                }
                catch (Exception ex) when (Expected(ex)) { Warn(warnings, f.Path + "：" + ex.Message); }
            }
        }
        reporter.SetStage(fastMode ? "正在整理采样候选（操作前必做完整校验）" : "正在完整校验重复内容");
        var hashed = new List<(Candidate File, string Hash)>();
        foreach (var sampleGroup in samples.GroupBy(f => (f.File.Logical, f.Hash)).Where(g => g.Count() > 1))
        foreach (var item in sampleGroup)
        {
            token.ThrowIfCancellationRequested(); var f = item.File; reporter.Current(f.Path);
            try
            {
                ValidateCandidate(f); var hash = item.Hash;
                if (!fastMode && !_hashCache.TryGetFull(f.Path, f.Logical, f.Written, out hash, f.Identity))
                {
                    using var stream = OpenRead(f.Path); hash = DuplicateMutationService.Hash(stream, token);
                    ValidateCandidate(f); _hashCache.StoreFull(f.Path, f.Logical, f.Written, hash, f.Identity);
                }
                hashed.Add((f, hash));
            }
            catch (Exception ex) when (Expected(ex)) { Warn(warnings, f.Path + "：" + ex.Message); }
        }
        var output = new List<DuplicateFileItem>(); var number = 0;
        foreach (var group in hashed.GroupBy(i => (i.File.Logical, i.Hash)).OrderByDescending(g => g.Key.Logical))
        {
            var members = group.GroupBy(i => i.File.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(i => ReferenceDirectoryService.Contains(i.File.Path, references)).ThenBy(i => i.File.Path, StringComparer.OrdinalIgnoreCase).First()).ToArray();
            if (members.Length < 2) continue; number++;
            foreach (var i in members.OrderBy(i => i.File.Path, StringComparer.OrdinalIgnoreCase)) output.Add(new()
            {
                GroupNumber = number, Copies = members.Length, FullPath = i.File.Path, LogicalBytes = i.File.Logical, AllocatedBytes = i.File.Allocated,
                LogicalSize = CleanupService.FormatBytes(i.File.Logical), AllocatedSize = CleanupService.FormatBytes(i.File.Allocated),
                Hash = i.Hash, FileIdentity = i.File.Identity, LinkCount = i.File.Links, IsFullHashVerified = !fastMode, LastWriteTime = i.File.Written.ToLocalTime()
            });
        }
        DuplicateDecisionService.Annotate(output, references, []);
        if (!_hashCache.TrySave(out var error)) Warn(warnings, "哈希缓存保存失败：" + error);
        reporter.Complete(roots[0]);
        return new(roots[0], scanned, number, DuplicateDecisionService.PotentialBytes(output), output, warnings);
    }
    private static List<string> ResolveRoots(string requested, IReadOnlyList<string>? scope, IReadOnlyList<string> references, List<string> warnings)
    {
        var roots = new List<string>();
        foreach (var path in (scope is { Count: > 0 } ? scope : new[] { requested }).Concat(references))
        {
            try
            {
                var root = DiskAnalysisService.NormalizePath(path.Trim());
                if (!Directory.Exists(root) || DiskAnalysisService.HasReparsePointAncestor(root)) { Warn(warnings, root + "：不存在或包含重解析点，跳过"); continue; }
                if (roots.Any(r => DiskAnalysisService.IsWithin(root, r))) continue;
                roots.RemoveAll(r => DiskAnalysisService.IsWithin(r, root)); roots.Add(root);
            }
            catch (Exception ex) when (Expected(ex) || ex is ArgumentException) { Warn(warnings, path + "：" + ex.Message); }
        }
        if (roots.Count == 0) throw new DirectoryNotFoundException("扫描目录和参考目录均不可用，请检查路径/白名单。");
        return roots;
    }
    private static void ValidateCandidate(Candidate f)
    {
        var info = new FileInfo(f.Path);
        if (!info.Exists || info.Length != f.Logical || info.LastWriteTimeUtc != f.Written || WindowsFileStorageService.GetFileIdentity(f.Path) != f.Identity) throw new IOException("枚举后文件发生变化，已跳过");
    }
    private static string Sample(Candidate f)
    {
        using var stream = OpenRead(f.Path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[(int)Math.Min(f.Logical, SampleBytes)]; var read = stream.Read(buffer); hash.AppendData(buffer, 0, read);
        if (f.Logical > SampleBytes) { stream.Position = f.Logical - SampleBytes; read = stream.Read(buffer); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException;
    private static void Warn(List<string> warnings, string warning) { if (warnings.Count < 50) warnings.Add(warning); }
    private sealed record Candidate(string Path, long Logical, long Allocated, string Identity, uint Links, DateTime Written);
}
