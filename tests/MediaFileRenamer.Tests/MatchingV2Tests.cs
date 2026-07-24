using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Net;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class MatchingV2ParserTests
{
    [TestMethod]
    public void Scan_ParsesMultiEpisodeRange()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("Show.Name.S01E01-E02.Pilot.mkv");

        var item = AssertSingle(path);

        Assert.AreEqual("TV", item.MediaType);
        Assert.AreEqual(1, item.Season);
        Assert.AreEqual(1, item.Episode);
        Assert.AreEqual(2, item.EpisodeEnd);
        Assert.AreEqual("Pilot", item.EpisodeTitle);
    }

    [TestMethod]
    public void Scan_ParsesDateBasedEpisode()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("The.Daily.Show.2026.07.23.Guest.Name.mkv");

        var item = AssertSingle(path);

        Assert.AreEqual("TV", item.MediaType);
        Assert.AreEqual("The Daily Show", item.TitleGuess);
        Assert.AreEqual(new DateOnly(2026, 7, 23), item.AirDate);
        Assert.AreEqual("Guest Name", item.EpisodeTitle);
    }

    [TestMethod]
    public void Scan_InvalidCalendarDateDoesNotCrash()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("Show.2026.02.31.Guest.mkv");

        var item = AssertSingle(path);

        Assert.IsNull(item.AirDate);
    }

    [TestMethod]
    public void Scan_ParsesAbsoluteNumberOnlyWithExplicitTvFolder()
    {
        using var temp = new TempDirectory();
        var show = temp.CreateDirectory(Path.Combine("Anime Show (TV Series 2024)", "Season 01"));
        var path = temp.CreateFile(Path.Combine(show, "012 - The Promise.mkv"));

        var item = AssertSingle(path);

        Assert.AreEqual("TV", item.MediaType);
        Assert.AreEqual("Anime Show", item.TitleGuess);
        Assert.AreEqual(12, item.AbsoluteEpisode);
        Assert.AreEqual(EpisodeOrder.Absolute, item.EpisodeOrder);
        Assert.AreEqual("The Promise", item.EpisodeTitle);
    }

    [TestMethod]
    public void Scan_ParsesSplitPartAndMovieEdition()
    {
        using var temp = new TempDirectory();
        var episode = temp.CreateFile("Show.S01E03.Finale.Part.2.mkv");
        var movie = temp.CreateFile("Blade.Runner.1982.Directors.Cut.1080p.mkv");

        var items = new MediaScanner().Scan([episode, movie]);

        var episodeItem = items.Single(item => item.SourcePath == episode);
        Assert.AreEqual(2, episodeItem.PartNumber);
        Assert.AreEqual("Finale", episodeItem.EpisodeTitle);

        var movieItem = items.Single(item => item.SourcePath == movie);
        Assert.AreEqual("Movie", movieItem.MediaType);
        Assert.AreEqual("Blade Runner", movieItem.TitleGuess);
        Assert.AreEqual("Directors Cut", movieItem.Edition);
    }

    private static MediaPreviewItem AssertSingle(string path)
    {
        return new MediaScanner().Scan([path]).Single();
    }
}

