using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.App.Services;

public enum EpisodeMetadataSource
{
    Tmdb,
    Tvdb
}

public sealed record ResolvedMetadataMatch(
    TmdbMatch Match,
    EpisodeMetadataSource EpisodeSource);

public sealed class TvEpisodeMetadataResolver
{
    public async Task<ResolvedMetadataMatch> ResolveAsync(
        TmdbClient tmdbClient,
        TvdbClient? tvdbClient,
        TmdbCandidate candidate,
        MediaPreviewItem item,
        CancellationToken cancellationToken = default)
    {
        var preferTvdbOrder = item.EpisodeOrder != EpisodeOrder.Default
            || item.AirDate is not null
            || item.AbsoluteEpisode is not null;
        var tmdbMatch = await tmdbClient.BuildMatchAsync(
            candidate,
            item,
            resolveEpisode: !preferTvdbOrder,
            cancellationToken);
        if (candidate.MediaType != "TV"
            || (!preferTvdbOrder && !string.IsNullOrWhiteSpace(tmdbMatch.EpisodeTitle))
            || tvdbClient is null)
        {
            return new ResolvedMetadataMatch(tmdbMatch, EpisodeMetadataSource.Tmdb);
        }

        var season = tmdbMatch.Season ?? item.Season;
        var episode = item.AbsoluteEpisode ?? tmdbMatch.Episode ?? item.Episode;
        if (episode is null && item.AirDate is null)
        {
            return new ResolvedMetadataMatch(tmdbMatch, EpisodeMetadataSource.Tmdb);
        }

        var tvdbId = candidate.TvdbId;
        if (tvdbId is null && candidate.TmdbId is not null)
        {
            tvdbId = await tmdbClient.FindTvdbIdAsync(
                candidate.TmdbId.Value,
                cancellationToken);
        }

        if (tvdbId is null)
        {
            return new ResolvedMetadataMatch(tmdbMatch, EpisodeMetadataSource.Tmdb);
        }

        var tvdbEpisode = await tvdbClient.FindEpisodeAsync(
            tvdbId.Value,
            season,
            episode,
            item.EpisodeOrder,
            item.AirDate,
            cancellationToken);
        if (tvdbEpisode is null
            || !TitlesAgree(item.EpisodeTitle, tvdbEpisode.Name))
        {
            return new ResolvedMetadataMatch(tmdbMatch, EpisodeMetadataSource.Tmdb);
        }

        return new ResolvedMetadataMatch(
            tmdbMatch with
            {
                EpisodeTitle = tvdbEpisode.Name,
                Season = tvdbEpisode.Season,
                Episode = tvdbEpisode.Episode,
                TvdbId = tvdbId
            },
            EpisodeMetadataSource.Tvdb);
    }

    internal static bool TitlesAgree(string existingTitle, string candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(existingTitle))
        {
            return true;
        }

        return Normalize(existingTitle) == Normalize(candidateTitle);
    }

    private static string Normalize(string value)
    {
        return new string(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }
}
