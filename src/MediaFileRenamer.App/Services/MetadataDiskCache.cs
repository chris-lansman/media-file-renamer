using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MediaFileRenamer.App.Services;

public sealed class MetadataCacheOptions
{
    public int MaxEntries { get; init; } = 512;
    public long MaxFileBytes { get; init; } = 4 * 1024 * 1024;
    public TimeSpan DefaultTimeToLive { get; init; } = TimeSpan.FromDays(7);
}

public readonly record struct MetadataCacheKey
{
    private MetadataCacheKey(string provider, string resource, string identity)
    {
        Provider = NormalizeToken(provider, nameof(provider));
        Resource = NormalizeToken(resource, nameof(resource));
        Identity = NormalizeIdentity(identity);
    }

    public string Provider { get; }
    public string Resource { get; }
    public string Identity { get; }
    public string Canonical => $"{Provider}|{Resource}|{Identity}";

    public static MetadataCacheKey Search(
        string provider,
        string mediaType,
        string query)
    {
        return new MetadataCacheKey(
            provider,
            $"search-{NormalizeToken(mediaType, nameof(mediaType))}",
            NormalizeQuery(query));
    }

    public static MetadataCacheKey Episode(
        string provider,
        int seriesId,
        EpisodeOrder order,
        int? season,
        int? episode,
        DateOnly? airDate = null)
    {
        return new MetadataCacheKey(
            provider,
            "episode",
            string.Join(
                ":",
                seriesId,
                order.ToString().ToLowerInvariant(),
                season?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                episode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                airDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-"));
    }

    public static MetadataCacheKey ResourceById(
        string provider,
        string resource,
        params object?[] identifiers)
    {
        return new MetadataCacheKey(
            provider,
            resource,
            string.Join(
                ":",
                identifiers.Select(identifier =>
                    Convert.ToString(
                        identifier,
                        System.Globalization.CultureInfo.InvariantCulture) ?? "-")));
    }

    public override string ToString()
    {
        return Canonical;
    }

    private static string NormalizeToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Cache key components cannot be empty.", parameterName);
        }

        return value.Trim().ToLowerInvariant().Replace('|', '-');
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A cache search query cannot be empty.", nameof(query));
        }

        return string.Join(
                " ",
                query.Trim().Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }

    private static string NormalizeIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new ArgumentException("A cache identity cannot be empty.", nameof(identity));
        }

        return identity.Trim().ToLowerInvariant().Replace('|', '-');
    }
}

