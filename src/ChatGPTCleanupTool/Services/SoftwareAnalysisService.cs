using System.IO;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed class SoftwareAnalysisService
{
    private readonly ISoftwareInventory _inventory;
    private readonly ISoftwareRestorePoint _restore;
    private readonly ISoftwareUninstaller _uninstaller;
    private readonly Dictionary<string, Guard> _guards = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Guard(SoftwareRegistration Entry, string? ExecutableIdentity, long Length, long Ticks, bool Allowed);
    private string CatalogPath { get; }
    public SoftwareAnalysisService(string? dataRoot = null, ISoftwareInventory? inventory = null, ISoftwareRestorePoint? restore = null, ISoftwareUninstaller? uninstaller = null)
    {
        var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanupAssistant");
        CatalogPath = Path.Combine(root, "software-inventory.sqlite"); _inventory = inventory ?? new WindowsSoftwareInventory(); _restore = restore ?? new SoftwareRestorePointService(); _uninstaller = uninstaller ?? new WindowsSoftwareUninstaller();
    }
    public Task<SoftwareResult> AnalyzeAsync(SoftwareOptions options, CancellationToken token = default, IProgress<string>? progress = null) => Task.Run(() =>
    {
        _guards.Clear(); progress?.Report("读取已安装软件、商店应用和平台清单…");
        var snapshot = _inventory.Read(options.Store, options.Games, token); var warnings = snapshot.Warnings.ToList();
        using var catalog = new SoftwareCatalog(CatalogPath); var history = catalog.Update(snapshot);
        var entries = snapshot.Entries.ToList();
        if (options.FindResidues)
        {
            progress?.Report("比对历史登记，查找疑似残留（只读）…");
            entries.AddRange(_inventory.FindDirectories(snapshot.Entries, history, options, token));
            warnings.Add("残留发现不是穷尽扫描：只检查常见目录/注册表顶层，历史登记连续两次消失才关联残留；名称关联不是删除依据。重装、更名、便携程序和共享数据可能误判。首次使用没有卸载历史，无法证明已有 AppData 属于已卸载软件。残留永远不进入卸载或删除队列。");
        }
        var items = new List<SoftwareItem>(); int budget = options.MaximumEntries; bool limited = false;
        var measured = new Dictionary<string, Measurement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.DistinctBy(e => e.Id))
        {
            token.ThrowIfCancellationRequested(); var item = new SoftwareItem { Registration = entry };
            SoftwareCommand? command = null;
            try { command = SoftwareWindowsCommands.Plan(entry); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { item.Evidence = "卸载命令不可确认：" + ex.Message; }
            item.CanUninstall = command is not null;
            item.Status = entry.Protected ? "系统/共享组件 · 禁止批量卸载" : entry.Kind switch
            { SoftwareKind.DirectoryResidue or SoftwareKind.RegistryResidue => "疑似卸载残留 · 只提示", SoftwareKind.Orphan => "未登记/便携/共享目录 · 只提示", SoftwareKind.Epic => "由游戏平台管理", _ => command is null ? "卸载程序缺失/指令不支持" : "已登记 · 需确认卸载" };
            item.Evidence += entry.Kind switch
            {
                SoftwareKind.Desktop => "登记来源：" + entry.RegistryPath + "；未使用 Win32_Product，避免触发 MSI 自动修复。没有卸载入口不证明文件无用。",
                SoftwareKind.Store => "仅当前用户包；框架、资源包、NonRemovable 阻止卸载，不删除 WindowsApps 或其他用户数据。",
                SoftwareKind.Steam => "Steam 库/ACF 清单，大小为平台报告；卸载交给 Steam 确认，交接后停止本批队列。",
                SoftwareKind.Epic => "Epic 清单检测，仅分析内容；不手工删除游戏平台或游戏文件。",
                _ => "只读提示，可能是个人配置、存档、共享组件或便携软件；不能根据软件名称相似/登记消失就认定可删除。"
            };
            if (options.Measure && entry.InstallPath.Length > 0 && Directory.Exists(entry.InstallPath) && WindowsSoftwareInventory.WithinScope(entry.InstallPath, options))
            {
                if (!measured.TryGetValue(entry.InstallPath, out var measurement))
                { progress?.Report("测量目录：" + entry.InstallPath); measurement = Measure(entry.InstallPath, options, ref budget, token); measured[entry.InstallPath] = measurement; }
                item.LogicalBytes = measurement.Logical; item.AllocatedBytes = measurement.Allocated; item.FileCount = measurement.Files;
                item.SizeBasis = measurement.Complete ? "本次测量（不含其他目录/共享数据）" : "部分测量 · " + measurement.Error; limited |= !measurement.Complete;
            }
            else if (options.Measure) item.SizeBasis = entry.InstallPath.Length == 0 ? "安装位置未知，未测量" : "白名单/保护/路径不存在，未测量";
            if (entry.InstallPath.Length > 0 && (options.Exclusions?.Any(p => DevelopmentCodexCatalog.Overlap(entry.InstallPath, p)) ?? false))
            { item.CanUninstall = false; item.Status = "参考/排除目录保护"; }
            string? identity = null; long length = 0, ticks = 0;
            if (command is { Platform: false } && entry.Kind == SoftwareKind.Desktop && entry.ProductCode.Length == 0)
            { try { var file = new FileInfo(command.Executable); identity = WindowsFileStorageService.GetFileIdentity(command.Executable); length = file.Length; ticks = file.LastWriteTimeUtc.Ticks; } catch { item.CanUninstall = false; item.Status = "无法确认卸载程序身份"; } }
            _guards[entry.Id] = new(entry, identity, length, ticks, item.CanUninstall); items.Add(item);
        }
        warnings.Add("软件列表默认来自系统登记，白名单只限制目录测量和目录发现，不隐藏位置未知的登记项；默认不测量大目录。相同/嵌套安装路径、商店共享框架和硬链接占用不能跨软件直接相加。系统还原点不是个人文件备份，也不保证恢复游戏/软件的全部内容。");
        return new SoftwareResult(items.OrderByDescending(i => i.SortBytes).ThenBy(i => i.Name).ToArray(), warnings, limited, DateTime.UtcNow);
    }, token);

    private sealed record Measurement(long Logical, long Allocated, long Files, bool Complete, string Error);
    private static Measurement Measure(string root, SoftwareOptions options, ref int budget, CancellationToken token)
    {
        long logical = 0, allocated = 0, count = 0; var identities = new HashSet<string>();
        var iterators = new Stack<IEnumerator<string>>();
        try
        {
            if (root == Path.GetPathRoot(root) || root.Length < 4 || DiskAnalysisService.HasReparsePointAncestor(root)) throw new IOException("根目录或链接目录不做软件归属测量");
            iterators.Push(Directory.EnumerateFileSystemEntries(root).GetEnumerator());
            while (iterators.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var current = iterators.Peek(); if (!current.MoveNext()) { current.Dispose(); iterators.Pop(); continue; }
                if (--budget < 0 || identities.Count > 250000) throw new IOException("测量达到条目/内存上限");
                var path = current.Current; if (!WindowsSoftwareInventory.WithinScope(path, options) || RecoveryFileProtection.IsManagedPath(path)) throw new IOException("包含排除/保护项");
                var attributes = File.GetAttributes(path); if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x400000)) != 0) throw new IOException("包含链接、离线或云占位文件");
                if (attributes.HasFlag(FileAttributes.Directory)) { iterators.Push(Directory.EnumerateFileSystemEntries(path).GetEnumerator()); continue; }
                var file = new FileInfo(path); logical += file.Length; count++; var storage = WindowsFileStorageService.GetFileStorageInfo(path); if (identities.Add(storage.Identity)) allocated += storage.AllocatedBytes;
            }
            return new(logical, allocated, count, true, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return new(logical, allocated, count, false, ex.Message); }
        finally { foreach (var iterator in iterators) iterator.Dispose(); }
    }

    public async Task<SoftwareBatchResult> UninstallAsync(IReadOnlyList<SoftwareItem> selected, bool dryRun, Func<IReadOnlyList<string>> currentExclusions,
        CancellationToken token = default, IProgress<string>? progress = null)
    {
        var records = new List<SoftwareOperation>(); using var catalog = new SoftwareCatalog(CatalogPath);
        void Record(SoftwareOperation record) { catalog.Record(record); records.Add(record); }
        var plan = new List<Guard>();
        foreach (var item in selected.DistinctBy(i => i.Id))
        {
            token.ThrowIfCancellationRequested();
            try { if (!_guards.TryGetValue(item.Id, out var guard) || !guard.Allowed || item.Registration != guard.Entry) throw new IOException("不属于可信卸载计划，或是残留/受保护项。"); Validate(guard, currentExclusions(), token); plan.Add(guard); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Record(new(DateTime.UtcNow, item.Name, item.Id, "卸载检查", "已跳过", ex.Message)); }
        }
        if (plan.Count == 0) return new(records, dryRun);
        SoftwareRestorePoint point = new(false, 0, "", "模拟不建立还原点");
        if (!dryRun)
        {
            progress?.Report("正在建立并确认本次卸载的新还原点…");
            try { point = await _restore.CreateAsync(token); }
            catch (Exception ex) { point = new(false, 0, "", ex.Message); }
            Record(new(DateTime.UtcNow, "本批卸载", "restore", "建立还原点", point.Success && point.Sequence > 0 ? "已确认" : "失败 · 队列停止", point.Description + " " + point.Error, point.Sequence));
            if (!point.Success || point.Sequence <= 0 || token.IsCancellationRequested) return new(records, false);
        }
        foreach (var guard in plan)
        {
            if (token.IsCancellationRequested) break;
            var entry = guard.Entry;
            try
            {
                progress?.Report("再次验证并卸载：" + entry.Name); Validate(guard, currentExclusions(), token);
                if (dryRun) { Record(new(DateTime.UtcNow, entry.Name, entry.Id, "模拟卸载", "模拟通过", "未启动卸载程序、未建立还原点、未删除残留")); continue; }
                Record(new(DateTime.UtcNow, entry.Name, entry.Id, "卸载", "执行前记录", "用户确认的厂商/系统卸载；不追加静默或重启参数", point.Sequence));
                var launch = await _uninstaller.RunAsync(entry, token);
                if (launch.Canceled) { Record(new(DateTime.UtcNow, entry.Name, entry.Id, "卸载", "停止等待 · 队列停止", "卸载程序可能仍在运行，不强杀；请完成其窗口后重新扫描", point.Sequence, launch.ProcessId)); break; }
                if (launch.HandedToPlatform) { Record(new(DateTime.UtcNow, entry.Name, entry.Id, "卸载", "已交给平台 · 未确认", "请在平台完成卸载；停止队列避免并发弹窗，完成后重扫", point.Sequence, launch.ProcessId)); break; }
                var remains = _inventory.Refresh(entry, token);
                var status = launch.ExitCode is 0 or 3010 && remains is null ? launch.ExitCode == 3010 ? "登记已移除 · 需重启" : "登记已移除" : "未确认 / 失败";
                Record(new(DateTime.UtcNow, entry.Name, entry.Id, "卸载", status, $"返回码 {launch.ExitCode}；{(remains is null ? "登记已消失，不证明个人数据已清理" : "登记仍在，可能取消、失败或后台仍在完成") }；未删除残留", point.Sequence, launch.ProcessId));
                if (status == "未确认 / 失败") break; // A bootstrapper may have left children running; avoid a parallel uninstall.
            }
            catch (Exception ex) { Record(new(DateTime.UtcNow, entry.Name, entry.Id, "卸载", "已跳过 / 失败", ex.Message, point.Sequence)); if (ex is OperationCanceledException) break; }
        }
        return new(records, dryRun);
    }
    private void Validate(Guard guard, IReadOnlyList<string> exclusions, CancellationToken token)
    {
        var fresh = _inventory.Refresh(guard.Entry, token);
        if (fresh != guard.Entry || fresh.Protected) throw new IOException("登记、版本、路径或卸载命令已变化/消失，请重新扫描。");
        var command = SoftwareWindowsCommands.Plan(fresh) ?? throw new IOException("卸载指令安全性不能确认。");
        if (fresh.InstallPath.Length > 0 && exclusions.Any(p => DevelopmentCodexCatalog.Overlap(fresh.InstallPath, p))) throw new IOException("安装目录命中参考/排除保护。");
        if (fresh.InstallPath.Length > 0 && RecoveryFileProtection.IsManagedPath(fresh.InstallPath)) throw new IOException("恢复目录不能处理。");
        if (guard.ExecutableIdentity is not null)
        {
            var info = new FileInfo(command.Executable);
            if (info.Length != guard.Length || info.LastWriteTimeUtc.Ticks != guard.Ticks || WindowsFileStorageService.GetFileIdentity(command.Executable) != guard.ExecutableIdentity || exclusions.Any(p => DevelopmentCodexCatalog.Overlap(command.Executable, p))) throw new IOException("卸载程序身份已变化或受到保护。");
        }
    }
    public async Task<SoftwareRestorePoint> CreateRestorePointAsync(CancellationToken token = default)
    {
        var point = await _restore.CreateAsync(token);
        if (point.Success && point.Sequence <= 0) point = point with { Success = false, Error = "未确认新的还原点序号。" };
        using var catalog = new SoftwareCatalog(CatalogPath);
        catalog.Record(new(DateTime.UtcNow, "单独建立还原点", "restore", "建立还原点", point.Success ? "已确认" : "失败", point.Description + " " + point.Error, point.Sequence)); return point;
    }
    public IReadOnlyList<SoftwareOperation> ReadRecords() { if (!File.Exists(CatalogPath)) return []; using var catalog = new SoftwareCatalog(CatalogPath); return catalog.ReadRecords(); }
}
