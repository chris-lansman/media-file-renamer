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
    public string BuildDestination(MediaPreviewItem item, string outputRoot, RenamePreset preset, string customFormat = "")
    {
        var safeTitle = Sanitize(item.MatchedTitle);
        var extension = item.Extension;

        var relative = preset == RenamePreset.Custom
            ? BuildCustomPath(item, customFormat, extension)
            : item.MediaType == "TV"
            ? BuildTvPath(item, safeTitle, extension, preset)
            : BuildMoviePath(item, safeTitle, extension, preset);

        return Path.Combine(outputRoot, relative);
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

        return builder.ToString().Trim(' ', '.');
    }
}
