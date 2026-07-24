using System.IO;

namespace MediaFileRenamer.App.Services;

public interface ITransferCleanup
{
    void Delete(string path);
}

internal sealed class TransferCleanup : ITransferCleanup
{
    public void Delete(string path) => File.Delete(path);
}
