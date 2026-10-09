using ChatGPTCleanupTool.Models;
using System.Diagnostics;

namespace ChatGPTCleanupTool.Services;

internal sealed class ScanProgressReporter(IProgress<ScanProgressInfo>? progress)
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _files;
    private long _directories;
    private long _bytes;
    private string _stage = "准备扫描";

    public void SetStage(string stage, string path = "")
    {
        _stage = stage;
        Report(path, force: true);
    }

    public void Directory(string path)
    {
        _directories++;
        Report(path);
    }

    public void File(string path, long bytes)
    {
        _files++;
        _bytes += Math.Max(0, bytes);
        Report(path);
    }

    public void Current(string path) => Report(path);

    public void Complete(string path = "") => Report(path, force: true);

    private void Report(string path, bool force = false)
    {
        if (progress is null || (!force && _stopwatch.ElapsedMilliseconds < 180))
        {
            return;
        }

        _stopwatch.Restart();
        progress.Report(new ScanProgressInfo(_stage, path, _files, _directories, _bytes));
    }
}
