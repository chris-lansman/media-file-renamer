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
    private static readonly HashSet<string> CompanionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".vtt", ".nfo", ".xml",
        ".jpg", ".jpeg", ".png", ".webp"
    };
    private static readonly HashSet<string> ExtraFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Behind The Scenes", "Deleted Scenes", "Featurettes", "Interviews",
        "Scenes", "Shorts", "Trailers", "Other", "Extras", "Samples"
    };

    public IReadOnlyList<MediaPreviewItem> Scan(IEnumerable<string> paths)
    {
        var items = paths
            .SelectMany(ExpandPath)
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(file =>
            {
                var item = Parse(file.Path, file.GroupPath);
                item.CompanionPaths.AddRange(FindCompanionFiles(file.Path));
                return item;
            })
            .ToList();

        foreach (var group in items.GroupBy(item => item.SourceGroupPath, StringComparer.OrdinalIgnoreCase))
        {
            var groupedItems = group.ToList();
            var count = groupedItems.Count;
            InferMissingSeasonFromSiblings(groupedItems);
            foreach (var item in groupedItems)
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

    internal static void InferMissingSeasonFromSiblings(
        IReadOnlyList<MediaPreviewItem> items)
    {
        var explicitSeasons = items
            .Where(item => item.MediaType == "TV" && item.Season is not null)
            .Select(item => item.Season!.Value)
            .Distinct()
            .ToList();
        if (explicitSeasons.Count != 1)
        {
            return;
        }

        var season = explicitSeasons[0];
        foreach (var item in items.Where(item =>
                     item.MediaType == "TV"
                     && item.Season is null
                     && item.Episode is not null))
        {
            item.Season = season;
            item.Status = $"Parsed TV episode; inferred season {season} from sibling files";
        }
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
                     .Where(file => MediaExtensions.Contains(Path.GetExtension(file)))
                     .Where(file => !IsInsideExtraFolder(file, path)))
        {
            yield return new ScannedFile(file, GetGroupPath(file));
        }
    }

    private static IEnumerable<string> FindCompanionFiles(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        return Directory.EnumerateFiles(directory)
            .Where(path => CompanionExtensions.Contains(Path.GetExtension(path)))
            .Where(path =>
            {
                var companionStem = Path.GetFileNameWithoutExtension(path);
                return companionStem.Equals(stem, StringComparison.OrdinalIgnoreCase)
                    || companionStem.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsInsideExtraFolder(string file, string scanRoot)
    {
        var root = Path.GetFullPath(scanRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));

        while (!string.IsNullOrWhiteSpace(directory)
               && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (ExtraFolderNames.Contains(Path.GetFileName(directory)))
            {
                return true;
            }

            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
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
        var absoluteCleaned = CleanReleaseName(fileName, removeLeadingIndex: false);
        var cleaned = CleanReleaseName(fileName);
        var partNumber = ParsePartNumber(cleaned);
        cleaned = SplitPartPattern().Replace(cleaned, " ").Trim();
        var edition = ParseEdition(cleaned);
        cleaned = EditionPattern().Replace(cleaned, " ").Trim();
        var tvMatch = TvPattern().Match(cleaned);
        if (!tvMatch.Success)
        {
            tvMatch = AlternateTvPattern().Match(cleaned);
        }
        var yearMatch = YearPattern().Match(cleaned);
        var folderShow = ParseShowFolder(Path.GetFileName(groupPath));

        if (tvMatch.Success)
        {
            var parsedTitle = TrimTitle(cleaned[..tvMatch.Index]);
            var title = IsGenericEpisodeTitle(parsedTitle) && folderShow is not null
                ? folderShow.Title
                : NormalizeTitle(parsedTitle);
            var parsedEpisodeTitle = TrimTitle(cleaned[(tvMatch.Index + tvMatch.Length)..]);
            return new MediaPreviewItem
            {
                SourcePath = path,
                SourceGroupPath = groupPath,
                Extension = Path.GetExtension(path),
                MediaType = "TV",
                TitleGuess = title,
                MatchedTitle = title,
                Year = folderShow?.Year,
                Season = int.Parse(tvMatch.Groups["season"].Value),
                Episode = int.Parse(tvMatch.Groups["episode"].Value),
                EpisodeEnd = ParseEpisodeEnd(tvMatch),
                PartNumber = partNumber,
                EpisodeTitle = string.IsNullOrWhiteSpace(parsedEpisodeTitle)
                    ? ""
                    : NormalizeTitle(parsedEpisodeTitle),
                Status = "Parsed TV episode"
            };
        }

        var dateEpisodeMatch = DateEpisodePattern().Match(cleaned);
        if (dateEpisodeMatch.Success && TryParseAirDate(dateEpisodeMatch, out var airDate))
        {
            var parsedTitle = TrimTitle(cleaned[..dateEpisodeMatch.Index]);
            var title = IsGenericEpisodeTitle(parsedTitle) && folderShow is not null
                ? folderShow.Title
                : NormalizeTitle(parsedTitle);
            var parsedEpisodeTitle = TrimTitle(cleaned[(dateEpisodeMatch.Index + dateEpisodeMatch.Length)..]);
            return new MediaPreviewItem
            {
                SourcePath = path,
                SourceGroupPath = groupPath,
                Extension = Path.GetExtension(path),
                MediaType = "TV",
                TitleGuess = title,
                MatchedTitle = title,
                Year = folderShow?.Year,
                AirDate = airDate,
                PartNumber = partNumber,
                EpisodeTitle = string.IsNullOrWhiteSpace(parsedEpisodeTitle)
                    ? ""
                    : NormalizeTitle(parsedEpisodeTitle),
                Status = "Parsed date-based TV episode"
            };
        }

        absoluteCleaned = SplitPartPattern().Replace(absoluteCleaned, " ").Trim();
        var absoluteMatch = AbsoluteEpisodePattern().Match(absoluteCleaned);
        if (absoluteMatch.Success && folderShow?.IsExplicitTv == true)
        {
            var absoluteEpisode = int.Parse(absoluteMatch.Groups["episode"].Value);
            return new MediaPreviewItem
            {
                SourcePath = path,
                SourceGroupPath = groupPath,
                Extension = Path.GetExtension(path),
                MediaType = "TV",
                TitleGuess = folderShow.Title,
                MatchedTitle = folderShow.Title,
                Year = folderShow.Year,
                AbsoluteEpisode = absoluteEpisode,
                Episode = absoluteEpisode,
                EpisodeOrder = EpisodeOrder.Absolute,
                PartNumber = partNumber,
                EpisodeTitle = NormalizeTitle(absoluteMatch.Groups["title"].Value),
                Status = "Parsed absolute-numbered TV episode"
            };
        }

        var namedEpisodeMatch = NamedEpisodePattern().Match(cleaned);
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
                PartNumber = partNumber,
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
                PartNumber = partNumber,
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
            Edition = edition,
            PartNumber = partNumber,
            Status = yearMatch.Success ? "Parsed movie" : "Type uncertain"
        };
    }

    private static int? ParseEpisodeEnd(Match match)
    {
        return int.TryParse(match.Groups["episodeEnd"].Value, out var episodeEnd)
            ? episodeEnd
            : null;
    }

    private static bool TryParseAirDate(Match match, out DateOnly airDate)
    {
        return DateOnly.TryParse(
            $"{match.Groups["year"].Value}-{match.Groups["month"].Value}-{match.Groups["day"].Value}",
            out airDate);
    }

    private static int? ParsePartNumber(string value)
    {
        var match = SplitPartPattern().Match(value);
        return match.Success && int.TryParse(match.Groups["part"].Value, out var part)
            ? part
            : null;
    }

    private static string ParseEdition(string value)
    {
        var match = EditionPattern().Match(value);
        return match.Success
            ? SpacePattern().Replace(match.Groups["edition"].Value, " ").Trim()
            : "";
    }

    private static string CleanReleaseName(string value, bool removeLeadingIndex = true)
    {
        var cleaned = value.Replace('.', ' ').Replace('_', ' ');
        if (removeLeadingIndex)
        {
            cleaned = LeadingIndexPattern().Replace(cleaned, "");
        }
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

    private static bool IsGenericEpisodeTitle(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            || value.Equals("Special", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Specials", StringComparison.OrdinalIgnoreCase)
            || SeasonFolderPattern().IsMatch(value);
    }

    private static string TrimTitle(string value)
    {
        return value.Trim(' ', '.', '-', '_', '(', '[', '{');
    }

    [GeneratedRegex(@"S(?<season>\d{1,2})E(?<episode>\d{1,3})(?:(?:\s*[-–]\s*E?|E)(?<episodeEnd>\d{1,3}))?", RegexOptions.IgnoreCase)]
    private static partial Regex TvPattern();

    [GeneratedRegex(@"(?<!\d)(?<season>\d{1,2})x(?<episode>\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex AlternateTvPattern();

    [GeneratedRegex(@"(?<!\d)(?<year>19\d{2}|20\d{2})[-. ](?<month>0?[1-9]|1[0-2])[-. ](?<day>0?[1-9]|[12]\d|3[01])(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DateEpisodePattern();

    [GeneratedRegex(@"^\s*(?<episode>\d{1,4})\s*[-. ]+\s*(?<title>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteEpisodePattern();

    [GeneratedRegex(@"\s*(?:part|pt|cd|disc)\s*[-.]?\s*(?<part>\d{1,2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPartPattern();

    [GeneratedRegex(@"\s*[\[(]?(?<edition>director'?s\s+cut|extended(?:\s+edition|\s+cut)?|unrated(?:\s+edition)?|theatrical(?:\s+cut)?|final\s+cut|imax(?:\s+edition)?|remastered)[\])]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionPattern();

    [GeneratedRegex(@"^(?:episode|ep)\s*(?<episode>\d{1,3})\s*[-.:]?\s*(?<title>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedEpisodePattern();

    [GeneratedRegex(@"^(?:season\s*\d{1,3}|s\d{1,3}|specials?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderPattern();

    [GeneratedRegex(@"^(?<title>.+?)\s*\(\s*(?:tv\s*)?series\s*(?<year>19\d{2}|20\d{2})(?:\s*[-–—]\s*(?:19\d{2}|20\d{2}))?\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TvSeriesFolderPattern();

    [GeneratedRegex(@"\b(?<year>19\d{2}|20\d{2})\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b(480p|720p|1080p|2160p|4k\d{2,3}|4k|uhd|web[- ]?dl|webrip|bluray|brrip|dvdrip|hdrip|remux|hdr10|hdr|dolby[- ]?vision|dovi|dv|av1|10bit|8bit|x26[45]?|h26[45]|hevc|aac|dts|truehd|atmos|proper|repack|no[- ]?dnr|\d{2}mm)\b", RegexOptions.IgnoreCase)]
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
