using MediaFileRenamer.App.ViewModels;
using System.IO;

namespace MediaFileRenamer.App.Services;

public enum FileOperation
{
    Move,
    Copy
}

public sealed record FileTransferProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentFile,
    long BytesTransferred,
    long TotalBytes);

public sealed class RenameApplier
{
    private const int CopyBufferSize = 1024 * 1024;
    private readonly RenamePlanner _planner = new();
    private readonly OperationJournalService? _journals;

    public RenameApplier(OperationJournalService? journals = null)
    {
        _journals = journals;
    }

    public RenameResult Apply(IEnumerable<MediaPreviewItem> items, FileOperation operation)
    {
        return ApplyAsync(items, operation).GetAwaiter().GetResult();
    }

    public async Task<RenameResult> ApplyAsync(
        IEnumerable<MediaPreviewItem> items,
        FileOperation operation,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var itemList = items.ToList();
        var preflight = Preflight(itemList, operation);
        if (!preflight.IsValid)
        {
            if (preflight.FailedItem is not null)
            {
                preflight.FailedItem.Status = $"Failed: {preflight.Error}";
            }

            return new RenameResult(0, [], FailureMessage: preflight.Error);
        }

        var noOpItems = itemList
            .Where(item => string.Equals(
                Path.GetFullPath(item.SourcePath),
                Path.GetFullPath(item.DestinationPath),
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var journal = _journals?.Create(operation, preflight.Transfers);
        var completedTransfers = new List<PlannedTransfer>();
        var totalBytes = preflight.Transfers.Sum(transfer => transfer.Length);
        long bytesTransferred = 0;

        try
        {
            for (var index = 0; index < preflight.Transfers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transfer = preflight.Transfers[index];
                journal?.MarkInProgress(transfer.SourcePath, transfer.DestinationPath);

                var transferProgress = progress is null
                    ? null
                    : new InlineProgress<long>(current =>
                    {
                        progress.Report(new FileTransferProgress(
                            index,
                            preflight.Transfers.Count,
                            Path.GetFileName(transfer.SourcePath),
                            bytesTransferred + current,
                            totalBytes));
                    });

                await ExecuteTransferAsync(
                    transfer,
                    operation,
                    transferProgress,
                    cancellationToken);

                bytesTransferred += transfer.Length;
                completedTransfers.Add(transfer);
                journal?.MarkCompleted(transfer.SourcePath, transfer.DestinationPath);
                progress?.Report(new FileTransferProgress(
                    index + 1,
                    preflight.Transfers.Count,
                    Path.GetFileName(transfer.SourcePath),
                    bytesTransferred,
                    totalBytes));
            }
        }
        catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
        {
            var rollbackErrors = Rollback(completedTransfers, operation, journal);
            var error = ex is OperationCanceledException
                ? "Operation canceled; completed transfers were rolled back."
                : $"Transfer failed and the batch was rolled back: {ex.Message}";
            if (rollbackErrors.Count > 0)
            {
                error += $" Manual recovery is required for {rollbackErrors.Count} file(s).";
            }

            journal?.MarkFailed(error, rollbackErrors);
            foreach (var item in itemList)
            {
                item.Status = rollbackErrors.Count == 0
                    ? "Rolled back; no batch changes kept"
                    : "Failed: rollback incomplete; open operation history";
            }

            return new RenameResult(
                0,
                [],
                journal?.Path,
                RolledBack: rollbackErrors.Count == 0,
                FailureMessage: error);
        }

        foreach (var item in itemList)
        {
            item.Status = noOpItems.Contains(item)
                ? "Already named"
                : operation == FileOperation.Move ? "Moved" : "Copied";
        }

        journal?.MarkCompleted();
        var sourceDirectories = operation == FileOperation.Move
            ? preflight.Transfers
                .Select(transfer => Path.GetDirectoryName(transfer.SourcePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var deletedFolders = operation == FileOperation.Move
            ? DeleteEmptySourceFolders(sourceDirectories)
            : 0;

        return new RenameResult(
            deletedFolders,
            itemList,
            journal?.Path);
    }

    private BatchPreflight Preflight(
        IReadOnlyList<MediaPreviewItem> items,
        FileOperation operation)
    {
        if (items.Count == 0)
        {
            return BatchPreflight.Invalid("No files were supplied.");
        }

        if (items.Any(item => item.RequiresReview))
        {
            return BatchPreflight.Invalid("Every item must be reviewed before applying.");
        }

        var transfers = new List<PlannedTransfer>();
        foreach (var item in items)
        {
            var validationError = ValidateItem(item);
            if (validationError is not null)
            {
                return BatchPreflight.Invalid(validationError, item);
            }

            var source = Path.GetFullPath(item.SourcePath);
            var destination = Path.GetFullPath(item.DestinationPath);
            if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            {
                transfers.Add(new PlannedTransfer(
                    item,
                    source,
                    destination,
                    IsCompanion: false,
                    new FileInfo(source).Length));
            }

            foreach (var companion in _planner.BuildCompanionDestinations(item))
            {
                if (!File.Exists(companion.SourcePath))
                {
                    return BatchPreflight.Invalid(
                        $"Companion file no longer exists: {Path.GetFileName(companion.SourcePath)}",
                        item);
                }

                var companionSource = Path.GetFullPath(companion.SourcePath);
                var companionReadError = ValidateReadable(companionSource);
                if (companionReadError is not null)
                {
                    return BatchPreflight.Invalid(companionReadError, item);
                }

                var companionDestination = Path.GetFullPath(companion.DestinationPath);
                if (!companionSource.Equals(companionDestination, StringComparison.OrdinalIgnoreCase))
                {
                    transfers.Add(new PlannedTransfer(
                        item,
                        companionSource,
                        companionDestination,
                        IsCompanion: true,
                        new FileInfo(companionSource).Length));
                }
            }
        }

        var duplicateDestination = transfers
            .GroupBy(transfer => transfer.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDestination is not null)
        {
            return BatchPreflight.Invalid(
                $"Multiple files map to {duplicateDestination.Key}.",
                duplicateDestination.First().Item);
        }

        var duplicateSource = transfers
            .GroupBy(transfer => transfer.SourcePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSource is not null)
        {
            return BatchPreflight.Invalid(
                $"A source file is included more than once: {duplicateSource.Key}.",
                duplicateSource.First().Item);
        }

        foreach (var transfer in transfers)
        {
            if (File.Exists(transfer.DestinationPath))
            {
                return BatchPreflight.Invalid(
                    $"Destination already exists: {transfer.DestinationPath}",
                    transfer.Item);
            }

            var destinationDirectory = Path.GetDirectoryName(transfer.DestinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                return BatchPreflight.Invalid("A destination has no parent directory.", transfer.Item);
            }

            try
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return BatchPreflight.Invalid(
                    $"Cannot create the destination folder: {ex.Message}",
                    transfer.Item);
            }
        }

        foreach (var destinationDirectory in transfers
                     .Select(transfer => Path.GetDirectoryName(transfer.DestinationPath))
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var probe = Path.Combine(
                destinationDirectory,
                $".mfr-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(
                    probe,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return BatchPreflight.Invalid(
                    $"The destination is not writable: {destinationDirectory}. {ex.Message}");
            }
            finally
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
        }

        var spaceError = ValidateFreeSpace(transfers, operation);
        return spaceError is null
            ? BatchPreflight.Valid(transfers)
            : BatchPreflight.Invalid(spaceError);
    }

    private static string? ValidateItem(MediaPreviewItem item)
    {
        if (item.MediaType == "Unknown")
        {
            return "Choose Movie or TV for every item.";
        }

        if (item.MediaType == "TV" && (item.Season is null || item.Episode is null))
        {
            return "Every TV item needs a resolved season and episode number.";
        }

        if (!File.Exists(item.SourcePath))
        {
            return $"Source file no longer exists: {item.SourcePath}";
        }

        if (string.IsNullOrWhiteSpace(item.DestinationPath))
        {
            return "Every item needs a valid destination.";
        }

        return ValidateReadable(item.SourcePath);
    }

    private static string? ValidateReadable(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Source file cannot be read: {ex.Message}";
        }
    }

    private static string? ValidateFreeSpace(
        IReadOnlyList<PlannedTransfer> transfers,
        FileOperation operation)
    {
        var requiredByRoot = transfers
            .Where(transfer => operation == FileOperation.Copy || !IsSameVolume(
                transfer.SourcePath,
                transfer.DestinationPath))
            .GroupBy(
                transfer => Path.GetPathRoot(transfer.DestinationPath) ?? "",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Root = group.Key, Bytes = group.Sum(item => item.Length) });

        foreach (var requirement in requiredByRoot)
        {
            try
            {
                var drive = new DriveInfo(requirement.Root);
                if (drive.IsReady && drive.AvailableFreeSpace < requirement.Bytes)
                {
                    return $"Not enough free space on {requirement.Root}. "
                        + $"Required: {FormatBytes(requirement.Bytes)}; "
                        + $"available: {FormatBytes(drive.AvailableFreeSpace)}.";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // UNC and virtual filesystems do not always expose capacity. Transfer-time
                // verification still prevents a source deletion when a copy is incomplete.
            }
        }

        return null;
    }

    private static async Task ExecuteTransferAsync(
        PlannedTransfer transfer,
        FileOperation operation,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(transfer.DestinationPath)!;
        Directory.CreateDirectory(destinationDirectory);

        if (operation == FileOperation.Move
            && IsSameVolume(transfer.SourcePath, transfer.DestinationPath))
        {
            File.Move(transfer.SourcePath, transfer.DestinationPath);
            progress?.Report(transfer.Length);
            return;
        }

        await CopyVerifiedAsync(
            transfer.SourcePath,
            transfer.DestinationPath,
            progress,
            cancellationToken);

        if (operation == FileOperation.Move)
        {
            try
            {
                File.Delete(transfer.SourcePath);
            }
            catch
            {
                File.Delete(transfer.DestinationPath);
                throw;
            }
        }
    }

    private static async Task CopyVerifiedAsync(
        string source,
        string destination,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".mfr-partial-{Guid.NewGuid():N}";
        try
        {
            await using var input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[CopyBufferSize];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                progress?.Report(copied);
            }

            await output.FlushAsync(cancellationToken);
            if (copied != input.Length)
            {
                throw new IOException(
                    $"Copy verification failed for {Path.GetFileName(source)}.");
            }

            output.Close();
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static List<string> Rollback(
        IEnumerable<PlannedTransfer> completedTransfers,
        FileOperation operation,
        ActiveOperationJournal? journal)
    {
        var errors = new List<string>();
        foreach (var transfer in completedTransfers.Reverse())
        {
            try
            {
                if (operation == FileOperation.Copy)
                {
                    if (File.Exists(transfer.DestinationPath))
                    {
                        File.Delete(transfer.DestinationPath);
                    }
                }
                else if (File.Exists(transfer.DestinationPath))
                {
                    if (File.Exists(transfer.SourcePath))
                    {
                        throw new IOException(
                            $"Cannot restore {transfer.SourcePath}; it already exists.");
                    }

                    var sourceDirectory = Path.GetDirectoryName(transfer.SourcePath);
                    if (!string.IsNullOrWhiteSpace(sourceDirectory))
                    {
                        Directory.CreateDirectory(sourceDirectory);
                    }

                    File.Move(transfer.DestinationPath, transfer.SourcePath);
                }

                journal?.MarkRolledBack(transfer.SourcePath, transfer.DestinationPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{transfer.DestinationPath}: {ex.Message}");
            }
        }

        return errors;
    }

    private static bool IsSameVolume(string left, string right)
    {
        return string.Equals(
            Path.GetPathRoot(Path.GetFullPath(left)),
            Path.GetPathRoot(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static int DeleteEmptySourceFolders(IEnumerable<string> sourceDirectories)
    {
        var protectedDirectories = GetProtectedDirectories();
        var deleted = 0;

        foreach (var directory in sourceDirectories
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (protectedDirectories.Contains(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                if (TryDeleteEmptyDirectory(directory))
                {
                    deleted++;
                }
            }
            catch (IOException)
            {
                // The move succeeded; leave a locked source folder for inspection.
            }
            catch (UnauthorizedAccessException)
            {
                // The move succeeded; never fail the batch over folder cleanup permissions.
            }
        }

        return deleted;
    }

    private static bool TryDeleteEmptyDirectory(string directory)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            var entries = Directory.EnumerateFileSystemEntries(
                directory,
                "*",
                SearchOption.AllDirectories);
            foreach (var entry in entries)
            {
                var attributes = File.GetAttributes(entry);
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(150);
            }
        }

        return false;
    }

    private static HashSet<string> GetProtectedDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var protectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(userProfile),
            Path.GetPathRoot(userProfile) ?? userProfile
        };

        var specialFolders = new[]
        {
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos
        };

        foreach (var specialFolder in specialFolders)
        {
            var path = Environment.GetFolderPath(specialFolder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                protectedDirectories.Add(Path.GetFullPath(path));
            }
        }

        protectedDirectories.Add(Path.Combine(userProfile, "Downloads"));
        return protectedDirectories;
    }

    internal sealed record PlannedTransfer(
        MediaPreviewItem Item,
        string SourcePath,
        string DestinationPath,
        bool IsCompanion,
        long Length);

    private sealed record BatchPreflight(
        bool IsValid,
        IReadOnlyList<PlannedTransfer> Transfers,
        string? Error,
        MediaPreviewItem? FailedItem)
    {
        public static BatchPreflight Valid(IReadOnlyList<PlannedTransfer> transfers) =>
            new(true, transfers, null, null);

        public static BatchPreflight Invalid(string error, MediaPreviewItem? failedItem = null) =>
            new(false, [], error, failedItem);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed record RenameResult(
    int DeletedSourceFolders,
    IReadOnlyList<MediaPreviewItem> CompletedItems,
    string? JournalPath = null,
    bool RolledBack = false,
    string? FailureMessage = null);
