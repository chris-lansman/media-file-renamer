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
    private int? _tmdbId;
    private string _episodeTitle = "";
    private string _mediaType = "Movie";

    public string SourcePath { get; init; } = "";
    public string SourceGroupPath { get; init; } = "";
    public int GroupFileCount { get; set; } = 1;
    public string SourceFileName => Path.GetFileNameWithoutExtension(SourcePath);
    public string SourceFolder => Path.GetDirectoryName(SourcePath) ?? "";
    public string Extension { get; init; } = "";
    public string ExtensionLabel => Extension.TrimStart('.').ToUpperInvariant();
    public string MediaType
    {
        get => _mediaType;
        set => SetField(ref _mediaType, value);
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
        set => SetField(ref _season, value);
    }

    public int? Episode
    {
        get => _episode;
        set => SetField(ref _episode, value);
    }

    public int? TmdbId
    {
        get => _tmdbId;
        set => SetField(ref _tmdbId, value);
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchState)));
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

            if (Status.Contains("needs review", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("uncertain", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("No ", StringComparison.OrdinalIgnoreCase))
            {
                return "Review needed";
            }

            if (Status.StartsWith("TMDB", StringComparison.OrdinalIgnoreCase))
            {
                return "Matched";
            }

            if (Status.StartsWith("Manual", StringComparison.OrdinalIgnoreCase)
                || Status.StartsWith("Local", StringComparison.OrdinalIgnoreCase))
            {
                return "Manual choice";
            }

            if (Status is "Moved" or "Copied" or "Already named")
            {
                return "Complete";
            }

            return "Ready to match";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
