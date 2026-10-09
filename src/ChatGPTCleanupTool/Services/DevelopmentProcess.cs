using System.Diagnostics;
using System.IO;
using System.Text;

namespace ChatGPTCleanupTool.Services;

internal sealed record DevelopmentCommand(int ExitCode, string Output, string Error);

internal static class DevelopmentProcess
{
    // Never invoke a shell; do not allow Git's configured fsmonitor or optional index writes.
    internal static DevelopmentCommand Git(string root, CancellationToken token, params string[] args) =>
        Run("git", root, ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-C", root, .. args], token);

    internal static DevelopmentCommand Run(string tool, string root, IReadOnlyList<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(tool) { WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start) ?? throw new IOException("无法启动 " + tool);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        // Limit both pipes independently; drain concurrently to avoid a pipe deadlock.
        static async Task<string> Read(StreamReader reader, CancellationToken cancellation)
        {
            var text = new StringBuilder(); var buffer = new char[8192]; int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
            { if (text.Length + n > 16 * 1024 * 1024) throw new IOException("命令结果超过安全上限；本项目只读分析。"); text.Append(buffer, 0, n); }
            return text.ToString();
        }
        var output = Read(process.StandardOutput, timeout.Token); var error = Read(process.StandardError, timeout.Token);
        try { Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).GetAwaiter().GetResult(); }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } token.ThrowIfCancellationRequested(); throw new IOException(tool + " 超时或输出不可读取。"); }
        token.ThrowIfCancellationRequested();
        return new(process.ExitCode, output.Result, error.Result);
    }
}
