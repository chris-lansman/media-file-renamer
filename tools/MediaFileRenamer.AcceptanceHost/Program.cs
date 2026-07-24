using MediaFileRenamer.App.Services;
using MediaFileRenamer.App.ViewModels;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFileRenamer.AcceptanceHost;

internal static class Program
{
    private static string? _resultPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CommandOptions.Parse(args);
            _resultPath = options.OptionalPath("result-path");
            return options.Command switch
            {
                "transfer" => await TransferAsync(options),
                "undo" => Undo(options),
                "inspect-recovery" => InspectRecovery(options),
                "recover" => Recover(options),
                _ => throw new ArgumentException(
                    $"Unknown command '{options.Command}'.")
            };
        }
        catch (Exception exception)
        {
            WriteJson(new
            {
                success = false,
                error = exception.Message,
                exceptionType = exception.GetType().Name
            });
            return 2;
        }
    }

    private static async Task<int> TransferAsync(CommandOptions options)
    {
        var source = options.RequiredPath("source", mustExist: true);
        var destination = options.RequiredPath("destination", mustExist: false);
        var journalDirectory = options.RequiredDirectory("journal");
        var operation = Enum.Parse<FileOperation>(
            options.Required("operation"),
            ignoreCase: true);
        var cancellation = new CancellationTokenSource();
        var progress = BuildProgress(options, cancellation);
        var item = new MediaPreviewItem
        {
            SourcePath = source,
            SourceGroupPath = Path.GetDirectoryName(source) ?? "",
            Extension = Path.GetExtension(source),
            MediaType = "Movie",
            MatchedTitle = "Acceptance Fixture",
            Year = 2026,
            DestinationPath = destination,
            Status = "Manual choice"
        };

        foreach (var companion in options.Many("companion"))
        {
            var fullCompanion = Path.GetFullPath(companion);
            if (!File.Exists(fullCompanion))
            {
                throw new FileNotFoundException(
                    "A companion fixture does not exist.",
                    fullCompanion);
            }

            item.CompanionPaths.Add(fullCompanion);
        }

        var service = new OperationJournalService(journalDirectory);
        var result = await new RenameApplier(service).ApplyAsync(
            [item],
            operation,
            progress,
            cancellation.Token);
        WriteJson(new
        {
            success = string.IsNullOrWhiteSpace(result.FailureMessage),
            result.RolledBack,
            result.FailureMessage,
            result.JournalPath,
            item.Status,
            source,
            destination
        });
        return string.IsNullOrWhiteSpace(result.FailureMessage) ? 0 : 10;
    }

    private static IProgress<FileTransferProgress>? BuildProgress(
        CommandOptions options,
        CancellationTokenSource cancellation)
    {
        var pauseAtBytes = options.OptionalLong("pause-at-bytes");
        var pausedMarker = options.OptionalPath("paused-marker");
        var releaseMarker = options.OptionalPath("release-marker");
        var cancelWhenPaused = options.HasFlag("cancel-when-paused");
        if (pauseAtBytes is null)
        {
            return null;
        }

        if (pauseAtBytes < 1 || string.IsNullOrWhiteSpace(pausedMarker))
        {
            throw new ArgumentException(
                "Paused transfers require a positive --pause-at-bytes and "
                + "--paused-marker.");
        }

        if (!cancelWhenPaused && string.IsNullOrWhiteSpace(releaseMarker))
        {
            throw new ArgumentException(
                "Paused transfers require either --cancel-when-paused or "
                + "--release-marker.");
        }

        return new InlineProgress<FileTransferProgress>(value =>
        {
            if (value.BytesTransferred < pauseAtBytes)
            {
                return;
            }

            CreateMarkerOnce(
                pausedMarker,
                JsonSerializer.Serialize(new
                {
                    processId = Environment.ProcessId,
                    value.CurrentFile,
                    value.BytesTransferred,
                    value.TotalBytes
                }, JsonOptions));

            if (cancelWhenPaused)
            {
                cancellation.Cancel();
                return;
            }

            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!File.Exists(releaseMarker))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "The release marker was not created within five minutes.");
                }

                Thread.Sleep(50);
            }
        });
    }

    private static int Undo(CommandOptions options)
    {
        var journalDirectory = options.RequiredDirectory("journal");
        var result = new OperationJournalService(journalDirectory)
            .UndoLastCompleted();
        WriteJson(result);
        return result.Success ? 0 : 11;
    }

    private static int InspectRecovery(CommandOptions options)
    {
        var journalDirectory = options.RequiredDirectory("journal");
        var result = new OperationJournalService(journalDirectory)
            .GetInterruptedOperations();
        WriteJson(new
        {
            success = true,
            count = result.Count,
            operations = result
        });
        return 0;
    }

    private static int Recover(CommandOptions options)
    {
        var journalDirectory = options.RequiredDirectory("journal");
        var id = Guid.Parse(options.Required("id"));
        var action = Enum.Parse<InterruptedOperationAction>(
            options.Required("action"),
            ignoreCase: true);
        var result = new OperationJournalService(journalDirectory)
            .RecoverInterrupted(id, action);
        WriteJson(result);
        return result.Success ? 0 : 12;
    }

    private static void CreateMarkerOnce(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
        catch (IOException) when (File.Exists(path))
        {
            // The first progress callback owns the marker.
        }
    }

    private static void WriteJson<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (!string.IsNullOrWhiteSpace(_resultPath))
        {
            var directory = Path.GetDirectoryName(_resultPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(
                _resultPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }

        Console.WriteLine(json);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, List<string>> _values;
        private readonly HashSet<string> _flags;

        private CommandOptions(
            string command,
            Dictionary<string, List<string>> values,
            HashSet<string> flags)
        {
            Command = command;
            _values = values;
            _flags = flags;
        }

        public string Command { get; }

        public static CommandOptions Parse(string[] args)
        {
            if (args.Length == 0)
            {
                throw new ArgumentException(
                    "Specify transfer, undo, inspect-recovery, or recover.");
            }

            var values = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < args.Length; index++)
            {
                var token = args[index];
                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Unexpected argument '{token}'.");
                }

                var name = token[2..];
                if (index + 1 >= args.Length
                    || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    flags.Add(name);
                    continue;
                }

                if (!values.TryGetValue(name, out var list))
                {
                    list = [];
                    values[name] = list;
                }

                list.Add(args[++index]);
            }

            return new CommandOptions(
                args[0].ToLowerInvariant(),
                values,
                flags);
        }

        public string Required(string name)
        {
            return Many(name).LastOrDefault()
                ?? throw new ArgumentException($"Missing --{name}.");
        }

        public IReadOnlyList<string> Many(string name)
        {
            return _values.TryGetValue(name, out var values)
                ? values
                : [];
        }

        public bool HasFlag(string name) => _flags.Contains(name);

        public long? OptionalLong(string name)
        {
            var raw = Many(name).LastOrDefault();
            return raw is null ? null : long.Parse(raw);
        }

        public string? OptionalPath(string name)
        {
            var raw = Many(name).LastOrDefault();
            return raw is null ? null : Path.GetFullPath(raw);
        }

        public string RequiredPath(string name, bool mustExist)
        {
            var path = Path.GetFullPath(Required(name));
            if (mustExist && !File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The --{name} file does not exist.",
                    path);
            }

            return path;
        }

        public string RequiredDirectory(string name)
        {
            var path = Path.GetFullPath(Required(name));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
