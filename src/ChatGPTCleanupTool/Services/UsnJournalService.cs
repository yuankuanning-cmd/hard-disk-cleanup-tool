using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatGPTCleanupTool.Services;

public sealed record UsnCheckpoint(ulong JournalId, long NextUsn, long FirstUsn);
public sealed record UsnChange(ulong FileReference, ulong ParentReference, long Usn, uint Reason, FileAttributes Attributes, string Name);
public interface IUsnJournalProvider
{
    bool TryQuery(string volume, out UsnCheckpoint checkpoint, out string reason);
    IEnumerable<UsnChange> Read(string volume, UsnCheckpoint start, UsnCheckpoint end, CancellationToken token);
    string? ResolveDirectory(string volume, ulong reference);
}
public sealed class UsnJournalService : IUsnJournalProvider
{
    public bool TryQuery(string volume, out UsnCheckpoint checkpoint, out string reason)
    {
        checkpoint = new(0, 0, 0); reason = "";
        try
        {
            if (volume.Length != 3 || new DriveInfo(volume).DriveFormat != "NTFS") { reason = "不是本地 NTFS 磁盘"; return false; }
            using var handle = NativeVolume.Open(volume, 0x80000000);
            if (handle.IsInvalid) { reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message; return false; }
            var buffer = new byte[80];
            if (!NativeVolume.DeviceIoControl(handle, 0x000900F4, [], 0, buffer, buffer.Length, out var bytes, IntPtr.Zero) || bytes < 56)
            { reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message; return false; }
            checkpoint = new(BitConverter.ToUInt64(buffer, 0), BitConverter.ToInt64(buffer, 16), BitConverter.ToInt64(buffer, 8));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { reason = ex.Message; return false; }
    }
    public IEnumerable<UsnChange> Read(string volume, UsnCheckpoint start, UsnCheckpoint end, CancellationToken token)
    {
        if (start.JournalId != end.JournalId || start.NextUsn < end.FirstUsn || start.NextUsn > end.NextUsn)
            throw new IOException("USN 日志已轮转、重建或游标失效，必须执行目录核对。");
        using var handle = NativeVolume.Open(volume, 0x80000000);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var input = new byte[40]; var output = new byte[1024 * 1024];
        var cursor = start.NextUsn;
        while (cursor < end.NextUsn)
        {
            token.ThrowIfCancellationRequested();
            BitConverter.GetBytes(cursor).CopyTo(input, 0);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(input, 8);
            BitConverter.GetBytes(start.JournalId).CopyTo(input, 32);
            if (!NativeVolume.DeviceIoControl(handle, 0x000900BB, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (bytes < 8) throw new IOException("USN 返回数据不完整。");
            var next = BitConverter.ToInt64(output, 0);
            foreach (var change in ParseBuffer(output, bytes))
                if (change.Usn >= start.NextUsn && change.Usn < end.NextUsn) yield return change;
            if (next <= cursor) throw new IOException("USN 游标未前进。");
            cursor = next;
        }
    }
    public static IReadOnlyList<UsnChange> ParseBuffer(byte[] buffer, int bytes)
    {
        var result = new List<UsnChange>();
        for (var offset = 8; offset < bytes;)
        {
            if (bytes - offset < 60) throw new IOException("USN 记录头被截断。");
            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            var version = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4));
            if (length < 60 || length > bytes - offset || version != 2) throw new IOException("USN 记录格式不受支持，退回普通核对。");
            var nameLength = BitConverter.ToUInt16(buffer, offset + 56);
            var nameOffset = BitConverter.ToUInt16(buffer, offset + 58);
            if (nameOffset < 60 || (nameLength & 1) != 0 || nameOffset + nameLength > length) throw new IOException("USN 文件名范围无效。");
            result.Add(new(BitConverter.ToUInt64(buffer, offset + 8), BitConverter.ToUInt64(buffer, offset + 16),
                BitConverter.ToInt64(buffer, offset + 24), BitConverter.ToUInt32(buffer, offset + 40),
                (FileAttributes)BitConverter.ToUInt32(buffer, offset + 52), Encoding.Unicode.GetString(buffer, offset + nameOffset, nameLength)));
            offset += length;
        }
        return result;
    }
    public string? ResolveDirectory(string volume, ulong reference)
    {
        using var volumeHandle = NativeVolume.Open(volume, 0x80000000);
        if (volumeHandle.IsInvalid) return null;
        var descriptor = new FileIdDescriptor { Size = 24, Type = 0, FileId = reference };
        using var file = OpenFileById(volumeHandle, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
        if (file.IsInvalid) return null;
        var name = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(file, name, (uint)name.Capacity, 0);
        if (count == 0 || count >= name.Capacity) return null;
        var path = name.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\")) path = path[4..];
        try
        {
            if (!Directory.Exists(path) || DiskAnalysisService.HasReparsePointAncestor(path)) return null;
            return DiskAnalysisService.NormalizePath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    { [FieldOffset(0)] public uint Size; [FieldOffset(4)] public uint Type; [FieldOffset(8)] public ulong FileId; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id, uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
