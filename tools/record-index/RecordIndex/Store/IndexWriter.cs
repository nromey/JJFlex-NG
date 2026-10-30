using System.IO.Compression;
using System.Text;
using JJFlex.RecordIndex.Indexing;
using Microsoft.Data.Sqlite;

namespace JJFlex.RecordIndex.Store;

/// <summary>
/// Writes one archive's index inside one transaction, so an interrupted run
/// leaves either the whole archive indexed or none of it. Chunks and bodies are
/// de-duplicated against everything already in the database.
/// </summary>
public sealed class IndexWriter : IDisposable
{
    private readonly IndexDatabase _db;
    private readonly SqliteTransaction _tx;
    private readonly SqliteCommand _findChunk, _addChunk, _addFts, _findBody, _addBody, _addBodyChunk, _addMember, _findSig;

    public long NewChunks { get; private set; }
    public long ReusedChunks { get; private set; }
    public long NewChunkTextChars { get; private set; }

    public IndexWriter(IndexDatabase db)
    {
        _db = db;
        _tx = db.Connection.BeginTransaction();
        _findChunk = Prep("SELECT id FROM chunk WHERE sha256=$s", "$s");
        _addChunk = Prep("INSERT INTO chunk(sha256,text_z,line_map,char_len,first_ts) VALUES($s,$t,$m,$n,$ts) RETURNING id", "$s", "$t", "$m", "$n", "$ts");
        _addFts = db.HasFts ? Prep("INSERT INTO chunk_fts(rowid,text) VALUES($id,$t)", "$id", "$t") : null!;
        _findBody = Prep("SELECT id FROM body WHERE sha256=$s", "$s");
        _addBody = Prep("INSERT INTO body(sha256,size,chunk_count) VALUES($s,$z,$n) RETURNING id", "$s", "$z", "$n");
        _addBodyChunk = Prep("INSERT INTO body_chunk(body_id,seq,chunk_id,line_start,byte_offset) VALUES($b,$q,$c,$l,$o)", "$b", "$q", "$c", "$l", "$o");
        _addMember = Prep("INSERT INTO member(archive_id,path,crc32,size,mtime,kind,body_id,note) VALUES($a,$p,$crc,$z,$t,$k,$b,$n)", "$a", "$p", "$crc", "$z", "$t", "$k", "$b", "$n");
        _findSig = Prep("SELECT body_id FROM member WHERE crc32=$crc AND size=$z AND path=$p AND body_id IS NOT NULL LIMIT 1", "$crc", "$z", "$p");
    }

    private SqliteCommand Prep(string sql, params string[] names)
    {
        var c = _db.Command(sql, _tx);
        foreach (var n in names) c.Parameters.Add(new SqliteParameter(n, null));
        c.Prepare();
        return c;
    }

    public SqliteTransaction Transaction => _tx;

