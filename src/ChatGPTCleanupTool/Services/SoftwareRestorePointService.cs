using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed class SoftwareRestorePointService : ISoftwareRestorePoint
{
    private sealed record Request(string Nonce, string UserSid, DateTime CreatedUtc);
    private sealed record Response(string Nonce, SoftwareRestorePoint Result);
    private static string RequestRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanupAssistant", "software-actions", "restore");
    public async Task<SoftwareRestorePoint> CreateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(RequestRoot);
        if (DiskAnalysisService.HasReparsePointAncestor(RequestRoot)) throw new IOException("还原点请求目录包含链接。");
        var nonce = Guid.NewGuid().ToString("N"); var path = Path.Combine(RequestRoot, nonce + ".request.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new Request(nonce, WindowsIdentity.GetCurrent().User?.Value ?? "", DateTime.UtcNow));
        var executable = Environment.ProcessPath ?? throw new IOException("软件启动路径不可用。");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new IOException("还原点助手请使用发布的 EXE，不在测试宿主中提权。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--software-create-restore-point"); start.ArgumentList.Add(path);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动还原点助手。");
        // Cancel stops waiting, but never kills System Restore while a snapshot may be in progress.
        try { await helper.WaitForExitAsync(token); }
        catch (OperationCanceledException) { throw new OperationCanceledException("已停止等待，还原点助手可能仍在完成系统操作；不会开始卸载。", token); }
        var resultPath = path + ".result.json";
        if (helper.ExitCode != 0 || !File.Exists(resultPath) || new FileInfo(resultPath).Length > 16384 || DiskAnalysisService.HasReparsePointAncestor(resultPath))
            return new(false, 0, "", "还原点未得到确认，可能系统保护关闭、UAC拒绝或不同账户提权。");
        var response = JsonSerializer.Deserialize<Response>(File.ReadAllText(resultPath));
        if (response is null || response.Nonce != nonce) return new(false, 0, "", "还原点回执身份不匹配。");
        return response.Result;
    }

    internal static int RunHelper(string requestPath)
    {
        try
        {
            var path = DiskAnalysisService.NormalizePath(requestPath);
            if (!string.Equals(Path.GetDirectoryName(path), RequestRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).EndsWith(".request.json", StringComparison.Ordinal) || DiskAnalysisService.HasReparsePointAncestor(path) || new FileInfo(path).Length > 16384) return 1;
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path));
            if (request is null || !Guid.TryParseExact(request.Nonce, "N", out _) || Path.GetFileName(path) != request.Nonce + ".request.json" || DateTime.UtcNow - request.CreatedUtc > TimeSpan.FromMinutes(10) || request.CreatedUtc > DateTime.UtcNow || request.UserSid != WindowsIdentity.GetCurrent().User?.Value || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
            var description = "DiskCleanupAssistant uninstall " + request.Nonce;
            var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new(); try { " +
                "$before=@(Get-ComputerRestorePoint -ErrorAction Stop | Select-Object -ExpandProperty SequenceNumber); " +
                "Checkpoint-Computer -Description " + SoftwareWindowsCommands.Quote(description) + " -RestorePointType APPLICATION_UNINSTALL -ErrorAction Stop -WarningAction Stop; " +
                "$point=Get-ComputerRestorePoint -ErrorAction Stop | Where-Object {$_.Description -eq " + SoftwareWindowsCommands.Quote(description) + " -and $_.SequenceNumber -notin $before} | Select-Object -First 1; " +
                "if($null -eq $point){throw '没有建立本次操作的新还原点（可能受24小时频率限制）'}; @{Success=$true;Sequence=[long]$point.SequenceNumber;Description=$point.Description;Error=''} | ConvertTo-Json -Compress " +
                "} catch { @{Success=$false;Sequence=0;Description='';Error=$_.Exception.Message} | ConvertTo-Json -Compress }";
            var start = new ProcessStartInfo(SoftwareWindowsCommands.PowerShellPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync(); process.WaitForExit(); Task.WhenAll(output, errors).GetAwaiter().GetResult();
            var result = output.Result.Length <= 16384 ? JsonSerializer.Deserialize<SoftwareRestorePoint>(output.Result) : null;
            result ??= new(false, 0, "", "还原点助手返回格式不支持。");
            using var responseStream = new FileStream(path + ".result.json", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(responseStream, new Response(request.Nonce, result)); return 0;
        }
        catch { return 1; }
    }
}
