using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaFileRenamer.App.Services;

internal sealed record UpdatePackage(
    Version Version,
    Uri ReleaseUri,
    Uri ZipUri,
    Uri ChecksumUri);

internal sealed record UpdateLaunchPlan(
    int ParentProcessId,
    string InstallationDirectory,
    string PackagePath,
    string ExpectedSha256,
    IReadOnlyList<string> RelaunchArguments);

internal sealed record UpdateInstallStatus(
    string Stage,
    bool IsTerminal,
    bool Succeeded,
    string? Message);

internal sealed record UpdateInstallOutcome(bool Succeeded, string Message);

internal sealed class UpdateDownloadService
{
    internal const string PackageAssetName = "MediaFileRenamer-win-x64.zip";
    internal const string ChecksumAssetName = PackageAssetName + ".sha256";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateClient();
    private readonly HttpClient _httpClient;

    public UpdateDownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task DownloadAndLaunchAsync(
        UpdatePackage package,
        IReadOnlyList<string> relaunchArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(relaunchArguments);
        ValidateOfficialPackage(package);

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)
            || !File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                "The current application executable could not be located for update.");
        }

        var installationDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(installationDirectory)
            || !UpdateInstallService.IsSafeInstallationDirectory(installationDirectory))
        {
            throw new InvalidOperationException(
                "The current application folder is not safe to update automatically.");
        }

        var sessionDirectory = CreateSessionDirectory();
        try
        {
            var packagePath = Path.Combine(sessionDirectory, PackageAssetName);
            var checksumPath = Path.Combine(sessionDirectory, ChecksumAssetName);
            await DownloadFileAsync(package.ZipUri, packagePath, cancellationToken);
            await DownloadFileAsync(package.ChecksumUri, checksumPath, cancellationToken);

            var expectedChecksum = ParseChecksum(
                await File.ReadAllTextAsync(checksumPath, cancellationToken),
                PackageAssetName);
            await VerifyChecksumAsync(packagePath, expectedChecksum, cancellationToken);

            var helperPath = CopyHelperRuntime(executablePath, sessionDirectory);

            var planPath = Path.Combine(sessionDirectory, "update-plan.json");
            var plan = new UpdateLaunchPlan(
                Environment.ProcessId,
                installationDirectory,
                packagePath,
                expectedChecksum,
                relaunchArguments.ToArray());
            await File.WriteAllTextAsync(
                planPath,
                JsonSerializer.Serialize(plan),
                cancellationToken);

            var helper = Process.Start(new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                WorkingDirectory = sessionDirectory,
                ArgumentList = { "--apply-update-plan", planPath }
            });
            if (helper is null)
            {
                throw new InvalidOperationException("The update helper could not be started.");
            }

            await WaitForHelperReadyAsync(
                helper,
                UpdateInstallService.GetStatusPath(planPath),
                cancellationToken);
        }
        catch
        {
            TryDeleteDirectory(sessionDirectory);
            throw;
        }
    }

    internal static string ParseChecksum(string checksumContents, string expectedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksumContents);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFileName);

        var line = checksumContents
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SingleOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException("The update checksum file is empty.");
        }

        var parts = line.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !string.Equals(parts[1].TrimStart('*'), expectedFileName, StringComparison.Ordinal)
            || parts[0].Length != 64
            || !parts[0].All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("The update checksum file is invalid.");
        }

        return parts[0].ToUpperInvariant();
    }

    internal static async Task VerifyChecksumAsync(
        string packagePath,
        string expectedChecksum,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(packagePath);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        VerifyCalculatedChecksum(actual, expectedChecksum);
    }

    internal static void VerifyChecksum(string packagePath, string expectedChecksum)
    {
        using var stream = File.OpenRead(packagePath);
        VerifyCalculatedChecksum(
            Convert.ToHexString(SHA256.HashData(stream)),
            expectedChecksum);
    }

    private static void VerifyCalculatedChecksum(string actual, string expectedChecksum)
    {
        if (!string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The downloaded update did not match its published SHA-256 checksum.");
        }
    }

    internal static void ValidateOfficialPackage(UpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!IsOfficialAssetUri(package.ZipUri, PackageAssetName)
            || !IsOfficialAssetUri(package.ChecksumUri, ChecksumAssetName))
        {
            throw new InvalidOperationException(
                "The update download location was not an official GitHub release asset.");
        }
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("MediaFileRenamer-Updater/1.0");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length
            && length > MaximumPackageBytes)
        {
            throw new InvalidOperationException("The update package is unexpectedly large.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 131072,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
        if (output.Length > MaximumPackageBytes)
        {
            throw new InvalidOperationException("The update package is unexpectedly large.");
        }
    }

    private static bool IsOfficialAssetUri(Uri uri, string assetName)
    {
        return uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(
                "/chris-lansman/media-file-renamer/releases/download/",
                StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith(
                "/" + assetName,
                StringComparison.Ordinal);
    }

    private static string CreateSessionDirectory()
    {
        var directory = Path.Combine(
            UpdateInstallService.SessionRoot,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static string CopyHelperRuntime(
        string executablePath,
        string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);

        var installationDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException(
                "The current application executable has no installation folder.");
        var helperDirectory = Path.Combine(sessionDirectory, "update-helper");
        var helperPath = Path.Combine(
            helperDirectory,
            Path.GetFileName(executablePath));
        Directory.CreateDirectory(helperDirectory);
        File.Copy(executablePath, helperPath, overwrite: true);

        foreach (var runtimeFile in Directory.EnumerateFiles(
                     installationDirectory,
                     "*.dll",
                     SearchOption.AllDirectories)
                 .Concat(Directory.EnumerateFiles(
                     installationDirectory,
                     "*.deps.json",
                     SearchOption.TopDirectoryOnly))
                 .Concat(Directory.EnumerateFiles(
                     installationDirectory,
                     "*.runtimeconfig.json",
                     SearchOption.TopDirectoryOnly)))
        {
            var relativePath = Path.GetRelativePath(
                installationDirectory,
                runtimeFile);
            var destination = Path.GetFullPath(Path.Combine(
                helperDirectory,
                relativePath));
            var fullHelperDirectory = Path.GetFullPath(helperDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(
                    fullHelperDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The current application folder contains an unsafe runtime path.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(runtimeFile, destination, overwrite: true);
        }

        return helperPath;
    }

    private static HttpClient CreateClient() => new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A partial download is harmless and is cleaned up on a later startup.
        }
    }

    private static async Task WaitForHelperReadyAsync(
        Process helper,
        string statusPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (UpdateInstallService.TryReadStatus(statusPath, out var status)
                && status is not null)
            {
                if (string.Equals(
                        status.Stage,
                        UpdateInstallService.WaitingForParentStage,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (status.IsTerminal)
                {
                    throw new InvalidOperationException(
                        status.Message
                        ?? "The update helper could not prepare the installation.");
                }
            }

            if (helper.HasExited)
            {
                throw new InvalidOperationException(
                    "The update helper stopped before it could prepare the installation.");
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new InvalidOperationException(
            "The update helper did not confirm that it was ready. The current version is still open.");
    }
}

internal static class UpdateInstallService
{
    private const string ApplyUpdateOption = "--apply-update-plan";
    private const string UpdateResultOption = "--show-update-result";
    private const string ApplicationExecutableName = "MediaFileRenamer.exe";
    private const string StatusFileName = "update-status.json";
    internal const string WaitingForParentStage = "waiting-for-parent";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string SessionRoot => Path.Combine(
        Path.GetTempPath(),
        "MediaFileRenamer",
        "updates");

    internal static string GetStatusPath(string planPath)
    {
        var sessionDirectory = Path.GetDirectoryName(planPath);
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            throw new ArgumentException(
                "The update plan has no session directory.",
                nameof(planPath));
        }

        return Path.Combine(sessionDirectory, StatusFileName);
    }

    public static bool TryApplyFromArguments(
        IReadOnlyList<string> arguments,
        out string? failureMessage)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        failureMessage = null;
        var matchingIndexes = arguments
            .Select((argument, index) => new { argument, index })
            .Where(value => string.Equals(
                value.argument,
                ApplyUpdateOption,
                StringComparison.OrdinalIgnoreCase))
            .Select(value => value.index)
            .ToList();
        if (matchingIndexes.Count == 0)
        {
            return false;
        }

        if (matchingIndexes.Count != 1
            || matchingIndexes[0] + 1 >= arguments.Count
            || string.IsNullOrWhiteSpace(arguments[matchingIndexes[0] + 1]))
        {
            failureMessage = "The update helper received invalid startup information.";
            return true;
        }

        var planPath = arguments[matchingIndexes[0] + 1];
        var reporter = new UpdateStatusReporter(GetStatusPath(planPath));
        reporter.Write("helper-started");
        try
        {
            ApplyUpdatePlan(planPath, restartApplication: true, reporter);
        }
        catch (Exception ex)
        {
            failureMessage = "The update could not be installed. Your current version was left in place. "
                + ex.Message;
            reporter.Write("failed", isTerminal: true, succeeded: false, failureMessage);
            TryRestartPreviousApplication(planPath, reporter.StatusPath);
        }

        return true;
    }

    internal static void ApplyUpdatePlan(
        string planPath,
        bool restartApplication,
        UpdateStatusReporter? reporter = null)
    {
        if (string.IsNullOrWhiteSpace(planPath)
            || !Path.IsPathFullyQualified(planPath)
            || !File.Exists(planPath))
        {
            throw new ArgumentException("The update plan file could not be found.", nameof(planPath));
        }

        var plan = JsonSerializer.Deserialize<UpdateLaunchPlan>(
            File.ReadAllText(planPath),
            JsonOptions)
            ?? throw new InvalidOperationException("The update plan could not be read.");
        ValidatePlan(plan);
        reporter?.Write(WaitingForParentStage);
        WaitForParentExit(plan.ParentProcessId);
        reporter?.Write("verifying-package");
        // This runs on the WPF helper's startup thread. Do not block that thread on an
        // async operation, because its continuation would need the same dispatcher.
        UpdateDownloadService.VerifyChecksum(plan.PackagePath, plan.ExpectedSha256);

        var sessionDirectory = Path.GetDirectoryName(planPath)
            ?? throw new InvalidOperationException("The update plan has no session directory.");
        var stagingDirectory = Path.Combine(sessionDirectory, "staged-update");
        reporter?.Write("extracting-package");
        ExtractPackage(plan.PackagePath, stagingDirectory);
        reporter?.Write("installing-update");
        ApplyStagedFiles(stagingDirectory, plan.InstallationDirectory);

        if (restartApplication)
        {
            reporter?.Write("restarting-application", isTerminal: true, succeeded: true,
                "The update was installed successfully.");
            StartApplication(plan, reporter?.StatusPath);
        }
    }

    internal static bool IsSafeInstallationDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(root)
            && !string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(fullPath, ApplicationExecutableName));
    }

    internal static void CleanupStaleSessions()
    {
        if (!Directory.Exists(SessionRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var directory in Directory.EnumerateDirectories(SessionRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Another helper can still be exiting; retry on a later startup.
            }
        }
    }

    internal static IReadOnlyList<string> RemoveUpdateResultArgument(
        IReadOnlyList<string> arguments,
        out UpdateInstallOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        outcome = null;
        var remaining = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], UpdateResultOption, StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(arguments[index]);
                continue;
            }

            if (index + 1 < arguments.Count
                && TryReadOutcome(arguments[index + 1], out var readOutcome))
            {
                outcome = readOutcome;
            }

            index++;
        }

        return remaining;
    }

    private static void ValidatePlan(UpdateLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsSafeInstallationDirectory(plan.InstallationDirectory)
            || !File.Exists(plan.PackagePath)
            || plan.ExpectedSha256.Length != 64
            || !plan.ExpectedSha256.All(Uri.IsHexDigit)
            || plan.RelaunchArguments.Any(argument => string.Equals(
                argument,
                ApplyUpdateOption,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The update plan contains unsafe paths or values.");
        }
    }

    private static void StartApplication(UpdateLaunchPlan plan, string? statusPath)
    {
        var executablePath = Path.Combine(
            plan.InstallationDirectory,
            ApplicationExecutableName);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = plan.InstallationDirectory
        };
        foreach (var argument in plan.RelaunchArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(statusPath))
        {
            startInfo.ArgumentList.Add(UpdateResultOption);
            startInfo.ArgumentList.Add(statusPath);
        }

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("The updated application could not be restarted.");
        }
    }

    private static void TryRestartPreviousApplication(string planPath, string statusPath)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<UpdateLaunchPlan>(
                File.ReadAllText(planPath),
                JsonOptions);
            if (plan is not null)
            {
                ValidatePlan(plan);
                StartApplication(plan, statusPath);
            }
        }
        catch
        {
            // The status file remains in the update session for diagnosis if relaunching fails.
        }
    }

    private static bool TryReadOutcome(string statusPath, out UpdateInstallOutcome? outcome)
    {
        outcome = null;
        if (!IsPathInSessionRoot(statusPath)
            || !TryReadStatus(statusPath, out var status)
            || status is null
            || !status.IsTerminal)
        {
            return false;
        }

        outcome = new UpdateInstallOutcome(
            status.Succeeded,
            status.Message ?? (status.Succeeded
                ? "The update was installed successfully."
                : "The update could not be installed. Your previous version is still installed."));
        return true;
    }

    internal static bool TryReadStatus(string statusPath, out UpdateInstallStatus? status)
    {
        status = null;
        try
        {
            if (!File.Exists(statusPath))
            {
                return false;
            }

            status = JsonSerializer.Deserialize<UpdateInstallStatus>(
                File.ReadAllText(statusPath),
                JsonOptions);
            return status is not null && !string.IsNullOrWhiteSpace(status.Stage);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    private static bool IsPathInSessionRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var root = Path.GetFullPath(SessionRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void WaitForParentExit(int parentProcessId)
    {
        if (parentProcessId <= 0)
        {
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            if (!parent.HasExited && !parent.WaitForExit(90000))
            {
                throw new InvalidOperationException(
                    "The current application did not close in time to install the update.");
            }
        }
        catch (ArgumentException)
        {
            // The app has already exited, which is the expected handoff path.
        }
    }

    private static void ExtractPackage(string packagePath, string stagingDirectory)
    {
        Directory.CreateDirectory(stagingDirectory);
        var fullStagingDirectory = Path.GetFullPath(stagingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
            if (!destination.StartsWith(fullStagingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The update archive contains an unsafe file path.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        if (!File.Exists(Path.Combine(stagingDirectory, ApplicationExecutableName)))
        {
            throw new InvalidOperationException("The update archive does not contain the application executable.");
        }
    }

    private static void ApplyStagedFiles(string stagingDirectory, string installationDirectory)
    {
        var backupDirectory = Path.Combine(
            Path.GetDirectoryName(stagingDirectory)!,
            "rollback-backup");
        var changes = new List<FileChange>();
        var fullInstallationDirectory = Path.GetFullPath(installationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        try
        {
            foreach (var stagedFile in Directory.EnumerateFiles(
                         stagingDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagingDirectory, stagedFile);
                var targetPath = Path.GetFullPath(Path.Combine(installationDirectory, relativePath));
                if (!targetPath.StartsWith(fullInstallationDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The update archive contains an unsafe file path.");
                }

                var backupPath = Path.Combine(backupDirectory, relativePath);
                var existed = File.Exists(targetPath);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(targetPath, backupPath, overwrite: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                changes.Add(new FileChange(targetPath, backupPath, existed));
                File.Copy(stagedFile, targetPath, overwrite: true);
            }
        }
        catch
        {
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                if (change.Existed)
                {
                    File.Copy(change.BackupPath, change.TargetPath, overwrite: true);
                }
                else if (File.Exists(change.TargetPath))
                {
                    File.Delete(change.TargetPath);
                }
            }

            throw;
        }
    }

    private sealed record FileChange(string TargetPath, string BackupPath, bool Existed);

    internal sealed class UpdateStatusReporter
    {
        public UpdateStatusReporter(string statusPath)
        {
            StatusPath = statusPath;
        }

        public string StatusPath { get; }

        public void Write(
            string stage,
            bool isTerminal = false,
            bool succeeded = false,
            string? message = null)
        {
            var directory = Path.GetDirectoryName(StatusPath)
                ?? throw new InvalidOperationException("The update status file has no directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = StatusPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(new UpdateInstallStatus(
                        stage,
                        isTerminal,
                        succeeded,
                        message)));
                File.Move(temporaryPath, StatusPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