    public long AddArchive(ArchiveInfo a)
    {
        using var c = _db.Command("""
            INSERT INTO archive(path,kind,archive_date,size,mtime_utc,indexed_utc) VALUES($p,$k,$d,$z,$m,$i) RETURNING id
            """, _tx);
        c.Parameters.AddWithValue("$p", a.RelativePath);
        c.Parameters.AddWithValue("$k", a.Kind);
        c.Parameters.AddWithValue("$d", (object?)a.Date ?? DBNull.Value);
        c.Parameters.AddWithValue("$z", a.Size);
        c.Parameters.AddWithValue("$m", a.MtimeUtc);
        c.Parameters.AddWithValue("$i", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        return Convert.ToInt64(c.ExecuteScalar());
    }

    public void FinishArchive(long archiveId, int members, int textMembers)
    {
        using var c = _db.Command("UPDATE archive SET member_count=$m, text_members=$t WHERE id=$id", _tx);
        c.Parameters.AddWithValue("$m", members);
        c.Parameters.AddWithValue("$t", textMembers);
        c.Parameters.AddWithValue("$id", archiveId);
        c.ExecuteNonQuery();
    }

    /// <summary>A body already known by an identical member elsewhere (same path, CRC-32 and size).</summary>
    public long? FindBySignature(string path, uint crc, long size)
    {
        _findSig.Parameters["$crc"].Value = (long)crc;
        _findSig.Parameters["$z"].Value = size;
        _findSig.Parameters["$p"].Value = path;
        var r = _findSig.ExecuteScalar();
        return r is null or DBNull ? null : Convert.ToInt64(r);
    }

    public void AddMember(long archiveId, string path, uint? crc, long size, DateTimeOffset? mtime, string kind, long? bodyId, string? note)
    {
        var p = _addMember.Parameters;
        p["$a"].Value = archiveId;
        p["$p"].Value = path;
        p["$crc"].Value = crc is uint u ? (long)u : DBNull.Value;
        p["$z"].Value = size;
        p["$t"].Value = mtime is DateTimeOffset t ? t.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value;
        p["$k"].Value = kind;
        p["$b"].Value = bodyId is long b ? b : DBNull.Value;
        p["$n"].Value = (object?)note ?? DBNull.Value;
        _addMember.ExecuteNonQuery();
    }

    public long? FindBody(byte[] sha)
    {
        _findBody.Parameters["$s"].Value = sha;
        var r = _findBody.ExecuteScalar();
        return r is null or DBNull ? null : Convert.ToInt64(r);
    }

    /// <summary>Stores a body and its chunk list unless an identical body already exists.</summary>
    public long AddBody(byte[] sha, long size, IReadOnlyList<BodyChunkRef> chunks)
    {
        if (FindBody(sha) is long existing) return existing;
        _addBody.Parameters["$s"].Value = sha;
        _addBody.Parameters["$z"].Value = size;
        _addBody.Parameters["$n"].Value = chunks.Count;
        long id = Convert.ToInt64(_addBody.ExecuteScalar());
        var p = _addBodyChunk.Parameters;
        for (int i = 0; i < chunks.Count; i++)
        {
            p["$b"].Value = id;
            p["$q"].Value = i;
            p["$c"].Value = chunks[i].ChunkId;
            p["$l"].Value = chunks[i].LineStart;
            p["$o"].Value = chunks[i].ByteOffset;
            _addBodyChunk.ExecuteNonQuery();
        }
        return id;
    }

    public long GetOrAddChunk(ChunkData c) => GetOrAddChunk(c.Sha256(), c.Text, null, c.Map, c.FirstTimestamp);

    /// <summary>
    /// Finds a chunk by identity or stores it. <paramref name="compressed"/> may be
    /// supplied (when merging a stage) to avoid compressing twice.
    /// </summary>
    public long GetOrAddChunk(byte[] sha, string text, byte[]? compressed, byte[] map, string? firstTs)
    {
        _findChunk.Parameters["$s"].Value = sha;
        var found = _findChunk.ExecuteScalar();
        if (found is not null and not DBNull) { ReusedChunks++; return Convert.ToInt64(found); }

        var p = _addChunk.Parameters;
        p["$s"].Value = sha;
        p["$t"].Value = compressed ?? Compress(text);
        p["$m"].Value = map;
        p["$n"].Value = text.Length;
        p["$ts"].Value = (object?)firstTs ?? DBNull.Value;
        long id = Convert.ToInt64(_addChunk.ExecuteScalar());
        if (_db.HasFts)
        {
            _addFts.Parameters["$id"].Value = id;
            _addFts.Parameters["$t"].Value = text;
            _addFts.ExecuteNonQuery();
        }
        NewChunks++;
        NewChunkTextChars += text.Length;
        return id;
    }

    public void Commit() => _tx.Commit();

    public void Dispose()
    {
        foreach (var c in new[] { _findChunk, _addChunk, _addFts, _findBody, _addBody, _addBodyChunk, _addMember, _findSig })
            c?.Dispose();
        _tx.Dispose();
    }

    public static byte[] Compress(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) br.Write(raw);
        return ms.ToArray();
    }

    public static string Decompress(byte[] z)
    {
        using var br = new BrotliStream(new MemoryStream(z), CompressionMode.Decompress);
        using var sr = new StreamReader(br, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}

public readonly record struct BodyChunkRef(long ChunkId, long LineStart, long ByteOffset);

public sealed record ArchiveInfo(string FullPath, string RelativePath, string Kind, string? Date, long Size, string MtimeUtc);
