using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Win32;

namespace ChatGPTCleanupTool.Controls;

public partial class MediaCenter : UserControl
{
    private MediaAnalysisResult? _result; private string _search = ""; private int _previewVersion;
    private int? _contextGroup;
    private CancellationTokenSource? _previewCancellation;
    public event EventHandler? ScanRequested;
    public event EventHandler? ReferencesRequested;
    public MediaCenter() { InitializeComponent(); Unloaded += (_, _) => { _previewCancellation?.Cancel(); }; }
    public string RootPath { get => RootBox.Text; set => RootBox.Text = value; }
    public bool ImagesEnabled { get => ImageBox.IsChecked == true; set => ImageBox.IsChecked = value; }
    public bool VideosEnabled { get => VideoBox.IsChecked == true; set => VideoBox.IsChecked = value; }
    public bool AudioEnabled { get => AudioBox.IsChecked == true; set => AudioBox.IsChecked = value; }
    public int Sensitivity { get => Math.Clamp(SensitivityBox.SelectedIndex, 0, 2); set => SensitivityBox.SelectedIndex = Math.Clamp(value, 0, 2); }
    public int ImageDistance => Sensitivity switch { 0 => 4, 2 => 12, _ => 8 };
    public MediaAnalysisResult? Result => _result;
    public DataGrid ResultGrid => Results;
    public void SetSearchText(string text) { _search = text.Trim(); RefreshFilter(); }
    public void SetResult(MediaAnalysisResult result)
    {
        _previewCancellation?.Cancel(); _previewVersion++; _result = result; _contextGroup = null;
        Results.ItemsSource = result.Items; RefreshFilter();
        int screenshots = result.Items.Count(row => row.Fingerprint.Origin == MediaOrigin.Screenshot);
        int downloads = result.Items.Count(row => row.Fingerprint.Origin == MediaOrigin.Download);
        int codex = result.Items.Count(row => row.Fingerprint.Origin == MediaOrigin.Codex);
        Summary.Text = $"{(result.Canceled ? "已取消，保留已完成结果" : result.Limited ? "部分结果（已达上限）" : "分析完成")}：{result.Items.Count:N0} 个媒体，{result.GroupCount:N0} 组候选；缓存复用 {result.CacheHits:N0}，新建指纹 {result.Decoded:N0}。截图 {screenshots:N0} / 下载 {downloads:N0} / Codex {codex:N0}。相似 ≠ 完全重复。";
        WarningsButton.Content = $"读取提示 ({result.Warnings.Count})";
        LeftImage.Source = RightImage.Source = null; LeftDetails.Text = "选中候选查看对照；取消后可再次扫描复用已完成缓存。";
        RightDetails.Text = "EXIF 检查只覆盖已支持字段，不保证图片不含其他隐私。"; LeftPath.Text = RightPath.Text = "";
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }
    public void UpdateReferenceProtection(IReadOnlyList<string> roots)
    {
        if (_result is null) return;
        foreach (var row in _result.Items) { row.IsReference = roots.Any(root => DiskAnalysisService.IsWithin(row.FullPath, root)); row.IsSuggested = false; }
        MediaAnalysisService.Suggest(_result.Items); Results.Items.Refresh();
    }
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (CandidatesBox is not null && ((sender == PrivacyBox && PrivacyBox.IsChecked == true) || (sender == ExifBox && ExifBox.IsChecked == true)))
            CandidatesBox.IsChecked = false;
        RefreshFilter();
    }
    private void RefreshFilter()
    {
        if (Results?.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(Results.ItemsSource);
        view.Filter = value => value is MediaResultItem row &&
            (_contextGroup is null || row.Group == _contextGroup) &&
            (CandidatesBox.IsChecked != true || row.Group > 0) &&
            (PrivacyBox.IsChecked != true || row.Fingerprint.Privacy.Length > 0) &&
            (ExifBox.IsChecked != true || row.MetadataGroup > 0) &&
            (KindBox.SelectedIndex == 0 || (int)row.Fingerprint.Kind == KindBox.SelectedIndex - 1) &&
            (OriginBox.SelectedIndex == 0 || (int)row.Fingerprint.Origin == (OriginBox.SelectedIndex == 4 ? 0 : OriginBox.SelectedIndex)) &&
            (_search.Length == 0 || row.FullPath.Contains(_search, StringComparison.OrdinalIgnoreCase) || row.Advice.Contains(_search, StringComparison.OrdinalIgnoreCase));
        view.Refresh();
    }
    internal void SetContextGroup(int? group)
    {
        _contextGroup = group;
        if (group is not null) { _search = ""; KindBox.SelectedIndex = 0; OriginBox.SelectedIndex = 0; PrivacyBox.IsChecked = false; ExifBox.IsChecked = false; CandidatesBox.IsChecked = true; }
        RefreshFilter();
        Summary.Text = group is { } number ? $"正在仅查看第{number}相似组；右键“恢复所有相似组”可返回。相似不等于完全重复。" : "已恢复所有相似组；其他来源/类型/搜索筛选仍然生效。";
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择相似媒体分析目录", InitialDirectory = Directory.Exists(RootPath) ? RootPath : "" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) RootPath = dialog.FolderName;
    }
    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke(this, EventArgs.Empty);
    private void References_Click(object sender, RoutedEventArgs e) => ReferencesRequested?.Invoke(this, EventArgs.Empty);
    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int version = ++_previewVersion;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = new();
        LeftImage.Source = RightImage.Source = null;
        if (Results.SelectedItem is not MediaResultItem selected || _result is null)
        { LeftDetails.Text = "选中候选查看图片对比。"; RightDetails.Text = "相似 ≠ 完全重复；请人工确认。"; LeftPath.Text = RightPath.Text = ""; return; }
        var comparison = selected.Group > 0 ? _result.Items.Where(row => row.Group == selected.Group && row != selected)
            .OrderByDescending(row => row.IsSuggested).ThenByDescending(row => row.Similarity).FirstOrDefault() : null;
        LeftDetails.Text = Describe(selected); LeftPath.Text = selected.FullPath; LeftPath.ToolTip = selected.FullPath;
        RightDetails.Text = comparison is null ? "此文件没有相似组对照。" : Describe(comparison);
        RightPath.Text = comparison?.FullPath ?? ""; RightPath.ToolTip = comparison?.FullPath;
        if (selected.Fingerprint.Kind != MediaKind.Image) return;
        try
        {
            var paths = comparison is null ? new[] { selected.FullPath } : new[] { selected.FullPath, comparison.FullPath };
            var previews = await DuplicatePreviewService.LoadGroupAsync(paths, _previewCancellation.Token).ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != _previewVersion) return;
                LeftImage.Source = previews[0].Thumbnail;
                if (previews[0].Thumbnail is null) LeftDetails.Text += "\n" + previews[0].Description;
                if (previews.Count > 1) { RightImage.Source = previews[1].Thumbnail; if (previews[1].Thumbnail is null) RightDetails.Text += "\n" + previews[1].Description; }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => { if (version == _previewVersion) LeftDetails.Text += "\n预览失败：" + ex.Message; }); }
    }
    private static string Describe(MediaResultItem row) => $"{row.Name}\n{row.Dimensions} · {row.SizeText}" +
        (row.Fingerprint.Kind == MediaKind.Image ? $"\n{row.TakenText}\n{row.PrivacyText}" : $"\n时长 {row.DurationText}\n{(row.Fingerprint.Kind == MediaKind.Video ? "五帧视觉指纹；不自动播放" : "声音频谱指纹；不保证录音/音质完全相同")}") + $"\n{row.Advice}";
    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not MediaResultItem row) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"/select,\"{row.FullPath.Replace("\"", "") }\"" }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { MessageBox.Show(Window.GetWindow(this), ex.Message, "无法打开位置"); }
    }
    private void CopyPath_Click(object sender, RoutedEventArgs e) { if (Results.SelectedItem is MediaResultItem row) Clipboard.SetText(row.FullPath); }
    private void Warnings_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _result?.Warnings.Count > 0 ? string.Join("\r\n\r\n", _result.Warnings) : "暂无读取错误。格式支持取决于 Windows 已安装解码器；GIF 只分析第一帧，音乐分析前 120 秒，视频采样五帧。\r\nEXIF 只检查常见 GPS、作者、版权、序列号和备注等字段，不等于全面隐私审计。\r\n来源分类按路径和文件名推断，不能证明文件来源。", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14) };
        new Window { Title = "媒体读取与支持范围", Content = box, Width = 760, Height = 470, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "只清空本工具建立的媒体指纹缓存，不改动任何图片、视频或音乐。下次将重新计算指纹。\n\n继续？", "清空指纹缓存", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        try { await Task.Run(() => { using var cache = new MediaFingerprintCache(); cache.Clear(); }); Summary.Text = "媒体指纹缓存已清空，媒体文件和当前分析结果均未改动。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { MessageBox.Show(Window.GetWindow(this), "缓存清理失败：" + ex.Message); }
        finally { IsEnabled = true; }
    }
}
