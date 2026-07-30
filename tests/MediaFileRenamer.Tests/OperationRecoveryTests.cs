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
    public void RollbackInterruptedMove_RestoresMetadataAfterCrossVolumeStyleMove()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = Path.Combine(temp.Path, "Source", "Movie.mkv");
        var destination = temp.CreateFile(
            Path.Combine("Output", "Renamed.mkv"),
            "video");
        var creationTimeUtc = new DateTime(2017, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var lastWriteTimeUtc = new DateTime(2018, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(destination, creationTimeUtc);
        File.SetLastWriteTimeUtc(destination, lastWriteTimeUtc);
        File.SetAttributes(
            destination,
            FileAttributes.Archive
            | FileAttributes.Hidden
            | FileAttributes.ReadOnly);
        var operationId = WriteJournal(
            journals,
            FileOperation.Move,
            OperationJournalStatus.InProgress,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.InProgress)
            ]);
        var service = new OperationJournalService(
            journals,
            null,
            new TimestampResettingFileMove());

        try
        {
            var result = service.RecoverInterrupted(
                operationId,
                InterruptedOperationAction.Rollback);

            Assert.IsTrue(result.Success, result.Message);
            Assert.IsTrue(File.Exists(source));
            Assert.IsFalse(File.Exists(destination));
            Assert.AreEqual(creationTimeUtc, File.GetCreationTimeUtc(source));
            Assert.AreEqual(lastWriteTimeUtc, File.GetLastWriteTimeUtc(source));
            const FileAttributes expected =
                FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly;
            Assert.AreEqual(expected, File.GetAttributes(source) & expected);
        }
        finally
        {
            ClearReadOnly(source);
            ClearReadOnly(destination);
        }
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
    public void UndoLegacyCopy_AllowsByteIdenticalRetainedSource()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "video");
        var destination = temp.CreateFile("Destination.mkv", "video");
        WriteJournal(
            journals,
            FileOperation.Copy,
            OperationJournalStatus.Completed,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.Completed)
            ]);
        var service = new OperationJournalService(journals);

        var result = service.UndoLastCompleted();

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public void UndoLegacyMove_RefusesWithoutDestinationFingerprint()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = Path.Combine(temp.Path, "Source.mkv");
        var destination = temp.CreateFile("Destination.mkv", "video");
        WriteJournal(
            journals,
            FileOperation.Move,
            OperationJournalStatus.Completed,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.Completed)
            ]);
        var service = new OperationJournalService(journals);

        var result = service.UndoLastCompleted();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "older journal");
        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual("video", File.ReadAllText(destination));
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

    [TestMethod]
    public void InspectInterruptedOperation_BlocksUnavailableDestinationRoot()
    {
        using var temp = new TempDirectory();
        var journals = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "video");
        var destination = Path.Combine(temp.Path, "Unavailable", "Destination.mkv");
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
        var service = new OperationJournalService(
            journals,
            new SelectiveAvailabilityProbe(destination));

        var recovery = service.GetInterruptedOperations().Single();
        var result = service.RecoverInterrupted(
            operationId,
            InterruptedOperationAction.Rollback);

        Assert.IsFalse(recovery.CanRollback);
        Assert.AreEqual(
            InterruptedOperationAssessment.ManualReviewRequired,
            recovery.Assessment);
        Assert.AreEqual(
            InterruptedEntryState.PathUnavailable,
            recovery.Entries.Single().State);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(
            OperationJournalStatus.InProgress,
            service.GetHistory().Single().Status);
    }

    [TestMethod]
    public void Undo_BlocksUnavailableDestinationWithoutTerminalizingJournal()
    {
        using var temp = new TempDirectory();
        var journalDirectory = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "video");
        var destination = Path.Combine(temp.Path, "Output", "Destination.mkv");
        var initialService = new OperationJournalService(journalDirectory);
        var item = new MediaFileRenamer.App.ViewModels.MediaPreviewItem
        {
            SourcePath = source,
            Extension = ".mkv",
            MediaType = "Movie",
            MatchedTitle = "Movie",
            DestinationPath = destination,
            Status = "TMDB match"
        };
        var applied = new RenameApplier(initialService).Apply(
            [item],
            FileOperation.Copy);
        Assert.HasCount(1, applied.CompletedItems);
        var unavailableService = new OperationJournalService(
            journalDirectory,
            new SelectiveAvailabilityProbe(destination));

        var result = unavailableService.UndoLastCompleted();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "unavailable");
        Assert.IsTrue(File.Exists(source));
        Assert.IsTrue(File.Exists(destination));
        Assert.AreEqual(
            OperationJournalStatus.Completed,
            unavailableService.GetHistory().Single().Status);
    }

    [TestMethod]
    public void GetOperationDetails_ExposesSelectedJournalFiles()
    {
        using var temp = new TempDirectory();
        var journalDirectory = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "video");
        var destination = temp.CreateFile("Output/Renamed.mkv", "video");
        var operationId = WriteJournal(
            journalDirectory,
            FileOperation.Copy,
            OperationJournalStatus.Completed,
            [
                Entry(
                    source,
                    destination,
                    5,
                    OperationJournalEntryStatus.Completed)
            ]);
        var service = new OperationJournalService(journalDirectory);

        var details = service.GetOperationDetails(operationId);

        Assert.IsNotNull(details);
        Assert.AreEqual(operationId, details.Id);
        Assert.AreEqual(FileOperation.Copy, details.Operation);
        Assert.AreEqual(OperationJournalStatus.Completed, details.Status);
        Assert.HasCount(1, details.Files);
        Assert.AreEqual(source, details.Files[0].SourcePath);
        Assert.AreEqual(destination, details.Files[0].DestinationPath);
        Assert.IsNull(service.GetOperationDetails(Guid.NewGuid()));
    }

    [TestMethod]
    public void UndoCompleted_UndoesOnlyTheSelectedEligibleJournal()
    {
        using var temp = new TempDirectory();
        var journalDirectory = temp.CreateDirectory("Journals");
        var sourceOne = temp.CreateFile("SourceOne.mkv", "first");
        var destinationOne = temp.CreateFile("Output/First.mkv", "first");
        var selectedId = WriteJournal(
            journalDirectory,
            FileOperation.Copy,
            OperationJournalStatus.Completed,
            [
                Entry(
                    sourceOne,
                    destinationOne,
                    5,
                    OperationJournalEntryStatus.Completed)
            ]);
        var sourceTwo = temp.CreateFile("SourceTwo.mkv", "second");
        var destinationTwo = temp.CreateFile("Output/Second.mkv", "second");
        WriteJournal(
            journalDirectory,
            FileOperation.Copy,
            OperationJournalStatus.Completed,
            [
                Entry(
                    sourceTwo,
                    destinationTwo,
                    6,
                    OperationJournalEntryStatus.Completed)
            ]);
        var service = new OperationJournalService(journalDirectory);

        var result = service.UndoCompleted(selectedId);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(File.Exists(sourceOne));
        Assert.IsFalse(File.Exists(destinationOne));
        Assert.IsTrue(File.Exists(sourceTwo));
        Assert.IsTrue(File.Exists(destinationTwo));
        Assert.AreEqual(
            OperationJournalStatus.Undone,
            service.GetHistory().Single(item => item.Id == selectedId).Status);
    }

    [TestMethod]
    public void UndoCompleted_RejectsUnknownOrIneligibleJournal()
    {
        using var temp = new TempDirectory();
        var journalDirectory = temp.CreateDirectory("Journals");
        var source = temp.CreateFile("Source.mkv", "video");
        var operationId = WriteJournal(
            journalDirectory,
            FileOperation.Copy,
            OperationJournalStatus.Resolved,
            [
                Entry(
                    source,
                    Path.Combine(temp.Path, "Output", "Renamed.mkv"),
                    5,
                    OperationJournalEntryStatus.Completed)
            ]);
        var service = new OperationJournalService(journalDirectory);

        var ineligible = service.UndoCompleted(operationId);
        var unknown = service.UndoCompleted(Guid.NewGuid());

        Assert.IsFalse(ineligible.Success);
        StringAssert.Contains(ineligible.Message, "not available to undo");
        Assert.IsFalse(unknown.Success);
        StringAssert.Contains(unknown.Message, "no longer available");
        Assert.IsTrue(File.Exists(source));
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

    private sealed class SelectiveAvailabilityProbe(string unavailablePath)
        : IPathAvailabilityProbe
    {
        public PathAvailability GetRootAvailability(string path) =>
            path.Equals(unavailablePath, StringComparison.OrdinalIgnoreCase)
                ? PathAvailability.Unavailable
                : PathAvailability.Available;
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path) & ~FileAttributes.ReadOnly;
        File.SetAttributes(
            path,
            attributes == 0 ? FileAttributes.Normal : attributes);
    }

    private sealed class TimestampResettingFileMove : IFileMoveOperation
    {
        public void Move(string sourcePath, string destinationPath)
        {
            File.Move(sourcePath, destinationPath);
            File.SetCreationTimeUtc(
                destinationPath,
                new DateTime(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetAttributes(destinationPath, FileAttributes.Normal);
        }
    }
}
