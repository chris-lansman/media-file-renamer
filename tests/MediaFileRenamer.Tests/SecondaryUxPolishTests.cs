using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class SecondaryUxPolishTests
{
    [TestMethod]
    public void SettingsValidation_RequiresExplicitConfidenceRange()
    {
        using var temp = new TempDirectory();

        var nonNumeric = SettingsWindow.ValidateSettingsValues("high", temp.Path);
        var tooLow = SettingsWindow.ValidateSettingsValues("79", temp.Path);
        var valid = SettingsWindow.ValidateSettingsValues("80", temp.Path);

        Assert.IsFalse(nonNumeric.IsValid);
        StringAssert.Contains(nonNumeric.ConfidenceError, "whole-number");
        Assert.IsFalse(tooLow.IsValid);
        StringAssert.Contains(tooLow.ConfidenceError, "80 through 100");
        Assert.IsTrue(valid.IsValid);
        Assert.AreEqual(80, valid.Confidence);
    }

    [TestMethod]
    public void SettingsValidation_RequiresAccessibleFullyQualifiedFolder()
    {
        using var temp = new TempDirectory();

        var empty = SettingsWindow.ValidateSettingsValues("92", "");
        var relative = SettingsWindow.ValidateSettingsValues("92", "Renamed");
        var futureFolder = Path.Combine(temp.Path, "Future", "Renamed");
        var valid = SettingsWindow.ValidateSettingsValues("92", futureFolder);

        Assert.IsFalse(empty.IsValid);
        StringAssert.Contains(empty.OutputFolderError, "Choose");
        Assert.IsFalse(relative.IsValid);
        StringAssert.Contains(relative.OutputFolderError, "complete folder path");
        Assert.IsTrue(valid.IsValid);
        Assert.AreEqual(Path.GetFullPath(futureFolder), valid.OutputFolder);
        Assert.IsFalse(Directory.Exists(futureFolder));
    }

    [STATestMethod]
    public void SettingsWindow_DisablesSaveAndAnnouncesActionableErrors()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var window = new SettingsWindow(
            new AppSettings { DefaultOutputFolder = temp.Path },
            Path.Combine(temp.Path, "settings.json"));
        var confidence = (TextBox)window.FindName("AutoMatchConfidenceTextBox");
        var save = (Button)window.FindName("SaveSettingsButton");
        var error =
            (TextBlock)window.FindName("ConfidenceValidationTextBlock");

        confidence.Text = "101";

        Assert.IsFalse(save.IsEnabled);
        Assert.AreEqual(Visibility.Visible, error.Visibility);
        StringAssert.Contains(error.Text, "80 through 100");
        Assert.AreEqual(
            AutomationLiveSetting.Assertive,
            AutomationProperties.GetLiveSetting(error));

        confidence.Text = "95";

        Assert.IsTrue(save.IsEnabled);
        Assert.AreEqual(Visibility.Collapsed, error.Visibility);
        window.Close();
    }

    [STATestMethod]
    public void HistoryWindow_DisablesActionsWithoutASelection()
    {
        WpfTestApplication.EnsureResources();
        using var temp = new TempDirectory();
        var window = new OperationHistoryWindow(
            new OperationJournalService(temp.CreateDirectory("Journals")));

        Assert.IsFalse(((Button)window.FindName("UndoButton")).IsEnabled);
        Assert.IsFalse(
            ((Button)window.FindName("CopyDetailsButton")).IsEnabled);
        Assert.IsFalse(
            ((Button)window.FindName("OpenDestinationButton")).IsEnabled);
        StringAssert.Contains(
            ((TextBox)window.FindName("OperationDetailsTextBox")).Text,
            "Select an operation");
        window.Close();
    }

    [TestMethod]
    public void HistoryDetails_AreCopyableAndDestinationIsOpenedOnlyWhenPresent()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "Output", "Movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var summary = new OperationJournalSummary(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperation.Copy,
            OperationJournalStatus.Completed,
            1,
            "");
        var details = new OperationJournalDetails(
            summary.Id,
            summary.CreatedAt,
            DateTimeOffset.UtcNow,
            summary.Operation,
            summary.Status,
            "",
            [
                new OperationJournalFileDetails(
                    Path.Combine(temp.Path, "Source.mkv"),
                    destination,
                    false,
                    123,
                    OperationJournalEntryStatus.Completed)
            ]);

        var text = OperationHistoryWindow.FormatDetails(details, summary);
        var openable =
            OperationHistoryWindow.GetOpenableDestinationDirectory(details);

        StringAssert.Contains(text, $"Operation ID: {summary.Id}");
        StringAssert.Contains(text, $"Destination: {destination}");
        Assert.AreEqual(Path.GetDirectoryName(destination), openable);
        Directory.Delete(Path.GetDirectoryName(destination)!);
        Assert.IsNull(
            OperationHistoryWindow.GetOpenableDestinationDirectory(details));
    }

    [TestMethod]
    public async Task UpdateChecker_ReportsNewerGitHubRelease()
    {
        HttpRequestMessage? capturedRequest = null;
        using var client = new HttpClient(new StubHttpHandler(request =>
        {
            capturedRequest = request;
            return TestHttp.JsonResponse(
                """
                {
                  "tag_name": "v1.2.0",
                  "html_url": "https://github.com/chris-lansman/media-file-renamer/releases/tag/v1.2.0"
                }
                """);
        }));
        var checker = new GitHubReleaseUpdateChecker(client);

        var result = await checker.CheckAsync(
            new Version(1, 1, 0),
            CancellationToken.None);

        Assert.IsTrue(result.UpdateAvailable);
        Assert.AreEqual(new Version(1, 2, 0), result.LatestVersion);
        StringAssert.Contains(result.Message, "1.2.0 is available");
        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual(
            GitHubReleaseUpdateChecker.LatestReleaseApiUrl,
            capturedRequest.RequestUri?.AbsoluteUri);
        Assert.IsTrue(capturedRequest.Headers.UserAgent.Any());
    }

    [TestMethod]
    public async Task UpdateChecker_ReportsCurrentVersionAndPropagatesOfflineFailure()
    {
        using var currentClient = new HttpClient(new StubHttpHandler(_ =>
            TestHttp.JsonResponse(
                """
                {
                  "tag_name": "1.1.0",
                  "html_url": "https://github.com/chris-lansman/media-file-renamer/releases/tag/v1.1.0"
                }
                """)));
        var current = await new GitHubReleaseUpdateChecker(currentClient)
            .CheckAsync(new Version(1, 1, 0), CancellationToken.None);
        using var offlineClient = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        Assert.IsFalse(current.UpdateAvailable);
        StringAssert.Contains(current.Message, "up to date");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new GitHubReleaseUpdateChecker(offlineClient).CheckAsync(
                new Version(1, 1, 0),
                CancellationToken.None));
    }

    [DataRow("v2.3.4", 2, 3, 4)]
    [DataRow("1.5.0-beta.1", 1, 5, 0)]
    [DataRow("1.5.0+build.9", 1, 5, 0)]
    [TestMethod]
    public void UpdateChecker_ParsesReleaseTags(
        string tag,
        int major,
        int minor,
        int build)
    {
        Assert.IsTrue(
            GitHubReleaseUpdateChecker.TryParseReleaseVersion(
                tag,
                out var version));
        Assert.AreEqual(new Version(major, minor, build), version);
    }
}
