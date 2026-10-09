using System.IO;
using System.Text.Json;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

public sealed class DuplicateOperationLogService
{
    public string Path { get; }
    public DuplicateOperationLogService(string? path = null) => Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanupAssistant", "duplicate-actions.sqlite");
    internal Session OpenSession()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var connection = SqliteScanIndex.Open(Path, false);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,timestamp INTEGER NOT NULL,record TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_operation_time ON operations(timestamp DESC);";
            command.ExecuteNonQuery(); return new(connection);
        }
        catch { connection.Dispose(); throw; }
    }
    public Task<IReadOnlyList<DuplicateOperationRecord>> ReadRecentAsync(int maximum = 500) => Task.Run<IReadOnlyList<DuplicateOperationRecord>>(() =>
    {
        if (!File.Exists(Path)) return [];
        using var connection = SqliteScanIndex.Open(Path, true); using var command = connection.CreateCommand();
        command.CommandText = "SELECT record FROM operations ORDER BY timestamp DESC LIMIT $count"; command.Parameters.AddWithValue("$count", Math.Clamp(maximum, 1, 5000));
        using var reader = command.ExecuteReader(); var records = new List<DuplicateOperationRecord>();
        while (reader.Read()) records.Add(JsonSerializer.Deserialize<DuplicateOperationRecord>(reader.GetString(0))!);
        return records;
    });
    internal sealed class Session(SqliteConnection connection) : IDisposable
    {
        public void Save(DuplicateOperationRecord record)
        {
            using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO operations VALUES($id,$time,$record) ON CONFLICT(id) DO UPDATE SET timestamp=excluded.timestamp,record=excluded.record";
            command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$time", record.Timestamp.UtcTicks); command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(record)); command.ExecuteNonQuery();
        }
        public void Dispose() => connection.Dispose();
    }
}
