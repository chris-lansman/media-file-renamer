using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class WorkspaceExperienceTests
{
    [TestMethod]
    public void WorkspaceRoundTrip_PreservesEditsButRescansCompanionsAndRequiresReview()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Show.S01E02.mkv");
        var subtitle = temp.CreateFile("Show.S01E02.en.srt");
        var saved = new MediaPreviewItem
        {
            SourcePath = source,
            MatchedTitle = "Edited Show",
            MediaType = "TV",
            Season = 3,
            Episode = 4,
            EpisodeEnd = 5,
            EpisodeTitle = "Edited title",
            TmdbId = 17,
            TvdbId = 18,
            EpisodeOrder = EpisodeOrder.Default,
            Status = "TMDB matched",
            DestinationPath = "Z:\\obsolete\\file.mkv",
            Edition = "Extended",
            PartNumber = 2
        };
        saved.CompanionPaths.Add("Z:\\obsolete\\subtitle.srt");
        var store = new WorkspaceSessionService(Path.Combine(temp.Path, "workspace.json"));
        store.Save(new WorkspaceSession { Items = [saved], Width = 990, Preset = 2 });
        var loaded = store.Load()!;
        var scanned = new MediaScanner().Scan([source]).Single();
        WorkspaceSessionService.RestoreDetails(loaded.Items.Single(), scanned);

        Assert.AreEqual("Edited Show", scanned.MatchedTitle);
        Assert.AreEqual(3, scanned.Season);
        Assert.AreEqual(5, scanned.EpisodeEnd);
        Assert.AreEqual("Edited title", scanned.EpisodeTitle);
        Assert.AreEqual(18, scanned.TvdbId);
        Assert.AreEqual(2, scanned.PartNumber);
        Assert.IsTrue(scanned.RequiresReview);
        Assert.AreNotEqual(saved.DestinationPath, scanned.DestinationPath);
        CollectionAssert.AreEqual(new[] { subtitle }, scanned.CompanionPaths.ToArray());
        Assert.AreEqual(990d, loaded.Width);
        Assert.AreEqual(2, loaded.Preset);
    }

    [TestMethod]
    public void WorkspaceLoad_RejectsCorruptUnsupportedAndRelativePathData()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "workspace.json");
        var store = new WorkspaceSessionService(path);
        File.WriteAllText(path, "{broken");
        Assert.ThrowsExactly<JsonException>(() => store.Load());
        File.WriteAllText(path, "{\"Version\":99}");
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        File.WriteAllText(path, "{\"Items\":[{\"SourcePath\":\"relative.mkv\"}]}");
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        store.Save(new WorkspaceSession());
        Assert.AreEqual(0, store.Load()!.Items.Count);
    }

    [DataRow("Movie.release.mkv", "Movie.mkv", "Movie.", "", "mkv")]
    [DataRow("Old.mkv", "New.mkv", "", "New", ".mkv")]
    [DataRow("Same.mkv", "Same.mkv", "Same.mkv", "", "")]
    [DataRow("", "New", "", "New", "")]
    [TestMethod]
    public void FilenameDifference_HandlesReplacementDeletionAndIdenticalNames(string before, string after, string prefix, string changed, string suffix)
    {
        Assert.AreEqual((prefix, changed, suffix), FilenameDifference.Compare(before, after));
    }

    [DataRow("{Ttile}")]
    [DataRow("{Title")]
    [DataRow("Title}")]
    [TestMethod]
    public void CustomNaming_RejectsUnknownOrIncompleteTokens(string pattern)
    {
        using var temp = new TempDirectory();
        Assert.ThrowsExactly<ArgumentException>(() => new RenamePlanner().BuildDestination(
            new MediaPreviewItem { MatchedTitle = "Movie", Extension = ".mkv" }, temp.Path, RenamePreset.Custom, pattern));
    }

    [STATestMethod]
    public void SearchAndRemoval_DoNotBypassReviewGateOrDeleteSources()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var first = temp.CreateFile("First.mkv");
        var second = temp.CreateFile("Second.mkv");
        var window = new MainWindow();
        ((DataGrid)window.FindName("OriginalGrid")).ItemsSource = window.PreviewItems;
        Invoke(window, "AddSources", (object)new[] { first, second });
        window.PreviewItems[0].MediaType = "Movie";
        window.PreviewItems[0].Status = "Manual movie choice";
        var search = Field<TextBox>(window, "_search");
        search.Text = "First";
        Assert.AreEqual(1, CollectionViewSource.GetDefaultView(window.PreviewItems).Cast<object>().Count());
        Assert.IsFalse(((Button)window.FindName("RenameButton")).IsEnabled);
        Invoke(window, "NavigateReview", 1);
        var grid = (DataGrid)window.FindName("OriginalGrid");
        Assert.AreSame(window.PreviewItems[1], grid.SelectedItem);
        Invoke(window, "RemoveSelected_Click", window, new RoutedEventArgs());
        Assert.AreEqual(1, window.PreviewItems.Count);
        Assert.IsTrue(File.Exists(first));
        Assert.IsTrue(File.Exists(second));
        Assert.IsTrue(((Button)window.FindName("RenameButton")).IsEnabled);
        window.Close();
    }

    [DataRow(640, 640, false)]
    [DataRow(1024, 768, false)]
    [DataRow(1360, 860, false)]
    [DataRow(640, 640, true)]
    [DataRow(1024, 768, true)]
    [DataRow(1360, 860, true)]
    [STATestMethod]
    public void WorkspaceLayout_KeepsApplyVisibleAndRendersAtSupportedWidths(int width, int height, bool needsReview)
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Example.Movie.2024.1080p.BluRay.mkv");
        temp.CreateFile("Example.Movie.2024.1080p.BluRay.en.srt");
        var window = new MainWindow();
        ((DataGrid)window.FindName("OriginalGrid")).ItemsSource = window.PreviewItems;
        Invoke(window, "AddSources", (object)new[] { source });
        window.PreviewItems[0].MatchedTitle = "Example Movie";
        window.PreviewItems[0].Status = needsReview ? "Needs review" : "Manual movie choice";
        var root = (FrameworkElement)window.Content;
        root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        root.UpdateLayout();
        var apply = (Button)window.FindName("RenameButton");
        var bounds = apply.TransformToAncestor(root).TransformBounds(new Rect(apply.RenderSize));
        Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= width && bounds.Bottom <= height);
        Assert.IsTrue(apply.ActualHeight >= 24);
        if (width == 640)
        {
            var inspection = Field<StackPanel>(window, "_inspection");
            var inspectionBounds = inspection.TransformToAncestor(root).TransformBounds(new Rect(inspection.RenderSize));
            Assert.IsTrue(inspectionBounds.Top + 60 < bounds.Top, "The filename comparison must be visible above the fixed operation footer.");
            if (!needsReview) Assert.IsTrue(inspectionBounds.Bottom < bounds.Top);
            Assert.IsFalse(Field<Expander>(window, "_setupExpander").IsExpanded);
        }
        Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)((FrameworkElement)window.FindName("SelectedSeasonTextBox")).Parent).Visibility);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(FindRoot(), "TestResults", "workspace-previews");
        Directory.CreateDirectory(directory);
        using (var output = File.Create(Path.Combine(directory, $"workspace-{width}{(needsReview ? "-review" : "")}.png"))) encoder.Save(output);
        window.Close();
    }

    [STATestMethod]
    public void ConfirmAndNext_FollowsQueuePositionAndOffersShowAllWhenFinished()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var paths = new[] { temp.CreateFile("First.Movie.2024.mkv"), temp.CreateFile("Second.Movie.2024.mkv"), temp.CreateFile("Third.Movie.2024.mkv") };
        var window = new MainWindow();
        var grid = (DataGrid)window.FindName("OriginalGrid");
        grid.ItemsSource = window.PreviewItems;
        Invoke(window, "AddSources", (object)paths);
        var first = window.PreviewItems[0];
        var second = window.PreviewItems[1];
        var third = window.PreviewItems[2];
        ((ComboBox)window.FindName("ReviewFilterComboBox")).SelectedIndex = 1;
        grid.SelectedItem = second;
        Invoke(window, "ConfirmManualDetails_Click", window, new RoutedEventArgs());
        Assert.AreSame(third, grid.SelectedItem);
        Invoke(window, "ConfirmManualDetails_Click", window, new RoutedEventArgs());
        Assert.AreSame(first, grid.SelectedItem);
        Invoke(window, "ConfirmManualDetails_Click", window, new RoutedEventArgs());
        Assert.AreEqual(0, CollectionViewSource.GetDefaultView(window.PreviewItems).Cast<object>().Count());
        Assert.AreEqual(Visibility.Visible, Field<StackPanel>(window, "_filterEmptyPanel").Visibility);
        StringAssert.Contains(Field<TextBlock>(window, "_filterEmptyText").Text, "Review complete");
        Assert.AreEqual(Visibility.Collapsed, ((Border)window.FindName("EmptyDropPanel")).Visibility);
        Assert.IsTrue(((Button)window.FindName("RenameButton")).IsEnabled);
        Invoke(window, "ShowAllFiles");
        Assert.AreEqual(3, CollectionViewSource.GetDefaultView(window.PreviewItems).Cast<object>().Count());
        Assert.AreSame(first, grid.SelectedItem);
        Assert.AreEqual(Visibility.Collapsed, Field<StackPanel>(window, "_filterEmptyPanel").Visibility);
        window.Close();
    }

    [STATestMethod]
    public void SearchWithoutResults_ExplainsFiltersWithoutPretendingBatchIsEmpty()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var window = new MainWindow();
        ((DataGrid)window.FindName("OriginalGrid")).ItemsSource = window.PreviewItems;
        Invoke(window, "AddSources", (object)new[] { temp.CreateFile("Movie.2024.mkv") });
        Field<TextBox>(window, "_search").Text = "not a matching filename";
        Assert.AreEqual(Visibility.Visible, Field<StackPanel>(window, "_filterEmptyPanel").Visibility);
        StringAssert.Contains(Field<TextBlock>(window, "_filterEmptyText").Text, "No files match");
        Assert.IsFalse(((Button)window.FindName("RenameButton")).IsEnabled);
        Invoke(window, "ShowAllFiles");
        Assert.AreEqual(1, CollectionViewSource.GetDefaultView(window.PreviewItems).Cast<object>().Count());
        window.Close();
    }

    [STATestMethod]
    public void ClearedBatchRestore_RescansSourcesAndRequiresReviewWithoutOverwritingNewRows()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var firstPath = temp.CreateFile("First.Movie.2024.mkv");
        var missingPath = temp.CreateFile("Missing.Movie.2024.mkv");
        var window = new MainWindow();
        var grid = (DataGrid)window.FindName("OriginalGrid");
        grid.ItemsSource = window.PreviewItems;
        Invoke(window, "AddSources", (object)new[] { firstPath, missingPath });
        var first = window.PreviewItems[0];
        first.MatchedTitle = "Manual correction";
        first.Status = "Manual movie choice";
        Invoke(window, "Clear_Click", window, new RoutedEventArgs());
        Assert.AreEqual(0, window.PreviewItems.Count);
        Assert.IsTrue(File.Exists(firstPath));
        Assert.IsTrue(File.Exists(missingPath));
        Assert.AreEqual(Visibility.Visible, Field<Button>(window, "_restoreClearedButton").Visibility);
        File.Delete(missingPath);
        temp.CreateFile("First.Movie.2024.en.srt");
        var extraPath = temp.CreateFile("New.Movie.2024.mkv");
        Invoke(window, "AddSources", (object)new[] { extraPath });
        var previousContext = SynchronizationContext.Current;
        Task task;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            task = (Task)typeof(MainWindow).GetMethod("RestoreClearedBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        Invoke(window, "UpdateActionState");
        Assert.AreEqual(2, window.PreviewItems.Count);
        var restored = window.PreviewItems.Single(item => item.SourcePath == firstPath);
        Assert.AreEqual("Manual correction", restored.MatchedTitle);
        Assert.AreEqual(1, restored.CompanionCount);
        Assert.IsTrue(restored.RequiresReview);
        Assert.IsFalse(((Button)window.FindName("RenameButton")).IsEnabled);
        Assert.AreEqual(Visibility.Collapsed, Field<Button>(window, "_restoreClearedButton").Visibility);
        Assert.IsTrue(File.Exists(firstPath));
        window.Close();
    }

    [TestMethod]
    public void BatchRematching_IncludesProviderMatchesAndProtectsManualEpisodesUnlessExplicitlyIncluded()
    {
        var automatic = new MediaPreviewItem { Status = "TMDB auto 99%", TmdbId = 123 };
        var manual = new MediaPreviewItem { MediaType = "TV", Season = 1, Episode = 1, Status = "Manual TV details confirmed", TmdbId = 456 };
        var unresolved = new MediaPreviewItem { Status = "Needs review", TmdbId = 789 };
        var items = new[] { automatic, manual, unresolved };
        var protectedTargets = MainWindow.GetBatchRematchItems(items, false);
        CollectionAssert.AreEqual(new[] { automatic, unresolved }, protectedTargets);
        Assert.IsFalse(MainWindow.CanUpdateRelatedEpisode(manual, protectedTargets.ToHashSet()));
        Assert.IsFalse(MainWindow.CanUpdateRelatedEpisode(manual, null));
        var allTargets = MainWindow.GetBatchRematchItems(items, true);
        CollectionAssert.AreEqual(items, allTargets);
        Assert.IsTrue(MainWindow.CanUpdateRelatedEpisode(manual, allTargets.ToHashSet()));
    }

    [STATestMethod]
    public void ShowGroupingAndCompanionInspection_FollowCurrentReviewedDestinations()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var first = temp.CreateFile("Example.Show.S01E01.mkv");
        var subtitle = temp.CreateFile("Example.Show.S01E01.en.srt");
        var second = temp.CreateFile("Example.Show.S02E01.mkv");
        var window = new MainWindow();
        var grid = (DataGrid)window.FindName("OriginalGrid");
        grid.ItemsSource = window.PreviewItems;
        Invoke(window, "AddSources", (object)new[] { first, second });
        foreach (var item in window.PreviewItems) item.MatchedTitle = "Example Show";
        Field<CheckBox>(window, "_groupShows").IsChecked = true;
        var groups = CollectionViewSource.GetDefaultView(window.PreviewItems).Groups!;
        Assert.AreEqual(2, groups.Count);
        StringAssert.Contains(((CollectionViewGroup)groups[0]).Name.ToString()!, "Season 01");
        grid.SelectedItem = window.PreviewItems[0];
        Invoke(window, "RefreshWorkspaceDetails");
        var companionText = Field<TextBox>(window, "_companions").Text;
        StringAssert.Contains(companionText, subtitle);
        StringAssert.Contains(companionText, new RenamePlanner().BuildCompanionDestinations(window.PreviewItems[0]).Single().DestinationPath);
        window.Close();
    }

    [STATestMethod]
    public void BusyWorkspace_PreventsRemovalAndClosing()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Movie.mkv");
        var window = new MainWindow();
        ((DataGrid)window.FindName("OriginalGrid")).ItemsSource = window.PreviewItems;
        Invoke(window, "AddSources", (object)new[] { source });
        typeof(MainWindow).GetField("_isBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        Invoke(window, "SetActionButtonsEnabled", false);
        Assert.IsFalse(((DataGrid)window.FindName("OriginalGrid")).IsEnabled);
        Assert.IsFalse(((TextBox)window.FindName("OutputFolderTextBox")).IsEnabled);
        Invoke(window, "RemoveSelected_Click", window, new RoutedEventArgs());
        Assert.AreEqual(1, window.PreviewItems.Count);
        var closing = new System.ComponentModel.CancelEventArgs();
        Invoke(window, "OnClosing", closing);
        Assert.IsTrue(closing.Cancel);
        typeof(MainWindow).GetField("_isBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
        window.Close();
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaFileRenamer.sln"))) directory = directory.Parent;
        return directory!.FullName;
    }
}
