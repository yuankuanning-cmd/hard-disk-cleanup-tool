namespace ChatGPTCleanupTool.Models;

public sealed record ScanProgressInfo(
    string Stage,
    string CurrentPath,
    long ProcessedFiles,
    long ProcessedDirectories,
    long ProcessedBytes);
