namespace JJFlexUpdater.Staging;

/// <summary>
/// Layout of the staging dir Track D writes and Track M (helper exe) reads.
/// One per update attempt. The dir name uses a GUID so simultaneous updates
/// (rare, but possible if a user re-clicks) don't collide.
///
/// Layout:
///   %TEMP%\JJFlexUpdate-&lt;guid&gt;\
///   ├── handoff-manifest.json
///   ├── files\           (downloaded payload, mirrors install-dir layout)
///   └── backup\          (helper writes here for rollback; we just create it)
/// </summary>
public sealed class StagingDir
{
    public const string HandoffManifestName = "handoff-manifest.json";
    public const string FilesSubdir = "files";
    public const string BackupSubdir = "backup";

    public string Root { get; }
    public string FilesDir => Path.Combine(Root, FilesSubdir);
    public string BackupDir => Path.Combine(Root, BackupSubdir);
    public string HandoffManifestPath => Path.Combine(Root, HandoffManifestName);

    private StagingDir(string root)
    {
        Root = root;
    }

    /// <summary>
    /// Create a fresh staging dir under %TEMP% with files\ and backup\
    /// subdirs ready. Callers downloads into <see cref="FilesDir"/> then
    /// write the handoff manifest via
    /// <see cref="HandoffManifestBuilder.WriteAsync"/>.
    /// </summary>
    public static StagingDir Create()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "JJFlexUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, FilesSubdir));
        Directory.CreateDirectory(Path.Combine(root, BackupSubdir));
        return new StagingDir(root);
    }

    /// <summary>
    /// Re-attach to an existing staging dir (used by manual-retry tooling
    /// that points at a directory left behind from a failed prior attempt).
    /// </summary>
    public static StagingDir Attach(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Staging dir not found: {root}");
        Directory.CreateDirectory(Path.Combine(root, FilesSubdir));
        Directory.CreateDirectory(Path.Combine(root, BackupSubdir));
        return new StagingDir(root);
    }
}
