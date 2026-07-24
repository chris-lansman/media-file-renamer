using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;

var tempFile = Path.Combine(Path.GetTempPath(), "A Million Ways to Die in the West (2014).mp4");
File.WriteAllText(tempFile, "");

try
{
    var item = new MediaScanner().Scan([tempFile]).Single();
    AssertEqual("A Million Ways to Die in the West", item.TitleGuess, "movie title parse");
    AssertEqual(2014, item.Year, "movie year parse");

    var candidate = new TmdbCandidate(
        188161,
        "Movie",
        "A Million Ways to Die in the West",
        2014,
        "2014-05-30",
        "",
        "");

    var match = TmdbClient.FindAutoMatch(item, [candidate], confidenceThreshold: 92);
    AssertNotNull(match, "exact TMDB auto-match");
    AssertEqual(100, match!.ConfidencePercent, "exact TMDB confidence");

    TestRestorationReleaseNameParsing();
    TestNamedTvEpisodesUseShowFolder();
    TestExplicitTvFolderClassifiesTitleOnlyEpisodes();
    TestAlternateTvEpisodePattern();
    TestFileCountDoesNotForceTv();
    TestAmbiguousDatabaseTypesRequireReview();
    TestPlexMoviePathIncludesTmdbId();
    TestTvShowIdentityAppliesToRelatedEpisodes();
    TestMoveDeletesEmptySourceFolder();
    TestMovePreservesNonEmptySourceFolder();
    TestUnresolvedTvEpisodeIsNotMoved();

    Console.WriteLine("All tests passed.");
}
finally
{
    File.Delete(tempFile);
}

static void TestPlexMoviePathIncludesTmdbId()
{
    var item = new MediaPreviewItem
    {
        Extension = ".mkv",
        MediaType = "Movie",
        MatchedTitle = "Star Wars",
        Year = 1977,
        TmdbId = 11
    };

    var destination = new RenamePlanner().BuildDestination(
        item,
        @"C:\Output",
        RenamePreset.PlexStandard);

    var expected = Path.Combine(
        @"C:\Output",
        "Movies",
        "Star Wars (1977) {tmdb-11}",
        "Star Wars (1977) {tmdb-11}.mkv");
    AssertEqual(expected, destination, "Plex TMDB movie path");
}

static void TestTvShowIdentityAppliesToRelatedEpisodes()
{
    var firstEpisode = new MediaPreviewItem
    {
        Extension = ".mkv",
        MediaType = "TV",
        TitleGuess = "The 13 Ghosts of Scooby-Doo",
        MatchedTitle = "The 13 Ghosts of Scooby-Doo",
        Season = 1,
        Episode = 1
    };
    var secondEpisode = new MediaPreviewItem
    {
        Extension = ".mkv",
        MediaType = "TV",
        TitleGuess = "The 13 Ghosts of Scooby-Doo",
        MatchedTitle = "The 13 Ghosts of Scooby-Doo",
        Season = 1,
        Episode = 2
    };
    var identity = new TvShowIdentity(1069, "The 13 Ghosts of Scooby-Doo", 1985);

    if (!TvShowIdentityMatcher.IsRelatedEpisode(firstEpisode, secondEpisode))
    {
        throw new InvalidOperationException("TV show identity relation: expected sibling episodes to be related.");
    }

    TvShowIdentityMatcher.Apply(secondEpisode, identity);
    var destination = new RenamePlanner().BuildDestination(
        secondEpisode,
        @"C:\Output",
        RenamePreset.PlexStandard);

    var expected = Path.Combine(
        @"C:\Output",
        "TV Shows",
        "The 13 Ghosts of Scooby-Doo (1985) {tmdb-1069}",
        "Season 01",
        "The 13 Ghosts of Scooby-Doo - S01E02.mkv");
    AssertEqual(expected, destination, "Plex TMDB TV sibling path");
}

