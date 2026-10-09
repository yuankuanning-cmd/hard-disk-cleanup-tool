using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGPTCleanupTool.Controls;
using ChatGPTCleanupTool.Models;
using ChatGPTCleanupTool.Services;

internal static class DuplicateCenterTests
{
    public static async Task RunAsync(string testRoot, bool recycle)
    {
        var root = Path.Combine(testRoot, "duplicate-center"); Directory.CreateDirectory(root);
        var refs = new ReferenceDirectoryService(Path.Combine(root, "references.json"));
        var log = new DuplicateOperationLogService(Path.Combine(root, "audit.sqlite"));
        var service = new DuplicateFileService(new FileHashCacheService(Path.Combine(root, "hash.json")), refs, log);
        var work = Path.Combine(root, "work"); var backup = Path.Combine(root, "backup-2026"); var originals = Path.Combine(root, "originals");
        var a = Write(Path.Combine(work, "a.bin")); var b = Write(Path.Combine(backup, "long-name-copy.bin")); var o = Write(Path.Combine(originals, "original.bin"));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(-2)); File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(-5));
        var result = await service.AnalyzeAsync(root, 1); var files = result.Files;
        Check(files.Count == 3 && files.All(f => f.IsCrossDirectory && f.HasBackupWorkOverlap), "跨目录与备份推断失败");
        DuplicateDecisionService.Apply(files, DuplicateKeepPolicy.Oldest); Check(!files.Single(f => f.FullPath == b).IsSelected, "最旧保留失败");
        DuplicateDecisionService.Apply(files, DuplicateKeepPolicy.Newest); Check(!files.Single(f => f.FullPath == o).IsSelected, "最新保留失败");
        DuplicateDecisionService.Apply(files, DuplicateKeepPolicy.ShortestPath); Check(!files.OrderBy(f => f.FullPath.Length).First().IsSelected, "最短路径策略失败");
        DuplicateDecisionService.SelectDirectory(files, root, true); Check(files.Count(f => !f.IsSelected) == 1, "按目录批量选择必须保留一份");
        var allSelected = await service.ApplyActionAsync(files, files, new(DuplicateAction.Recycle, true));
        Check(allSelected.DeletedCount == 0 && allSelected.Failures.Count == 3, "业务层必须拒绝全组删除");
        refs.Add(originals);
        // The explicit original directory participates even when outside the whitelist.
        var withOriginal = await service.AnalyzeAsync(work, 1, true, [work, backup]);
        Check(withOriginal.Files.Count == 3 && withOriginal.Files.Single(f => f.FullPath == o).IsReference, "白名单之外原件未参与比较");
        DuplicateDecisionService.Apply(withOriginal.Files, DuplicateKeepPolicy.Oldest);
        Check(!withOriginal.Files.Single(f => f.FullPath == o).IsSelected && withOriginal.Files.Count(f => f.IsSelected) == 2, "参考目录必须优先保护");
        var originalAttempt = await service.ApplyActionAsync(withOriginal.Files, [withOriginal.Files.Single(f => f.FullPath == o)], new(DuplicateAction.Recycle, true));
        Check(originalAttempt.DeletedCount == 0 && File.Exists(o), "绕过界面的原件操作未被拒绝");
        var originalLinkAttempt = await service.ApplyActionAsync(withOriginal.Files, withOriginal.Files.Where(f => f.IsSelected), new(DuplicateAction.HardLink, true, true));
        Check(originalLinkAttempt.DeletedCount == 0, "参考原件不得作为硬链接来源");
        var verified = await service.ApplyActionAsync(withOriginal.Files, withOriginal.Files.Where(f => f.IsSelected), new(DuplicateAction.Recycle, true));
        Check(verified.DeletedCount == 2 && verified.ReclaimedAllocatedBytes == 0 && verified.TheoreticalBytes > 0, "模拟/回收站不能虚报已释放空间");
        Check((await log.ReadRecentAsync()).Count >= 8, "逐项 SQLite 操作日志未保存");
        var alias = Path.Combine(work, "alias.bin"); new NativeDuplicateLinkOperations().Create(alias, o);
        var aliased = await service.AnalyzeAsync(root, 1, true);
        Check(aliased.Files.Count == 3 && aliased.Files.Any(f => f.FullPath == o && f.IsReference && f.LinkCount == 2), "原件别名应物理去重并优先显示受保护原件");
        File.WriteAllText(Path.Combine(root, "references.json"), "not valid JSON");
        await RejectAsync(() => service.ApplyActionAsync(withOriginal.Files, [withOriginal.Files.First(f => !f.IsReference)], new(DuplicateAction.Recycle, true)), "损坏原件配置应停止整个操作");
        File.WriteAllText(Path.Combine(root, "references.json"), JsonSerializer.Serialize(new[] { originals }));
        Check(DuplicateMutationService.HasReferenceAlias(alias, [originals]), "应识别未显示在组内的参考目录别名");
        var excluded = new ExclusionService(Path.Combine(root, "exclusions.json")); excluded.SetReferenceRoots([originals]);
        Check(excluded.Contains(o) && excluded.GetPaths().Contains(originals), "原件保护未同步到其他清理页排除规则");
        excluded.SetReferenceReadFailed(); Check(excluded.Contains(a), "原件配置异常应禁止其他页面清理");
        excluded.SetReferenceRoots([]); Check(!excluded.Contains(o), "解除参考保护不应留下虚假永久排除项");
        var identityCache = new FileHashCacheService(Path.Combine(root, "identity-cache.json"));
        identityCache.StoreFull(a, 300_000, DateTime.UnixEpoch, "old-hash", "identity-one");
        Check(!identityCache.TryGetFull(a, 300_000, DateTime.UnixEpoch, out _, "identity-two"), "同名/同时间替换的文件必须使哈希缓存失效");

        var changed = NewService(root, "changed", out _, out _); var changeRoot = Path.Combine(root, "changed");
        var c1 = Write(Path.Combine(changeRoot, "one.bin")); var c2 = Write(Path.Combine(changeRoot, "two.bin"));
        var changeScan = await changed.AnalyzeAsync(changeRoot, 1);
        var keeper = changeScan.Files.Single(f => f.FullPath == c1); var target = changeScan.Files.Single(f => f.FullPath == c2);
        // Same size and timestamp: only a new complete hash detects this.
        var time = File.GetLastWriteTimeUtc(c1); using (var write = File.OpenWrite(c1)) { write.Position = 100; write.WriteByte(77); } File.SetLastWriteTimeUtc(c1, time);
        var changedKeeper = await changed.ApplyActionAsync(changeScan.Files, [target], new(DuplicateAction.Recycle, true));
        Check(changedKeeper.DeletedCount == 0 && File.Exists(c2), "保留副本变更必须拒绝，即使目标未改变");

        var collision = NewService(root, "collision", out _, out _); var collisionRoot = Path.Combine(root, "collision");
        Write(Path.Combine(collisionRoot, "a.bin")); var different = Write(Path.Combine(collisionRoot, "b.bin"));
        using (var write = File.OpenWrite(different)) { write.Position = 150_000; write.WriteByte(99); }
        var sampleScan = await collision.AnalyzeAsync(collisionRoot, 1, true);
        Check(sampleScan.Files.Count == 2, "测试应制造相同首尾采样、不同中间内容");
        var collisionReject = await collision.ApplyActionAsync(sampleScan.Files, [sampleScan.Files.First()], new(DuplicateAction.Recycle, true));
        Check(collisionReject.DeletedCount == 0, "采样误匹配在操作前必须被完整哈希拒绝");
        var fullScan = await collision.AnalyzeAsync(collisionRoot, 1, false); Check(fullScan.Files.Count == 0, "完整扫描不得确认采样误匹配");

        var hard = NewService(root, "hard", out var hardRefs, out var hardLog); var hardRoot = Path.Combine(root, "hard");
        var h1 = Write(Path.Combine(hardRoot, "one.bin")); var h2 = Write(Path.Combine(hardRoot, "two.bin"));
        var hardScan = await hard.AnalyzeAsync(hardRoot, 1);
        var noConsent = await hard.ApplyActionAsync(hardScan.Files, [hardScan.Files.Single(f => f.FullPath == h2)], new(DuplicateAction.HardLink));
        Check(noConsent.DeletedCount == 0, "未显式确认硬链接风险不能操作");
        var hardResult = await hard.ApplyActionAsync(hardScan.Files, [hardScan.Files.Single(f => f.FullPath == h2)], new(DuplicateAction.HardLink, false, true));
        Check(hardResult.DeletedCount == 1, "真实硬链接替换失败：" + string.Join(";", hardResult.Failures));
        Check(WindowsFileStorageService.GetFileIdentity(h1) == WindowsFileStorageService.GetFileIdentity(h2) && hardResult.ReclaimedAllocatedBytes > 0, "硬链接身份/已释放占用错误");
        Check(!Directory.EnumerateFiles(hardRoot, ".diskcleanup-*.tmp").Any(), "成功后不应遗留恢复文件");
        Check((await hard.AnalyzeAsync(hardRoot, 1, true)).Files.Count == 0, "硬链接别名不应再次列为重复独立文件");

        var multi = NewService(root, "multi", out _, out _); var multiRoot = Path.Combine(root, "multi");
        var m1 = Write(Path.Combine(multiRoot, "one.bin")); var m2 = Write(Path.Combine(multiRoot, "two.bin"));
        var remainingAlias = Path.Combine(multiRoot, "zz-existing-alias.bin"); new NativeDuplicateLinkOperations().Create(remainingAlias, m2);
        var multiScan = await multi.AnalyzeAsync(multiRoot, 1); var multiTarget = multiScan.Files.Single(f => f.FullPath == m2);
        Check(multiTarget.LinkCount == 2 && multiTarget.TheoreticalReclaimableBytes == 0, "已有外部硬链接的单项理论值应保守为零");
        var multiResult = await multi.ApplyActionAsync(multiScan.Files, [multiTarget], new(DuplicateAction.HardLink, false, true));
        Check(multiResult.DeletedCount == 1 && multiResult.ReclaimedAllocatedBytes == 0 && WindowsFileStorageService.GetFileIdentity(remainingAlias) != WindowsFileStorageService.GetFileIdentity(m1), "已有其他文件名时替换不能虚报释放");

        var rollbackService = NewService(root, "rollback", out var rollbackRefs, out var rollbackLog); var rollbackRoot = Path.Combine(root, "rollback");
        var r1 = Write(Path.Combine(rollbackRoot, "one.bin")); var r2 = Write(Path.Combine(rollbackRoot, "two.bin"));
        var rollbackScan = await rollbackService.AnalyzeAsync(rollbackRoot, 1); var oldId = WindowsFileStorageService.GetFileIdentity(r2);
        var rollback = await new DuplicateMutationService(rollbackRefs, rollbackLog, new FailBackupDelete()).ExecuteAsync(rollbackScan.Files,
            [rollbackScan.Files.Single(f => f.FullPath == r2)], new(DuplicateAction.HardLink, false, true));
        Check(rollback.DeletedCount == 0 && WindowsFileStorageService.GetFileIdentity(r2) == oldId && File.ReadAllBytes(r1).SequenceEqual(File.ReadAllBytes(r2)), "替换后的故障应回滚原目标");
        var recovery = await new DuplicateMutationService(rollbackRefs, rollbackLog, new FailNewLink()).ExecuteAsync(rollbackScan.Files,
            [rollbackScan.Files.Single(f => f.FullPath == r2)], new(DuplicateAction.HardLink, false, true));
        Check(recovery.DeletedCount == 0 && File.Exists(recovery.Records.Single().BackupPath) && recovery.Records.Single().Status.Contains("恢复"), "替换准备失败应保留并记录恢复副本");

        var adsService = NewService(root, "ads", out _, out _); var adsRoot = Path.Combine(root, "ads");
        Write(Path.Combine(adsRoot, "one.bin")); var adsTarget = Write(Path.Combine(adsRoot, "two.bin"));
        File.WriteAllText(adsTarget + ":extra", "keep this stream");
        var adsScan = await adsService.AnalyzeAsync(adsRoot, 1);
        var adsReject = await adsService.ApplyActionAsync(adsScan.Files, [adsScan.Files.Single(f => f.FullPath == adsTarget)], new(DuplicateAction.HardLink, false, true));
        Check(adsReject.DeletedCount == 0 && File.ReadAllText(adsTarget + ":extra") == "keep this stream", "额外数据流必须保留并拒绝替换");
        using (var held = new FileStream(adsTarget, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var sharingReject = await adsService.ApplyActionAsync(adsScan.Files, [adsScan.Files.Single(f => f.FullPath == adsTarget)], new(DuplicateAction.Recycle, true));
            Check(sharingReject.DeletedCount == 0, "其他程序正在读目标时应拒绝，避免虚报即时释放");
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var canceled = await hard.ApplyActionAsync(hardScan.Files, [hardScan.Files.Last()], new(DuplicateAction.Recycle, true), cancellationToken: cancellation.Token);
        Check(canceled.WasCanceled && canceled.DeletedCount == 0, "操作取消未正确返回部分结果");

        var imageRoot = Path.Combine(root, "images"); Directory.CreateDirectory(imageRoot);
        var imageA = Path.Combine(imageRoot, "one.png"); var imageB = Path.Combine(imageRoot, "two.png");
        var bitmap = BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 8 * 4], 16 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(imageA)) encoder.Save(stream); File.Copy(imageA, imageB);
        var imageService = NewService(root, "image-meta", out _, out _); var imageScan = await imageService.AnalyzeAsync(imageRoot, 1);
        await DuplicateDecisionService.ReadImageInformationAsync(imageScan.Files);
        Check(imageScan.Files.All(f => f.PixelWidth == 16 && f.PixelHeight == 8 && f.IsLosslessImage), "图片质量元数据错误");
        DuplicateDecisionService.Apply(imageScan.Files, DuplicateKeepPolicy.HighestResolution); Check(imageScan.Files.Count(f => !f.IsSelected) == 1, "分辨率策略应保留一份");
        DuplicateDecisionService.Apply(imageScan.Files, DuplicateKeepPolicy.HighestQuality); Check(imageScan.Files.Count(f => !f.IsSelected) == 1, "质量指标策略应保留一份");
        var preview = await DuplicatePreviewService.LoadGroupAsync([imageA, imageB]); Check(preview.All(p => p.Thumbnail is { IsFrozen: true }), "图片预览应冻结、关闭文件句柄");
        var text = Path.Combine(root, "preview.txt"); File.WriteAllText(text, new string('x', 20_000));
        var textPreview = await DuplicatePreviewService.LoadGroupAsync(Enumerable.Repeat(text, 20)); Check(textPreview.Count == 8 && textPreview[0].Text.Length == 4096, "预览应限制数量和读取长度");
        if (recycle)
        {
            var recycleService = NewService(root, "recycle", out _, out _); var recycleRoot = Path.Combine(root, "recycle");
            var keep = Write(Path.Combine(recycleRoot, "keep.bin")); var remove = Write(Path.Combine(recycleRoot, "remove.bin"));
            var scan = await recycleService.AnalyzeAsync(recycleRoot, 1);
            var removed = await recycleService.ApplyActionAsync(scan.Files, [scan.Files.Single(f => f.FullPath == remove)], new(DuplicateAction.Recycle));
            Check(removed.DeletedCount == 1 && removed.ReclaimedAllocatedBytes == 0 && !File.Exists(remove) && File.Exists(keep), "真实回收站操作/实际空间统计错误：" + string.Join(";", removed.Failures));
            Console.WriteLine("PASS: 重复中心真实回收站移入成功，保留副本存在，已释放显示 0。");
        }
        Console.WriteLine("PASS: 重复中心参考目录、别名、5 种策略、目录批选、双方完整重哈希、采样碰撞、真实 NTFS 硬链接、两类故障/回滚、ADS/打开句柄拒绝、持久日志、取消、图片与限量预览。");
    }
    public static void RunUi(string output)
    {
        var center = new DuplicateCenter(); var files = new BulkObservableCollection<DuplicateFileItem>();
        DuplicateFileItem Item(int group, string path) => new() { GroupNumber = group, Copies = 2, FullPath = path, LogicalBytes = 1024, AllocatedBytes = 4096, LogicalSize = "1 KiB", AllocatedSize = "4 KiB", Hash = "ABC", FileIdentity = path, IsFullHashVerified = true, LastWriteTime = DateTime.Now };
        files.Add(Item(1, @"C:\Demo\work\plan.txt")); files.Add(Item(1, @"C:\Demo\backup-2026\plan.txt")); files.Add(Item(2, @"C:\Demo\same\a.txt")); files.Add(Item(2, @"C:\Demo\same\b.txt"));
        var refs = new ReferenceDirectoryService(Path.Combine(output, "nonexistent-ui-references.json")); var log = new DuplicateOperationLogService(Path.Combine(output, "nonexistent-ui-audit.sqlite"));
        center.Configure(refs, log); center.SetFiles(files, []); center.OnlyCrossDirectory = true;
        Check(center.ResultGrid.Items.Count == 2, "界面跨目录筛选应保留整个组");
        center.SetSearchText("backup-2026"); Check(center.ResultGrid.Items.Count == 2, "全局搜索匹配任一副本时应保留整个组");
        center.OnlyBackupOverlap = true; Check(center.ResultGrid.Items.Count == 2, "备份/工作筛选错误");
        center.OnlyReferenceMatched = true; Check(center.ResultGrid.Items.Count == 0, "仅匹配原件筛选错误");
        center.OnlyReferenceMatched = false; center.SetSearchText(""); center.Measure(new Size(1220, 500)); center.Arrange(new Rect(0, 0, 1220, 500)); center.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1220, 500, 96, 96, PixelFormats.Pbgra32); bitmap.Render(center); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, "duplicate-center-preview.png")); encoder.Save(stream);
        Console.WriteLine("PASS: 重复中心 WPF 编译/渲染、跨目录/备份/原件筛选、按整组搜索。");
    }
    private sealed class FailBackupDelete : IDuplicateLinkOperations
    {
        private readonly NativeDuplicateLinkOperations native = new();
        public void Create(string name, string existing) => native.Create(name, existing);
        public void Replace(string replacement, string target) => native.Replace(replacement, target);
        public void Delete(string path) { if (Path.GetFileName(path).StartsWith(".diskcleanup-backup-")) throw new IOException("injected backup cleanup failure"); native.Delete(path); }
    }
    private sealed class FailNewLink : IDuplicateLinkOperations
    {
        private readonly NativeDuplicateLinkOperations native = new();
        public void Create(string name, string existing) { if (Path.GetFileName(name).StartsWith(".diskcleanup-link-")) throw new IOException("injected new link failure"); native.Create(name, existing); }
        public void Replace(string replacement, string target) => native.Replace(replacement, target);
        public void Delete(string path) => native.Delete(path);
    }
    private static DuplicateFileService NewService(string root, string name, out ReferenceDirectoryService refs, out DuplicateOperationLogService log)
    { var meta = Path.Combine(root, "metadata-" + name); refs = new(Path.Combine(meta, "references.json")); log = new(Path.Combine(meta, "audit.sqlite")); return new(new FileHashCacheService(Path.Combine(meta, "hash.json")), refs, log); }
    private static string Write(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[300_000]); return path; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task RejectAsync(Func<Task> action, string message) { try { await action(); } catch (Exception ex) when (ex is IOException or JsonException) { return; } throw new InvalidOperationException(message); }
}
