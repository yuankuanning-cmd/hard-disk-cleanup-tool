using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ChatGPTCleanupTool.Models;

namespace ChatGPTCleanupTool.Services;

public sealed class DevelopmentCleanupService
{
    private readonly string _dataRoot;
    private readonly string _profile;
    private readonly string _local;
    private readonly Dictionary<string, Guard> _guards = [];
    private DevelopmentOptions? _options;
    private sealed record Guard(DevelopmentItem Item, DevelopmentGitState? Git, bool Recycle, bool Remove, bool Manual);
    public string LogPath => Path.Combine(_dataRoot, "development-operations.sqlite");
    public DevelopmentCleanupService(string? dataRoot = null, string? profile = null, string? local = null)
    {
        _profile = profile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _local = local ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dataRoot = dataRoot ?? Path.Combine(_local, "DiskCleanupAssistant");
    }

    public Task<DevelopmentResult> AnalyzeAsync(DevelopmentOptions options, CancellationToken token = default, IProgress<string>? progress = null) =>
        Task.Run(() => Analyze(options, token, progress), token);

    private DevelopmentResult Analyze(DevelopmentOptions options, CancellationToken token, IProgress<string>? progress)
    {
        _guards.Clear(); _options = options;
        var items = new List<DevelopmentItem>(); var warnings = new List<string>();
        var roots = options.Roots.Select(DiskAnalysisService.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Any(p => !Directory.Exists(p) || DiskAnalysisService.HasReparsePointAncestor(p))) throw new IOException("项目扫描范围不存在或包含链接。");
        var exclusions = options.Exclusions ?? [];
        var projects = new Dictionary<string, DevelopmentGitState?>(StringComparer.OrdinalIgnoreCase);
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>(roots.Select(p => (p, 0)));
        bool limited = false; int visited = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var (path, depth) = pending.Pop();
            if (++visited > 50000 || projects.Count >= options.MaximumProjects) { limited = true; break; }
            if (!discovered.Add(path) || exclusions.Any(p => DiskAnalysisService.IsWithin(path, p)) || RecoveryFileProtection.IsManagedPath(path)) continue;
            try
            {
                if (DiskAnalysisService.HasReparsePointAncestor(path)) continue;
                var gitMarker = Path.Combine(path, ".git");
                bool isProject = Directory.Exists(gitMarker) || File.Exists(gitMarker) || HasManifest(path);
                if (isProject)
                {
                    progress?.Report("检查项目状态：" + path);
                    DevelopmentGitState? git = null;
                    try { git = DevelopmentGit.Inspect(path, options.MergeTarget, token); }
                    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { warnings.Add("Git 状态未确认：" + path + " · " + ex.Message); }
                    projects[path] = git;
                    AddBuilds(path, git, items, token);
                    if (git is not null && string.Equals(path, git.Root, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var tree in DevelopmentGit.List(path, token))
                        {
                            if (!roots.Any(r => DiskAnalysisService.IsWithin(tree.Path, r))) continue;
                            if (tree.Path != path && Directory.Exists(tree.Path)) pending.Push((tree.Path, 0));
                            if (items.Any(i => i.Kind == DevelopmentKind.Worktree && string.Equals(i.FullPath, tree.Path, StringComparison.OrdinalIgnoreCase))) continue;
                            items.Add(new() { FullPath = tree.Path, Name = "工作树 · " + Path.GetFileName(tree.Path), Kind = DevelopmentKind.Worktree,
                                Project = tree.Path, Safety = DevelopmentSafety.Unknown, GitState = tree.Prunable ? "已失效（Git 管理记录仍存在）" : "待检查",
                                Evidence = tree.Main ? "主工作树不能移除" : tree.Locked ? "已锁定的工作树不能移除" : "须确认合并、未提交内容、任务引用和托管关系" });
                        }
                    }
                }
                if (depth < 12) foreach (var child in Directory.EnumerateDirectories(path))
                {
                    var name = Path.GetFileName(child);
                    if (SkipDiscovery(name)) continue;
                    pending.Push((child, depth + 1));
                }
                else limited = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("目录无法完整检查：" + path + " · " + ex.Message); }
        }
        if (options.IncludeCaches) AddCaches(items);
        if (options.IncludeCodex) AddCodex(options.CodexHome, items, warnings, token);
        var catalog = DevelopmentCodexCatalog.Load(options.CodexHome, items.Select(i => i.FullPath).ToArray(), token);
        if (!catalog.Complete) warnings.Add(catalog.Warning);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var owners = catalog.Owners(item.FullPath);
            item.TaskState = owners.Length == 0 ? "无直接任务编号匹配" : string.Join("；", owners.Select(t => t.Id + (t.Archived ? " · 已归档" : " · 未归档") + (t.Pinned ? " · 已固定" : "")));
            if (catalog.IsReferenced(item.FullPath)) { item.Safety = DevelopmentSafety.InUse; item.Evidence += "；未归档/固定任务的工作目录或产物引用命中"; }
            if (item.Kind == DevelopmentKind.Codex)
            {
                if (owners.Length == 0) item.Evidence += "；无法证明任务已失效，不以文件年龄判断无用";
                else if (owners.All(t => t.Archived)) item.Evidence += "；任务已归档，但生成图片、日志、补丁不一定能重建，需人工保留/整理";
                if (!catalog.Complete && item.Safety != DevelopmentSafety.InUse) item.Safety = DevelopmentSafety.Unknown;
            }
        }
        var manifestPath = Path.Combine(_dataRoot, "development-manifest.sqlite");
        using (var manifest = new DevelopmentManifest(manifestPath))
        {
            manifest.Clear(); int budget = options.MaximumEntries;
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Kind == DevelopmentKind.Worktree)
                {
                    DevelopmentGitState? git = projects.GetValueOrDefault(item.FullPath);
                    try { if (git is null && Directory.Exists(item.FullPath)) git = DevelopmentGit.Inspect(item.FullPath, options.MergeTarget, token); }
                    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { item.Evidence += "；" + ex.Message; }
                    if (git is not null)
                    {
                        projects[item.FullPath] = git;
                        item.GitState = git.Summary;
                        item.Evidence += "；" + git.Summary;
                        bool managed = git.Managed || catalog.IsManaged(item.FullPath);
                        bool inUse = catalog.IsReferenced(item.FullPath);
                        item.Safety = managed ? DevelopmentSafety.Protected : inUse ? DevelopmentSafety.InUse : !git.Clean ? DevelopmentSafety.Uncommitted : git.Merged ? DevelopmentSafety.Regenerable : DevelopmentSafety.Unknown;
                        if (managed) item.Evidence += "；Codex 托管工作树请在 Codex 中归档（保留恢复快照）";
                        item.CanRemoveWorktree = catalog.Complete && !managed && !inUse && git.Clean && git.Merged && !git.Main && !git.Locked && !DevelopmentGit.HasIgnoredOrSubmodules(git, token);
                        if (git.Clean && !git.Main && !item.CanRemoveWorktree) item.Evidence += "；包含忽略内容/子模块、未确认引用或其他保护条件，仅分析";
                    }
                }
                progress?.Report("统计逻辑大小与实际占用：" + item.FullPath);
                if (budget <= 0) { limited = true; item.Evidence += "；达到本次条目总上限"; continue; }
                manifest.Scan(item, exclusions, budget, token);
                budget -= item.EntryCount;
                if (!item.Complete) limited = true;
                item.CanRecycle = item.Complete && item.Safety == DevelopmentSafety.Regenerable && item.Kind is DevelopmentKind.Build or DevelopmentKind.Cache;
                // Without a complete task/reference catalog, project cleanup remains read-only.
                if (item.Kind == DevelopmentKind.Build && !catalog.Complete) { item.CanRecycle = false; item.Evidence += "；任务引用未确认，清理关闭"; }
                item.CanRemoveWorktree &= item.Complete;
                var owners = catalog.Owners(item.FullPath);
                var codexRelative = Path.GetRelativePath(options.CodexHome, item.FullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                item.CanManuallyRecycle = item.Complete && catalog.Complete && item.Kind == DevelopmentKind.Codex &&
                    codexRelative is "visualizations" or "generated_images" && owners.Length > 0 && owners.All(t => t.Archived && !t.Pinned) && !catalog.IsReferenced(item.FullPath) && !catalog.IsManaged(item.FullPath);
                var gitState = projects.GetValueOrDefault(item.Project);
                _guards[item.Id] = new(Clone(item), gitState, item.CanRecycle, item.CanRemoveWorktree, item.CanManuallyRecycle);
            }
            if (options.CompareBuilds) { progress?.Report("完整哈希比对跨工作树构建产物（受控读取）…"); manifest.CompareBuilds(items, projects, token); }
        }
        if (options.IncludeDocker) AddDocker(items, projects, warnings, token);
        if (limited) warnings.Add("已保留部分结果；达到深度、项目、条目或读取限制的项目不允许清理。");
        return new(items.OrderByDescending(i => i.AllocatedBytes).ToArray(), warnings, projects.Count, catalog.Tasks.Count, limited, manifestPath);
    }

    private static bool HasManifest(string path) => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
        .Any(p => Path.GetExtension(p).Equals(".csproj", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".fsproj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p) is "package.json" or "Cargo.toml" or "pom.xml" or "build.gradle" or "build.gradle.kts" or "pyproject.toml");
    private static bool SkipDiscovery(string name) => name.ToLowerInvariant() is ".git" or ".vs" or "bin" or "obj" or "node_modules" or ".next" or "dist" or "target" or "build" or "venv" or ".venv" or ".codex" || name.StartsWith(".diskcleanup-", StringComparison.OrdinalIgnoreCase);

    private static void AddBuilds(string path, DevelopmentGitState? git, List<DevelopmentItem> items, CancellationToken token)
    {
        bool dotnet = Directory.EnumerateFiles(path).Any(p => Path.GetExtension(p) is ".csproj" or ".fsproj" or ".vbproj");
        bool node = File.Exists(Path.Combine(path, "package.json"));
        bool locked = new[] { "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock" }.Any(n => File.Exists(Path.Combine(path, n)));
        foreach (var name in new[] { "bin", "obj", ".vs", "node_modules", ".next", "dist", "target", "build" })
        {
            var candidate = Path.Combine(path, name); if (!Directory.Exists(candidate) || items.Any(i => string.Equals(i.FullPath, candidate, StringComparison.OrdinalIgnoreCase))) continue;
            var proof = name switch
            {
                "bin" or "obj" when dotnet => ".NET 项目清单可重建 bin / obj（需可用 SDK 和依赖源）",
                "node_modules" when node && locked => "package.json 与锁文件可重新安装依赖；本地改包可能丢失，请先确认",
                ".next" when node && File.Exists(Path.Combine(path, "package.json")) => "Next 输出目录候选；还需确认 build 脚本",
                "dist" when node => "前端输出目录候选；还需确认 build 脚本",
                "target" when File.Exists(Path.Combine(path, "Cargo.toml")) || File.Exists(Path.Combine(path, "pom.xml")) => "Cargo / Maven 项目清单可重建 target",
                "build" when File.Exists(Path.Combine(path, "build.gradle")) || File.Exists(Path.Combine(path, "build.gradle.kts")) => "Gradle 项目清单可重建 build",
                _ => "目录名称本身不是可重建证据；.vs 可能含个人调试配置"
            };
            bool regenerate = dotnet && name is "bin" or "obj" || name == "node_modules" && node && locked || proof.StartsWith("Cargo / Maven") || proof.StartsWith("Gradle");
            if (node && name is ".next" or "dist")
            {
                try { using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "package.json"))); var build = json.RootElement.GetProperty("scripts").GetProperty("build").GetString() ?? "";
                    // A custom build script may produce something else; require a known compiler command.
                    regenerate = name == ".next" ? build.Contains("next build", StringComparison.Ordinal) : build.Contains("vite build", StringComparison.Ordinal) || build.Contains("vue-cli-service build", StringComparison.Ordinal);
                } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException) { regenerate = false; }
            }
            var safety = git is { Clean: false } ? DevelopmentSafety.Uncommitted : git is not null && regenerate && DevelopmentGit.IsIgnoredUntracked(git, candidate, token) ? DevelopmentSafety.Regenerable : DevelopmentSafety.Unknown;
            items.Add(new() { FullPath = candidate, Name = name, Kind = DevelopmentKind.Build, Project = path, GitState = git?.Summary ?? "Git 状态未确认", Safety = safety,
                Evidence = proof + (git is null ? "；缺少 Git 可恢复性证明" : safety == DevelopmentSafety.Unknown ? "；可能被 Git 跟踪 / 不是忽略产物，不开放清理" : "；已检查 Git 跟踪、忽略规则及未提交状态") });
        }
    }

    private void AddCaches(List<DevelopmentItem> items)
    {
        var caches = new (string Name, string Path, bool Safe, string Evidence)[]
        {
            ("NuGet HTTP 缓存", Path.Combine(_local, "NuGet", "v3-cache"), true, "只含 HTTP 下载缓存；不包含全局私有包"),
            ("NuGet 全局包", Path.Combine(_profile, ".nuget", "packages"), false, "可能含本地/私有包，不能保证可重新下载"),
            ("npm 内容缓存", Path.Combine(_local, "npm-cache", "_cacache"), true, "npm 下载内容缓存；源失效或离线时需保留"),
            ("pnpm store", Path.Combine(_local, "pnpm", "store"), false, "共享内容寻址存储可能仍被多个项目硬链接引用；请用 pnpm store prune"),
            ("Yarn 缓存", Path.Combine(_local, "Yarn", "Cache"), true, "下载包缓存；私有源/离线使用请保留"),
            ("pip 缓存", Path.Combine(_local, "pip", "Cache"), true, "下载/构建 wheel 缓存；本地 wheel 和离线使用请先备份"),
            ("Cargo 下载缓存", Path.Combine(_profile, ".cargo", "registry", "cache"), true, "registry 下载包；不会删除 Cargo 源码、git 数据或工具链"),
            ("Cargo registry 源码", Path.Combine(_profile, ".cargo", "registry", "src"), false, "源码可能有本地修改，不按缓存处理"),
            ("Cargo Git 仓库", Path.Combine(_profile, ".cargo", "git"), false, "共享 Git 数据与本地补丁需单独核实"),
            ("Gradle 缓存", Path.Combine(_profile, ".gradle", "caches"), false, "可能含转换产物、本地或已失效依赖；请先关闭 Gradle 并确认依赖源"),
            ("Maven 本地仓库", Path.Combine(_profile, ".m2", "repository"), false, "可能有 mvn install 的私人包，未必可重新下载")
        };
        foreach (var cache in caches.Where(c => Directory.Exists(c.Path)))
            items.Add(new() { FullPath = cache.Path, Name = cache.Name, Kind = DevelopmentKind.Cache, Project = "用户级共享缓存", Safety = cache.Safe ? DevelopmentSafety.Regenerable : DevelopmentSafety.Unknown, Evidence = cache.Evidence + "；独立勾选才纳入扫描（不受项目白名单限制）" });
    }

    private static void AddCodex(string home, List<DevelopmentItem> items, List<string> warnings, CancellationToken token)
    {
        if (!Directory.Exists(home)) return;
        foreach (var name in new[] { "generated_images", "visualizations", ".tmp", "tmp", "cache" })
        {
            var root = Path.Combine(home, name); if (!Directory.Exists(root)) continue;
            var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); int visited = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var (path, depth) = pending.Pop();
                if (++visited > 5000) { warnings.Add("Codex 产物分组达到上限：" + root); break; }
                try
                {
                    if (DiskAnalysisService.HasReparsePointAncestor(path)) continue;
                    bool task = Guid.TryParse(Path.GetFileName(path), out _);
                    if (task || depth >= 4 || Directory.EnumerateFiles(path).Any())
                    {
                        items.Add(new() { FullPath = path, Name = "Codex " + name + " · " + Path.GetFileName(path), Kind = DevelopmentKind.Codex,
                            Safety = DevelopmentSafety.Unknown, Project = "Codex", Evidence = "生成图、可视化、临时补丁可能是唯一成果；只读分析，需证明重建来源后再处理" });
                        continue;
                    }
                    foreach (var child in Directory.EnumerateDirectories(path)) pending.Push((child, depth + 1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("Codex 目录无法分组：" + ex.Message); }
            }
        }
        foreach (var log in Directory.EnumerateFiles(home, "*.log", SearchOption.TopDirectoryOnly))
            items.Add(new() { FullPath = log, Name = "Codex 日志 · " + Path.GetFileName(log), Kind = DevelopmentKind.Codex, Safety = DevelopmentSafety.Unknown, Project = "Codex", Evidence = "运行/审计日志可能仍在写入或用于恢复；只读，不凭年龄删除" });
        // Intentionally exclude auth/config/state/logs SQLite, sessions, archives, skills and memories.
    }

    private static void AddDocker(List<DevelopmentItem> items, Dictionary<string, DevelopmentGitState?> projects, List<string> warnings, CancellationToken token)
    {
        try
        {
            var result = DevelopmentProcess.Run("docker", Environment.CurrentDirectory, ["buildx", "du", "--format=json"], token);
            if (result.ExitCode != 0) { warnings.Add("Docker 构建缓存不可读取（需正在运行的 Docker 与 buildx）。"); return; }
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(5000))
            {
                using var json = JsonDocument.Parse(line); var r = json.RootElement;
                string Get(string key) => r.TryGetProperty(key, out var value) ? value.ToString() : "";
                long.TryParse(Get("Size"), out var size); var description = Get("Description");
                var owners = projects.Keys.Where(p => description.Contains(p, StringComparison.OrdinalIgnoreCase) || description.Contains(p.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToArray();
                items.Add(new() { FullPath = "docker-cache:" + Get("ID"), Name = "Docker 缓存 · " + Get("ID"), Kind = DevelopmentKind.Docker,
                    Project = owners.Length == 1 ? owners[0] + "（路径线索，非独占证明）" : "共享 / 归属未知", Safety = DevelopmentSafety.Unknown, Complete = true,
                    LogicalBytes = size, AllocatedBytes = 0, Evidence = $"{description}；Reclaimable={Get("Reclaimable")} Shared={Get("Shared")}；大小为 BuildKit 报告值，不是卷实际占用；缓存可跨项目复用，不执行全局 prune" });
            }
            // History records establish project provenance, but the public CLI has no reliable per-record join to cache IDs.
            // Show provenance separately instead of attributing shared cache bytes to a guessed project.
            var history = DevelopmentProcess.Run("docker", Environment.CurrentDirectory, ["buildx", "history", "ls", "--format=json"], token);
            if (history.ExitCode == 0)
            {
                using var json = JsonDocument.Parse(history.Output);
                foreach (var record in json.RootElement.EnumerateArray().Take(20))
                {
                    var id = record.GetProperty("ID").GetString() ?? ""; if (id.Length == 0 || id.StartsWith('-')) continue;
                    var inspect = DevelopmentProcess.Run("docker", Environment.CurrentDirectory, ["buildx", "history", "inspect", "--format=json", id], token); if (inspect.ExitCode != 0) continue;
                    using var detail = JsonDocument.Parse(inspect.Output); var r = detail.RootElement;
                    string Get(string key) => r.TryGetProperty(key, out var value) ? value.ToString() : "";
                    var context = Get("Context"); var vcs = Get("VCSRepository");
                    var owners = projects.Where(p => Path.IsPathFullyQualified(context) && string.Equals(DiskAnalysisService.NormalizePath(context), p.Key, StringComparison.OrdinalIgnoreCase) || vcs.Length > 0 && p.Value?.Remote == vcs).Select(p => p.Key).ToArray();
                    items.Add(new() { FullPath = "docker-build:" + id, Name = "构建记录 · " + Get("Name"), Kind = DevelopmentKind.Docker, Safety = DevelopmentSafety.Unknown, Complete = true,
                        Project = owners.Length == 1 ? owners[0] : "归属未知 / 多个工作树", Evidence = $"Context={context}；VCS={vcs}；Revision={Get("VCSRevision")}；项目来源记录，不计为独占可释放缓存" });
                }
            }
            else warnings.Add("此 buildx 不支持构建历史来源分析；缓存保留为共享/未知归属。");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException or KeyNotFoundException)
        { warnings.Add("Docker 分析受限：" + ex.Message); }
    }

    public Task<DevelopmentOperationResult> ExecuteAsync(IReadOnlyList<DevelopmentItem> selected, bool removeWorktrees, bool dryRun,
        Func<IReadOnlyList<string>> currentExclusions, CancellationToken token = default, bool manuallyConfirmedArchive = false)
    {
        var completion = new TaskCompletionSource<DevelopmentOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var records = new List<DevelopmentOperation>();
            try
            {
                if (_options is null) throw new InvalidOperationException("请先扫描。");
                using var manifest = new DevelopmentManifest(Path.Combine(_dataRoot, "development-manifest.sqlite"));
                using var audit = OpenAudit();
                foreach (var selection in selected)
                {
                    if (token.IsCancellationRequested) break;
                    var action = removeWorktrees ? "Git 移除工作树（保留分支）" : "移入回收站";
                    string path = selection.FullPath, result, reason; long size = 0;
                    try
                    {
                        if (!_guards.TryGetValue(selection.Id, out var guard) || !string.Equals(path, guard.Item.FullPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("不是本次可信扫描计划。");
                        var item = guard.Item; size = item.TheoreticalBytes;
                        if (!item.Complete || !(removeWorktrees ? guard.Remove : manuallyConfirmedArchive ? guard.Manual : guard.Recycle)) throw new IOException("该项目未证明可恢复，或仍受保护。");
                        var exclusions = currentExclusions(); ValidateTarget(path, exclusions);
                        var catalog = DevelopmentCodexCatalog.Load(_options.CodexHome, [path], token);
                        if (catalog.IsReferenced(path) || item.Kind is DevelopmentKind.Build or DevelopmentKind.Worktree && !catalog.Complete) throw new IOException("任务仍引用此路径或引用索引无法读全。");
                        if (catalog.IsManaged(path)) throw new IOException("Codex 托管路径必须通过 Codex 归档。");
                        if (manuallyConfirmedArchive)
                        {
                            var owners = catalog.Owners(path);
                            if (!catalog.Complete || owners.Length == 0 || owners.Any(t => !t.Archived || t.Pinned)) throw new IOException("归档状态已变化或归属未确认。");
                            var artifactGit = DevelopmentGit.Inspect(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, _options.MergeTarget, token);
                            if (artifactGit is not null && (!artifactGit.Clean || !DevelopmentGit.IsIgnoredUntracked(artifactGit, path, token))) throw new IOException("归档产物涉及未提交或 Git 跟踪内容。");
                            foreach (var owner in owners.Where(t => Directory.Exists(t.Cwd)))
                            { var ownerGit = DevelopmentGit.Inspect(owner.Cwd, _options.MergeTarget, token); if (ownerGit is { Clean: false }) throw new IOException("归档任务的原工作目录仍有未提交内容，先保存变更。"); }
                        }
                        if (guard.Git is { } original)
                        {
                            var fresh = DevelopmentGit.Inspect(item.Project, _options.MergeTarget, token) ?? throw new IOException("Git 状态已失效。");
                            if (!fresh.Clean || fresh.Head != original.Head || fresh.CommonDirectory != original.CommonDirectory) throw new IOException("Git 有未提交内容、提交或工作树关系已变化。");
                            if (removeWorktrees)
                            {
                                if (fresh.Main || fresh.Locked || fresh.Managed || !fresh.Merged || fresh.TargetHead != original.TargetHead || DevelopmentGit.HasIgnoredOrSubmodules(fresh, token)) throw new IOException("工作树合并目标、忽略内容或保护状态已变化。");
                            }
                            else if (!DevelopmentGit.IsIgnoredUntracked(fresh, path, token)) throw new IOException("目录已被 Git 跟踪，或不再是忽略产物。");
                        }
                        manifest.Validate(item, exclusions, token); ValidateTarget(path, currentExclusions()); token.ThrowIfCancellationRequested();
                        WriteAudit(audit, new(path, action, "执行前记录", dryRun ? "模拟，不修改文件" : "安全检查已通过", size, 0, DateTime.UtcNow));
                        if (!dryRun)
                        {
                            if (removeWorktrees)
                            {
                                // No --force, no branch deletion and no manual folder deletion.
                                var coordinator = DevelopmentGit.List(item.Project, token).First(t => t.Main).Path;
                                var removed = DevelopmentProcess.Git(coordinator, token, "worktree", "remove", "--", path);
                                if (removed.ExitCode != 0) throw new IOException("Git 拒绝移除：" + removed.Error.Trim());
                            }
                            else SpaceRecycleService.RecycleWithShell(path);
                        }
                        result = dryRun ? "模拟通过" : removeWorktrees ? "已移除" : "已回收";
                        reason = removeWorktrees ? "分支与合并目标保留，可用 git worktree add 恢复已提交文件；不保留已删除 checkout 的目录元数据" : "回收站可恢复；未清空回收站，实际释放空间记为 0";
                        if (!dryRun) _guards.Remove(selection.Id);
                    }
                    catch (Exception ex) { result = ex is OperationCanceledException ? "已取消" : "已跳过"; reason = ex.Message; }
                    var record = new DevelopmentOperation(path, action, result, reason, size, 0, DateTime.UtcNow); WriteAudit(audit, record); records.Add(record);
                }
                completion.SetResult(new(records, dryRun));
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Development cleanup validation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private static void ValidateTarget(string path, IReadOnlyList<string> exclusions)
    {
        RecoveryFileProtection.EnsureNotManaged(path);
        if (!Path.IsPathFullyQualified(path) || DiskAnalysisService.HasReparsePointAncestor(path) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) throw new IOException("只支持无链接的本地固定磁盘路径。");
        if (exclusions.Any(p => DevelopmentCodexCatalog.Overlap(path, p))) throw new IOException("包含参考目录或排除项。");
        var protectedRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
        if (protectedRoots.Where(p => p.Length > 0).Any(p => DevelopmentCodexCatalog.Overlap(path, p)) || DiskAnalysisService.IsWithin(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path)) throw new IOException("系统、程序目录与整个用户目录不能清理。");
    }

    private SqliteConnection OpenAudit()
    {
        Directory.CreateDirectory(_dataRoot); var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LogPath, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS operations(id INTEGER PRIMARY KEY,record TEXT NOT NULL)"; command.ExecuteNonQuery(); return db;
    }
    private static void WriteAudit(SqliteConnection db, DevelopmentOperation record)
    { using var command = db.CreateCommand(); command.CommandText = "INSERT INTO operations(record) VALUES($record)"; command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(record)); command.ExecuteNonQuery(); }
    public IReadOnlyList<DevelopmentOperation> ReadRecords()
    {
        if (!File.Exists(LogPath)) return [];
        using var db = OpenAudit(); using var command = db.CreateCommand(); command.CommandText = "SELECT record FROM operations ORDER BY id DESC LIMIT 200";
        var records = new List<DevelopmentOperation>(); using var reader = command.ExecuteReader(); while (reader.Read()) if (JsonSerializer.Deserialize<DevelopmentOperation>(reader.GetString(0)) is { } record) records.Add(record); return records;
    }
    private static DevelopmentItem Clone(DevelopmentItem item) => new() { Id = item.Id, FullPath = item.FullPath, Name = item.Name, Kind = item.Kind, Safety = item.Safety,
        Project = item.Project, GitState = item.GitState, TaskState = item.TaskState, Evidence = item.Evidence, Complete = item.Complete,
        LogicalBytes = item.LogicalBytes, AllocatedBytes = item.AllocatedBytes, TheoreticalBytes = item.TheoreticalBytes, FileCount = item.FileCount, LastModifiedUtc = item.LastModifiedUtc };
}
