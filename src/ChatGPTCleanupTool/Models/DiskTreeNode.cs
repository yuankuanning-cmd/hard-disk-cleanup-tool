using System.IO;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Models;

/// <summary>A compact scan index; formatted strings and UI containers are created only on demand.</summary>
public sealed class DiskTreeNode
{
    private List<DiskTreeNode>? _children;
    private string? _directoryPath;
    internal IndexedNodeSource? Source { get; init; }
    internal long IndexId { get; init; }
    internal long? ParentIndexId { get; init; }
    internal string FileIdentity { get; init; } = "";
    private DiskTreeNode? _parent;
    private IReadOnlyList<DiskTreeNode>? _indexedChildren;
    private IReadOnlyList<DiskTreeNode>? _directories;
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsScanRoot { get; init; }
    public DiskTreeNode? Parent { get => _parent ?? (ParentIndexId is { } id ? Source?.GetNode(id) : null); internal set => _parent = value; }
    public string FullPath
    {
        get => _directoryPath ?? Path.Combine(Parent!.FullPath, Name);
        init => _directoryPath = value;
    }
    public long LogicalBytes { get; internal set; }
    /// <summary>Unique allocation attributed once to the lexically first scanned hard-link name.</summary>
    public long AllocatedBytes { get; internal set; }
    public long FileCount { get; internal set; }
    public DateTime LastWriteTimeUtc { get; init; }
    public string Extension { get; init; } = string.Empty;
    public string StorageNote { get; internal set; } = string.Empty;
    public bool IsIncomplete { get; internal set; }
    public bool IsExcluded { get; internal set; }
    public long? GrowthBytes { get; internal set; }
    public long? LogicalGrowthBytes { get; internal set; }
    public double GrowthBytesPerDay { get; internal set; }
    public DateTimeOffset? ComparedAt { get; internal set; }
    public DateTimeOffset? ComparedAtLocal => ComparedAt?.ToLocalTime();
    public IReadOnlyList<DiskTreeNode> Children => Source is not null ? _indexedChildren ??= Source.GetChildren(this) : _children ?? (IReadOnlyList<DiskTreeNode>)Array.Empty<DiskTreeNode>();
    public IReadOnlyList<DiskTreeNode> Directories { get => _directories ??= (IReadOnlyList<DiskTreeNode>?)Source?.GetChildren(this, directoriesOnly: true) ?? Array.Empty<DiskTreeNode>(); internal set => _directories = value; }
    public bool CanRecycle => !IsScanRoot && !IsExcluded && !IsIncomplete && (Source?.CanRecycle ?? true);
    public string LogicalSize => CleanupService.FormatBytes(LogicalBytes);
    public string AllocatedSize => CleanupService.FormatBytes(AllocatedBytes);
    public string Kind => IsDirectory ? "目录" : "文件";
    public string State => IsExcluded ? "已排除（不计入）" : IsIncomplete ? "部分结果" : StorageNote;
    public string GrowthSize => GrowthBytes is { } n ? SignedBytes(n) : "—";
    public string LogicalGrowthSize => LogicalGrowthBytes is { } n ? SignedBytes(n) : "—";
    public string GrowthRate => ComparedAt is not null
        ? SignedBytes((long)Math.Clamp(GrowthBytesPerDay, long.MinValue + 1d, long.MaxValue - 1d)) + "/天" : "—";
    public string TreeLabel => $"{Name}   {AllocatedSize}" + (IsIncomplete ? " · 部分" : IsExcluded ? " · 排除" : "");

    internal void Add(DiskTreeNode child)
    {
        child.Parent = this;
        (_children ??= []).Add(child);
    }

    internal void Remove(DiskTreeNode child) => _children?.Remove(child);

    internal void Finish()
    {
        if (!IsDirectory) return;
        // One post-order pass, no recursive traversal or per-file ancestor updates.
        if (_children is not null)
        {
            _children.Sort((a, b) =>
            {
                var size = b.AllocatedBytes.CompareTo(a.AllocatedBytes);
                return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            });
            LogicalBytes = _children.Sum(n => n.LogicalBytes);
            AllocatedBytes = _children.Sum(n => n.AllocatedBytes);
            FileCount = _children.Sum(n => n.FileCount);
            IsIncomplete |= _children.Any(n => n.IsIncomplete);
            Directories = _children.Where(n => n.IsDirectory).ToArray();
        }
    }

    public IEnumerable<DiskTreeNode> DescendantsAndSelf()
    {
        if (Source is not null)
        {
            foreach (var node in Source.EnumerateSubtree(IndexId)) yield return node;
            yield break;
        }
        var pending = new Stack<DiskTreeNode>();
        pending.Push(this);
        while (pending.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    private static string SignedBytes(long bytes) =>
        (bytes > 0 ? "+" : bytes < 0 ? "−" : "") + CleanupService.FormatBytes(Math.Abs(bytes));
}
