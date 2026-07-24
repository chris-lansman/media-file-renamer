using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.App.Services;

public sealed record TvShowIdentity(int? TmdbId, int? TvdbId, string Title, int? Year);

public static class TvShowIdentityMatcher
{
    public static TvShowIdentity FromCandidate(TmdbCandidate candidate)
    {
        return new TvShowIdentity(candidate.TmdbId, candidate.TvdbId, candidate.Title, candidate.Year);
    }

    public static bool IsRelatedEpisode(MediaPreviewItem source, MediaPreviewItem candidate)
    {
        if (source.MediaType != "TV" || candidate.MediaType == "Movie")
        {
            return false;
        }

        var sourceHasGroup = !string.IsNullOrWhiteSpace(source.SourceGroupPath);
        var candidateHasGroup = !string.IsNullOrWhiteSpace(candidate.SourceGroupPath);
        if (sourceHasGroup && candidateHasGroup)
        {
            return string.Equals(source.SourceGroupPath, candidate.SourceGroupPath, StringComparison.OrdinalIgnoreCase);
        }

        return Normalize(source.TitleGuess) == Normalize(candidate.TitleGuess);
    }

    public static void Apply(MediaPreviewItem item, TvShowIdentity identity)
    {
        item.MediaType = "TV";
        item.TmdbId = identity.TmdbId;
        item.TvdbId = identity.TvdbId;
        item.MatchedTitle = identity.Title;
        item.Year = identity.Year ?? item.Year;
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
