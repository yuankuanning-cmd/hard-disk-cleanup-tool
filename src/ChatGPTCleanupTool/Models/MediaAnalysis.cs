using ChatGPTCleanupTool.Services;

namespace ChatGPTCleanupTool.Models;

public enum MediaKind { Image, Video, Audio }
public enum MediaOrigin { Other, Screenshot, Download, Codex }

public sealed record MediaFingerprint
{
    public required string Path { get; init; }
    public MediaKind Kind { get; init; }
    public MediaOrigin Origin { get; init; }
    public long Length { get; init; }
    public long WrittenTicks { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Duration { get; init; }
    public DateTime? TakenAt { get; init; }
    public string MetadataDigest { get; init; } = "";
    public string MetadataSummary { get; init; } = "";
    public string Privacy { get; init; } = "";
    // Each visual sample has full / 90% / 75% crops, each with eight rotation/mirror hashes.
    public ulong[] VisualHashes { get; init; } = [];
    public double[] Colors { get; init; } = [];
    public double[] Texture { get; init; } = [];
    public double[] SampleTimes { get; init; } = [];
    public uint[] AudioHashes { get; init; } = [];
    public double AudioEnergy { get; init; }
}

public sealed class MediaResultItem
{
    public required MediaFingerprint Fingerprint { get; init; }
    public int Group { get; internal set; }
    public double Similarity { get; internal set; }
    public bool IsReference { get; internal set; }
    public bool IsSuggested { get; internal set; }
    public int MetadataGroup { get; internal set; }
    public string Advice { get; internal set; } = "";
    public string FullPath => Fingerprint.Path;
    public string Name => System.IO.Path.GetFileName(FullPath);
    public string KindText => Fingerprint.Kind switch { MediaKind.Image => "图片", MediaKind.Video => "视频", _ => "音乐 / 音频" };
    public string OriginText => Fingerprint.Origin switch { MediaOrigin.Screenshot => "截图", MediaOrigin.Download => "下载图片", MediaOrigin.Codex => "Codex 生成", _ => "其他" };
    public string GroupText => Group > 0 ? $"候选 {Group}" : "未匹配";
    public string SizeText => CleanupService.FormatBytes(Fingerprint.Length);
    public string Dimensions => Fingerprint.Width > 0 ? $"{Fingerprint.Width} × {Fingerprint.Height}" : "—";
    public string DurationText => Fingerprint.Duration > 0 ? TimeSpan.FromSeconds(Fingerprint.Duration).ToString(@"hh\:mm\:ss") : "—";
    public string TakenText => Fingerprint.TakenAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "无拍摄日期";
    public string SimilarityText => Group > 0 ? $"{Similarity:P0}" : "—";
    public string PrivacyText => string.IsNullOrEmpty(Fingerprint.Privacy) ? "未发现已检查字段" : Fingerprint.Privacy;
    public string MetadataText => MetadataGroup > 0 ? $"EXIF 同字段组 {MetadataGroup}（不等于内容重复）" : Fingerprint.MetadataSummary;
}

public sealed record MediaAnalysisOptions(string Root, bool Images = true, bool Videos = true, bool Audio = true,
    int ImageDistance = 8, IReadOnlyList<string>? ScopeRoots = null, IReadOnlyList<string>? Exclusions = null,
    IReadOnlyList<string>? ReferenceRoots = null, int MaximumFiles = 50_000);
public sealed record MediaAnalysisResult(IReadOnlyList<MediaResultItem> Items, IReadOnlyList<string> Warnings,
    int CacheHits, int Decoded, int GroupCount, bool Canceled, bool Limited);
