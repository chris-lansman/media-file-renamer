using MediaFileRenamer.App;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class AppStartupOptionsTests
{
    [TestMethod]
    public void Parse_WithoutDataRoot_PreservesDefaultConfiguration()
    {
        var options = AppStartupOptions.Parse([]);

        Assert.IsNull(options.DataPaths);
    }

    [TestMethod]
    public void Parse_DataRoot_CentralizesAllApplicationFiles()
    {
        using var directory = new DesktopTestDirectory();
        var root = Path.Combine(directory.Path, "isolated app data");

        var options = AppStartupOptions.Parse(["--data-root", root]);

        Assert.IsNotNull(options.DataPaths);
        Assert.IsTrue(options.DataPaths.IsCustom);
        Assert.AreEqual(Path.GetFullPath(root), options.DataPaths.RootDirectory);
        Assert.AreEqual(
            Path.Combine(root, "settings.json"),
            options.DataPaths.SettingsPath);
        Assert.AreEqual(
            Path.Combine(root, "operations"),
            options.DataPaths.OperationDirectory);
        Assert.AreEqual(
            Path.Combine(root, "Logs"),
            options.DataPaths.LogDirectory);
    }

    [TestMethod]
    public void Parse_DataRoot_IsCaseInsensitiveAndIgnoresUnrelatedArguments()
    {
        using var directory = new DesktopTestDirectory();
        var root = Path.Combine(directory.Path, "isolated");

        var options = AppStartupOptions.Parse(
            ["unrelated.media", "--DATA-ROOT", root, "--another-option"]);

        Assert.AreEqual(Path.GetFullPath(root), options.DataPaths?.RootDirectory);
    }

    [TestMethod]
    public void Parse_DataRoot_RejectsMissingRelativeRootAndExistingFile()
    {
        Assert.Throws<ArgumentException>(
            () => AppStartupOptions.Parse(["--data-root"]));
        Assert.Throws<ArgumentException>(
            () => AppStartupOptions.Parse(["--data-root", "relative"]));

        using var directory = new DesktopTestDirectory();
        var file = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(file, "fixture");

        Assert.Throws<ArgumentException>(
            () => AppStartupOptions.Parse(["--data-root", file]));
    }

    [TestMethod]
    public void Parse_DataRoot_RejectsDriveRootAndDuplicateOption()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        Assert.IsFalse(string.IsNullOrWhiteSpace(root));

        Assert.Throws<ArgumentException>(
            () => AppStartupOptions.Parse(["--data-root", root!]));

        using var directory = new DesktopTestDirectory();
        Assert.Throws<ArgumentException>(
            () => AppStartupOptions.Parse(
                ["--data-root", directory.Path, "--data-root", directory.Path]));
    }
}