public sealed class MetadataDiskCache
{
    private const int CurrentSchemaVersion = 1;
    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<MetadataDiskCache> SharedInstance =
        new(() => new MetadataDiskCache());
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new LenientJsonStringEnumConverter() }
    };

    private readonly MetadataCacheOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _pathLock;

    public MetadataDiskCache(
        string? cachePath = null,
        MetadataCacheOptions? options = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        CachePath = Path.GetFullPath(
            cachePath
            ?? Path.Combine(AppDataPaths.Current.RootDirectory, "metadata-cache.json"));
        _options = options ?? new MetadataCacheOptions();
        if (_options.MaxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The metadata cache must allow at least one entry.");
        }

        if (_options.MaxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The metadata cache file size must be positive.");
        }

        if (_options.DefaultTimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The metadata cache lifetime must be positive.");
        }

        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _pathLock = PathLocks.GetOrAdd(CachePath, _ => new object());
    }

    public static MetadataDiskCache Shared => SharedInstance.Value;
    public string CachePath { get; }

    public int Count
    {
        get
        {
            lock (_pathLock)
            {
                var document = LoadUnsafe();
                if (PruneExpired(document) | TrimToBounds(document))
                {
                    SaveUnsafe(document);
                }

                return document.Entries.Count;
            }
        }
    }

    public bool TryGet<T>(MetadataCacheKey key, out T? value)
    {
        lock (_pathLock)
        {
            var document = LoadUnsafe();
            var changed = PruneExpired(document) | TrimToBounds(document);
            var entry = document.Entries
                .Where(candidate => string.Equals(
                    candidate.Key,
                    key.Canonical,
                    StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.CachedAtUtc)
                .FirstOrDefault();
            if (entry is null)
            {
                if (changed)
                {
                    SaveUnsafe(document);
                }

                value = default;
                return false;
            }

            try
            {
                value = entry.Value.Deserialize<T>(JsonOptions);
                if (changed)
                {
                    SaveUnsafe(document);
                }

                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                document.Entries.Remove(entry);
                SaveUnsafe(document);
                value = default;
                return false;
            }
        }
    }

    public void Set<T>(
        MetadataCacheKey key,
        T value,
        TimeSpan? timeToLive = null)
    {
        var lifetime = timeToLive ?? _options.DefaultTimeToLive;
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                "The metadata cache lifetime must be positive.");
        }

        JsonElement serializedValue;
        try
        {
            serializedValue = JsonSerializer.SerializeToElement(value, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return;
        }

        lock (_pathLock)
        {
            var document = LoadUnsafe();
            PruneExpired(document);
            document.Entries.RemoveAll(candidate => string.Equals(
                candidate.Key,
                key.Canonical,
                StringComparison.Ordinal));

            var now = _utcNow().ToUniversalTime();
            document.Entries.Add(new MetadataCacheEntry
            {
                Key = key.Canonical,
                CachedAtUtc = now,
                ExpiresAtUtc = now.Add(lifetime),
                Value = serializedValue
            });
            TrimToBounds(document);
            SaveUnsafe(document);
        }
    }

    public void Remove(MetadataCacheKey key)
    {
        lock (_pathLock)
        {
            var document = LoadUnsafe();
            if (document.Entries.RemoveAll(candidate => string.Equals(
                candidate.Key,
                key.Canonical,
                StringComparison.Ordinal)) > 0)
            {
                SaveUnsafe(document);
            }
        }
    }

    private MetadataCacheDocument LoadUnsafe()
    {
        if (!File.Exists(CachePath))
        {
            return new MetadataCacheDocument();
        }

        try
        {
            var document = JsonSerializer.Deserialize<MetadataCacheDocument>(
                File.ReadAllText(CachePath, Encoding.UTF8),
                JsonOptions);
            if (document?.Entries is null)
            {
                throw new JsonException("The metadata cache document has no entries collection.");
            }

            document.SchemaVersion = CurrentSchemaVersion;
            document.Entries = document.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(entry => entry.CachedAtUtc).First())
                .ToList();
            return document;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            QuarantineCorruptFileUnsafe();
            return new MetadataCacheDocument();
        }
    }

    private bool PruneExpired(MetadataCacheDocument document)
    {
        var now = _utcNow().ToUniversalTime();
        return document.Entries.RemoveAll(entry =>
            entry.ExpiresAtUtc == default || entry.ExpiresAtUtc <= now) > 0;
    }

    private bool TrimToBounds(MetadataCacheDocument document)
    {
        var originalCount = document.Entries.Count;
        document.Entries = document.Entries
            .Select((entry, index) => (Entry: entry, Index: index))
            .OrderByDescending(candidate => candidate.Entry.CachedAtUtc)
            .ThenByDescending(candidate => candidate.Index)
            .Take(_options.MaxEntries)
            .Select(candidate => candidate.Entry)
            .ToList();

        while (document.Entries.Count > 0
            && Serialize(document).LongLength > _options.MaxFileBytes)
        {
            document.Entries.RemoveAt(document.Entries.Count - 1);
        }

        return document.Entries.Count != originalCount;
    }

    private void SaveUnsafe(MetadataCacheDocument document)
    {
        var directory = Path.GetDirectoryName(CachePath);
        var temporaryPath = CachePath + ".tmp";
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = Serialize(document);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Metadata caching is an optimization and must never break matching.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A later write replaces this process-local temporary file.
            }
        }
    }

    private static byte[] Serialize(MetadataCacheDocument document)
    {
        document.SchemaVersion = CurrentSchemaVersion;
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private void QuarantineCorruptFileUnsafe()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            var corruptPath =
                $"{CachePath}.corrupt-{_utcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(CachePath, corruptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If quarantine is unavailable, the next successful write still repairs the cache.
        }
    }

    private sealed class MetadataCacheDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<MetadataCacheEntry> Entries { get; set; } = [];
    }

    private sealed class MetadataCacheEntry
    {
        public string Key { get; set; } = "";
        public DateTimeOffset CachedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public JsonElement Value { get; set; }
    }
}