[TestClass]
public sealed class MatchingV2ProviderTests
{
    [TestMethod]
    public async Task TvdbEpisode_UsesSelectedDvdOrder()
    {
        var requests = new List<CapturedRequest>();
        var handler = new StubHttpHandler(request =>
        {
            requests.Add(CapturedRequest.From(request));
            return request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"data":{"token":"token"}}""")
                : TestHttp.JsonResponse(
                    """{"data":{"episodes":[{"id":10,"name":"DVD Episode","seasonNumber":2,"number":4}]}}""");
        });
        var client = new TvdbClient("key", httpClient: new HttpClient(handler));

        var episode = await client.FindEpisodeAsync(99, 2, 4, EpisodeOrder.Dvd);

        Assert.IsNotNull(episode);
        StringAssert.Contains(requests[1].PathAndQuery, "/episodes/dvd?");
        StringAssert.Contains(requests[1].PathAndQuery, "season=2");
        StringAssert.Contains(requests[1].PathAndQuery, "episodeNumber=4");
    }

    [TestMethod]
    public async Task TvdbEpisode_RetriesTransientServerFailure()
    {
        var episodeAttempts = 0;
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return TestHttp.JsonResponse("""{"data":{"token":"token"}}""");
            }

            episodeAttempts++;
            return episodeAttempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : TestHttp.JsonResponse(
                    """{"data":{"episodes":[{"id":10,"name":"Recovered","seasonNumber":1,"number":1}]}}""");
        });
        var client = new TvdbClient("key", httpClient: new HttpClient(handler));

        var episode = await client.FindEpisodeAsync(99, 1, 1);

        Assert.IsNotNull(episode);
        Assert.AreEqual("Recovered", episode.Name);
        Assert.AreEqual(2, episodeAttempts);
    }

    [TestMethod]
    public async Task Resolver_SupportsTvdbOnlyCandidateAndAirDate()
    {
        var tmdbCalls = 0;
        var tmdb = new TmdbClient(
            "tmdb",
            new HttpClient(new StubHttpHandler(_ =>
            {
                tmdbCalls++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })));
        var tvdb = new TvdbClient(
            "tvdb",
            httpClient: new HttpClient(new StubHttpHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                    ? TestHttp.JsonResponse("""{"data":{"token":"token"}}""")
                    : TestHttp.JsonResponse(
                        """{"data":{"episodes":[{"id":20,"name":"Live Episode","seasonNumber":3,"number":9,"aired":"2026-07-23"}]}}"""))));
        var candidate = new TmdbCandidate(0, "TV", "Daily Show", 2026, "", "", "")
        {
            TmdbId = null,
            TvdbId = 77,
            Provider = MetadataProvider.Tvdb
        };
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Daily Show",
            AirDate = new DateOnly(2026, 7, 23)
        };

        var resolved = await new TvEpisodeMetadataResolver().ResolveAsync(
            tmdb,
            tvdb,
            candidate,
            item);

        Assert.AreEqual(0, tmdbCalls);
        Assert.AreEqual(EpisodeMetadataSource.Tvdb, resolved.EpisodeSource);
        Assert.IsNull(resolved.Match.TmdbId);
        Assert.AreEqual(77, resolved.Match.TvdbId);
        Assert.AreEqual(3, resolved.Match.Season);
        Assert.AreEqual(9, resolved.Match.Episode);
        Assert.AreEqual("Live Episode", resolved.Match.EpisodeTitle);
    }

    [TestMethod]
    public async Task UnifiedSearch_KeepsTvdbOnlyCandidateWhenTmdbIsAmbiguous()
    {
        var tmdb = new TmdbClient(
            "tmdb",
            new HttpClient(new StubHttpHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/search/tv", StringComparison.Ordinal))
                {
                    return TestHttp.JsonResponse(
                        """{"results":[{"id":1,"name":"Close Show","first_air_date":"2020-01-01"},{"id":2,"name":"Close Shows","first_air_date":"2020-01-01"}]}""");
                }

                return TestHttp.JsonResponse("""{"tv_results":[]}""");
            })));
        var tvdb = new TvdbClient(
            "tvdb",
            httpClient: new HttpClient(new StubHttpHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                    ? TestHttp.JsonResponse("""{"data":{"token":"token"}}""")
                    : TestHttp.JsonResponse(
                        """{"data":[{"tvdb_id":"900","name":"Close Show","year":"2020","aliases":["The Close Show"]}]}"""))));
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Close",
            Year = 2020
        };

        var result = await new MetadataMatchService().SearchAsync(
            item,
            tmdb,
            tvdb,
            92);

        Assert.IsTrue(result.TvdbWasQueried);
        var tvdbOnly = result.Candidates.Single(candidate => candidate.TvdbId == 900);
        Assert.IsNull(tvdbOnly.TmdbId);
        Assert.AreEqual(MetadataProvider.Tvdb, tvdbOnly.Provider);
        CollectionAssert.Contains(tvdbOnly.Aliases.ToList(), "The Close Show");
    }

    [TestMethod]
    public void Confidence_UsesAliasAndExplainsEvidence()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "La Casa de Papel",
            Year = 2017
        };
        var candidate = new TmdbCandidate(71446, "TV", "Money Heist", 2017, "", "", "")
        {
            Aliases = ["La Casa de Papel"],
            Provider = MetadataProvider.TmdbAndTvdb,
            TvdbId = 327417
        };

        var score = TmdbClient.ScoreCandidate(item, candidate);

        Assert.IsTrue(score.ConfidencePercent >= 97);
        CollectionAssert.Contains(score.Evidence.ToList(), "Alias: La Casa de Papel");
        CollectionAssert.Contains(score.Evidence.ToList(), "Confirmed by TMDB and TVDB");
    }
}
