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
        set
        {
            if (SetField(ref _matchedTitle, value))
            {
                NotifyReviewStateChanged();
            }
        }
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
        set
        {
            if (SetField(ref _tmdbId, value))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public int? TvdbId
    {
        get => _tvdbId;
        set
        {
            if (SetField(ref _tvdbId, value))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string EpisodeTitle
    {
        get => _episodeTitle;
        set
        {
            if (SetField(ref _episodeTitle, value))
            {
                NotifyReviewStateChanged();
            }
        }
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

    public string ReviewReason
    {
        get
        {
            if (!RequiresReview)
            {
                return "This file is ready.";
            }

            if (MatchState == "Blocked")
            {
                return Status;
            }

            if (MediaType == "Unknown")
            {
                return "The app could not confidently determine whether this is a movie or TV episode.";
            }

            if (MediaType == "TV" && Season is null)
            {
                return TmdbId is not null || TvdbId is not null
                    ? "The show was identified, but the season could not be determined."
                    : "A season number could not be determined from the file or folder name.";
            }

            if (MediaType == "TV" && Episode is null)
            {
                return "The show was identified, but the episode number could not be determined.";
            }

            if (MediaType == "TV" && string.IsNullOrWhiteSpace(EpisodeTitle))
            {
                return "The season and episode are known, but the episode title could not be verified.";
            }

            return string.IsNullOrWhiteSpace(Status)
                ? "This file still needs a decision before the batch can be applied."
                : Status;
        }
    }

    public string ReviewSuggestion
    {
        get
        {
            if (!RequiresReview)
            {
                return "No action is required.";
            }

            if (MediaType == "TV" && (TmdbId is not null || TvdbId is not null))
            {
                return "Browse the matched show's episodes, or edit the Selected file fields below and confirm your details.";
            }

            if (MediaType == "TV")
            {
                return "Choose the correct show first, or edit the Selected file fields below and confirm your details.";
            }

            if (MediaType == "Unknown")
            {
                return "Choose a metadata match, or set the type and complete the Selected file fields below manually.";
            }

            return "Choose a different match, or confirm the edited details in Selected file below.";
        }
    }

    public bool CanConfirmManualDetails =>
        !string.IsNullOrWhiteSpace(MatchedTitle)
        && (MediaType == "Movie"
            || (MediaType == "TV"
                && Season is >= 0
                && Episode is > 0
                && !string.IsNullOrWhiteSpace(EpisodeTitle)));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyReviewStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchState)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiresReview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewReason)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewSuggestion)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConfirmManualDetails)));
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
