using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class ProductionNamingTests
{
    [TestMethod]
    public void PlexTvName_PreservesEpisodeRangeAndSplitPart()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            MatchedTitle = "Example Show",
            Season = 1,
            Episode = 1,
            EpisodeEnd = 2,
            EpisodeTitle = "Opening Night",
            PartNumber = 2,
            Extension = ".mkv",
            TvdbId = 1234
        };

        var destination = new RenamePlanner().BuildDestination(
            item,
            @"C:\Output",
            RenamePreset.PlexStandard);

        StringAssert.Contains(
            destination,
            @"TV Shows\Example Show {tvdb-1234}\Season 01\Example Show - S01E01-E02 - Opening Night - pt2.mkv");
    }

    [TestMethod]
    public void PlexMovieName_PreservesEditionTag()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "Movie",
            MatchedTitle = "Blade Runner",
            Year = 1982,
            Edition = "Director's Cut",
            Extension = ".mkv",
            TmdbId = 78
        };

        var destination = new RenamePlanner().BuildDestination(
            item,
            @"C:\Output",
            RenamePreset.PlexStandard);

        StringAssert.Contains(
            destination,
            @"Movies\Blade Runner (1982) {tmdb-78} {edition-Director's Cut}\Blade Runner (1982) {tmdb-78} {edition-Director's Cut}.mkv");
    }

    [TestMethod]
    public void CustomName_ExposesAdvancedMetadataTokens()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            MatchedTitle = "Example Show",
            AirDate = new DateOnly(2026, 7, 23),
            AbsoluteEpisode = 12,
            EpisodeEnd = 13,
            PartNumber = 2,
            Edition = "Extended",
            Extension = ".mkv",
            TvdbId = 1234
        };

        var destination = new RenamePlanner().BuildDestination(
            item,
            @"C:\Output",
            RenamePreset.Custom,
            @"{Title}\{AirDate}-{AbsoluteEpisode}-{EpisodeEnd}-pt{Part}-{ProviderId}-{EditionTag}");

        StringAssert.EndsWith(
            destination,
            @"Example Show\2026-07-23-012-13-pt2-{tvdb-1234}-{edition-Extended}.mkv");
    }
}
