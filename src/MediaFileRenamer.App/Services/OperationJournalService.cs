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
                        File.Delete(entry.DestinationPath);
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

    private static OperationJournal? TryLoad(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<OperationJournal>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
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

public enum OperationJournalStatus
{
    Planned,
    InProgress,
    Completed,
    RolledBack,
    RollbackFailed,
    Undone,
    UndoFailed
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
