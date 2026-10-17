using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.IO;

namespace ChatGPTCleanupTool.Services;

public static class WindowsFileStorageService
{
    public static long GetAllocatedBytes(string path) => GetFileStorageInfo(path).AllocatedBytes;

    public static string GetFileIdentity(string path)
    {
        using var handle = NativeVolume.CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return $"{info.VolumeSerialNumber:X8}:{(((ulong)info.FileIndexHigh << 32) | info.FileIndexLow):X16}";
    }

    public static FileStorageInfo GetFileStorageInfo(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        return GetFileStorageInfo(stream.SafeFileHandle);
    }

    // Size/identity do not require opening file contents. Avoid read-data access and antivirus content scans.
    internal static FileStorageInfo GetFileStorageMetadata(string path)
    {
        using var handle = NativeVolume.CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return GetFileStorageInfo(handle);
    }

    public static FileStorageInfo GetFileStorageInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if (!GetFileInformationByHandleEx(
                handle,
                FileInformationClass.FileStandardInfo,
                out var standardInfo,
                (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new FileStorageInfo(
            standardInfo.AllocationSize,
            $"{info.VolumeSerialNumber:X8}:{index:X16}",
            standardInfo.NumberOfLinks, index);
    }

    public static ulong GetDirectoryReference(string path)
    {
        using var handle = NativeVolume.CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return 0;
        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInformationClass informationClass,
        out FileStandardInformation information,
        uint bufferSize);

    private enum FileInformationClass
    {
        FileStandardInfo = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public sealed record FileStorageInfo(long AllocatedBytes, string Identity, uint LinkCount, ulong FileReference = 0);
