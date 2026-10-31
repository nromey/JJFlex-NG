namespace JJFlex.RecordIndex.Indexing;

/// <summary>
/// Reads a stream line by line as raw bytes, reporting each line's byte offset,
/// so a search hit can say exactly where in a member it sits. StreamReader cannot
/// do this: it hides how many bytes it consumed.
///
/// A line's bytes are valid only until the next call. Very long lines (a session
/// transcript can hold a multi-megabyte tool result on one line) are returned
/// whole; the buffer grows to fit.
/// </summary>
public sealed class ByteLineReader
{
    private readonly Stream _s;
    private byte[] _buf = new byte[1 << 20];
    private int _start, _end;
    private long _offsetOfStart;
    private bool _eof;

    public ByteLineReader(Stream s) => _s = s;

    /// <summary>Total bytes consumed so far (the offset of the next line).</summary>
    public long Position => _offsetOfStart;

    /// <summary>
    /// Returns the next line without its line ending, its byte offset, and its raw
    /// length including the line ending. False at the end of the stream.
    /// </summary>
    public bool Next(out ReadOnlySpan<byte> line, out long offset, out int rawLength)
    {
        int scanFrom = _start;
        while (true)
        {
            int nl = Array.IndexOf(_buf, (byte)'\n', scanFrom, _end - scanFrom);
            if (nl >= 0)
            {
                offset = _offsetOfStart;
                rawLength = nl - _start + 1;
                int len = nl - _start;
                if (len > 0 && _buf[nl - 1] == (byte)'\r') len--;
                line = new ReadOnlySpan<byte>(_buf, _start, len);
                _start = nl + 1;
                _offsetOfStart += rawLength;
                return true;
            }
            if (_eof)
            {
                if (_end > _start)
                {
                    offset = _offsetOfStart;
                    rawLength = _end - _start;
                    int len = rawLength;
                    if (_buf[_start + len - 1] == (byte)'\r') len--;
                    line = new ReadOnlySpan<byte>(_buf, _start, len);
                    _offsetOfStart += rawLength;
                    _start = _end;
                    return true;
                }
                line = default; offset = _offsetOfStart; rawLength = 0;
                return false;
            }
            scanFrom = _end;
            Fill(ref scanFrom);
        }
    }

    private void Fill(ref int scanFrom)
    {
        if (_start > 0)
        {
            int keep = _end - _start;
            Buffer.BlockCopy(_buf, _start, _buf, 0, keep);
            scanFrom -= _start;
            _end = keep;
            _start = 0;
        }
        if (_end == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
        int n = _s.Read(_buf, _end, _buf.Length - _end);
        if (n == 0) _eof = true;
        else _end += n;
    }
}
