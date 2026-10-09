namespace ChatGPTCleanupTool.Models;

public enum ScanScheduling { Automatic, Sequential, Parallel }
public sealed record ScanEngineOptions
{
    public ScanScheduling Scheduling { get; init; } = ScanScheduling.Automatic;
    public bool ComputeFingerprints { get; init; }
    public bool ResumeCheckpoint { get; init; }
    public bool PreferUsn { get; init; } = true;
    public bool RetainPartialResults { get; init; } = true;
    public int MaximumParallelism { get; init; } = 4;
}
public sealed record ScanEngineStatistics(string Method, string DevicePolicy,
    long EnumeratedFiles, long MetadataReads, long ReusedFiles, long HashedFiles,
    long ReusedHashes, long RemovedEntries, int WorkerLimit, bool IsPartial,
    bool UsedUsn, string Status);
