using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Net;
using System.Reflection;
using System.Text;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class WindowSmokeTests
{
    [STATestMethod]
    public void MainWindow_ConstructsWithoutStartupException()
    {
        _ = System.Windows.Application.Current ?? new System.Windows.Application();
        var window = new MainWindow();

        Assert.AreEqual("Media File Renamer", window.Title);
        window.Close();
    }

    [STATestMethod]
    public void SettingsWindow_DisplaysSavedApiKeysAsPlainText()
    {
        _ = System.Windows.Application.Current ?? new System.Windows.Application();
        var settings = new AppSettings
        {
            TmdbApiKey = "visible-tmdb-key",
            TvdbApiKey = "visible-tvdb-key",
            TvdbPin = "visible-tvdb-pin"
        };
        var window = new SettingsWindow(settings, @"C:\settings.json");

        var tmdbBox = (System.Windows.Controls.TextBox)window.FindName("TmdbApiKeyBox");
        var tvdbBox = (System.Windows.Controls.TextBox)window.FindName("TvdbApiKeyBox");
        var tvdbPinBox = (System.Windows.Controls.TextBox)window.FindName("TvdbPinBox");
        Assert.AreEqual("visible-tmdb-key", tmdbBox.Text);
        Assert.AreEqual("visible-tvdb-key", tvdbBox.Text);
        Assert.AreEqual("visible-tvdb-pin", tvdbPinBox.Text);
        window.Close();
    }

    [STATestMethod]
    public void SettingsWindow_ScrollsFormWhenAvailableHeightIsLimited()
    {
        _ = System.Windows.Application.Current ?? new System.Windows.Application();
        var window = new SettingsWindow(new AppSettings(), @"C:\settings.json")
        {
            Height = 420
        };

        var scrollViewer = (System.Windows.Controls.ScrollViewer)window.FindName(
            "SettingsScrollViewer");
        scrollViewer.Measure(new System.Windows.Size(600, 120));
        scrollViewer.Arrange(new System.Windows.Rect(0, 0, 600, 120));
        scrollViewer.UpdateLayout();

        Assert.AreEqual(
            System.Windows.Controls.ScrollBarVisibility.Auto,
            scrollViewer.VerticalScrollBarVisibility);
        Assert.IsGreaterThan(0, scrollViewer.ScrollableHeight);
        window.Close();
    }

    [STATestMethod]
    public void MainWindow_DisablesApplyUntilEveryItemIsReviewed()
    {
        _ = System.Windows.Application.Current ?? new System.Windows.Application();
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Example Movie (2024).mkv");
        var window = new MainWindow();
        ((System.Windows.Controls.TextBox)window.FindName("OutputFolderTextBox")).Text =
            Path.Combine(temp.Path, "Output");

        var addSources = typeof(MainWindow).GetMethod(
            "AddSources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(addSources);
        addSources.Invoke(window, [new[] { source }]);

        var renameButton = (System.Windows.Controls.Button)window.FindName("RenameButton");
        var reviewCount = (System.Windows.Controls.TextBlock)window.FindName("ReviewCountTextBlock");
        Assert.IsFalse(renameButton.IsEnabled);
        Assert.AreEqual("1 item needs review", reviewCount.Text);

        window.PreviewItems.Single().Status = "Local movie choice";

        Assert.IsTrue(renameButton.IsEnabled);
        Assert.AreEqual("All items reviewed", reviewCount.Text);
        window.Close();
    }

    [STATestMethod]
    public void MatchPicker_WithoutApiKeyOffersLocalClassification()
    {
        _ = System.Windows.Application.Current ?? new System.Windows.Application();
        var item = new MediaPreviewItem
        {
            SourcePath = @"C:\Media\Unknown.mkv",
            TitleGuess = "Unknown"
        };
        var window = new MatchPickerWindow(item, null, item.TitleGuess, []);

        var searchBox = (System.Windows.Controls.TextBox)window.FindName("SearchTextBox");
        var searchButton = (System.Windows.Controls.Button)window.FindName("SearchButton");
        var searchStatus = (System.Windows.Controls.TextBlock)window.FindName("SearchStatusTextBlock");
        Assert.IsFalse(searchBox.IsEnabled);
        Assert.IsFalse(searchButton.IsEnabled);
        StringAssert.Contains(searchStatus.Text, "Use as Movie or Use as TV");
        window.Close();
    }
}

[TestClass]
public sealed class MediaPreviewItemTests
{
    [TestMethod]
    [DataRow("TMDB auto 100%", "Matched")]
    [DataRow("TMDB show match", "Matched")]
    [DataRow("TVDB episode fallback", "Matched")]
    [DataRow("Needs review", "Review needed")]
    [DataRow("TV matched; episode needs review", "Review needed")]
    [DataRow("TV matched; episode title needs review", "Review needed")]
    [DataRow("Type uncertain; matching folder and files", "Review needed")]
    [DataRow("Failed: destination file already exists", "Blocked")]
    [DataRow("Manual TMDB match", "Manual choice")]
    [DataRow("Ready", "Ready to match")]
    public void MatchState_SummarizesDetailedStatus(string status, string expected)
    {
        var item = new MediaPreviewItem { Status = status };

        Assert.AreEqual(expected, item.MatchState);
    }

    [TestMethod]
    public void MatchLabel_ShowsMetadataProvider()
    {
        var tmdbItem = new MediaPreviewItem { Status = "TMDB auto 100%" };
        var tvdbItem = new MediaPreviewItem { Status = "TVDB episode fallback" };

        Assert.AreEqual("Matched · TMDB", tmdbItem.MatchLabel);
        Assert.AreEqual("Matched · TVDB", tvdbItem.MatchLabel);
    }

    [TestMethod]
    public void RequiresReview_IsTrueUntilChoiceIsDeliberate()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "Movie",
            Status = "Parsed movie"
        };

        Assert.IsTrue(item.RequiresReview);

        item.Status = "Local movie choice";

        Assert.IsFalse(item.RequiresReview);
        Assert.AreEqual("Manual choice", item.MatchState);
    }

    [TestMethod]
    public void LocalTvChoice_StillRequiresResolvedEpisodeNumbers()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            Status = "Local TV choice"
        };

        Assert.IsTrue(item.RequiresReview);
        Assert.AreEqual("Review needed", item.MatchState);

        item.Season = 1;
        item.Episode = 2;

        Assert.IsFalse(item.RequiresReview);
        Assert.AreEqual("Manual choice", item.MatchState);
    }
}

