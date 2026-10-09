using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace ChatGPTCleanupTool.Services;

public sealed record DuplicatePreview(string Path, string Description, BitmapSource? Thumbnail, string Text);
public static class DuplicatePreviewService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml", ".cs", ".py", ".js", ".ts", ".html", ".css", ".ini" };
    public static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path));
    internal static void ValidateReadPath(string path)
    {
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || DiskAnalysisService.HasReparsePointAncestor(System.IO.Path.GetDirectoryName(path)!)) throw new IOException("文件不存在或路径含重解析点");
    }
    public static Task<IReadOnlyList<DuplicatePreview>> LoadGroupAsync(IEnumerable<string> paths, CancellationToken token = default) => Task.Run<IReadOnlyList<DuplicatePreview>>(() =>
    {
        var results = new List<DuplicatePreview>();
        foreach (var path in paths.Take(8))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ValidateReadPath(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (IsImage(path))
                {
                    if (stream.Length > 200L * 1024 * 1024) { results.Add(new(path, "图片超过 200 MiB，跳过缩略图以控制内存", null, "")); continue; }
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000) { results.Add(new(path, "图片超过 1 亿像素，跳过缩略图以控制解码内存", null, "")); continue; }
                    var portrait = frame.PixelHeight > frame.PixelWidth; stream.Position = 0;
                    var image = new BitmapImage(); image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad;
                    if (portrait) image.DecodePixelHeight = 256; else image.DecodePixelWidth = 256;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; image.EndInit(); image.Freeze();
                    results.Add(new(path, "图片缩略预览（未运行外部程序）", image, ""));
                }
                else if (TextExtensions.Contains(System.IO.Path.GetExtension(path)))
                {
                    var data = new byte[4096]; var count = stream.Read(data); var text = Encoding.UTF8.GetString(data, 0, count);
                    results.Add(new(path, "文本前 4 KiB（纯文本，不执行内容）", null, text.Contains('\0') ? "二进制内容，不显示文本" : text));
                }
                else results.Add(new(path, "此类型仅显示文件信息；不自动打开文件", null, ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.FileFormatException)
            { results.Add(new(path, "无法预览：" + ex.Message, null, "")); }
        }
        return results;
    }, token);
}
