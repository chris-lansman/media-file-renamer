namespace MediaFileRenamer.App.Services;

public enum EpisodeOrder
{
    Default,
    Official,
    Dvd,
    Absolute,
    Alternate,
    Regional
}

public enum MetadataProvider
{
    Tmdb,
    Tvdb,
    TmdbAndTvdb
}

public sealed record MetadataIdentity(int? TmdbId, int? TvdbId)
{
    public bool IsEmpty => TmdbId is null && TvdbId is null;
}
