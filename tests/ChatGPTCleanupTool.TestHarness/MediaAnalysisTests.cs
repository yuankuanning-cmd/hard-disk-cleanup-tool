using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

internal static class MediaAnalysisTests
{
    private static MediaAnalysisResult? _uiResult;
    public static async Task RunAsync(string testRoot)
    {
        TestCandidateIndex();
        var root = Path.Combine(testRoot, "media"); Directory.CreateDirectory(root);
        var originals = Path.Combine(root, "originals"); var downloads = Path.Combine(root, "Downloads");
        var source = Image(Path.Combine(originals, "original.jpg"), 640, 480, 0, 95, true);
        var smaller = Image(Path.Combine(downloads, "smaller.jpg"), 320, 240, 0, 55);
        var compressed = Image(Path.Combine(downloads, "compressed.jpg"), 640, 480, 0, 25);
        var rotated = Image(Path.Combine(downloads, "rotated.png"), 480, 640, 1);
        var mirror = Image(Path.Combine(downloads, "mirror.png"), 640, 480, 4);
        var watermark = Image(Path.Combine(root, ".codex", "generated_images", "watermarked.png"), 640, 480, 0, watermark: true);
        var other = Image(Path.Combine(root, "Screenshots", "Screenshot-other.png"), 640, 480, 0, seed: 5);
        var exifCopy = Image(Path.Combine(downloads, "same-exif-different-image.jpg"), 640, 480, 0, 90, true, seed: 3);
        var broken = Path.Combine(root, "broken.png"); File.WriteAllText(broken, "not an image");
        var silent = Wav(Path.Combine(root, "silent.wav"), 0, false, silence: true);
        var music = Wav(Path.Combine(root, "recording.wav"), .7, false);
        var quieter = Wav(Path.Combine(root, "different-name-quiet.wav"), .18, false);
        var unrelated = Wav(Path.Combine(root, "other-song.wav"), .7, true);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        var before = files.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var service = new MediaAnalysisService(Path.Combine(testRoot, "media-cache.sqlite"));
        var watch = Stopwatch.StartNew();
        var options = new MediaAnalysisOptions(root, Videos: false, ReferenceRoots: [originals]);
        var result = await service.AnalyzeAsync(options);
        Console.WriteLine($"MEDIA cold: {watch.ElapsedMilliseconds} ms; {result.Items.Count} media; {result.GroupCount} groups; warnings: {string.Join(" | ", result.Warnings)}");
        MediaResultItem Row(string path) => result.Items.Single(row => row.FullPath == path);
        var original = Row(source); Check(original.Group > 0, "原图没有匹配相似组");
        foreach (var path in new[] { smaller, compressed, rotated, mirror, watermark })
        {
            Console.WriteLine($"MEDIA image: {Path.GetFileName(path)} group={Row(path).Group}, score={Row(path).Similarity:F3}");
            Check(Row(path).Group == original.Group, "缩放 / 压缩 / 旋转 / 镜像 / 边缘水印识别失败：" + path);
        }
        Check(Row(other).Group != original.Group && Row(exifCopy).Group != original.Group, "不同内容不应只因 EXIF 或文件类型加入相似组");
        Check(original.IsReference && original.IsSuggested && original.Advice.Contains("始终保留"), "参考目录媒体保护失败");
        Check(original.Fingerprint.TakenAt == new DateTime(2020, 1, 2, 3, 4, 5), "EXIF 拍摄日期读取失败");
        Check(original.PrivacyText.Contains("作者") && original.PrivacyText.Contains("GPS"), "EXIF 作者 / GPS 检查失败：" + original.PrivacyText);
        Check(original.MetadataGroup > 0 && Row(exifCopy).MetadataGroup == original.MetadataGroup, "EXIF 同字段分组失败");
        Check(Row(watermark).Fingerprint.Origin == MediaOrigin.Codex && Row(other).Fingerprint.Origin == MediaOrigin.Screenshot && Row(smaller).Fingerprint.Origin == MediaOrigin.Download, "来源分类失败");
        Console.WriteLine($"MEDIA audio: recording={Row(music).Group}, gain={Row(quieter).Group}, unrelated={Row(unrelated).Group}, silence={Row(silent).Group}");
        Check(Row(music).Group > 0 && Row(music).Group == Row(quieter).Group, "不同音量的同一录音未被声音指纹识别");
        Check(Row(unrelated).Group != Row(music).Group && Row(silent).Group == 0, "不同音乐 / 静音误匹配");
        Check(result.Warnings.Any(w => w.Contains("broken.png")), "损坏媒体没有逐项提示");
        watch.Restart(); var warm = await service.AnalyzeAsync(options);
        Console.WriteLine($"MEDIA warm: {watch.ElapsedMilliseconds} ms; hits={warm.CacheHits}; decoded={warm.Decoded}");
        Check(warm.CacheHits == result.Items.Count && warm.Decoded == 0, "媒体缓存未复用");
        Image(smaller, 320, 240, 0, 55, seed: 4);
        var modified = await service.AnalyzeAsync(options);
        Check(modified.Decoded == 1 && modified.CacheHits == result.Items.Count - 1, "媒体变化未使缓存失效");
        Image(smaller, 320, 240, 0, 55);
        var excluded = await service.AnalyzeAsync(new(root, Videos: false, ScopeRoots: [downloads], Exclusions: [downloads], ReferenceRoots: [originals]));
        Check(excluded.Items.Count == 1 && excluded.Items[0].FullPath == source && excluded.Items[0].IsReference, "白名单 / 排除 / 额外参考目录处理失败");
        var limit = await service.AnalyzeAsync(options with { MaximumFiles = 2 }); Check(limit.Limited && limit.Items.Count <= 2 && limit.Warnings.Any(w => w.Contains("上限")), "媒体上限未显式报告");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineMediaProgress(_ => cancellation.Cancel());
        var canceled = await service.AnalyzeAsync(options, cancellation.Token, progress); Check(canceled.Canceled, "媒体取消未返回部分结果状态");
        using var partialCancellation = new CancellationTokenSource();
        var partial = await service.AnalyzeAsync(options, partialCancellation.Token, new InlineMediaProgress(p => { if (p.ProcessedFiles >= 3) partialCancellation.Cancel(); }));
        Check(partial.Canceled && partial.Items.Count >= 3 && partial.Items.All(row => File.Exists(row.FullPath)), "取消未保留已完成媒体结果");
        using (var cache = new MediaFingerprintCache(Path.Combine(testRoot, "media-cache.sqlite"))) cache.Clear();
        var cleared = await service.AnalyzeAsync(options); Check(cleared.Decoded == result.Items.Count && cleared.CacheHits == 0, "指纹缓存清理失败");
        var suggestions = new[] {
            new MediaResultItem { Group = 1, Fingerprint = new() { Path = "large-new.jpg", Width = 640, Height = 480, Length = 100_000, TakenAt = new DateTime(2022,1,1) } },
            new MediaResultItem { Group = 1, Fingerprint = new() { Path = "original-old.jpg", Width = 640, Height = 480, Length = 90_000, TakenAt = new DateTime(2020,1,1) } },
            new MediaResultItem { Group = 1, Fingerprint = new() { Path = "small.jpg", Width = 320, Height = 240, Length = 40_000, TakenAt = new DateTime(2019,1,1) } } };
        MediaAnalysisService.Suggest(suggestions); Check(suggestions[1].IsSuggested && !suggestions[2].IsSuggested, "保留建议分辨率 / 拍摄日期决策失败");
        foreach (var file in files.Where(path => path != smaller)) Check(before[file] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), "只读分析修改了媒体文件");
        _uiResult = result;

