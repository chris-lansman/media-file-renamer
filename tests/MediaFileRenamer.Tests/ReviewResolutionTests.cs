using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class ReviewResolutionTests
{
    [TestMethod]
    public void ReviewGuidance_ExplainsMatchedShowWithMissingSeason()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            MatchedTitle = "Scooby-Doo, Where Are You!",
            TmdbId = 926,
            Episode = 16,
            EpisodeTitle = "The Beast Is Awake in Bottomless",
            Status = "TV matched; episode number needs review"
        };

        Assert.AreEqual("Review needed", item.MatchState);
        Assert.Contains("season", item.ReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Browse", item.ReviewSuggestion);
    }

    [TestMethod]
    public void ConfirmManualDetails_ClearsStaleReviewStatus()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            MatchedTitle = "Scooby-Doo, Where Are You!",
            Season = 3,
            Episode = 16,
            EpisodeTitle = "The Beast Is Awake in Bottomless Lake",
            Status = "TV matched; episode number needs review"
        };

        var confirmed = MainWindow.TryConfirmManualDetails(item, out var error);

        Assert.IsTrue(confirmed, error);
        Assert.AreEqual("Manual choice", item.MatchState);
        Assert.IsFalse(item.RequiresReview);
    }

    [TestMethod]
    public void ConfirmManualDetails_RequiresCompleteTvCoordinatesAndTitle()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            MatchedTitle = "Scooby-Doo, Where Are You!",
            Episode = 16,
            EpisodeTitle = "The Beast Is Awake in Bottomless Lake",
            Status = "TV matched; episode number needs review"
        };

        var confirmed = MainWindow.TryConfirmManualDetails(item, out var error);

        Assert.IsFalse(confirmed);
        Assert.Contains("season", error, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(item.RequiresReview);
    }

    [TestMethod]
    public void EpisodePicker_RanksCoordinateAndPartialTitleMatchFirst()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            Season = 3,
            Episode = 16,
            EpisodeTitle = "The Beast Is Awake in Bottomless"
        };
        var choices = new[]
        {
            new TmdbEpisodeChoice(3, 2, "A Creepy Tangle in the Bermuda Triangle"),
            new TmdbEpisodeChoice(3, 16, "The Beast Is Awake in Bottomless Lake"),
            new TmdbEpisodeChoice(1, 16, "A Night of Fright Is No Delight")
        };

        var ranked = EpisodePickerWindow.RankChoices(item, choices);

        Assert.AreEqual("S03E16", ranked[0].Coordinate);
        Assert.AreEqual("The Beast Is Awake in Bottomless Lake", ranked[0].Title);
    }

    [TestMethod]
    public void EpisodeResolver_AcceptsUniqueHighConfidencePartialTitle()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            Episode = 16,
            EpisodeTitle = "The Beast Is Awake in Bottomless"
        };
        var episodes = new[]
        {
            new TmdbEpisode("A Creepy Tangle in the Bermuda Triangle", 3, 2),
            new TmdbEpisode("The Beast Is Awake in Bottomless Lake", 3, 16),
            new TmdbEpisode("A Night of Fright Is No Delight", 1, 16)
        };

        var match = TmdbClient.FindBestEpisodeTitleMatch(item, episodes);

        Assert.IsNotNull(match);
        Assert.AreEqual(3, match.Season);
        Assert.AreEqual(16, match.Number);
    }

    [TestMethod]
    public void EpisodeResolver_RejectsAmbiguousShortTitles()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            EpisodeTitle = "Pilot"
        };
        var episodes = new[]
        {
            new TmdbEpisode("Pilot Part One", 1, 1),
            new TmdbEpisode("Pilot Part Two", 1, 2)
        };

        Assert.IsNull(TmdbClient.FindBestEpisodeTitleMatch(item, episodes));
    }
}
