using System.Text.Json;
using System.IO;

namespace ChatGPTCleanupTool.Services;

public sealed class ExclusionService
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private HashSet<string> _paths;
    private string[] _referenceRoots = [];
    private bool _referenceReadFailed;
    public void SetReferenceRoots(IReadOnlyList<string> roots) { lock (_sync) { _referenceRoots = roots.ToArray(); _referenceReadFailed = false; } }
    public void SetReferenceReadFailed() { lock (_sync) _referenceReadFailed = true; }

    public ExclusionService(string? filePath = null)
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleanupAssistant");
        _filePath = filePath ?? Path.Combine(appDataRoot, "exclusions.json");
        _paths = Load();
    }

    public string FilePath => _filePath;

    public IReadOnlySet<string> GetPaths()
    {
        lock (_sync)
        {
            if (_referenceReadFailed) throw new IOException("原件保护配置无法读取，清理已禁用。");
            return _paths.Concat(_referenceRoots).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool Contains(string path)
    {
        if (RecoveryFileProtection.IsManagedPath(path)) return true;
        var normalized = TryNormalize(path);
        lock (_sync)
        {
            return _referenceReadFailed || normalized is not null && (_paths.Contains(normalized) ||
                _paths.Concat(_referenceRoots).Any(root => DiskAnalysisService.IsWithin(normalized, root)));
        }
    }

    public void SetExcluded(string path, bool excluded)
    {
        var normalized = TryNormalize(path);
        if (normalized is null)
        {
            return;
        }

        SetExcludedPaths([normalized], excluded);
    }

    public void SetExcludedPaths(IEnumerable<string> paths, bool excluded)
    {
        var normalized = paths.Select(path => TryNormalize(path) ?? throw new ArgumentException("排除路径无效：" + path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_sync)
        {
            var before = new HashSet<string>(_paths, StringComparer.OrdinalIgnoreCase);
            try { foreach (var path in normalized) { if (excluded) _paths.Add(path); else _paths.Remove(path); } Save(); }
            catch { _paths.Clear(); _paths.UnionWith(before); throw; }
        }
    }
    private HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_filePath)) ?? [];
            return paths
                .Select(TryNormalize)
                .Where(path => path is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(
            _paths.OrderBy(path => path),
            new JsonSerializerOptions { WriteIndented = true });
        var temporary = Path.Combine(Path.GetDirectoryName(_filePath)!, ".diskcleanup-exclusions-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json); stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? TryNormalize(string path)
    {
        try
        {
            return DiskAnalysisService.NormalizePath(path);
        }
        catch
        {
            return null;
        }
    }
}
