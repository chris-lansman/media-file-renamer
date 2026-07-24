using System.IO;

namespace MediaFileRenamer.App.Services;

public interface IFileMoveOperation
{
    void Move(string sourcePath, string destinationPath);
}

internal sealed class FileMoveOperation : IFileMoveOperation
{
    public void Move(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath);
}
