using System.IO;
using System.Runtime.InteropServices;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed class SpaceRecycleService
{
    public Task RecycleAsync(DiskTreeNode node, IReadOnlyList<string> exclusions, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Validate(node, exclusions, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!dryRun) RecycleWithShell(node.FullPath);
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(cancellationToken); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Space recycle validation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static void Validate(DiskTreeNode node, IReadOnlyList<string> exclusions, CancellationToken token = default)
    {
        var path = DiskAnalysisService.NormalizePath(node.FullPath);
        RecoveryFileProtection.EnsureNotManaged(path);
        if (!node.CanRecycle)
            throw new InvalidOperationException("扫描根目录、未读完整的项目或排除项不能移入回收站。");
        if (node.Parent is null || !DiskAnalysisService.IsWithin(path, node.Parent.FullPath))
            throw new InvalidOperationException("该项目不在已扫描的父目录内。");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType != DriveType.Fixed)
            throw new InvalidOperationException("为避免永久删除，此功能仅支持本地固定磁盘的 Windows 回收站。");
        var protectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Where(p => !string.IsNullOrEmpty(p)).Select(DiskAnalysisService.NormalizePath);
        if (protectedPaths.Any(root => DiskAnalysisService.IsWithin(path, root) || DiskAnalysisService.IsWithin(root, path)))
            throw new InvalidOperationException("系统、程序安装或公共应用数据目录受保护，请使用相应卸载/清理工具。");
        var profile = DiskAnalysisService.NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (DiskAnalysisService.IsWithin(profile, path))
            throw new InvalidOperationException("不能清理整个用户目录或其上级目录。");
        var normalizedExclusions = exclusions.Select(DiskAnalysisService.NormalizePath).ToArray();
        if (normalizedExclusions.Any(root => DiskAnalysisService.IsWithin(path, root) || DiskAnalysisService.IsWithin(root, path)))
            throw new InvalidOperationException("该项目包含排除项，不能整体移入回收站。");
        for (var ancestor = node.IsDirectory ? path : Path.GetDirectoryName(path); ancestor is not null;
             ancestor = Path.GetDirectoryName(ancestor))
        {
            if (File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("路径中的链接或重解析点已变化，请重新扫描。");
        }
        if (node.Source is { } source)
        {
            long expectedCount = 0;
            foreach (var scanned in source.EnumerateSubtree(node.IndexId))
            {
                token.ThrowIfCancellationRequested(); expectedCount++;
                RecoveryFileProtection.EnsureNotManaged(scanned.FullPath);
                var attrs = File.GetAttributes(scanned.FullPath);
                var info = scanned.IsDirectory ? (FileSystemInfo)new DirectoryInfo(scanned.FullPath) : new FileInfo(scanned.FullPath);
                if (scanned.IsIncomplete || scanned.IsExcluded || attrs.HasFlag(FileAttributes.ReparsePoint) ||
                    attrs.HasFlag(FileAttributes.Directory) != scanned.IsDirectory || info.LastWriteTimeUtc != scanned.LastWriteTimeUtc ||
                    info is FileInfo file && file.Length != scanned.LogicalBytes)
                    throw new InvalidOperationException("扫描后内容或保护状态已变化，请重新扫描。");
                if (scanned.IsDirectory)
                {
                    long count = 0;
                    foreach (var entry in new DirectoryInfo(scanned.FullPath).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                    {
                        token.ThrowIfCancellationRequested(); count++;
                        if (source.Find(entry.FullName) is not { } row || row.ParentId != scanned.IndexId)
                            throw new InvalidOperationException("扫描后出现新内容，请重新扫描。");
                    }
                    if (count != scanned.Children.Count) throw new InvalidOperationException("扫描后有内容消失，请重新扫描。");
                }
                else if (scanned.FileIdentity.Length > 0 && WindowsFileStorageService.GetFileIdentity(scanned.FullPath) != scanned.FileIdentity)
                    throw new InvalidOperationException("文件身份已变化，请重新扫描。");
            }
            if (expectedCount == 0) throw new InvalidOperationException("索引项目已经失效，请重新扫描。");
            return;
        }
        var expected = new Dictionary<string, DiskTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in node.DescendantsAndSelf())
        {
            token.ThrowIfCancellationRequested();
            RecoveryFileProtection.EnsureNotManaged(item.FullPath);
            if (item.IsExcluded || item.IsIncomplete) throw new InvalidOperationException("子项受保护或未读取完整。");
            expected.Add(item.FullPath, item);
        }
        var pending = new Stack<string>();
        pending.Push(path);
        var actualCount = 0;
        while (pending.TryPop(out var actualPath))
        {
            token.ThrowIfCancellationRequested();
            if (!expected.TryGetValue(actualPath, out var scanned))
                throw new InvalidOperationException("扫描后出现了新内容，请重新扫描后再确认清理。");
            var attributes = File.GetAttributes(actualPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Directory) != scanned.IsDirectory)
                throw new InvalidOperationException("项目类型或链接状态已变化，请重新扫描。");
            var info = scanned.IsDirectory ? (FileSystemInfo)new DirectoryInfo(actualPath) : new FileInfo(actualPath);
            if (info.LastWriteTimeUtc != scanned.LastWriteTimeUtc ||
                info is FileInfo file && file.Length != scanned.LogicalBytes)
                throw new InvalidOperationException("扫描后项目已变化，请重新扫描后再确认清理。");
            actualCount++;
            if (scanned.IsDirectory)
                foreach (var entry in new DirectoryInfo(actualPath).EnumerateFileSystemInfos("*", new EnumerationOptions
                         { AttributesToSkip = 0, IgnoreInaccessible = false })) pending.Push(entry.FullName);
        }
        if (actualCount != expected.Count)
            throw new InvalidOperationException("扫描后有内容消失，请重新扫描。");
    }

    internal static void RecycleWithShell(string path)
    {
        var operation = (IFileOperation)new FileOperation();
        IShellItem? item = null;
        try
        {
            // RECYCLEONDELETE forces recycling; unavailable recycle storage is an error, not permanent deletion.
            // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x40 | 0x400 | 0x10 | 0x4));
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted || File.Exists(path) || Directory.Exists(path))
                throw new IOException("Windows 未完成回收站操作；项目仍保留，请检查回收站和文件占用状态。");
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            Marshal.FinalReleaseComObject(operation);
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private class FileOperation { }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(uint window);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
