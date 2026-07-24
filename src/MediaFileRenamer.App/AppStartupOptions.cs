using MediaFileRenamer.App.Services;

namespace MediaFileRenamer.App;

internal sealed record AppStartupOptions(AppDataPaths? DataPaths)
{
    public static AppStartupOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        AppDataPaths? dataPaths = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    "--data-root",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dataPaths is not null)
            {
                throw new ArgumentException(
                    "The --data-root option can be specified only once.");
            }

            if (index + 1 >= arguments.Count
                || string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                throw new ArgumentException(
                    "The --data-root option requires an absolute directory path.");
            }

            dataPaths = AppDataPaths.CreateForDataRoot(arguments[++index]);
        }

        return new AppStartupOptions(dataPaths);
    }
}