[TestClass]
public sealed class MediaScannerTests
{
    [TestMethod]
    public void Scan_ParsesMovieTitleAndYear()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("A Million Ways to Die in the West (2014).mp4");

        var item = new MediaScanner().Scan([source]).Single();

        Assert.AreEqual("Movie", item.MediaType);
        Assert.AreEqual("A Million Ways to Die in the West", item.TitleGuess);
        Assert.AreEqual(2014, item.Year);
    }

    [TestMethod]
    public void Scan_RemovesCommonReleaseNoise()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("05-Star.Wars.4K77.2160p.UHD.no-DNR.35mm.x264.v1.0.mkv");

        var item = new MediaScanner().Scan([source]).Single();

        Assert.AreEqual("Star Wars", item.TitleGuess);
    }

    [TestMethod]
    public void Scan_UsesExplicitTvFolderForNamedEpisode()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("Scooby Doo, Where Are You! (TV Series 1969-1970)");
        temp.CreateFile(Path.Combine(folder, "Episode 1 What a Night for a Knight.mkv"));

        var item = new MediaScanner().Scan([folder]).Single();

        Assert.AreEqual("TV", item.MediaType);
        Assert.AreEqual("Scooby Doo, Where Are You!", item.TitleGuess);
        Assert.AreEqual(1969, item.Year);
        Assert.AreEqual(1, item.Episode);
        Assert.AreEqual("What a Night for a Knight", item.EpisodeTitle);
    }

    [TestMethod]
    public void Scan_UsesExplicitTvFolderForTitleOnlyEpisode()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("Short Show (TV Series 2021-2021)");
        temp.CreateFile(Path.Combine(folder, "The Beginning.mkv"));

        var item = new MediaScanner().Scan([folder]).Single();

        Assert.AreEqual("TV", item.MediaType);
        Assert.AreEqual("Short Show", item.TitleGuess);
        Assert.AreEqual("The Beginning", item.EpisodeTitle);
    }

    [TestMethod]
    [DataRow("Small Show S02E03 The Visit.mkv", 2, 3)]
    [DataRow("Small Show 2x03 The Visit.mkv", 2, 3)]
    public void Scan_RecognizesCommonEpisodePatterns(string fileName, int season, int episode)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile(fileName);

        var item = new MediaScanner().Scan([source]).Single();

        Assert.AreEqual("TV", item.MediaType);
        Assert.AreEqual(season, item.Season);
        Assert.AreEqual(episode, item.Episode);
    }

    [TestMethod]
    public void Scan_UsesParentSeriesForSpecialsFolderAndGenericFileName()
    {
        using var temp = new TempDirectory();
        var showFolder = temp.CreateDirectory("Curious George");
        var specialsFolder = temp.CreateDirectory(Path.Combine(showFolder, "Specials"));
        temp.CreateFile(Path.Combine(specialsFolder, "Specials - S00E07.mkv"));

        var item = new MediaScanner().Scan([showFolder]).Single();

        Assert.AreEqual("TV", item.MediaType);
        Assert.AreEqual("Curious George", item.TitleGuess);
        Assert.AreEqual(showFolder, item.SourceGroupPath);
        Assert.AreEqual(0, item.Season);
        Assert.AreEqual(7, item.Episode);
    }

    [TestMethod]
    public void Scan_PreservesEpisodeTitleAfterSeasonEpisodeCode()
    {
        using var temp = new TempDirectory();
        var showFolder = temp.CreateDirectory("Curious George");
        var specialsFolder = temp.CreateDirectory(Path.Combine(showFolder, "Specials"));
        temp.CreateFile(Path.Combine(
            specialsFolder,
            "S00E08 - Curious George Goes to the Hospital.mkv"));

        var item = new MediaScanner().Scan([showFolder]).Single();

        Assert.AreEqual("Curious George", item.TitleGuess);
        Assert.AreEqual("Curious George Goes to the Hospital", item.EpisodeTitle);
        Assert.AreEqual(0, item.Season);
        Assert.AreEqual(8, item.Episode);
    }

    [TestMethod]
    public void Scan_FileCountAloneDoesNotForceTv()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("First Film.mkv");
        temp.CreateFile("Second Film.mkv");
        temp.CreateFile("Third Film.mkv");

        var items = new MediaScanner().Scan([temp.Path]);

        Assert.AreEqual(3, items.Count);
        Assert.IsTrue(items.All(item => item.MediaType == "Unknown"));
        Assert.IsTrue(items.All(item => item.GroupFileCount == 3));
    }

    [TestMethod]
    public void Scan_IncludesCommonPlexVideoExtensions()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Disc.m2ts");
        temp.CreateFile("Clip.webm");

        var items = new MediaScanner().Scan([temp.Path]);

        Assert.AreEqual(2, items.Count);
    }
}

