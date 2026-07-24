using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.App.Services;

public sealed record MetadataCandidateSearchResult(
    IReadOnlyList<TmdbCandidate> Candidates,
    TmdbAutoMatch? AutoMatch,
    bool TvdbWasQueried);

public sealed class MetadataMatchService
{
    public async Task<MetadataCandidateSearchResult> SearchAsync(
        MediaPreviewItem item,
        TmdbClient tmdbClient,
        TvdbClient? tvdbClient,
        int confidenceThreshold,
        string? queryOverride = null,
        CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(queryOverride)
            ? item.TitleGuess
            : queryOverride.Trim();
        IReadOnlyList<TmdbCandidate> tmdbCandidates;
        MetadataLookupException? tmdbFailure = null;
        try
        {
            tmdbCandidates = await tmdbClient.SearchCandidatesAsync(
                item,
                query,
                cancellationToken);
        }
        catch (MetadataLookupException ex) when (tvdbClient is not null && item.MediaType != "Movie")
        {
            tmdbCandidates = [];
            tmdbFailure = ex;
        }

        var initialAutoMatch = TmdbClient.FindAutoMatch(
            item,
            tmdbCandidates,
            confidenceThreshold,
            query);
        var shouldQueryTvdb = tvdbClient is not null
            && item.MediaType != "Movie"
            && (tmdbCandidates.Count == 0 || initialAutoMatch is null);
        if (!shouldQueryTvdb)
        {
            return new MetadataCandidateSearchResult(tmdbCandidates, initialAutoMatch, false);
        }

        IReadOnlyList<TvdbSeriesCandidate> tvdbResults;
        try
        {
            tvdbResults = await tvdbClient!.SearchSeriesAsync(query, cancellationToken);
        }
        catch (MetadataLookupException) when (tmdbCandidates.Count > 0)
        {
            return new MetadataCandidateSearchResult(
                tmdbCandidates,
                initialAutoMatch,
                true);
        }
        if (tvdbResults.Count == 0 && tmdbCandidates.Count == 0 && tmdbFailure is not null)
        {
            throw tmdbFailure;
        }

        var mappedCandidates = await Task.WhenAll(
            tvdbResults.Select(result => MapTvdbCandidateAsync(
                result,
                tmdbClient,
                cancellationToken)));
        var merged = MergeCandidates(tmdbCandidates, mappedCandidates);
        return new MetadataCandidateSearchResult(
            merged,
            TmdbClient.FindAutoMatch(item, merged, confidenceThreshold, query),
            true);
    }

    internal static IReadOnlyList<TmdbCandidate> MergeCandidates(
        IReadOnlyList<TmdbCandidate> tmdbCandidates,
        IReadOnlyList<TmdbCandidate> tvdbCandidates)
    {
        var results = tmdbCandidates.ToList();
        foreach (var tvdbCandidate in tvdbCandidates)
        {
            var existingIndex = results.FindIndex(existing =>
                (tvdbCandidate.TmdbId is not null && existing.TmdbId == tvdbCandidate.TmdbId)
                || (tvdbCandidate.TvdbId is not null && existing.TvdbId == tvdbCandidate.TvdbId));

            if (existingIndex < 0)
            {
                results.Add(tvdbCandidate);
                continue;
            }

            var existing = results[existingIndex];
            results[existingIndex] = existing with
            {
                TmdbId = existing.TmdbId ?? tvdbCandidate.TmdbId,
                TvdbId = existing.TvdbId ?? tvdbCandidate.TvdbId,
                Provider = MetadataProvider.TmdbAndTvdb,
                Aliases = existing.Aliases
                    .Concat(tvdbCandidate.Aliases)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        return results
            .DistinctBy(candidate => (
                candidate.TmdbId,
                candidate.TvdbId,
                candidate.MediaType,
                Normalize(candidate.Title),
                candidate.Year))
            .Take(16)
            .ToList();
    }

    private static async Task<TmdbCandidate> MapTvdbCandidateAsync(
        TvdbSeriesCandidate candidate,
        TmdbClient tmdbClient,
        CancellationToken cancellationToken)
    {
        var tmdbCandidate = await tmdbClient.FindTvByTvdbIdAsync(
            candidate.TvdbId,
            cancellationToken);
        if (tmdbCandidate is not null)
        {
            return tmdbCandidate with
            {
                TvdbId = candidate.TvdbId,
                Provider = MetadataProvider.TmdbAndTvdb,
                Aliases = tmdbCandidate.Aliases
                    .Concat(candidate.Aliases)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        return new TmdbCandidate(
            0,
            "TV",
            candidate.Title,
            candidate.Year,
            candidate.Year?.ToString() ?? "",
            candidate.PosterUrl,
            candidate.Overview)
        {
            TmdbId = null,
            TvdbId = candidate.TvdbId,
            Provider = MetadataProvider.Tvdb,
            Aliases = candidate.Aliases
        };
    }

    private static string Normalize(string value)
    {
        return new string(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }
}
