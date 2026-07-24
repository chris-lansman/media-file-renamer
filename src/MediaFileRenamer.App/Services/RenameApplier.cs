using MediaFileRenamer.App.ViewModels;
using System.IO;

namespace MediaFileRenamer.App.Services;

public enum FileOperation
{
    Move,
    Copy
}

public sealed class RenameApplier
{
    public RenameResult Apply(IEnumerable<MediaPreviewItem> items, FileOperation operation)
    {
        var itemList = items.ToList();
        var sourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedItems = new List<MediaPreviewItem>();
        var duplicateDestinations = itemList
            .Where(item => !string.IsNullOrWhiteSpace(item.DestinationPath))
            .GroupBy(item => Path.GetFullPath(item.DestinationPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in itemList)
        {
            try
            {
                if (item.MediaType == "TV" && (item.Season is null || item.Episode is null))
                {
                    item.Status = "Failed: TV episode number is unresolved";
                    continue;
                }

                if (!File.Exists(item.SourcePath))
                {
                    item.Status = "Failed: source file no longer exists";
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.DestinationPath))
                {
                    item.Status = "Invalid destination";
                    continue;
                }

                var source = Path.GetFullPath(item.SourcePath);
                var destination = Path.GetFullPath(item.DestinationPath);
                if (duplicateDestinations.Contains(destination))
                {
                    item.Status = "Failed: another item has the same destination";
                    continue;
                }

                if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                {
                    item.Status = "Already named";
                    completedItems.Add(item);
                    continue;
                }

                if (File.Exists(destination))
                {
                    item.Status = "Failed: destination file already exists";
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destination);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    item.Status = "Invalid destination";
                    continue;
                }

                Directory.CreateDirectory(destinationDirectory);

                if (operation == FileOperation.Move)
                {
                    var sourceDirectory = Path.GetDirectoryName(item.SourcePath);
                    File.Move(item.SourcePath, destination);
                    if (!string.IsNullOrWhiteSpace(sourceDirectory))
                    {
                        sourceDirectories.Add(sourceDirectory);
                    }
                }
                else
                {
                    File.Copy(item.SourcePath, destination);
                }

                item.DestinationPath = destination;
                item.Status = operation == FileOperation.Move ? "Moved" : "Copied";
                completedItems.Add(item);
            }
            catch (Exception ex)
            {
                item.Status = $"Failed: {ex.Message}";
            }
        }

        var deletedFolders = operation == FileOperation.Move
            ? DeleteEmptySourceFolders(sourceDirectories)
            : 0;

        return new RenameResult(deletedFolders, completedItems);
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
                // The move succeeded; leave a locked source folder for the user to inspect.
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
        // Explorer, antivirus, or thumbnail generation can briefly keep a newly emptied
        // download folder busy immediately after the final file is moved.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            var entries = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories);
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
}

public sealed record RenameResult(
    int DeletedSourceFolders,
    IReadOnlyList<MediaPreviewItem> CompletedItems);