[TestClass]
public sealed class MatchingTests
{
    [TestMethod]
    public async Task SearchCandidates_ReportsRejectedApiKey()
    {
        using var httpClient = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = new TmdbClient("bad-key", httpClient);
        var item = new MediaPreviewItem { MediaType = "Movie", TitleGuess = "Film" };

        var exception = await Assert.ThrowsAsync<MetadataLookupException>(
            () => client.SearchCandidatesAsync(item));

        StringAssert.Contains(exception.Message, "rejected the API key");
    }

    [TestMethod]
    public async Task SearchCandidates_DistinguishesNoResultsFromFailure()
    {
        using var httpClient = new HttpClient(new StubHttpHandler(_ => TestHttp.JsonResponse("{\"results\":[]}")));
        var client = new TmdbClient("key", httpClient);
        var item = new MediaPreviewItem { MediaType = "Movie", TitleGuess = "No Such Film" };

        var candidates = await client.SearchCandidatesAsync(item);

        Assert.AreEqual(0, candidates.Count);
    }

    [TestMethod]
    public async Task BuildMatch_ResolvesBothSeasonZeroEpisodeTitles()
    {
        var requestedPaths = new List<string>();
        using var httpClient = new HttpClient(new StubHttpHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            return TestHttp.JsonResponse(
                """
                {
                  "episodes": [
                    {
                      "name": "Curious George Comes to America",
                      "season_number": 0,
                      "episode_number": 7
                    },
                    {
                      "name": "Curious George Goes to the Hospital",
                      "season_number": 0,
                      "episode_number": 8
                    }
                  ]
                }
                """);
        }));
        var client = new TmdbClient("key", httpClient);
        var firstItem = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Curious George",
            Season = 0,
            Episode = 7
        };
        var secondItem = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Curious George",
            Season = 0,
            Episode = 8
        };
        var candidate = new TmdbCandidate(
            656,
            "TV",
            "Curious George",
            2006,
            "2006-09-04",
            "",
            "");

        var firstMatch = await client.BuildMatchAsync(candidate, firstItem);
        var secondMatch = await client.BuildMatchAsync(candidate, secondItem);

        Assert.AreEqual("Curious George Comes to America", firstMatch.EpisodeTitle);
        Assert.AreEqual(0, firstMatch.Season);
        Assert.AreEqual(7, firstMatch.Episode);
        Assert.AreEqual("Curious George Goes to the Hospital", secondMatch.EpisodeTitle);
        Assert.AreEqual(0, secondMatch.Season);
        Assert.AreEqual(8, secondMatch.Episode);
        CollectionAssert.Contains(requestedPaths, "/3/tv/656/season/0");
        Assert.HasCount(1, requestedPaths);
    }

    [TestMethod]
    public async Task BuildMatch_TitleLookupIncludesSeasonZero()
    {
        var requestedPaths = new List<string>();
        using var httpClient = new HttpClient(new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requestedPaths.Add(path);
            return path.EndsWith("/tv/656", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"number_of_seasons":0}""")
                : TestHttp.JsonResponse(
                    """
                    {
                      "episodes": [
                        {
                          "name": "Curious George Comes to America",
                          "season_number": 0,
                          "episode_number": 7
                        }
                      ]
                    }
                    """);
        }));
        var client = new TmdbClient("key", httpClient);
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Curious George",
            EpisodeTitle = "Curious George Comes to America"
        };
        var candidate = new TmdbCandidate(
            656,
            "TV",
            "Curious George",
            2006,
            "2006-09-04",
            "",
            "");

        var match = await client.BuildMatchAsync(candidate, item);

        Assert.AreEqual("Curious George Comes to America", match.EpisodeTitle);
        Assert.AreEqual(0, match.Season);
        Assert.AreEqual(7, match.Episode);
        CollectionAssert.Contains(requestedPaths, "/3/tv/656/season/0");
        Assert.HasCount(2, requestedPaths);
    }

    [TestMethod]
    public void FindAutoMatch_AcceptsExactTitleAndYear()
    {
        var item = new MediaPreviewItem
        {
            MediaType = "Movie",
            TitleGuess = "A Million Ways to Die in the West",
            Year = 2014
        };
        var candidate = new TmdbCandidate(188161, "Movie", item.TitleGuess, 2014, "2014-05-30", "", "");

        var match = TmdbClient.FindAutoMatch(item, [candidate], 92);

        Assert.IsNotNull(match);
        Assert.AreEqual(100, match.ConfidencePercent);
    }

    [TestMethod]
    public void FindAutoMatch_RequiresReviewForMovieTvTie()
    {
        var item = new MediaPreviewItem { MediaType = "Unknown", TitleGuess = "Shared Title", GroupFileCount = 3 };
        var candidates = new[]
        {
            new TmdbCandidate(10, "TV", "Shared Title", 2020, "2020-01-01", "", ""),
            new TmdbCandidate(20, "Movie", "Shared Title", 2020, "2020-01-01", "", "")
        };

        Assert.IsNull(TmdbClient.FindAutoMatch(item, candidates, 92));
    }

    [TestMethod]
    public void RelatedEpisodes_DoNotCrossSeparateSourceFolders()
    {
        var first = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Same Show",
            SourceGroupPath = @"C:\One"
        };
        var second = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Same Show",
            SourceGroupPath = @"C:\Two"
        };

        Assert.IsFalse(TvShowIdentityMatcher.IsRelatedEpisode(first, second));
    }
}

[TestClass]
public sealed class TvdbFallbackTests
{
    [TestMethod]
    public async Task FindEpisode_UsesDefaultOrderExactCoordinatesAndCachesResult()
    {
        var requests = new List<CapturedRequest>();
        using var httpClient = new HttpClient(new StubHttpHandler(request =>
        {
            requests.Add(CapturedRequest.From(request));
            return request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"data":{"token":"tvdb-token"}}""")
                : TestHttp.JsonResponse(
                    """
                    {
                      "data": {
                        "episodes": [
                          {
                            "id": 7007,
                            "name": "Curious George Comes to America",
                            "seasonNumber": 0,
                            "number": 7
                          }
                        ]
                      }
                    }
                    """);
        }));
        var client = new TvdbClient("tvdb-key", "subscriber-pin", httpClient);

        var first = await client.FindEpisodeAsync(123, 0, 7);
        var second = await client.FindEpisodeAsync(123, 0, 7);

        Assert.IsNotNull(first);
        Assert.AreEqual("Curious George Comes to America", first.Name);
        Assert.AreEqual(0, first.Season);
        Assert.AreEqual(7, first.Episode);
        Assert.AreSame(first, second);
        Assert.HasCount(2, requests);
        StringAssert.Contains(requests[0].Body, "\"apikey\":\"tvdb-key\"");
        StringAssert.Contains(requests[0].Body, "\"pin\":\"subscriber-pin\"");
        Assert.AreEqual(
            "/v4/series/123/episodes/default?page=0&season=0&episodeNumber=7",
            requests[1].PathAndQuery);
        Assert.AreEqual("Bearer", requests[1].AuthorizationScheme);
        Assert.AreEqual("tvdb-token", requests[1].AuthorizationParameter);
    }

    [TestMethod]
    public async Task FindEpisode_RejectsResponseWithDifferentCoordinates()
    {
        using var httpClient = new HttpClient(new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"data":{"token":"tvdb-token"}}""")
                : TestHttp.JsonResponse(
                    """
                    {
                      "data": {
                        "episodes": [
                          {
                            "id": 1001,
                            "name": "Different Episode",
                            "seasonNumber": 1,
                            "number": 1
                          }
                        ]
                      }
                    }
                    """)));
        var client = new TvdbClient("tvdb-key", httpClient: httpClient);

        var match = await client.FindEpisodeAsync(123, 0, 7);

        Assert.IsNull(match);
    }

    [TestMethod]
    public async Task Resolver_UsesTvdbWhenTmdbEpisodeIsMissing()
    {
        var tmdbRequests = new List<string>();
        using var tmdbHttpClient = new HttpClient(new StubHttpHandler(request =>
        {
            tmdbRequests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/external_ids", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"tvdb_id":123}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var tvdbHttpClient = new HttpClient(new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"data":{"token":"tvdb-token"}}""")
                : TestHttp.JsonResponse(
                    """
                    {
                      "data": {
                        "episodes": [
                          {
                            "id": 7007,
                            "name": "Curious George Comes to America",
                            "seasonNumber": 0,
                            "number": 7
                          }
                        ]
                      }
                    }
                    """)));
        var tmdbClient = new TmdbClient("tmdb-key", tmdbHttpClient);
        var tvdbClient = new TvdbClient("tvdb-key", httpClient: tvdbHttpClient);
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Curious George",
            Season = 0,
            Episode = 7
        };
        var candidate = new TmdbCandidate(
            656,
            "TV",
            "Curious George",
            2006,
            "2006-09-04",
            "",
            "");

        var resolution = await new TvEpisodeMetadataResolver().ResolveAsync(
            tmdbClient,
            tvdbClient,
            candidate,
            item);

        Assert.AreEqual(EpisodeMetadataSource.Tvdb, resolution.EpisodeSource);
        Assert.AreEqual(
            "Curious George Comes to America",
            resolution.Match.EpisodeTitle);
        CollectionAssert.Contains(tmdbRequests, "/3/tv/656/external_ids");
    }

    [TestMethod]
    public async Task Resolver_LeavesConflictingFilenameTitleForReview()
    {
        using var tmdbHttpClient = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var tvdbHttpClient = new HttpClient(new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"data":{"token":"tvdb-token"}}""")
                : TestHttp.JsonResponse(
                    """
                    {
                      "data": {
                        "episodes": [
                          {
                            "id": 7007,
                            "name": "Canonical Episode Title",
                            "seasonNumber": 0,
                            "number": 7
                          }
                        ]
                      }
                    }
                    """)));
        var tmdbClient = new TmdbClient("tmdb-key", tmdbHttpClient);
        var tvdbClient = new TvdbClient("tvdb-key", httpClient: tvdbHttpClient);
        var item = new MediaPreviewItem
        {
            MediaType = "TV",
            TitleGuess = "Show",
            Season = 0,
            Episode = 7,
            EpisodeTitle = "A Different Episode"
        };
        var candidate = new TmdbCandidate(
            10,
            "TV",
            "Show",
            2020,
            "2020-01-01",
            "",
            "")
        {
            TvdbId = 123
        };

        var resolution = await new TvEpisodeMetadataResolver().ResolveAsync(
            tmdbClient,
            tvdbClient,
            candidate,
            item);

        Assert.AreEqual(EpisodeMetadataSource.Tmdb, resolution.EpisodeSource);
        Assert.IsNull(resolution.Match.EpisodeTitle);
    }
}

[TestClass]
public sealed class RenamePlannerTests
{
    [TestMethod]
    public void PlexMoviePath_IncludesTmdbId()
    {
        var destination = new RenamePlanner().BuildDestination(
            Movie("Star Wars", 1977, 11),
            @"C:\Output",
            RenamePreset.PlexStandard);

        Assert.AreEqual(
            @"C:\Output\Movies\Star Wars (1977) {tmdb-11}\Star Wars (1977) {tmdb-11}.mkv",
            destination);
    }

    [TestMethod]
    public void PlexTvPath_IncludesSeasonEpisodeAndTmdbId()
    {
        var item = new MediaPreviewItem
        {
            Extension = ".mkv",
            MediaType = "TV",
            MatchedTitle = "The 13 Ghosts of Scooby-Doo",
            Year = 1985,
            Season = 1,
            Episode = 2,
            EpisodeTitle = "A Spooky Night",
            TmdbId = 1069
        };

        var destination = new RenamePlanner().BuildDestination(item, @"C:\Output", RenamePreset.PlexStandard);

        Assert.AreEqual(
            @"C:\Output\TV Shows\The 13 Ghosts of Scooby-Doo (1985) {tmdb-1069}\Season 01\The 13 Ghosts of Scooby-Doo - S01E02 - A Spooky Night.mkv",
            destination);
    }

    [TestMethod]
    public void CustomPath_RejectsParentTraversal()
    {
        Assert.Throws<ArgumentException>(() => new RenamePlanner().BuildDestination(
            Movie("Film", 2024, 1), @"C:\Output", RenamePreset.Custom, @"..\Escape\{Title}"));
    }

    [TestMethod]
    public void CustomPath_RejectsRootedPath()
    {
        Assert.Throws<ArgumentException>(() => new RenamePlanner().BuildDestination(
            Movie("Film", 2024, 1), @"C:\Output", RenamePreset.Custom, @"D:\Escape\{Title}"));
    }

    [TestMethod]
    public void OutputRoot_MustBeAbsolute()
    {
        Assert.Throws<ArgumentException>(() => new RenamePlanner().BuildDestination(
            Movie("Film", 2024, 1), "relative-output", RenamePreset.PlexStandard));
    }

    [TestMethod]
    public void ReservedWindowsTitle_IsMadeSafe()
    {
        var destination = new RenamePlanner().BuildDestination(
            Movie("CON", 2024, 1), @"C:\Output", RenamePreset.PlexStandard);

        StringAssert.Contains(destination, @"\CON_ (2024) {tmdb-1}\");
    }

    [TestMethod]
    public void UnknownMedia_IsStagedForReview()
    {
        var item = new MediaPreviewItem
        {
            Extension = ".mkv",
            MediaType = "Unknown",
            MatchedTitle = "Unclear Title"
        };

        var destination = new RenamePlanner().BuildDestination(item, @"C:\Output", RenamePreset.PlexStandard);

        Assert.AreEqual(@"C:\Output\Review Needed\Unclear Title.mkv", destination);
    }

    private static MediaPreviewItem Movie(string title, int year, int tmdbId) => new()
    {
        Extension = ".mkv",
        MediaType = "Movie",
        MatchedTitle = title,
        Year = year,
        TmdbId = tmdbId
    };
}

[TestClass]
public sealed class RenameApplierTests
{
    [TestMethod]
    public void Move_DeletesEmptySourceTree()
    {
        using var temp = new TempDirectory();
        var sourceFolder = temp.CreateDirectory("Source Movie");
        temp.CreateDirectory(Path.Combine("Source Movie", "Empty", "Nested"));
        var source = temp.CreateFile(Path.Combine(sourceFolder, "Movie.mkv"), "test");
        var destination = Path.Combine(temp.Path, "Output", "Movie.mkv");

        var result = new RenameApplier().Apply([Item(source, destination)], FileOperation.Move);

        Assert.IsFalse(Directory.Exists(sourceFolder));
        Assert.IsTrue(File.Exists(destination));
        Assert.AreEqual(1, result.DeletedSourceFolders);
        Assert.AreEqual(1, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Move_PreservesSourceFolderContainingAnotherFile()
    {
        using var temp = new TempDirectory();
        var sourceFolder = temp.CreateDirectory("Source Movie");
        var source = temp.CreateFile(Path.Combine(sourceFolder, "Movie.mkv"), "test");
        temp.CreateFile(Path.Combine(sourceFolder, "poster.jpg"), "keep");

        var result = new RenameApplier().Apply(
            [Item(source, Path.Combine(temp.Path, "Output", "Movie.mkv"))], FileOperation.Move);

        Assert.IsTrue(Directory.Exists(sourceFolder));
        Assert.AreEqual(0, result.DeletedSourceFolders);
    }

    [TestMethod]
    public void Copy_PreservesSourceFile()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Movie.mkv", "test");
        var destination = Path.Combine(temp.Path, "Output", "Movie.mkv");

        var result = new RenameApplier().Apply([Item(source, destination)], FileOperation.Copy);

        Assert.IsTrue(File.Exists(source));
        Assert.IsTrue(File.Exists(destination));
        Assert.AreEqual(1, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Apply_DoesNotOverwriteExistingDestination()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Source.mkv", "source");
        var destination = temp.CreateFile(Path.Combine("Output", "Movie.mkv"), "existing");

        var result = new RenameApplier().Apply([Item(source, destination)], FileOperation.Move);

        Assert.IsTrue(File.Exists(source));
        Assert.AreEqual("existing", File.ReadAllText(destination));
        Assert.AreEqual(0, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Apply_BlocksDuplicatePlannedDestinations()
    {
        using var temp = new TempDirectory();
        var first = temp.CreateFile("First.mkv", "first");
        var second = temp.CreateFile("Second.mkv", "second");
        var destination = Path.Combine(temp.Path, "Output", "Same.mkv");

        var result = new RenameApplier().Apply(
            [Item(first, destination), Item(second, destination)], FileOperation.Move);

        Assert.IsTrue(File.Exists(first));
        Assert.IsTrue(File.Exists(second));
        Assert.AreEqual(0, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Apply_TreatsIdenticalSourceAndDestinationAsNoOp()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Already Named.mkv", "test");
        var item = Item(source, source);

        var result = new RenameApplier().Apply([item], FileOperation.Move);

        Assert.IsTrue(File.Exists(source));
        Assert.AreEqual("Already named", item.Status);
        Assert.AreEqual(1, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Apply_BlocksUnresolvedTvEpisode()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Episode.mkv", "test");
        var item = Item(source, Path.Combine(temp.Path, "Output", "Show - S00E00.mkv"));
        item.MediaType = "TV";

        var result = new RenameApplier().Apply([item], FileOperation.Move);

        Assert.IsTrue(File.Exists(source));
        Assert.AreEqual(0, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Apply_BlocksUnknownMediaType()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Unknown.mkv", "test");
        var item = Item(source, Path.Combine(temp.Path, "Output", "Unknown.mkv"));
        item.MediaType = "Unknown";

        var result = new RenameApplier().Apply([item], FileOperation.Move);

        Assert.IsTrue(File.Exists(source));
        Assert.AreEqual(0, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Apply_BlocksEntireBatchWhenOneItemRequiresReview()
    {
        using var temp = new TempDirectory();
        var safeSource = temp.CreateFile("Matched.mkv", "safe");
        var reviewSource = temp.CreateFile("Ambiguous.mkv", "review");
        var safeItem = Item(safeSource, Path.Combine(temp.Path, "Output", "Matched.mkv"));
        var reviewItem = Item(reviewSource, Path.Combine(temp.Path, "Output", "Ambiguous.mkv"));
        reviewItem.Status = "Needs review";

        var result = new RenameApplier().Apply([safeItem, reviewItem], FileOperation.Move);

        Assert.IsTrue(File.Exists(safeSource));
        Assert.IsTrue(File.Exists(reviewSource));
        Assert.IsFalse(File.Exists(safeItem.DestinationPath));
        Assert.IsFalse(File.Exists(reviewItem.DestinationPath));
        Assert.AreEqual(0, result.CompletedItems.Count);
    }

    [TestMethod]
    public void Apply_AllowsMatchedAndManualItemsInSameBatch()
    {
        using var temp = new TempDirectory();
        var matchedSource = temp.CreateFile("Matched.mkv", "matched");
        var manualSource = temp.CreateFile("Manual.mkv", "manual");
        var matchedItem = Item(matchedSource, Path.Combine(temp.Path, "Output", "Matched.mkv"));
        var manualItem = Item(manualSource, Path.Combine(temp.Path, "Output", "Manual.mkv"));
        manualItem.Status = "Local movie choice";

        var result = new RenameApplier().Apply([matchedItem, manualItem], FileOperation.Copy);

        Assert.AreEqual(2, result.CompletedItems.Count);
        Assert.IsTrue(File.Exists(matchedItem.DestinationPath));
        Assert.IsTrue(File.Exists(manualItem.DestinationPath));
    }

    private static MediaPreviewItem Item(string source, string destination) => new()
    {
        SourcePath = source,
        Extension = ".mkv",
        MediaType = "Movie",
        MatchedTitle = "Movie",
        DestinationPath = destination,
        Status = "TMDB match"
    };
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");

    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public string CreateDirectory(string relativePath)
    {
        var path = System.IO.Path.IsPathFullyQualified(relativePath)
            ? relativePath
            : System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string contents = "")
    {
        var path = System.IO.Path.IsPathFullyQualified(relativePath)
            ? relativePath
            : System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }
}

internal sealed record CapturedRequest(
    string Method,
    string PathAndQuery,
    string AuthorizationScheme,
    string AuthorizationParameter,
    string Body)
{
    public static CapturedRequest From(HttpRequestMessage request)
    {
        return new CapturedRequest(
            request.Method.Method,
            request.RequestUri?.PathAndQuery ?? "",
            request.Headers.Authorization?.Scheme ?? "",
            request.Headers.Authorization?.Parameter ?? "",
            request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
    }
}

internal static class TestHttp
{
    public static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
