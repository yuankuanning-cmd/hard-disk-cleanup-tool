using System.Text.Json;
using System.IO;

namespace ChatGPTCleanupTool.Services;

public sealed class FileHashCacheService
{
    private const int MaxEntries = 200_000;
    private readonly string _cachePath;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public FileHashCacheService(string? cachePath = null)
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleanupAssistant");
        _cachePath = cachePath ?? Path.Combine(appDataRoot, "file-hash-cache.json");
        Load();
    }

    public bool TryGetSample(string path, long length, DateTime lastWriteTimeUtc, out string hash, string? identity = null) =>
        TryGet(path, length, lastWriteTimeUtc, requireFull: false, out hash, identity);

    public bool TryGetFull(string path, long length, DateTime lastWriteTimeUtc, out string hash, string? identity = null) =>
        TryGet(path, length, lastWriteTimeUtc, requireFull: true, out hash, identity);

    public void StoreSample(string path, long length, DateTime lastWriteTimeUtc, string hash, string? identity = null) =>
        Store(path, length, lastWriteTimeUtc, sampleHash: hash, fullHash: null, identity);

    public void StoreFull(string path, long length, DateTime lastWriteTimeUtc, string hash, string? identity = null) =>
        Store(path, length, lastWriteTimeUtc, sampleHash: null, fullHash: hash, identity);

    public bool TrySave(out string? error)
    {
        error = null;
        if (!_dirty)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var oldestAllowed = DateTime.UtcNow.AddDays(-120).Ticks;
            var snapshot = _entries.Values
                .Where(entry => entry.LastSeenUtcTicks >= oldestAllowed)
                .OrderByDescending(entry => entry.LastSeenUtcTicks)
                .Take(MaxEntries)
                .ToArray();
            var temporaryPath = _cachePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
            File.Move(temporaryPath, _cachePath, overwrite: true);
            _dirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryGet(
        string path,
        long length,
        DateTime lastWriteTimeUtc,
        bool requireFull,
        out string hash,
        string? identity)
    {
        hash = string.Empty;
        var normalized = Normalize(path);
        if (!_entries.TryGetValue(normalized, out var entry) ||
            entry.Length != length || entry.LastWriteTimeUtcTicks != lastWriteTimeUtc.Ticks || identity is not null && entry.Identity != identity)
        {
            return false;
        }

        hash = requireFull ? entry.FullHash ?? string.Empty : entry.SampleHash ?? string.Empty;
        if (hash.Length == 0)
        {
            return false;
        }

        _entries[normalized] = entry with { LastSeenUtcTicks = DateTime.UtcNow.Ticks };
        _dirty = true;
        return true;
    }

    private void Store(
        string path,
        long length,
        DateTime lastWriteTimeUtc,
        string? sampleHash,
        string? fullHash,
        string? identity)
    {
        var normalized = Normalize(path);
        _entries.TryGetValue(normalized, out var current);
        if (current is null || current.Length != length ||
            current.LastWriteTimeUtcTicks != lastWriteTimeUtc.Ticks || identity is not null && current.Identity != identity)
        {
            current = new CacheEntry(
                normalized, length, lastWriteTimeUtc.Ticks, null, null, DateTime.UtcNow.Ticks, identity);
        }

        _entries[normalized] = current with
        {
            SampleHash = sampleHash ?? current.SampleHash,
            FullHash = fullHash ?? current.FullHash,
            LastSeenUtcTicks = DateTime.UtcNow.Ticks
        };
        _dirty = true;
    }

    private void Load()
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<CacheEntry[]>(File.ReadAllText(_cachePath)) ?? [];
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    _entries[entry.Path] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 损坏或不兼容的缓存只会降低下一次扫描速度，不影响扫描正确性。
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private sealed record CacheEntry(
        string Path,
        long Length,
        long LastWriteTimeUtcTicks,
        string? SampleHash,
        string? FullHash,
        long LastSeenUtcTicks,
        string? Identity = null);
}
