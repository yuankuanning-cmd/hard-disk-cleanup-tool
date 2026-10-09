using System.ComponentModel;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Models;

public enum DevelopmentKind { Build, Cache, Codex, Worktree, Docker }
public enum DevelopmentSafety { Regenerable, Uncommitted, Unknown, InUse, Protected }

public sealed class DevelopmentItem : INotifyPropertyChanged
{
    private bool _selected;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public DevelopmentKind Kind { get; init; }
    public DevelopmentSafety Safety { get; internal set; }
    public string Project { get; init; } = "";
    public string TaskState { get; internal set; } = "";
    public string GitState { get; internal set; } = "";
    public string Evidence { get; internal set; } = "";
    public string DuplicateHint { get; internal set; } = "";
    public DateTime LastModifiedUtc { get; internal set; }
    public long LogicalBytes { get; internal set; }
    public long AllocatedBytes { get; internal set; }
    public long TheoreticalBytes { get; internal set; }
    public long FileCount { get; internal set; }
    internal int EntryCount { get; set; }
    public bool Complete { get; internal set; }
    public bool CanRecycle { get; internal set; }
    public bool CanRemoveWorktree { get; internal set; }
    public bool CanManuallyRecycle { get; internal set; }
    public string SizeText => CleanupService.FormatBytes(LogicalBytes);
    public string AllocatedText => Kind == DevelopmentKind.Docker ? "未测量" : CleanupService.FormatBytes(AllocatedBytes);
    public string ModifiedText => LastModifiedUtc == default ? "—" : LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string KindText => Kind switch { DevelopmentKind.Build => "构建 / 依赖", DevelopmentKind.Cache => "包缓存", DevelopmentKind.Codex => "Codex 产物", DevelopmentKind.Worktree => "Git 工作树", _ => "Docker 构建" };
    public string SafetyText => Safety switch { DevelopmentSafety.Regenerable => "可重新生成", DevelopmentSafety.Uncommitted => "未提交内容", DevelopmentSafety.InUse => "仍被引用", DevelopmentSafety.Protected => "受保护", _ => "未知文件" };
    public string ActionText => !Complete ? "未读完整 · 不可清理" : CanRecycle ? "可回收（需再次检查）" : CanRemoveWorktree ? "可由 Git 移除" : CanManuallyRecycle ? "归档成果 · 需备份后人工确认" : "仅分析 / 人工核实";
    public bool IsSelected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record DevelopmentOptions(IReadOnlyList<string> Roots, string CodexHome, bool IncludeCodex = true,
    bool IncludeCaches = false, bool IncludeDocker = false, bool CompareBuilds = true,
    string MergeTarget = "", IReadOnlyList<string>? Exclusions = null, int MaximumProjects = 200,
    int MaximumEntries = 1_000_000);
public sealed record DevelopmentResult(IReadOnlyList<DevelopmentItem> Items, IReadOnlyList<string> Warnings,
    int ProjectCount, int TaskCount, bool Limited, string ManifestPath);
public sealed record DevelopmentOperation(string Path, string Action, string Result, string Reason,
    long TheoreticalBytes, long ActuallyReleasedBytes, DateTime TimeUtc);
public sealed record DevelopmentOperationResult(IReadOnlyList<DevelopmentOperation> Records, bool DryRun)
{
    public int Succeeded => Records.Count(r => r.Result is "已回收" or "已移除" or "模拟通过");
    public int Failed => Records.Count - Succeeded;
}
