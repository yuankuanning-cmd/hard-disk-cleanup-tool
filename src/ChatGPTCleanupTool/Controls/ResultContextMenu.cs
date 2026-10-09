using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Controls;

internal sealed record MenuTarget(object Item, string Name, string Path, string Details)
{
    internal static MenuTarget From(object item) => item switch
    {
        CleanupCategoryItem c => new(c, c.Name, "", $"{c.Name}\n风险：{c.Risk}\n文件：{c.FileCount:N0} · {c.DisplaySize}\n{c.Description}"),
        CleanupFileItem f => new(f, f.Name, f.FullPath, $"{f.FullPath}\n大小：{f.DisplaySize}\n修改：{f.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n永久排除：{f.IsExcluded}"),
        CodexSmartFileItem f => new(f, System.IO.Path.GetFileName(f.FullPath), f.FullPath, $"{f.FullPath}\n{f.Recommendation} · {f.RecommendationReason}\n逻辑：{f.LogicalSize} · 占用：{f.AllocatedSize}\n年龄/评分不是删除授权；未知产物请到开发环境页核实任务归属。"),
        DiskTreeNode n => new(n, n.Name, n.FullPath, $"{n.FullPath}\n逻辑：{n.LogicalSize} · 占用：{n.AllocatedSize}\n文件：{n.FileCount:N0}\n{n.State}"),
        DuplicateFileItem f => new(f, f.Name, f.FullPath, $"{f.FullPath}\n重复组：{f.GroupNumber} · {f.Copies} 个副本\n{f.Role}\n逻辑：{f.LogicalSize} · 占用：{f.AllocatedSize}\n{f.ImageInformation}\n完整哈希已验证：{f.IsFullHashVerified}\n处理前仍需重新哈希，组内至少保留一份。"),
        ExternalStorageItem f => new(f, f.Name, FilePath(f.PathOrDetail), $"{f.Source} · {f.Name}\n{f.Status}\n逻辑：{f.LogicalSize} · 占用：{f.AllocatedSize} · 可回收：{f.ReclaimableSize}\n{f.PathOrDetail}\nWSL/Docker虚拟磁盘不能当普通垃圾文件删除。"),
        CleanupHistoryItem h => new(h, h.Operation, "", $"{h.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {h.Operation}\n项目：{h.ItemCount} · 释放：{h.ReclaimedSize} · 失败：{h.FailureCount}\n{h.Status}"),
        MediaResultItem m => new(m, m.Name, m.FullPath, $"{m.FullPath}\n{m.KindText} · {m.Dimensions} · {m.SizeText}\n{m.TakenText}\n{m.PrivacyText}\n{m.Advice}\n相似不等于内容完全相同，不提供自动删除。"),
        DevelopmentItem d => new(d, d.Name, d.Kind == DevelopmentKind.Docker ? "" : d.FullPath, $"{d.FullPath}\n{d.SafetyText} · {d.ActionText}\n项目：{d.Project}\n{d.GitState}\n{d.TaskState}\n{d.Evidence}\n{d.DuplicateHint}"),
        SoftwareItem s => new(s, s.Name, s.FullPath, $"{s.Name} · {s.Publisher} · {s.Version}\n{s.FullPath}\n{s.Status}\n逻辑/登记：{s.SizeText} · 实际：{s.AllocatedText}\n{s.SizeBasis}\n{s.Evidence}"),
        DevelopmentOperation r => new(r, r.Action, r.Path, $"{r.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {r.Action}\n{r.Path}\n{r.Result} · {r.Reason}\n理论字节：{r.TheoreticalBytes} · 实际释放字节：{r.ActuallyReleasedBytes}"),
        SoftwareOperation r => new(r, r.Name, "", $"{r.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {r.Name}\n{r.Action} · {r.Result}\n{r.Reason}\n还原点：{r.RestorePoint} · 进程：{r.ProcessId}"),
        DuplicateOperationRecord r => new(r, r.Action, r.FilePath, $"{r.LocalTime:yyyy-MM-dd HH:mm:ss} · {r.Action}\n{r.Status} · {r.Reason}\n处理：{r.FilePath}\n保留：{r.KeeperPath}\n恢复：{r.BackupPath}\n理论：{r.TheoreticalSize} · 已释放：{r.ActualSize}"),
        string path => new(path, System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path, path, path),
        _ => new(item, item.ToString() ?? "", "", item.ToString() ?? "")
    };
    private static string FilePath(string text) => ResultContextMenu.IsFilePath(text) ? text : "";
}

