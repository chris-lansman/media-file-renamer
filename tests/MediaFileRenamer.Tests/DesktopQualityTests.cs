using MediaFileRenamer.App;
using MediaFileRenamer.App.Services;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class DiagnosticLogTests
{
    [TestMethod]
    public void Redact_RemovesCredentialsAndUserProfile()
    {
        using var directory = new DesktopTestDirectory();
        var logger = new DiagnosticLogService(directory.Path);
        logger.RegisterSensitiveValue("direct-secret-value");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = logger.Redact(
            $"""api_key=query-secret "TvdbPin": "json-secret" Authorization: Bearer token-secret """
            + $"direct-secret-value {profile}\\Videos\\Movie.mkv");

        Assert.DoesNotContain("query-secret", result);
        Assert.DoesNotContain("json-secret", result);
        Assert.DoesNotContain("token-secret", result);
        Assert.DoesNotContain("direct-secret-value", result);
        Assert.Contains("[REDACTED]", result);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            Assert.DoesNotContain(profile, result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[PATH]", result);
        }
    }

    [TestMethod]
    public void Information_RotatesAndRetainsBoundedLogFiles()
    {
        using var directory = new DesktopTestDirectory();
        var logger = new DiagnosticLogService(
            directory.Path,
            maximumLogBytes: 300,
            retainedLogCount: 3);

        for (var index = 0; index < 10; index++)
        {
            logger.Information($"{index}: {new string('x', 180)}");
        }

        var files = Directory.GetFiles(directory.Path, "MediaFileRenamer*.log");
        Assert.IsLessThanOrEqualTo(3, files.Length);
        Assert.IsTrue(File.Exists(logger.CurrentLogPath));
        Assert.IsTrue(files.Any(path => path.EndsWith(".1.log", StringComparison.Ordinal)));
    }
}

[TestClass]
public sealed class MetadataProviderTestServiceTests
{
    [TestMethod]
    public async Task TestTmdb_ReportsSuccessAndUsesAuthenticationEndpoint()
    {
        var handler = new ProviderStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new MetadataProviderTestService(new HttpClient(handler));

        var result = await service.TestTmdbAsync("tmdb-key");

        Assert.IsTrue(result.IsSuccess);
        Assert.Contains("successfully", result.Message);
        Assert.AreEqual(
            "https://api.themoviedb.org/3/authentication?api_key=tmdb-key",
            handler.LastRequestUri?.AbsoluteUri);
    }

    [TestMethod]
    public async Task TestTmdb_UnauthorizedResponseIsActionableWithoutEchoingKey()
    {
        var handler = new ProviderStubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var service = new MetadataProviderTestService(new HttpClient(handler));

        var result = await service.TestTmdbAsync("private-key");

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("rejected", result.Message);
        Assert.DoesNotContain("private-key", result.Message);
    }

    [TestMethod]
    public async Task TestTvdb_SendsKeyAndPinToLogin()
    {
        var handler = new ProviderStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new MetadataProviderTestService(new HttpClient(handler));

        var result = await service.TestTvdbAsync("tvdb-key", "subscriber-pin");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("https://api4.thetvdb.com/v4/login", handler.LastRequestUri?.AbsoluteUri);
        Assert.Contains("\"apikey\":\"tvdb-key\"", handler.LastRequestBody);
        Assert.Contains("\"pin\":\"subscriber-pin\"", handler.LastRequestBody);
    }

    [TestMethod]
    public async Task TestTvdb_MissingKeyDoesNotMakeNetworkRequest()
    {
        var handler = new ProviderStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new MetadataProviderTestService(new HttpClient(handler));

        var result = await service.TestTvdbAsync("", "");

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("Enter a TVDB API key", result.Message);
        Assert.IsNull(handler.LastRequestUri);
    }
}

[TestClass]
public sealed class DesktopWindowQualityTests
{
    [STATestMethod]
    public void SettingsWindow_IsResizableAccessibleAndKeepsCredentialsVisible()
    {
        _ = Application.Current ?? new Application();
        var settings = new AppSettings
        {
            TmdbApiKey = "visible-tmdb",
            TvdbApiKey = "visible-tvdb",
            TvdbPin = "visible-pin"
        };
        var window = new SettingsWindow(settings, @"C:\settings.json");

        Assert.AreEqual(System.Windows.ResizeMode.CanResize, window.ResizeMode);
        Assert.AreEqual(
            "Media File Renamer settings",
            AutomationProperties.GetName(window));
        Assert.AreEqual(
            "visible-tmdb",
            ((TextBox)window.FindName("TmdbApiKeyBox")).Text);
        Assert.AreEqual(
            "visible-tvdb",
            ((TextBox)window.FindName("TvdbApiKeyBox")).Text);
        Assert.AreEqual(
            "visible-pin",
            ((TextBox)window.FindName("TvdbPinBox")).Text);
        Assert.IsNotNull(window.FindName("TestTmdbButton"));
        Assert.IsNotNull(window.FindName("TestTvdbButton"));
        window.Close();
    }

    [STATestMethod]
    public void SettingsWindow_FirstRunExplainsSafetyAndDefaultsToCopy()
    {
        _ = Application.Current ?? new Application();
        var window = new SettingsWindow(
            new AppSettings(),
            @"C:\settings.json",
            isFirstRun: true);

        Assert.AreEqual(
            Visibility.Visible,
            ((FrameworkElement)window.FindName("FirstRunBanner")).Visibility);
        Assert.AreEqual(
            1,
            ((ComboBox)window.FindName("DefaultOperationComboBox")).SelectedIndex);
        Assert.AreEqual(
            "_Save and Start",
            ((Button)window.FindName("SaveSettingsButton")).Content);
        window.Close();
    }

    [STATestMethod]
    public void AboutWindow_ContainsProviderAttributionAndCredentialPolicy()
    {
        _ = Application.Current ?? new Application();
        var window = new AboutWindow();

        var tmdbNotice = (TextBlock)window.FindName("TmdbNoticeTextBlock");
        var tvdbNotice = (TextBlock)window.FindName("TvdbAttributionTextBlock");
        var credentialPolicy =
            (TextBlock)window.FindName("BringYourOwnCredentialsTextBlock");
        Assert.AreEqual(
            "This product uses the TMDB API but is not endorsed or certified by TMDB.",
            tmdbNotice.Text);
        Assert.Contains("TheTVDB", tvdbNotice.Text);
        Assert.Contains("Each user supplies", credentialPolicy.Text);
        Assert.IsNotNull(window.FindName("TmdbLogo"));
        window.Close();
    }

    [STATestMethod]
    public void RecoveryWindow_WithNoInterruptedOperationsIsNonDestructive()
    {
        _ = Application.Current ?? new Application();
        using var directory = new DesktopTestDirectory();
        var window = new RecoveryWindow(
            new OperationJournalService(directory.Path),
            []);

        Assert.HasCount(0, window.Recoveries);
        Assert.IsFalse(((Button)window.FindName("RollbackButton")).IsEnabled);
        Assert.IsFalse(((Button)window.FindName("MarkResolvedButton")).IsEnabled);
        StringAssert.Contains(
            ((TextBlock)window.FindName("RecoveryStatusTextBlock")).Text,
            "No interrupted operations");
        window.Close();
    }
}

internal sealed class ProviderStubHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }

    public string LastRequestBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestBody = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return responseFactory(request);
    }
}

internal sealed class DesktopTestDirectory : IDisposable
{
    public DesktopTestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"MediaFileRenamer-Desktop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
