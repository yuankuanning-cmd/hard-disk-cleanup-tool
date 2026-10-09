using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Controls;

public sealed record TreemapTile(DiskTreeNode Node, Rect Bounds, bool IsHeader = false, int AggregatedCount = 0);

/// <summary>Weighted, recursive binary treemap, capped by visible tile count, never one WPF element per file.</summary>
public static class TreemapLayout
{
    public static IReadOnlyList<TreemapTile> Build(DiskTreeNode node, Size size, bool allocated, int budget = 1800)
    {
        var tiles = new List<TreemapTile>();
        if (size.Width < 2 || size.Height < 2 || budget < 1) return tiles;
        LayoutChildren(node, new Rect(size), 0, 0);
        return tiles;

        void LayoutChildren(DiskTreeNode parent, Rect area, int depth, int reservedSiblings)
        {
            var total = (double)(allocated ? parent.AllocatedBytes : parent.LogicalBytes);
            if (total <= 0 || area.Width < 2 || area.Height < 2) return;
            var entries = new List<(DiskTreeNode Node, double Weight, int Others)>();
            var used = 0d;
            var available = budget - tiles.Count - reservedSiblings;
            if (available <= 1)
            {
                if (available == 1) tiles.Add(new(parent, area, AggregatedCount: parent.Children.Count));
                return;
            }
            // Children are scan-sorted. Do not sort/materialize a million files on every resize.
            var limit = Math.Max(1, Math.Min(350, available - 1));
            var children = parent.Source?.TopChildren(parent, allocated, limit + 1) ?? parent.Children;
            var i = 0;
            for (; i < children.Count && entries.Count < limit; i++)
            {
                var child = children[i];
                var weight = (double)(allocated ? child.AllocatedBytes : child.LogicalBytes);
                if (weight <= 0) continue;
                entries.Add((child, weight, 0));
                used += weight;
            }
            if (i < parent.Children.Count && total > used)
                entries.Add((parent, total - used, parent.Children.Count - i));
            if (entries.Count == 0) return;
            var sums = new double[entries.Count + 1];
            for (var j = 0; j < entries.Count; j++) sums[j + 1] = sums[j] + entries[j].Weight;
            var placedLeaves = 0;
            Split(0, entries.Count, area);

            void Split(int start, int end, Rect rect)
            {
                if (end - start == 1)
                {
                    var entry = entries[start];
                    tiles.Add(new(entry.Node, rect, AggregatedCount: entry.Others));
                    placedLeaves++;
                    var remainingSiblings = reservedSiblings + entries.Count - placedLeaves;
                    if (entry.Others == 0 && entry.Node.IsDirectory && depth < 3 &&
                        rect.Width > 90 && rect.Height > 70 && tiles.Count + remainingSiblings + 2 <= budget)
                    {
                        var header = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, 23);
                        tiles.Add(new(entry.Node, header, true));
                        LayoutChildren(entry.Node, new Rect(rect.X + 3, rect.Y + 27, rect.Width - 6, rect.Height - 30), depth + 1, remainingSiblings);
                    }
                    return;
                }
                var weight = sums[end] - sums[start];
                var target = sums[start] + weight / 2;
                var left = start + 1;
                var right = end - 1;
                while (left < right)
                {
                    var middle = (left + right) / 2;
                    if (sums[middle] < target) left = middle + 1; else right = middle;
                }
                var split = left;
                if (split > start + 1 && Math.Abs(sums[split - 1] - target) < Math.Abs(sums[split] - target)) split--;
                var fraction = (sums[split] - sums[start]) / weight;
                if (rect.Width >= rect.Height)
                {
                    var width = rect.Width * fraction;
                    Split(start, split, new(rect.X, rect.Y, width, rect.Height));
                    Split(split, end, new(rect.X + width, rect.Y, rect.Width - width, rect.Height));
                }
                else
                {
                    var height = rect.Height * fraction;
                    Split(start, split, new(rect.X, rect.Y, rect.Width, height));
                    Split(split, end, new(rect.X, rect.Y + height, rect.Width, rect.Height - height));
                }
            }
        }
    }
}

