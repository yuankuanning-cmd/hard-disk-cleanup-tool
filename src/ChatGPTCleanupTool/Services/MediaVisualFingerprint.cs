using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

internal static class MediaVisualFingerprint
{
    internal const int HashesPerSample = 24;
    private static readonly double[,] Cosines = MakeCosines();
    internal sealed record Visual(ulong[] Hashes, double[] Colors, double Texture);

    internal static MediaFingerprint ReadImage(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DuplicatePreviewService.ValidateReadPath(path);
        var file = new FileInfo(path);
        if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("图片超过 256 MB 安全解码上限");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
            throw new InvalidDataException("图片尺寸超过 1 亿像素安全上限");
        var metadata = ReadMetadata(frame.Metadata as BitmapMetadata);
        // DecodePixel* lets WIC's native codecs reduce decode allocation when supported.
        var bitmap = new BitmapImage();
        stream.Position = 0; bitmap.BeginInit(); bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(256, width);
        else bitmap.DecodePixelHeight = Math.Min(256, height);
        bitmap.EndInit(); bitmap.Freeze();
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        token.ThrowIfCancellationRequested();
        var visual = Create(pixels, bgra.PixelWidth, bgra.PixelHeight);
        return new MediaFingerprint { Path = path, Kind = MediaKind.Image, Length = file.Length,
            WrittenTicks = file.LastWriteTimeUtc.Ticks, Width = width, Height = height,
            TakenAt = metadata.Taken, Privacy = metadata.Privacy, MetadataDigest = metadata.Digest,
            MetadataSummary = metadata.Summary, VisualHashes = visual.Hashes, Colors = visual.Colors,
            Texture = [visual.Texture] };
    }

