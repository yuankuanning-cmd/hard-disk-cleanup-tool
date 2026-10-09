using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ChatGPTCleanupTool.Models;
using Microsoft.Win32.SafeHandles;

namespace ChatGPTCleanupTool.Services;

internal interface IDuplicateLinkOperations
{
    void Create(string name, string existing);
    void Replace(string replacement, string target);
    void Delete(string path);
}
internal sealed class NativeDuplicateLinkOperations : IDuplicateLinkOperations
{
    public void Create(string name, string existing) { if (!CreateHardLink(name, existing, IntPtr.Zero)) { var error = Marshal.GetLastPInvokeError(); throw new Win32Exception(error, "创建硬链接失败：" + new Win32Exception(error).Message); } }
    public void Replace(string replacement, string target)
    {
        // POSIX rename semantics allow the verified old target handle to stay locked until the swap completes.
        using var handle = NativeVolume.CreateFile(replacement, 0x10000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var name = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var offset = IntPtr.Size == 8 ? 20 : 12; var data = Marshal.AllocHGlobal(offset + name.Length + 2);
        try
        {
            Marshal.Copy(new byte[offset + name.Length + 2], 0, data, offset + name.Length + 2);
            Marshal.WriteInt32(data, 3); // FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS
            Marshal.WriteInt32(data, offset - 4, name.Length); Marshal.Copy(name, 0, IntPtr.Add(data, offset), name.Length);
            if (!SetFileInformationByHandle(handle, 22, data, (uint)(offset + name.Length + 2)))
            { var error = Marshal.GetLastPInvokeError(); throw new Win32Exception(error, "安全交换文件名称失败（需要支持此操作的 Windows/NTFS）：" + new Win32Exception(error).Message); }
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public void Delete(string path) => File.Delete(path);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr data, uint size);
}

/// <summary>Full rehash of target AND retained copy in both scan modes, fail-closed originals, durable item audit.</summary>
internal sealed class DuplicateMutationService(ReferenceDirectoryService references, DuplicateOperationLogService log, IDuplicateLinkOperations? linkOperations = null)
{
    private readonly IDuplicateLinkOperations _links = linkOperations ?? new NativeDuplicateLinkOperations();
    public Task<DuplicateDeletionResult> ExecuteAsync(IReadOnlyList<DuplicateFileItem> all, IEnumerable<DuplicateFileItem> selected,
        DuplicateActionOptions options, IReadOnlyList<string>? exclusions = null, CancellationToken token = default)
    {
        var files = selected.DistinctBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var completion = new TaskCompletionSource<DuplicateDeletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Execute(all, files, options, exclusions ?? [], token)); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Duplicate full verification and mutation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private DuplicateDeletionResult Execute(IReadOnlyList<DuplicateFileItem> all, IReadOnlyList<DuplicateFileItem> files,
        DuplicateActionOptions options, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        var protectedRoots = references.GetPaths();
        var selectedPaths = files.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = all.GroupBy(f => f.GroupNumber).ToDictionary(g => g.Key, g => g.ToArray());
        var records = new List<DuplicateOperationRecord>(); var failures = new List<string>();
        var operation = Guid.NewGuid().ToString("N"); var count = 0; long theory = 0, actual = 0; var canceled = false;
        var before = files.Select(f => Path.GetPathRoot(f.FullPath)!).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(v => v, FreeBytes);
        using var audit = log.OpenSession(); // If the audit cannot be opened, no filesystem mutation is allowed.
        foreach (var file in files)
        {
            if (token.IsCancellationRequested) { canceled = true; break; }
            var record = new DuplicateOperationRecord(Guid.NewGuid().ToString("N"), operation, DateTimeOffset.UtcNow,
                options.DryRun ? "模拟 " + options.Action : options.Action == DuplicateAction.Recycle ? "移入回收站" : "NTFS 硬链接替换", file.GroupNumber, file.FullPath, "", "准备校验", 0, 0, "");
            var mutated = false;
            try
            {
                audit.Save(record);
                if (!groups.TryGetValue(file.GroupNumber, out var group) || !group.Any(f => f.FullPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase))) throw new IOException("项目不属于当前扫描结果");
                var currentRoots = references.GetPaths(); // Recheck persisted protection, not just the UI flag.
                var allProtected = protectedRoots.Concat(currentRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                bool Protected(DuplicateFileItem f) => f.IsReference || ReferenceDirectoryService.Contains(f.FullPath, allProtected);
                if (Protected(file) || file.IsExcluded || ReferenceDirectoryService.Contains(file.FullPath, exclusions)) throw new IOException("参考原件或永久排除项，禁止操作");
                ValidatePath(file.FullPath);
                using var target = OpenStable(file.FullPath, allowDelete: true);
                ValidateSnapshot(file, target);
                var targetHash = Hash(target, token);
                if (file.IsFullHashVerified && !targetHash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("目标内容与扫描时完整哈希不同");
                var targetStorage = WindowsFileStorageService.GetFileStorageInfo(target.SafeFileHandle);
                if (HasReferenceAlias(file.FullPath, allProtected)) throw new IOException("目标存在参考目录中的硬链接别名，禁止操作");
                // Protect outside aliases of reference files, even if an earlier/fast scan did not label them.
                foreach (var original in group.Where(Protected))
                {
                    if (!File.Exists(original.FullPath)) continue;
                    if (WindowsFileStorageService.GetFileIdentity(original.FullPath) == targetStorage.Identity) throw new IOException("目标是原件的硬链接别名，禁止修改");
                }
                var keepers = group.Where(k => !selectedPaths.Contains(k.FullPath))
                    .OrderByDescending(Protected).ThenByDescending(k => k.LastWriteTime).ThenBy(k => k.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
                if (keepers.Length == 0) throw new IOException("没有未选择的保留副本，每组至少保留一份");
                FileStream? keeperStream = null; DuplicateFileItem? keeper = null; string keeperError = "";
                foreach (var candidate in keepers)
                {
                    FileStream? stream = null;
                    try
                    {
                        if (options.Action == DuplicateAction.HardLink && (Protected(candidate) || candidate.IsExcluded || ReferenceDirectoryService.Contains(candidate.FullPath, exclusions))) continue;
                        if (options.Action == DuplicateAction.HardLink && HasReferenceAlias(candidate.FullPath, allProtected)) continue;
                        ValidatePath(candidate.FullPath);
                        stream = new FileStream(candidate.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | (options.Action == DuplicateAction.HardLink ? FileShare.Delete : 0), 1024 * 1024, FileOptions.SequentialScan);
                        ValidateSnapshot(candidate, stream);
                        var hash = Hash(stream, token);
                        if (!hash.Equals(targetHash, StringComparison.OrdinalIgnoreCase) || candidate.IsFullHashVerified && !hash.Equals(candidate.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("保留副本内容变化或采样误匹配");
                        if (options.Action == DuplicateAction.HardLink && !SameNtfsVolume(file.FullPath, candidate.FullPath)) throw new IOException("硬链接需要同一本地 NTFS 固定卷");
                        keeper = candidate; keeperStream = stream; break;
                    }
                    catch (Exception ex) when (IsExpected(ex)) { keeperError = ex.Message; stream?.Dispose(); }
                }
                if (keeper is null || keeperStream is null) throw new IOException("没有可验证的保留副本" + (options.Action == DuplicateAction.HardLink ? "（参考原件不能作为硬链接来源）" : "") + "；" + keeperError);
                using (keeperStream)
                {
                    var bytes = targetStorage.LinkCount == 1 ? targetStorage.AllocatedBytes : 0;
                    long releasedAllocation = 0;
                    record = record with { KeeperPath = keeper.FullPath, Status = "完整哈希已复核", TheoreticalBytes = bytes };
                    audit.Save(record); token.ThrowIfCancellationRequested();
                    ValidatePath(file.FullPath); ValidatePath(keeper.FullPath);
                    ValidateSnapshot(file, target);
                    ValidateSnapshot(keeper, keeperStream);
                    var finalRoots = allProtected.Concat(references.GetPaths()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (HasReferenceAlias(file.FullPath, finalRoots) || options.Action == DuplicateAction.HardLink && HasReferenceAlias(keeper.FullPath, finalRoots)) throw new IOException("操作前复核发现原件保护，已停止");
                    if (options.Action == DuplicateAction.HardLink)
                    {
                        if (!options.HardLinkConsent) throw new IOException("未确认共享修改和权限风险，禁止硬链接替换");
                        if (HasNamedStreams(file.FullPath) || HasNamedStreams(keeper.FullPath)) throw new IOException("文件含额外数据流，硬链接替换可能丢失这些信息，已拒绝");
                        if (((File.GetAttributes(file.FullPath) | File.GetAttributes(keeper.FullPath)) & (FileAttributes.Encrypted | FileAttributes.Offline)) != 0) throw new IOException("加密或云端占位文件不能执行此替换");
                        if (!options.DryRun)
                        {
                            var parent = Path.GetDirectoryName(file.FullPath)!;
                            record = record with { BackupPath = Path.Combine(parent, ".diskcleanup-backup-" + record.Id + ".tmp"), TemporaryLinkPath = Path.Combine(parent, ".diskcleanup-link-" + record.Id + ".tmp"), Status = "替换准备（恢复路径已记录）" };
                            audit.Save(record);
                            var keeperId = WindowsFileStorageService.GetFileIdentity(keeper.FullPath);
                            var swapped = false;
                            try
                            {
                                _links.Create(record.BackupPath, file.FullPath);
                                _links.Create(record.TemporaryLinkPath, keeper.FullPath);
                                if (WindowsFileStorageService.GetFileIdentity(record.BackupPath) != targetStorage.Identity || WindowsFileStorageService.GetFileIdentity(record.TemporaryLinkPath) != keeperId || WindowsFileStorageService.GetFileIdentity(file.FullPath) != targetStorage.Identity) throw new IOException("替换前文件身份已变化");
                                // Atomic name swap without ReplaceFile's ACL/ADS merging into the retained file.
                                _links.Replace(record.TemporaryLinkPath, file.FullPath); swapped = true;
                                if (WindowsFileStorageService.GetFileIdentity(file.FullPath) != keeperId) throw new IOException("替换后身份不符");
                                record = record with { Status = "链接已替换，清理恢复副本前" }; audit.Save(record);
                                _links.Delete(record.BackupPath);
                                if (File.Exists(record.BackupPath)) throw new IOException("恢复副本未清理，不能计入已释放空间");
                                var orphan = WindowsFileStorageService.GetFileStorageInfo(target.SafeFileHandle);
                                // An outside process may have created another name since initial verification.
                                if (orphan.LinkCount == 0) releasedAllocation = orphan.AllocatedBytes;
                                mutated = true;
                            }
                            catch
                            {
                                if (swapped && File.Exists(record.BackupPath))
                                {
                                    try { _links.Replace(record.BackupPath, file.FullPath); }
                                    catch (Exception rollback) { throw new IOException("替换回滚失败；保留恢复文件 " + record.BackupPath + "；" + rollback.Message); }
                                }
                                if (File.Exists(record.TemporaryLinkPath)) _links.Delete(record.TemporaryLinkPath);
                                // On any uncertainty preserve the backup, never destroy the only recoverable copy.
                                throw;
                            }
                        }
                    }
                    else if (!options.DryRun) { SpaceRecycleService.RecycleWithShell(file.FullPath); mutated = true; }
                    target.Dispose(); // Release the last open handle before measuring actual freed allocation.
                    count++; theory += bytes;
                    var freed = !options.DryRun && options.Action == DuplicateAction.HardLink ? releasedAllocation : 0;
                    actual += freed;
                    record = record with { Status = options.DryRun ? "模拟通过（文件未修改）" : "成功", ActualBytes = freed,
                        Reason = options.Action == DuplicateAction.Recycle ? "原副本在回收站，内容空间尚未释放；不会自动清空回收站。" : "共享内容、修改时间、权限；不再是独立备份。" };
                    try { audit.Save(record); }
                    catch (Exception ex) { record = record with { Status = "操作完成但日志更新失败", Reason = record.Reason + " " + ex.Message }; failures.Add(record.FilePath + "：" + record.Reason); }
                }
            }
            catch (OperationCanceledException) { canceled = true; record = record with { Status = "取消", Reason = "未执行本项修改" }; TrySave(audit, record); records.Add(record); break; }
            catch (Exception ex) when (IsExpected(ex))
            {
                record = record with { Status = mutated ? "需重新核对" : File.Exists(record.BackupPath) ? "失败 · 保留恢复副本" : "跳过/失败", Reason = ex.Message };
                failures.Add(file.FullPath + "：" + ex.Message); TrySave(audit, record);
            }
            records.Add(record);
        }
        return new(count, actual, failures) { TheoreticalBytes = theory, Records = records, WasCanceled = canceled,
            VolumeObservations = before.Select(p => new VolumeSpaceObservation(p.Key, p.Value, FreeBytes(p.Key))).ToArray() };
    }
    private static void TrySave(DuplicateOperationLogService.Session session, DuplicateOperationRecord record) { try { session.Save(record); } catch { /* Original before-operation record remains, in-memory failure is shown. */ } }
    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException;
    private static long? FreeBytes(string volume) { try { return new DriveInfo(volume).AvailableFreeSpace; } catch { return null; } }
    internal static bool SameNtfsVolume(string a, string b)
    {
        var root = Path.GetPathRoot(a)!;
        return root.Equals(Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase) && new DriveInfo(root) is { DriveFormat: "NTFS", DriveType: DriveType.Fixed };
    }
    internal static void ValidatePath(string path)
    {
        var normalized = DiskAnalysisService.NormalizePath(path);
        if (!File.Exists(normalized) || File.GetAttributes(normalized).HasFlag(FileAttributes.ReparsePoint) || DiskAnalysisService.HasReparsePointAncestor(Path.GetDirectoryName(normalized)!)) throw new IOException("文件不存在或路径包含链接/重解析点");
        if (new DriveInfo(Path.GetPathRoot(normalized)!).DriveType != DriveType.Fixed) throw new IOException("仅支持本地固定磁盘，避免无回收站时永久删除");
        var protectedRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Where(p => !string.IsNullOrEmpty(p));
        if (protectedRoots.Any(r => DiskAnalysisService.IsWithin(normalized, r)) || normalized.Split('\\').Any(p => p.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || p.StartsWith(".diskcleanup-", StringComparison.OrdinalIgnoreCase))) throw new IOException("系统、回收站或恢复副本路径受保护");
    }
    private static FileStream OpenStable(string path, bool allowDelete) => new(path, FileMode.Open, FileAccess.Read, allowDelete ? FileShare.Delete : FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    private static void ValidateSnapshot(DuplicateFileItem f, FileStream stream)
    {
        var info = new FileInfo(f.FullPath);
        if (stream.Length != f.LogicalBytes || info.LastWriteTimeUtc != f.LastWriteTime.ToUniversalTime() || WindowsFileStorageService.GetFileIdentity(f.FullPath) != f.FileIdentity || WindowsFileStorageService.GetFileStorageInfo(stream.SafeFileHandle).Identity != f.FileIdentity) throw new IOException("扫描后的大小、时间或文件身份变化，请重新扫描");
    }
    internal static string Hash(Stream stream, CancellationToken token)
    {
        stream.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int count;
        while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static bool HasNamedStreams(string path)
    {
        using var find = FindFirstStream(path, 0, out var data, 0);
        if (find.IsInvalid) { var error = Marshal.GetLastPInvokeError(); if (error == 38) return false; throw new Win32Exception(error); }
        do { if (!data.Name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase)) return true; } while (FindNextStream(find, out data));
        var final = Marshal.GetLastPInvokeError(); if (final != 38) throw new Win32Exception(final); return false;
    }
    internal static bool HasReferenceAlias(string path, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return false;
        if (ReferenceDirectoryService.Contains(path, roots)) return true;
        var volume = Path.GetPathRoot(path)!;
        roots = roots.Where(r => volume.Equals(Path.GetPathRoot(r), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (roots.Count == 0 || !new DriveInfo(volume).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return false;
        uint length = 1024; var buffer = new StringBuilder((int)length);
        var find = FindFirstFileName(path, 0, ref length, buffer);
        if (find.IsInvalid && Marshal.GetLastPInvokeError() == 234)
        { find.Dispose(); buffer = new StringBuilder(checked((int)length)); find = FindFirstFileName(path, 0, ref length, buffer); }
        using (find)
        {
            if (find.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError()); // Fail closed when aliases cannot be checked.
            do
            {
                if (ReferenceDirectoryService.Contains(Path.Combine(volume, buffer.ToString().TrimStart('\\')), roots)) return true;
                length = (uint)buffer.Capacity;
                if (FindNextFileName(find, ref length, buffer)) continue;
                var error = Marshal.GetLastPInvokeError();
                if (error == 38) return false;
                if (error != 234) throw new Win32Exception(error);
                buffer = new StringBuilder(checked((int)length));
                if (!FindNextFileName(find, ref length, buffer)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            } while (true);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    private sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
    { private FindHandle() : base(true) { } protected override bool ReleaseHandle() => FindClose(handle); }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern FindHandle FindFirstStream(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStream(FindHandle handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern FindHandle FindFirstFileName(string file, uint flags, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll", EntryPoint = "FindNextFileNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextFileName(FindHandle find, ref uint length, StringBuilder name);
}
