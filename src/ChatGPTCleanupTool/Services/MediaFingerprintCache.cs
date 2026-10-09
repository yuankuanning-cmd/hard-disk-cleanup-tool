using System.IO;
using System.Text.Json;
using ChatGPTCleanupTool.Models;
using Microsoft.Data.Sqlite;

namespace ChatGPTCleanupTool.Services;

/// <summary>Separate, versioned cache. Only successful fingerprints are stored, never previews or full media.</summary>
public sealed class MediaFingerprintCache : IDisposable
{
    internal const int AlgorithmVersion = 1;
    private readonly SqliteConnection _db;
    public MediaFingerprintCache(string? path = null)
    {
        path ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleanupAssistant", "media-fingerprints.sqlite");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _db.Open();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE IF NOT EXISTS fingerprints (path TEXT PRIMARY KEY COLLATE NOCASE, stamp TEXT NOT NULL, version INTEGER NOT NULL, data TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }
        catch { _db.Dispose(); throw; }
    }
    public MediaFingerprint? Get(string path, string stamp)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT data FROM fingerprints WHERE path=$p AND stamp=$s AND version=$v";
        cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$s", stamp);
        cmd.Parameters.AddWithValue("$v", AlgorithmVersion);
        var json = cmd.ExecuteScalar() as string;
        if (json is null) return null;
        try
        {
            var item = JsonSerializer.Deserialize<MediaFingerprint>(json);
            if (item is null || !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) || !Enum.IsDefined(item.Kind) ||
                item.Colors is null || item.Texture is null || item.VisualHashes is null || item.AudioHashes is null || item.SampleTimes is null ||
                item.Privacy is null || item.MetadataSummary is null || item.MetadataDigest is null || !double.IsFinite(item.Duration)) return null;
            if (item.Kind == MediaKind.Audio)
                return item.AudioHashes.Length is >= 28 and <= 480 && double.IsFinite(item.AudioEnergy) ? item : null;
            int samples = item.Kind == MediaKind.Image ? 1 : 5;
            return item.Width > 0 && item.Height > 0 && item.VisualHashes.Length == samples * 24 && item.Colors.Length == samples * 12 &&
                item.Texture.Length == samples && item.Colors.All(double.IsFinite) && item.Texture.All(double.IsFinite) ? item : null;
        }
        catch (JsonException) { return null; }
    }
    public void Put(MediaFingerprint item, string stamp)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO fingerprints VALUES ($p,$s,$v,$d) ON CONFLICT(path) DO UPDATE SET stamp=excluded.stamp,version=excluded.version,data=excluded.data";
        cmd.Parameters.AddWithValue("$p", item.Path); cmd.Parameters.AddWithValue("$s", stamp);
        cmd.Parameters.AddWithValue("$v", AlgorithmVersion);
        cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(item)); cmd.ExecuteNonQuery();
    }
    public void Dispose() => _db.Dispose();
    public void Clear()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "DELETE FROM fingerprints; VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }
}
