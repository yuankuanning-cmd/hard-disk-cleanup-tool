using System.Text;
using System.Text.Json;
using System.IO;

namespace ChatGPTCleanupTool.Services;

public sealed class CleanupLogService
{
    private readonly string _logPath;

    public CleanupLogService(string? logPath = null)
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleanupAssistant");
        _logPath = logPath ?? Path.Combine(appDataRoot, "cleanup-history.jsonl");
    }

    public string LogPath => _logPath;

    public async Task AppendAsync(
        CleanupResult result,
        CleanupScanOptions options,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        var entry = new CleanupLogEntry(
            DateTimeOffset.Now,
            options.OlderThanDays,
            options.ProjectRoot,
            result.DeletedCount,
            result.DeletedBytes,
            result.DeletedFiles,
            result.Failures);
        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        await File.AppendAllTextAsync(
            _logPath,
            line,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    public Task AppendOperationAsync(
        string operation,
        int itemCount,
        long reclaimedBytes,
        int failureCount,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        var entry = new OperationLogEntry(
            DateTimeOffset.Now,
            operation,
            itemCount,
            reclaimedBytes,
            failureCount,
            dryRun);
        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        return File.AppendAllTextAsync(
            _logPath,
            line,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    public async Task<IReadOnlyList<CleanupHistoryItem>> ReadHistoryAsync(
        int maximumEntries = 500,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_logPath))
        {
            return [];
        }

        var items = new List<CleanupHistoryItem>();
        foreach (var line in await File.ReadAllLinesAsync(_logPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var timestamp = root.TryGetProperty("Timestamp", out var timestampValue) &&
                                timestampValue.TryGetDateTimeOffset(out var parsedTimestamp)
                    ? parsedTimestamp
                    : DateTimeOffset.MinValue;
                var operation = root.TryGetProperty("Operation", out var operationValue)
                    ? operationValue.GetString() ?? "清理"
                    : "快速清理";
                var itemCount = ReadInt(root, "ItemCount", "DeletedCount");
                var bytes = ReadLong(root, "ReclaimedBytes", "DeletedBytes");
                var failureCount = root.TryGetProperty("FailureCount", out var failureValue)
                    ? failureValue.GetInt32()
                    : root.TryGetProperty("Failures", out var failures) &&
                      failures.ValueKind == JsonValueKind.Array
                        ? failures.GetArrayLength()
                        : 0;
                var dryRun = root.TryGetProperty("DryRun", out var dryRunValue) && dryRunValue.GetBoolean();
                items.Add(new CleanupHistoryItem(
                    timestamp,
                    operation,
                    itemCount,
                    bytes,
                    CleanupService.FormatBytes(bytes),
                    failureCount,
                    dryRun ? "模拟执行" : failureCount == 0 ? "成功" : "部分完成"));
            }
            catch (JsonException)
            {
                // 单行损坏不影响其余历史记录。
            }
        }

        return items
            .OrderByDescending(item => item.Timestamp)
            .Take(Math.Max(1, maximumEntries))
            .ToArray();
    }

    private static int ReadInt(JsonElement element, string primaryName, string fallbackName)
    {
        if (element.TryGetProperty(primaryName, out var primary) && primary.TryGetInt32(out var value))
        {
            return value;
        }

        return element.TryGetProperty(fallbackName, out var fallback) && fallback.TryGetInt32(out value)
            ? value
            : 0;
    }

    private static long ReadLong(JsonElement element, string primaryName, string fallbackName)
    {
        if (element.TryGetProperty(primaryName, out var primary) && primary.TryGetInt64(out var value))
        {
            return value;
        }

        return element.TryGetProperty(fallbackName, out var fallback) && fallback.TryGetInt64(out value)
            ? value
            : 0;
    }

    private sealed record CleanupLogEntry(
        DateTimeOffset Timestamp,
        int OlderThanDays,
        string? ProjectRoot,
        int DeletedCount,
        long DeletedBytes,
        IReadOnlyList<DeletedFile> DeletedFiles,
        IReadOnlyList<CleanupFailure> Failures);

    private sealed record OperationLogEntry(
        DateTimeOffset Timestamp,
        string Operation,
        int ItemCount,
        long ReclaimedBytes,
        int FailureCount,
        bool DryRun);
}

public sealed record CleanupHistoryItem(
    DateTimeOffset Timestamp,
    string Operation,
    int ItemCount,
    long ReclaimedBytes,
    string ReclaimedSize,
    int FailureCount,
    string Status);
