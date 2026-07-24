using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFileRenamer.App.Services;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class OperationRecoveryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [TestMethod]
    public void InspectInterruptedOperation_ReportsMoveAndStalePartialSafely()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var movedSource = Path.Combine(temp.Path, "Source", "Movie.mkv");
        var movedDestination = temp.CreateFile(
            Path.Combine("Output", "Renamed.mkv"),
            "video");
        var pendingSource = temp.CreateFile(
            Path.Combine("Source", "Movie.en.srt"),
            "subtitle");
        var pendingDestination = Path.Combine(
            temp.Path,
            "Output",
            "Renamed.en.srt");
        var partial = pendingDestination + $".mfr-partial-{Guid.NewGuid():N}";
        File.WriteAllText(partial, "sub");
        var similarlyNamedUserFile = pendingDestination + ".mfr-partial-not-an-app-id";
        File.WriteAllText(similarlyNamedUserFile, "keep");
        var operationId = WriteJournal(
            journals,
            FileOperation.Move,
            OperationJournalStatus.InProgress,
            [
                Entry(
                    movedSource,
                    movedDestination,
                    5,
                    OperationJournalEntryStatus.Completed),
                Entry(
                    pendingSource,
                    pendingDestination,
                    8,
                    OperationJournalEntryStatus.InProgress)
            ]);

        var recovery = new OperationJournalService(journals)
            .GetInterruptedOperations()
            .Single();

        Assert.AreEqual(operationId, recovery.Id);
        Assert.IsTrue(recovery.CanRollback);
        Assert.AreEqual(
            InterruptedOperationAssessment.SafeToRollback,
            recovery.Assessment);
        Assert.AreEqual(1, recovery.ChangedFileCount);
        Assert.AreEqual(1, recovery.PartialArtifactCount);
        Assert.AreEqual(
            InterruptedEntryState.DestinationContainsTransfer,
            recovery.Entries[0].State);
        Assert.AreEqual(
            InterruptedEntryState.PartialArtifactOnly,
            recovery.Entries[1].State);
        CollectionAssert.AreEqual(
            new[] { partial },
            recovery.Entries[1].PartialPaths.ToArray());
        Assert.IsTrue(File.Exists(similarlyNamedUserFile));
    }

    [TestMethod]
    public void RollbackInterrupted_RestoresMoveAndRemovesOnlyAppPartial()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var movedSource = Path.Combine(temp.Path, "Source", "Movie.mkv");
        var movedDestination = temp.CreateFile(
            Path.Combine("Output", "Renamed.mkv"),
            "video");
        var pendingSource = temp.CreateFile(
            Path.Combine("Source", "Movie.en.srt"),
            "subtitle");
        var pendingDestination = Path.Combine(
            temp.Path,
            "Output",
            "Renamed.en.srt");
        var partial = pendingDestination + $".mfr-partial-{Guid.NewGuid():N}";
        File.WriteAllText(partial, "sub");
        File.SetAttributes(
            partial,
            File.GetAttributes(partial) | FileAttributes.ReadOnly);
        var similarlyNamedUserFile = pendingDestination + ".mfr-partial-keep-me";
        File.WriteAllText(similarlyNamedUserFile, "keep");
        var operationId = WriteJournal(
            journals,
            FileOperation.Move,
            OperationJournalStatus.InProgress,
            [
                Entry(
                    movedSource,
                    movedDestination,
                    5,
                    OperationJournalEntryStatus.Completed),
                Entry(
                    pendingSource,
                    pendingDestination,
                    8,
                    OperationJournalEntryStatus.InProgress)
            ]);
        var service = new OperationJournalService(journals);

        var result = service.RecoverInterrupted(
            operationId,
            InterruptedOperationAction.Rollback);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(1, result.RestoredFiles);
        Assert.AreEqual(1, result.RemovedPartialArtifacts);
        Assert.IsTrue(File.Exists(movedSource));
        Assert.AreEqual("video", File.ReadAllText(movedSource));
        Assert.IsFalse(File.Exists(movedDestination));
        Assert.IsTrue(File.Exists(pendingSource));
        Assert.IsFalse(File.Exists(partial));
        Assert.IsTrue(File.Exists(similarlyNamedUserFile));
        Assert.HasCount(0, service.GetInterruptedOperations());
        Assert.AreEqual(
            OperationJournalStatus.RolledBack,
            service.GetHistory().Single().Status);
    }

    [TestMethod]
    public void RollbackInterrupted_RemovesIdenticalReadOnlyCopyDestination()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "video");
        var destination = temp.CreateFile("Destination.mkv", "video");
        File.SetAttributes(
            destination,
            File.GetAttributes(destination) | FileAttributes.ReadOnly);
        var operationId = WriteJournal(
            journals,
            FileOperation.Copy,
            OperationJournalStatus.InProgress,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.InProgress)
            ]);
        var service = new OperationJournalService(journals);

        var result = service.RecoverInterrupted(
            operationId,
            InterruptedOperationAction.Rollback);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public void UndoCompletedCopy_RemovesReadOnlyDestination()
    {
        using var temp = new TempDirectory();
        var journals = new OperationJournalService(
            temp.CreateDirectory("Journals"));
        var source = temp.CreateFile("Source.mkv", "video");
        File.SetAttributes(
            source,
            File.GetAttributes(source) | FileAttributes.ReadOnly);
        try
        {
            var destination = Path.Combine(temp.Path, "Output", "Destination.mkv");
            var item = new MediaFileRenamer.App.ViewModels.MediaPreviewItem
            {
                SourcePath = source,
                Extension = ".mkv",
                MediaType = "Movie",
                MatchedTitle = "Movie",
                DestinationPath = destination,
                Status = "TMDB match"
            };
            var applied = new RenameApplier(journals).Apply(
                [item],
                FileOperation.Copy);
            Assert.HasCount(1, applied.CompletedItems);
            Assert.IsTrue(
                File.GetAttributes(destination).HasFlag(FileAttributes.ReadOnly));

            var result = journals.UndoLastCompleted();

            Assert.IsTrue(result.Success, result.Message);
            Assert.IsTrue(File.Exists(source));
            Assert.IsFalse(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(source))
            {
                File.SetAttributes(
                    source,
                    File.GetAttributes(source) & ~FileAttributes.ReadOnly);
            }
        }
    }

    [TestMethod]
    public void RollbackInterrupted_RefusesDifferentSourceAndDestinationWithoutChanges()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "abcde");
        var destination = temp.CreateFile("Destination.mkv", "vwxyz");
        var operationId = WriteJournal(
            journals,
            FileOperation.Copy,
            OperationJournalStatus.InProgress,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.InProgress)
            ]);
        var service = new OperationJournalService(journals);
        var inspection = service.GetInterruptedOperations().Single();
        Assert.AreEqual(
            InterruptedOperationAssessment.VerificationRequired,
            inspection.Assessment);

        var result = service.RecoverInterrupted(
            operationId,
            InterruptedOperationAction.Rollback);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "differ");
        Assert.AreEqual("abcde", File.ReadAllText(source));
        Assert.AreEqual("vwxyz", File.ReadAllText(destination));
        Assert.HasCount(1, service.GetInterruptedOperations());
    }

    [TestMethod]
    public void RollbackInterrupted_RefusesOccupiedMoveSourceWithoutOverwriting()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "newer");
        var destination = temp.CreateFile("Destination.mkv", "video");
        var operationId = WriteJournal(
            journals,
            FileOperation.Move,
            OperationJournalStatus.InProgress,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.Completed)
            ]);
        var service = new OperationJournalService(journals);

        var result = service.RecoverInterrupted(
            operationId,
            InterruptedOperationAction.Rollback);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("newer", File.ReadAllText(source));
        Assert.AreEqual("video", File.ReadAllText(destination));
    }

    [TestMethod]
    public void MarkResolved_ChangesOnlyJournalAndRemovesStartupPrompt()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "source");
        var destination = temp.CreateFile("Destination.mkv", "destination");
        var operationId = WriteJournal(
            journals,
            FileOperation.Move,
            OperationJournalStatus.Planned,
            [
                Entry(
                    source,
                    destination,
                    6,
                    OperationJournalEntryStatus.Planned)
            ]);
        var service = new OperationJournalService(journals);

        var result = service.RecoverInterrupted(
            operationId,
            InterruptedOperationAction.MarkResolved);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("source", File.ReadAllText(source));
        Assert.AreEqual("destination", File.ReadAllText(destination));
        Assert.HasCount(0, service.GetInterruptedOperations());
        Assert.AreEqual(
            OperationJournalStatus.Resolved,
            service.GetHistory().Single().Status);
    }

    [TestMethod]
    public void InspectInterruptedOperation_LoadsJournalLeftAtAtomicTempPath()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "video");
        var destination = Path.Combine(temp.Path, "Destination.mkv");
        var operationId = WriteJournal(
            journals,
            FileOperation.Copy,
            OperationJournalStatus.Planned,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.Planned)
            ],
            temporaryOnly: true);
        var service = new OperationJournalService(journals);

        var recovery = service.GetInterruptedOperations().Single();
        var result = service.RecoverInterrupted(
            operationId,
            InterruptedOperationAction.Rollback);

        Assert.AreEqual(operationId, recovery.Id);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsFalse(File.Exists(recovery.JournalPath + ".tmp"));
        Assert.AreEqual(
            OperationJournalStatus.RolledBack,
            service.GetHistory().Single().Status);
    }

    private static object Entry(
        string sourcePath,
        string destinationPath,
        long length,
        OperationJournalEntryStatus status) => new
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            IsCompanion = false,
            Length = length,
            Status = status
        };

    private static Guid WriteJournal(
        string journalDirectory,
        FileOperation operation,
        OperationJournalStatus status,
        object[] entries,
        bool temporaryOnly = false)
    {
        var id = Guid.NewGuid();
        var journal = new
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = (DateTimeOffset?)null,
            Operation = operation,
            Status = status,
            Entries = entries,
            Error = (string?)null,
            RollbackErrors = Array.Empty<string>()
        };
        var suffix = temporaryOnly ? ".json.tmp" : ".json";
        var path = Path.Combine(journalDirectory, $"{id:N}{suffix}");
        File.WriteAllText(path, JsonSerializer.Serialize(journal, JsonOptions));
        return id;
    }
}
