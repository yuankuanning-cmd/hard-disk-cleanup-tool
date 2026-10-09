using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed class DiskSnapshotService(string? storageDirectory = null)
{
    private readonly string _directory = storageDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskCleanupAssistant", "space-snapshots");
    public string StorageDirectory => _directory;

    public DiskAnalysisResult CompareAndSave(DiskAnalysisResult result,
        IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
    {
        var scope = string.Join('\n', result.Roots.Select(n => n.FullPath.ToUpperInvariant()).Order()) +
                    "\nmode=" + result.IsApproximate + "\nexclude=" +
                    string.Join('\n', exclusions.Select(n => n.ToUpperInvariant()).Order());
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        var path = Path.Combine(_directory, key + ".json");
        var previous = new Dictionary<string, DirectorySnapshot>(StringComparer.OrdinalIgnoreCase);
        var note = "首次扫描已建立增长基线；再次扫描相同范围和模式后显示增长排行。";
        try
        {
            if (File.Exists(path))
            {
                using var input = File.OpenRead(path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, DirectorySnapshot>>(input);
                if (stored is not null) previous = new(stored, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            note = "旧增长基线不可读，本次将重新建立基线。";
        }

        var current = new Dictionary<string, DirectorySnapshot>(StringComparer.OrdinalIgnoreCase);
        var growing = new List<DiskTreeNode>();
        var comparable = 0;
        foreach (var node in result.Roots.SelectMany(n => n.DescendantsAndSelf()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.IsDirectory || node.IsExcluded) continue;
            node.GrowthBytes = null;
            node.LogicalGrowthBytes = null;
            node.GrowthBytesPerDay = 0;
            node.ComparedAt = null;
            if (node.IsIncomplete)
            {
                if (previous.TryGetValue(node.FullPath, out var old)) current[node.FullPath] = old;
                continue; // An unreadable subtree must not become a false shrink/growth baseline.
            }
            var hasBaseline = previous.TryGetValue(node.FullPath, out var baseline);
            if (!hasBaseline)
            {
                // A new directory has known prior size zero only if a previously complete ancestor was scanned.
                for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
                {
                    if (!previous.TryGetValue(ancestor.FullPath, out var oldAncestor)) continue;
                    baseline = new(0, 0, oldAncestor.ScannedAt);
                    hasBaseline = true;
                    break;
                }
            }
            if (hasBaseline && baseline is not null)
            {
                comparable++;
                node.GrowthBytes = node.AllocatedBytes - baseline.AllocatedBytes;
                node.LogicalGrowthBytes = node.LogicalBytes - baseline.LogicalBytes;
                node.ComparedAt = baseline.ScannedAt;
                var elapsedDays = Math.Max((result.ScannedAt - baseline.ScannedAt).TotalDays, 1d / 86400);
                node.GrowthBytesPerDay = node.GrowthBytes.Value / elapsedDays;
                if (node.GrowthBytes > 0) growing.Add(node);
            }
            current[node.FullPath] = new(node.LogicalBytes, node.AllocatedBytes, result.ScannedAt);
        }
        if (comparable > 0)
            note = $"与上次同范围、同模式扫描对比；{comparable:N0} 个目录可比。按占用增长速率排序（父子目录不相加）。";
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = File.Create(temporary)) JsonSerializer.Serialize(output, current);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            // Only this service's own hash-named cache files are eligible; unrelated files are untouched.
            foreach (var stale in new DirectoryInfo(_directory).EnumerateFiles("*.json")
                         .Where(file => file.Name.Length == 69 && file.Name[..64].All(Uri.IsHexDigit))
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(16))
            {
                try { stale.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note += " 增长基线保存失败：" + ex.Message;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return result with
        {
            GrowthDirectories = growing.OrderByDescending(n => n.GrowthBytesPerDay).Take(50).ToArray(),
            ComparisonNote = note
        };
    }

    public sealed record DirectorySnapshot(long LogicalBytes, long AllocatedBytes, DateTimeOffset ScannedAt);
}
