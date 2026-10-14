namespace MediaFileRenamer.App.Services;

internal static class FilenameDifference
{
    public static (string Prefix, string Changed, string Suffix) Compare(string original, string proposed)
    {
        var prefix = 0;
        while (prefix < Math.Min(original.Length, proposed.Length) && original[prefix] == proposed[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(original.Length, proposed.Length) - prefix
            && original[^(suffix + 1)] == proposed[^(suffix + 1)]) suffix++;
        return (proposed[..prefix], proposed[prefix..(proposed.Length - suffix)], proposed[(proposed.Length - suffix)..]);
    }
}
