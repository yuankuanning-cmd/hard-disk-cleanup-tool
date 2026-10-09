using System.ComponentModel;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Models;

public enum SoftwareKind { Desktop, Store, Steam, Epic, Orphan, DirectoryResidue, RegistryResidue }
public sealed record SoftwareRegistration(string Id, string Source, SoftwareKind Kind, string Name, string Publisher,
    string Version, string InstallPath, long ReportedBytes, string UninstallCommand = "", string ProductCode = "",
    bool Protected = false, string RegistryPath = "", string PackageName = "", string PlatformId = "",
    string ManifestPath = "", bool Machine = false);
public sealed record SoftwareSnapshot(IReadOnlyList<SoftwareRegistration> Entries, IReadOnlySet<string> CompleteSources, IReadOnlyList<string> Warnings);
public sealed record SoftwareOptions(bool Store = true, bool Games = true, bool FindResidues = true, bool Measure = false,
    IReadOnlyList<string>? ScopeRoots = null, IReadOnlyList<string>? Exclusions = null, int MaximumEntries = 1_000_000);
public sealed class SoftwareItem : INotifyPropertyChanged
{
    private bool _selected;
    public required SoftwareRegistration Registration { get; init; }
    public string Id => Registration.Id;
    public string Name => Registration.Name;
    public string Publisher => Registration.Publisher;
    public string Version => Registration.Version;
    public string FullPath => Registration.InstallPath.Length > 0 ? Registration.InstallPath : Registration.RegistryPath;
    public string KindText => Registration.Kind switch { SoftwareKind.Desktop => "桌面软件", SoftwareKind.Store => "商店应用", SoftwareKind.Steam => "Steam 游戏", SoftwareKind.Epic => "Epic 内容", SoftwareKind.Orphan => "未登记目录", SoftwareKind.DirectoryResidue => "疑似目录残留", _ => "疑似注册表残留" };
    public string Status { get; internal set; } = "";
    public string Evidence { get; internal set; } = "";
    public bool CanUninstall { get; internal set; }
    public long? LogicalBytes { get; internal set; }
    public long? AllocatedBytes { get; internal set; }
    public long SortBytes => AllocatedBytes ?? LogicalBytes ?? Registration.ReportedBytes;
    public string SizeText => LogicalBytes is { } bytes ? CleanupService.FormatBytes(bytes) : Registration.ReportedBytes > 0 ? CleanupService.FormatBytes(Registration.ReportedBytes) + "（登记）" : "未知";
    public string AllocatedText => AllocatedBytes is { } bytes ? CleanupService.FormatBytes(bytes) : "未测量";
    public string SizeBasis { get; internal set; } = "登记/平台报告，未测量";
    public long FileCount { get; internal set; }
    public bool IsSelected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record SoftwareResult(IReadOnlyList<SoftwareItem> Items, IReadOnlyList<string> Warnings, bool Limited, DateTime ScannedUtc);
public sealed record SoftwareHistory(SoftwareRegistration Registration, int AbsentScans, DateTime LastSeenUtc);
public sealed record SoftwareOperation(DateTime TimeUtc, string Name, string Id, string Action, string Result, string Reason, long RestorePoint = 0, int? ProcessId = null);
public sealed record SoftwareBatchResult(IReadOnlyList<SoftwareOperation> Records, bool DryRun);
public sealed record SoftwareRestorePoint(bool Success, long Sequence, string Description, string Error);
public sealed record SoftwareLaunchResult(int? ExitCode, int? ProcessId, bool HandedToPlatform = false, bool Canceled = false);

public interface ISoftwareInventory
{
    SoftwareSnapshot Read(bool store, bool games, CancellationToken token);
    SoftwareRegistration? Refresh(SoftwareRegistration original, CancellationToken token);
    IReadOnlyList<SoftwareRegistration> FindDirectories(IReadOnlyList<SoftwareRegistration> installed, IReadOnlyList<SoftwareHistory> history, SoftwareOptions options, CancellationToken token);
}
public interface ISoftwareRestorePoint { Task<SoftwareRestorePoint> CreateAsync(CancellationToken token); }
public interface ISoftwareUninstaller { Task<SoftwareLaunchResult> RunAsync(SoftwareRegistration entry, CancellationToken token); }
