using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

public sealed class MediaAnalysisService(string? cachePath = null)
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".avi", ".wmv", ".mkv", ".webm", ".mpeg", ".mpg" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".opus", ".aiff" };

    public Task<MediaAnalysisResult> AnalyzeAsync(MediaAnalysisOptions options, CancellationToken token = default,
        IProgress<ScanProgressInfo>? progress = null) => Task.Run(async () =>
    {
        if (options.MaximumFiles < 1 || options.MaximumFiles > 50_000) throw new ArgumentOutOfRangeException(nameof(options.MaximumFiles));
        if (options.ImageDistance is < 2 or > 12) throw new ArgumentOutOfRangeException(nameof(options.ImageDistance));
        var roots = (options.ScopeRoots is { Count: > 0 } ? options.ScopeRoots : new[] { options.Root })
            .Concat(options.ReferenceRoots ?? []).Select(DiskAnalysisService.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in roots)
            if (!Directory.Exists(root) || DiskAnalysisService.HasReparsePointAncestor(root)) throw new IOException("扫描目录不存在或含重解析点：" + root);
        roots = roots.Where(root => !roots.Any(other => !string.Equals(root, other, StringComparison.OrdinalIgnoreCase) && DiskAnalysisService.IsWithin(root, other))).ToArray();
        var devices = roots.Select(new StorageDeviceProbe().Detect).ToArray();
        int workers = devices.All(device => device == StorageMedium.Ssd) ? Math.Min(2, Environment.ProcessorCount) : 1;
        var warnings = new ConcurrentQueue<string>(); var items = new ConcurrentBag<MediaFingerprint>();
        var reporter = new ScanProgressReporter(progress); var reportLock = new object(); var cacheLock = new object();
        using var decoderGate = new SemaphoreSlim(1); int hits = 0, decoded = 0; bool limited = false, canceled = false, cacheHealthy = true;
        MediaFingerprintCache? cache = null;
        try { cache = new(cachePath); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { warnings.Enqueue("缓存不可用，本次重新分析：" + ex.Message); }
        try
        {
            reporter.SetStage($"相似媒体：{(workers > 1 ? "SSD 最多两路" : "机械 / 未知介质顺序读取")} · 枚举 → 缓存 → 指纹");
            try
            {
                await Parallel.ForEachAsync(Enumerate(), new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, async (candidate, ct) =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested(); DuplicatePreviewService.ValidateReadPath(candidate.Path);
                        string stamp = Stamp(candidate.Path);
                        MediaFingerprint? item;
                        lock (cacheLock)
                        {
                            item = null;
                            if (cacheHealthy)
                                try { item = cache?.Get(candidate.Path, stamp); }
                                catch (SqliteException ex) { cacheHealthy = false; Warn("缓存读取失败，本次重新分析：" + ex.Message); }
                        }
                        if (item?.Kind != candidate.Kind) item = null;
                        if (item is not null) Interlocked.Increment(ref hits);
                        else
                        {
                            if (candidate.Kind == MediaKind.Image) item = MediaVisualFingerprint.ReadImage(candidate.Path, ct);
                            else
                            {
                                await decoderGate.WaitAsync(ct);
                                try
                                {
                                    using var decoder = new WindowsMediaDecoder(candidate.Path, candidate.Kind == MediaKind.Video);
                                    item = candidate.Kind == MediaKind.Video ? decoder.ReadVideo(candidate.Path, ct) : decoder.ReadAudio(candidate.Path, ct);
                                }
                                finally { decoderGate.Release(); }
                            }
                            // Do not cache a fingerprint if the underlying file changed during decoding.
                            if (stamp != Stamp(candidate.Path)) throw new IOException("分析期间文件发生变化，已跳过");
                            try { lock (cacheLock) { if (cacheHealthy) cache?.Put(item, stamp); } }
                            catch (SqliteException ex) { Warn("无法保存媒体缓存：" + ex.Message); }
                            Interlocked.Increment(ref decoded);
                        }
                        items.Add(item with { Origin = Classify(candidate.Path) });
                        lock (reportLock) reporter.File(candidate.Path, item.Length);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or FileFormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or COMException or Win32Exception or DllNotFoundException or SqliteException)
                    { Warn(candidate.Path + "：" + ex.Message); }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
            var rows = items.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item => new MediaResultItem {
                Fingerprint = item, IsReference = (options.ReferenceRoots ?? []).Any(root => DiskAnalysisService.IsWithin(item.Path, root)) }).ToArray();
            if (!canceled)
            {
                reporter.SetStage("相似媒体：索引候选、逐组比较（不是完全重复验证）");
                try { BuildGroups(rows, options.ImageDistance, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
            }
            Suggest(rows);
            int metadataGroup = 0;
            foreach (var group in rows.Where(row => row.Fingerprint.MetadataDigest.Length > 0).GroupBy(row => row.Fingerprint.MetadataDigest).Where(group => group.Count() > 1))
            { metadataGroup++; foreach (var row in group) row.MetadataGroup = metadataGroup; }
            reporter.Complete();
            var ordered = rows.OrderBy(row => row.Group == 0 ? int.MaxValue : row.Group).ThenByDescending(row => row.IsSuggested).ThenBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
            return new MediaAnalysisResult(ordered, warnings.ToArray(), hits, decoded, rows.Where(row => row.Group > 0).Select(row => row.Group).Distinct().Count(), canceled, limited);
        }
        finally { cache?.Dispose(); }

        void Warn(string text) { if (warnings.Count < 500) warnings.Enqueue(text); }
        IEnumerable<(string Path, MediaKind Kind)> Enumerate()
        {
            int found = 0; var stack = new Stack<string>(roots.Reverse());
            while (stack.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                bool reference = (options.ReferenceRoots ?? []).Any(root => DiskAnalysisService.IsWithin(directory, root));
                bool leadsToReference = (options.ReferenceRoots ?? []).Any(root => DiskAnalysisService.IsWithin(root, directory));
                if (!reference && !leadsToReference && (options.Exclusions ?? []).Any(root => DiskAnalysisService.IsWithin(directory, root))) continue;
                lock (reportLock) reporter.Directory(directory);
                // One directory at a time; do not materialize the entire tree or enqueue every file.
                IEnumerator<string>? enumerator = null;
                try { enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(directory + "：" + ex.Message); }
                if (enumerator is null) continue;
                using (enumerator)
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested(); string path;
                        try { if (!enumerator.MoveNext()) break; path = enumerator.Current; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(directory + "：" + ex.Message); break; }
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(path + "：" + ex.Message); continue; }
                        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Offline) || ((uint)attributes & 0x00440000) != 0 || RecoveryFileProtection.IsManagedPath(path))
                        {
                            if (!attributes.HasFlag(FileAttributes.Directory) && (Images.Contains(System.IO.Path.GetExtension(path)) || Videos.Contains(System.IO.Path.GetExtension(path)) || Audio.Contains(System.IO.Path.GetExtension(path))))
                                Warn(path + "：跳过链接、云占位、离线或异常恢复文件，不触发下载");
                            continue;
                        }
                        if (attributes.HasFlag(FileAttributes.Directory)) { stack.Push(path); continue; }
                        if (!(options.ReferenceRoots ?? []).Any(root => DiskAnalysisService.IsWithin(path, root)) &&
                            (options.Exclusions ?? []).Any(root => DiskAnalysisService.IsWithin(path, root))) continue;
                        string extension = System.IO.Path.GetExtension(path); MediaKind? kind = null;
                        if (options.Images && Images.Contains(extension)) kind = MediaKind.Image;
                        else if (options.Videos && Videos.Contains(extension)) kind = MediaKind.Video;
                        else if (options.Audio && Audio.Contains(extension)) kind = MediaKind.Audio;
                        if (kind is null) continue;
                        if (found++ >= options.MaximumFiles) { limited = true; Warn($"已达到 {options.MaximumFiles:N0} 个媒体文件上限，请缩小白名单分批分析；本次结果不代表完整目录。"); yield break; }
                        yield return (path, kind.Value);
                    }
                }
            }
        }
    });

    internal static MediaOrigin Classify(string path)
    {
        string lower = path.Replace('/', '\\').ToLowerInvariant(); string name = System.IO.Path.GetFileName(lower);
        if (lower.Contains("\\.codex\\") || lower.Contains("\\generated_images\\") || lower.Contains("\\visualizations\\") || name.StartsWith("codex-")) return MediaOrigin.Codex;
        if (lower.Contains("\\screenshots\\") || lower.Contains("\\截图\\") || name.StartsWith("screenshot") || name.StartsWith("屏幕截图") || name.StartsWith("截图") || name.StartsWith("snip")) return MediaOrigin.Screenshot;
        if (lower.Contains("\\downloads\\") || lower.Contains("\\下载\\")) return MediaOrigin.Download;
        return MediaOrigin.Other;
    }

    private static string Stamp(string path)
    {
        var info = new FileInfo(path); string identity = WindowsFileStorageService.GetFileIdentity(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        byte[] bytes = new byte[(int)Math.Min(131072, stream.Length)];
        int first = (int)Math.Min(65536, stream.Length); stream.ReadExactly(bytes.AsSpan(0, first));
        int tail = bytes.Length - first;
        if (tail > 0) { stream.Position = stream.Length - tail; stream.ReadExactly(bytes.AsSpan(first, tail)); }
        return $"{identity}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(SHA256.HashData(bytes))}";
    }

    internal static void BuildGroups(MediaResultItem[] rows, int distance, CancellationToken token)
    {
        int nextGroup = 0;
        foreach (MediaKind kind in Enum.GetValues<MediaKind>())
        {
            // Video re-encoding, chroma resampling and encoder padding need more tolerance than still images.
            // Retain the duration, color, temporal-diversity and 4-of-5-frame guards.
            int visualDistance = kind == MediaKind.Video ? Math.Clamp(distance + 6, 10, 18) : distance;
            var trees = new[] { new HashIndex(kind == MediaKind.Audio ? 32 : 64), new HashIndex(), new HashIndex() }; var anchors = new List<MediaResultItem>();
            foreach (var row in rows.Where(row => row.Fingerprint.Kind == kind))
            {
                token.ThrowIfCancellationRequested(); var item = row.Fingerprint; var candidates = new HashSet<int>();
                if (kind == MediaKind.Audio)
                {
                    if (!InformativeAudio(item)) continue;
                    foreach (uint hash in AudioIndexSamples(item)) trees[0].Query(hash, 6, candidates);
                }
                else
                {
                    int sample = kind == MediaKind.Video ? 2 : 0;
                    if (item.Texture.Length <= sample || item.Texture[sample] < 5) continue;
                    for (int crop = 0; crop < 3; crop++) for (int transform = 0; transform < 8; transform++)
                        trees[crop].Query(item.VisualHashes[sample * 24 + crop * 8 + transform], crop == 0 ? visualDistance : Math.Max(2, visualDistance - 2), candidates);
                }
                MediaResultItem? match = null; double best = 0;
                foreach (int index in candidates.Order())
                {
                    token.ThrowIfCancellationRequested(); var anchor = anchors[index]; double score = Compare(anchor.Fingerprint, item, visualDistance, token);
                    if (score > best) { best = score; match = anchor; }
                }
                if (match is not null)
                {
                    if (match.Group == 0) { match.Group = ++nextGroup; match.Similarity = 1; }
                    row.Group = match.Group; row.Similarity = best;
                    // Matched rows are not indexed: prevents A~B~C transitive false-positive chains.
                }
                else
                {
                    int index = anchors.Count; anchors.Add(row);
                    if (kind == MediaKind.Audio) foreach (uint hash in AudioIndexSamples(item)) trees[0].Add(hash, index);
                    else for (int crop = 0; crop < 3; crop++) trees[crop].Add(item.VisualHashes[(kind == MediaKind.Video ? 48 : 0) + crop * 8], index);
                }
            }
        }
    }
    private static IEnumerable<uint> AudioIndexSamples(MediaFingerprint item) =>
        item.AudioHashes.Where((_, index) => index % 16 == 0).Distinct();
    private static bool InformativeAudio(MediaFingerprint item) => item.AudioEnergy >= .002 && item.AudioHashes.Length >= 28 && item.AudioHashes.Distinct().Take(5).Count() >= 5;
    private static double Compare(MediaFingerprint a, MediaFingerprint b, int distance, CancellationToken token)
    {
        if (a.Kind == MediaKind.Image) return MediaVisualFingerprint.Compare(a, 0, b, 0, distance);
        if (Math.Abs(a.Duration - b.Duration) > Math.Max(a.Kind == MediaKind.Audio ? 10 : 1, Math.Max(a.Duration, b.Duration) * .05)) return 0;
        if (a.Kind == MediaKind.Audio) return MediaAudioFingerprint.Compare(a.AudioHashes, b.AudioHashes, token);
        if (a.Texture.Length != 5 || b.Texture.Length != 5 || a.VisualHashes.Where((_, i) => i % 24 == 0).Distinct().Count() < 3 ||
            b.VisualHashes.Where((_, i) => i % 24 == 0).Distinct().Count() < 3) return 0;
        double total = 0; int matched = 0;
        for (int sample = 0; sample < 5; sample++) { double score = MediaVisualFingerprint.Compare(a, sample, b, sample, distance); if (score > 0) { matched++; total += score; } }
        return matched >= 4 ? total / matched : 0;
    }

    internal static void Suggest(IEnumerable<MediaResultItem> rows)
    {
        foreach (var row in rows) row.Advice = row.IsReference ? "参考目录原件：始终保留" : row.Group == 0 ? "未匹配；不是无用文件" : "相似候选：请人工对比，不可直接按重复删除";
        foreach (var group in rows.Where(row => row.Group > 0).GroupBy(row => row.Group))
        {
            var protectedRows = group.Where(row => row.IsReference).ToArray();
            if (protectedRows.Length > 0) { foreach (var row in protectedRows) row.IsSuggested = true; continue; }
            long maxPixels = group.Max(row => (long)row.Fingerprint.Width * row.Fingerprint.Height);
            var eligible = group.Where(row => maxPixels == 0 || (long)row.Fingerprint.Width * row.Fingerprint.Height >= maxPixels * .95).ToArray();
            var reasonable = eligible.Where(row => maxPixels == 0 || row.Fingerprint.Length / (double)Math.Max(1L, (long)row.Fingerprint.Width * row.Fingerprint.Height) is >= .03 and <= 8).ToArray();
            if (reasonable.Length > 0) eligible = reasonable;
            var selected = eligible.OrderBy(row => row.Fingerprint.TakenAt ?? DateTime.MaxValue).ThenBy(row => row.Fingerprint.Length)
                .ThenBy(row => row.FullPath.Length).First();
            selected.IsSuggested = true;
            selected.Advice = maxPixels > 0 ? "建议保留：最高分辨率附近 / 体积合理" + (selected.Fingerprint.TakenAt is not null ? " / 最早已知拍摄日期" : "；无拍摄日期依据") + "（不保证是原图）"
                : "建议保留：相同录音候选中体积较小；请试听确认音质";
        }
    }

    // Multi-index Hamming search: a neighbor within radius r must have at least one of four
    // segments within floor(r/4). Enumerate that segment's small mask set, then verify the full hash.
    // Unlike a high-dimensional BK tree, unrelated large image libraries do not degrade to tree-wide walks.
    internal sealed class HashIndex
    {
        private readonly int _segmentBits;
        private readonly ulong _mask;
        private readonly Dictionary<ushort, List<(ulong Hash, int Item)>>[] _buckets = [[], [], [], []];
        private static readonly ConcurrentDictionary<(int Bits, int Radius), ushort[]> Masks = new();
        public HashIndex(int width = 64)
        {
            if (width is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(width));
            _segmentBits = width / 4; _mask = (1UL << _segmentBits) - 1;
        }
        public void Add(ulong hash, int item)
        {
            for (int segment = 0; segment < 4; segment++)
            {
                ushort key = (ushort)((hash >> (segment * _segmentBits)) & _mask);
                if (!_buckets[segment].TryGetValue(key, out var list)) _buckets[segment][key] = list = [];
                list.Add((hash, item));
            }
        }
        public void Query(ulong hash, int radius, HashSet<int> result)
        {
            if (radius is < 0 or > 18) throw new ArgumentOutOfRangeException(nameof(radius));
            var masks = Masks.GetOrAdd((_segmentBits, radius / 4), key => MakeMasks(key.Bits, key.Radius));
            for (int segment = 0; segment < 4; segment++)
            {
                ushort key = (ushort)((hash >> (segment * _segmentBits)) & _mask);
                foreach (ushort mask in masks)
                    if (_buckets[segment].TryGetValue((ushort)(key ^ mask), out var list))
                        foreach (var entry in list)
                            if (!result.Contains(entry.Item) && BitOperations.PopCount(hash ^ entry.Hash) <= radius) result.Add(entry.Item);
            }
        }
        private static ushort[] MakeMasks(int bits, int radius)
        {
            var masks = new List<ushort> { 0 };
            for (int flipped = 1; flipped <= radius; flipped++) Generate(0, flipped, 0);
            return masks.ToArray();
            void Generate(int from, int remaining, ushort value)
            {
                if (remaining == 0) { masks.Add(value); return; }
                for (int bit = from; bit <= bits - remaining; bit++) Generate(bit + 1, remaining - 1, (ushort)(value | 1 << bit));
            }
        }
    }
}
