using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Models;

public enum DuplicateKeepPolicy { Newest, Oldest, ShortestPath, HighestResolution, HighestQuality }
public enum DuplicateAction { Recycle, HardLink }
public sealed record DuplicateActionOptions(DuplicateAction Action, bool DryRun = false, bool HardLinkConsent = false);
public sealed record DuplicateOperationRecord(string Id, string OperationId, DateTimeOffset Timestamp, string Action,
    int Group, string FilePath, string KeeperPath, string Status, long TheoreticalBytes, long ActualBytes,
    string Reason, string BackupPath = "", string TemporaryLinkPath = "")
{
    public string TheoreticalSize => CleanupService.FormatBytes(TheoreticalBytes);
    public string ActualSize => CleanupService.FormatBytes(ActualBytes);
    public DateTimeOffset LocalTime => Timestamp.ToLocalTime();
}
public sealed record VolumeSpaceObservation(string Volume, long? Before, long? After)
{
    public long? Delta => Before is { } a && After is { } b ? b - a : null;
    public string Description => Volume + " 可用空间观测变化 " + (Delta is { } n ? (n >= 0 ? "+" : "−") + CleanupService.FormatBytes(Math.Abs(n)) : "不可读") + "（可能受其他程序影响，不等于本次释放量）";
}
public sealed record DuplicateDirectoryChoice(string Path, int Count)
{
    public override string ToString() => $"{Path}（{Count} 个）";
}
