using System.IO;
using System.Text.Json;

namespace MediaFileRenamer.App.Services;

public sealed class AppSettings
{
    public string TmdbApiKey { get; set; } = "";
    public bool UseTmdbLookup { get; set; } = true;
    public string TvdbApiKey { get; set; } = "";
    public string TvdbPin { get; set; } = "";
    public bool UseTvdbFallback { get; set; } = true;
    public int AutoMatchConfidencePercent { get; set; } = 92;
    public string DefaultOutputFolder { get; set; } = GetDefaultOutputFolder();

    public static string GetDefaultOutputFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "renamed-media");
    }
}

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string SettingsPath { get; }

    public AppSettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaFileRenamer",
            "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return Normalize(JsonSerializer.Deserialize<AppSettings>(json));
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        settings.TmdbApiKey ??= "";
        settings.TvdbApiKey ??= "";
        settings.TvdbPin ??= "";
        settings.DefaultOutputFolder = string.IsNullOrWhiteSpace(settings.DefaultOutputFolder)
            ? AppSettings.GetDefaultOutputFolder()
            : settings.DefaultOutputFolder.Trim();
        settings.AutoMatchConfidencePercent = Math.Clamp(
            settings.AutoMatchConfidencePercent,
            80,
            100);
        return settings;
    }
}
