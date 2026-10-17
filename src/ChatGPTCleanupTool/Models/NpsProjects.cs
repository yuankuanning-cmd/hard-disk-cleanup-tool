using System.ComponentModel;
using System.Text.Json.Serialization;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Models;

public enum NpsProjectKind { Project, Backup, Recovery, Suspected, StandaloneModel, StructuredProject }
public enum NpsCleanupKind { Generated, Results, WholeProject, ModelFile }
public sealed record NpsProjectItem(string Id, string Name, string FullPath, string ManifestPath, NpsProjectKind Kind,
    bool Recognized, string Version, string Created, int ModelFiles, string Evidence)
{
    public string KindText => Kind switch { NpsProjectKind.Project => "清单识别工程", NpsProjectKind.StructuredProject => "综合结构识别工程", NpsProjectKind.Backup => "工程备份（路径推断）", NpsProjectKind.Recovery => "工具恢复快照", NpsProjectKind.StandaloneModel => "待归属模型线索", _ => "待核实工程目录" };
    [JsonInclude] public long? LogicalBytes { get; internal set; }
    [JsonInclude] public long? AllocatedBytes { get; internal set; }
    [JsonInclude] public long? FileCount { get; internal set; }
    [JsonInclude] public DateTime? LastModifiedUtc { get; internal set; }
    [JsonIgnore] public NpsCleanupItem? Cleanup { get; internal set; }
    public string SizeText => LogicalBytes is { } n ? CleanupService.FormatBytes(n) + (Cleanup?.PartialSize == true ? "（部分）" : "") : Cleanup?.Evidence.Contains("取消") == true ? "未统计（取消）" : "正在统计…";
    public string AllocatedText => AllocatedBytes is { } n ? CleanupService.FormatBytes(n) + (Cleanup?.PartialSize == true ? "（部分）" : "") : Cleanup?.Evidence.Contains("取消") == true ? "未统计（取消）" : "正在统计…";
    public string DeleteScope => Kind == NpsProjectKind.StandaloneModel ? "仅线索，不单独删除" : "整个工程文件夹";
    public string CleanupStatus => Cleanup?.Evidence ?? "等待自动统计";
}
public sealed class NpsCleanupItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string FullPath { get; init; }
    public required NpsCleanupKind Kind { get; init; }
    public string Name => System.IO.Path.GetFileName(FullPath);
    public string KindText => Kind switch { NpsCleanupKind.Generated => "编译/代码生成", NpsCleanupKind.Results => "仿真结果（需确认）", NpsCleanupKind.ModelFile => "仅此模型文件", _ => "整个工程" };
    public long LogicalBytes { get; internal set; }
    public long AllocatedBytes { get; internal set; }
    public long FileCount { get; internal set; }
    public string SizeText => CleanupService.FormatBytes(LogicalBytes);
    public string AllocatedText => CleanupService.FormatBytes(AllocatedBytes);
    public bool CanRecycle { get; internal set; }
    public bool PartialSize { get; internal set; }
    public string Evidence { get; internal set; } = "";
    private bool _selected;
    public bool IsSelected { get => _selected; set { _selected = value && CanRecycle; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record NpsScanOptions(IReadOnlyList<string> Roots, int MaximumMarkers = 100_000, int MaximumProjects = 20_000);
public sealed record NpsScanResult(IReadOnlyList<NpsProjectItem> Projects, IReadOnlyList<string> Warnings, bool Complete,
    long VisitedFiles, long VisitedDirectories, long Skipped, DateTime ScannedUtc, string Scope, string Scheduling);
public sealed record NpsOperation(DateTime TimeUtc, string Path, string Action, string Result, string Reason);
public sealed record NpsCleanupResult(IReadOnlyList<NpsOperation> Records, bool DryRun);
internal sealed record NpsMarker(string Path, bool Model, bool Recognized, string Version, string Created, string Evidence, bool Structure = false);
internal sealed record NpsRootEvidence(string Root, string ManifestPath, bool Confirmed, bool HasModelDirectory,
    int CompanionDirectories, bool SupportedModel, bool NpsScript, string Description);
