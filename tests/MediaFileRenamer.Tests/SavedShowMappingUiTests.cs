using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using System.Windows;
using System.Windows.Controls;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class SavedShowMappingUiTests
{
    [STATestMethod]
    public void MainWindow_ExposesRememberedShowAction()
    {
        WpfTestApplication.EnsureResources();
        var window = new MainWindow();

        var button = (Button)window.FindName("RememberShowMappingButton");
        var status = (TextBlock)window.FindName(
            "RememberShowMappingStatusTextBlock");

        Assert.IsNotNull(button);
        Assert.IsNotNull(status);
        Assert.AreEqual(
            "Remember selected show for its source folder",
            System.Windows.Automation.AutomationProperties.GetName(button));

        window.Close();
    }

    [STATestMethod]
    public void Settings_PreservesAndCanRemoveRememberedShow()
    {
        WpfTestApplication.EnsureResources();
        var mapping = new SavedShowMapping
        {
            SourcePath = @"C:\Media\TV\Example Show",
            SourceIdentity = @"C:\Media\TV\Example Show",
            Title = "Example Show",
            MediaType = "TV",
            TmdbId = 123,
            TvdbId = 456,
            EpisodeOrder = EpisodeOrder.Dvd
        };
        var window = new SettingsWindow(
            new AppSettings { SavedShowMappings = [mapping] },
            @"C:\settings.json");
        var grid = (DataGrid)window.FindName("SavedShowMappingsGrid");
        var remove = (Button)window.FindName(
            "RemoveSavedShowMappingButton");

        Assert.HasCount(1, window.Settings.SavedShowMappings);
        Assert.AreNotSame(mapping, window.Settings.SavedShowMappings[0]);

        grid.SelectedItem = window.Settings.SavedShowMappings[0];
        Assert.IsTrue(remove.IsEnabled);
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.IsEmpty(window.Settings.SavedShowMappings);
        Assert.IsFalse(remove.IsEnabled);
        window.Close();
    }

    [TestMethod]
    public void RememberedCandidate_UsesAvailableProviderIdentity()
    {
        var mapping = new SavedShowMapping
        {
            SourcePath = @"C:\Media\Example",
            SourceIdentity = @"C:\Media\Example",
            Title = "Example",
            MediaType = "TV",
            TmdbId = 10,
            TvdbId = 20
        };

        var combined = MainWindow.CreateRememberedCandidate(
            mapping,
            includeTmdbId: true);
        var tvdbOnly = MainWindow.CreateRememberedCandidate(
            mapping,
            includeTmdbId: false);

        Assert.AreEqual(10, combined.TmdbId);
        Assert.AreEqual(20, combined.TvdbId);
        Assert.AreEqual(MetadataProvider.TmdbAndTvdb, combined.Provider);
        Assert.IsNull(tvdbOnly.TmdbId);
        Assert.AreEqual(20, tvdbOnly.TvdbId);
        Assert.AreEqual(MetadataProvider.Tvdb, tvdbOnly.Provider);
    }

    [TestMethod]
    public void RememberedMapping_RequiresAnAvailableMatchingProvider()
    {
        var mapping = new SavedShowMapping
        {
            SourcePath = @"C:\Media\Example",
            SourceIdentity = @"C:\Media\Example",
            Title = "Example",
            MediaType = "TV",
            TmdbId = 10,
            TvdbId = 20
        };

        Assert.IsTrue(MainWindow.IsUsableRememberedMapping(
            mapping,
            hasTmdbClient: true,
            hasTvdbClient: false));
        Assert.IsTrue(MainWindow.IsUsableRememberedMapping(
            mapping,
            hasTmdbClient: false,
            hasTvdbClient: true));
        Assert.IsFalse(MainWindow.IsUsableRememberedMapping(
            mapping,
            hasTmdbClient: false,
            hasTvdbClient: false));
    }
}
