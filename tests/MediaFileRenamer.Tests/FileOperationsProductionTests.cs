using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;

namespace MediaFileRenamer.Tests;

[TestClass]
public sealed class CompanionFileTests
{
    [TestMethod]
    public void Scan_AssociatesSameStemCompanionsAndSkipsExtrasFolders()
    {
        using var temp = new TempDirectory();
        var showFolder = temp.CreateDirectory(Path.Combine("Show", "Season 01"));
        var media = temp.CreateFile(
            Path.Combine(showFolder, "Show - S01E01.mkv"),
            "video");
        var subtitle = temp.CreateFile(
            Path.Combine(showFolder, "Show - S01E01.en.forced.srt"),
            "subtitle");
        var nfo = temp.CreateFile(
            Path.Combine(showFolder, "Show - S01E01.nfo"),
            "metadata");
        temp.CreateFile(
            Path.Combine("Show", "Extras", "Behind the scenes.mkv"),
            "extra");

        var items = new MediaScanner().Scan([Path.Combine(temp.Path, "Show")]);

        Assert.HasCount(1, items);
        Assert.AreEqual(media, items[0].SourcePath);
        CollectionAssert.AreEquivalent(
            new[] { subtitle, nfo },
            items[0].CompanionPaths);
    }

    [TestMethod]
    public void Planner_PreservesSubtitleQualifierForCompanionDestination()
    {
        var item = new MediaPreviewItem
        {
            SourcePath = @"C:\Source\Old Name.mkv",
            DestinationPath = @"C:\Output\New Name.mkv",
            Extension = ".mkv",
            MediaType = "Movie",
            MatchedTitle = "New Name",
            Status = "TMDB match"
        };
        item.CompanionPaths.Add(@"C:\Source\Old Name.en.forced.srt");

        var companion = new RenamePlanner()
            .BuildCompanionDestinations(item)
            .Single();

        Assert.AreEqual(
            @"C:\Output\New Name.en.forced.srt",
            companion.DestinationPath);
    }
}

[TestClass]
public sealed class TransactionalRenameTests
{
    [TestMethod]
    public void Copy_PreservesMetadataForMediaAndCompanion()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile(
            Path.Combine("Source", "Movie.mkv"),
            "video");
        var subtitle = temp.CreateFile(
            Path.Combine("Source", "Movie.en.srt"),
            "subtitle");
        var destination = Path.Combine(temp.Path, "Output", "Renamed Movie.mkv");
        var subtitleDestination = Path.Combine(
            temp.Path,
            "Output",
            "Renamed Movie.en.srt");
        var item = Item(source, destination);
        item.CompanionPaths.Add(subtitle);

        var mediaCreation = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var mediaLastWrite = new DateTime(2020, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var companionCreation = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var companionLastWrite = new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        SetMetadata(source, mediaCreation, mediaLastWrite);
        SetMetadata(subtitle, companionCreation, companionLastWrite);

        try
        {
            var result = new RenameApplier().Apply([item], FileOperation.Copy);

            Assert.HasCount(1, result.CompletedItems);
            AssertMetadataMatches(source, destination);
            AssertMetadataMatches(subtitle, subtitleDestination);
            Assert.IsFalse(
                File.GetAttributes(destination).HasFlag(FileAttributes.Temporary));
            Assert.IsFalse(
                File.GetAttributes(subtitleDestination).HasFlag(FileAttributes.Temporary));
        }
        finally
        {
            ClearReadOnly(source);
            ClearReadOnly(subtitle);
            ClearReadOnly(destination);
            ClearReadOnly(subtitleDestination);
        }
    }

    [TestMethod]
    public void Move_IncludesCompanionsAndUndoRestoresEntireOperation()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile(Path.Combine("Source", "Movie.mkv"), "video");
        var subtitle = temp.CreateFile(
            Path.Combine("Source", "Movie.en.srt"),
            "subtitle");
        var destination = Path.Combine(temp.Path, "Output", "Renamed Movie.mkv");
        var subtitleDestination = Path.Combine(
            temp.Path,
            "Output",
            "Renamed Movie.en.srt");
        var item = Item(source, destination);
        item.CompanionPaths.Add(subtitle);
        var journals = new OperationJournalService(
            temp.CreateDirectory("Journals"));

        var result = new RenameApplier(journals).Apply([item], FileOperation.Move);

        Assert.HasCount(1, result.CompletedItems);
        Assert.IsTrue(File.Exists(destination));
        Assert.IsTrue(File.Exists(subtitleDestination));
        Assert.IsFalse(File.Exists(source));
        Assert.IsFalse(File.Exists(subtitle));
        Assert.AreEqual(
            OperationJournalStatus.Completed,
            journals.GetHistory().Single().Status);

        var undo = journals.UndoLastCompleted();

