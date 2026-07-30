using MediaFileRenamer.App.Services;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class SettingsProductionTests
{
    [TestMethod]
    public void Load_NormalizesMissingAndOutOfRangeValues()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            path,
            """
            {
              "TmdbApiKey": null,
              "TvdbApiKey": null,
              "TvdbPin": null,
              "AutoMatchConfidencePercent": 150,
              "DefaultOutputFolder": ""
            }
            """);

        var settings = new AppSettingsService(path).Load();

        Assert.AreEqual("", settings.TmdbApiKey);
        Assert.AreEqual("", settings.TvdbApiKey);
        Assert.AreEqual("", settings.TvdbPin);
        Assert.AreEqual(100, settings.AutoMatchConfidencePercent);
        Assert.IsFalse(string.IsNullOrWhiteSpace(settings.DefaultOutputFolder));
    }

    [TestMethod]
    public void Save_ReplacesExistingSettingsAndLeavesNoTemporaryFile()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{}");
        var service = new AppSettingsService(path);

        service.Save(new AppSettings { TmdbApiKey = "personal-key" });

        Assert.AreEqual("personal-key", service.Load().TmdbApiKey);
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod]
    public void Save_RoundTripsDefaultOperationAsReadableJson()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var service = new AppSettingsService(path);

        service.Save(new AppSettings { DefaultOperation = FileOperation.Copy });

        Assert.AreEqual(FileOperation.Copy, service.Load().DefaultOperation);
        StringAssert.Contains(File.ReadAllText(path), "\"DefaultOperation\": \"Copy\"");
    }

    [TestMethod]
    public void ShowMapping_RoundTripsAndMatchesNormalizedPathIdentity()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var source = Path.Combine(directory.Path, "TV", "Curious George");
        var service = new AppSettingsService(path);

        var saved = service.UpsertShowMapping(new SavedShowMapping
        {
            SourcePath = source + Path.DirectorySeparatorChar,
            Title = " Curious George ",
            MediaType = "tv",
            TmdbId = 656,
            TvdbId = 79429,
            EpisodeOrder = EpisodeOrder.Default
        });

        var found = service.FindShowMapping(source.ToUpperInvariant());
        Assert.IsNotNull(found);
        Assert.AreEqual("Curious George", found.Title);
        Assert.AreEqual("TV", found.MediaType);
        Assert.AreEqual(656, found.TmdbId);
        Assert.AreEqual(79429, found.TvdbId);
        Assert.AreEqual(EpisodeOrder.Default, found.EpisodeOrder);
        Assert.AreEqual(
            AppSettingsService.NormalizeSourceIdentity(source),
            saved.SourceIdentity);
        Assert.AreEqual(AppSettingsService.CurrentSchemaVersion, service.Load().SchemaVersion);
    }

    [TestMethod]
    public void ShowMapping_UpsertReplacesIdentityAndRemovePersists()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var source = Path.Combine(directory.Path, "TV", "Show");
        var service = new AppSettingsService(path);

        service.UpsertShowMapping(new SavedShowMapping
        {
            SourcePath = source,
            Title = "Old title",
            TmdbId = 1
        });
        service.UpsertShowMapping(new SavedShowMapping
        {
            SourcePath = source.ToUpperInvariant(),
            Title = "New title",
            TvdbId = 2,
            EpisodeOrder = EpisodeOrder.Dvd
        });

        var mappings = service.GetSavedShowMappings();
        Assert.HasCount(1, mappings);
        Assert.AreEqual("New title", mappings[0].Title);
        Assert.AreEqual(EpisodeOrder.Dvd, mappings[0].EpisodeOrder);
        Assert.IsTrue(service.RemoveShowMapping(source));
        Assert.IsEmpty(service.GetSavedShowMappings());
        Assert.IsFalse(service.RemoveShowMapping(source));
    }

    [TestMethod]
    public void Load_ToleratesFutureFieldsAndUnknownEnumValues()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var source = Path.Combine(directory.Path, "TV", "Future Show");
        File.WriteAllText(
            path,
            $$"""
            {
              "SchemaVersion": 99,
              "DefaultOperation": "FutureOperation",
              "FutureSetting": true,
              "SavedShowMappings": [
                {
                  "SourcePath": {{System.Text.Json.JsonSerializer.Serialize(source)}},
                  "Title": "Future Show",
                  "TmdbId": 42,
                  "EpisodeOrder": "FutureOrder",
                  "FutureMappingField": "ignored"
                }
              ]
            }
            """);

        var settings = new AppSettingsService(path).Load();

        Assert.AreEqual(FileOperation.Move, settings.DefaultOperation);
        Assert.AreEqual(AppSettingsService.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.HasCount(1, settings.SavedShowMappings);
        Assert.AreEqual(EpisodeOrder.Default, settings.SavedShowMappings[0].EpisodeOrder);
    }

    [TestMethod]
    public void Load_DropsInvalidAndDuplicateMappingsWithoutLosingSettings()
    {
        using var directory = new DesktopTestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var source = Path.Combine(directory.Path, "TV", "Show");
        var service = new AppSettingsService(path);
        service.Save(new AppSettings
        {
            TmdbApiKey = "personal-key",
            SavedShowMappings =
            [
                new SavedShowMapping { SourcePath = source, Title = "Old", TmdbId = 1 },
                new SavedShowMapping
                {
                    SourcePath = source.ToUpperInvariant(),
                    Title = "Newest",
                    TmdbId = 2,
                    UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
                },
                new SavedShowMapping { SourcePath = source + " Invalid", Title = "", TmdbId = 3 },
                new SavedShowMapping { SourcePath = source + " NoProvider", Title = "No provider" }
            ]
        });

        var loaded = service.Load();

        Assert.AreEqual("personal-key", loaded.TmdbApiKey);
        Assert.HasCount(1, loaded.SavedShowMappings);
        Assert.AreEqual("Newest", loaded.SavedShowMappings[0].Title);
        Assert.AreEqual(2, loaded.SavedShowMappings[0].TmdbId);
    }
}
