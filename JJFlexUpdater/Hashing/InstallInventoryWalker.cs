namespace JJFlexUpdater.Hashing;

/// <summary>
/// Walks the install dir, computes sha256 per file with cache hits where
/// possible, and returns an <see cref="InstallInventory"/>. Hashes only
/// what changed since last run — the cache invalidates per-file when
/// either size or mtime drifts.
///
/// Excluded paths: anything under <see cref="ExcludedSubdirs"/>. Those
/// are user-data dirs that the updater must not touch (logs, traces,
/// crashes, the cache file itself, per-operator config). The exclude
/// list is conservative — better to leave a stale file present than to
/// nuke a user's preferences during a delta swap.
/// </summary>
public sealed class InstallInventoryWalker
{
    /// <summary>
    /// Forward-slash path prefixes (case-insensitive) that the walker
    /// skips. Conservative on purpose — anything user-writable that's
    /// shipped as part of the install bundle stays managed; anything
    /// the user creates at runtime is excluded.
    /// </summary>
    private static readonly string[] ExcludedSubdirs =
    {
        "logs/",
        "traces/",
        "crashes/",
        "errors/",
        "user/",
    };

    private readonly HashInventoryCache _cache;

    public InstallInventoryWalker() : this(new HashInventoryCache()) { }

    public InstallInventoryWalker(HashInventoryCache cache)
    {
        _cache = cache;
    }

    public async Task<InstallInventory> ScanAsync(
        string installDir,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(installDir))
            throw new DirectoryNotFoundException($"Install dir not found: {installDir}");

        var entries = new Dictionary<string, InventoryEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (string fullPath in Directory.EnumerateFiles(
                     installDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relPath = ToRelativeForwardSlash(installDir, fullPath);
            if (IsExcluded(relPath)) continue;

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
            }
            catch
            {
                continue; // file might have vanished mid-walk
            }

            long size = info.Length;
            long mtime = info.LastWriteTimeUtc.Ticks;
            string sha256;

            if (_cache.TryGet(relPath, size, mtime, out string cached))
            {
                sha256 = cached;
            }
            else
            {
                try
                {
                    sha256 = await LocalFileHasher.ComputeAsync(fullPath, cancellationToken)
                                                  .ConfigureAwait(false);
                    _cache.Set(relPath, size, mtime, sha256);
                }
                catch (IOException)
                {
                    // Locked / vanished file — skip it. Update path will
                    // discover the discrepancy via the manifest comparison.
                    continue;
                }
            }

            entries[relPath] = new InventoryEntry
            {
                RelPath = relPath,
                SizeBytes = size,
                Sha256 = sha256,
            };
        }

        _cache.RetainOnly(entries.Keys.ToList());
        _cache.Save();

        return new InstallInventory(installDir, entries);
    }

    private static string ToRelativeForwardSlash(string installDir, string fullPath)
    {
        string rel = Path.GetRelativePath(installDir, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsExcluded(string relPath)
    {
        foreach (string prefix in ExcludedSubdirs)
        {
            if (relPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Exclude our own cache file in case the install dir overlaps with
        // the LocalAppData layout in a portable-mode deploy.
        return relPath.Equals(HashInventoryCache.DefaultFileName, StringComparison.OrdinalIgnoreCase);
    }
}
