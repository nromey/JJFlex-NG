using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JJFlex.RecordIndex.Store;

namespace JJFlex.RecordIndex.Indexing;

public sealed record IndexResult(int Members, int TextMembers, int ReusedBodies, long NewChunks, long ReusedChunks, long NewChunkChars, long BytesRead);

/// <summary>
/// Reads one archive of the record, strictly read-only, and writes its index.
/// A zip is opened in ZipArchiveMode.Read on a FileStream opened FileAccess.Read
/// with FileShare.Read: nothing here can modify, move, re-zip or delete it.
/// </summary>
public static partial class ArchiveIndexer
{
    /// <summary>Text members larger than this are listed but not read.</summary>
    public const long MaxTextMember = 3L * 1024 * 1024 * 1024;

    public static ArchiveInfo Describe(string recordRoot, string fullPath)
    {
        var fi = new FileInfo(fullPath);
        var rel = Path.GetRelativePath(recordRoot, fi.FullName);
        return new ArchiveInfo(fi.FullName, rel, MemberClassifier.ArchiveKind(rel), DateFromName(fi.Name) ?? fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
            fi.Length, fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [GeneratedRegex(@"(?<!\d)(20\d{2})(\d{2})(\d{2})[-_](\d{2})(\d{2})(\d{2})?(?!\d)")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"(?<!\d)(20\d{2})-?(\d{2})-?(\d{2})(?!\d)")]
    private static partial Regex DateOnly();

    internal static string? DateFromName(string name)
    {
        var m = Stamp().Match(name);
        if (m.Success) return $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value} {m.Groups[4].Value}:{m.Groups[5].Value}";
        m = DateOnly().Match(name);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd");
        return null;
    }

    public static IndexResult Index(IndexWriter w, ArchiveInfo a)
    {
        long archiveId = w.AddArchive(a);
        int members = 0, textMembers = 0, reused = 0;
        long bytesRead = 0;
        using var fs = new FileStream(a.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

        if (a.FullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
                members++;
                var path = e.FullName.Replace('\\', '/');
                var (kind, how, note) = MemberClassifier.Classify(a.Kind, path);
                if (how == Treatment.Skip || e.Length > MaxTextMember)
                {
                    w.AddMember(archiveId, path, e.Crc32, e.Length, e.LastWriteTime, kind, null, note ?? "too large to index");
                    continue;
                }
                if (w.FindBySignature(path, e.Crc32, e.Length) is long known)
                {
                    reused++; textMembers++;
                    w.AddMember(archiveId, path, e.Crc32, e.Length, e.LastWriteTime, kind, known, null);
                    continue;
                }
                using var s = e.Open();
                var (body, why) = IndexStream(w, s, how, e.Length);
                bytesRead += e.Length;
                if (body is not null) textMembers++;
                w.AddMember(archiveId, path, e.Crc32, e.Length, e.LastWriteTime, body is null ? "binary" : kind, body, why);
            }
        }
        else
        {
            members = 1;
            var name = Path.GetFileName(a.FullPath);
            var (kind, how, note) = MemberClassifier.Classify(a.Kind, name);
            var mtime = new DateTimeOffset(File.GetLastWriteTime(a.FullPath));
            if (how == Treatment.Skip || a.Size > MaxTextMember)
            {
                w.AddMember(archiveId, name, null, a.Size, mtime, kind, null, note ?? "too large to index");
            }
            else
            {
                var (body, why) = IndexStream(w, fs, how, a.Size);
                bytesRead += a.Size;
                if (body is not null) textMembers++;
                w.AddMember(archiveId, name, null, a.Size, mtime, body is null ? "binary" : kind, body, why);
            }
        }
        w.FinishArchive(archiveId, members, textMembers);
        return new IndexResult(members, textMembers, reused, w.NewChunks, w.ReusedChunks, w.NewChunkTextChars, bytesRead);
    }

    /// <summary>
    /// Reads one member's bytes once: hashing them, splitting lines, extracting
    /// text and chunking, all in a single pass, so a gigabyte transcript is never
    /// held in memory.
    /// </summary>
    public static (long? BodyId, string? Note) IndexStream(IndexWriter w, Stream source, Treatment how, long size)
    {
        var head = new byte[8192];
        int n = ReadUpTo(source, head);
        var headSpan = head.AsSpan(0, n);
        if (how == Treatment.Sniff && !MemberClassifier.LooksLikeText(headSpan)) return (null, "binary (by content)");
        if (n == 0 && size > 0) return (null, "could not be read");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool utf16 = n >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF));
        Stream stream = new HashingStream(new PrefixedStream(head, n, source), hash);
        if (utf16)
        {
            // Rare in the record (some PowerShell output). Re-encode to UTF-8 so the
            // line reader works; byte offsets then count UTF-8 bytes, and the
            // extract command reads the member the same way, so they still agree.
            var enc = head[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            using var reader = new StreamReader(stream, enc, detectEncodingFromByteOrderMarks: true);
            stream = new MemoryStream(Encoding.UTF8.GetBytes(reader.ReadToEnd()));
        }

        var refs = new List<BodyChunkRef>();
        var chunker = new Chunker(c => refs.Add(new BodyChunkRef(w.GetOrAddChunk(c), c.LineStart, c.ByteOffset)));
        var lines = new ByteLineReader(stream);
        long lineNo = 0;
        while (lines.Next(out var line, out var offset, out var raw))
        {
            lineNo++;
            if (lineNo == 1 && line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF) line = line[3..];
            string text; string? ts = null;
            switch (how)
            {
                case Treatment.Jsonl: (text, ts) = LineText.Jsonl(line); break;
                case Treatment.Html: text = LineText.Html(line); break;
                default: text = LineText.Plain(line); break;
            }
            chunker.AddLine(lineNo, offset, raw, text, ts);
        }
        chunker.Flush();
        if (utf16) stream.Dispose();
        var sha = hash.GetHashAndReset();
        return (w.AddBody(sha, size, refs), null);
    }

    private static int ReadUpTo(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int r = s.Read(buf, total, buf.Length - total);
            if (r == 0) break;
            total += r;
        }
        return total;
    }
}

/// <summary>Replays bytes already read, then continues with the stream.</summary>
internal sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream rest) : Stream
{
    private int _pos;
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos < prefixLength)
        {
            int n = Math.Min(count, prefixLength - _pos);
            Buffer.BlockCopy(prefix, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        return rest.Read(buffer, offset, count);
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Hashes everything read through it.</summary>
internal sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        if (n > 0) hash.AppendData(buffer, offset, n);
        return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
