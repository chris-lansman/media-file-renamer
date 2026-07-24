namespace MediaFileRenamer.App.Services;

public sealed class MetadataLookupException : Exception
{
    public MetadataLookupException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
