using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

internal static class NpsProjectRecognizer
{
    private static readonly string[] CompanionNames = ["script", "result", "external", "loadflow", "fpga_bin", "monitor"];
    internal static bool IsStructureClue(string name) => name.Equals("model", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("loadflow", StringComparison.OrdinalIgnoreCase) || name.Equals("fpga_bin", StringComparison.OrdinalIgnoreCase);
    internal static NpsMarker? ReadStructure(string root)
    {
        try
        {
            bool model = Directory.Exists(Path.Combine(root, "model"));
            int companions = CompanionNames.Count(p => Directory.Exists(Path.Combine(root, p)));
            if ((!model || companions < 1) && companions < 3) return null;
            var proof = InspectRoot(root);
            return new(root, false, proof.Confirmed, "", "", proof.Description, Structure: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(root, false, false, "", "", "目录结构候选，读取不完整：" + ex.Message, Structure: true); }
    }

    // A model extension alone never establishes an owning project directory.
    internal static NpsRootEvidence InspectRoot(string root)
    {
        try { return InspectRootCore(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(root, "", false, false, 0, false, false, "工程依据未读全：" + ex.Message); }
    }
    private static NpsRootEvidence InspectRootCore(string root)
    {
        if (!Directory.Exists(root) || !IsOrdinaryFile(root))
            return new(root, "", false, false, 0, false, false, "目录不存在或属于链接/恢复保护");
        bool hasModel = Directory.Exists(Path.Combine(root, "model"));
        var companions = CompanionNames.Where(p => Directory.Exists(Path.Combine(root, p))).ToArray();
        var manifestPaths = Directory.EnumerateFiles(root, "*.emtproj", SearchOption.TopDirectoryOnly).Take(2).ToArray();
        var manifest = manifestPaths.Length == 1 && IsOrdinaryFile(manifestPaths[0]) ? Read(manifestPaths[0]) : null;
        bool validManifest = manifest?.Recognized == true;
        // Only bounded, local, ordinary support files are inspected. Never hydrate or execute a file.
        bool supportedModel = false, npsScript = false;
        if (!validManifest && hasModel && companions.Length >= 2 && IsOrdinaryFile(Path.Combine(root, "model")))
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "model"), "*.npsdata", SearchOption.TopDirectoryOnly).Take(16))
                if (IsOrdinaryFile(file) && Read(file).Recognized) { supportedModel = true; break; }
        if (!validManifest && companions.Contains("script") && IsOrdinaryFile(Path.Combine(root, "script")))
        {
            int inspected = 0;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "script"), "*", SearchOption.TopDirectoryOnly).Take(64))
            {
                if (!new[] { ".py", ".json", ".xml", ".txt" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || !IsOrdinaryFile(file)) continue;
                if (++inspected > 8) break;
                using var reader = new StreamReader(file, Encoding.UTF8, true);
                var buffer = new char[16 * 1024]; int count = reader.ReadBlock(buffer, 0, buffer.Length);
                var text = new string(buffer, 0, count);
                if (new[] { "SimuNPS-EMT", "import simunps", "from simunps", "import nps_emt", "from nps_emt" }.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
                { npsScript = true; break; }
            }
        }
        bool completeLayout = hasModel && companions.Length >= 2;
        bool confirmed = validManifest && (hasModel || companions.Length >= 2) ||
            manifestPaths.Length == 0 && completeLayout && (supportedModel || npsScript);
        var reasons = new List<string>();
        if (validManifest) reasons.Add("已验证工程XML清单");
        else if (manifestPaths.Length > 0) reasons.Add(manifestPaths.Length > 1 ? "多个清单，归属有歧义" : "清单未知/受损");
        else reasons.Add("未找到工程清单");
        if (hasModel) reasons.Add("model目录");
        if (companions.Length > 0) reasons.Add("配套目录：" + string.Join("/", companions));
        if (supportedModel) reasons.Add("模型数据库结构已核对");
        if (npsScript) reasons.Add("脚本具有NPS专用标记");
        reasons.Add(confirmed ? "多项依据确认工程边界" : "仅候选，不能确认完整工程归属");
        return new(root, manifestPaths.Length == 1 ? manifestPaths[0] : "", confirmed, hasModel, companions.Length, supportedModel, npsScript, string.Join("；", reasons));
    }
    private static bool IsOrdinaryFile(string path) => !DiskAnalysisService.HasReparsePointAncestor(path) &&
        (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x400000)) == 0 && !RecoveryFileProtection.IsManagedPath(path);
    internal static NpsMarker Read(string path)
    {
        try
        {
            if (!IsOrdinaryFile(path)) throw new IOException("标志文件属于链接/离线/云占位/恢复保护");
            if (path.EndsWith(".npsdata", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> header = stackalloc byte[16];
                if (stream.Read(header) != 16 || !header.SequenceEqual("SQLite format 3\0"u8)) return new(path, true, false, "", "", "npsdata扩展名，但不是已支持的SQLite模型格式");
                stream.ReadByte(); stream.ReadByte(); // Page-size bytes precede the read/write-format flags.
                int writeMode = stream.ReadByte(), readMode = stream.ReadByte();
                if (writeMode == 2 || readMode == 2 || File.Exists(path + "-wal") || File.Exists(path + "-shm") || File.Exists(path + "-journal"))
                    return new(path, true, false, "", "", "SQLite模型候选处于WAL/恢复日志模式；不打开数据库，避免创建或改写辅助文件");
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('model','block')";
                return new(path, true, Convert.ToInt32(command.ExecuteScalar()) == 2, "", "", "只读核对SQLite头和model/block表；不修改模型数据库");
            }
            if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("工程清单超过1MiB");
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var root = XDocument.Load(reader).Root;
            bool recognized = root?.Name.LocalName == "Project" && root.Element("Version") is { Value.Length: > 0 } &&
                (root.Element("ProjectConfig") is not null || root.Element("LoadFlowConfig") is not null);
            return new(path, false, recognized, root?.Element("Version")?.Value ?? "", root?.Element("CreateTime")?.Value ?? "",
                recognized ? "emtproj清单的Project/Version/配置结构已核对（依据本机样例）" : "有emtproj扩展名，但结构未知；仅提示，不授权清理");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or SqliteException)
        { return new(path, path.EndsWith(".npsdata", StringComparison.OrdinalIgnoreCase), false, "", "", "格式/权限检查未完成：" + ex.Message); }
    }
    internal static string ManifestHash(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("工程清单太大");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static NpsProjectKind Classify(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p.Equals(".simunps-mcp", StringComparison.OrdinalIgnoreCase))) return NpsProjectKind.Recovery;
        if (parts.Any(p => p.Contains(".restore-backup-", StringComparison.OrdinalIgnoreCase) || p.Equals("backup", StringComparison.OrdinalIgnoreCase) || p.Equals("backups", StringComparison.OrdinalIgnoreCase)))
            return NpsProjectKind.Backup;
        return NpsProjectKind.Project;
    }
}
