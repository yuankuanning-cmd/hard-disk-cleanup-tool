using System.IO;
using System.Text.Json;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

internal sealed class NpsProjectCatalog : IDisposable
{
    private readonly SqliteConnection _db;
    internal NpsProjectCatalog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS markers(path TEXT PRIMARY KEY COLLATE NOCASE,model INTEGER,record TEXT); CREATE TABLE IF NOT EXISTS state(id INTEGER PRIMARY KEY,record TEXT); CREATE TABLE IF NOT EXISTS operations(id INTEGER PRIMARY KEY,record TEXT);";
        command.ExecuteNonQuery();
    }
    internal void Start()
    { using var command = _db.CreateCommand(); command.CommandText = "DELETE FROM markers; DELETE FROM state;"; command.ExecuteNonQuery(); }
    internal void Add(IReadOnlyList<NpsMarker> markers)
    {
        using var transaction = _db.BeginTransaction();
        foreach (var marker in markers)
        {
            using var command = _db.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT OR REPLACE INTO markers VALUES($path,$model,$record)";
            command.Parameters.AddWithValue("$path", marker.Path); command.Parameters.AddWithValue("$model", marker.Model ? 1 : marker.Structure ? 2 : 0); command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(marker)); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    internal IReadOnlyList<NpsProjectItem> Resolve(int maximum, List<string> warnings)
    {
        var projects = new Dictionary<string, NpsProjectItem>(StringComparer.OrdinalIgnoreCase);
        using var command = _db.CreateCommand(); command.CommandText = "SELECT record FROM markers WHERE model=0 ORDER BY path";
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var marker = JsonSerializer.Deserialize<NpsMarker>(reader.GetString(0))!;
            var root = DiskAnalysisService.NormalizePath(Path.GetDirectoryName(marker.Path)!);
            if (projects.Count >= maximum) { warnings.Add("工程结果达到上限；不能据此确认全盘完整。"); break; }
            if (projects.ContainsKey(root)) { var prior = projects[root]; projects[root] = prior with { Recognized = false, Kind = NpsProjectKind.Suspected, Evidence = prior.Evidence + "；同目录多个工程清单，需人工核实" }; continue; }
            var proof = NpsProjectRecognizer.InspectRoot(root);
            bool confirmed = marker.Recognized && proof.Confirmed;
            projects[root] = new(root, Path.GetFileName(root), root, marker.Path, confirmed ? NpsProjectRecognizer.Classify(root) : NpsProjectKind.Suspected,
                confirmed, marker.Version, marker.Created, 0, proof.Description);
        }
        command.CommandText = "SELECT record FROM markers WHERE model=2 ORDER BY path";
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var marker = JsonSerializer.Deserialize<NpsMarker>(reader.GetString(0))!;
            var root = DiskAnalysisService.NormalizePath(marker.Path);
            if (projects.ContainsKey(root)) continue;
            if (projects.Count >= maximum) { warnings.Add("工程目录结果达到上限；部分结果。"); break; }
            var proof = NpsProjectRecognizer.InspectRoot(root);
            bool confirmed = marker.Recognized && proof.Confirmed;
            var kind = confirmed ? NpsProjectRecognizer.Classify(root) : NpsProjectKind.Suspected;
            if (kind == NpsProjectKind.Project && proof.ManifestPath.Length == 0) kind = NpsProjectKind.StructuredProject;
            projects[root] = new(root, Path.GetFileName(root), root, proof.ManifestPath, kind, confirmed, "", "", 0, proof.Description);
        }
        command.CommandText = "SELECT record FROM markers WHERE model=1 ORDER BY path";
        var standalone = new List<NpsProjectItem>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var marker = JsonSerializer.Deserialize<NpsMarker>(reader.GetString(0))!;
            string? owner = Path.GetDirectoryName(marker.Path);
            while (owner is not null && !projects.ContainsKey(owner)) owner = Path.GetDirectoryName(owner);
            if (owner is not null) { var project = projects[owner]; projects[owner] = project with { ModelFiles = project.ModelFiles + 1 }; }
            else if (projects.Count + standalone.Count < maximum)
                standalone.Add(new(marker.Path, Path.GetFileName(marker.Path), marker.Path, "", NpsProjectKind.StandaloneModel, marker.Recognized, "", "", 1,
                    (marker.Recognized ? "确认是已支持的NPS模型，但没有找到所属工程清单；" : "疑似模型；") + marker.Evidence + "；不可按父目录整体清理"));
            else { warnings.Add("独立模型结果达到上限；部分结果。"); break; }
        }
        return projects.Values.Concat(standalone).OrderBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal void Save(NpsScanResult result)
    { using var command = _db.CreateCommand(); command.CommandText = "INSERT OR REPLACE INTO state VALUES(1,$record)"; command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(result)); command.ExecuteNonQuery(); }
    internal NpsScanResult? Load()
    { using var command = _db.CreateCommand(); command.CommandText = "SELECT record FROM state WHERE id=1"; return command.ExecuteScalar() is string text ? JsonSerializer.Deserialize<NpsScanResult>(text) : null; }
    internal void Record(NpsOperation record)
    { using var command = _db.CreateCommand(); command.CommandText = "INSERT INTO operations(record) VALUES($record)"; command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(record)); command.ExecuteNonQuery(); }
    internal IReadOnlyList<NpsOperation> Records()
    { using var command = _db.CreateCommand(); command.CommandText = "SELECT record FROM operations ORDER BY id DESC LIMIT 500"; using var reader = command.ExecuteReader(); var list = new List<NpsOperation>(); while (reader.Read()) list.Add(JsonSerializer.Deserialize<NpsOperation>(reader.GetString(0))!); return list; }
    public void Dispose() => _db.Dispose();
}
