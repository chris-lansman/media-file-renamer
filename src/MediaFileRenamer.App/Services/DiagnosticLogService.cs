using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaFileRenamer.App.Services;

public sealed partial class DiagnosticLogService
{
    private const long DefaultMaximumLogBytes = 1_048_576;
    private const int DefaultRetainedLogCount = 5;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, byte> _sensitiveValues =
        new(StringComparer.Ordinal);
    private readonly long _maximumLogBytes;
    private readonly int _retainedLogCount;

    public DiagnosticLogService(
        string? logDirectory = null,
        long maximumLogBytes = DefaultMaximumLogBytes,
        int retainedLogCount = DefaultRetainedLogCount)
    {
        if (maximumLogBytes < 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLogBytes),
                "The maximum log size must be at least 256 bytes.");
        }

        if (retainedLogCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedLogCount),
                "At least one log file must be retained.");
        }

        LogDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaFileRenamer",
            "Logs");
        _maximumLogBytes = maximumLogBytes;
        _retainedLogCount = retainedLogCount;
    }

    public string LogDirectory { get; }

    public string CurrentLogPath => Path.Combine(LogDirectory, "MediaFileRenamer.log");

    public void RegisterSensitiveValue(string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length >= 3)
        {
            _sensitiveValues.TryAdd(trimmed, 0);
        }
    }

    public void Information(string message)
    {
        Write("INFO", message);
    }

    public void Warning(string message)
    {
        Write("WARN", message);
    }

    public void Error(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", detail);
    }

    public string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "";
        }

        var redacted = ApiQueryValueRegex().Replace(message, "$1[REDACTED]");
        redacted = JsonCredentialRegex().Replace(redacted, "$1[REDACTED]$3");
        redacted = BearerTokenRegex().Replace(redacted, "$1[REDACTED]");

        foreach (var sensitiveValue in _sensitiveValues.Keys.OrderByDescending(value => value.Length))
        {
            redacted = redacted.Replace(
                sensitiveValue,
                "[REDACTED]",
                StringComparison.Ordinal);
        }

        redacted = WindowsPathRegex().Replace(redacted, "[PATH]");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            redacted = redacted.Replace(
                userProfile,
                "%USERPROFILE%",
                StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }

    public string BuildDiagnosticSummary()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        return string.Join(
            Environment.NewLine,
            $"Media File Renamer {version}",
            $"OS: {Environment.OSVersion}",
            $"Runtime: {Environment.Version}",
            $"Log: {Redact(CurrentLogPath)}");
    }

    private void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} [{level}] {Redact(message)}{Environment.NewLine}";
            var byteCount = Encoding.UTF8.GetByteCount(line);

            lock (_writeLock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(byteCount);
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never make the application fail.
        }
    }

    private void RotateIfNeeded(int pendingByteCount)
    {
        var current = new FileInfo(CurrentLogPath);
        if (!current.Exists || current.Length + pendingByteCount <= _maximumLogBytes)
        {
            return;
        }

        var oldestPath = GetArchivePath(_retainedLogCount - 1);
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (var index = _retainedLogCount - 2; index >= 1; index--)
        {
            var source = GetArchivePath(index);
            if (File.Exists(source))
            {
                File.Move(source, GetArchivePath(index + 1), true);
            }
        }

        if (_retainedLogCount > 1)
        {
            File.Move(CurrentLogPath, GetArchivePath(1), true);
        }
        else
        {
            File.Delete(CurrentLogPath);
        }
    }

    private string GetArchivePath(int index)
    {
        return Path.Combine(LogDirectory, $"MediaFileRenamer.{index}.log");
    }

    [GeneratedRegex(@"(?i)([?&](?:api_key|apikey|pin)=)[^&\s]+")]
    private static partial Regex ApiQueryValueRegex();

    [GeneratedRegex(
        @"(?i)(""?(?:TmdbApiKey|TvdbApiKey|TvdbPin|api_key|apikey|pin)""?\s*[:=]\s*""?)([^"",}\s]+)(""?)")]
    private static partial Regex JsonCredentialRegex();

    [GeneratedRegex(@"(?i)(Authorization\s*:\s*Bearer\s+)[^\s]+")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(?:[A-Z]:\\|\\\\[^\\\s]+\\)[^""'\r\n]+")]
    private static partial Regex WindowsPathRegex();
}

public static class DiagnosticLog
{
    public static DiagnosticLogService Current { get; } = new();
}
