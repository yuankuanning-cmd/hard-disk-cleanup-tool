using System.IO;
using System.Text.Json;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

internal sealed class SoftwareCatalog : IDisposable
{
    private readonly SqliteConnection _db;
    internal SoftwareCatalog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); _db.Open();
        using var command = _db.CreateCommand(); command.CommandText = "PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS inventory(id TEXT PRIMARY KEY,source TEXT,record TEXT,absent INTEGER,seen TEXT); CREATE TABLE IF NOT EXISTS operations(id INTEGER PRIMARY KEY,record TEXT)"; command.ExecuteNonQuery();
    }
    internal IReadOnlyList<SoftwareHistory> Update(SoftwareSnapshot snapshot)
    {
        using var transaction = _db.BeginTransaction();
        foreach (var source in snapshot.CompleteSources)
        { using var command = _db.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE inventory SET absent=absent+1 WHERE source=$source"; command.Parameters.AddWithValue("$source", source); command.ExecuteNonQuery(); }
        foreach (var entry in snapshot.Entries)
        {
            using var command = _db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO inventory VALUES($id,$source,$record,0,$seen) ON CONFLICT(id) DO UPDATE SET source=excluded.source,record=excluded.record,absent=0,seen=excluded.seen";
            command.Parameters.AddWithValue("$id", entry.Id); command.Parameters.AddWithValue("$source", entry.Source); command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(entry)); command.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
        }
        transaction.Commit();
        using var read = _db.CreateCommand(); read.CommandText = "SELECT record,absent,seen FROM inventory WHERE absent>0 LIMIT 3000";
        var result = new List<SoftwareHistory>(); using var reader = read.ExecuteReader(); while (reader.Read()) if (JsonSerializer.Deserialize<SoftwareRegistration>(reader.GetString(0)) is { } entry) result.Add(new(entry, reader.GetInt32(1), DateTime.Parse(reader.GetString(2)).ToUniversalTime())); return result;
    }
    internal void Record(SoftwareOperation record)
    { using var command = _db.CreateCommand(); command.CommandText = "INSERT INTO operations(record) VALUES($record)"; command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(record)); command.ExecuteNonQuery(); }
    internal IReadOnlyList<SoftwareOperation> ReadRecords()
    { using var command = _db.CreateCommand(); command.CommandText = "SELECT record FROM operations ORDER BY id DESC LIMIT 300"; var result = new List<SoftwareOperation>(); using var reader = command.ExecuteReader(); while (reader.Read()) if (JsonSerializer.Deserialize<SoftwareOperation>(reader.GetString(0)) is { } row) result.Add(row); return result; }
    public void Dispose() => _db.Dispose();
}