public sealed class DiskTreemap : FrameworkElement
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(45, 113, 181), Color.FromRgb(18, 133, 117), Color.FromRgb(148, 89, 185),
        Color.FromRgb(191, 115, 40), Color.FromRgb(177, 72, 112), Color.FromRgb(58, 135, 163),
        Color.FromRgb(101, 129, 53), Color.FromRgb(190, 83, 68), Color.FromRgb(87, 101, 185),
        Color.FromRgb(128, 106, 78), Color.FromRgb(42, 127, 143), Color.FromRgb(142, 92, 147)
    ];
    private static readonly SolidColorBrush[] FileBrushes = Palette.Select(CreateBrush).ToArray();
    private static readonly SolidColorBrush DirectoryBrush = CreateBrush(Color.FromRgb(69, 88, 116));
    private static readonly SolidColorBrush OtherBrush = CreateBrush(Color.FromRgb(120, 133, 151));
    private static readonly Pen BorderPen = new(CreateBrush(Color.FromRgb(237, 243, 249)), 1);
    private static readonly Typeface Typeface = new("Microsoft YaHei UI");
    private IReadOnlyList<TreemapTile> _tiles = [];
    private DiskTreeNode? _root;
    private DiskTreeNode? _selected;
    private TreemapTile? _hover;
    private bool _allocated = true;
    public DiskTreeNode? Root { get => _root; set { _root = value; Rebuild(); } }
    public DiskTreeNode? SelectedNode { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public bool UseAllocated { get => _allocated; set { _allocated = value; Rebuild(); } }
    public event Action<DiskTreeNode>? NodeClicked;
    public event Action<DiskTreeNode>? AggregatedClicked;
    public event Action<DiskTreeNode>? ContextRequested;
    public IReadOnlyList<TreemapTile> Tiles => _tiles;

    public DiskTreemap()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        Focusable = true;
        SizeChanged += (_, _) => Rebuild();
    }
    public static Brush ExtensionBrush(string extension)
    {
        uint hash = 2166136261;
        foreach (var character in extension.ToLowerInvariant()) hash = (hash ^ character) * 16777619;
        return FileBrushes[hash % FileBrushes.Length];
    }
    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    private void Rebuild()
    {
        _tiles = _root is null ? [] : TreemapLayout.Build(_root, RenderSize, _allocated);
        _hover = null; ToolTip = null; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(CreateBrush(Color.FromRgb(245, 248, 252)), null, new Rect(RenderSize));
        if (_tiles.Count == 0)
        {
            DrawText(context, _root is null ? "扫描后，在这里探索空间分布" : "没有可绘制的占用；零字节文件请在明细中查看",
                new Rect(18, 18, Math.Max(0, ActualWidth - 36), 48), Brushes.SlateGray, 14);
            return;
        }
        foreach (var tile in _tiles)
        {
            var rect = tile.Bounds;
            var fill = tile.AggregatedCount > 0 ? OtherBrush : tile.Node.IsDirectory ? DirectoryBrush : ExtensionBrush(tile.Node.Extension);
            context.DrawRectangle(fill, BorderPen, rect);
            if (rect.Width > 55 && rect.Height > 18)
            {
                var name = tile.AggregatedCount > 0 ? $"其余 {tile.AggregatedCount:N0} 项 · 见明细" : tile.Node.Name;
                DrawText(context, name, new Rect(rect.X + 6, rect.Y + 3, Math.Max(1, rect.Width - 12), 20), Brushes.White, 12);
                if (!tile.IsHeader && rect.Height > 47 && !tile.Node.IsDirectory)
                    DrawText(context, _allocated ? tile.Node.AllocatedSize : tile.Node.LogicalSize,
                        new Rect(rect.X + 6, rect.Y + 24, Math.Max(1, rect.Width - 12), 18), Brushes.White, 11);
            }
        }
        foreach (var tile in _tiles.Where(t => t.Node == _selected && !t.IsHeader))
        {
            var rect = tile.Bounds; rect.Inflate(-1, -1);
            if (rect.Width > 0 && rect.Height > 0) context.DrawRectangle(null, new Pen(Brushes.White, 3), rect);
        }
    }
    private void DrawText(DrawingContext context, string text, Rect bounds, Brush color, double size)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface, size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = bounds.Width, MaxTextHeight = bounds.Height, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(formatted, bounds.TopLeft);
    }
    public TreemapTile? HitTestTile(Point point) => _tiles.LastOrDefault(t => t.Bounds.Contains(point));
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tile = HitTestTile(e.GetPosition(this));
        if (tile == _hover) return;
        _hover = tile;
        ToolTip = tile is null ? null : tile.AggregatedCount > 0
            ? $"其余 {tile.AggregatedCount:N0} 项已合并显示；完整文件仍在目录明细中。"
            : $"{tile.Node.FullPath}\n逻辑大小：{tile.Node.LogicalSize}\n" +
              $"{(_allocated ? "实际占用" : "占用") }：{tile.Node.AllocatedSize}\n文件数：{tile.Node.FileCount:N0}\n{tile.Node.State}";
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (HitTestTile(e.GetPosition(this)) is { } tile)
        {
            SelectedNode = tile.Node;
            if (tile.AggregatedCount > 0) AggregatedClicked?.Invoke(tile.Node); else NodeClicked?.Invoke(tile.Node);
            e.Handled = true;
        }
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (HitTestTile(e.GetPosition(this)) is { } tile)
        { SelectedNode = tile.Node; ContextRequested?.Invoke(tile.Node); e.Handled = true; }
    }
}
