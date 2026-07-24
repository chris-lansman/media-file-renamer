using System.IO;

namespace MediaFileRenamer.App.Services;

internal sealed class AppDataPaths
{
    private static readonly object ConfigurationLock = new();
    private static AppDataPaths _current = CreateDefault();
    private static bool _isConsumed;
    private static bool _isConfigured;

    private AppDataPaths(string rootDirectory, bool isCustom)
    {
        RootDirectory = rootDirectory;
        IsCustom = isCustom;
    }

    public string RootDirectory { get; }
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public string OperationDirectory => Path.Combine(RootDirectory, "operations");
    public string LogDirectory => Path.Combine(RootDirectory, "Logs");
    public bool IsCustom { get; }

    public static AppDataPaths Current
    {
        get
        {
            lock (ConfigurationLock)
            {
                _isConsumed = true;
                return _current;
            }
        }
    }

    public static AppDataPaths CreateForDataRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException(
                "The --data-root value cannot be empty.",
                nameof(dataRoot));
        }

        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException(
                "The --data-root value must be an absolute path.",
                nameof(dataRoot));
        }

        var fullPath = Path.GetFullPath(dataRoot);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot)
            || string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The --data-root value must not be a drive or share root.",
                nameof(dataRoot));
        }

        if (File.Exists(fullPath))
        {
            throw new ArgumentException(
                "The --data-root value names an existing file, not a directory.",
                nameof(dataRoot));
        }

        return new AppDataPaths(fullPath, isCustom: true);
    }

    public static void Configure(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (ConfigurationLock)
        {
            if (_isConsumed)
            {
                throw new InvalidOperationException(
                    "Application data paths were already used and cannot be reconfigured.");
            }

            if (_isConfigured)
            {
                throw new InvalidOperationException(
                    "Application data paths were already configured.");
            }

            _current = paths;
            _isConfigured = true;
        }
    }

    private static AppDataPaths CreateDefault()
    {
        return new AppDataPaths(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaFileRenamer"),
            isCustom: false);
    }
}
