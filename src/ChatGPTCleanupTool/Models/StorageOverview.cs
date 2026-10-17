using System.IO;
using System.Windows.Media;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Models;

public sealed record DriveUsage(string Root, string Label, string Format, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - Math.Clamp(FreeBytes, 0, TotalBytes));
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp(100d * UsedBytes / TotalBytes, 0, 100);
    public string Title => Root.TrimEnd('\\') + "  " + (Label.Length > 0 ? Label : "本地磁盘");
    public string TotalText => CleanupService.FormatBytes(TotalBytes);
    public string FreeText => CleanupService.FormatBytes(FreeBytes);
    public string UsedText => CleanupService.FormatBytes(UsedBytes);
    public string SpaceStatus => UsedPercent >= 90 ? "空间紧张" : UsedPercent >= 80 ? "留意剩余空间" : "空间充足";
    public Brush UsageBrush => UsedPercent >= 90 ? Danger : UsedPercent >= 80 ? Warning : Teal;
    private static readonly Brush Teal = Create("#16A897"), Warning = Create("#D99A31"), Danger = Create("#D95765");
    private static Brush Create(string color) { var value = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; value.Freeze(); return value; }
}
public sealed record StorageOverview(IReadOnlyList<DriveUsage> Drives, IReadOnlyList<string> Warnings, DateTime CapturedUtc);
public sealed record ScanBookmark(string Path)
{
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
}
