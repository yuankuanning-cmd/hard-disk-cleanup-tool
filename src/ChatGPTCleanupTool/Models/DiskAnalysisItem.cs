using System.ComponentModel;

namespace ChatGPTCleanupTool.Models;

public sealed record DiskAnalysisItem(
    string Kind,
    string Name,
    string FullPath,
    long LogicalBytes,
    string LogicalSize,
    long AllocatedBytes,
    string AllocatedSize,
    string StorageNote,
    DateTime? LastWriteTime);

public sealed record DiskAnalysisResult(
    string Root,
    long ScannedLogicalBytes,
    long ScannedAllocatedBytes,
    int ScannedFileCount,
    IReadOnlyList<DiskAnalysisItem> Items,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<DiskTreeNode> Roots { get; init; } = [];
    public IReadOnlyList<DiskTreeNode> LargestFiles { get; init; } = [];
    public IReadOnlyList<DiskExtensionSummary> Extensions { get; init; } = [];
    public IReadOnlyList<DiskTreeNode> GrowthDirectories { get; init; } = [];
    public string ComparisonNote { get; init; } = string.Empty;
    public bool IsApproximate { get; init; }
    public bool NeedsRescan { get; init; }
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? IndexPath { get; init; }
    public ScanEngineStatistics? EngineStatistics { get; init; }
    public bool IsHistoricalSnapshot { get; init; }
    public bool HasCheckpoint { get; init; }
}

public sealed record DiskExtensionSummary(string Extension, long LogicalBytes, long AllocatedBytes, long FileCount)
{
    public string Label => string.IsNullOrEmpty(Extension) ? "无扩展名" : Extension;
    public string LogicalSize => Services.CleanupService.FormatBytes(LogicalBytes);
    public string AllocatedSize => Services.CleanupService.FormatBytes(AllocatedBytes);
    public System.Windows.Media.Brush Color => Controls.DiskTreemap.ExtensionBrush(Extension);
}

public sealed class DuplicateFileItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isReference;
    private bool _isExcluded;

    public required int GroupNumber { get; init; }
    public required int Copies { get; init; }
    public required string FullPath { get; init; }
    public required long LogicalBytes { get; init; }
    public required long AllocatedBytes { get; init; }
    public required string LogicalSize { get; init; }
    public required string AllocatedSize { get; init; }
    public required string Hash { get; init; }
    public required string FileIdentity { get; init; }
    public required bool IsFullHashVerified { get; init; }
    public required DateTime LastWriteTime { get; init; }
    public uint LinkCount { get; init; } = 1;
    public string DirectoryPath => System.IO.Path.GetDirectoryName(FullPath) ?? "";
    public string Name => System.IO.Path.GetFileName(FullPath);
    public bool IsReference { get => _isReference; internal set { _isReference = value; if (value) IsSelected = false; Changed(nameof(IsReference)); Changed(nameof(IsSelectable)); Changed(nameof(Role)); } }
    public bool IsExcluded { get => _isExcluded; internal set { _isExcluded = value; if (value) IsSelected = false; Changed(nameof(IsExcluded)); Changed(nameof(IsSelectable)); Changed(nameof(Role)); } }
    public bool IsSelectable => !IsReference && !IsExcluded;
    public bool IsCrossDirectory { get; internal set; }
    public bool HasBackupWorkOverlap { get; internal set; }
    public bool GroupHasReference { get; internal set; }
    public string DirectoryRole => Services.DuplicateDecisionService.IsBackupPath(FullPath) ? "备份路径（名称推断）" : "工作/普通路径";
    public string Role => IsReference ? "原件 · 不可操作" : IsExcluded ? "永久排除 · 保留" : DirectoryRole;
    public long TheoreticalReclaimableBytes => LinkCount > 1 ? 0 : AllocatedBytes;
    public int PixelWidth { get; internal set; }
    public int PixelHeight { get; internal set; }
    public int BitsPerPixel { get; internal set; }
    public bool IsLosslessImage { get; internal set; }
    public string ImageInformation => PixelWidth > 0 ? $"{PixelWidth} × {PixelHeight} · {BitsPerPixel} 位" : "非图片 / 未读取";
    internal void NotifyImage() => Changed(nameof(ImageInformation));
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !IsSelectable) value = false;
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record DuplicateAnalysisResult(
    string Root,
    int ScannedFileCount,
    int GroupCount,
    long PotentialAllocatedBytes,
    IReadOnlyList<DuplicateFileItem> Files,
    IReadOnlyList<string> Warnings);

public sealed record DuplicateDeletionResult(
    int DeletedCount,
    long ReclaimedAllocatedBytes,
    IReadOnlyList<string> Failures)
{
    public long TheoreticalBytes { get; init; }
    public IReadOnlyList<DuplicateOperationRecord> Records { get; init; } = [];
    public bool WasCanceled { get; init; }
    public IReadOnlyList<VolumeSpaceObservation> VolumeObservations { get; init; } = [];
}

public sealed class EmptyDirectoryItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    public required string FullPath { get; init; }
    public required string LastWriteTime { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record EmptyDirectoryScanResult(
    string Root,
    int ScannedDirectoryCount,
    int EmptyDirectoryCount,
    IReadOnlyList<EmptyDirectoryItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record EmptyDirectoryDeletionResult(
    int DeletedCount,
    int FailedCount,
    long DeletedLogicalBytes,
    IReadOnlyList<string> Failures);

public sealed record ExternalStorageItem(
    string Source,
    string Name,
    string Status,
    long LogicalBytes,
    string LogicalSize,
    long AllocatedBytes,
    string AllocatedSize,
    long ReclaimableBytes,
    string ReclaimableSize,
    string PathOrDetail);

public sealed record ExternalStorageResult(
    bool DockerAvailable,
    bool DockerRunning,
    IReadOnlyList<ExternalStorageItem> Items,
    IReadOnlyList<string> Warnings);

public sealed class CodexSmartFileItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExcluded;

    public required string Group { get; init; }
    public required string FullPath { get; init; }
    public required long LogicalBytes { get; init; }
    public required long AllocatedBytes { get; init; }
    public required string LogicalSize { get; init; }
    public required string AllocatedSize { get; init; }
    public required string Recommendation { get; init; }
    public required int RecommendationScore { get; init; }
    public required string RecommendationReason { get; init; }
    public required DateTime LastWriteTime { get; init; }
    public required DateTime LastAccessTime { get; init; }
    public required string Extension { get; init; }
    public required string FolderHint { get; init; }
    public int DuplicateGroup { get; init; }
    public int DuplicateCount { get; init; }
    public required bool IsSafeToDelete { get; init; }

    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded == value)
            {
                return;
            }

            _isExcluded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExcluded)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record CodexSmartAnalysisResult(
    string Root,
    int ScannedFileCount,
    int CandidateCount,
    long TotalLogicalBytes,
    long TotalAllocatedBytes,
    long EstimatedReclaimBytes,
    IReadOnlyList<CodexSmartFileItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record CodexSmartDeletionResult(
    int DeletedCount,
    long ReclaimedBytes,
    IReadOnlyList<string> Failures);
