using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed record SoftwareCommand(string Executable, IReadOnlyList<string> Arguments, bool Platform = false);

public static class SoftwareWindowsCommands
{
    internal static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    internal static DevelopmentCommand ReadPowerShell(string script, CancellationToken token) => DevelopmentProcess.Run(PowerShellPath,
        Environment.GetFolderPath(Environment.SpecialFolder.System), ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new(); " + script))], token);

    public static SoftwareCommand? Plan(SoftwareRegistration entry)
    {
        if (entry.Protected) return null;
        if (entry.Kind == SoftwareKind.Store)
            return Regex.IsMatch(entry.PackageName, @"\A[A-Za-z0-9_.-]{5,500}\z") ? new(PowerShellPath, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; Remove-AppxPackage -Package " + Quote(entry.PackageName) + " -ErrorAction Stop"))]) : null;
        if (entry.Kind == SoftwareKind.Steam)
            return Regex.IsMatch(entry.PlatformId, @"\A[0-9]{1,12}\z") ? new("steam://uninstall/" + entry.PlatformId, [], true) : null;
        if (entry.Kind != SoftwareKind.Desktop) return null;
        if (Guid.TryParse(entry.ProductCode, out var product))
            return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"), ["/x", product.ToString("B"), "/norestart"]);
        if (string.IsNullOrWhiteSpace(entry.UninstallCommand) || entry.UninstallCommand.Length > 32000) return null;
        var parts = Split(entry.UninstallCommand);
        if (parts.Count == 0 || !Path.IsPathFullyQualified(parts[0]) || Path.GetExtension(parts[0]).ToLowerInvariant() != ".exe") return null;
        var executable = DiskAnalysisService.NormalizePath(parts[0]);
        if (Path.GetFileName(executable).ToLowerInvariant() is "cmd.exe" or "powershell.exe" or "pwsh.exe" or "rundll32.exe" or "regsvr32.exe" or "mshta.exe" or "wscript.exe" or "cscript.exe" or "msiexec.exe") return null;
        if (!File.Exists(executable) || DiskAnalysisService.HasReparsePointAncestor(executable) || new DriveInfo(Path.GetPathRoot(executable)!).DriveType != DriveType.Fixed) return null;
        return new(executable, parts.Skip(1).ToArray());
    }
    public static IReadOnlyList<string> Split(string command)
    {
        var pointer = CommandLineToArgv(command, out var count); if (pointer == IntPtr.Zero) throw new IOException("卸载命令无法解析。");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgv(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class WindowsSoftwareUninstaller : ISoftwareUninstaller
{
    public async Task<SoftwareLaunchResult> RunAsync(SoftwareRegistration entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var command = SoftwareWindowsCommands.Plan(entry) ?? throw new IOException("卸载指令不支持、安全性未确认或程序已失效，请使用 Windows 已安装应用。 ");
        var start = new ProcessStartInfo(command.Executable) { UseShellExecute = entry.Kind != SoftwareKind.Store,
            CreateNoWindow = entry.Kind == SoftwareKind.Store };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (entry.Machine && entry.Kind == SoftwareKind.Desktop) start.Verb = "runas";
        if (entry.Kind == SoftwareKind.Store) { start.RedirectStandardError = true; start.RedirectStandardOutput = true; }
        using var process = Process.Start(start);
        if (command.Platform) return new(null, process?.Id, true);
        if (process is null) return new(null, null, true);
        var output = entry.Kind == SoftwareKind.Store ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var error = entry.Kind == SoftwareKind.Store ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { return new(null, process.Id, Canceled: true); } // Never kill an uninstaller or its children.
        await Task.WhenAll(output, error);
        if (entry.Kind == SoftwareKind.Store && process.ExitCode != 0) throw new IOException("Windows 拒绝卸载应用：" + error.Result[..Math.Min(1000, error.Result.Length)]);
        return new(process.ExitCode, process.Id);
    }
}
