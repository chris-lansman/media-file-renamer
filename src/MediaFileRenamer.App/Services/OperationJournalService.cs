using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFileRenamer.App.Services;

public sealed class OperationJournalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string JournalDirectory { get; }

    public OperationJournalService(string? journalDirectory = null)
    {
        JournalDirectory = journalDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaFileRenamer",
            "operations");
    }

    internal ActiveOperationJournal Create(
        FileOperation operation,
        IReadOnlyList<RenameApplier.PlannedTransfer> transfers)
    {
        Directory.CreateDirectory(JournalDirectory);
        var journal = new OperationJournal
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Operation = operation,
            Status = OperationJournalStatus.Planned,
            Entries = transfers.Select(transfer => new OperationJournalEntry
            {
                SourcePath = transfer.SourcePath,
                DestinationPath = transfer.DestinationPath,
                IsCompanion = transfer.IsCompanion,
                Length = transfer.Length,
                Status = OperationJournalEntryStatus.Planned
            }).ToList()
        };
        var path = Path.Combine(
            JournalDirectory,
            $"{journal.CreatedAt:yyyyMMdd-HHmmss}-{journal.Id:N}.json");
        var active = new ActiveOperationJournal(path, journal, Save);
        active.Save();
        return active;
    }

    public IReadOnlyList<OperationJournalSummary> GetHistory(int maximum = 20)
    {
        if (!Directory.Exists(JournalDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(JournalDirectory, "*.json")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maximum))
            .Select(TryLoad)
            .Where(journal => journal is not null)
            .Select(journal => new OperationJournalSummary(
                journal!.Id,
                journal.CreatedAt,
                journal.Operation,
                journal.Status,
                journal.Entries.Count,
                journal.Error ?? ""))
            .ToList();
    }

    public IReadOnlyList<InterruptedOperationRecovery> GetInterruptedOperations()
    {
        return FindInterruptedJournals()
            .Select(candidate => Inspect(candidate.SavePath, candidate.Journal))
            .OrderByDescending(recovery => recovery.CreatedAt)
            .ToList();
    }

    public InterruptedOperationResult RecoverInterrupted(
        Guid operationId,
        InterruptedOperationAction action)
    {
        var candidate = FindInterruptedJournals()
            .FirstOrDefault(value => value.Journal.Id == operationId);
        if (candidate is null)
        {
            return new InterruptedOperationResult(
                false,
                0,
                0,
                "The interrupted operation is no longer available.");
        }

        if (action == InterruptedOperationAction.MarkResolved)
        {
            candidate.Journal.Status = OperationJournalStatus.Resolved;
            candidate.Journal.CompletedAt = DateTimeOffset.UtcNow;
            candidate.Journal.Error = null;
            candidate.Journal.RollbackErrors = [];
            Save(candidate.SavePath, candidate.Journal);
            DeleteRecoveredTemporaryJournal(candidate);
            return new InterruptedOperationResult(
                true,
                0,
                0,
                "Marked the interrupted operation as resolved. No media files were changed.");
        }

        var inspection = Inspect(candidate.SavePath, candidate.Journal);
        if (!inspection.CanRollback)
        {
            return new InterruptedOperationResult(
                false,
                0,
                0,
                "Automatic rollback is unavailable because the file state is ambiguous. "
                + "Review the listed paths, then mark the operation resolved when it is safe.");
        }

        foreach (var entry in inspection.Entries.Where(value =>
                     value.State == InterruptedEntryState.ContentVerificationRequired))
        {
            if (!FilesAreIdentical(entry.SourcePath, entry.DestinationPath))
            {
                return new InterruptedOperationResult(
                    false,
                    0,
                    0,
                    $"Rollback made no changes because source and destination differ: "
                    + entry.DestinationPath);
            }
        }

        // Reinspect immediately before changing anything. This catches common races
        // such as a destination being replaced while the recovery prompt is open.
        inspection = Inspect(candidate.SavePath, candidate.Journal);
        if (!inspection.CanRollback)
        {
            return new InterruptedOperationResult(
                false,
                0,
                0,
                "Rollback made no changes because the file state changed during recovery.");
        }

        var restored = 0;
        var removedArtifacts = 0;
        try
        {
            foreach (var entry in inspection.Entries.Reverse())
            {
                switch (entry.State)
                {
                    case InterruptedEntryState.DestinationContainsTransfer:
                        RestoreMovedFile(entry);
                        restored++;
                        break;

                    case InterruptedEntryState.ContentVerificationRequired:
                        DeleteVerifiedDuplicate(entry);
                        restored++;
                        break;
                }

                foreach (var partialPath in entry.PartialPaths)
                {
                    if (File.Exists(partialPath)
                        && IsPartialForDestination(partialPath, entry.DestinationPath))
                    {
                        DeleteFilePreservingAttributesOnFailure(partialPath);
                        removedArtifacts++;
                    }
                }

                var journalEntry = candidate.Journal.Entries.Single(value =>
                    value.SourcePath.Equals(entry.SourcePath, StringComparison.OrdinalIgnoreCase)
                    && value.DestinationPath.Equals(
                        entry.DestinationPath,
                        StringComparison.OrdinalIgnoreCase));
                journalEntry.Status = OperationJournalEntryStatus.RolledBack;
                Save(candidate.SavePath, candidate.Journal);
            }

            candidate.Journal.Status = OperationJournalStatus.RolledBack;
            candidate.Journal.CompletedAt = DateTimeOffset.UtcNow;
            candidate.Journal.Error = null;
            candidate.Journal.RollbackErrors = [];
            Save(candidate.SavePath, candidate.Journal);
            DeleteRecoveredTemporaryJournal(candidate);
            return new InterruptedOperationResult(
                true,
                restored,
                removedArtifacts,
                $"Rolled back {restored} file transfer(s) and removed "
                + $"{removedArtifacts} stale partial file(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            candidate.Journal.Status = OperationJournalStatus.RollbackFailed;
            candidate.Journal.Error = ex.Message;
            candidate.Journal.RollbackErrors = [ex.Message];
            Save(candidate.SavePath, candidate.Journal);
            return new InterruptedOperationResult(
                false,
                restored,
                removedArtifacts,
                $"Recovery stopped safely after {restored} file(s): {ex.Message}");
        }
    }

    public UndoResult UndoLastCompleted()
    {
        var candidate = FindLatestUndoable();
        if (candidate is null)
        {
            return new UndoResult(false, 0, "No completed operation is available to undo.");
        }

        var (path, journal) = candidate.Value;
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            if (entry.Status != OperationJournalEntryStatus.Completed)
            {
                continue;
            }

            if (journal.Operation == FileOperation.Move)
            {
                if (!File.Exists(entry.DestinationPath))
                {
                    return new UndoResult(
                        false,
                        0,
                        $"Cannot undo because a destination is missing: {entry.DestinationPath}");
                }

                if (File.Exists(entry.SourcePath))
                {
                    return new UndoResult(
                        false,
                        0,
                        $"Cannot undo because the original path is occupied: {entry.SourcePath}");
                }
            }

            if (File.Exists(entry.DestinationPath)
                && new FileInfo(entry.DestinationPath).Length != entry.Length)
            {
                return new UndoResult(
                    false,
                    0,
                    $"Cannot undo because a destination changed size: {entry.DestinationPath}");
            }
        }

        var restored = 0;
        try
        {
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                if (entry.Status != OperationJournalEntryStatus.Completed)
                {
                    continue;
                }

                if (journal.Operation == FileOperation.Copy)
                {
                    if (File.Exists(entry.DestinationPath))
                    {
                        DeleteFilePreservingAttributesOnFailure(entry.DestinationPath);
                    }
                }
                else
                {
                    var sourceDirectory = Path.GetDirectoryName(entry.SourcePath);
                    if (!string.IsNullOrWhiteSpace(sourceDirectory))
                    {
                        Directory.CreateDirectory(sourceDirectory);
                    }

                    File.Move(entry.DestinationPath, entry.SourcePath);
                }

                entry.Status = OperationJournalEntryStatus.Undone;
                restored++;
                Save(path, journal);
            }

            journal.Status = OperationJournalStatus.Undone;
            Save(path, journal);
            return new UndoResult(true, restored, $"Undid {restored} file transfer(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            journal.Status = OperationJournalStatus.UndoFailed;
            journal.Error = ex.Message;
            Save(path, journal);
            return new UndoResult(
                false,
                restored,
                $"Undo stopped after {restored} file(s): {ex.Message}");
        }
    }

    private (string Path, OperationJournal Journal)? FindLatestUndoable()
    {
        if (!Directory.Exists(JournalDirectory))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(JournalDirectory, "*.json")
                     .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var journal = TryLoad(path);
            if (journal?.Status is OperationJournalStatus.Completed
                or OperationJournalStatus.UndoFailed)
            {
                return (path, journal);
            }
        }

        return null;
    }

    private IReadOnlyList<JournalCandidate> FindInterruptedJournals()
    {
        if (!Directory.Exists(JournalDirectory))
        {
            return [];
        }

        var paths = Directory.EnumerateFiles(
                JournalDirectory,
                "*.json",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                JournalDirectory,
                "*.json.tmp",
                SearchOption.TopDirectoryOnly));

        return paths
            .Select(path => new { Path = path, Journal = TryLoad(path) })
            .Where(value => value.Journal is not null)
            .GroupBy(value => value.Journal!.Id)
            .Select(group => group
                .OrderBy(value => value.Path.EndsWith(
                    ".json.tmp",
                    StringComparison.OrdinalIgnoreCase))
                .First())
            .Where(value => IsInterruptedStatus(value.Journal!.Status))
            .Select(value => new JournalCandidate(
                value.Path,
                value.Path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    ? value.Path[..^4]
                    : value.Path,
                value.Journal!))
            .OrderByDescending(value => value.Journal.CreatedAt)
            .ToList();
    }

    private static bool IsInterruptedStatus(OperationJournalStatus status) =>
        status is OperationJournalStatus.Planned
            or OperationJournalStatus.InProgress
            or OperationJournalStatus.RollbackFailed;

    private static InterruptedOperationRecovery Inspect(
        string journalPath,
        OperationJournal journal)
    {
        var entries = journal.Entries
            .Select(entry => InspectEntry(entry, journal.Operation))
            .ToList();
        var canRollback = entries.All(entry =>
            entry.State != InterruptedEntryState.ManualReviewRequired);
        var requiresVerification = entries.Any(entry =>
            entry.State == InterruptedEntryState.ContentVerificationRequired);
        var changedFiles = entries.Count(entry =>
            entry.State is InterruptedEntryState.DestinationContainsTransfer
                or InterruptedEntryState.ContentVerificationRequired);
        var partialFiles = entries.Sum(entry => entry.PartialPaths.Count);
        var assessment = !canRollback
            ? InterruptedOperationAssessment.ManualReviewRequired
            : requiresVerification
                ? InterruptedOperationAssessment.VerificationRequired
                : changedFiles == 0 && partialFiles == 0
                    ? InterruptedOperationAssessment.NoChangesDetected
                    : InterruptedOperationAssessment.SafeToRollback;
        var message = assessment switch
        {
            InterruptedOperationAssessment.ManualReviewRequired =>
                "The current file state does not prove a safe rollback. "
                + "No files will be changed automatically.",
            InterruptedOperationAssessment.VerificationRequired =>
                "Rollback is available after identical source and destination "
                + "contents are verified.",
            InterruptedOperationAssessment.NoChangesDetected =>
                "No completed transfers or stale partial files were found.",
            _ => $"Safe rollback is available for {changedFiles} transfer(s) and "
                + $"{partialFiles} stale partial file(s)."
        };

        return new InterruptedOperationRecovery(
            journal.Id,
            journal.CreatedAt,
            journal.Operation,
            journal.Status,
            journalPath,
            assessment,
            canRollback,
            changedFiles,
            partialFiles,
            message,
            entries);
    }

    private static InterruptedOperationEntry InspectEntry(
        OperationJournalEntry entry,
        FileOperation operation)
    {
        try
        {
            var sourceExists = File.Exists(entry.SourcePath);
            var destinationExists = File.Exists(entry.DestinationPath);
            var sourceLength = sourceExists
                ? new FileInfo(entry.SourcePath).Length
                : (long?)null;
            var destinationLength = destinationExists
                ? new FileInfo(entry.DestinationPath).Length
                : (long?)null;
            var partialPaths = GetPartialPaths(entry.DestinationPath);
            var state = DetermineEntryState(
                entry,
                operation,
                sourceExists,
                destinationExists,
                sourceLength,
                destinationLength,
                partialPaths.Count);
            var message = state switch
            {
                InterruptedEntryState.Unchanged =>
                    "The source is present and no destination was created.",
                InterruptedEntryState.PartialArtifactOnly =>
                    "The source is present; only app-owned partial copy files remain.",
                InterruptedEntryState.DestinationContainsTransfer =>
                    "The destination has the expected length and the original path is empty.",
                InterruptedEntryState.ContentVerificationRequired =>
                    "Both paths exist with the expected length; contents must match before "
                    + "the destination can be removed.",
                InterruptedEntryState.AlreadyRestored =>
                    "The source is present and the destination is absent.",
                _ => "The source/destination state is ambiguous and requires manual review."
            };

            return new InterruptedOperationEntry(
                entry.SourcePath,
                entry.DestinationPath,
                entry.IsCompanion,
                entry.Length,
                entry.Status,
                state,
                sourceExists,
                destinationExists,
                sourceLength,
                destinationLength,
                partialPaths,
                message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InterruptedOperationEntry(
                entry.SourcePath,
                entry.DestinationPath,
                entry.IsCompanion,
                entry.Length,
                entry.Status,
                InterruptedEntryState.ManualReviewRequired,
                File.Exists(entry.SourcePath),
                File.Exists(entry.DestinationPath),
                null,
                null,
                [],
                $"The file state could not be inspected: {ex.Message}");
        }
    }

    private static InterruptedEntryState DetermineEntryState(
        OperationJournalEntry entry,
        FileOperation operation,
        bool sourceExists,
        bool destinationExists,
        long? sourceLength,
        long? destinationLength,
        int partialCount)
    {
        if (entry.Status == OperationJournalEntryStatus.Planned)
        {
            return sourceExists && !destinationExists && sourceLength == entry.Length
                ? partialCount == 0
                    ? InterruptedEntryState.Unchanged
                    : InterruptedEntryState.PartialArtifactOnly
                : InterruptedEntryState.ManualReviewRequired;
        }

        if (entry.Status == OperationJournalEntryStatus.RolledBack)
        {
            return sourceExists && !destinationExists
                ? InterruptedEntryState.AlreadyRestored
                : InterruptedEntryState.ManualReviewRequired;
        }

        if (sourceExists && !destinationExists && sourceLength == entry.Length)
        {
            return partialCount == 0
                ? InterruptedEntryState.AlreadyRestored
                : InterruptedEntryState.PartialArtifactOnly;
        }

        if (!sourceExists
            && destinationExists
            && destinationLength == entry.Length
            && operation == FileOperation.Move)
        {
            return InterruptedEntryState.DestinationContainsTransfer;
        }

        if (sourceExists
            && destinationExists
            && sourceLength == entry.Length
            && destinationLength == entry.Length)
        {
            return InterruptedEntryState.ContentVerificationRequired;
        }

        return InterruptedEntryState.ManualReviewRequired;
    }

    private static IReadOnlyList<string> GetPartialPaths(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var prefix = Path.GetFileName(destinationPath) + ".mfr-partial-";
        return Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsPartialForDestination(path, destinationPath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPartialForDestination(string partialPath, string destinationPath)
    {
        var expectedPrefix = Path.GetFullPath(destinationPath) + ".mfr-partial-";
        var fullPartialPath = Path.GetFullPath(partialPath);
        if (!fullPartialPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = fullPartialPath[expectedPrefix.Length..];
        return suffix.Length == 32
            && Guid.TryParseExact(suffix, "N", out _);
    }

    private static bool FilesAreIdentical(string sourcePath, string destinationPath)
    {
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);
            using var destination = new FileStream(
                destinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);
            if (source.Length != destination.Length)
            {
                return false;
            }

            var sourceBuffer = new byte[1024 * 1024];
            var destinationBuffer = new byte[1024 * 1024];
            int sourceRead;
            while ((sourceRead = source.Read(sourceBuffer, 0, sourceBuffer.Length)) > 0)
            {
                var destinationRead = destination.Read(
                    destinationBuffer,
                    0,
                    destinationBuffer.Length);
                if (sourceRead != destinationRead
                    || !sourceBuffer.AsSpan(0, sourceRead)
                        .SequenceEqual(destinationBuffer.AsSpan(0, destinationRead)))
                {
                    return false;
                }
            }

            return destination.ReadByte() == -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RestoreMovedFile(InterruptedOperationEntry entry)
    {
        if (File.Exists(entry.SourcePath))
        {
            throw new IOException(
                $"Cannot restore because the original path is occupied: {entry.SourcePath}");
        }

        if (!File.Exists(entry.DestinationPath)
            || new FileInfo(entry.DestinationPath).Length != entry.ExpectedLength)
        {
            throw new IOException(
                $"Cannot restore because the destination changed: {entry.DestinationPath}");
        }

        var sourceDirectory = Path.GetDirectoryName(entry.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            Directory.CreateDirectory(sourceDirectory);
        }

        File.Move(entry.DestinationPath, entry.SourcePath);
    }

    private static void DeleteVerifiedDuplicate(InterruptedOperationEntry entry)
    {
        if (!FilesAreIdentical(entry.SourcePath, entry.DestinationPath))
        {
            throw new IOException(
                $"Cannot remove a destination that no longer matches its source: "
                + entry.DestinationPath);
        }

        DeleteFilePreservingAttributesOnFailure(entry.DestinationPath);
    }

    private static void DeleteFilePreservingAttributesOnFailure(string path)
    {
        var originalAttributes = File.GetAttributes(path);
        var changedAttributes = originalAttributes.HasFlag(FileAttributes.ReadOnly);
        if (changedAttributes)
        {
            var writableAttributes = originalAttributes & ~FileAttributes.ReadOnly;
            File.SetAttributes(
                path,
                writableAttributes == 0 ? FileAttributes.Normal : writableAttributes);
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            if (changedAttributes && File.Exists(path))
            {
                try
                {
                    File.SetAttributes(path, originalAttributes);
                }
                catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException)
                {
                    // Preserve the original delete failure. Restoring attributes is
                    // best-effort if filesystem permissions changed concurrently.
                }
            }

            throw;
        }
    }

    private static void DeleteRecoveredTemporaryJournal(JournalCandidate candidate)
    {
        if (!candidate.LoadPath.Equals(
                candidate.SavePath,
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(candidate.LoadPath))
        {
            File.Delete(candidate.LoadPath);
        }
    }

    private static OperationJournal? TryLoad(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<OperationJournal>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException)
        {
            return null;
        }
    }

    private static void Save(string path, OperationJournal journal)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record JournalCandidate(
        string LoadPath,
        string SavePath,
        OperationJournal Journal);
}

internal sealed class ActiveOperationJournal
{
    private readonly OperationJournal _journal;
    private readonly Action<string, OperationJournal> _save;

    public string Path { get; }

    public ActiveOperationJournal(
        string path,
        OperationJournal journal,
        Action<string, OperationJournal> save)
    {
        Path = path;
        _journal = journal;
        _save = save;
    }

    public void Save() => _save(Path, _journal);

    public void MarkInProgress(string source, string destination)
    {
        _journal.Status = OperationJournalStatus.InProgress;
        Find(source, destination).Status = OperationJournalEntryStatus.InProgress;
        Save();
    }

    public void MarkCompleted(string source, string destination)
    {
        Find(source, destination).Status = OperationJournalEntryStatus.Completed;
        Save();
    }

    public void MarkRolledBack(string source, string destination)
    {
        Find(source, destination).Status = OperationJournalEntryStatus.RolledBack;
        Save();
    }

    public void MarkCompleted()
    {
        _journal.Status = OperationJournalStatus.Completed;
        _journal.CompletedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public void MarkFailed(string error, IReadOnlyList<string> rollbackErrors)
    {
        _journal.Status = rollbackErrors.Count == 0
            ? OperationJournalStatus.RolledBack
            : OperationJournalStatus.RollbackFailed;
        _journal.Error = error;
        _journal.RollbackErrors = rollbackErrors.ToList();
        _journal.CompletedAt = DateTimeOffset.UtcNow;
        Save();
    }

    private OperationJournalEntry Find(string source, string destination)
    {
        return _journal.Entries.Single(entry =>
            entry.SourcePath.Equals(source, StringComparison.OrdinalIgnoreCase)
            && entry.DestinationPath.Equals(destination, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record OperationJournalSummary(
    Guid Id,
    DateTimeOffset CreatedAt,
    FileOperation Operation,
    OperationJournalStatus Status,
    int FileCount,
    string Error);

public sealed record UndoResult(bool Success, int RestoredFiles, string Message);

public sealed record InterruptedOperationRecovery(
    Guid Id,
    DateTimeOffset CreatedAt,
    FileOperation Operation,
    OperationJournalStatus JournalStatus,
    string JournalPath,
    InterruptedOperationAssessment Assessment,
    bool CanRollback,
    int ChangedFileCount,
    int PartialArtifactCount,
    string Message,
    IReadOnlyList<InterruptedOperationEntry> Entries);

public sealed record InterruptedOperationEntry(
    string SourcePath,
    string DestinationPath,
    bool IsCompanion,
    long ExpectedLength,
    OperationJournalEntryStatus JournalEntryStatus,
    InterruptedEntryState State,
    bool SourceExists,
    bool DestinationExists,
    long? SourceLength,
    long? DestinationLength,
    IReadOnlyList<string> PartialPaths,
    string Message);

public sealed record InterruptedOperationResult(
    bool Success,
    int RestoredFiles,
    int RemovedPartialArtifacts,
    string Message);

public enum InterruptedOperationAction
{
    Rollback,
    MarkResolved
}

public enum InterruptedOperationAssessment
{
    NoChangesDetected,
    SafeToRollback,
    VerificationRequired,
    ManualReviewRequired
}

public enum InterruptedEntryState
{
    Unchanged,
    PartialArtifactOnly,
    DestinationContainsTransfer,
    ContentVerificationRequired,
    AlreadyRestored,
    ManualReviewRequired
}

public enum OperationJournalStatus
{
    Planned,
    InProgress,
    Completed,
    RolledBack,
    RollbackFailed,
    Undone,
    UndoFailed,
    Resolved
}

public enum OperationJournalEntryStatus
{
    Planned,
    InProgress,
    Completed,
    RolledBack,
    Undone
}

internal sealed class OperationJournal
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public FileOperation Operation { get; set; }
    public OperationJournalStatus Status { get; set; }
    public List<OperationJournalEntry> Entries { get; set; } = [];
    public string? Error { get; set; }
    public List<string> RollbackErrors { get; set; } = [];
}

internal sealed class OperationJournalEntry
{
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public bool IsCompanion { get; set; }
    public long Length { get; set; }
    public OperationJournalEntryStatus Status { get; set; }
}
