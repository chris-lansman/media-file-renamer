using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Net;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class MetadataDiskCacheTests
{
    [TestMethod]
    public void Set_RoundTripsAcrossInstancesAndLeavesNoTemporaryFile()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var key = MetadataCacheKey.Search("TMDB", "TV", "  Curious   George ");

        new MetadataDiskCache(path).Set(key, new CacheTestValue("Curious George", 656));
        var found = new MetadataDiskCache(path).TryGet<CacheTestValue>(key, out var value);

        Assert.IsTrue(found);
        Assert.IsNotNull(value);
        Assert.AreEqual("Curious George", value.Title);
        Assert.AreEqual(656, value.Id);
        Assert.IsFalse(File.Exists(path + ".tmp"));
        StringAssert.Contains(File.ReadAllText(path), "tmdb|search-tv|curious george");
    }

    [TestMethod]
    public void TryGet_ExpiresEntryAndRepairsFile()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var cache = new MetadataDiskCache(path, utcNow: () => now);
        var key = MetadataCacheKey.ResourceById("tmdb", "series", 10);
        cache.Set(key, "value", TimeSpan.FromMinutes(5));

        now = now.AddMinutes(6);
        var found = cache.TryGet<string>(key, out _);

        Assert.IsFalse(found);
        Assert.AreEqual(0, cache.Count);
        Assert.IsFalse(File.ReadAllText(path).Contains("\"value\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Load_QuarantinesCorruptFileAndContinuesCaching()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        File.WriteAllText(path, "{ definitely not valid JSON");
        var cache = new MetadataDiskCache(path);
        var key = MetadataCacheKey.ResourceById("tvdb", "series", 20);

        Assert.IsFalse(cache.TryGet<string>(key, out _));
        Assert.IsFalse(File.Exists(path));
        Assert.HasCount(1, Directory.GetFiles(directory.Path, "metadata-cache.json.corrupt-*"));

        cache.Set(key, "recovered");
        Assert.IsTrue(cache.TryGet<string>(key, out var recovered));
        Assert.AreEqual("recovered", recovered);
    }

    [TestMethod]
    public void Set_EvictsOldestEntriesAtEntryBound()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var cache = new MetadataDiskCache(
            path,
            new MetadataCacheOptions { MaxEntries = 2 },
            () => now);
        var first = MetadataCacheKey.ResourceById("tmdb", "series", 1);
        var second = MetadataCacheKey.ResourceById("tmdb", "series", 2);
        var third = MetadataCacheKey.ResourceById("tmdb", "series", 3);

        cache.Set(first, "first");
        now = now.AddSeconds(1);
        cache.Set(second, "second");
        now = now.AddSeconds(1);
        cache.Set(third, "third");

        Assert.AreEqual(2, cache.Count);
        Assert.IsFalse(cache.TryGet<string>(first, out _));
        Assert.IsTrue(cache.TryGet<string>(second, out _));
        Assert.IsTrue(cache.TryGet<string>(third, out _));
    }

    [TestMethod]
    public void Set_EnforcesSerializedFileSizeBound()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var cache = new MetadataDiskCache(
            path,
            new MetadataCacheOptions
            {
                MaxEntries = 10,
                MaxFileBytes = 512
            });

        cache.Set(
            MetadataCacheKey.ResourceById("tmdb", "oversized", 1),
            new string('x', 2_000));

        Assert.IsTrue(new FileInfo(path).Length <= 512);
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void EpisodeKey_DistinguishesProviderOrderAndCoordinates()
    {
        var defaultKey = MetadataCacheKey.Episode(
            "tvdb",
            123,
            EpisodeOrder.Default,
            0,
            7);
        var officialKey = MetadataCacheKey.Episode(
            "tvdb",
            123,
            EpisodeOrder.Official,
            0,
            7);
        var tmdbKey = MetadataCacheKey.Episode(
            "tmdb",
            123,
            EpisodeOrder.Default,
            0,
            7);

        Assert.AreNotEqual(defaultKey.Canonical, officialKey.Canonical);
        Assert.AreNotEqual(defaultKey.Canonical, tmdbKey.Canonical);
        Assert.AreEqual(
            "tvdb|episode|123:default:0:7:-",
            defaultKey.Canonical);
    }

    [TestMethod]
    public void MultipleInstances_MergeWritesWithoutLosingEntries()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var firstCache = new MetadataDiskCache(path);
        var secondCache = new MetadataDiskCache(path);
        var first = MetadataCacheKey.Search("tmdb", "movie", "First");
        var second = MetadataCacheKey.Search("tvdb", "tv", "Second");

        firstCache.Set(first, 1);
        secondCache.Set(second, 2);

        Assert.IsTrue(firstCache.TryGet<int>(first, out var firstValue));
        Assert.AreEqual(1, firstValue);
        Assert.IsTrue(firstCache.TryGet<int>(second, out var secondValue));
        Assert.AreEqual(2, secondValue);
    }

    [TestMethod]
    public async Task TmdbSearch_PersistsAcrossClientsWithoutStoringApiKey()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var firstRequests = 0;
        using var firstHttp = new HttpClient(new StubHttpHandler(_ =>
        {
            firstRequests++;
            return TestHttp.JsonResponse(
                """{"results":[{"id":656,"name":"Curious George","first_air_date":"2006-09-04"}]}""");
        }));
        var firstClient = new TmdbClient(
            "personal-tmdb-key",
            firstHttp,
            new MetadataDiskCache(path));
        var item = new MediaPreviewItem { MediaType = "TV", TitleGuess = "Curious George" };

        var first = await firstClient.SearchCandidatesAsync(item);

        var secondRequests = 0;
        using var secondHttp = new HttpClient(new StubHttpHandler(_ =>
        {
            secondRequests++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));
        var secondClient = new TmdbClient(
            "different-personal-key",
            secondHttp,
            new MetadataDiskCache(path));
        var second = await secondClient.SearchCandidatesAsync(item);

        Assert.AreEqual(1, firstRequests);
        Assert.AreEqual(0, secondRequests);
        Assert.AreEqual(656, first.Single().TmdbId);
        Assert.AreEqual(656, second.Single().TmdbId);
        var cacheJson = File.ReadAllText(path);
        Assert.IsFalse(cacheJson.Contains("personal-tmdb-key", StringComparison.Ordinal));
        Assert.IsFalse(cacheJson.Contains("different-personal-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TvdbEpisode_PersistsAcrossClientsWithoutRepeatingLogin()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var firstRequests = 0;
        using var firstHttp = new HttpClient(new StubHttpHandler(request =>
        {
            firstRequests++;
            return request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? TestHttp.JsonResponse("""{"data":{"token":"short-lived-token"}}""")
                : TestHttp.JsonResponse(
                    """{"data":{"episodes":[{"id":7007,"name":"Comes to America","seasonNumber":0,"number":7}]}}""");
        }));
        var firstClient = new TvdbClient(
            "personal-tvdb-key",
            httpClient: firstHttp,
            metadataCache: new MetadataDiskCache(path));

        var first = await firstClient.FindEpisodeAsync(123, 0, 7);

        var secondRequests = 0;
        using var secondHttp = new HttpClient(new StubHttpHandler(_ =>
        {
            secondRequests++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));
        var secondClient = new TvdbClient(
            "another-key",
            httpClient: secondHttp,
            metadataCache: new MetadataDiskCache(path));
        var second = await secondClient.FindEpisodeAsync(123, 0, 7);

        Assert.AreEqual(2, firstRequests);
        Assert.AreEqual(0, secondRequests);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual("Comes to America", second.Name);
        var cacheJson = File.ReadAllText(path);
        Assert.IsFalse(cacheJson.Contains("personal-tvdb-key", StringComparison.Ordinal));
        Assert.IsFalse(cacheJson.Contains("short-lived-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CachedProviderLookup_StillHonorsCancellation()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "metadata-cache.json");
        var cache = new MetadataDiskCache(path);
        cache.Set(
            MetadataCacheKey.Search("tmdb", "tv", "Show"),
            new List<TmdbCandidate>
            {
                new(1, "TV", "Show", 2020, "", "", "")
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new TmdbClient(
            "key",
            new HttpClient(new StubHttpHandler(_ =>
                throw new InvalidOperationException("No request expected."))),
            cache);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.SearchCandidatesAsync(
                new MediaPreviewItem { MediaType = "TV", TitleGuess = "Show" },
                cancellationToken: cancellation.Token));
    }

    private sealed record CacheTestValue(string Title, int Id);
}