static void TestRestorationReleaseNameParsing()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var sourceFile = Path.Combine(root, "05-Star.Wars.4K77.2160p.UHD.no-DNR.35mm.x264.v1.0.mkv");
    File.WriteAllText(sourceFile, "");

    try
    {
        var item = new MediaScanner().Scan([sourceFile]).Single();
        AssertEqual("Star Wars", item.TitleGuess, "restoration release title parse");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestNamedTvEpisodesUseShowFolder()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    var sourceFolder = Path.Combine(root, "Scooby Doo, Where Are You! (TV Series 1969-1970)");
    Directory.CreateDirectory(sourceFolder);
    var sourceFile = Path.Combine(sourceFolder, "Episode 1 What a Night for a Knight.mkv");
    File.WriteAllText(sourceFile, "");

    try
    {
        var item = new MediaScanner().Scan([sourceFolder]).Single();
        AssertEqual("TV", item.MediaType, "named episode media type");
        AssertEqual("Scooby Doo, Where Are You!", item.TitleGuess, "named episode show title");
        AssertEqual(1969, item.Year, "named episode show year");
        AssertEqual(1, item.Episode, "named episode number");
        AssertEqual("What a Night for a Knight", item.EpisodeTitle, "named episode title");

        var candidate = new TmdbCandidate(926, "TV", "Scooby-Doo, Where Are You!", 1969, "1969-09-13", "", "");
        var match = TmdbClient.FindAutoMatch(item, [candidate], confidenceThreshold: 92);
        AssertNotNull(match, "named episode TMDB auto-match");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void TestExplicitTvFolderClassifiesTitleOnlyEpisodes()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    var sourceFolder = Path.Combine(root, "Short Show (TV Series 2021-2021)");
    Directory.CreateDirectory(sourceFolder);
    var sourceFile = Path.Combine(sourceFolder, "The Beginning.mkv");
    File.WriteAllText(sourceFile, "");

    try
    {
        var item = new MediaScanner().Scan([sourceFolder]).Single();
        AssertEqual("TV", item.MediaType, "explicit TV folder media type");
        AssertEqual("Short Show", item.TitleGuess, "explicit TV folder title");
        AssertEqual("The Beginning", item.EpisodeTitle, "title-only episode title");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestAlternateTvEpisodePattern()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var sourceFile = Path.Combine(root, "Small Show 1x03 The Visit.mkv");
    File.WriteAllText(sourceFile, "");

    try
    {
        var item = new MediaScanner().Scan([sourceFile]).Single();
        AssertEqual("TV", item.MediaType, "alternate TV media type");
        AssertEqual(1, item.Season, "alternate TV season");
        AssertEqual(3, item.Episode, "alternate TV episode");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestFileCountDoesNotForceTv()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "First Film.mkv"), "");
    File.WriteAllText(Path.Combine(root, "Second Film.mkv"), "");
    File.WriteAllText(Path.Combine(root, "Third Film.mkv"), "");

    try
    {
        var items = new MediaScanner().Scan([root]);
        AssertEqual(3, items.Count, "uncertain group file count");
        foreach (var item in items)
        {
            AssertEqual("Unknown", item.MediaType, "file count remains uncertain");
            AssertEqual(3, item.GroupFileCount, "group evidence count");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestAmbiguousDatabaseTypesRequireReview()
{
    var item = new MediaPreviewItem
    {
        MediaType = "Unknown",
        TitleGuess = "Shared Title",
        GroupFileCount = 3
    };
    var candidates = new[]
    {
        new TmdbCandidate(10, "TV", "Shared Title", 2020, "2020-01-01", "", ""),
        new TmdbCandidate(20, "Movie", "Shared Title", 2020, "2020-01-01", "", "")
    };

    var match = TmdbClient.FindAutoMatch(item, candidates, confidenceThreshold: 92);
    AssertEqual<TmdbAutoMatch?>(null, match, "ambiguous movie and TV match");
}

static void TestUnresolvedTvEpisodeIsNotMoved()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    var sourceFolder = Path.Combine(root, "Source");
    var outputFolder = Path.Combine(root, "Output");
    Directory.CreateDirectory(sourceFolder);
    var sourceFile = Path.Combine(sourceFolder, "Mystery Episode.mkv");
    File.WriteAllText(sourceFile, "test");

    try
    {
        var item = new MediaPreviewItem
        {
            SourcePath = sourceFile,
            Extension = ".mkv",
            MediaType = "TV",
            MatchedTitle = "Mystery Show",
            DestinationPath = Path.Combine(outputFolder, "Mystery Show - S00E00.mkv")
        };
        var result = new RenameApplier().Apply([item], FileOperation.Move);

        AssertEqual(true, File.Exists(sourceFile), "unresolved TV source preserved");
        AssertEqual(0, result.CompletedItems.Count, "unresolved TV move blocked");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestMoveDeletesEmptySourceFolder()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    var sourceFolder = Path.Combine(root, "Source Movie");
    var outputFolder = Path.Combine(root, "Output");
    Directory.CreateDirectory(sourceFolder);
    var sourceFile = Path.Combine(sourceFolder, "Movie (2024).mkv");
    File.WriteAllText(sourceFile, "test");

    try
    {
        var item = new MediaPreviewItem
        {
            SourcePath = sourceFile,
            Extension = ".mkv",
            MatchedTitle = "Movie",
            DestinationPath = Path.Combine(outputFolder, "Movie (2024).mkv")
        };
        var result = new RenameApplier().Apply([item], FileOperation.Move);

        AssertEqual(false, Directory.Exists(sourceFolder), "empty source folder deletion");
        AssertEqual(1, result.DeletedSourceFolders, "deleted source folder count");
        AssertEqual(1, result.CompletedItems.Count, "completed move count");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void TestMovePreservesNonEmptySourceFolder()
{
    var root = Path.Combine(Path.GetTempPath(), $"MediaFileRenamer-{Guid.NewGuid():N}");
    var sourceFolder = Path.Combine(root, "Source Movie");
    var outputFolder = Path.Combine(root, "Output");
    Directory.CreateDirectory(sourceFolder);
    var sourceFile = Path.Combine(sourceFolder, "Movie (2024).mkv");
    File.WriteAllText(sourceFile, "test");
    File.WriteAllText(Path.Combine(sourceFolder, "poster.jpg"), "keep");

    try
    {
        var item = new MediaPreviewItem
        {
            SourcePath = sourceFile,
            Extension = ".mkv",
            MatchedTitle = "Movie",
            DestinationPath = Path.Combine(outputFolder, "Movie (2024).mkv")
        };
        var result = new RenameApplier().Apply([item], FileOperation.Move);

        AssertEqual(true, Directory.Exists(sourceFolder), "non-empty source folder preservation");
        AssertEqual(0, result.DeletedSourceFolders, "preserved source folder count");
        AssertEqual(1, result.CompletedItems.Count, "completed preserved-folder move count");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected '{expected}', got '{actual}'.");
    }
}

static void AssertNotNull(object? value, string name)
{
    if (value is null)
    {
        throw new InvalidOperationException($"{name}: expected a value.");
    }
}
