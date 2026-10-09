using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public enum StorageMedium { Ssd, Hdd, Unknown }
public interface IStorageDeviceProbe { StorageMedium Detect(string root); }
public sealed class StorageDeviceProbe : IStorageDeviceProbe
{
    public StorageMedium Detect(string root)
    {
        try
        {
            var volume = Path.GetPathRoot(root)!;
            if (new DriveInfo(volume).DriveType != DriveType.Fixed || volume.Length != 3) return StorageMedium.Unknown;
            using var handle = NativeVolume.Open(volume, 0);
            if (handle.IsInvalid) return StorageMedium.Unknown;
            var input = new byte[12]; BitConverter.GetBytes(7).CopyTo(input, 0); // StorageDeviceSeekPenaltyProperty
            var output = new byte[32];
            if (!NativeVolume.DeviceIoControl(handle, 0x002D1400, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero) || bytes < 9)
                return StorageMedium.Unknown;
            return output[8] != 0 ? StorageMedium.Hdd : StorageMedium.Ssd;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return StorageMedium.Unknown; }
    }
}
public static class StorageDevicePolicy
{
    public static (int Workers, string Description) Choose(IReadOnlyList<string> roots, ScanEngineOptions options, IStorageDeviceProbe probe)
    {
        if (options.Scheduling == ScanScheduling.Sequential) return (1, "手动顺序扫描");
        var limit = Math.Clamp(Math.Min(options.MaximumParallelism, Environment.ProcessorCount), 1, 8);
        if (options.Scheduling == ScanScheduling.Parallel) return (limit, $"手动受控并行（最多 {limit} 路）");
        var devices = roots.Select(probe.Detect).ToArray();
        if (devices.All(type => type == StorageMedium.Ssd)) return (limit, $"SSD 受控并行（最多 {limit} 路）");
        return (1, devices.Any(type => type == StorageMedium.Hdd) ? "机械硬盘：自动顺序扫描" : "介质未知/混合：保守顺序扫描");
    }
}
internal static class NativeVolume
{
    internal static SafeFileHandle Open(string volume, uint access) => CreateFile(
        @"\\.\" + volume.TrimEnd('\\'), access, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int bytes, IntPtr overlapped);
}
