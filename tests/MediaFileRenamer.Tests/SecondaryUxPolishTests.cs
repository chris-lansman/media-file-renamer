using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
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

    [STATestMethod]
    public void AboutWindow_OffersReleasePageBeforeNetworkCheck()
    {
        WpfTestApplication.EnsureResources();
        var window = new AboutWindow();

        Assert.IsTrue(
            ((Button)window.FindName("OpenReleaseButton")).IsEnabled);
        Assert.AreEqual(
            AutomationLiveSetting.Polite,
            AutomationProperties.GetLiveSetting(
                (TextBlock)window.FindName("UpdateStatusTextBlock")));
        window.Close();
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
                  "html_url": "https://github.com/chris-lansman/media-file-renamer/releases/tag/v1.2.0",
                  "assets": [
                    {
                      "name": "MediaFileRenamer-win-x64.zip",
                      "browser_download_url": "https://github.com/chris-lansman/media-file-renamer/releases/download/v1.2.0/MediaFileRenamer-win-x64.zip"
                    },
                    {
                      "name": "MediaFileRenamer-win-x64.zip.sha256",
                      "browser_download_url": "https://github.com/chris-lansman/media-file-renamer/releases/download/v1.2.0/MediaFileRenamer-win-x64.zip.sha256"
                    }
                  ]
                }
                """);
        }));
        var checker = new GitHubReleaseUpdateChecker(client);

        var result = await checker.CheckAsync(
            new Version(1, 1, 0),
            CancellationToken.None);

        Assert.IsTrue(result.UpdateAvailable);
        Assert.IsTrue(result.CanInstall);
        Assert.IsNotNull(result.Package);
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

    [TestMethod]
    public async Task UpdateChecker_ExplainsUnavailableOrPrivateReleaseFeed()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        var exception = await Assert.ThrowsExactlyAsync<UpdateCheckException>(() =>
            new GitHubReleaseUpdateChecker(client).CheckAsync(
                new Version(1, 1, 4),
                CancellationToken.None));

        StringAssert.Contains(exception.UserMessage, "HTTP 404");
        StringAssert.Contains(exception.UserMessage, "publicly readable");
    }

    [TestMethod]
    public async Task UpdateChecker_ExplainsGitHubRateLimitRejection()
    {
        using var client = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var exception = await Assert.ThrowsExactlyAsync<UpdateCheckException>(() =>
            new GitHubReleaseUpdateChecker(client).CheckAsync(
                new Version(1, 1, 4),
                CancellationToken.None));

        StringAssert.Contains(exception.UserMessage, "HTTP 403");
        StringAssert.Contains(exception.UserMessage, "Try again later");
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

    [TestMethod]
    public void UpdateChecker_RefusesAssetsOutsideTheOfficialReleasePath()
    {
        var package = new UpdatePackage(
            new Version(1, 2, 0),
            new Uri("https://github.com/chris-lansman/media-file-renamer/releases/tag/v1.2.0"),
            new Uri("https://example.test/MediaFileRenamer-win-x64.zip"),
            new Uri("https://example.test/MediaFileRenamer-win-x64.zip.sha256"));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            UpdateDownloadService.ValidateOfficialPackage(package));

        StringAssert.Contains(exception.Message, "official GitHub release asset");
    }

    [TestMethod]
    public void UpdateInstaller_ParsesOnlyTheExpectedSha256Record()
    {
        var hash = new string('a', 64);

        var parsed = UpdateDownloadService.ParseChecksum(
            $"{hash} *MediaFileRenamer-win-x64.zip",
            "MediaFileRenamer-win-x64.zip");

        Assert.AreEqual(hash.ToUpperInvariant(), parsed);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            UpdateDownloadService.ParseChecksum(
                $"{hash} other.zip",
                "MediaFileRenamer-win-x64.zip"));
    }

    [TestMethod]
    public void UpdateInstaller_AppliesVerifiedPackageAndKeepsUnrelatedFiles()
    {
        using var temp = new TempDirectory();
        var installationDirectory = temp.CreateDirectory("Installed app");
        var sessionDirectory = temp.CreateDirectory("Update session");
        var executable = Path.Combine(installationDirectory, "MediaFileRenamer.exe");
        var preserved = Path.Combine(installationDirectory, "user-notes.txt");
        File.WriteAllText(executable, "old executable");
        File.WriteAllText(preserved, "keep me");

        var packagePath = Path.Combine(sessionDirectory, "MediaFileRenamer-win-x64.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(
                       archive.CreateEntry("MediaFileRenamer.exe").Open()))
            {
                writer.Write("new executable");
            }

            using var assetWriter = new StreamWriter(
                archive.CreateEntry("Assets/readme.txt").Open());
            assetWriter.Write("new asset");
        }

        var checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        var planPath = Path.Combine(sessionDirectory, "update-plan.json");
        var plan = new UpdateLaunchPlan(
            ParentProcessId: 0,
            InstallationDirectory: installationDirectory,
            PackagePath: packagePath,
            ExpectedSha256: checksum,
            RelaunchArguments: []);
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

        UpdateInstallService.ApplyUpdatePlan(planPath, restartApplication: false);

        Assert.AreEqual("new executable", File.ReadAllText(executable));
        Assert.AreEqual("new asset", File.ReadAllText(
            Path.Combine(installationDirectory, "Assets", "readme.txt")));
        Assert.AreEqual("keep me", File.ReadAllText(preserved));
    }

    [TestMethod]
    public void UpdateInstaller_ReportsProgressWhileApplyingAVerifiedPackage()
    {
        using var temp = new TempDirectory();
        var installationDirectory = temp.CreateDirectory("Installed app");
        var sessionDirectory = temp.CreateDirectory("Update session");
        File.WriteAllText(
            Path.Combine(installationDirectory, "MediaFileRenamer.exe"),
            "old executable");

        var packagePath = Path.Combine(sessionDirectory, "MediaFileRenamer-win-x64.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(
                   archive.CreateEntry("MediaFileRenamer.exe").Open()))
        {
            writer.Write("new executable");
        }

        var planPath = Path.Combine(sessionDirectory, "update-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(new UpdateLaunchPlan(
            ParentProcessId: 0,
            InstallationDirectory: installationDirectory,
            PackagePath: packagePath,
            ExpectedSha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))),
            RelaunchArguments: [])));
        var reporter = new UpdateInstallService.UpdateStatusReporter(
            UpdateInstallService.GetStatusPath(planPath));

        UpdateInstallService.ApplyUpdatePlan(planPath, restartApplication: false, reporter);

        Assert.IsTrue(UpdateInstallService.TryReadStatus(reporter.StatusPath, out var status));
        Assert.IsNotNull(status);
        Assert.AreEqual("installing-update", status.Stage);
        Assert.IsFalse(status.IsTerminal);
    }

    [TestMethod]
    public void UpdateInstaller_RemovesOnlyAValidUpdateResultArgument()
    {
        var sessionDirectory = Path.Combine(
            UpdateInstallService.SessionRoot,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        try
        {
            var statusPath = Path.Combine(sessionDirectory, "update-status.json");
            File.WriteAllText(statusPath, JsonSerializer.Serialize(new UpdateInstallStatus(
                "restarting-application",
                IsTerminal: true,
                Succeeded: true,
                Message: "The update was installed successfully.")));

            var remaining = UpdateInstallService.RemoveUpdateResultArgument(
                ["--data-root", "C:\\test-data", "--show-update-result", statusPath, "movie.mkv"],
                out var outcome);

            CollectionAssert.AreEqual(
                new[] { "--data-root", "C:\\test-data", "movie.mkv" },
                remaining.ToArray());
            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual("The update was installed successfully.", outcome.Message);
        }
        finally
        {
            if (Directory.Exists(sessionDirectory))
            {
                Directory.Delete(sessionDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateInstaller_CopiesTheRuntimeRequiredByItsHelper()
    {
        using var temp = new TempDirectory();
        var installationDirectory = temp.CreateDirectory("Installed app");
        var sessionDirectory = temp.CreateDirectory("Update session");
        var executable = Path.Combine(installationDirectory, "MediaFileRenamer.exe");
        File.WriteAllText(executable, "app host");
        File.WriteAllText(
            Path.Combine(installationDirectory, "MediaFileRenamer.dll"),
            "app assembly");
        File.WriteAllText(
            Path.Combine(installationDirectory, "MediaFileRenamer.deps.json"),
            "dependencies");
        File.WriteAllText(
            Path.Combine(installationDirectory, "MediaFileRenamer.runtimeconfig.json"),
            "runtime");
        var satelliteDirectory = Path.Combine(installationDirectory, "de");
        Directory.CreateDirectory(satelliteDirectory);
        File.WriteAllText(Path.Combine(satelliteDirectory, "resources.dll"), "satellite");
        File.WriteAllText(Path.Combine(installationDirectory, "user-notes.txt"), "do not copy");

        var helperPath = UpdateDownloadService.CopyHelperRuntime(
            executable,
            sessionDirectory);
        var helperDirectory = Path.GetDirectoryName(helperPath)!;

        Assert.AreEqual(
            Path.Combine(sessionDirectory, "update-helper", "MediaFileRenamer.exe"),
            helperPath);
        Assert.IsTrue(File.Exists(Path.Combine(helperDirectory, "MediaFileRenamer.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(
            helperDirectory,
            "MediaFileRenamer.deps.json")));
        Assert.IsTrue(File.Exists(Path.Combine(
            helperDirectory,
            "MediaFileRenamer.runtimeconfig.json")));
        Assert.IsTrue(File.Exists(Path.Combine(helperDirectory, "de", "resources.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(helperDirectory, "user-notes.txt")));
    }
}
