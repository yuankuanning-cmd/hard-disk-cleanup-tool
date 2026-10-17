using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed class NpsProjectService
{
    private readonly string _catalogPath, _manifestPath;
    private readonly IStorageDeviceProbe _probe;
    private readonly Func<string, bool> _running;
    private readonly Action<string> _recycle;
    private readonly Dictionary<string, Guard> _guards = new();
    private sealed record Guard(NpsProjectItem Project, string ManifestHash, NpsCleanupItem View, DevelopmentItem Snapshot);
    private static readonly EnumerationOptions Enumeration = new() { AttributesToSkip = 0, IgnoreInaccessible = false };
    public NpsProjectService(string? dataRoot = null, IStorageDeviceProbe? probe = null, Func<string, bool>? running = null, Action<string>? recycle = null)
    {
        var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanupAssistant");
        _catalogPath = Path.Combine(root, "nps-projects.sqlite"); _manifestPath = Path.Combine(root, "nps-measurements.sqlite");
        _probe = probe ?? new StorageDeviceProbe(); _running = running ?? HasRunningNps; _recycle = recycle ?? SpaceRecycleService.RecycleWithShell;
    }
    public static IReadOnlyList<string> DiskRoots(bool removable = false) => DriveInfo.GetDrives()
        .Where(d => (d.DriveType == DriveType.Fixed || removable && d.DriveType == DriveType.Removable) && d.IsReady).Select(d => d.RootDirectory.FullName).ToArray();
    public Task<NpsScanResult> DiscoverAsync(NpsScanOptions options, CancellationToken token = default, IProgress<string>? progress = null) => Task.Run(async () =>
    {
        foreach (var guard in _guards.Values) guard.View.CanRecycle = false;
        _guards.Clear();
        var roots = options.Roots.Select(DiskAnalysisService.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        roots = roots.Where(r => !roots.Any(p => p != r && DiskAnalysisService.IsWithin(r, p))).ToArray();
        if (roots.Length == 0) throw new IOException("请至少选择一个可访问磁盘或目录");
        var warnings = new ConcurrentQueue<string>(); long files = 0, directories = 0, skipped = 0; int markers = 0, incomplete = 0;
        var policy = StorageDevicePolicy.Choose(roots, new ScanEngineOptions { MaximumParallelism = 2 }, _probe);
        using var queue = new BlockingCollection<NpsMarker>(256); using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var catalog = new NpsProjectCatalog(_catalogPath); catalog.Start();
        using (var snapshots = new DevelopmentManifest(_manifestPath)) snapshots.Clear();
        void Warn(string message) { Interlocked.Increment(ref skipped); if (warnings.Count < 500) warnings.Enqueue(message); }
        var writer = Task.Run(() =>
        {
            try
            {
                var batch = new List<NpsMarker>(128);
                foreach (var marker in queue.GetConsumingEnumerable())
                { batch.Add(marker); if (batch.Count == 128) { catalog.Add(batch); batch.Clear(); } }
                if (batch.Count > 0) catalog.Add(batch);
            }
            catch { stop.Cancel(); throw; }
        });
        var producer = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = policy.Workers, CancellationToken = stop.Token }, root =>
                {
                    var watch = Stopwatch.StartNew(); var iterators = new Stack<IEnumerator<FileSystemInfo>>(); var inspectedStructures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        if (DiskAnalysisService.HasReparsePointAncestor(root) || RecoveryFileProtection.IsManagedPath(root)) { Warn("扫描根是链接或清理工具恢复项，未跟随：" + root); Interlocked.Exchange(ref incomplete, 1); return; }
                        iterators.Push(new DirectoryInfo(root).EnumerateFileSystemInfos("*", Enumeration).GetEnumerator());
                        while (iterators.Count > 0)
                        {
                            stop.Token.ThrowIfCancellationRequested(); var iterator = iterators.Peek(); FileSystemInfo entry;
                            try { if (!iterator.MoveNext()) { iterator.Dispose(); iterators.Pop(); continue; } entry = iterator.Current; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            { Warn("目录未读全：" + ex.Message); iterator.Dispose(); iterators.Pop(); continue; }
                            if (watch.ElapsedMilliseconds > 250)
                            { watch.Restart(); progress?.Report($"已查看文件 {Interlocked.Read(ref files):N0} / 目录 {Interlocked.Read(ref directories):N0} · 标志 {Volatile.Read(ref markers):N0} · 跳过 {Interlocked.Read(ref skipped):N0}\n{entry.FullName}"); }
                            var attributes = entry.Attributes;
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x400000)) != 0 || entry.Name.StartsWith(".diskcleanup-", StringComparison.OrdinalIgnoreCase))
                            { Warn("链接/云占位/恢复项未读取：" + entry.FullName); continue; }
                            if (attributes.HasFlag(FileAttributes.Directory))
                            {
                                Interlocked.Increment(ref directories);
                                if (entry.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                                { Warn("系统回收/卷管理目录未扫描：" + entry.FullName); continue; }
                                if (NpsProjectRecognizer.IsStructureClue(entry.Name))
                                {
                                    var owner = Path.GetDirectoryName(entry.FullName)!;
                                    if (!inspectedStructures.Contains(owner) && NpsProjectRecognizer.ReadStructure(owner) is { } structure)
                                    {
                                        inspectedStructures.Add(owner);
                                        if (Interlocked.Increment(ref markers) > options.MaximumMarkers) { Interlocked.Exchange(ref incomplete, 1); stop.Cancel(); break; }
                                        queue.Add(structure, stop.Token);
                                    }
                                }
                                if (iterators.Count >= 512) { Warn("目录深度超过512：" + entry.FullName); continue; }
                                try { iterators.Push(((DirectoryInfo)entry).EnumerateFileSystemInfos("*", Enumeration).GetEnumerator()); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn("目录不可访问：" + entry.FullName + " · " + ex.Message); }
                            }
                            else
                            {
                                Interlocked.Increment(ref files);
                                if (!entry.Name.EndsWith(".emtproj", StringComparison.OrdinalIgnoreCase) && !entry.Name.EndsWith(".npsdata", StringComparison.OrdinalIgnoreCase)) continue;
                                if (Interlocked.Increment(ref markers) > options.MaximumMarkers) { Interlocked.Exchange(ref incomplete, 1); stop.Cancel(); break; }
                                queue.Add(NpsProjectRecognizer.Read(entry.FullName), stop.Token);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Warn("根范围读取不完整：" + root + " · " + ex.Message); Interlocked.Exchange(ref incomplete, 1); }
                    finally { foreach (var iterator in iterators) iterator.Dispose(); }
                });
            }
            catch (OperationCanceledException) { Interlocked.Exchange(ref incomplete, 1); }
            finally { queue.CompleteAdding(); }
        });
        await Task.WhenAll(producer, writer);
        var notes = warnings.ToList();
        if (incomplete != 0) notes.Add(token.IsCancellationRequested ? "已取消，保留已发现清单；未覆盖所有范围。" : "扫描达到上限/部分范围失败；结果不代表全盘完整。");
        notes.Insert(0, "结合工程XML清单、model及配套目录、已验证模型结构、NPS专用脚本识别完整工程，不以npsdata后缀单独定性。仅可访问范围；权限、链接/云占位等跳过项列出。自动统计整个工程大小，不自动勾选；孤立文件只作待归属线索，不单独删除。");
        var projects = catalog.Resolve(options.MaximumProjects, notes);
        bool complete = incomplete == 0 && !notes.Any(n => n.Contains("结果达到上限"));
        var result = new NpsScanResult(projects, notes, complete, files, directories, skipped, DateTime.UtcNow, string.Join("; ", roots), policy.Description);
        catalog.Save(result); return result;
    });
    public NpsScanResult? LoadPrevious()
    { _guards.Clear(); if (!File.Exists(_catalogPath)) return null; using var catalog = new NpsProjectCatalog(_catalogPath); return catalog.Load(); }
    public void Save(NpsScanResult result) { using var catalog = new NpsProjectCatalog(_catalogPath); catalog.Save(result); }

    public Task<IReadOnlyList<NpsCleanupItem>> MeasureAsync(IReadOnlyList<NpsProjectItem> projects, IReadOnlyList<string> exclusions,
        CancellationToken token = default, IProgress<string>? progress = null, bool replaceAll = false) => Task.Run<IReadOnlyList<NpsCleanupItem>>(() =>
    {
        using var manifest = new DevelopmentManifest(_manifestPath); var views = new List<NpsCleanupItem>(); var progressWatch = Stopwatch.StartNew();
        if (replaceAll) { foreach (var guard in _guards.Values) guard.View.CanRecycle = false; _guards.Clear(); manifest.Clear(); }
        foreach (var project in projects.DistinctBy(p => p.Id))
        {
            if (views.Count == 0 || progressWatch.ElapsedMilliseconds >= 250) { progressWatch.Restart(); progress?.Report($"自动统计大小 {views.Count + 1:N0}/{projects.Count:N0} · {project.Name}\n" + project.FullPath); }
            if (_guards.Remove(project.Id, out var old)) { old.View.CanRecycle = false; manifest.Forget(old.View.Id); }
            bool fileOnly = project.Kind == NpsProjectKind.StandaloneModel;
            var view = new NpsCleanupItem { Id = Guid.NewGuid().ToString("N"), ProjectId = project.Id, ProjectName = project.Name, FullPath = project.FullPath, Kind = fileOnly ? NpsCleanupKind.ModelFile : NpsCleanupKind.WholeProject };
            project.Cleanup = view;
            project.LogicalBytes = null; project.AllocatedBytes = null; project.FileCount = null; project.LastModifiedUtc = null;
            views.Add(view);
            if (token.IsCancellationRequested) { view.Evidence = "统计已取消；重新扫描后可勾选"; continue; }
            try
            {
                // Measure before eligibility checks: protected items still show their real size, never a misleading 0 B.
                string hash = "";
                var snapshot = new DevelopmentItem { Id = view.Id, FullPath = project.FullPath, Name = project.Name, Kind = DevelopmentKind.Build };
                manifest.Scan(snapshot, [], int.MaxValue, token, npsMeasurement: true, progress: progress);
                view.LogicalBytes = snapshot.LogicalBytes; view.AllocatedBytes = snapshot.AllocatedBytes; view.FileCount = snapshot.FileCount;
                project.LogicalBytes = snapshot.LogicalBytes; project.AllocatedBytes = snapshot.AllocatedBytes; project.FileCount = snapshot.FileCount; project.LastModifiedUtc = snapshot.LastModifiedUtc;
                view.PartialSize = !snapshot.Complete;
                if (!snapshot.Complete) throw new IOException(snapshot.Evidence.Length > 0 ? snapshot.Evidence : "位置未读全");
                if (fileOnly)
                {
                    throw new IOException("待归属模型线索：尚未确认整个工程目录，不单独删除文件");
                }
                if (!project.Recognized || project.Kind is NpsProjectKind.Suspected or NpsProjectKind.Recovery) throw new IOException("工程归属未确认或属于工具恢复快照，不能整体删除");
                if (!string.Equals(project.Id, project.FullPath, StringComparison.OrdinalIgnoreCase) ||
                    NpsProjectRecognizer.Classify(project.FullPath) == NpsProjectKind.Recovery)
                    throw new IOException("工程清单必须位于对应目录本层，不能借外部清单或恢复目录授权清理");
                EnsureRootSafe(project.FullPath, exclusions);
                var proof = NpsProjectRecognizer.InspectRoot(project.FullPath);
                if (!proof.Confirmed || !string.Equals(proof.ManifestPath, project.ManifestPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("工程边界依据已变化或不足，不能整体回收");
                hash = proof.ManifestPath.Length > 0 ? NpsProjectRecognizer.ManifestHash(proof.ManifestPath) : "";
                if (manifest.HasNestedNpsProject(snapshot.Id, project.FullPath, project.ManifestPath)) throw new IOException("包含其他独立工程；请单独处理子工程，不能整体回收");
                if (FindGitAncestor(project.FullPath)) throw new IOException("工程位于Git仓库；请在开发环境/Git中核实，避免回收版本库或工作树关系");
                if (proof.ManifestPath.Length > 0 && hash != NpsProjectRecognizer.ManifestHash(proof.ManifestPath)) throw new IOException("测量期间工程清单变化");
                view.CanRecycle = true; view.Evidence = "可勾选删除 · 整个工程文件夹（含模型、脚本、结果）";
                _guards[project.Id] = new(project, hash, view, snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { view.CanRecycle = false; view.Evidence = "不可勾选：" + ex.Message; }
        }
        return views;
    });

    public Task<NpsCleanupResult> RecycleAsync(IReadOnlyList<NpsCleanupItem> selected, bool backedUpAndClosed, bool dryRun,
        Func<IReadOnlyList<string>> currentExclusions, CancellationToken token = default, IProgress<string>? progress = null) => Task.Run(() =>
    {
        var records = new List<NpsOperation>(); using var catalog = new NpsProjectCatalog(_catalogPath); using var manifest = new DevelopmentManifest(_manifestPath);
        void Record(NpsOperation record) { catalog.Record(record); records.Add(record); }
        int processed = 0;
        foreach (var item in selected.DistinctBy(i => i.Id))
        {
            if (token.IsCancellationRequested) break;
            progress?.Report($"{(dryRun ? "模拟检查" : "删除到回收站")} {++processed:N0}/{selected.Count:N0}\n{item.FullPath}");
            try
            {
                if (!dryRun && !backedUpAndClosed) throw new IOException("必须明确确认工程已备份并关闭NPS");
                if (!_guards.TryGetValue(item.ProjectId, out var guard) || !ReferenceEquals(item, guard.View) || !item.CanRecycle) throw new IOException("扫描结果已过期，请重新扫描");
                if (item.Kind != NpsCleanupKind.WholeProject || guard.Project.Kind == NpsProjectKind.StandaloneModel) throw new IOException("本页只回收完整工程，不单独删除模型文件");
                var exclusions = currentExclusions(); EnsureRootSafe(guard.Project.FullPath, exclusions);
                if (_running(guard.Project.FullPath)) throw new IOException("检测到NPS/工程内程序仍在运行，请先保存并关闭；不会强制结束");
                var proof = NpsProjectRecognizer.InspectRoot(item.FullPath);
                if (FindGitAncestor(item.FullPath) || !proof.Confirmed || !string.Equals(proof.ManifestPath, guard.Project.ManifestPath, StringComparison.OrdinalIgnoreCase) ||
                    proof.ManifestPath.Length > 0 && NpsProjectRecognizer.ManifestHash(proof.ManifestPath) != guard.ManifestHash) throw new IOException("工程边界/清单已变化，请重新扫描");
                manifest.Validate(guard.Snapshot, exclusions, token, metadataOnly: true);
                token.ThrowIfCancellationRequested();
                if (dryRun) { Record(new(DateTime.UtcNow, item.FullPath, item.KindText, "模拟通过", "未移动/删除文件；未清空回收站")); continue; }
                Record(new(DateTime.UtcNow, item.FullPath, item.KindText, "执行前记录", "使用者确认删除；强制进入Windows回收站"));
                RecycleOnSta(item.FullPath);
                if (Directory.Exists(item.FullPath) || File.Exists(item.FullPath)) throw new IOException("目标仍在，未确认回收完成");
                item.CanRecycle = false; _guards.Remove(item.ProjectId); // Selection notifications belong to the UI thread.
                item.Evidence = "已移入回收站";
                Record(new(DateTime.UtcNow, item.FullPath, item.KindText, "已回收", "回收站未清空，实际释放为0；可在Windows回收站恢复"));
            }
            catch (Exception ex) { item.Evidence = "失败/跳过：" + ex.Message; Record(new(DateTime.UtcNow, item.FullPath, item.KindText, "已跳过 / 失败", ex.Message)); if (ex is OperationCanceledException) break; }
        }
        return new NpsCleanupResult(records, dryRun);
    }, token);
    private void RecycleOnSta(string path)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { _recycle(path); } catch (Exception ex) { failure = ex; } }) { IsBackground = true, Name = "NPS project recycle" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
    public IReadOnlyList<NpsOperation> Records()
    { if (!File.Exists(_catalogPath)) return []; using var catalog = new NpsProjectCatalog(_catalogPath); return catalog.Records(); }
    internal static void EnsureRootSafe(string root, IReadOnlyList<string> exclusions)
    {
        root = DiskAnalysisService.NormalizePath(root); RecoveryFileProtection.EnsureNotManaged(root);
        if (root == Path.GetPathRoot(root) || !Directory.Exists(root) || DiskAnalysisService.HasReparsePointAncestor(root) || new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed) throw new IOException("只允许本地固定卷普通工程目录，根目录/链接不可回收");
        var protectedPaths = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Where(p => p.Length > 0);
        if (protectedPaths.Any(p => DevelopmentCodexCatalog.Overlap(root, p)) || DiskAnalysisService.IsWithin(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root) || exclusions.Any(p => DevelopmentCodexCatalog.Overlap(root, p)))
            throw new IOException("命中系统目录、整个用户目录、原件或排除保护");
    }
    private static bool FindGitAncestor(string path)
    { for (string? p = path; p is not null; p = Path.GetDirectoryName(p)) if (File.Exists(Path.Combine(p, ".git")) || Directory.Exists(Path.Combine(p, ".git"))) return true; return false; }
    private static bool HasRunningNps(string root)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Contains("nps", StringComparison.OrdinalIgnoreCase)) return true;
                    if (process.ProcessName.StartsWith("Process", StringComparison.OrdinalIgnoreCase))
                    { var path = process.MainModule?.FileName; if (path is not null && DiskAnalysisService.IsWithin(path, root)) return true; }
                }
                catch (System.ComponentModel.Win32Exception) { if (process.ProcessName.StartsWith("Process", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }
}
