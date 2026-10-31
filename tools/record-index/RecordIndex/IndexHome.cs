using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JJFlex.RecordIndex.Crypto;
using JJFlex.RecordIndex.Indexing;
using JJFlex.RecordIndex.Store;

namespace JJFlex.RecordIndex;

/// <summary>
/// The folder that holds the index: the encrypted database, its keyring, and
/// staged archives waiting to be merged. Never the record itself, and never the
/// repository.
/// </summary>
public sealed class IndexHome
{
    public const string DefaultHome = @"\\nas.macaw-jazz.ts.net\jjflex\record-index";
    public const string DefaultRecord = @"\\nas.macaw-jazz.ts.net\jjflex\historical";

    public string Folder { get; }
    public string DatabasePath => Path.Combine(Folder, "record-index.db");
    public string KeyringPath => Path.Combine(Folder, "record-index.keyring.json");
    public string RotatingKeyringPath => Path.Combine(Folder, "record-index.keyring.rotating.json");
    public string StagingFolder => Path.Combine(Folder, "staging");

    public IndexHome(string folder)
    {
        Folder = Path.GetFullPath(folder);
        if (Repository.IsInsideGitWorkTree(Folder))
            throw new IOException($"The index must not live inside a git working tree ({Folder}): it is private data, and this repository is public.");
    }

    public bool Exists => File.Exists(DatabasePath) || File.Exists(KeyringPath);

    public Keyring LoadKeyring() => Keyring.Load(KeyringPath);

    // ---------------------------------------------------------------- init

    public sealed class InitOptions
    {
        public string RecordRoot { get; set; } = DefaultRecord;
        public string RecoveryTo { get; set; } = "clipboard";
        public bool VerifyRecovery { get; set; } = true;
        public bool WithYubiKey { get; set; }
        public byte Slot { get; set; } = PivSlots.KeyManagement;
        public bool UseExistingKey { get; set; }
        public PivTouch Touch { get; set; } = PivTouch.Always;
        /// <summary>Under test: receives the recovery key text instead of the clipboard or a file.</summary>
        public Action<string>? CaptureRecovery { get; set; }
    }

