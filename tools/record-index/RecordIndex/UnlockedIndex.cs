using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using JJFlex.RecordIndex.Crypto;
using JJFlex.RecordIndex.Indexing;
using JJFlex.RecordIndex.Store;

namespace JJFlex.RecordIndex;

public sealed record CatchUpReport(int Archives, int Indexed, int Unchanged, int Removed, int Merged, int Failed, long BytesRead, long NewChunks, long NewChunkChars, TimeSpan Elapsed);

/// <summary>An open, unlocked index. Holds the database key in memory only, and zeroes it on dispose.</summary>
public sealed class UnlockedIndex : IDisposable
{
    private readonly IndexHome _home;
    private byte[] _key;

    public IndexDatabase Db { get; }
    public Keyring Ring { get; private set; }
    public Recipient UnlockedBy { get; }

    internal UnlockedIndex(IndexHome home, IndexDatabase db, byte[] key, Keyring ring, Recipient by)
    {
        _home = home; Db = db; _key = key; Ring = ring; UnlockedBy = by;
    }

    public string RecordRoot => Db.GetMeta("record_root") ?? IndexHome.DefaultRecord;

    // ---------------------------------------------------------------- indexing

    private (long Id, long Size, string Mtime)? ArchiveState(string rel)
    {
        using var c = Db.Command("SELECT id, size, mtime_utc FROM archive WHERE path=$p");
        c.Parameters.AddWithValue("$p", rel);
        using var r = c.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.GetString(2)) : null;
    }

    private void RemoveArchive(long id)
    {
        using var c = Db.Command("DELETE FROM archive WHERE id=$id");
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
    }

    /// <summary>Indexes one archive now, replacing an older index of it if the file changed.</summary>
    public (bool Indexed, IndexResult? Result) IndexArchive(string fullPath)
    {
        var info = ArchiveIndexer.Describe(RecordRoot, fullPath);
        if (ArchiveState(info.RelativePath) is { } s)
        {
            if (s.Size == info.Size && s.Mtime == info.MtimeUtc) return (false, null);
            RemoveArchive(s.Id);
        }
        using var w = new IndexWriter(Db);
        var result = ArchiveIndexer.Index(w, info);
        w.Commit();
        return (true, result);
    }

    /// <summary>
    /// Brings the index level with the record: merges anything staged, indexes
    /// every archive that is new or changed, and forgets archives that were pruned
    /// from the record (then drops text nothing refers to any more).
    /// </summary>
    public CatchUpReport CatchUp(IPrompter prompt, string? only = null, int? limit = null, bool dryRun = false)
    {
        var sw = Stopwatch.StartNew();
        int merged = dryRun ? 0 : Merge(prompt);
        var onDisk = RecordScanner.Enumerate(RecordRoot).ToList();
        if (only is not null) onDisk = onDisk.Where(p => Path.GetRelativePath(RecordRoot, p).StartsWith(only, StringComparison.OrdinalIgnoreCase)).ToList();

        var known = new Dictionary<string, (long Id, long Size, string Mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var c = Db.Command("SELECT id, path, size, mtime_utc FROM archive"))
        using (var r = c.ExecuteReader())
            while (r.Read()) known[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetString(3));

        int indexed = 0, unchanged = 0, removed = 0, failed = 0;
        long bytes = 0, newChunks = 0, newChars = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int todo = onDisk.Count(p =>
        {
            var fi = new FileInfo(p);
            var rel = Path.GetRelativePath(RecordRoot, p);
            return !(known.TryGetValue(rel, out var k) && k.Size == fi.Length && k.Mtime == fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"));
        });
        prompt.Say($"{onDisk.Count} archives in the record; {todo} new or changed.");

        int done = 0;
        foreach (var path in onDisk)
        {
            var rel = Path.GetRelativePath(RecordRoot, path);
            seen.Add(rel);
            var fi = new FileInfo(path);
            if (known.TryGetValue(rel, out var k) && k.Size == fi.Length && k.Mtime == fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")) { unchanged++; continue; }
            if (limit is int lim && indexed >= lim) continue;
            if (dryRun) { indexed++; continue; }
            var t = Stopwatch.StartNew();
            try
            {
                var (did, res) = IndexArchive(path);
                done++;
                if (did && res is not null)
                {
                    indexed++;
                    bytes += res.BytesRead; newChunks += res.NewChunks; newChars += res.NewChunkChars;
                    prompt.Say($"{done} of {todo}: {rel}, {res.TextMembers} of {res.Members} members with text, {res.NewChunks} new chunks, {t.Elapsed.TotalSeconds:0.0} seconds.");
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failed++;
                prompt.Say($"Could not index {rel}: {e.Message}");
            }
        }

        if (only is null && limit is null && !dryRun)
        {
            foreach (var (rel, k) in known)
                if (!seen.Contains(rel)) { RemoveArchive(k.Id); removed++; }
            if (removed > 0) prompt.Say($"{removed} archives were pruned from the record and are dropped from the index.");
        }
        if (!dryRun && (removed > 0 || indexed > 0)) CollectGarbage();
        return new CatchUpReport(onDisk.Count, indexed, unchanged, removed, merged, failed, bytes, newChunks, newChars, sw.Elapsed);
    }

    /// <summary>Drops bodies no member uses, and chunks no body uses, from the table and the full-text index.</summary>
    public (int Bodies, int Chunks) CollectGarbage()
    {
        using var tx = Db.Connection.BeginTransaction();
        int bodies, chunks = 0;
        using (var c = Db.Command("DELETE FROM body WHERE id NOT IN (SELECT body_id FROM member WHERE body_id IS NOT NULL)", tx))
            bodies = c.ExecuteNonQuery();
        var dead = new List<long>();
        using (var c = Db.Command("SELECT id FROM chunk WHERE id NOT IN (SELECT chunk_id FROM body_chunk)", tx))
        using (var r = c.ExecuteReader())
            while (r.Read()) dead.Add(r.GetInt64(0));
        using (var delFts = Db.Command("DELETE FROM chunk_fts WHERE rowid=$id", tx))
        using (var delChunk = Db.Command("DELETE FROM chunk WHERE id=$id", tx))
        {
            delFts.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$id", 0L));
            delChunk.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$id", 0L));
            foreach (var id in dead)
            {
                delFts.Parameters[0].Value = id; delFts.ExecuteNonQuery();
                delChunk.Parameters[0].Value = id; delChunk.ExecuteNonQuery();
                chunks++;
            }
        }
        tx.Commit();
        return (bodies, chunks);
    }

    // ---------------------------------------------------------------- staging

    /// <summary>Folds every staged archive into the index, then deletes the stage (the index's own file, never the record).</summary>
    public int Merge(IPrompter prompt)
    {
        var staged = _home.StagedFiles().ToList();
        if (staged.Count == 0) return 0;
        var pkcs8 = Db.GetMeta("staging_private_pkcs8") ?? throw new UnlockException("This index has no staging key.");
        using var stagingKey = ECDiffieHellman.Create();
        stagingKey.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8), out _);
        var stagingSpki = Convert.FromBase64String(Ring.StagingPublicKey);
        int merged = 0;
        foreach (var stagePath in staged)
        {
            var manifest = JsonSerializer.Deserialize<StageManifest>(File.ReadAllText(stagePath + ".json"))!;
            var key = Envelope.Unwrap(manifest.Wrap.ToWrapped(), stagingSpki, IndexHome.StageContext(Ring, Path.GetFileName(stagePath)), Envelope.SoftwareAgreement(stagingKey));
            try
            {
                using (var stage = IndexDatabase.Open(stagePath, key, readOnly: true))
                    MergeOne(stage, manifest);
                File.Delete(stagePath);
                File.Delete(stagePath + ".json");
                merged++;
                prompt.Say($"Merged the staged {manifest.ArchivePath}.");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        return merged;
    }

    private void MergeOne(IndexDatabase stage, StageManifest m)
    {
        if (ArchiveState(m.ArchivePath) is { } s)
        {
            if (s.Size == m.Size && s.Mtime == m.MtimeUtc) return;
            RemoveArchive(s.Id);
        }
        using var w = new IndexWriter(Db);
        using (var ca = stage.Command("SELECT id, path, kind, archive_date, size, mtime_utc, member_count, text_members FROM archive"))
        using (var ra = ca.ExecuteReader())
        {
            while (ra.Read())
            {
                var info = new ArchiveInfo("", ra.GetString(1), ra.GetString(2), ra.IsDBNull(3) ? null : ra.GetString(3), ra.GetInt64(4), ra.GetString(5));
                long newArchive = w.AddArchive(info);
                var bodyMap = new Dictionary<long, long>();
                using var cm = stage.Command("SELECT path, crc32, size, mtime, kind, body_id, note FROM member WHERE archive_id=$a ORDER BY id");
                cm.Parameters.AddWithValue("$a", ra.GetInt64(0));
                using var rm = cm.ExecuteReader();
                while (rm.Read())
                {
                    long? body = null;
                    if (!rm.IsDBNull(5))
                    {
                        long sb = rm.GetInt64(5);
                        if (!bodyMap.TryGetValue(sb, out var mb)) bodyMap[sb] = mb = CopyBody(stage, w, sb);
                        body = mb;
                    }
                    DateTimeOffset? mt = rm.IsDBNull(3) ? null : DateTimeOffset.Parse(rm.GetString(3), System.Globalization.CultureInfo.InvariantCulture);
                    w.AddMember(newArchive, rm.GetString(0), rm.IsDBNull(1) ? null : (uint)rm.GetInt64(1), rm.GetInt64(2), mt, rm.GetString(4), body, rm.IsDBNull(6) ? null : rm.GetString(6));
                }
                w.FinishArchive(newArchive, ra.GetInt32(6), ra.GetInt32(7));
            }
        }
        w.Commit();
    }

    private static long CopyBody(IndexDatabase stage, IndexWriter w, long stageBody)
    {
        byte[] sha; long size;
        using (var c = stage.Command("SELECT sha256, size FROM body WHERE id=$b"))
        {
            c.Parameters.AddWithValue("$b", stageBody);
            using var r = c.ExecuteReader(); r.Read();
            sha = (byte[])r[0]; size = r.GetInt64(1);
        }
        if (w.FindBody(sha) is long existing) return existing;
        var refs = new List<BodyChunkRef>();
        using (var c = stage.Command("""
            SELECT ch.sha256, ch.text_z, ch.line_map, ch.first_ts, bc.line_start, bc.byte_offset
            FROM body_chunk bc JOIN chunk ch ON ch.id = bc.chunk_id WHERE bc.body_id=$b ORDER BY bc.seq
            """))
        {
            c.Parameters.AddWithValue("$b", stageBody);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var z = (byte[])r[1];
                long id = w.GetOrAddChunk((byte[])r[0], IndexWriter.Decompress(z), z, (byte[])r[2], r.IsDBNull(3) ? null : r.GetString(3));
                refs.Add(new BodyChunkRef(id, r.GetInt64(4), r.GetInt64(5)));
            }
        }
        return w.AddBody(sha, size, refs);
    }

    // ---------------------------------------------------------------- keys

    /// <summary>Adds a recipient: wraps the SAME database key to one more public key. The database is not touched.</summary>
    public Recipient AddRecipient(Recipient r)
    {
        if (Ring.Recipients.Any(x => x.Id == r.Id)) throw new UnlockException($"That key is already enrolled, as {r.Id}.");
        Ring.Recipients.Add(r);
        Ring.Save(_home.KeyringPath);
        return r;
    }

    public Recipient NewRecoveryRecipient(string label, byte[] spki) =>
        Recipient.Create(Recipient.KindRecovery, label, spki, _key, Ring.DatabaseIdBytes);

    public Recipient NewYubiKeyRecipient(byte[] spki, int? serial, byte slot)
    {
        var r = Recipient.Create(Recipient.KindYubiKey, "YubiKey", spki, _key, Ring.DatabaseIdBytes);
        r.YubiKeySerial = serial;
        r.PivSlot = PivSlots.Name(slot);
        return r;
    }

    /// <summary>
    /// Removes a recipient. Refused if it would leave nothing, or if it is the
    /// key this session was unlocked with: the operator must prove another key
    /// still opens the index before taking one away.
    /// </summary>
    public Recipient RemoveRecipient(string id)
    {
        var r = Ring.Recipients.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new UnlockException($"No key with id {id} is enrolled. Run record-index keys list.");
        if (Ring.Recipients.Count == 1) throw new UnlockException("That is the only key. Removing it would lock the index for good.");
        if (r.Id == UnlockedBy.Id)
            throw new UnlockException("That is the key you unlocked with. Unlock with a different key to remove this one, so you know another key still works.");
        Ring.Recipients.Remove(r);
        Ring.Save(_home.KeyringPath);
        return r;
    }

    /// <summary>
    /// Replaces the database key itself, for when a key may have been exposed:
    /// removing a recipient stops it unlocking from now on, but whoever already
    /// unwrapped the old key could still read an old copy of the database.
    /// Crash-safe order: the new keyring is written aside first, then the
    /// database is re-encrypted, then the new keyring replaces the old.
    /// </summary>
    public void RotateDatabaseKey(IPrompter prompt)
    {
        var newKey = Envelope.NewKey();
        try
        {
            var dbId = Ring.DatabaseIdBytes;
            var next = JsonSerializer.Deserialize<Keyring>(JsonSerializer.Serialize(Ring))!;
            next.KeyCheck = Convert.ToBase64String(Envelope.KeyCheck(newKey, dbId));
            foreach (var r in next.Recipients) r.Rewrap(newKey, dbId);
            next.Save(_home.RotatingKeyringPath);
            prompt.Say("Re-encrypting every page of the database under a new key. Nothing is re-indexed.");
            Db.Rekey(newKey);
            File.Move(_home.RotatingKeyringPath, _home.KeyringPath, overwrite: true);
            Ring = next;
            CryptographicOperations.ZeroMemory(_key);
            _key = newKey;
            newKey = [];
        }
        finally { if (newKey.Length > 0) CryptographicOperations.ZeroMemory(newKey); }
    }

    // ---------------------------------------------------------------- facts

    public IReadOnlyList<(string Name, long Value)> Counts()
    {
        var list = new List<(string, long)>();
        foreach (var (name, sql) in new[]
        {
            ("archives", "SELECT count(*) FROM archive"),
            ("members", "SELECT count(*) FROM member"),
            ("members with text", "SELECT count(*) FROM member WHERE body_id IS NOT NULL"),
            ("distinct member contents", "SELECT count(*) FROM body"),
            ("distinct chunks", "SELECT count(*) FROM chunk"),
            ("chunk placements", "SELECT count(*) FROM body_chunk"),
            ("indexed characters", "SELECT coalesce(sum(char_len),0) FROM chunk"),
            ("compressed text bytes", "SELECT coalesce(sum(length(text_z)),0) FROM chunk"),
        })
        {
            using var c = Db.Command(sql);
            list.Add((name, Convert.ToInt64(c.ExecuteScalar())));
        }
        return list;
    }

    public void Optimize()
    {
        Db.Exec("INSERT INTO chunk_fts(chunk_fts) VALUES('optimize');");
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        Db.Dispose();
    }
}
