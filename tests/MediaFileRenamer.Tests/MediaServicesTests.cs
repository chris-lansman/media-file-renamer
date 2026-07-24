using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Net;
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
            TvdbApiKey = "visible-tvdb-key"
        };
        var window = new SettingsWindow(settings, @"C:\settings.json");

        var tmdbBox = (System.Windows.Controls.TextBox)window.FindName("TmdbApiKeyBox");
        var tvdbBox = (System.Windows.Controls.TextBox)window.FindName("TvdbApiKeyBox");
        Assert.AreEqual("visible-tmdb-key", tmdbBox.Text);
        Assert.AreEqual("visible-tvdb-key", tvdbBox.Text);
        window.Close();
    }
}

[TestClass]
public sealed class MediaPreviewItemTests
{
    [TestMethod]
    [DataRow("TMDB auto 100%", "Matched")]
    [DataRow("TMDB show match", "Matched")]
    [DataRow("Needs review", "Review needed")]
    [DataRow("TV matched; episode needs review", "Review needed")]
    [DataRow("Type uncertain; matching folder and files", "Review needed")]
    [DataRow("Failed: destination file already exists", "Blocked")]
    [DataRow("Manual TMDB match", "Manual choice")]
    [DataRow("Ready", "Ready to match")]
    public void MatchState_SummarizesDetailedStatus(string status, string expected)
    {
        var item = new MediaPreviewItem { Status = status };

        Assert.AreEqual(expected, item.MatchState);
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

    private static MediaPreviewItem Item(string source, string destination) => new()
    {
        SourcePath = source,
        Extension = ".mkv",
        MediaType = "Movie",
        MatchedTitle = "Movie",
        DestinationPath = destination
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