    /// <summary>
    /// Creates the index: a random database key, a recovery key pair handed to
    /// the operator (and proved saved) BEFORE anything is written, an optional
    /// YubiKey recipient, and the staging key pair whose private half goes inside
    /// the database.
    /// </summary>
    public void Init(InitOptions o, UnlockOptions unlock, IPrompter prompt)
    {
        if (Exists) throw new IOException($"An index already exists in {Folder}. Nothing was changed.");
        Directory.CreateDirectory(Folder);

        var dbKey = Envelope.NewKey();
        try
        {
            var dbId = RandomNumberGenerator.GetBytes(16);
            var ring = new Keyring
            {
                DatabaseId = Convert.ToBase64String(dbId),
                KeyCheck = Convert.ToBase64String(Envelope.KeyCheck(dbKey, dbId)),
            };

            using var recovery = RecoveryKey.Generate();
            var recoverySpki = recovery.ExportSubjectPublicKeyInfo();
            HandRecovery(recovery, o.RecoveryTo, o.VerifyRecovery, o.CaptureRecovery, unlock, prompt);
            ring.Recipients.Add(Recipient.Create(Recipient.KindRecovery, "Recovery key (1Password)", recoverySpki, dbKey, dbId));

            if (o.WithYubiKey)
                ring.Recipients.Add(EnrollYubiKey(o.Slot, o.UseExistingKey, o.Touch, dbKey, dbId, unlock, prompt));

            using var staging = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ring.StagingPublicKey = Convert.ToBase64String(staging.ExportSubjectPublicKeyInfo());

            using (var db = IndexDatabase.Create(DatabasePath, dbKey, withFts: true))
            {
                db.SetMeta("schema_version", Schema.Version.ToString());
                db.SetMeta("database_id", ring.DatabaseId);
                db.SetMeta("record_root", Path.GetFullPath(o.RecordRoot));
                db.SetMeta("created_utc", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                db.SetMeta("staging_private_pkcs8", Convert.ToBase64String(staging.ExportPkcs8PrivateKey()));
            }
            ring.Save(KeyringPath);
            prompt.Say($"Created the index in {Folder}, locked to {ring.Recipients.Count} key{(ring.Recipients.Count == 1 ? "" : "s")}.");
        }
        finally { CryptographicOperations.ZeroMemory(dbKey); }
    }

    private static void HandRecovery(ECDiffieHellman recovery, string to, bool verify, Action<string>? capture, UnlockOptions unlock, IPrompter prompt)
    {
        var text = RecoveryKey.ToText(recovery);
        if (capture is not null) capture(text);
        else RecoveryHandoff.Hand(text, to, prompt);
        if (verify && !RecoveryHandoff.Verify(recovery.ExportSubjectPublicKeyInfo(), unlock, prompt))
            throw new UnlockException("The recovery key was not confirmed, so nothing was enrolled. Run the command again.");
    }

    private static Recipient EnrollYubiKey(byte slot, bool useExisting, PivTouch touch, byte[] dbKey, byte[] dbId, UnlockOptions unlock, IPrompter prompt)
    {
        using var dev = (unlock.OpenPiv ?? Unlocker.DefaultOpenPiv(prompt))(null);
        var existing = dev.GetPublicKey(slot);
        byte[] spki;
        if (existing is not null)
        {
            if (!useExisting)
                throw new UnlockException($"PIV slot {PivSlots.Name(slot)} on this YubiKey already holds a key. Nothing was changed. To use that key, add --existing; to keep it untouched, choose an empty slot such as 82 with --slot.");
            spki = existing;
            prompt.Say($"Using the key already in slot {PivSlots.Name(slot)}.");
        }
        else
        {
            if (useExisting) throw new UnlockException($"PIV slot {PivSlots.Name(slot)} is empty, so there is no existing key to use.");
            prompt.Say($"Generating a new key inside the YubiKey, in slot {PivSlots.Name(slot)}. It cannot be copied out.");
            spki = dev.GenerateP256(slot, touch);
        }
        // Prove the slot can actually unwrap before trusting it with the index.
        var trial = Envelope.NewKey();
        var probe = Envelope.Wrap(trial, spki, dbId);
        prompt.Say("Testing the YubiKey key once before enrolling it.");
        var back = Envelope.Unwrap(probe, spki, dbId, eph => dev.KeyAgree(slot, eph));
        if (!back.AsSpan().SequenceEqual(trial)) throw new UnlockException("The YubiKey test failed; nothing was enrolled.");
        var r = Recipient.Create(Recipient.KindYubiKey, "YubiKey", spki, dbKey, dbId);
        r.YubiKeySerial = dev.Serial;
        r.PivSlot = PivSlots.Name(slot);
        return r;
    }

    // ---------------------------------------------------------------- unlock

    /// <summary>Unlocks and opens the index. The returned session owns the key and zeroes it.</summary>
    public UnlockedIndex Open(UnlockOptions unlock, IPrompter prompt, bool readOnly = false)
    {
        if (!File.Exists(DatabasePath)) throw new UnlockException($"There is no index in {Folder}. Run record-index init first.");
        var rings = new List<(Keyring Ring, bool Rotating)> { (LoadKeyring(), false) };
        if (File.Exists(RotatingKeyringPath))
        {
            prompt.Say("A key rotation was interrupted. Trying both the old and the new key.");
            rings.Add((Keyring.Load(RotatingKeyringPath), true));
        }
        Exception? last = null;
        foreach (var (ring, rotating) in rings)
        {
            var (key, by) = Unlocker.Unlock(ring, unlock, prompt);
            try
            {
                var db = IndexDatabase.Open(DatabasePath, key, readOnly);
                if (db.GetMeta("database_id") != ring.DatabaseId)
                {
                    db.Dispose();
                    throw new UnlockException("The keyring belongs to a different index than this database.");
                }
                if (rotating) File.Move(RotatingKeyringPath, KeyringPath, overwrite: true);
                else if (File.Exists(RotatingKeyringPath)) File.Delete(RotatingKeyringPath);
                return new UnlockedIndex(this, db, key, ring, by);
            }
            catch (CryptographicException e) { last = e; CryptographicOperations.ZeroMemory(key); }
        }
        throw new UnlockException("The key unwrapped but did not open the database. " + last?.Message);
    }

    // ---------------------------------------------------------------- stage

    /// <summary>
    /// The seal's hook. Indexes one archive into an encrypted stage WITHOUT
    /// unlocking anything: the stage's key is wrapped to the staging public key,
    /// whose private half is inside the main database. The next unlocked catch-up
    /// or merge folds it in.
    /// </summary>
    public string Stage(string recordRoot, string archivePath, IPrompter prompt)
    {
        var ring = LoadKeyring();
        var full = Path.GetFullPath(archivePath);
        if (!RecordScanner.Included(recordRoot, full))
            throw new IOException($"{full} is not an archive the index covers (it must be inside {recordRoot}, and a zip or a loose text file outside the excluded folders).");
        var info = ArchiveIndexer.Describe(recordRoot, full);
        Directory.CreateDirectory(StagingFolder);

        var safe = new string(info.RelativePath.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safe}.stage.db";
        var stagePath = Path.Combine(StagingFolder, name);
        var partial = stagePath + ".partial";

        var key = Envelope.NewKey();
        try
        {
            var context = StageContext(ring, name);
            var predecessor = ArchiveIndexer.Predecessor(full);
            var manifest = new StageManifest
            {
                ArchivePath = info.RelativePath, Size = info.Size, MtimeUtc = info.MtimeUtc,
                Predecessor = predecessor is null ? null : Path.GetRelativePath(recordRoot, predecessor),
                Wrap = WrapJson.From(Envelope.Wrap(key, Convert.FromBase64String(ring.StagingPublicKey), context)),
            };
            if (File.Exists(partial)) File.Delete(partial);
            IndexResult result;
            using (var db = IndexDatabase.Create(partial, key, withFts: false))
            {
                db.SetMeta("database_id", ring.DatabaseId);
                using var w = new IndexWriter(db);
                result = ArchiveIndexer.Index(w, info, predecessor is null ? null : ArchiveIndexer.Signatures(predecessor));
                w.Commit();
            }
            File.WriteAllText(stagePath + ".json", JsonSerializer.Serialize(manifest));
            File.Move(partial, stagePath);
            prompt.Say($"Staged {info.RelativePath}: {result.Members} members, {result.TextMembers} with text. It joins the searchable index at the next unlocked catch-up or merge.");
            return stagePath;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static byte[] StageContext(Keyring ring, string stageName) =>
        [.. ring.DatabaseIdBytes, .. Encoding.UTF8.GetBytes("stage:" + stageName)];

    public IEnumerable<string> StagedFiles() =>
        Directory.Exists(StagingFolder)
            ? Directory.EnumerateFiles(StagingFolder, "*.stage.db").Where(f => File.Exists(f + ".json")).OrderBy(f => f, StringComparer.Ordinal)
            : [];
}

public sealed class StageManifest
{
    public string ArchivePath { get; set; } = "";
    public long Size { get; set; }
    public string MtimeUtc { get; set; } = "";
    /// <summary>The previous archive in the series; members unchanged since it are not in the stage.</summary>
    public string? Predecessor { get; set; }
    public WrapJson Wrap { get; set; } = new();
}
