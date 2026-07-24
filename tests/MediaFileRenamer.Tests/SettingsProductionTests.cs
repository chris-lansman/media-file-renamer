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
}