internal sealed class ResultMenuSession
{
    internal ContextMenu Menu { get; }
    internal FrameworkElement Owner { get; }
    internal IReadOnlyList<MenuTarget> Rows { get; }
    internal MenuTarget? Current { get; }
    internal bool TooMany { get; }
    private readonly Func<bool> _blocked;
    internal ResultMenuSession(ContextMenu menu, FrameworkElement owner, IReadOnlyList<MenuTarget> rows, MenuTarget? current, bool tooMany, Func<bool> blocked)
    { Menu = menu; Owner = owner; Rows = rows; Current = current; TooMany = tooMany; _blocked = blocked; }
    internal void Separator() { if (Menu.Items.Count > 0 && Menu.Items[^1] is not System.Windows.Controls.Separator) Menu.Items.Add(new System.Windows.Controls.Separator()); }
    internal void Add(string id, string label, Action action, bool enabled = true, bool changesState = false, string? hint = null, Func<bool>? available = null, bool allowLargeSelection = false)
    {
        bool Ready() => enabled && (!changesState || (!_blocked() && (allowLargeSelection || !TooMany))) && (available?.Invoke() ?? true);
        var entry = new MenuItem { Tag = id, Header = label, IsEnabled = Ready(), Padding = new Thickness(10, 5, 14, 5), MinHeight = 30, ToolTip = hint };
        entry.Opacity = entry.IsEnabled ? 1 : .45;
        entry.IsEnabledChanged += (_, _) => entry.Opacity = entry.IsEnabled ? 1 : .45;
        ToolTipService.SetShowOnDisabled(entry, true);
        entry.Click += (_, _) =>
        {
            if (!Ready()) return;
            try { action(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(Owner), "操作未完成：\n" + ex.Message, "右键菜单", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        Menu.Items.Add(entry);
    }
}

/// <summary>One menu policy for rows, lists, trees and page backgrounds. Never enumerates an ItemsSource.</summary>
internal static class ResultContextMenu
{
    internal const int MaximumRows = 1000;
    private sealed class Binding(FrameworkElement owner, Action<ResultMenuSession> actions, Func<bool> blocked, Func<object?>? current)
    {
        internal readonly FrameworkElement Owner = owner;
        internal readonly Action<ResultMenuSession> Actions = actions;
        internal readonly Func<bool> Blocked = blocked;
        internal readonly Func<object?>? Current = current;
        internal object? PointerItem;
        internal bool PointerPending;
    }
    private static readonly DependencyProperty BindingProperty = DependencyProperty.RegisterAttached("MenuBinding", typeof(Binding), typeof(ResultContextMenu));
    internal static void Attach(FrameworkElement owner, Action<ResultMenuSession> actions, Func<bool>? blocked = null, Func<object?>? current = null)
    {
        if (owner.GetValue(BindingProperty) is Binding) throw new InvalidOperationException("菜单不可重复绑定。");
        var binding = new Binding(owner, actions, blocked ?? (() => false), current); owner.SetValue(BindingProperty, binding);
        System.Windows.Automation.AutomationProperties.SetHelpText(owner, "右键查看操作；菜单键或Shift+F10打开菜单；Ctrl+C复制高亮所选路径/记录。");
        owner.ContextMenu = NewMenu();
        owner.ContextMenuOpening += (_, e) =>
        {
            if (!OwnsEvent(owner, e.OriginalSource as DependencyObject)) return;
            Prepare(binding);
        };
        owner.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (!OwnsEvent(owner, e.OriginalSource as DependencyObject)) return;
            binding.PointerPending = true;
            var container = owner is TreeView ? FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) :
                owner is ItemsControl control ? ItemsControl.ContainerFromElement(control, e.OriginalSource as DependencyObject) : null;
            var item = container switch { DataGridRow row => row.Item, ListBoxItem row => row.DataContext, TreeViewItem row => row.DataContext, _ => null };
            binding.PointerItem = item;
            SelectPointer(owner, item, container);
        };
        owner.PreviewKeyDown += (_, e) =>
        {
            if (!OwnsEvent(owner, e.OriginalSource as DependencyObject) || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
            if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                binding.PointerPending = false; Prepare(binding);
                owner.ContextMenu!.PlacementTarget = owner; owner.ContextMenu.Placement = PlacementMode.Bottom; owner.ContextMenu.IsOpen = true; e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && owner is ItemsControl)
            {
                var session = Snapshot(binding, NewMenu());
                CopyRows(session); e.Handled = true; // Prevent DataGrid's default eager cell clipboard expansion.
            }
        };
    }
    internal static ContextMenu NewMenu()
    {
        var menu = new ContextMenu
        {
            FontFamily = new FontFamily("Microsoft YaHei"), FontSize = 13, FontWeight = FontWeights.Normal,
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(210, 219, 230)), BorderThickness = new Thickness(1),
            Padding = new Thickness(4), MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40), MinWidth = 230
        };
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && menu.Tag is ResultMenuSession session)
            { CopyRows(session); menu.IsOpen = false; e.Handled = true; }
        };
        return menu;
    }
    private static void CopyRows(ResultMenuSession session)
    {
        try
        {
            if (session.TooMany) throw new IOException("高亮超过1000行，请使用CSV导出。");
            Copy(session.Rows.Select(r => r.Path.Length > 0 ? r.Path : r.Details));
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(session.Owner), "复制未完成：\n" + ex.Message, "复制路径/记录"); }
    }
    private static bool OwnsEvent(FrameworkElement owner, DependencyObject? source)
    {
        for (var node = source; node is not null; node = Parent(node))
        {
            if (node is TextBox or PasswordBox && node != owner) return false; // Keep native text editing/copy menus.
            if (node is FrameworkElement element && element.ContextMenu is not null) return ReferenceEquals(element, owner);
        }
        return source == owner;
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    { for (; node is not null; node = Parent(node)) if (node is T result) return result; return null; }
    internal static void SelectPointer(FrameworkElement owner, object? item, DependencyObject? container = null)
    {
        if (item is null) return; // Blank clicks must not reuse the previous row.
        if (owner is DataGrid grid && !grid.SelectedItems.Contains(item)) { if (grid.SelectionMode != DataGridSelectionMode.Single) grid.SelectedItems.Clear(); grid.SelectedItem = item; }
        else if (owner is ListBox list && !list.SelectedItems.Contains(item)) { if (list.SelectionMode != SelectionMode.Single) list.SelectedItems.Clear(); list.SelectedItem = item; }
        else if (owner is TreeView && container is TreeViewItem tree) tree.IsSelected = true;
    }
    private static ResultMenuSession Snapshot(Binding binding, ContextMenu menu)
    {
        IEnumerable selected = binding.Owner switch { DataGrid g => g.SelectedItems, ListBox l => l.SelectedItems, TreeView t when t.SelectedItem is { } row => new[] { row }, _ => binding.Current?.Invoke() is { } row ? new[] { row } : Array.Empty<object>() };
        var objects = binding.PointerPending && binding.PointerItem is null ? [] : selected.Cast<object>().Take(MaximumRows + 1).ToArray();
        var current = binding.PointerPending ? binding.PointerItem : objects.FirstOrDefault();
        return new(menu, binding.Owner, objects.Length > MaximumRows ? [] : objects.Select(MenuTarget.From).ToArray(), current is null ? null : MenuTarget.From(current), objects.Length > MaximumRows, binding.Blocked);
    }
    private static void Prepare(Binding binding)
    {
        var menu = binding.Owner.ContextMenu!; menu.Items.Clear();
        var session = Snapshot(binding, menu); binding.PointerPending = false; Populate(session); binding.Actions(session);
    }
    internal static ContextMenu BuildForTest(FrameworkElement owner, object? pointerItem = null, bool pointer = false)
    {
        var binding = (Binding)owner.GetValue(BindingProperty); binding.PointerPending = pointer; binding.PointerItem = pointerItem;
        if (pointer) SelectPointer(owner, pointerItem);
        Prepare(binding); return owner.ContextMenu!;
    }
    internal static void Populate(ResultMenuSession session)
    {
        session.Menu.Tag = session; // Frozen target also remains correct while the popup owns keyboard focus.
        var current = session.Current; var rows = session.Rows; bool any = rows.Count > 0;
        var title = session.TooMany ? "选中超过1000行，请使用导出功能" : current is null ? "页面操作 · 右键一行可查看更多" : $"当前：{(current.Name.Length > 55 ? current.Name[..55] + "…" : current.Name)}" + (rows.Count > 1 ? $" · 已选{rows.Count}行" : "");
        session.Menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, Opacity = .65, ToolTip = current?.Path, Padding = new Thickness(10, 6, 14, 6) });
        if (current is not null)
        {
            session.Add("open-location", "打开当前项的位置", () => OpenLocation(current.Path), IsFilePath(current.Path), hint: "文件会在资源管理器中定位；目录会直接打开。不执行文件。");
            session.Add("properties", "当前项的系统属性…", () => ShowProperties(current.Path, session.Owner), IsFilePath(current.Path), hint: "仅支持本地文件/目录；注册表与平台描述不是文件路径。");
        }
        if (any)
        {
            session.Add("copy-paths", rows.Any(r => r.Path.Length > 0) ? "复制所选路径" : "复制所选记录", () => Copy(rows.Select(r => r.Path.Length > 0 ? r.Path : r.Details)), hint: "Ctrl+C；多选时逐行复制，只处理当前高亮行，不是清理勾选项。");
            ((MenuItem)session.Menu.Items[^1]).InputGestureText = "Ctrl+C";
            session.Add("copy-names", "复制所选名称", () => Copy(rows.Select(r => r.Name)));
            session.Add("copy-details", "复制所选详细信息", () => Copy(rows.Select(r => r.Details)));
            session.Add("details", "查看所选扫描详情…", () => ShowDetails(rows, session.Owner));
        }
    }
    internal static bool IsFilePath(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && Path.IsPathFullyQualified(path) && !path.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) && !path.Contains('\n'); }
        catch { return false; }
    }
    internal static void OpenLocation(string path)
    {
        if (!IsFilePath(path)) throw new IOException("不是可打开的本地文件路径。");
        if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("该路径已不存在或不可访问，请重新扫描。\n" + path);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
        start.Arguments = File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"";
        Process.Start(start);
    }
    private static void ShowProperties(string path, FrameworkElement owner)
    {
        if (!IsFilePath(path) || !File.Exists(path) && !Directory.Exists(path)) throw new IOException("路径不存在或不可访问。");
        if (!SHObjectProperties(new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(owner)).Handle, 2, path, null)) throw new IOException("Windows无法显示该项的属性。");
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHObjectProperties", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint type, string path, string? page);
    private static void Copy(IEnumerable<string> values)
    {
        var text = string.Join(Environment.NewLine, values.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        if (text.Length > 1024 * 1024) throw new IOException("复制内容超过1MiB，请使用CSV导出。");
        if (text.Length > 0) Clipboard.SetText(text);
    }
    private static void ShowDetails(IReadOnlyList<MenuTarget> rows, FrameworkElement owner)
    {
        var text = string.Join("\n\n────────────────\n\n", rows.Take(20).Select(r => r.Details));
        if (rows.Count > 20) text += "\n\n仅预览前20项；完整内容请复制或导出。";
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        new Window { Title = "扫描详情（只读）", Content = box, Owner = Window.GetWindow(owner), Width = 740, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
}
