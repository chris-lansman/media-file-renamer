using System.IO;

namespace MediaFileRenamer.App.Services;

public enum PathAvailability
{
    Available,
    Unavailable,
    Indeterminate
}

public enum FilePathState
{
    Exists,
    Missing,
    Unavailable,
    Indeterminate
}

public interface IPathAvailabilityProbe
{
    PathAvailability GetRootAvailability(string path);

    FilePathState GetFileState(string path)
    {
        return GetRootAvailability(path) switch
        {
            PathAvailability.Unavailable => FilePathState.Unavailable,
            PathAvailability.Indeterminate => FilePathState.Indeterminate,
            _ => File.Exists(path) ? FilePathState.Exists : FilePathState.Missing
        };
    }
}

internal sealed class PathAvailabilityProbe : IPathAvailabilityProbe
{
    public PathAvailability GetRootAvailability(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return PathAvailability.Indeterminate;
            }

            if (root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return Directory.Exists(root)
                    ? PathAvailability.Available
                    : PathAvailability.Unavailable;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady
                ? PathAvailability.Available
                : PathAvailability.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return PathAvailability.Indeterminate;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException)
        {
            return PathAvailability.Unavailable;
        }
    }

    public FilePathState GetFileState(string path)
    {
        var rootAvailability = GetRootAvailability(path);
        if (rootAvailability == PathAvailability.Unavailable)
        {
            return FilePathState.Unavailable;
        }

        if (rootAvailability == PathAvailability.Indeterminate)
        {
            return FilePathState.Indeterminate;
        }

        try
        {
            _ = File.GetAttributes(path);
            return FilePathState.Exists;
        }
        catch (FileNotFoundException)
        {
            return FilePathState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FilePathState.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return FilePathState.Indeterminate;
        }
        catch (IOException)
        {
            return FilePathState.Indeterminate;
        }
    }
}
