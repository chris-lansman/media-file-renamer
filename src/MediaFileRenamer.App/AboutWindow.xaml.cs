using MediaFileRenamer.App.Services;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Navigation;

namespace MediaFileRenamer.App;

public partial class AboutWindow : Window
{
    private static readonly Uri ReleasesPageUri = new(
        "https://github.com/chris-lansman/media-file-renamer/releases/latest");
    private readonly IUpdateChecker _updateChecker;
    private readonly UpdateDownloadService _updateDownloadService;
    private readonly Version _currentVersion;
    private CancellationTokenSource? _updateCancellation;
    private Uri _latestReleaseUri = ReleasesPageUri;
    private UpdateCheckResult? _latestUpdate;

    public AboutWindow()
        : this(new GitHubReleaseUpdateChecker(), GetApplicationVersion())
    {
    }

    internal AboutWindow(
        IUpdateChecker updateChecker,
        Version currentVersion,
        UpdateDownloadService? updateDownloadService = null,
        UpdateCheckResult? initialUpdate = null)
    {
        InitializeComponent();
        _updateChecker = updateChecker;
        _currentVersion = currentVersion;
        _updateDownloadService = updateDownloadService ?? new UpdateDownloadService();
        VersionTextBlock.Text = $"Version {_currentVersion.ToString(3)}";
        if (initialUpdate is not null)
        {
            ApplyUpdateResult(initialUpdate);
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("Could not open an attribution link.", ex);
            System.Windows.MessageBox.Show(
                this,
                "Windows could not open this link. Check your default browser and try again.",
                "Could not open link",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DiagnosticLog.Current.BuildDiagnosticSummary());
            CopyDiagnosticsButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("Could not copy the diagnostic summary.", ex);
            System.Windows.MessageBox.Show(
                this,
                "Windows could not copy the diagnostic summary to the clipboard.",
                "Could not copy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        _updateCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusTextBlock.Foreground =
            (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        UpdateStatusTextBlock.Text = "Checking GitHub Releases...";

        try
        {
            var result = await _updateChecker.CheckAsync(
                _currentVersion,
                _updateCancellation.Token);
            ApplyUpdateResult(result);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusTextBlock.Text =
                "The update check timed out. Check your connection and try again, or open the release page.";
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or JsonException
                or InvalidOperationException)
        {
            DiagnosticLog.Current.Error("Could not check for updates.", ex);
            UpdateStatusTextBlock.Text =
                "Updates could not be checked right now. You can keep using this version, try again later, or open the release page.";
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var package = _latestUpdate?.Package;
        if (package is null)
        {
            OpenRelease_Click(sender, e);
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"Download and install version {package.Version.ToString(3)} now? "
            + "The app will close briefly and restart when the verified update is ready.",
            "Install update",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        _updateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        CheckForUpdatesButton.IsEnabled = false;
        OpenReleaseButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusTextBlock.Foreground =
            (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        UpdateStatusTextBlock.Text =
            $"Downloading and verifying version {package.Version.ToString(3)}...";

        try
        {
            await _updateDownloadService.DownloadAndLaunchAsync(
                package,
                GetRelaunchArguments(),
                _updateCancellation.Token);
            UpdateStatusTextBlock.Text = "Update verified. Closing to install and restart...";
            await Task.Delay(250);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            UpdateStatusTextBlock.Text =
                "The update download timed out or was cancelled. Your current version is still installed.";
            RestoreUpdateActions();
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            DiagnosticLog.Current.Error("Could not download or start the update helper.", ex);
            UpdateStatusTextBlock.Text =
                "The update could not be installed. Your current version is still installed; try again or open the release page.";
            RestoreUpdateActions();
        }
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        OpenUri(_latestReleaseUri, "Windows could not open the release page.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        base.OnClosed(e);
    }

    private static IReadOnlyList<string> GetRelaunchArguments()
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var relaunchArguments = new List<string>(arguments.Length);
        for (var index = 0; index < arguments.Length; index++)
        {
            if (string.Equals(
                    arguments[index],
                    "--apply-update-plan",
                    StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            relaunchArguments.Add(arguments[index]);
        }

        return relaunchArguments;
    }

    private void RestoreUpdateActions()
    {
        CheckForUpdatesButton.IsEnabled = true;
        OpenReleaseButton.IsEnabled = true;
        InstallUpdateButton.IsEnabled = _latestUpdate?.CanInstall == true;
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        _latestUpdate = result;
        _latestReleaseUri = result.ReleaseUri;
        InstallUpdateButton.Visibility = result.CanInstall
            ? Visibility.Visible
            : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = result.CanInstall;
        UpdateStatusTextBlock.Foreground =
            (System.Windows.Media.Brush)FindResource(
                result.UpdateAvailable ? "ReviewBrush" : "MatchBrush");
        UpdateStatusTextBlock.Text = result.Message;
    }

    private void OpenUri(Uri uri, string failureMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("Could not open a web link.", ex);
            System.Windows.MessageBox.Show(
                this,
                failureMessage,
                "Could not open link",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static Version GetApplicationVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0);
}

internal interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken);
}

internal sealed class GitHubReleaseUpdateChecker : IUpdateChecker
{
    internal const string LatestReleaseApiUrl =
        "https://api.github.com/repos/chris-lansman/media-file-renamer/releases/latest";
    private static readonly HttpClient SharedHttpClient = CreateClient();
    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateChecker(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            LatestReleaseApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("MediaFileRenamer-UpdateCheck/1.0");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        var releaseUrl = root.GetProperty("html_url").GetString();
        if (!TryParseReleaseVersion(tag, out var latestVersion)
            || !Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri)
            || releaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "GitHub returned incomplete release information.");
        }

        var package = TryGetPackage(root, latestVersion, releaseUri);
        var updateAvailable = latestVersion > Normalize(currentVersion);
        var message = updateAvailable
            ? package is not null
                ? $"Version {latestVersion.ToString(3)} is available. Choose Install update to download, verify, and restart."
                : $"Version {latestVersion.ToString(3)} is available. Open the release page to download it."
            : $"You are up to date. Version {currentVersion.ToString(3)} is the latest release.";
        return new UpdateCheckResult(
            updateAvailable,
            latestVersion,
            releaseUri,
            package,
            message);
    }

    private static UpdatePackage? TryGetPackage(
        JsonElement root,
        Version latestVersion,
        Uri releaseUri)
    {
        if (!root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Uri? packageUri = null;
        Uri? checksumUri = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var download = asset.TryGetProperty("browser_download_url", out var downloadElement)
                ? downloadElement.GetString()
                : null;
            if (!Uri.TryCreate(download, UriKind.Absolute, out var downloadUri))
            {
                continue;
            }

            if (string.Equals(name, UpdateDownloadService.PackageAssetName, StringComparison.Ordinal))
            {
                packageUri = downloadUri;
            }
            else if (string.Equals(name, UpdateDownloadService.ChecksumAssetName, StringComparison.Ordinal))
            {
                checksumUri = downloadUri;
            }
        }

        if (packageUri is null || checksumUri is null)
        {
            return null;
        }

        var package = new UpdatePackage(
            latestVersion,
            releaseUri,
            packageUri,
            checksumUri);
        try
        {
            UpdateDownloadService.ValidateOfficialPackage(package);
            return package;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool TryParseReleaseVersion(
        string? tag,
        out Version version)
    {
        var candidate = tag?.Trim();
        if (candidate?.StartsWith("v", StringComparison.OrdinalIgnoreCase) == true)
        {
            candidate = candidate[1..];
        }

        var suffix = candidate?.IndexOfAny(['-', '+']) ?? -1;
        if (suffix >= 0)
        {
            candidate = candidate![..suffix];
        }

        return Version.TryParse(candidate, out version!);
    }

    private static Version Normalize(Version version) =>
        new(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));

    private static HttpClient CreateClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
}

internal sealed record UpdateCheckResult(
    bool UpdateAvailable,
    Version LatestVersion,
    Uri ReleaseUri,
    UpdatePackage? Package,
    string Message)
{
    public bool CanInstall => UpdateAvailable && Package is not null;
}
