using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;
using Microsoft.Data.Sqlite;

internal static class DevelopmentTests
{
    private static DevelopmentResult? _ui;
    internal static DevelopmentResult UiResult => _ui ?? throw new InvalidOperationException("缺少 UI 结果");
    public static async Task RunAsync(string testRoot, bool recycle)
    {
        var root = Path.Combine(testRoot, "development"); var scope = Path.Combine(root, "scope"); var main = Path.Combine(scope, "main");
        Directory.CreateDirectory(main); var codex = Path.Combine(root, ".codex"); Directory.CreateDirectory(codex);
        var state = Path.Combine(codex, "state_5.sqlite");
        using (var db = new SqliteConnection("Data Source=" + state + ";Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE threads(id TEXT,cwd TEXT,rollout_path TEXT,archived INTEGER,is_pinned INTEGER); CREATE TABLE thread_attachments(attachment_type TEXT,payload TEXT)"; command.ExecuteNonQuery();
        }
        Write(Path.Combine(main, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(Path.Combine(main, "Program.cs"), "Console.WriteLine(\"test\");");
        Write(Path.Combine(main, ".gitignore"), "bin/\nobj/\n.vs/\nnode_modules/\n.next/\ndist/\n");
        Git(main, "init", "-b", "main"); Git(main, "add", "."); Git(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture");
        var merged = Path.Combine(scope, "merged"); Git(main, "worktree", "add", "-b", "feature-done", merged);
        var content = new string('a', 8192); Write(Path.Combine(main, "bin", "Release", "app.dll"), content); Write(Path.Combine(main, "obj", "generated.txt"), "generated");
        Write(Path.Combine(main, ".vs", "local.suo"), "personal setting"); Write(Path.Combine(merged, "bin", "Release", "app.dll"), content);
        var taskId = Guid.NewGuid().ToString(); var image = Write(Path.Combine(codex, "visualizations", "2026", "10", "08", taskId, "unique.png"), "unique generated image");
        AddTask(state, taskId, Path.Combine(root, "retired"), "", archived: true);
        var profile = Path.Combine(root, "profile"); var local = Path.Combine(root, "local");
        Write(Path.Combine(local, "pip", "Cache", "wheel.bin"), "downloaded"); Write(Path.Combine(profile, ".m2", "repository", "private.jar"), "irreplaceable");
        Write(Path.Combine(local, "pnpm", "store", "linked.bin"), "shared"); Write(Path.Combine(profile, ".nuget", "packages", "private.nupkg"), "private");
        var service = new DevelopmentCleanupService(Path.Combine(root, "data"), profile, local);
        var options = new DevelopmentOptions([scope], codex, IncludeCaches: true);
        var watch = Stopwatch.StartNew(); var result = await service.AnalyzeAsync(options); _ui = result;
        DevelopmentItem Row(string path) => result.Items.Single(i => i.FullPath == path);
        Check(result.ProjectCount == 2, "应发现主工作树和关联工作树");
        var bin = Row(Path.Combine(main, "bin")); Check(bin.CanRecycle && bin.Safety == DevelopmentSafety.Regenerable && bin.FileCount == 1 && bin.LogicalBytes == 8192, "干净忽略构建目录应可回收并有准确统计");
        Check(bin.AllocatedBytes > 0 && bin.DuplicateHint.Contains("跨工作树"), "没有实际占用或完整哈希跨树比对");
        Check(!Row(Path.Combine(main, ".vs")).CanRecycle, ".vs 个人配置不应当作可重建");
        var archived = result.Items.Single(i => image.StartsWith(i.FullPath) && i.Kind == DevelopmentKind.Codex);
        Check(archived.TaskState.Contains("已归档") && !archived.CanRecycle, "归档不应使唯一生成成果可删除");
        Check(archived.CanManuallyRecycle, "已归档无活动引用的成果应提供备份后人工确认入口");
        var defaultArchive = await service.ExecuteAsync([archived], false, true, () => []); Check(defaultArchive.Failed == 1, "未知归档成果不能进入普通可重建清理");
        var manualArchive = await service.ExecuteAsync([archived], false, true, () => [], manuallyConfirmedArchive: true); Check(manualArchive.Succeeded == 1 && File.Exists(image), "人工确认归档模拟没有通过或修改了文件");
        SetArchived(state, taskId, false);
        var reactivatedArchive = await service.ExecuteAsync([archived], false, true, () => [], manuallyConfirmedArchive: true); Check(reactivatedArchive.Failed == 1, "归档任务重新启用后旧清理计划仍可执行");
        SetArchived(state, taskId);
        Check(!Row(Path.Combine(profile, ".m2", "repository")).CanRecycle && !Row(Path.Combine(profile, ".nuget", "packages")).CanRecycle && !Row(Path.Combine(local, "pnpm", "store")).CanRecycle, "私人包与共享 pnpm store 必须保留");
        var dry = await service.ExecuteAsync([bin], false, true, () => []); Check(dry.Succeeded == 1 && Directory.Exists(bin.FullPath), "模拟不应修改文件");
        var excluded = await service.ExecuteAsync([bin], false, true, () => [Path.Combine(bin.FullPath, "Release")]); Check(excluded.Failed == 1, "新增排除规则未生效");
        Write(Path.Combine(bin.FullPath, "new.tmp"), "new"); var changed = await service.ExecuteAsync([bin], false, true, () => []); Check(changed.Failed == 1 && changed.Records[0].Reason.Contains("扫描后"), "扫描后新增内容未被阻止");
        result = await service.AnalyzeAsync(options); bin = Row(Path.Combine(main, "bin"));
        var activeId = Guid.NewGuid().ToString(); var rollout = Write(Path.Combine(codex, "sessions", "active.jsonl"), "{\"path\":\"" + bin.FullPath.Replace("\\", "\\\\") + "\"}");
        AddTask(state, activeId, Path.Combine(root, "other-project"), rollout, false);
        var active = await service.ExecuteAsync([bin], false, true, () => []); Check(active.Failed == 1, "其他任务的产物引用未阻止清理");
        result = await service.AnalyzeAsync(options); Check(Row(bin.FullPath).Safety == DevelopmentSafety.InUse && !Row(bin.FullPath).CanRecycle, "活动任务引用未显示保护");
        SetArchived(state, activeId); result = await service.AnalyzeAsync(options);
        var dirtySource = Path.Combine(main, "Program.cs"); File.AppendAllText(dirtySource, "\n// uncommitted");
        var dirty = await service.ExecuteAsync([Row(bin.FullPath)], false, true, () => []); Check(dirty.Failed == 1 && dirty.Records[0].Reason.Contains("Git"), "Git 变脏后应拒绝旧计划");
        result = await service.AnalyzeAsync(options); Check(Row(bin.FullPath).Safety == DevelopmentSafety.Uncommitted && !Row(bin.FullPath).CanRecycle, "脏项目未分类为未提交");
        Git(main, "add", "Program.cs"); Git(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture update");
        // Tracking a file inside an ignored output defeats the directory's regenerability evidence.
        Write(Path.Combine(main, "obj", "keep.txt"), "source in obj"); Git(main, "add", "-f", "obj/keep.txt"); Git(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "tracked output");
        result = await service.AnalyzeAsync(options); Check(!Row(Path.Combine(main, "obj")).CanRecycle, "Git 已跟踪的产物不应可回收");
        // A clean, merged linked checkout with no ignored files can be removed through Git, not recursive deletion.
        var removable = Path.Combine(scope, "removable"); Git(main, "worktree", "add", "-b", "feature-empty", removable);
        result = await service.AnalyzeAsync(options); var worktree = Row(removable);
        Check(worktree.CanRemoveWorktree, "干净已合并工作树未提供 Git 移除操作");
        var removeDry = await service.ExecuteAsync([worktree], true, true, () => []); Check(removeDry.Succeeded == 1 && Directory.Exists(removable), "工作树模拟失败");
        if (recycle)
        {
            var removal = await service.ExecuteAsync([worktree], true, false, () => []); Check(removal.Succeeded == 1 && !Directory.Exists(removable), "Git 正常移除失败");
            Check(Git(main, "rev-parse", "--verify", "feature-empty").Length > 0, "移除工作树不能删除分支");
            result = await service.AnalyzeAsync(options); var pip = Row(Path.Combine(local, "pip", "Cache"));
            var recycling = await service.ExecuteAsync([pip], false, false, () => []); Check(recycling.Succeeded == 1 && !Directory.Exists(pip.FullPath) && recycling.Records[0].ActuallyReleasedBytes == 0, "强制回收站操作或释放空间记录错误");
            result = await service.AnalyzeAsync(options); var archiveNow = result.Items.Single(i => i.FullPath == archived.FullPath);
            var archiveRecycle = await service.ExecuteAsync([archiveNow], false, false, () => [], manuallyConfirmedArchive: true);
            Check(archiveRecycle.Succeeded == 1 && !File.Exists(image), "经备份确认的归档成果未回收");
        }
        var noMetadata = await service.AnalyzeAsync(options with { CodexHome = Path.Combine(root, "missing") });
        Check(noMetadata.Items.Where(i => i.Kind == DevelopmentKind.Build).All(i => !i.CanRecycle), "任务索引不可用必须关闭项目清理");
        var limit = await service.AnalyzeAsync(options with { MaximumEntries = 2 }); Check(limit.Limited && limit.Items.All(i => !i.CanRecycle && !i.CanRemoveWorktree), "部分扫描结果不能清理");
        var records = service.ReadRecords(); Check(records.Any(r => r.Result == "执行前记录") && records.Any(r => r.Result == "已跳过"), "缺少持久执行意图与失败原因");
        var legacy = await new CodexSmartCleanupService().AnalyzeAsync(codex, 1, 0, true);
        Check(legacy.Items.All(i => !i.IsSafeToDelete) && legacy.EstimatedReclaimBytes == 0, "旧 Codex 年龄评分仍在授权删除");
        Check(result.Items.All(i => !i.IsSelected), "清理计划不能默认勾选");
        Console.WriteLine($"PASS DEVELOPMENT: {watch.ElapsedMilliseconds} ms; Git worktrees/dirty/tracked outputs, Codex archived/active references, caches, full-hash cross-tree copies, changed manifests, fresh exclusions, partial limits, audit{(recycle ? ", native recycle and Git removal" : "")}");
    }
    public static void RunUi(string output)
    {
        Check(_ui is not null, "缺少开发环境 UI 测试结果");
        var control = new DevelopmentCenter { RootPath = @"D:\Projects", CodexHome = @"C:\Users\Example\.codex" }; control.SetResult(_ui!);
        control.Measure(new Size(1500, 800)); control.Arrange(new Rect(0, 0, 1500, 800)); control.UpdateLayout();
        control.SetSearchText("not-found-test-xyz"); Check(CollectionViewSource.GetDefaultView(control.ResultGrid.ItemsSource).IsEmpty, "开发环境搜索未生效");
        control.SetSearchText(""); Check(!CollectionViewSource.GetDefaultView(control.ResultGrid.ItemsSource).IsEmpty, "开发环境搜索未恢复");
        control.ResultGrid.SelectedItem = _ui!.Items.First(); control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1500, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "development-center-preview.png"))) png.Save(stream);
        Check(((TextBlock)control.FindName("EvidenceText")).Text.Length > 20, "保护依据未显示");
        Console.WriteLine("PASS DEVELOPMENT UI: WPF rendering, selection evidence, virtualized grid, search");
    }
    public static Task RunStandaloneUiAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { var app = new App(); app.InitializeComponent(); var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "outputs")); Directory.CreateDirectory(output); RunUi(output); var main = new MainWindow(); Check(main.FindName("DevelopmentCenterView") is DevelopmentCenter && ((TabControl)main.FindName("MainTabControl")).Items.Count >= 8, "主窗口未集成第八页"); app.Shutdown(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static string Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(cwd); foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.WaitForExit(); Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new IOException("Fixture Git: " + error.Result); return output.Result.Trim();
    }
    private static void AddTask(string state, string id, string cwd, string rollout, bool archived)
    {
        using var db = new SqliteConnection("Data Source=" + state + ";Pooling=False"); db.Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO threads VALUES($id,$cwd,$rollout,$archived,0)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$cwd", cwd); command.Parameters.AddWithValue("$rollout", rollout); command.Parameters.AddWithValue("$archived", archived ? 1 : 0); command.ExecuteNonQuery();
    }
    private static void SetArchived(string state, string id, bool archived = true)
    { using var db = new SqliteConnection("Data Source=" + state + ";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE threads SET archived=$archived WHERE id=$id"; command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$archived", archived ? 1 : 0); command.ExecuteNonQuery(); }
}
