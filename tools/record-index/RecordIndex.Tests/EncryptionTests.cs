using System.Security.Cryptography;
using System.Text;
using JJFlex.RecordIndex.Crypto;
using JJFlex.RecordIndex.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JJFlex.RecordIndex.Tests;

/// <summary>
/// The database must be unreadable without its key. These were written first and
/// seen RED against the unencrypted database (see the #854 report), then the key
/// was applied.
/// </summary>
public class EncryptionTests
{
    private const string Marker = "zebracatapultmarmalade";

    private static (Sandbox S, byte[] Key) IndexedWithMarker()
    {
        var s = new Sandbox();
        s.Zip(@"memory\memory-20260101-010101.zip", ("feedback_marker.md", $"The secret word is {Marker}, and it must not be readable on disk."));
        s.Init();
        using (var ix = s.Home.Open(s.Unlock("recovery"), s.Prompt))
            ix.CatchUp(s.Prompt);
        // Recover the database key the way the tool does, for the wrong-key and no-key opens below.
        var (key, _) = Unlocker.Unlock(s.Home.LoadKeyring(), s.Unlock("recovery"), s.Prompt);
        return (s, key);
    }

    [Fact]
    public void The_file_does_not_start_with_the_SQLite_header()
    {
        var (s, _) = IndexedWithMarker();
        using (s)
        {
            var head = new byte[16];
            using (var f = File.OpenRead(s.Home.DatabasePath)) f.ReadExactly(head);
            Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(head));
        }
    }

    [Fact]
    public void An_indexed_word_appears_nowhere_in_the_bytes_on_disk()
    {
        var (s, _) = IndexedWithMarker();
        using (s)
        {
            // Positive control: the word IS in the index, so its absence on disk means something.
            using (var ix = s.Home.Open(s.Unlock("recovery"), s.Prompt, readOnly: true))
                Assert.Single(JJFlex.RecordIndex.Search.Searcher.Search(ix.Db, new() { Text = Marker }).Hits);

            var bytes = File.ReadAllBytes(s.Home.DatabasePath);
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Marker)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("feedback_marker")));
        }
    }

    [Fact]
    public void Opening_with_no_key_fails()
    {
        var (s, _) = IndexedWithMarker();
        using (s)
        {
            IndexDatabase.InitEngine();
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = s.Home.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master";
            var e = Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
            Assert.Equal(26, e.SqliteErrorCode); // SQLITE_NOTADB
        }
    }

    [Fact]
    public void Opening_with_the_wrong_key_fails()
    {
        var (s, key) = IndexedWithMarker();
        using (s)
        {
            var wrong = (byte[])key.Clone();
            wrong[0] ^= 1;
            Assert.Throws<CryptographicException>(() => IndexDatabase.Open(s.Home.DatabasePath, wrong).Dispose());
            using var right = IndexDatabase.Open(s.Home.DatabasePath, key);
            Assert.NotNull(right.GetMeta("database_id"));
        }
    }
}
