using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

internal sealed record DevelopmentTask(string Id, string Cwd, string Rollout, bool Archived, bool Pinned);

internal sealed class DevelopmentCodexCatalog
{
    internal List<DevelopmentTask> Tasks { get; } = [];
    internal List<string> ManagedTrees { get; } = [];
    internal HashSet<string> Referenced { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool Complete { get; private set; } = true;
    internal string Warning { get; private set; } = "";

    internal static DevelopmentCodexCatalog Load(string home, IReadOnlyList<string> targets, CancellationToken token)
    {
        var catalog = new DevelopmentCodexCatalog();
        if (!Directory.Exists(home)) { catalog.Fail("Codex 目录不存在，任务归属无法确认。"); return catalog; }
        try
        {
            var state = Directory.EnumerateFiles(home, "state_*.sqlite", SearchOption.TopDirectoryOnly)
                .Select(p => (Path: p, Version: int.TryParse(Path.GetFileNameWithoutExtension(p)[6..], out var v) ? v : -1))
                .OrderByDescending(x => x.Version).FirstOrDefault().Path;
            if (state is null) { catalog.Fail("未找到 Codex 任务目录索引；不按年龄推断归档或失效。"); return catalog; }
            if (DiskAnalysisService.HasReparsePointAncestor(state)) throw new IOException("任务索引路径包含链接。");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = state, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3 }.ToString());
            db.Open(); using var transaction = db.BeginTransaction(deferred: true);
            using var schema = db.CreateCommand(); schema.Transaction = transaction; schema.CommandText = "PRAGMA table_info(threads)";
            var columns = new HashSet<string>(); using (var reader = schema.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
            if (!new[] { "id", "cwd", "rollout_path", "archived" }.All(columns.Contains)) throw new IOException("不支持当前任务索引格式。");
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT id,cwd,rollout_path,archived," + (columns.Contains("is_pinned") ? "is_pinned" : "0") + " FROM threads LIMIT 10001";
            using (var reader = command.ExecuteReader()) while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (catalog.Tasks.Count == 10000) { catalog.Fail("任务数超过安全上限。"); break; }
                catalog.Tasks.Add(new(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt32(4) != 0));
            }
            using var tables = db.CreateCommand(); tables.Transaction = transaction;
            tables.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='thread_attachments'";
            if (Convert.ToInt64(tables.ExecuteScalar()) != 0)
            {
                using var attachments = db.CreateCommand(); attachments.Transaction = transaction;
                attachments.CommandText = "SELECT attachment_type,payload FROM thread_attachments LIMIT 10001";
                using var reader = attachments.ExecuteReader(); int count = 0;
                while (reader.Read())
                {
                    if (++count > 10000) { catalog.Fail("任务附件过多，托管工作树关系未读完整。"); break; }
                    if (!reader.GetString(0).Contains("worktree", StringComparison.OrdinalIgnoreCase)) continue;
                    try { using var json = JsonDocument.Parse(reader.GetString(1)); CollectPaths(json.RootElement, catalog.ManagedTrees); }
                    catch (JsonException) { catalog.Fail("工作树附件不可读取。"); }
                }
            }
            transaction.Commit();
            // Only test for references to the requested cleanup targets. Never retain message content.
            // An archived task may still contain a durable artifact, so archive status alone grants no deletion permission.
            long bytes = 0;
            var matcher = new ReferenceMatcher(targets, catalog.Referenced);
            foreach (var task in catalog.Tasks.Where(t => !t.Archived || t.Pinned))
            {
                foreach (var target in targets)
                    if (Overlap(target, task.Cwd)) catalog.Referenced.Add(target);
                if (targets.Count == 0) continue;
                if (!File.Exists(task.Rollout)) { catalog.Fail("未归档任务的引用记录缺失，相关清理保持关闭。"); continue; }
                var info = new FileInfo(task.Rollout); long originalLength = info.Length; var originalWrite = info.LastWriteTimeUtc; bytes += info.Length;
                if (bytes > 128 * 1024 * 1024 || info.Length > 32 * 1024 * 1024 || DiskAnalysisService.HasReparsePointAncestor(task.Rollout))
                { catalog.Fail("任务引用检查达到上限；相关清理保持关闭。"); continue; }
                using var stream = File.OpenRead(task.Rollout); using var reader = new StreamReader(stream);
                // One-pass multi-pattern matching: cost is linear in transcript size, not targets × transcript size.
                var buffer = new char[32 * 1024]; int n; matcher.Reset();
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested(); matcher.Feed(buffer.AsSpan(0, n));
                }
                info.Refresh(); if (info.Length != originalLength || info.LastWriteTimeUtc != originalWrite) catalog.Fail("任务引用记录在读取时发生变化。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        { catalog.Fail("Codex 任务索引不可读取：" + ex.Message); }
        return catalog;
    }

    private void Fail(string reason) { Complete = false; if (Warning.Length < 1500) Warning += reason + " "; }
    internal DevelopmentTask[] Owners(string path) => Tasks.Where(t => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(t.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    internal bool IsReferenced(string path) => Referenced.Contains(path) || Tasks.Any(t => (!t.Archived || t.Pinned) && Overlap(path, t.Cwd));
    internal bool IsManaged(string path) => ManagedTrees.Any(p => Overlap(path, p));
    internal static bool Overlap(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || !Path.IsPathFullyQualified(b)) return false;
        try { return DiskAnalysisService.IsWithin(a, b) || DiskAnalysisService.IsWithin(b, a); } catch { return true; }
    }
    private static void CollectPaths(JsonElement element, List<string> paths)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value && Path.IsPathFullyQualified(value)) paths.Add(DiskAnalysisService.NormalizePath(value));
        if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) CollectPaths(property.Value, paths);
        if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CollectPaths(child, paths);
    }

    private sealed class ReferenceMatcher
    {
        private sealed class Node { internal readonly Dictionary<char, int> Next = []; internal int Fail; internal readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase); }
        private readonly List<Node> _nodes = [new()]; private readonly HashSet<string> _matches; private int _state;
        internal ReferenceMatcher(IReadOnlyList<string> paths, HashSet<string> matches)
        {
            _matches = matches;
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var form in new[] { path, path.Replace('\\', '/'), path.Replace("\\", "\\\\") }.Distinct())
                {
                    int node = 0;
                    foreach (var raw in form)
                    { char c = char.ToUpperInvariant(raw); if (!_nodes[node].Next.TryGetValue(c, out var child)) { child = _nodes.Count; _nodes[node].Next[c] = child; _nodes.Add(new()); } node = child; }
                    _nodes[node].Paths.Add(path);
                }
            var queue = new Queue<int>(); foreach (var child in _nodes[0].Next.Values) queue.Enqueue(child);
            while (queue.Count > 0)
            {
                int parent = queue.Dequeue();
                foreach (var (c, child) in _nodes[parent].Next)
                {
                    int failure = _nodes[parent].Fail;
                    while (failure != 0 && !_nodes[failure].Next.ContainsKey(c)) failure = _nodes[failure].Fail;
                    if (_nodes[failure].Next.TryGetValue(c, out var link) && link != child) _nodes[child].Fail = link;
                    _nodes[child].Paths.UnionWith(_nodes[_nodes[child].Fail].Paths); queue.Enqueue(child);
                }
            }
        }
        internal void Reset() => _state = 0;
        internal void Feed(ReadOnlySpan<char> text)
        {
            foreach (var raw in text)
            {
                char c = char.ToUpperInvariant(raw);
                while (_state != 0 && !_nodes[_state].Next.ContainsKey(c)) _state = _nodes[_state].Fail;
                _state = _nodes[_state].Next.TryGetValue(c, out var next) ? next : 0;
                foreach (var path in _nodes[_state].Paths) _matches.Add(path);
            }
        }
    }
}
