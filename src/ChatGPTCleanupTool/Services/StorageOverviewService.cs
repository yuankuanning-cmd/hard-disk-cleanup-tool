using System.IO;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed class StorageOverviewService
{
    // Query volume counters only. Do not traverse directories, run commands, or probe network drives.
    public Task<StorageOverview> ReadAsync(CancellationToken token = default) => Task.Run(() =>
    {
        var disks = new List<DriveUsage>(); var warnings = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var total = drive.TotalSize; var free = drive.TotalFreeSpace;
                if (total > 0) disks.Add(new(drive.RootDirectory.FullName, drive.VolumeLabel, drive.DriveFormat, total, Math.Clamp(free, 0, total)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { warnings.Add(drive.Name + "：容量暂不可读取"); }
        }
        return new StorageOverview(disks.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase).ToArray(), warnings, DateTime.UtcNow);
    }, token);
}
