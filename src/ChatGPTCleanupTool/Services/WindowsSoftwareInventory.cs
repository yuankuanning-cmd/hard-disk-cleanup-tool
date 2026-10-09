using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatGPTCleanupTool.Models;
using Microsoft.Win32;

namespace ChatGPTCleanupTool.Services;

public sealed class WindowsSoftwareInventory : ISoftwareInventory
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public SoftwareSnapshot Read(bool store, bool games, CancellationToken token)
    {
        var entries = new List<SoftwareRegistration>(); var complete = new HashSet<string>(); var warnings = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser }) foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var source = $"{hive}/{view}"; bool ok = true;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view); using var root = baseKey.OpenSubKey(UninstallRoot, writable: false);
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    try { using var key = root!.OpenSubKey(name, writable: false); if (key is not null && ReadDesktop(key, name, hive, view) is { } entry) entries.Add(entry); }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { ok = false; warnings.Add("部分软件登记不可读取：" + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { ok = false; warnings.Add("软件登记来源不可读：" + source + " · " + ex.Message); }
            if (ok) complete.Add(source);
        }
        if (store)
        {
            try
            {
                var result = SoftwareWindowsCommands.ReadPowerShell("ConvertTo-Json -Compress -Depth 4 -InputObject @(Get-AppxPackage -ErrorAction Stop | Select-Object Name,Publisher,@{Name='Version';Expression={$_.Version.ToString()}},InstallLocation,PackageFullName,NonRemovable,IsFramework,IsResourcePackage)", token);
                if (result.ExitCode != 0) throw new IOException("Windows 应用包查询失败。");
                entries.AddRange(ParseStore(result.Output)); complete.Add("Store");
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException) { warnings.Add("商店应用仅查询当前用户，未能读全：" + ex.Message); }
        }
        if (games)
        {
            try
            {
                using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var steamRoot = steam?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamRoot)) { entries.AddRange(ReadSteam(steamRoot, token)); complete.Add("Steam"); }
                else complete.Add("Steam");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { warnings.Add("Steam 库未读完整：" + ex.Message); }
            try
            {
                var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
                int epicCount = 0;
                if (Directory.Exists(manifests)) foreach (var file in Directory.EnumerateFiles(manifests, "*.item").Take(3001))
                { token.ThrowIfCancellationRequested(); if (++epicCount > 3000) throw new IOException("Epic 内容超过清单上限。"); entries.Add(ReadEpic(file)); }
                complete.Add("Epic");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { warnings.Add("Epic 内容未读完整：" + ex.Message); }
        }
        // Shared HKCU views can return the very same registration twice. Distinct architectures/commands remain separate.
        var unique = entries.DistinctBy(e => e.Kind == SoftwareKind.Desktop ?
            e.RegistryPath + "|" + e.Name + "|" + e.Version + "|" + e.InstallPath + "|" + e.UninstallCommand : e.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(unique, complete, warnings);
    }
    private static SoftwareRegistration? ReadDesktop(RegistryKey key, string keyName, RegistryHive hive, RegistryView view)
    {
        string Text(string name) => (key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "").Trim();
        bool Flag(string name) => Convert.ToString(key.GetValue(name)) == "1";
        var name = Text("DisplayName"); if (name.Length == 0) return null;
        var location = Text("InstallLocation").Trim('"');
        if (location.Length > 0) location = NormalizeOptional(Environment.ExpandEnvironmentVariables(location));
        // DisplayIcon is only a weak location hint; do not claim a shared/system executable directory belongs to an app.
        var source = $"{hive}/{view}"; var registryPath = (hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + UninstallRoot + "\\" + keyName;
        long.TryParse(Convert.ToString(key.GetValue("EstimatedSize")), out var kilobytes);
        var protectedEntry = Flag("SystemComponent") || Flag("NoRemove") || Text("ParentKeyName").Length > 0 || Text("ReleaseType").Contains("Update", StringComparison.OrdinalIgnoreCase);
        return new(source + "/" + keyName, source, SoftwareKind.Desktop, name, Text("Publisher"), Text("DisplayVersion"), location,
            kilobytes is > 0 and < (long.MaxValue / 1024) ? kilobytes * 1024 : 0, Environment.ExpandEnvironmentVariables(Text("UninstallString")),
            Flag("WindowsInstaller") && Guid.TryParse(keyName, out var guid) ? guid.ToString("B") : "", protectedEntry, registryPath, Machine: hive == RegistryHive.LocalMachine);
    }
    internal static IReadOnlyList<SoftwareRegistration> ParseStore(string text)
    {
        using var document = JsonDocument.Parse(text); var rows = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        return rows.Where(r => r.ValueKind == JsonValueKind.Object).Select(r =>
        {
            string Get(string name) => r.TryGetProperty(name, out var value) ? value.ToString() : "";
            bool Flag(string name) => string.Equals(Get(name), "True", StringComparison.OrdinalIgnoreCase);
            var full = Get("PackageFullName"); return new SoftwareRegistration("Store/" + full, "Store", SoftwareKind.Store, Get("Name"), Get("Publisher"), Get("Version"), NormalizeOptional(Get("InstallLocation")), 0,
                Protected: Flag("NonRemovable") || Flag("IsFramework") || Flag("IsResourcePackage"), PackageName: full);
        }).ToArray();
    }
    internal static IReadOnlyList<SoftwareRegistration> ReadSteam(string steamRoot, CancellationToken token)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DiskAnalysisService.NormalizePath(steamRoot) };
        var config = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(config)) foreach (var (key, value) in ReadValvePairs(ReadBounded(config)))
            if ((key == "path" || int.TryParse(key, out _)) && Path.IsPathFullyQualified(value)) libraries.Add(DiskAnalysisService.NormalizePath(value));
        var entries = new List<SoftwareRegistration>();
        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps"); if (!Directory.Exists(steamapps)) continue;
            if (DiskAnalysisService.HasReparsePointAncestor(steamapps)) throw new IOException("游戏平台目录包含链接。");
            foreach (var file in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").Take(3001))
            {
                if (entries.Count >= 3000) throw new IOException("游戏数量超过上限。"); token.ThrowIfCancellationRequested();
                var values = ReadValvePairs(ReadBounded(file)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
                var id = values.GetValueOrDefault("appid", ""); if (!Regex.IsMatch(id, @"\A[0-9]{1,12}\z")) continue;
                if (!string.Equals(Path.GetFileName(file), "appmanifest_" + id + ".acf", StringComparison.OrdinalIgnoreCase)) throw new IOException("Steam 清单文件名与应用身份不一致。");
                var leaf = values.GetValueOrDefault("installdir", ""); var common = Path.Combine(steamapps, "common"); var path = NormalizeOptional(Path.Combine(common, leaf));
                if (leaf.Length == 0 || path.Length == 0 || !DiskAnalysisService.IsWithin(path, common) || string.Equals(path, common, StringComparison.OrdinalIgnoreCase)) throw new IOException("游戏清单路径越界。");
                long.TryParse(values.GetValueOrDefault("SizeOnDisk", ""), out var size);
                entries.Add(new("Steam/" + library + "/" + id, "Steam", SoftwareKind.Steam, values.GetValueOrDefault("name", "Steam " + id), "Steam", "", path, Math.Max(0, size), PlatformId: id, ManifestPath: file));
            }
        }
        return entries;
    }
    internal static IReadOnlyList<(string Key, string Value)> ReadValvePairs(string text) => Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"")
        .Select(m => (m.Groups[1].Value, m.Groups[2].Value.Replace("\\\\", "\\").Replace("\\\"", "\""))).ToArray();
    private static SoftwareRegistration ReadEpic(string file)
    {
        using var json = JsonDocument.Parse(ReadBounded(file)); var r = json.RootElement; string Get(string name) => r.TryGetProperty(name, out var value) ? value.ToString() : "";
        return new("Epic/" + Path.GetFileName(file), "Epic", SoftwareKind.Epic, Get("DisplayName"), "Epic Games", Get("AppVersionString"), NormalizeOptional(Get("InstallLocation")), 0,
            PlatformId: Get("AppName"), ManifestPath: file);
    }
    public SoftwareRegistration? Refresh(SoftwareRegistration original, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (original.Kind == SoftwareKind.Desktop)
        {
            var split = original.Source.Split('/'); if (split.Length != 2 || !Enum.TryParse<RegistryHive>(split[0], out var hive) || !Enum.TryParse<RegistryView>(split[1], out var view)) throw new IOException("软件来源不可确认。");
            var keyName = original.Id[(original.Source.Length + 1)..];
            using var root = RegistryKey.OpenBaseKey(hive, view); using var key = root.OpenSubKey(UninstallRoot + "\\" + keyName, writable: false); return key is null ? null : ReadDesktop(key, keyName, hive, view);
        }
        if (original.Kind == SoftwareKind.Store)
        {
            if (SoftwareWindowsCommands.Plan(original) is null && !original.Protected) throw new IOException("包身份格式不可确认。");
            var result = SoftwareWindowsCommands.ReadPowerShell("ConvertTo-Json -Compress -Depth 4 -InputObject @(Get-AppxPackage -ErrorAction Stop | Where-Object {$_.PackageFullName -eq " + SoftwareWindowsCommands.Quote(original.PackageName) + "} | Select-Object Name,Publisher,@{Name='Version';Expression={$_.Version.ToString()}},InstallLocation,PackageFullName,NonRemovable,IsFramework,IsResourcePackage)", token);
            if (result.ExitCode != 0) throw new IOException("应用包状态不可读取。"); return ParseStore(result.Output).SingleOrDefault();
        }
        if (original.Kind == SoftwareKind.Steam)
        {
            if (!File.Exists(original.ManifestPath)) return null;
            var root = Path.GetDirectoryName(Path.GetDirectoryName(original.ManifestPath))!; return ReadSteam(root, token).FirstOrDefault(e => e.Id == original.Id);
        }
        return null;
    }

    public IReadOnlyList<SoftwareRegistration> FindDirectories(IReadOnlyList<SoftwareRegistration> installed, IReadOnlyList<SoftwareHistory> history, SoftwareOptions options, CancellationToken token)
    {
        var rows = new List<SoftwareRegistration>();
        var areas = new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas)
        {
            if (!Directory.Exists(area)) continue;
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(area).Take(3000))
                {
                    token.ThrowIfCancellationRequested(); if (!WithinScope(directory, options) || BlockDiscovery(directory)) continue;
                    bool active = installed.Any(e => e.InstallPath.Length > 0 && DevelopmentCodexCatalog.Overlap(directory, e.InstallPath) || AliasMatch(Path.GetFileName(directory), e));
                    if (active) continue;
                    var prior = history.Where(h => h.AbsentScans >= 2 && AliasMatch(Path.GetFileName(directory), h.Registration)).ToArray();
                    bool program = area == Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) || area == Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    if (prior.Length == 0 && !program) continue;
                    rows.Add(new("Directory/" + directory, "Discovery", prior.Length > 0 ? SoftwareKind.DirectoryResidue : SoftwareKind.Orphan,
                        prior.Length > 0 ? prior[0].Registration.Name : Path.GetFileName(directory), prior.Length > 0 ? prior[0].Registration.Publisher : "", "", directory, 0));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Caller reports heuristic discovery as non-exhaustive. */ }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); using var software = root.OpenSubKey("Software", writable: false);
                foreach (var name in software?.GetSubKeyNames().Take(3000) ?? [])
                {
                    token.ThrowIfCancellationRequested(); if (IsSharedName(name) || installed.Any(e => AliasMatch(name, e))) continue;
                    var prior = history.FirstOrDefault(h => h.AbsentScans >= 2 && AliasMatch(name, h.Registration)); if (prior is null) continue;
                    var path = (hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE") + "\\Software\\" + name;
                    rows.Add(new("Registry/" + path, "Discovery", SoftwareKind.RegistryResidue, prior.Registration.Name, prior.Registration.Publisher, "", "", 0, RegistryPath: path));
                }
            }
            catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException) { }
        }
        return rows;
    }
    internal static bool AliasMatch(string leaf, SoftwareRegistration entry)
    {
        static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var name = Normalize(leaf); if (name.Length < 4 || IsSharedName(leaf)) return false;
        return new[] { entry.Name, entry.Publisher, entry.InstallPath.Length > 0 ? Path.GetFileName(entry.InstallPath) : "" }.Any(alias => Normalize(alias).Length >= 4 && Normalize(alias) == name);
    }
    private static bool IsSharedName(string name) => name.ToLowerInvariant() is "microsoft" or "windows" or "packages" or "common files" or "shared" or "temp" or "cache" or "google" or "intel" or "nvidia" or "amd" or "adobe" or "diskcleanupassistant" or "codex" || name.StartsWith(".", StringComparison.Ordinal);
    private static bool BlockDiscovery(string path) => IsSharedName(Path.GetFileName(path)) || RecoveryFileProtection.IsManagedPath(path) || DiskAnalysisService.HasReparsePointAncestor(path);
    internal static bool WithinScope(string path, SoftwareOptions options) => (options.ScopeRoots is not { Count: > 0 } || options.ScopeRoots.Any(r => DiskAnalysisService.IsWithin(path, r))) && !(options.Exclusions?.Any(r => DevelopmentCodexCatalog.Overlap(path, r)) ?? false);
    private static string NormalizeOptional(string path)
    { try { return Path.IsPathFullyQualified(path) ? DiskAnalysisService.NormalizePath(path) : ""; } catch { return ""; } }
    private static string ReadBounded(string path)
    { if (new FileInfo(path).Length > 4 * 1024 * 1024 || DiskAnalysisService.HasReparsePointAncestor(path)) throw new IOException("平台清单太大或包含链接。"); return File.ReadAllText(path); }
}
