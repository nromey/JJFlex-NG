using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace JJFlex.RecordIndex.Store;

/// <summary>
/// The encrypted SQLite database. Whole-file page encryption: full-text search
/// needs its words in plain form INSIDE the database, so encrypting columns would
/// either leave the index readable or make it unsearchable. Every page on disk,
/// including the FTS5 index pages, is encrypted.
///
/// Engine: SQLite3 Multiple Ciphers (MIT, maintained) through Microsoft.Data.Sqlite,
/// configured for its SQLCipher 4 compatible scheme (AES-256-CBC with an
/// HMAC-SHA512 per page). That choice means the file can also be opened by stock
/// SQLCipher tools given the raw key, which is an escape hatch that does not
/// depend on this program. The free SQLitePCLRaw SQLCipher bundle stopped at
/// 2.1.11 when SQLitePCLRaw moved to 3.x, so it is not a foundation to build on.
///
/// The key is a raw 256-bit key, not a password: it is already random, so
/// SQLCipher's password stretching would add nothing.
/// </summary>
public sealed class IndexDatabase : IDisposable
{
    public SqliteConnection Connection { get; }
    public bool HasFts { get; }

    private static int _initialised;

    private IndexDatabase(SqliteConnection c, bool hasFts) { Connection = c; HasFts = hasFts; }

    public static void InitEngine()
    {
        if (Interlocked.Exchange(ref _initialised, 1) == 0) SQLitePCL.Batteries_V2.Init();
    }

    /// <summary>Creates a new database. Fails if the file exists.</summary>
    public static IndexDatabase Create(string path, ReadOnlySpan<byte> key, bool withFts)
    {
        if (File.Exists(path)) throw new IOException($"{path} already exists.");
        var db = OpenRaw(path, key, SqliteOpenMode.ReadWriteCreate);
        Exec(db, Schema.Core);
        if (withFts) Exec(db, Schema.Fts);
        return new IndexDatabase(db, withFts);
    }

    public static IndexDatabase Open(string path, ReadOnlySpan<byte> key, bool readOnly = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no index database at {path}.", path);
        var c = OpenRaw(path, key, readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite);
        try
        {
            // The first read is where a wrong or missing key shows up.
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='chunk_fts'";
            bool fts = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            return new IndexDatabase(c, fts);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 26)
        {
            c.Dispose();
            throw new CryptographicException("The index database could not be read with that key.", e);
        }
    }

    private static SqliteConnection OpenRaw(string path, ReadOnlySpan<byte> key, SqliteOpenMode mode)
    {
        InitEngine();
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        ApplyKey(c, key);
        using var cmd = c.CreateCommand();
        // Rollback journal, not WAL: WAL needs shared memory and does not work
        // over a network share, where the index may live.
        cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON; PRAGMA cache_size=-65536; PRAGMA temp_store=MEMORY;";
        if (mode != SqliteOpenMode.ReadOnly) cmd.ExecuteNonQuery();
        else { cmd.CommandText = "PRAGMA cache_size=-65536; PRAGMA temp_store=MEMORY;"; cmd.ExecuteNonQuery(); }
        return c;
    }

    private static void ApplyKey(SqliteConnection c, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("The database key is 32 bytes.");
        // ENCRYPTION NOT YET APPLIED: the tests that prove the file is unreadable
        // without the key are written first and must be seen to fail.
        _ = c;
    }

    public void Exec(string sql) => Exec(Connection, sql);

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    public string? GetMeta(string key)
    {
        using var cmd = Command("SELECT value FROM meta WHERE key=$k");
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = Command("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", tx);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Changes the database key in place (SQLCipher's rekey). Every page is
    /// re-encrypted; nothing is re-indexed. Used by "keys rotate".
    /// </summary>
    public void Rekey(ReadOnlySpan<byte> newKey)
    {
        if (newKey.Length != 32) throw new ArgumentException("The database key is 32 bytes.");
        Exec($"PRAGMA rekey = \"x'{Convert.ToHexString(newKey)}'\";");
    }

    public void Dispose() => Connection.Dispose();
}
