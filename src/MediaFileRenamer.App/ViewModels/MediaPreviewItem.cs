using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MediaFileRenamer.App.ViewModels;

public sealed class MediaPreviewItem : INotifyPropertyChanged
{
    private string _matchedTitle = "";
    private string _destinationPath = "";
    private string _status = "";
    private int? _year;
    private int? _season;
    private int? _episode;
    private int? _episodeEnd;
    private int? _tmdbId;
    private int? _tvdbId;
    private string _episodeTitle = "";
    private string _mediaType = "Movie";

    public string SourcePath { get; init; } = "";
    public string SourceGroupPath { get; init; } = "";
    public int GroupFileCount { get; set; } = 1;
    public string SourceFileName => Path.GetFileNameWithoutExtension(SourcePath);
    public string SourceFolder => Path.GetDirectoryName(SourcePath) ?? "";
    public string Extension { get; init; } = "";
    public string ExtensionLabel => Extension.TrimStart('.').ToUpperInvariant();
    public List<string> CompanionPaths { get; } = [];
    public int CompanionCount => CompanionPaths.Count;
    public string MediaType
    {
        get => _mediaType;
        set
        {
            if (SetField(ref _mediaType, value))
            {
                NotifyReviewStateChanged();
            }
        }
    }
    public string TitleGuess { get; set; } = "";
    public string MatchedTitle
    {
        get => _matchedTitle;
        set => SetField(ref _matchedTitle, value);
    }

    public int? Year
    {
        get => _year;
        set => SetField(ref _year, value);
    }

    public int? Season
    {
        get => _season;
        set
        {
            if (SetField(ref _season, value))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public int? Episode
    {
        get => _episode;
        set
        {
            if (SetField(ref _episode, value))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public int? EpisodeEnd
    {
        get => _episodeEnd;
        set => SetField(ref _episodeEnd, value);
    }

    public DateOnly? AirDate { get; set; }
    public int? AbsoluteEpisode { get; set; }
    public int? PartNumber { get; set; }
    public string Edition { get; set; } = "";
    public Services.EpisodeOrder EpisodeOrder { get; set; } = Services.EpisodeOrder.Default;

    public int? TmdbId
    {
        get => _tmdbId;
        set => SetField(ref _tmdbId, value);
    }

    public int? TvdbId
    {
        get => _tvdbId;
        set => SetField(ref _tvdbId, value);
    }

    public string EpisodeTitle
    {
        get => _episodeTitle;
        set => SetField(ref _episodeTitle, value);
    }
    public string DestinationPath
    {
        get => _destinationPath;
        set
        {
            if (SetField(ref _destinationPath, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationFileName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationFolder)));
            }
        }
    }

    public string DestinationFileName => Path.GetFileNameWithoutExtension(DestinationPath);
    public string DestinationFolder => Path.GetDirectoryName(DestinationPath) ?? "";

    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string MatchState
    {
        get
        {
            if (Status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase))
            {
                return "Blocked";
            }

            if (Status is "Moved" or "Copied" or "Already named")
            {
                return "Complete";
            }

            if (MediaType == "Unknown"
                || (MediaType == "TV" && (Season is null || Episode is null))
                || Status.Contains("needs review", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("uncertain", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("No ", StringComparison.OrdinalIgnoreCase))
            {
                return "Review needed";
            }

            if (Status.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("TVDB", StringComparison.OrdinalIgnoreCase))
            {
                return "Matched";
            }

            if (Status.StartsWith("Manual", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("Local", StringComparison.OrdinalIgnoreCase))
            {
                return "Manual choice";
            }

            return "Ready to match";
        }
    }

    public string MatchLabel => MatchState == "Matched"
        ? Status.StartsWith("TVDB", StringComparison.OrdinalIgnoreCase)
            ? "Matched · TVDB"
            : Status.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase)
                ? "Matched · TMDB"
                : "Matched"
        : MatchState;

    public bool RequiresReview => MatchState is "Review needed" or "Ready to match" or "Blocked";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyReviewStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchState)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiresReview)));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