        var ffmpeg = Environment.GetEnvironmentVariable("MEDIA_TEST_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg))
        {
            string tools = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "media-test-tools", "decoder");
            if (Directory.Exists(tools)) ffmpeg = Directory.GetFiles(tools, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        if (ffmpeg is not null && File.Exists(ffmpeg))
        {
            var video = Path.Combine(root, "video.mp4"); var videoCopy = Path.Combine(root, "resized-video.mp4"); var videoOther = Path.Combine(root, "other-video.mp4");
            await Encode(ffmpeg, ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=15:duration=10", "-an", "-c:v", "h264_mf", "-hw_encoding", "0", "-b:v", "500k", video]);
            await Encode(ffmpeg, ["-i", video, "-vf", "scale=160:120", "-an", "-c:v", "h264_mf", "-hw_encoding", "0", "-b:v", "160k", videoCopy]);
            await Encode(ffmpeg, ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=15:duration=10", "-vf", "format=yuv420p", "-an", "-c:v", "h264_mf", "-hw_encoding", "0", "-b:v", "500k", videoOther]);
            var mp3 = Path.Combine(root, "recording-encoded.mp3"); await Encode(ffmpeg, ["-i", music, "-c:a", "libmp3lame", "-b:a", "96k", mp3]);
            var native = await service.AnalyzeAsync(new(root, Images: false));
            Console.WriteLine("MEDIA native: " + string.Join(" | ", native.Items.Select(row => $"{row.Name}: group{row.Group} score{row.Similarity:F3}")) + " warnings: " + string.Join(" | ", native.Warnings));
            MediaResultItem NativeRow(string path) => native.Items.Single(row => row.FullPath == path);
            var va = NativeRow(video).Fingerprint; var vb = NativeRow(videoCopy).Fingerprint;
            Console.WriteLine($"VIDEO debug: dimensions={va.Width}x{va.Height}/{vb.Width}x{vb.Height}; times={string.Join(',',va.SampleTimes)}/{string.Join(',',vb.SampleTimes)}; unique={va.VisualHashes.Where((_,i)=>i%24==0).Distinct().Count()}/{vb.VisualHashes.Where((_,i)=>i%24==0).Distinct().Count()}; per-frame={string.Join(',',Enumerable.Range(0,5).Select(i=>MediaVisualFingerprint.Compare(va,i,vb,i,8)))}");
            Console.WriteLine("VIDEO distances: " + string.Join(" | ",Enumerable.Range(0,5).Select(i => string.Join(',',Enumerable.Range(0,3).Select(c => Enumerable.Range(0,8).Min(t=>System.Numerics.BitOperations.PopCount(va.VisualHashes[i*24+c*8]^vb.VisualHashes[i*24+c*8+t])))) + $" colors={Enumerable.Range(0,12).Sum(c=>Math.Abs(va.Colors[i*12+c]-vb.Colors[i*12+c])):F3}")));
            Check(NativeRow(video).Fingerprint.SampleTimes.Length == 5 && NativeRow(video).Group > 0 && NativeRow(video).Group == NativeRow(videoCopy).Group, "真正 MP4 多时间点 / 缩放编码识别失败");
            Check(NativeRow(videoOther).Group != NativeRow(video).Group, "不同视频误匹配");
            Check(NativeRow(mp3).Group > 0 && NativeRow(mp3).Group == NativeRow(music).Group, "MP3 / WAV 跨编码声音指纹识别失败");
            Console.WriteLine("PASS: 真正 MP4 解码、多帧指纹、重编码/缩放相似视频、不同视频排除、MP3/WAV 音频指纹。");
        }
        else Console.WriteLine("SKIP: 未配置测试用 FFmpeg，未运行跨编码 MP4/MP3 夹具；软件本身不使用 FFmpeg。");
        Console.WriteLine("PASS: 相似媒体图片变换、真实 PCM 音量变化、EXIF 日期/GPS/作者/同字段、分类、参考目录、白名单/排除、缓存变化、取消、上限、只读安全。");
    }

    public static void RunUi(string output)
    {
        Check(_uiResult is not null, "缺少媒体 UI 测试结果");
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var center = new MediaCenter { RootPath = Path.GetDirectoryName(Path.GetDirectoryName(_uiResult!.Items.First(row => row.IsReference).FullPath))! }; center.SetResult(_uiResult!);
            Check(center.ResultGrid.Items.Count > 0, "媒体候选组 UI 未显示");
            center.SetSearchText("no-such-media"); Check(center.ResultGrid.Items.Count == 0, "媒体搜索未生效"); center.SetSearchText("");
            var origins = (ComboBox)center.FindName("OriginBox"); origins.SelectedIndex = 3;
            Check(center.ResultGrid.Items.Cast<MediaResultItem>().All(row => row.Fingerprint.Origin == MediaOrigin.Codex), "Codex 独立分类筛选失败"); origins.SelectedIndex = 0;
            var candidates = (CheckBox)center.FindName("CandidatesBox"); candidates.IsChecked = false;
            var privacy = (CheckBox)center.FindName("PrivacyBox"); privacy.IsChecked = true;
            Check(center.ResultGrid.Items.Count >= 2 && center.ResultGrid.Items.Cast<MediaResultItem>().All(row => row.Fingerprint.Privacy.Length > 0), "隐私字段清单筛选失败"); privacy.IsChecked = false;
            candidates.IsChecked = true;
            center.ResultGrid.SelectedItem = _uiResult!.Items.First(row => row.IsReference);
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) }; int ticks = 0;
            timer.Tick += (_, _) => { if (((System.Windows.Controls.Image)center.FindName("LeftImage")).Source is not null || ++ticks > 200) { timer.Stop(); frame.Continue = false; } };
            timer.Start(); Dispatcher.PushFrame(frame);
            Check(((System.Windows.Controls.Image)center.FindName("LeftImage")).Source is not null && ((System.Windows.Controls.Image)center.FindName("RightImage")).Source is not null, "并排图片预览未完成");
            center.Measure(new Size(1240, 565)); center.Arrange(new Rect(0, 0, 1240, 565)); center.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1240, 565, 96, 96, PixelFormats.Pbgra32); bitmap.Render(center);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(output); using var stream = File.Create(Path.Combine(output, "similar-media-preview.png")); encoder.Save(stream);
            Console.WriteLine("PASS: 相似媒体 WPF 分类/搜索/隐私筛选、真实异步双图预览与界面渲染。");
        }
        finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
    }
    public static Task RunStandaloneUiAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { var app = new App(); app.InitializeComponent(); RunUi(Path.Combine(Directory.GetCurrentDirectory(), "outputs")); app.Shutdown(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private static string Image(string path, int width, int height, int transform, int quality = 90, bool metadata = false, bool watermark = false, int seed = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double u = (x + .5) / width, v = (y + .5) / height;
            if (transform >= 4) u = 1 - u;
            for (int r = 0; r < transform % 4; r++) (u, v) = (v, 1 - u);
            int p = (y * width + x) * 4;
            double wave = Math.Sin((u * 5 + v * 3 + seed) * Math.PI);
            bool circle = Math.Pow(u - (.3 + seed * .045), 2) + Math.Pow(v - .4, 2) < .035;
            bool rectangle = u > .53 && u < .85 && v > .14 + seed * .03 && v < .71;
            pixels[p] = (byte)Math.Clamp(circle ? 230 : rectangle ? 45 : 100 + wave * 40 + v * 80, 0, 255);
            pixels[p + 1] = (byte)Math.Clamp(circle ? 80 : rectangle ? 190 : 40 + u * 120 + seed * 8, 0, 255);
            pixels[p + 2] = (byte)Math.Clamp(circle ? 210 : rectangle ? 50 : 180 - v * 110 + seed * 10, 0, 255); pixels[p + 3] = 255;
            if (watermark && v > .94 && u > .2) { pixels[p] = pixels[p + 1] = pixels[p + 2] = 255; }
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze();
        BitmapMetadata? tags = null;
        if (metadata)
        {
            tags = new BitmapMetadata("jpg");
            tags.SetQuery("/app1/ifd/{ushort=271}", "Test camera"); tags.SetQuery("/app1/ifd/{ushort=272}", "Fixture 1");
            tags.SetQuery("/app1/ifd/{ushort=315}", "Test owner");
            tags.SetQuery("/app1/ifd/exif/{ushort=36867}", "2020:01:02 03:04:05");
            tags.SetQuery("/app1/ifd/gps/{ushort=0}", new byte[] { 2, 3, 0, 0 });
            tags.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
        }
        BitmapEncoder encoder = Path.GetExtension(path) == ".jpg" ? new JpegBitmapEncoder { QualityLevel = quality } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source, null, tags, null)); using var stream = File.Create(path); encoder.Save(stream); return path;
    }
    private static string Wav(string path, double gain, bool other, bool silence = false)
    {
        const int rate = 22050, seconds = 16; int samples = rate * seconds;
        using var writer = new BinaryWriter(File.Create(path)); writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)rate; int beat = (int)(t * 3); double frequency = (other ? 170 : 260) + ((beat * (other ? 7 : 3)) % 13) * (other ? 39 : 23);
            double sound = Math.Sin(2 * Math.PI * frequency * t) * .45 + Math.Sin(2 * Math.PI * frequency * 2.5 * t) * .22 + Math.Sin(2 * Math.PI * (other ? 1240 : 870) * t) * .12;
            writer.Write((short)(silence ? 0 : sound * gain * short.MaxValue));
        }
        return path;
    }
    private static async Task Encode(string ffmpeg, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
        Check(process.ExitCode == 0, "媒体测试夹具编码失败：" + await error); await output;
    }
    private static void TestCandidateIndex()
    {
        var random = new Random(7341);
        foreach (int width in new[] { 32, 64 })
        {
            var hashes = Enumerable.Range(0, 2000).Select(_ => (ulong)random.NextInt64() | ((ulong)random.Next(2) << 63)).Select(h => width == 32 ? h & uint.MaxValue : h).ToArray();
            var index = new MediaAnalysisService.HashIndex(width);
            for (int i = 0; i < hashes.Length; i++) index.Add(hashes[i], i);
            foreach (int radius in new[] { 0, 4, 6, 8, 12 })
            {
                ulong query = hashes[17] ^ (width == 32 ? 0x01010101UL : 0x0003000300030003UL);
                var found = new HashSet<int>(); index.Query(query, radius, found);
                var expected = hashes.Select((hash, i) => (hash, i)).Where(pair => System.Numerics.BitOperations.PopCount(pair.hash ^ query) <= radius).Select(pair => pair.i).ToHashSet();
                Check(found.SetEquals(expected), "分段指纹索引与穷举不一致，存在漏候选或误候选");
            }
        }
        var large = new MediaAnalysisService.HashIndex(); var values = new ulong[20_000];
        for (int i = 0; i < values.Length; i++) { values[i] = (ulong)random.NextInt64() | ((ulong)random.Next(2) << 63); large.Add(values[i], i); }
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) { var found = new HashSet<int>(); large.Query(values[i] ^ 0x0003000300030003UL, 8, found); Check(found.Contains(i), "20,000项指纹索引漏检8位差异"); }
        Console.WriteLine($"PASS: 32/64位分段指纹索引与穷举一致；20,000个指纹的200次查询 {watch.ElapsedMilliseconds} ms（8位差异分散到各段）。");
    }
    private sealed class InlineMediaProgress(Action<ScanProgressInfo> action) : IProgress<ScanProgressInfo> { public void Report(ScanProgressInfo value) => action(value); }
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
}
