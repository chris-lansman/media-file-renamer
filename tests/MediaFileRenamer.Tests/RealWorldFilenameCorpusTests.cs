using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class RealWorldFilenameCorpusTests
{
    [TestMethod]
    public void Scan_ParsesRepresentativeReleaseCorpus()
    {
        var cases = new[]
        {
            new FilenameCase(
                "Example.Show.S01E01.Pilot.1080p.WEB-DL.mkv",
                "TV", "Example Show", null, 1, 1, null, "Pilot", ""),
            new FilenameCase(
                "Example_Show_2x09_The_Return_720p.mp4",
                "TV", "Example Show", null, 2, 9, null, "The Return", ""),
            new FilenameCase(
                "Example.Show.S03E07-E08.Double.Feature.mkv",
                "TV", "Example Show", null, 3, 7, 8, "Double Feature", ""),
            new FilenameCase(
                "Daily.News.2026.7.3.Guest.Name.WEBRip.mkv",
                "TV", "Daily News", null, null, null, null, "Guest Name", ""),
            new FilenameCase(
                "Blade.Runner.1982.Directors.Cut.2160p.Remux.DV.HDR10.HEVC.TrueHD.Atmos.mkv",
                "Movie", "Blade Runner", 1982, null, null, null, "", "Directors Cut"),
            new FilenameCase(
                "Alien.1979.Theatrical.Cut.1080p.BluRay.x265.10bit.mkv",
                "Movie", "Alien", 1979, null, null, null, "", "Theatrical Cut"),
            new FilenameCase(
                "1987.Lethal.Weapon.1920x1080.BDRip.x264.DTS-HD.MA.mkv",
                "Movie", "Lethal Weapon", 1987, null, null, null, "", ""),
            new FilenameCase(
                "Wreck.It.Ralph.2012.2160p.BluRay.REMUX.HEVC.TrueHD.7.1.Atmos-FGT.mkv",
                "Movie", "Wreck It Ralph", 2012, null, null, null, "", ""),
            new FilenameCase(
                "Movie.Name.2024.Extended.Edition.AV1.mkv",
                "Movie", "Movie Name", 2024, null, null, null, "", "Extended Edition"),
            new FilenameCase(
                "Show.Name.S00E07.Special.Event.mkv",
                "TV", "Show Name", null, 0, 7, null, "Special Event", ""),
            new FilenameCase(
                "Show.Name.S01E03.Finale.pt2.mkv",
                "TV", "Show Name", null, 1, 3, null, "Finale", "")
        };

        using var temp = new TempDirectory();
        foreach (var testCase in cases)
        {
            var item = ScanSingle(temp.CreateFile(testCase.FileName));

            Assert.AreEqual(testCase.MediaType, item.MediaType, testCase.FileName);
            Assert.AreEqual(testCase.Title, item.TitleGuess, testCase.FileName);
            Assert.AreEqual(testCase.Year, item.Year, testCase.FileName);
            Assert.AreEqual(testCase.Season, item.Season, testCase.FileName);
            Assert.AreEqual(testCase.Episode, item.Episode, testCase.FileName);
            Assert.AreEqual(testCase.EpisodeEnd, item.EpisodeEnd, testCase.FileName);
            Assert.AreEqual(testCase.EpisodeTitle, item.EpisodeTitle, testCase.FileName);
            Assert.AreEqual(testCase.Edition, item.Edition, testCase.FileName);
        }
    }

    [TestMethod]
    public void Scan_ParsesEverySupportedSeasonEpisodeWidth()
    {
        using var temp = new TempDirectory();
        for (var season = 0; season <= 20; season++)
        {
            foreach (var episode in new[] { 1, 9, 10, 99, 100, 999 })
            {
                var file = temp.CreateFile(
                    $"Generated.Show.S{season:00}E{episode:00}.Episode.{season}.{episode}.mkv");
                var item = ScanSingle(file);

                Assert.AreEqual("TV", item.MediaType, file);
                Assert.AreEqual(season, item.Season, file);
                Assert.AreEqual(episode, item.Episode, file);
            }
        }
    }

    [TestMethod]
    public void Scan_LeadingYearMovie_AutoMatchesItsExactMovieAndYear()
    {
        using var temp = new TempDirectory();
        var item = ScanSingle(temp.CreateFile(
            "1987.Lethal.Weapon.1920x1080.BDRip.x264.DTS-HD.MA.mkv"));
        var candidate = new TmdbCandidate(
            941,
            "Movie",
            "Lethal Weapon",
            1987,
            "1987-03-06",
            "",
            "");

        var match = TmdbClient.FindAutoMatch(item, [candidate], 92);

        Assert.IsNotNull(match);
        Assert.AreEqual(100, match.ConfidencePercent);
        Assert.AreEqual(941, match.Candidate.TmdbId);
    }

    [TestMethod]
    public void Scan_AssociatesPgsSubtitleCompanion()
    {
        using var temp = new TempDirectory();
        var media = temp.CreateFile("Film.2024.mkv");
        var subtitle = temp.CreateFile("Film.2024.en.forced.sup");

        var item = ScanSingle(media);

        CollectionAssert.Contains(item.CompanionPaths.ToList(), subtitle);
    }

    [TestMethod]
    public void Scan_UsesExplicitSeriesFolderForAbsoluteAndNamedEpisodes()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine("Anime Show (TV Series 2024)", "Season 01");
        var absolute = temp.CreateFile(Path.Combine(folder, "012 - The Promise.mkv"));
        var named = temp.CreateFile(Path.Combine(folder, "Episode 13 - A New Day.mkv"));

        var items = new MediaScanner().Scan([absolute, named]);
        var absoluteItem = items.Single(item => item.SourcePath == absolute);
        var namedItem = items.Single(item => item.SourcePath == named);

        Assert.AreEqual("Anime Show", absoluteItem.TitleGuess);
        Assert.AreEqual(12, absoluteItem.AbsoluteEpisode);
        Assert.AreEqual(EpisodeOrder.Absolute, absoluteItem.EpisodeOrder);
        Assert.AreEqual("The Promise", absoluteItem.EpisodeTitle);
        Assert.AreEqual("Anime Show", namedItem.TitleGuess);
        Assert.AreEqual(13, namedItem.Episode);
        Assert.AreEqual("A New Day", namedItem.EpisodeTitle);
    }

    private static MediaPreviewItem ScanSingle(string path)
    {
        return new MediaScanner().Scan([path]).Single();
    }

    private sealed record FilenameCase(
        string FileName,
        string MediaType,
        string Title,
        int? Year,
        int? Season,
        int? Episode,
        int? EpisodeEnd,
        string EpisodeTitle,
        string Edition);
}
