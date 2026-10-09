using ChatGPTCleanupTool.Models;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;

namespace ChatGPTCleanupTool.Services;

public sealed partial class ExternalStorageService
{
    public Task<ExternalStorageResult> AnalyzeAsync(
        CancellationToken cancellationToken = default) => AnalyzeCoreAsync(cancellationToken);

    public async Task<string> PruneDockerAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunProcessAsync(
            "docker.exe", ["system", "prune", "--force"], cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Error.Trim().Length > 0
                ? result.Error.Trim() : "Docker 清理失败。");
        return result.Output.Trim();
    }

    private static async Task<ExternalStorageResult> AnalyzeCoreAsync(
        CancellationToken cancellationToken)
    {
        var items = new List<ExternalStorageItem>();
        var warnings = new List<string>();
        var dockerAvailable = true;
        var dockerRunning = false;

        try
        {
            var result = await RunProcessAsync(
                "docker.exe", ["system", "df", "--format", "{{json .}}"], cancellationToken);
            dockerRunning = result.ExitCode == 0;
            if (dockerRunning)
            {
                foreach (var line in result.Output.Split(
                             ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        var root = json.RootElement;
                        var type = GetString(root, "Type", "类型");
                        var sizeText = GetString(root, "Size", "大小");
                        var reclaimableText = GetString(root, "Reclaimable", "可回收");
                        var size = ParseDockerSize(sizeText);
                        var reclaimable = ParseDockerSize(reclaimableText.Split(' ')[0]);
                        items.Add(new ExternalStorageItem(
                            "Docker", type, "Docker 引擎正在运行",
                            size, CleanupService.FormatBytes(size),
                            0, "见 Docker VHDX",
                            reclaimable, CleanupService.FormatBytes(reclaimable),
                            $"总计 {GetString(root, "Total", "总计")}，活动 {GetString(root, "Active", "活动")}"));
                    }
                    catch (JsonException ex)
                    {
                        warnings.Add($"无法解析 Docker 占用信息：{ex.Message}");
                    }
                }
            }
            else
            {
                warnings.Add(string.IsNullOrWhiteSpace(result.Error)
                    ? "Docker 已安装，但引擎未运行。"
                    : result.Error.Trim());
            }
        }
        catch (Win32Exception)
        {
            dockerAvailable = false;
            warnings.Add("未检测到 Docker CLI。");
        }

        AddVhdFiles(
            items, warnings, "Docker VHDX",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker"),
            "Docker");
        AddWslDistributions(items, warnings);

        return new ExternalStorageResult(
            dockerAvailable, dockerRunning, items, warnings.Take(50).ToArray());
    }

    private static void AddWslDistributions(
        List<ExternalStorageItem> items,
        List<string> warnings)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (root is null)
            {
                warnings.Add("未检测到 WSL 分发版。");
                return;
            }

            foreach (var keyName in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(keyName);
                var name = key?.GetValue("DistributionName") as string ?? keyName;
                var basePath = key?.GetValue("BasePath") as string;
                if (string.IsNullOrWhiteSpace(basePath)) continue;
                basePath = Environment.ExpandEnvironmentVariables(basePath);
                var vhdPath = Directory.Exists(basePath)
                    ? Directory.EnumerateFiles(basePath, "*.vhdx", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault()
                    : null;
                if (vhdPath is null) continue;
                AddVhdItem(items, warnings, "WSL", name, vhdPath, "动态虚拟磁盘；内部删除后宿主文件未必立即缩小");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"读取 WSL 分发版信息失败：{ex.Message}");
        }
    }

    private static void AddVhdFiles(
        List<ExternalStorageItem> items,
        List<string> warnings,
        string source,
        string root,
        string namePrefix)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.vhdx", SearchOption.AllDirectories))
                AddVhdItem(items, warnings, source, $"{namePrefix} 虚拟磁盘", path, "宿主机上的虚拟磁盘文件");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"扫描 {source} 失败：{ex.Message}");
        }
    }

    private static void AddVhdItem(
        List<ExternalStorageItem> items,
        List<string> warnings,
        string source,
        string name,
        string path,
        string status)
    {
        try
        {
            var info = new FileInfo(path);
            var allocated = WindowsFileStorageService.GetAllocatedBytes(path);
            items.Add(new ExternalStorageItem(
                source, name, status,
                info.Length, CleanupService.FormatBytes(info.Length),
                allocated, CleanupService.FormatBytes(allocated),
                0, "需在系统内清理后评估", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"读取 {path} 失败：{ex.Message}");
        }
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value)) return value.ToString();
        return string.Empty;
    }

    private static long ParseDockerSize(string text)
    {
        var match = SizeRegex().Match(text.Trim());
        if (!match.Success) return 0;
        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" or "KIB" => 1L << 10,
            "MB" or "MIB" => 1L << 20,
            "GB" or "GIB" => 1L << 30,
            "TB" or "TIB" => 1L << 40,
            _ => 1L
        };
        return checked((long)(value * multiplier));
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能恰好已经退出。
            }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*([KMGT]?i?B|B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