        Assert.IsTrue(undo.Success);
        Assert.AreEqual(2, undo.RestoredFiles);
        Assert.IsTrue(File.Exists(source));
        Assert.IsTrue(File.Exists(subtitle));
        Assert.IsFalse(File.Exists(destination));
        Assert.IsFalse(File.Exists(subtitleDestination));
    }

    [TestMethod]
    public async Task Copy_FailureAfterFirstTransferRollsBackCreatedDestination()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Movie.mkv", "video");
        var subtitle = temp.CreateFile("Movie.en.srt", "subtitle");
        var destination = Path.Combine(temp.Path, "Output", "Renamed.mkv");
        var item = Item(source, destination);
        item.CompanionPaths.Add(subtitle);
        File.SetAttributes(
            source,
            File.GetAttributes(source) | FileAttributes.ReadOnly);
        var progress = new InlineProgress<FileTransferProgress>(value =>
        {
            if (value.CompletedFiles == 1 && File.Exists(subtitle))
            {
                File.Delete(subtitle);
            }
        });

        try
        {
            var result = await new RenameApplier().ApplyAsync(
                [item],
                FileOperation.Copy,
                progress);

            Assert.IsTrue(result.RolledBack);
            Assert.IsNotNull(result.FailureMessage);
            Assert.IsFalse(File.Exists(destination));
            Assert.IsTrue(File.Exists(source));
            Assert.HasCount(0, result.CompletedItems);
        }
        finally
        {
            ClearReadOnly(source);
            ClearReadOnly(destination);
        }
    }

    [TestMethod]
    public void Preflight_BlocksWholeBatchWhenCompanionDestinationExists()
    {
        using var temp = new TempDirectory();
        var firstSource = temp.CreateFile("First.mkv", "first");
        var secondSource = temp.CreateFile("Second.mkv", "second");
        var secondSubtitle = temp.CreateFile("Second.en.srt", "subtitle");
        var first = Item(
            firstSource,
            Path.Combine(temp.Path, "Output", "First Renamed.mkv"));
        var second = Item(
            secondSource,
            Path.Combine(temp.Path, "Output", "Second Renamed.mkv"));
        second.CompanionPaths.Add(secondSubtitle);
        temp.CreateFile(
            Path.Combine("Output", "Second Renamed.en.srt"),
            "existing");

        var result = new RenameApplier().Apply(
            [first, second],
            FileOperation.Move);

        Assert.HasCount(0, result.CompletedItems);
        Assert.IsTrue(File.Exists(firstSource));
        Assert.IsTrue(File.Exists(secondSource));
        Assert.IsFalse(File.Exists(first.DestinationPath));
    }

    [TestMethod]
    public void Undo_RefusesToDeleteAChangedCopyDestination()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Movie.mkv", "video");
        var destination = Path.Combine(temp.Path, "Output", "Renamed.mkv");
        var journals = new OperationJournalService(
            temp.CreateDirectory("Journals"));
        var item = Item(source, destination);
        var applied = new RenameApplier(journals).Apply(
            [item],
            FileOperation.Copy);
        Assert.HasCount(1, applied.CompletedItems);
        File.WriteAllText(destination, "replacement with a different size");

        var undo = journals.UndoLastCompleted();

        Assert.IsFalse(undo.Success);
        StringAssert.Contains(undo.Message, "changed size");
        Assert.IsTrue(File.Exists(destination));
        Assert.AreEqual(
            "replacement with a different size",
            File.ReadAllText(destination));
    }

    [TestMethod]
    public async Task PreCanceledCopy_LeavesSourceAndDestinationUntouched()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Movie.mkv", "video");
        var destination = Path.Combine(temp.Path, "Output", "Renamed.mkv");
        var item = Item(source, destination);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new RenameApplier().ApplyAsync(
            [item],
            FileOperation.Copy,
            cancellationToken: cancellation.Token);

        Assert.IsTrue(result.RolledBack);
        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(File.Exists(destination));
        Assert.HasCount(0, result.CompletedItems);
    }

    private static MediaPreviewItem Item(string source, string destination) => new()
    {
        SourcePath = source,
        Extension = ".mkv",
        MediaType = "Movie",
        MatchedTitle = "Movie",
        DestinationPath = destination,
        Status = "TMDB match"
    };

    private static void SetMetadata(
        string path,
        DateTime creationTimeUtc,
        DateTime lastWriteTimeUtc)
    {
        File.SetCreationTimeUtc(path, creationTimeUtc);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        File.SetAttributes(
            path,
            FileAttributes.Archive
            | FileAttributes.Hidden
            | FileAttributes.ReadOnly
            | FileAttributes.Temporary);
    }

    private static void AssertMetadataMatches(string source, string destination)
    {
        Assert.AreEqual(
            File.GetCreationTimeUtc(source),
            File.GetCreationTimeUtc(destination));
        Assert.AreEqual(
            File.GetLastWriteTimeUtc(source),
            File.GetLastWriteTimeUtc(destination));

        const FileAttributes safeAttributes =
            FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly;
        Assert.AreEqual(
            File.GetAttributes(source) & safeAttributes,
            File.GetAttributes(destination) & safeAttributes);
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

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
