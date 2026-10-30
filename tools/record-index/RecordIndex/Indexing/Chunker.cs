using System.Security.Cryptography;
using System.Text;

namespace JJFlex.RecordIndex.Indexing;

/// <summary>One searchable piece of a member.</summary>
public sealed record ChunkData(string Text, byte[] Map, long LineStart, long ByteOffset, string? FirstTimestamp)
{
    /// <summary>Identity: SHA-256 over the map and the text, so equal chunks are stored once.</summary>
    public byte[] Sha256()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(BitConverter.GetBytes(Map.Length));
        h.AppendData(Map);
        h.AppendData(Encoding.UTF8.GetBytes(Text));
        return h.GetHashAndReset();
    }
}

/// <summary>
/// Cuts a member's text into chunks of about <see cref="Target"/> characters at
/// line boundaries. Deterministic from the start of the member, which is what makes
/// a grown transcript reuse every chunk but the last from its previous snapshot.
/// A line longer than a chunk is split into fixed windows.
///
/// Each chunk carries a line map: for every piece of a source line in it, the
/// offset where its text starts, how many source lines it advances, and (on a
/// line's first piece in the chunk) that line's raw byte length. From that, a
/// position in the chunk's text resolves to an exact line number and byte offset
/// in the member.
/// </summary>
public sealed class Chunker
{
    public const int Target = 8192;

    private readonly Action<ChunkData> _emit;
    private readonly StringBuilder _text = new();
    private readonly MemoryStream _map = new();
    private bool _any;
    private long _lineStart, _byteOffset, _prevLine;
    private int _prevTextStart;
    private string? _firstTs;

    public Chunker(Action<ChunkData> emit) => _emit = emit;

    public void AddLine(long lineNo, long offset, int rawLength, string text, string? timestamp)
    {
        bool firstPieceOfLineInChunk = true;
        int at = 0;
        do
        {
            int take = Math.Min(Target, text.Length - at);
            if (take > 0 && at + take < text.Length && char.IsHighSurrogate(text[at + take - 1])) take--;
            var piece = text.AsSpan(at, take);

            if (_any && _text.Length + 1 + piece.Length > Target)
            {
                Flush();
                firstPieceOfLineInChunk = true;
            }
            if (!_any)
            {
                _any = true;
                _lineStart = lineNo;
                _byteOffset = offset;
                _prevLine = lineNo;
                _prevTextStart = 0;
                _firstTs = null;
            }
            else
            {
                _text.Append('\n');
            }
            int textStart = _text.Length;
            _text.Append(piece);
            _firstTs ??= timestamp;

            WriteVarint(_map, (ulong)(textStart - _prevTextStart));
            WriteVarint(_map, (ulong)(lineNo - _prevLine));
            WriteVarint(_map, firstPieceOfLineInChunk ? (ulong)rawLength : 0);
            _prevTextStart = textStart;
            _prevLine = lineNo;
            firstPieceOfLineInChunk = false;
            at += take;
        }
        while (at < text.Length);
    }

    public void Flush()
    {
        if (!_any) return;
        _emit(new ChunkData(_text.ToString(), _map.ToArray(), _lineStart, _byteOffset, _firstTs));
        _text.Clear();
        _map.SetLength(0);
        _any = false;
    }

    internal static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    internal static ulong ReadVarint(ReadOnlySpan<byte> b, ref int i)
    {
        ulong v = 0; int shift = 0;
        while (true)
        {
            byte x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if (x < 0x80) return v;
            shift += 7;
        }
    }

    /// <summary>
    /// Resolves a character position in a chunk's text to (lines after the chunk's
    /// first line, bytes after the chunk's byte offset) for the line containing it.
    /// </summary>
    public static (long LineDelta, long ByteDelta) Locate(byte[] map, int position)
    {
        int i = 0;
        long line = 0, bytes = 0, lastRaw = 0;
        int textStart = 0;
        bool first = true;
        long foundLine = 0, foundBytes = 0;
        while (i < map.Length)
        {
            int ts = textStart + (int)ReadVarint(map, ref i);
            long ld = (long)ReadVarint(map, ref i);
            long raw = (long)ReadVarint(map, ref i);
            if (!first && ts > position) break;
            if (ld > 0) { bytes += lastRaw; line += ld; lastRaw = 0; }
            if (raw > 0) lastRaw = raw;
            textStart = ts;
            foundLine = line; foundBytes = bytes;
            first = false;
        }
        return (foundLine, foundBytes);
    }
}
