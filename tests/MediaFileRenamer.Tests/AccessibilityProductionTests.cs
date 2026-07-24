using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Xml.Linq;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class AccessibilityProductionTests
{
    [STATestMethod]
    public void MainWorkflow_HasLabelsLiveRegionsAndVisibleKeyboardFocus()
    {
        WpfTestApplication.EnsureResources();
        var window = new MainWindow();

        AssertLabeledBy(window, "OutputFolderTextBox", "OutputFolderLabel");
        AssertLabeledBy(window, "OperationComboBox", "OperationLabel");
        AssertLabeledBy(window, "PresetComboBox", "PresetLabel");
        AssertLabeledBy(window, "SelectedTitleTextBox", "SelectedTitleLabel");
        AssertLabeledBy(window, "SelectedYearTextBox", "SelectedYearLabel");
        AssertLabeledBy(window, "SelectedSeasonTextBox", "SelectedSeasonLabel");
        AssertLabeledBy(window, "SelectedEpisodeTextBox", "SelectedEpisodeLabel");
        AssertLabeledBy(
            window,
            "SelectedEpisodeTitleTextBox",
            "SelectedEpisodeTitleLabel");
        AssertLabeledBy(window, "SelectedTypeComboBox", "SelectedTypeLabel");
        AssertLabeledBy(window, "EpisodeOrderComboBox", "EpisodeOrderLabel");

        AssertLiveRegion(window, "StatusTextBlock", "Application status");
        AssertLiveRegion(window, "ReviewCountTextBlock", "Review status");
        Assert.IsNotNull(
            ((Button)window.FindName("AddFilesButton")).FocusVisualStyle);
        Assert.AreEqual(
            "Original media files",
            AutomationProperties.GetName(
                (DependencyObject)window.FindName("OriginalGrid")));
        Assert.AreEqual(
            "Proposed media names and review status",
            AutomationProperties.GetName(
                (DependencyObject)window.FindName("NewNamesGrid")));

        window.Close();
    }

    [STATestMethod]
    public void Dialogs_HaveScrollableLayoutsAndConventionalKeyboardActions()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var journals = new OperationJournalService(temp.CreateDirectory("Journals"));
        var item = new MediaPreviewItem
        {
            SourcePath = @"C:\Source\Movie.mkv",
            MediaType = "Movie",
            MatchedTitle = "Movie",
            Status = "Needs review"
        };
        var picker = new MatchPickerWindow(item, null, "Movie", []);
        var recovery = new RecoveryWindow(journals, []);
        var history = new OperationHistoryWindow(journals);

        AssertScrollableAtConstrainedSize(
            (ScrollViewer)picker.FindName("MatchPickerScrollViewer"),
            700,
            400);
        AssertScrollableAtConstrainedSize(
            (ScrollViewer)recovery.FindName("RecoveryScrollViewer"),
            700,
            400);
        AssertScrollableAtConstrainedSize(
            (ScrollViewer)history.FindName("OperationHistoryScrollViewer"),
            560,
            320);

        Assert.IsTrue(((Button)picker.FindName("CancelButton")).IsCancel);
        Assert.IsTrue(((Button)picker.FindName("UseSelectedButton")).IsDefault);
        Assert.IsTrue(((Button)history.FindName("CloseButton")).IsCancel);
        AssertLiveRegion(picker, "SearchStatusTextBlock", "Metadata search status");
        AssertLiveRegion(recovery, "RecoveryStatusTextBlock", "Recovery status");
        AssertLiveRegion(
            history,
            "HistoryStatusTextBlock",
            "Operation history status");

        picker.Close();
        recovery.Close();
        history.Close();
    }

    [STATestMethod]
    public void MainWindow_RemainsScrollableAtTwoHundredPercentEffectiveSize()
    {
        WpfTestApplication.EnsureResources();
        var window = new MainWindow();

        Assert.IsLessThanOrEqualTo(640, window.MinWidth);
        Assert.IsLessThanOrEqualTo(420, window.MinHeight);
        AssertScrollableAtConstrainedSize(
            (ScrollViewer)window.FindName("MainScrollViewer"),
            800,
            450);

        window.Close();
    }

    [STATestMethod]
    public void SettingsStagingGrid_DefinesTheSettingsPathRow()
    {
        WpfTestApplication.EnsureResources();
        var window = new SettingsWindow(new AppSettings(), @"C:\settings.json");
        var settingsPath = (TextBox)window.FindName("SettingsPathTextBox");
        var stagingGrid = (Grid)settingsPath.Parent;

        Assert.IsGreaterThan(
            Grid.GetRow(settingsPath),
            stagingGrid.RowDefinitions.Count);
        Assert.AreEqual(2, Grid.GetRow(settingsPath));

        window.Close();
    }

    [TestMethod]
    public void Project_DeclaresPerMonitorV2AndHighContrastResources()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MediaFileRenamer.App",
            "MediaFileRenamer.App.csproj"));
        var manifestPath = Path.Combine(
            root,
            "src",
            "MediaFileRenamer.App",
            "app.manifest");
        var manifest = XDocument.Load(manifestPath);
        var theme = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MediaFileRenamer.App",
            "Themes",
            "Theme.xaml"));

        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project);
        Assert.Contains(
            "<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>",
            project);
        Assert.IsTrue(manifest.Descendants().Any(element =>
            element.Name.LocalName == "dpiAwareness"
            && element.Value.Contains("PerMonitorV2", StringComparison.Ordinal)));
        Assert.Contains("SystemParameters.HighContrast", theme);
        Assert.Contains("SystemColors.HighlightBrushKey", theme);
        Assert.Contains("KeyboardFocusVisualStyle", theme);
    }

    [TestMethod]
    public void UserVisibleSources_DoNotContainCommonMojibakeSentinels()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "src", "MediaFileRenamer.App");
        var files = Directory.EnumerateFiles(
                appRoot,
                "*.xaml",
                SearchOption.AllDirectories)
            .Append(Path.Combine(appRoot, "ViewModels", "MediaPreviewItem.cs"));
        var sentinels = new[] { "â€", "Â·", "ï¿½", "\uFFFD" };

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var sentinel in sentinels)
            {
                Assert.DoesNotContain(
                    sentinel,
                    text,
                    $"{Path.GetRelativePath(root, file)} contains mojibake.");
            }
        }
    }

    [TestMethod]
    public void ItemTemplates_DeclareMeaningfulAutomationSummaries()
    {
        var root = FindRepositoryRoot();
        var files = new[]
        {
            "MainWindow.xaml",
            "MatchPickerWindow.xaml",
            "OperationHistoryWindow.xaml",
            "RecoveryWindow.xaml"
        };

        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(
                root,
                "src",
                "MediaFileRenamer.App",
                file));
            Assert.Contains(
                "AutomationProperties.Name",
                text,
                $"{file} should name its rows or items for UI Automation.");
            Assert.Contains(
                "MultiBinding StringFormat",
                text,
                $"{file} should expose a composite row or item identity.");
        }
    }

    private static void AssertLabeledBy(
        FrameworkElement window,
        string controlName,
        string labelName)
    {
        var control = (DependencyObject)window.FindName(controlName);
        var label = (DependencyObject)window.FindName(labelName);
        Assert.AreSame(label, AutomationProperties.GetLabeledBy(control));
    }

    private static void AssertLiveRegion(
        FrameworkElement window,
        string elementName,
        string expectedName)
    {
        var element = (DependencyObject)window.FindName(elementName);
        Assert.AreEqual(expectedName, AutomationProperties.GetName(element));
        Assert.AreEqual(
            AutomationLiveSetting.Polite,
            AutomationProperties.GetLiveSetting(element));
    }

    private static void AssertScrollableAtConstrainedSize(
        ScrollViewer scrollViewer,
        double width,
        double height)
    {
        scrollViewer.Measure(new Size(width, height));
        scrollViewer.Arrange(new Rect(0, 0, width, height));
        scrollViewer.UpdateLayout();

        Assert.IsTrue(
            scrollViewer.ScrollableWidth > 0 || scrollViewer.ScrollableHeight > 0,
            "Constrained content must remain reachable by scrolling.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaFileRenamer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate the repository root.");
        return "";
    }
}
