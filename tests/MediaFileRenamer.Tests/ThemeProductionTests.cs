using System.Xml.Linq;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class ThemeProductionTests
{
    [TestMethod]
    public void Theme_DefinesEveryRuntimePaletteBrush()
    {
        var root = FindRepositoryRoot();
        var themePath = Path.Combine(
            root,
            "src",
            "MediaFileRenamer.App",
            "Themes",
            "Theme.xaml");
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = XDocument.Load(themePath)
            .Descendants(presentation + "SolidColorBrush")
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "AppBackgroundBrush",
            "SurfaceBrush",
            "SurfaceMutedBrush",
            "TextBrush",
            "MutedTextBrush",
            "BorderBrush",
            "AccentBrush",
            "AccentHoverBrush",
            "AccentPressedBrush",
            "AccentSoftBrush",
            "MatchBrush",
            "MatchSoftBrush",
            "ReviewBrush",
            "ReviewSoftBrush",
            "BlockedBrush",
            "BlockedSoftBrush",
            "ControlHoverBrush",
            "ControlPressedBrush",
            "DisabledBackgroundBrush",
            "DisabledBorderBrush",
            "DisabledTextBrush",
            "PrimaryDisabledBrush",
            "PrimaryDisabledTextBrush",
            "GridLineBrush"
        ];

        foreach (var key in required)
        {
            Assert.Contains(key, keys);
        }
    }

    [TestMethod]
    public void App_TracksWindowsLightDarkAndHighContrastPreferences()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MediaFileRenamer.App",
            "App.xaml.cs"));

        Assert.Contains("AppsUseLightTheme", app);
        Assert.Contains("SystemEvents.UserPreferenceChanged", app);
        Assert.Contains("SystemParameters.HighContrast", app);
        Assert.Contains("ApplyDarkPalette", app);
        Assert.Contains("ApplyHighContrastPalette", app);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "MediaFileRenamer.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
