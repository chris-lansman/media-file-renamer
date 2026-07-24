using MediaFileRenamer.App.ViewModels;
using System.IO;
using System.Text.RegularExpressions;

namespace MediaFileRenamer.App.Services;

public sealed partial class MediaScanner
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".mpeg", ".mpg",
        ".m2ts", ".mts", ".webm", ".vob"
    };

    public IReadOnlyList<MediaPreviewItem> Scan(IEnumerable<string> paths)
    {
        var items = paths
            .SelectMany(ExpandPath)
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(file => Parse(file.Path, file.GroupPath))
            .ToList();

        foreach (var group in items.GroupBy(item => item.SourceGroupPath, StringComparer.OrdinalIgnoreCase))
        {
            var count = group.Count();
            foreach (var item in group)
            {
                item.GroupFileCount = count;
                if (item.MediaType == "Unknown" && count >= 3)
                {
                    item.Status = "Type uncertain; matching folder and files";
                }
            }
        }

        return items;
    }

    private static IEnumerable<ScannedFile> ExpandPath(string path)
    {
        if (File.Exists(path) && MediaExtensions.Contains(Path.GetExtension(path)))
        {
            yield return new ScannedFile(path, GetGroupPath(path));
            yield break;
        }

        if (!Directory.Exists(path))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(path, "*.*", options)
                     .Where(file => MediaExtensions.Contains(Path.GetExtension(file))))
        {
            yield return new ScannedFile(file, GetGroupPath(file));
        }
    }

    private static string GetGroupPath(string file)
    {
        var parent = Path.GetDirectoryName(file) ?? "";
        return SeasonFolderPattern().IsMatch(Path.GetFileName(parent))
            ? Path.GetDirectoryName(parent) ?? parent
            : parent;
    }

    private static MediaPreviewItem Parse(string path, string groupPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var cleaned = CleanReleaseName(fileName);
        var tvMatch = TvPattern().Match(cleaned);
        if (!tvMatch.Success)
        {
            tvMatch = AlternateTvPattern().Match(cleaned);
        }
        var yearMatch = YearPattern().Match(cleaned);

        if (tvMatch.Success)
        {
            var title = TrimTitle(cleaned[..tvMatch.Index]);
            return new MediaPreviewItem
            {
                SourcePath = path,
                SourceGroupPath = groupPath,
                Extension = Path.GetExtension(path),
                MediaType = "TV",
                TitleGuess = NormalizeTitle(title),
                MatchedTitle = NormalizeTitle(title),
                Season = int.Parse(tvMatch.Groups["season"].Value),
                Episode = int.Parse(tvMatch.Groups["episode"].Value),
                Status = "Parsed TV episode"
            };
        }

        var namedEpisodeMatch = NamedEpisodePattern().Match(cleaned);
        var folderShow = ParseShowFolder(Path.GetFileName(groupPath));
        if (namedEpisodeMatch.Success && folderShow is not null)
        {
            return new MediaPreviewItem
            {
                SourcePath = path,
                SourceGroupPath = groupPath,
                Extension = Path.GetExtension(path),
                MediaType = "TV",
                TitleGuess = folderShow.Title,
                MatchedTitle = folderShow.Title,
                Year = folderShow.Year,
                Episode = int.Parse(namedEpisodeMatch.Groups["episode"].Value),
                EpisodeTitle = NormalizeTitle(namedEpisodeMatch.Groups["title"].Value),
                Status = "Parsed TV episode from folder"
            };
        }

        if (folderShow?.IsExplicitTv == true)
        {
            return new MediaPreviewItem
            {
                SourcePath = path,
                SourceGroupPath = groupPath,
                Extension = Path.GetExtension(path),
                MediaType = "TV",
                TitleGuess = folderShow.Title,
                MatchedTitle = folderShow.Title,
                Year = folderShow.Year,
                EpisodeTitle = NormalizeTitle(cleaned),
                Status = "Parsed TV show from folder; resolving episode"
            };
        }

        var movieTitle = yearMatch.Success ? TrimTitle(cleaned[..yearMatch.Index]) : TrimTitle(cleaned);
        return new MediaPreviewItem
        {
            SourcePath = path,
            SourceGroupPath = groupPath,
            Extension = Path.GetExtension(path),
            MediaType = yearMatch.Success ? "Movie" : "Unknown",
            TitleGuess = NormalizeTitle(movieTitle),
            MatchedTitle = NormalizeTitle(movieTitle),
            Year = yearMatch.Success ? int.Parse(yearMatch.Groups["year"].Value) : null,
            Status = yearMatch.Success ? "Parsed movie" : "Type uncertain"
        };
    }

    private static string CleanReleaseName(string value)
    {
        var cleaned = value.Replace('.', ' ').Replace('_', ' ');
        cleaned = LeadingIndexPattern().Replace(cleaned, "");
        cleaned = ReleaseNoisePattern().Replace(cleaned, " ");
        cleaned = TrailingVersionPattern().Replace(cleaned, " ");
        return SpacePattern().Replace(cleaned, " ").Trim();
    }

    public static string CleanSearchText(string value)
    {
        return ParseShowFolder(value)?.Title ?? NormalizeTitle(TrimTitle(CleanReleaseName(value)));
    }

    private static ShowFolderInfo? ParseShowFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = TvSeriesFolderPattern().Match(value.Trim());
        if (match.Success)
        {
            var title = NormalizeTitle(TrimTitle(match.Groups["title"].Value));
            return string.IsNullOrWhiteSpace(title)
                ? null
                : new ShowFolderInfo(title, ParseFolderYear(match.Groups["year"].Value), true);
        }

        var titleWithoutYear = YearPattern().Replace(value, " ");
        var fallbackTitle = NormalizeTitle(TrimTitle(titleWithoutYear));
        return string.IsNullOrWhiteSpace(fallbackTitle) ? null : new ShowFolderInfo(fallbackTitle, null, false);
    }

    private static int? ParseFolderYear(string value)
    {
        return int.TryParse(value, out var year) ? year : null;
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown Title";
        }

        return SpacePattern().Replace(value, " ").Trim();
    }

    private static string TrimTitle(string value)
    {
        return value.Trim(' ', '.', '-', '_', '(', '[', '{');
    }

    [GeneratedRegex(@"S(?<season>\d{1,2})E(?<episode>\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex TvPattern();

    [GeneratedRegex(@"(?<!\d)(?<season>\d{1,2})x(?<episode>\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex AlternateTvPattern();

    [GeneratedRegex(@"^(?:episode|ep)\s*(?<episode>\d{1,3})\s*[-.:]?\s*(?<title>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedEpisodePattern();

    [GeneratedRegex(@"^season\s*\d{1,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderPattern();

    [GeneratedRegex(@"^(?<title>.+?)\s*\(\s*(?:tv\s*)?series\s*(?<year>19\d{2}|20\d{2})(?:\s*[-–—]\s*(?:19\d{2}|20\d{2}))?\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TvSeriesFolderPattern();

    [GeneratedRegex(@"\b(?<year>19\d{2}|20\d{2})\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b(480p|720p|1080p|2160p|4k\d{2,3}|4k|uhd|web[- ]?dl|webrip|bluray|brrip|dvdrip|hdrip|x26[45]?|h26[45]|hevc|aac|dts|truehd|atmos|proper|repack|no[- ]?dnr|\d{2}mm)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseNoisePattern();

    [GeneratedRegex(@"^\s*\d{1,3}\s*[- .]+\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingIndexPattern();

    [GeneratedRegex(@"\bv\d+(?:[ .]\d+)*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersionPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();

    private sealed record ScannedFile(string Path, string GroupPath);
    private sealed record ShowFolderInfo(string Title, int? Year, bool IsExplicitTv);
}
