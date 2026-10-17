using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Win32;

namespace ChatGPTCleanupTool.Controls;

public sealed class OverviewNavigateEventArgs(string action, string? path = null) : EventArgs { public string Action { get; } = action; public string? Path { get; } = path; }
public partial class OverviewCenter : UserControl
{
    private readonly ObservableCollection<ScanBookmark> _favorites = [];
    public OverviewCenter() { InitializeComponent(); FavoriteItems.ItemsSource = _favorites; }
    public event EventHandler? RefreshRequested, FavoritesChanged;
    public event EventHandler<OverviewNavigateEventArgs>? NavigateRequested;
    public StorageOverview? Snapshot { get; private set; }
    public IReadOnlyList<string> FavoritePaths => _favorites.Select(p => p.Path).ToArray();
    public void SetLoading(bool loading) { RefreshButton.IsEnabled = !loading; if (loading) UpdatedLabel.Text = "正在读取本地磁盘容量，不遍历文件…"; }
    public void SetSnapshot(StorageOverview snapshot)
    {
        Snapshot = snapshot; DriveCards.ItemsSource = snapshot.Drives;
        TotalValue.Text = snapshot.Drives.Count == 0 ? "—" : CleanupService.FormatBytes(snapshot.Drives.Sum(d => d.TotalBytes));
        UsedValue.Text = snapshot.Drives.Count == 0 ? "—" : CleanupService.FormatBytes(snapshot.Drives.Sum(d => d.UsedBytes));
        FreeValue.Text = snapshot.Drives.Count == 0 ? "—" : CleanupService.FormatBytes(snapshot.Drives.Sum(d => d.FreeBytes));
        UpdatedLabel.Text = $"更新于 {snapshot.CapturedUtc.ToLocalTime():HH:mm:ss}  ·  只读取容量，没有遍历文件";
        DriveMessage.Text = snapshot.Drives.Count == 0 ? "尚无可读取的本地固定磁盘，请刷新重试。" : string.Join("；", snapshot.Warnings);
        if (snapshot.Drives.Any(d => d.UsedPercent >= 90)) DriveMessage.Text = "有磁盘使用率达到90%，建议先用空间树定位主要占用，再决定清理。" + (DriveMessage.Text.Length > 0 ? "\n" + DriveMessage.Text : "");
        RefreshButton.IsEnabled = true;
    }
    public void SetCandidates(IEnumerable<CleanupCategoryItem> categories)
    {
        var all = categories.ToArray();
        CandidateValue.Text = all.Length == 0 ? "未扫描" : CleanupService.FormatBytes(all.SelectMany(c => c.Files).Where(f => !f.IsExcluded).DistinctBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).Sum(f => f.Bytes));
    }
    public void SetFavoritePaths(IEnumerable<string>? paths)
    {
        _favorites.Clear(); foreach (var path in paths ?? []) { if (_favorites.Count >= 12) break; AddFavorite(path, false); } UpdateFavoritesHint();
    }
    public bool AddFavorite(string path, bool notify = true)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || _favorites.Count >= 12) return false;
            path = DiskAnalysisService.NormalizePath(path);
            if (_favorites.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return false;
            _favorites.Add(new(path)); UpdateFavoritesHint(); if (notify) FavoritesChanged?.Invoke(this, EventArgs.Empty); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
    public bool RemoveFavorite(string path, bool notify = true)
    {
        var row = _favorites.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); if (row is null) return false;
        _favorites.Remove(row); UpdateFavoritesHint(); if (notify) FavoritesChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    private void UpdateFavoritesHint() => FavoritesHint.Visibility = _favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void Navigate_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke(this, new(((Button)sender).Tag?.ToString() ?? "quick"));
    private void AnalyzeDrive_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke(this, new("analyze", ((Button)sender).Tag?.ToString()));
    private void RemoveFavorite_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string path) RemoveFavorite(path); }
    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "收藏常用扫描目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && !AddFavorite(dialog.FolderName)) { FavoritesHint.Text = "最多收藏12个不同的本地目录；已收藏或网络目录不会重复添加。"; FavoritesHint.Visibility = Visibility.Visible; }
    }
}
