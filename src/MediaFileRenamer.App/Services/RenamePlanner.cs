using MediaFileRenamer.App.ViewModels;
using System.IO;
using System.Text;

namespace MediaFileRenamer.App.Services;

public enum RenamePreset
{
    PlexStandard,
    FlatReview,
    Custom
}

public sealed class RenamePlanner
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string BuildDestination(MediaPreviewItem item, string outputRoot, RenamePreset preset, string customFormat = "")
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || !Path.IsPathFullyQualified(outputRoot))
        {
            throw new ArgumentException("Choose a complete output folder path.", nameof(outputRoot));
        }

        var normalizedRoot = Path.GetFullPath(outputRoot.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var safeTitle = Sanitize(item.MatchedTitle);
        var extension = item.Extension;

        var relative = preset == RenamePreset.Custom
            ? BuildCustomPath(item, customFormat, extension)
            : item.MediaType == "TV"
            ? BuildTvPath(item, safeTitle, extension, preset)
            : BuildMoviePath(item, safeTitle, extension, preset);

        ValidateRelativePath(relative);
        var destination = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The custom format must stay inside the output folder.", nameof(customFormat));
        }

        return destination;
    }

    private static string BuildMoviePath(MediaPreviewItem item, string title, string extension, RenamePreset preset)
    {
        var year = item.Year is null ? "" : $" ({item.Year})";
        var idTag = BuildTmdbIdTag(item);
        var displayName = $"{title}{year}{idTag}";
        var fileName = $"{displayName}{extension}";

        return preset == RenamePreset.FlatReview
            ? Path.Combine("Movies", fileName)
            : Path.Combine("Movies", displayName, fileName);
    }

    private static string BuildTvPath(MediaPreviewItem item, string title, string extension, RenamePreset preset)
    {
        var season = item.Season ?? 0;
        var episode = item.Episode ?? 0;
        var episodeTitle = string.IsNullOrWhiteSpace(item.EpisodeTitle)
            ? ""
            : $" - {Sanitize(item.EpisodeTitle)}";
        var fileName = $"{title} - S{season:00}E{episode:00}{episodeTitle}{extension}";
        var showFolder = $"{title}{BuildYear(item)}{BuildTmdbIdTag(item)}";

        return preset == RenamePreset.FlatReview
            ? Path.Combine("TV Shows", fileName)
            : Path.Combine("TV Shows", showFolder, $"Season {season:00}", fileName);
    }

    private static string BuildCustomPath(MediaPreviewItem item, string pattern, string extension)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = item.MediaType == "TV"
                ? @"TV Shows\{Title}\Season {Season}\{Title} - S{Season}E{Episode} - {EpisodeTitle}"
                : @"Movies\{Title} ({Year})\{Title} ({Year})";
        }

        var relative = pattern
            .Replace("{Title}", Sanitize(item.MatchedTitle), StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", item.Year?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Season}", item.Season?.ToString("00") ?? "00", StringComparison.OrdinalIgnoreCase)
            .Replace("{Episode}", item.Episode?.ToString("00") ?? "00", StringComparison.OrdinalIgnoreCase)
            .Replace("{EpisodeTitle}", Sanitize(item.EpisodeTitle), StringComparison.OrdinalIgnoreCase)
            .Replace("{TmdbId}", BuildTmdbIdTag(item).TrimStart(), StringComparison.OrdinalIgnoreCase)
            .Trim();

        return relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? relative
            : relative + extension;
    }

    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
        {
            throw new ArgumentException("The custom format must be a relative path.");
        }

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The custom format cannot contain '.' or '..' path segments.");
        }

        if (segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("The custom format contains characters Windows cannot use in a file or folder name.");
        }
    }

    private static string BuildYear(MediaPreviewItem item)
    {
        return item.Year is null ? "" : $" ({item.Year})";
    }

    private static string BuildTmdbIdTag(MediaPreviewItem item)
    {
        return item.TmdbId is null ? "" : $" {{tmdb-{item.TmdbId}}}";
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '-' : c);
        }

        var sanitized = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "Unknown Title";
        }

        var baseName = Path.GetFileNameWithoutExtension(sanitized);
        return ReservedWindowsNames.Contains(baseName) ? sanitized + "_" : sanitized;
    }
}
