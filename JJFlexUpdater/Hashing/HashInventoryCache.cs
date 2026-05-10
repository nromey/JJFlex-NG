using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlexUpdater.Hashing;

/// <summary>
/// Disk-backed cache of (rel_path, mtime_ticks, size_bytes) → sha256.
/// Keeps repeated update checks fast: a 200 MB install hashes in seconds
/// the first time and milliseconds thereafter, since we only re-hash files
/// whose mtime or size has changed.
///
/// Storage: <c>%LOCALAPPDATA%\JJFlexRadio\update-hash-cache.json</c>. The
/// file is a best-effort cache, not authoritative — if it's missing,
/// corrupt, or out of date for a given file, we just re-hash. Never
/// throws on read or save failures.
/// </summary>
public sealed class HashInventoryCache
{
    public const string DefaultFileName = "update-hash-cache.json";

    private readonly string _path;
    private Dictionary<string, CachedEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public HashInventoryCache() : this(DefaultCachePath()) { }

    public HashInventoryCache(string path)
    {
        _path = path;
        Load();
    }

    public static string DefaultCachePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JJFlexRadio",
            DefaultFileName);

    public bool TryGet(string relPath, long sizeBytes, long mtimeTicks, out string sha256)
    {
        sha256 = string.Empty;
        if (_entries.TryGetValue(relPath, out var entry)
            && entry.SizeBytes == sizeBytes
            && entry.MtimeTicks == mtimeTicks
            && !string.IsNullOrEmpty(entry.Sha256))
        {
            sha256 = entry.Sha256;
            return true;
        }
        return false;
    }

    public void Set(string relPath, long sizeBytes, long mtimeTicks, string sha256)
    {
        _entries[relPath] = new CachedEntry
        {
            SizeBytes = sizeBytes,
            MtimeTicks = mtimeTicks,
            Sha256 = sha256,
        };
    }

    /// <summary>
    /// Drop entries that no longer correspond to a file in the install dir.
    /// Called after a fresh inventory walk so the cache doesn't bloat over
    /// time as files come and go between releases.
    /// </summary>
    public void RetainOnly(IReadOnlyCollection<string> liveRelPaths)
    {
        var live = new HashSet<string>(liveRelPaths, StringComparer.OrdinalIgnoreCase);
        var toDrop = _entries.Keys.Where(k => !live.Contains(k)).ToList();
        foreach (var k in toDrop) _entries.Remove(k);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var dto = new CacheDocument { Entries = _entries };
            string json = JsonSerializer.Serialize(dto, SerializerOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Cache is best-effort; failures here just cost a re-hash next run.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            string json = File.ReadAllText(_path);
            var dto = JsonSerializer.Deserialize<CacheDocument>(json, SerializerOptions);
            if (dto?.Entries is { Count: > 0 })
                _entries = new Dictionary<string, CachedEntry>(dto.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Corrupt cache — start fresh; will be overwritten on next Save.
            _entries = new Dictionary<string, CachedEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class CacheDocument
    {
        [JsonPropertyName("entries")]
        public Dictionary<string, CachedEntry>? Entries { get; set; }
    }

    private sealed class CachedEntry
    {
        [JsonPropertyName("size")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("mtime")]
        public long MtimeTicks { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
