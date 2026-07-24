using System.IO;
using System.Text.Json;

namespace MediaFileRenamer.App.Services;

public sealed class AppSettings
{
    public string TmdbApiKey { get; set; } = "";
    public bool UseTmdbLookup { get; set; } = true;
    public string TvdbApiKey { get; set; } = "";
    public bool UseTvdbFallback { get; set; } = true;
    public int AutoMatchConfidencePercent { get; set; } = 92;
    public string DefaultOutputFolder { get; set; } = @"C:\Users\cclan\renamed-media";
}

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaFileRenamer",
        "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
