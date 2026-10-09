using System.IO;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public static class DuplicateDecisionService
{
    private static readonly string[] BackupNames = ["backup", "backups", "bak", "archive", "archives", "备份", "备份目录", "历史备份", "存档"];
    public static bool IsBackupPath(string path) => (Path.GetDirectoryName(path) ?? "").Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => BackupNames.Any(name => part.Equals(name, StringComparison.OrdinalIgnoreCase) || part.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) || part.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) || (name.Contains('备') && part.Contains(name, StringComparison.OrdinalIgnoreCase))));
    public static void Annotate(IEnumerable<DuplicateFileItem> files, IReadOnlyList<string> references, IReadOnlyList<string> exclusions)
    {
        foreach (var group in files.GroupBy(f => f.GroupNumber))
        {
            var members = group.ToArray(); var cross = members.Select(f => f.DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
            var overlap = members.Any(f => IsBackupPath(f.FullPath)) && members.Any(f => !IsBackupPath(f.FullPath));
            var protectedIdentities = members.Where(f => ReferenceDirectoryService.Contains(f.FullPath, references)).Select(f => f.FileIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasReference = protectedIdentities.Count > 0;
            foreach (var f in members)
            {
                f.IsReference = ReferenceDirectoryService.Contains(f.FullPath, references) || protectedIdentities.Contains(f.FileIdentity);
                f.IsExcluded = ReferenceDirectoryService.Contains(f.FullPath, exclusions);
                f.IsCrossDirectory = cross; f.HasBackupWorkOverlap = overlap; f.GroupHasReference = hasReference;
            }
        }
    }
    public static long PotentialBytes(IEnumerable<DuplicateFileItem> files)
    {
        long bytes = 0;
        foreach (var group in files.GroupBy(f => f.GroupNumber))
        {
            var members = group.ToArray(); var protectedFiles = members.Any(f => !f.IsSelectable);
            var candidates = members.Where(f => f.IsSelectable).OrderBy(f => f.TheoreticalReclaimableBytes).ToArray();
            bytes += candidates.Skip(protectedFiles ? 0 : 1).Sum(f => f.TheoreticalReclaimableBytes);
        }
        return bytes;
    }
    public static void Apply(IEnumerable<DuplicateFileItem> files, DuplicateKeepPolicy policy)
    {
        foreach (var group in files.GroupBy(f => f.GroupNumber))
        {
            var members = group.ToArray(); var keeper = members.Any(f => !f.IsSelectable) ? null : ChooseKeeper(members, policy);
            foreach (var f in members) f.IsSelected = f.IsSelectable && f != keeper;
        }
    }
    public static DuplicateFileItem ChooseKeeper(IEnumerable<DuplicateFileItem> files, DuplicateKeepPolicy policy) => policy switch
    {
        DuplicateKeepPolicy.Oldest => files.OrderBy(f => f.LastWriteTime).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First(),
        DuplicateKeepPolicy.ShortestPath => files.OrderBy(f => f.FullPath.Length).ThenByDescending(f => f.LastWriteTime).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First(),
        DuplicateKeepPolicy.HighestResolution => files.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).ThenByDescending(f => f.BitsPerPixel).ThenByDescending(f => f.LastWriteTime).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First(),
        DuplicateKeepPolicy.HighestQuality => files.OrderByDescending(f => f.IsLosslessImage).ThenByDescending(f => (long)f.PixelWidth * f.PixelHeight).ThenByDescending(f => f.BitsPerPixel).ThenByDescending(f => f.LastWriteTime).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First(),
        _ => files.OrderByDescending(f => f.LastWriteTime).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First()
    };
    public static void SelectDirectory(IEnumerable<DuplicateFileItem> files, string directory, bool selected)
    {
        var all = files.ToArray();
        foreach (var f in all.Where(f => DiskAnalysisService.IsWithin(f.FullPath, directory))) f.IsSelected = selected;
        foreach (var group in all.GroupBy(f => f.GroupNumber))
            if (group.All(f => f.IsSelected)) ChooseKeeper(group, DuplicateKeepPolicy.Newest).IsSelected = false;
    }
    public static Task ReadImageInformationAsync(IEnumerable<DuplicateFileItem> files, CancellationToken token = default) => Task.Run(() =>
    {
        foreach (var f in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!DuplicatePreviewService.IsImage(f.FullPath)) continue;
                DuplicatePreviewService.ValidateReadPath(f.FullPath);
                using var stream = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0]; f.PixelWidth = frame.PixelWidth; f.PixelHeight = frame.PixelHeight; f.BitsPerPixel = frame.Format.BitsPerPixel;
                f.IsLosslessImage = decoder is PngBitmapDecoder or BmpBitmapDecoder or GifBitmapDecoder; // TIFF may use lossy JPEG, do not assume.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException or ArgumentException) { }
        }
    }, token);
}
