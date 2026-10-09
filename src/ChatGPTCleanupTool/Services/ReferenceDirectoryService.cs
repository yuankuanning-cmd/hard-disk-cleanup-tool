using System.IO;
using System.Text.Json;

namespace ChatGPTCleanupTool.Services;

/// <summary>Independent, fail-closed original-directory protection, never just a disabled UI checkbox.</summary>
public sealed class ReferenceDirectoryService
{
    private readonly string _path;
    private readonly object _sync = new();
    private string[] _roots = [];
    public ReferenceDirectoryService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanupAssistant", "duplicate-references.json");
    }
    public IReadOnlyList<string> GetPaths()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                if (_roots.Length > 0) throw new IOException("原件目录配置消失，已停止去重操作。请重新确认原件目录。");
                return [];
            }
            // Invalid or unreadable configuration must never silently remove protection.
            _roots = (JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? throw new IOException("原件目录配置无效"))
                .Select(DiskAnalysisService.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return _roots.ToArray();
        }
    }
    public void Add(string path)
    {
        path = DiskAnalysisService.NormalizePath(path);
        if (!Directory.Exists(path) || DiskAnalysisService.HasReparsePointAncestor(path)) throw new IOException("原件目录必须是存在的普通目录，不能是链接。");
        lock (_sync) Save(GetPaths().Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    public void Remove(string path) { lock (_sync) Save(GetPaths().Where(r => !r.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray()); }
    private void Save(string[] roots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(roots)); File.Move(temporary, _path, true); _roots = roots; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static bool Contains(string path, IReadOnlyList<string> roots) => roots.Any(r => DiskAnalysisService.IsWithin(path, r));
}
