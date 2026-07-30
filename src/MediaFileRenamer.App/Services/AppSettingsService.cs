using System.IO;
using System.Text.Json;

namespace MediaFileRenamer.App.Services;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = AppSettingsService.CurrentSchemaVersion;
    public string TmdbApiKey { get; set; } = "";
    public bool UseTmdbLookup { get; set; } = true;
    public string TvdbApiKey { get; set; } = "";
    public string TvdbPin { get; set; } = "";
    public bool UseTvdbFallback { get; set; } = true;
    public int AutoMatchConfidencePercent { get; set; } = 92;
    public string DefaultOutputFolder { get; set; } = GetDefaultOutputFolder();
    public FileOperation DefaultOperation { get; set; } = FileOperation.Move;
    public List<SavedShowMapping> SavedShowMappings { get; set; } = [];

    public static string GetDefaultOutputFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "renamed-media");
    }
}

public sealed class SavedShowMapping
{
    public string SourcePath { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string Title { get; set; } = "";
    public string MediaType { get; set; } = "TV";
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public EpisodeOrder EpisodeOrder { get; set; } = EpisodeOrder.Default;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public SavedShowMapping Copy()
    {
        return new SavedShowMapping
        {
            SourcePath = SourcePath,
            SourceIdentity = SourceIdentity,
            Title = Title,
            MediaType = MediaType,
            TmdbId = TmdbId,
            TvdbId = TvdbId,
            EpisodeOrder = EpisodeOrder,
            UpdatedAtUtc = UpdatedAtUtc
        };
    }
}

public sealed class AppSettingsService
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new LenientJsonStringEnumConverter() }
    };
    private readonly object _sync = new();

    public string SettingsPath { get; }
    public bool SettingsExist => File.Exists(SettingsPath);

    public AppSettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? AppDataPaths.Current.SettingsPath;
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            return LoadUnsafe();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            SaveUnsafe(settings);
        }
    }

    public IReadOnlyList<SavedShowMapping> GetSavedShowMappings()
    {
        lock (_sync)
        {
            return LoadUnsafe().SavedShowMappings
                .Select(mapping => mapping.Copy())
                .ToList();
        }
    }

    public SavedShowMapping? FindShowMapping(string sourcePath)
    {
        var identity = NormalizeSourceIdentity(sourcePath);
        lock (_sync)
        {
            return LoadUnsafe().SavedShowMappings
                .FirstOrDefault(mapping => string.Equals(
                    mapping.SourceIdentity,
                    identity,
                    StringComparison.OrdinalIgnoreCase))
                ?.Copy();
        }
    }

    public SavedShowMapping UpsertShowMapping(SavedShowMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var normalizedMapping = NormalizeMapping(mapping)
            ?? throw new ArgumentException(
                "A saved show mapping requires an absolute source path, a title, and at least one provider ID.",
                nameof(mapping));

        lock (_sync)
        {
            var settings = LoadUnsafe();
            settings.SavedShowMappings.RemoveAll(candidate => string.Equals(
                candidate.SourceIdentity,
                normalizedMapping.SourceIdentity,
                StringComparison.OrdinalIgnoreCase));
            settings.SavedShowMappings.Add(normalizedMapping);
            SaveUnsafe(settings);
            return normalizedMapping.Copy();
        }
    }

    public bool RemoveShowMapping(string sourcePath)
    {
        var identity = NormalizeSourceIdentity(sourcePath);
        lock (_sync)
        {
            var settings = LoadUnsafe();
            var removed = settings.SavedShowMappings.RemoveAll(mapping => string.Equals(
                mapping.SourceIdentity,
                identity,
                StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                SaveUnsafe(settings);
            }

            return removed;
        }
    }

    public static string NormalizeSourceIdentity(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("The source path cannot be empty.", nameof(sourcePath));
        }

        var fullPath = Path.GetFullPath(sourcePath.Trim())
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(
            fullPath.TrimEnd(Path.DirectorySeparatorChar),
            root?.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private AppSettings LoadUnsafe()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return Normalize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions));
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SaveUnsafe(AppSettings settings)
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
        settings.SchemaVersion = CurrentSchemaVersion;
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
        settings.SavedShowMappings = (settings.SavedShowMappings ?? [])
            .Select(NormalizeMapping)
            .Where(mapping => mapping is not null)
            .Select(mapping => mapping!)
            .GroupBy(mapping => mapping.SourceIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(mapping => mapping.UpdatedAtUtc).First())
            .OrderBy(mapping => mapping.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mapping => mapping.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return settings;
    }

    private static SavedShowMapping? NormalizeMapping(SavedShowMapping? mapping)
    {
        if (mapping is null
            || string.IsNullOrWhiteSpace(mapping.SourcePath)
            || string.IsNullOrWhiteSpace(mapping.Title)
            || mapping.TmdbId is null && mapping.TvdbId is null)
        {
            return null;
        }

        mapping = mapping.Copy();
        string identity;
        try
        {
            identity = NormalizeSourceIdentity(mapping.SourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (mapping.TmdbId <= 0)
        {
            mapping.TmdbId = null;
        }

        if (mapping.TvdbId <= 0)
        {
            mapping.TvdbId = null;
        }

        if (mapping.TmdbId is null && mapping.TvdbId is null)
        {
            return null;
        }

        mapping.SourcePath = NormalizeSourceIdentity(mapping.SourcePath);
        mapping.SourceIdentity = identity;
        mapping.Title = mapping.Title.Trim();
        mapping.MediaType = string.Equals(mapping.MediaType, "Movie", StringComparison.OrdinalIgnoreCase)
            ? "Movie"
            : "TV";
        mapping.UpdatedAtUtc = mapping.UpdatedAtUtc == default
            ? DateTimeOffset.UtcNow
            : mapping.UpdatedAtUtc.ToUniversalTime();
        return mapping;
    }
}
