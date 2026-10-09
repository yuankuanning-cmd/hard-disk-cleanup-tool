using System.Text.Json;
using System.IO;

namespace ChatGPTCleanupTool.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;

    public AppSettingsService(string? settingsPath = null)
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleanupAssistant");
        _settingsPath = settingsPath ?? Path.Combine(appDataRoot, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                   ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}

public sealed record AppSettings
{
    public bool FastMode { get; init; } = true;
    public bool DryRun { get; init; }
    public string Days { get; init; } = "7";
    public string? ProjectPath { get; init; }
    public string? CodexRoot { get; init; }
    public string CodexMinimumMb { get; init; } = "1";
    public string CodexAgeDays { get; init; } = "30";
    public bool CodexDetectDuplicates { get; init; }
    public string? AnalysisPath { get; init; }
    public string AnalysisMinimumMb { get; init; } = "100";
    public string? DuplicatePath { get; init; }
    public string DuplicateMinimumMb { get; init; } = "10";
    public string ScanScopeWhitelist { get; init; } = string.Empty;
    public int SelectedTabIndex { get; init; }
    public int EngineMode { get; init; }
    public bool IndexFingerprints { get; init; }
    public int DuplicateKeepPolicy { get; init; }
    public bool DuplicateOnlyCrossDirectory { get; init; }
    public bool DuplicateOnlyBackupOverlap { get; init; }
    public bool DuplicateOnlyReferenceMatched { get; init; }
    public string? MediaPath { get; init; }
    public bool MediaImages { get; init; } = true;
    public bool MediaVideos { get; init; } = true;
    public bool MediaAudio { get; init; } = true;
    public int MediaSensitivity { get; init; } = 1;
    public string? DevelopmentPath { get; init; }
    public string? DevelopmentCodexHome { get; init; }
    public bool DevelopmentCodex { get; init; } = true;
    public bool DevelopmentCaches { get; init; }
    public bool DevelopmentDocker { get; init; }
    public bool DevelopmentCompare { get; init; } = true;
    public string DevelopmentMergeTarget { get; init; } = "";
    public bool SoftwareStore { get; init; } = true;
    public bool SoftwareGames { get; init; } = true;
    public bool SoftwareResidues { get; init; } = true;
    public bool SoftwareMeasure { get; init; }
}