    internal static Visual Create(byte[] bgra, int width, int height, bool trimVideoBorders = false)
    {
        if (trimVideoBorders) (bgra, width, height) = TrimDarkBorders(bgra, width, height);
        // Composite transparency on white, so invisible RGB bytes do not influence visible similarity.
        for (int p = 0; p < bgra.Length; p += 4)
        {
            int alpha = bgra[p + 3];
            if (alpha == 255) continue;
            for (int c = 0; c < 3; c++) bgra[p + c] = (byte)((bgra[p + c] * alpha + 255 * (255 - alpha)) / 255);
            bgra[p + 3] = 255;
        }
        var hashes = new ulong[24]; var colors = new double[12];
        double sum = 0, sum2 = 0;
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int p = (Math.Min(height - 1, (int)((y + .5) * height / 32)) * width +
                Math.Min(width - 1, (int)((x + .5) * width / 32))) * 4;
            double gray = bgra[p] * .114 + bgra[p + 1] * .587 + bgra[p + 2] * .299;
            sum += gray; sum2 += gray * gray;
            for (int c = 0; c < 3; c++) colors[c * 4 + bgra[p + c] / 64] += 1.0 / 1024;
        }
        for (int crop = 0; crop < 3; crop++)
        {
            double fraction = crop == 0 ? 1 : crop == 1 ? .90 : .75;
            var grid = new double[1024];
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            {
                // Average a small area to dampen JPEG noise rather than sampling a single pixel.
                double sx = ((1 - fraction) / 2 + (x + .5) / 32 * fraction) * width;
                double sy = ((1 - fraction) / 2 + (y + .5) / 32 * fraction) * height;
                double v = 0;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int px = Math.Clamp((int)sx + dx, 0, width - 1), py = Math.Clamp((int)sy + dy, 0, height - 1);
                    int p = (py * width + px) * 4;
                    v += bgra[p] * .114 + bgra[p + 1] * .587 + bgra[p + 2] * .299;
                }
                grid[y * 32 + x] = v / 9;
            }
            for (int transform = 0; transform < 8; transform++)
                hashes[crop * 8 + transform] = Hash(Transform(grid, transform));
        }
        return new(hashes, colors, Math.Sqrt(Math.Max(0, sum2 / 1024 - Math.Pow(sum / 1024, 2))));
    }

    // Decode/encoder padding and letterboxing should not dominate the video fingerprint.
    // Only remove nearly uniformly black edge lines, and never more than 15% on any edge.
    private static (byte[] Pixels, int Width, int Height) TrimDarkBorders(byte[] source, int width, int height)
    {
        int top = 0, bottom = height - 1, left = 0, right = width - 1;
        bool DarkRow(int y) { for (int x = 0; x < width; x++) { int p = (y * width + x) * 4; if (source[p] > 20 || source[p + 1] > 20 || source[p + 2] > 20) return false; } return true; }
        bool DarkColumn(int x) { for (int y = top; y <= bottom; y++) { int p = (y * width + x) * 4; if (source[p] > 20 || source[p + 1] > 20 || source[p + 2] > 20) return false; } return true; }
        while (top < height * .15 && DarkRow(top)) top++;
        while (height - 1 - bottom < height * .15 && DarkRow(bottom)) bottom--;
        while (left < width * .15 && DarkColumn(left)) left++;
        while (width - 1 - right < width * .15 && DarkColumn(right)) right--;
        int w = right - left + 1, h = bottom - top + 1;
        if (w == width && h == height) return (source, width, height);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Array.Copy(source, ((y + top) * width + left) * 4, pixels, y * w * 4, w * 4);
        return (pixels, w, h);
    }

    private static double[] Transform(double[] source, int transform)
    {
        var target = new double[1024];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int sx = transform >= 4 ? 31 - x : x, sy = y;
            for (int rotation = 0; rotation < transform % 4; rotation++) (sx, sy) = (sy, 31 - sx);
            target[y * 32 + x] = source[sy * 32 + sx];
        }
        return target;
    }
    private static ulong Hash(double[] grid)
    {
        var intermediate = new double[32 * 8]; var values = new double[64];
        for (int y = 0; y < 32; y++) for (int u = 0; u < 8; u++)
            for (int x = 0; x < 32; x++) intermediate[y * 8 + u] += grid[y * 32 + x] * Cosines[u, x];
        for (int v = 0; v < 8; v++) for (int u = 0; u < 8; u++)
            for (int y = 0; y < 32; y++) values[v * 8 + u] += intermediate[y * 8 + u] * Cosines[v, y];
        double median = values.Skip(1).Order().ElementAt(31);
        ulong hash = 0;
        for (int i = 1; i < 64; i++) if (values[i] > median) hash |= 1UL << i;
        return hash;
    }
    private static double[,] MakeCosines()
    {
        var result = new double[8, 32];
        for (int u = 0; u < 8; u++) for (int x = 0; x < 32; x++) result[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64);
        return result;
    }

    internal static double Compare(MediaFingerprint a, int sampleA, MediaFingerprint b, int sampleB, int distance)
    {
        if (a.Texture[sampleA] < 5 || b.Texture[sampleB] < 5) return 0; // Blank/single-color frames cannot prove similarity.
        double aspectA = (double)a.Width / a.Height, aspectB = (double)b.Width / b.Height;
        if (Math.Min(Math.Abs(Math.Log(aspectA / aspectB)), Math.Abs(Math.Log(aspectA * aspectB))) > .18) return 0;
        double colorDistance = 0;
        for (int i = 0; i < 12; i++) colorDistance += Math.Abs(a.Colors[sampleA * 12 + i] - b.Colors[sampleB * 12 + i]);
        if (colorDistance > 1.5) return 0;
        double best = 0;
        for (int crop = 0; crop < 3; crop++)
        {
            int threshold = crop == 0 ? distance : Math.Max(2, distance - 2);
            ulong ah = a.VisualHashes[sampleA * 24 + crop * 8];
            for (int t = 0; t < 8; t++)
            {
                int d = BitOperations.PopCount(ah ^ b.VisualHashes[sampleB * 24 + crop * 8 + t]);
                if (d <= threshold) best = Math.Max(best, 1 - d / 64.0);
            }
        }
        return best;
    }

    private static (DateTime? Taken, string Privacy, string Digest, string Summary) ReadMetadata(BitmapMetadata? metadata)
    {
        if (metadata is null) return (null, "", "", "无可读取 EXIF");
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal); var privacy = new List<string>();
        DateTime? taken = null;
        foreach (var prefix in new[] { "/app1/ifd", "/ifd" })
        {
            foreach (var (tag, name) in new[] { (271, "相机厂商"), (272, "相机型号"), (306, "修改日期"), (315, "作者"), (33432, "版权") })
                Read($"{prefix}/{{ushort={tag}}}", name);
            foreach (var (tag, name) in new[] { (36867, "拍摄日期"), (36868, "数字化日期"), (42033, "机身序列号"), (42037, "镜头序列号"), (37510, "用户备注"), (42016, "图像标识") })
                Read($"{prefix}/exif/{{ushort={tag}}}", name);
            try { if (metadata.ContainsQuery(prefix + "/gps") || metadata.ContainsQuery(prefix + "/gps/{ushort=2}")) privacy.Add("含 GPS 位置"); }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        }
        if (fields.TryGetValue("拍摄日期", out var date) && DateTime.TryParseExact(date.Trim('\0', ' '),
            "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)) taken = parsed;
        foreach (var label in new[] { "作者", "版权", "机身序列号", "镜头序列号", "用户备注" })
            if (fields.ContainsKey(label)) privacy.Add("含" + label);
        string canonical = string.Join("\n", fields.Select(pair => pair.Key + "=" + pair.Value));
        string digest = fields.Count >= 3 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) : "";
        return (taken, string.Join("；", privacy.Distinct()), digest,
            fields.Count == 0 ? "无已检查 EXIF 字段" : string.Join("、", fields.Keys));

        void Read(string query, string name)
        {
            try
            {
                object? value = metadata.GetQuery(query);
                string? text = value is byte[] bytes ? Encoding.UTF8.GetString(bytes).Trim('\0') : value?.ToString()?.Trim('\0', ' ');
                if (!string.IsNullOrWhiteSpace(text)) fields[name] = text.Length > 1024 ? text[..1024] : text;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        }
    }
}
