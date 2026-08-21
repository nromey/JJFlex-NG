using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace JJTrace
{
    /// <summary>
    /// The live trace file's listener. Owns the FileStream/StreamWriter itself
    /// (rather than delegating to TextWriterTraceListener) for two reasons that
    /// both came out of the 2026-08-07 marathon session that grew the ACTIVE
    /// JJFlexRadioTrace.txt to 11.7 GB:
    ///
    /// 1. **Size-based rotation with no lost lines.** Rotation happens *inside*
    ///    this listener's own lock, so the file swap is atomic from every
    ///    writer's point of view — including code that calls
    ///    <c>System.Diagnostics.Trace.WriteLine</c> directly and never goes
    ///    through <see cref="Tracing.TraceLine(string)"/> (JJFlexWpf does this
    ///    in dozens of places). The alternative — remove listener, rename,
    ///    add new listener — leaves a window where Trace has zero listeners
    ///    and lines silently evaporate.
    ///
    /// 2. **The live trace is readable while it is being written.**
    ///    <c>File.Create(path)</c> opens with <c>FileShare.None</c>, which
    ///    means nothing — not Notepad, not a screen reader, not the crash
    ///    bundler — can read the trace of the session that is currently
    ///    running. That is part of why the day's crash bundle shipped with no
    ///    session trace in it. This opens <c>FileShare.ReadWrite</c>.
    ///
    /// Locking discipline: this listener's <c>_sync</c> is the innermost lock.
    /// It never calls back into <see cref="Tracing"/> tracing methods while
    /// held; the part-closed callback is required to be non-blocking (it just
    /// queues background compression). System.Diagnostics.Trace's own global
    /// lock is always taken *before* this one (Trace.WriteLine → listener),
    /// never after, so there is no lock-order inversion.
    /// </summary>
    internal sealed class RotatingTraceListener : TraceListener
    {
        private readonly object _sync = new object();

        private FileStream _stream;
        private StreamWriter _writer;
        private bool _closed;

        /// <summary>Bytes written into the currently open part.</summary>
        private long _bytesInPart;

        /// <summary>
        /// Byte count at which the next rotation attempt fires. Normally equal
        /// to the threshold; pushed out by one further threshold after a failed
        /// rotation so a persistent failure (file locked by another process,
        /// disk full) can't turn into a rotate-fail storm on every write.
        /// </summary>
        private long _nextRotateAt;

        /// <summary>1-based number of the part currently being written.</summary>
        private int _partNumber = 1;

        /// <summary>
        /// Resolves the plain-text path a closed part should be renamed to.
        /// Returning null or empty disables rotation for that attempt.
        /// </summary>
        private readonly Func<int, string> _resolvePartPath;

        /// <summary>
        /// Called (still holding <c>_sync</c>) once a part file has been closed
        /// and renamed. MUST be non-blocking — it queues compression, it does
        /// not perform it.
        /// </summary>
        private readonly Action<string, int> _onPartClosed;

        public string FilePath { get; private set; }

        public long RotationThresholdBytes { get; set; }

        public RotatingTraceListener(string path,
                                     long rotationThresholdBytes,
                                     Func<int, string> resolvePartPath,
                                     Action<string, int> onPartClosed)
        {
            FilePath = path;
            RotationThresholdBytes = rotationThresholdBytes;
            _resolvePartPath = resolvePartPath;
            _onPartClosed = onPartClosed;
            _nextRotateAt = rotationThresholdBytes;
            Open(path, append: false);
        }

        /// <summary>Bytes written into the part currently open.</summary>
        public long BytesInCurrentPart
        {
            get { lock (_sync) { return _bytesInPart; } }
        }

        /// <summary>1-based number of the part currently being written.</summary>
        public int PartNumber
        {
            get { lock (_sync) { return _partNumber; } }
        }

        /// <summary>True once at least one rotation has happened this session.</summary>
        public bool HasRotated
        {
            get { lock (_sync) { return _partNumber > 1; } }
        }

        /// <summary>
        /// Last rotation failure text, or null. Surfaced by Tracing so the
        /// failure is visible in the trace instead of being swallowed — errors
        /// never suppress-key.
        /// </summary>
        public string LastRotationError { get; private set; }

        private void Open(string path, bool append)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _stream = new FileStream(path,
                                     append ? FileMode.Append : FileMode.Create,
                                     FileAccess.Write,
                                     FileShare.ReadWrite | FileShare.Delete);
            // UTF8 without BOM: testers open these in Notepad and pipe them
            // through screen readers; a BOM in the middle of a part chain reads
            // as garbage characters.
            _writer = new StreamWriter(_stream, new UTF8Encoding(false));
            _writer.AutoFlush = false; // Trace.AutoFlush drives Flush() explicitly.
            _closed = false;
            _bytesInPart = append ? SafeLength(path) : 0;
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        public override void Write(string message)
        {
            if (message == null) return;
            lock (_sync)
            {
                if (_closed) return;
                try
                {
                    if (NeedIndent) WriteIndent();
                    _writer.Write(message);
                    // Byte estimate: trace content is effectively ASCII, so one
                    // char is one byte. An estimate is fine — the threshold is a
                    // policy number, not an invariant, and this runs on every
                    // single trace line so a FileInfo syscall per write is out
                    // of the question.
                    _bytesInPart += message.Length;
                }
                catch
                {
                    // A write failure means the file is gone / disk full. Close
                    // rather than throw from a trace call — tracing must never
                    // be the thing that takes the app down.
                    CloseInternal();
                    return;
                }

                if (RotationThresholdBytes > 0 && _bytesInPart >= _nextRotateAt)
                {
                    RotateInternal();
                }
            }
        }

        public override void WriteLine(string message)
        {
            // ── The vendor frame-gap firehose, coalesced ────────────────────
            //
            // FlexLib's Panadapter.cs calls Debug.WriteLine("Expected frame N
            // but got frame M") on every dropped FFT frame — unconditional, no
            // trace level, and FlexLib_API is vendored so the call site cannot
            // be touched. Measured 2026-08-21 (task #170): 66,653 of the
            // 71,600 lines in one 22-minute Info-level session were this line —
            // 96% of the standing log, at Info, where the meter stream never
            // even fires. This listener is the one chokepoint we own that every
            // such line passes through, so the coalescing lives here: one
            // PanFrameGaps summary per second at most, carrying the count, the
            // span, and the last raw line. The raw text is kept inside the
            // summary on purpose — a grep for "Expected frame" still finds the
            // evidence, it just finds one line instead of six hundred.
            if (message != null
                && message.StartsWith("Expected frame ", StringComparison.Ordinal)
                && message.Contains("but got frame", StringComparison.Ordinal))
            {
                string summary = CoalesceFrameGap(message);
                if (summary != null) Write(summary + Environment.NewLine);
                return;
            }

            Write(message + Environment.NewLine);
        }

        // Frame-gap coalescing state. Guarded by its own lock, taken only in
        // WriteLine and always BEFORE _sync (via Write) — never the other way —
        // so the listener's lock discipline is unchanged.
        private readonly object _gapSync = new object();
        private int _gapCount;
        private int _gapWindowStart;

        /// <summary>
        /// Fold one vendor dropped-frame line into the running window. Returns
        /// the summary to write when the window is at least a second old,
        /// otherwise null. Emission is driven by arrival, like the meter
        /// stream's: a burst emits once a second, and after a quiet stretch the
        /// next gap line flushes the old window with its true span — so the
        /// count is never lost, only the exact timing inside the window.
        /// </summary>
        private string CoalesceFrameGap(string rawLine)
        {
            const int WindowMs = 1000;
            lock (_gapSync)
            {
                if (_gapCount == 0) _gapWindowStart = Environment.TickCount;
                _gapCount++;

                int elapsed = Environment.TickCount - _gapWindowStart;
                if (elapsed < WindowMs) return null;

                string summary = "PanFrameGaps: n=" + _gapCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " over " + (elapsed / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "s last=\"" + rawLine + "\"";
                _gapCount = 0;
                return summary;
            }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                if (_closed) return;
                try { _writer.Flush(); }
                catch { CloseInternal(); }
            }
        }

        public override void Close()
        {
            lock (_sync) { CloseInternal(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }

        private void CloseInternal()
        {
            if (_closed) return;
            _closed = true;
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            _writer = null;
            _stream = null;
        }

        /// <summary>
        /// Close the current part, rename it out of the way, and reopen a fresh
        /// file at the same live path. Caller must hold <c>_sync</c>.
        ///
        /// There is deliberately no public "rotate now" — the clean-exit path
        /// archives its final segment directly rather than rotating, because
        /// rotating at exit would leave behind a freshly created empty live
        /// trace that next boot would read as evidence of a killed session.
        /// </summary>
        private string RotateInternal()
        {
            string partPath = null;
            try
            {
                partPath = _resolvePartPath?.Invoke(_partNumber);
            }
            catch (Exception ex)
            {
                LastRotationError = "part path: " + ex.Message;
                partPath = null;
            }

            if (string.IsNullOrEmpty(partPath))
            {
                // Can't name the part — push the next attempt out so we don't
                // re-try on every subsequent line.
                _nextRotateAt = _bytesInPart + Math.Max(RotationThresholdBytes, 1);
                return null;
            }

            int closedPart = _partNumber;
            try
            {
                CloseInternal();
                File.Move(FilePath, partPath);
                Open(FilePath, append: false);
                _partNumber = closedPart + 1;
                _nextRotateAt = RotationThresholdBytes;
                LastRotationError = null;

                // The breadcrumb that makes a chain of parts readable as one
                // session. Written directly to the fresh writer (not through
                // Trace) because we are inside the listener's own lock.
                string header = string.Format(
                    "--- trace continues from part {0:D3} ({1}) — this is part {2:D3} ---",
                    closedPart, Path.GetFileName(partPath), _partNumber);
                _writer.Write(header + Environment.NewLine);
                _writer.Flush();
                _bytesInPart = header.Length + Environment.NewLine.Length;
            }
            catch (Exception ex)
            {
                LastRotationError = ex.Message;
                // Recover: get *some* writable trace file back so the session
                // keeps tracing. Append to whichever of the two paths exists.
                try
                {
                    Open(File.Exists(FilePath) ? FilePath : partPath, append: true);
                    FilePath = File.Exists(FilePath) ? FilePath : partPath;
                }
                catch { /* tracing is down; nothing further we can safely do */ }
                _nextRotateAt = _bytesInPart + Math.Max(RotationThresholdBytes, 1);
                return null;
            }

            try { _onPartClosed?.Invoke(partPath, closedPart); }
            catch { /* queueing must never break the writer */ }

            return partPath;
        }
    }
}
