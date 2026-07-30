using MediaFileRenamer.App;
using MediaFileRenamer.App.ViewModels;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class MainWorkflowPolishTests
{
    [STATestMethod]
    public void MainReview_UsesOneUnifiedGridAndProgressiveEditor()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var first = temp.CreateFile("First Movie (2024).mkv");
        var second = temp.CreateFile("Second Movie (2023).mp4");
        var window = new MainWindow();

        var grid = (DataGrid)window.FindName("OriginalGrid");
        var compatibilityMarker = (Border)window.FindName("NewNamesGrid");
        var emptyPanel = (Border)window.FindName("EmptyDropPanel");
        var editor = (Grid)window.FindName("SelectedFileEditor");

        Assert.AreEqual(6, grid.Columns.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "Original file",
                "Source folder",
                "Proposed name",
                "Destination",
                "Status / provider",
                "Actions"
            },
            grid.Columns.Select(column => column.Header?.ToString()).ToArray());
        Assert.AreEqual(Visibility.Visible, emptyPanel.Visibility);
        Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
        Assert.AreEqual(Visibility.Collapsed, compatibilityMarker.Visibility);
        StringAssert.Contains(
            AutomationProperties.GetHelpText(grid),
            "Unified review table");

        AddSources(window, first, second);

        Assert.AreEqual(2, window.PreviewItems.Count);
        Assert.AreEqual(Visibility.Collapsed, emptyPanel.Visibility);
        grid.ItemsSource = window.PreviewItems;
        grid.SelectedItem = window.PreviewItems[0];
        Assert.AreEqual(Visibility.Visible, editor.Visibility);

        grid.SelectedItem = null;
        Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
        window.Close();
    }

    [STATestMethod]
    public void ApplyAction_DescribesSelectedOperationAndBatchSize()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var first = temp.CreateFile("First Movie (2024).mkv");
        var second = temp.CreateFile("Second Movie (2023).mp4");
        var window = new MainWindow();
        var operation = (ComboBox)window.FindName("OperationComboBox");
        var apply = (Button)window.FindName("RenameButton");

        operation.SelectedIndex = 0;
        AddSources(window, first, second);
        Assert.AreEqual("Move 2 files", apply.Content);
        Assert.AreEqual(
            "Move 2 reviewed files",
            AutomationProperties.GetName(apply));

        operation.SelectedIndex = 1;
        Assert.AreEqual("Copy 2 files", apply.Content);
        Assert.AreEqual(
            "Copy 2 reviewed files",
            AutomationProperties.GetName(apply));
        window.Close();
    }

    [TestMethod]
    public void MatchSummary_SeparatesMatchedReviewAndFailedOutcomes()
    {
        var items = new[]
        {
            new MediaPreviewItem { Status = "TMDB auto 100%" },
            new MediaPreviewItem { Status = "Local movie choice" },
            new MediaPreviewItem { Status = "Needs review" },
            new MediaPreviewItem
            {
                Status = "Failed: destination already exists"
            }
        };

        Assert.AreEqual(
            "Match complete: 2 matched/planned, 1 need review, 1 failed.",
            MainWindow.FormatMatchSummary(items));
    }

    [STATestMethod]
    public void LocalClassification_IsCompactAndDoesNotShowEmptyProviderResults()
    {
        WpfTestApplication.EnsureResources();
        var item = new MediaPreviewItem
        {
            SourcePath = @"C:\Media\Unknown.mkv",
            TitleGuess = "Unknown"
        };
        var window = new MatchPickerWindow(item, null, item.TitleGuess, []);

        Assert.AreEqual("Classify Media", window.Title);
        Assert.AreEqual(
            "Classify this media file",
            ((TextBlock)window.FindName("PickerHeadingTextBlock")).Text);
        Assert.IsLessThanOrEqualTo(500, window.Height);
        Assert.AreEqual(
            Visibility.Visible,
            ((Border)window.FindName("LocalClassificationPanel")).Visibility);
        Assert.AreEqual(
            Visibility.Collapsed,
            ((Grid)window.FindName("ProviderResultsGrid")).Visibility);
        Assert.IsTrue(((Button)window.FindName("LocalMovieButton")).IsDefault);
        window.Close();
    }

    private static void AddSources(MainWindow window, params string[] sources)
    {
        var method = typeof(MainWindow).GetMethod(
            "AddSources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(window, [sources]);
    }
}
