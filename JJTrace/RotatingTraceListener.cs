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

        /// <summary>
        /// <see cref="_closed"/> as a reader on another thread may see it
        /// WITHOUT taking <c>_sync</c>: set in the same step as every change
        /// of <see cref="_closed"/>, except inside a rotation, whose close and
        /// reopen are one operation to anyone outside this lock — a rotation
        /// publishes its end state, never its middle. See
        /// <see cref="ClosedWithoutWaiting"/>.
        /// </summary>
        private volatile bool _closedForReaders;

        /// <summary>Caller holds <c>_sync</c>. True while
        /// <see cref="RotateInternal"/> runs.</summary>
        private bool _rotating;

        /// <summary>Caller holds <c>_sync</c>. The one way <see cref="_closed"/>
        /// changes, so the lock-free view cannot fall behind it.</summary>
        private void SetClosed(bool closed)
        {
            _closed = closed;
            if (!_rotating) _closedForReaders = closed;
        }

        /// <summary>
        /// Immutable identity of the session whose parts this sink produces.
        ///
        /// <para><b>Carried rather than looked up, and that is a correctness
        /// fix, not tidiness.</b> Rotation names a part and queues its
        /// compression from inside this listener's own lock. Both used to read
        /// the process-global current session from there — so a rotation racing
        /// a lifecycle transition could stamp a part with the WRONG session's
        /// boot time, and a lock wrapped around listener closure would have
        /// closed on the coordinator gate from inside sink synchronisation,
        /// which is the inversion. Nothing below this line reaches back up.</para>
        /// </summary>
        internal TraceSinkStamp Stamp { get; }

        /// <summary>
        /// Suppress size rotation. Set while a terminal record is written so
        /// the last line of a sealed file cannot land in a part nobody is
        /// expecting, and the final part number stays the one that was frozen.
        /// </summary>
        private bool _rotationSuppressed;

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
                                     Action<string, int> onPartClosed,
                                     TraceSinkStamp stamp = null,
                                     int startPartNumber = 1,
                                     bool append = false)
        {
            FilePath = path;
            RotationThresholdBytes = rotationThresholdBytes;
            _resolvePartPath = resolvePartPath;
            _onPartClosed = onPartClosed;
            Stamp = stamp;
            _partNumber = startPartNumber < 1 ? 1 : startPartNumber;
            _startPartNumber = _partNumber;
            _nextRotateAt = rotationThresholdBytes;
            // Append exists for exactly one caller: a checkpoint whose detach
            // failed and has to get the session writing again. Opening that
            // path with FileMode.Create would truncate the very bytes the move
            // could not take away — destroying the evidence in the course of
            // failing to preserve it.
            Open(path, append);
        }

        /// <summary>The part number this sink opened at. A sink that opens at
        /// part 4 (a bundler checkpoint continued the session) has rotated in
        /// the session's terms even though it has not rotated in its own.</summary>
        private readonly int _startPartNumber;

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

        /// <summary>True once THIS sink has rotated, ignoring parts inherited
        /// from an earlier sink of the same session.</summary>
        public bool RotatedHere
        {
            get { lock (_sync) { return _partNumber > _startPartNumber; } }
        }

        /// <summary>True when the file is closed and no further write lands.</summary>
        public bool IsClosed
        {
            get { lock (_sync) { return _closed; } }
        }

        /// <summary>
        /// True once this sink has closed — over a write fault, a flush fault
        /// or an ordinary close — read WITHOUT taking this sink's lock, so it
        /// never waits on a write stalled on the disk. Set at the moment the
        /// sink closes, which for a write fault is inside the failing write:
        /// before the coordinator has noticed the fault, and before the
        /// queued retirement publishes <c>Failed</c> to the health model
        /// (Sol's review of H11). A rotation's momentary close is not
        /// published; its end state is.
        /// </summary>
        internal bool ClosedWithoutWaiting => _closedForReaders;

        /// <summary>
        /// The first write or flush failure this sink hit, or null while every
        /// byte handed to it has reached the stream. Latched: it is never
        /// cleared, because a sink that failed once has closed itself and a
        /// later "closed cleanly" must not be able to erase what happened.
        ///
        /// <para><b>Why it exists.</b> A write failure used to be swallowed
        /// into <see cref="CloseInternal"/>, and a later
        /// <see cref="FlushAndClose"/> on the already-closed sink answered
        /// true. So a session whose terminal records never reached the disk
        /// could report itself closed cleanly, and nothing downstream — the
        /// ticket, the operator's status — could know the tail was missing
        /// (Astra's ruling on the pending-record failure, implementation note
        /// 3: "Do not erase an earlier write failure when closing an
        /// already-closed sink").</para>
        /// </summary>
        public string WriteFault { get; private set; }

        /// <summary>True once any write or flush has failed on this sink.</summary>
        public bool Faulted
        {
            get { lock (_sync) { return WriteFault != null; } }
        }

        /// <summary>
        /// What this sink knows it has put in its file: which kinds of meter
        /// reading were written and flushed, and what a fault took. Kept
        /// here because nothing else sees every line; read by the seal and
        /// frozen onto its result (Sol's review of H9, blocker 2).
        /// </summary>
        private readonly TraceFileTally _tally = new TraceFileTally();

        /// <summary>A snapshot of <see cref="_tally"/>, under the lock.</summary>
        public TraceFileFacts Facts
        {
            get { lock (_sync) { return _tally.Snapshot(); } }
        }

        /// <summary>Caller holds <c>_sync</c>. Latch the first failure only.</summary>
        private void Fault(Exception ex)
        {
            if (WriteFault == null) WriteFault = ex == null ? "unknown write failure" : ex.Message;
        }

        /// <summary>
        /// Write one line straight into this sink, bypassing
        /// <c>System.Diagnostics.Trace</c> entirely.
        ///
        /// <para>The boundary uses this for terminal records. Going through
        /// <c>Trace.WriteLine</c> would take the framework's global trace lock
        /// while the coordinator gate is held, which is the one lock order the
        /// design forbids — and it would fan the line out to every other
        /// listener and to whatever sink is current, which for a terminal
        /// record is precisely the wrong file.</para>
        ///
        /// <para>Rotation is suppressed for the duration: a terminal record is
        /// finite and must land in the part whose number was just frozen.</para>
        ///
        /// <para><b>It says whether the line landed.</b> Written AND flushed to
        /// the stream, or false — with the failure latched in
        /// <see cref="WriteFault"/> and the sink closed. It used to return
        /// nothing and close quietly, which let a seal report its terminal
        /// records as written when they were not.</para>
        /// </summary>
        /// <returns>True when the line was written and flushed; false when the
        /// sink was already closed or the write failed.</returns>
        public bool WriteTerminalLine(string line)
        {
            if (line == null) return false;
            lock (_sync)
            {
                if (_closed) { _tally.Refused(line); return false; }
                _rotationSuppressed = true;
                bool inBuffer = false;
                try
                {
                    _writer.Write(line);
                    _writer.Write(Environment.NewLine);
                    _bytesInPart += line.Length + Environment.NewLine.Length;
                    _tally.Wrote(line);
                    inBuffer = true;
                    _writer.Flush();
                    _tally.Flushed();
                    return true;
                }
                catch (Exception ex)
                {
                    _tally.FaultedOn(line, failingAlreadyCounted: inBuffer);
                    Fault(ex);
                    CloseInternal();
                    return false;
                }
                finally
                {
                    _rotationSuppressed = false;
                }
            }
        }

        /// <summary>
        /// Flush and close this sink, reporting whether the bytes really landed.
        /// Called by the coordinator under the gate, on the sink it owns —
        /// never through the process-wide <c>Trace.Close</c>, which would close
        /// somebody else's session too.
        ///
        /// <para><b>A sink that already closed itself over a failure reports
        /// that failure here</b>, not success. The old answer for "already
        /// closed" was true unconditionally, so a terminal write that failed
        /// and closed the sink was followed by a close that said everything
        /// was fine.</para>
        /// </summary>
        public bool FlushAndClose(out string failure)
        {
            failure = null;
            lock (_sync)
            {
                if (_closed)
                {
                    failure = WriteFault;
                    return WriteFault == null;
                }
                bool ok = true;
                try { _writer?.Flush(); _tally.Flushed(); }
                catch (Exception ex) { ok = false; failure = ex.Message; _tally.FaultedOn(null, failingAlreadyCounted: false); Fault(ex); }
                try { _writer?.Dispose(); }
                catch (Exception ex) { ok = false; failure = failure ?? ex.Message; Fault(ex); }
                try { _stream?.Dispose(); }
                catch (Exception ex) { ok = false; failure = failure ?? ex.Message; Fault(ex); }
                _writer = null;
                _stream = null;
                SetClosed(true);
                return ok;
            }
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
            SetClosed(false);
            _bytesInPart = append ? SafeLength(path) : 0;
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        // ── Per-thread write cost, for the split stopwatch (#434) ───────────
        //
        // Thread-static rather than a field: the listener is called on the same
        // thread that called Trace.WriteLine, so this is exact and needs no
        // synchronisation, and it stays correct while another thread is blocked
        // on the global trace lock. Tracing.Emit opens a window around the
        // dispatch and reads it back, which separates "our file listener was
        // slow" from "something else in the listener set was slow" — the whole
        // question #434 could not answer while it was happening.
        [ThreadStatic] private static long _threadWriteTicks;

        /// <summary>Start accounting this thread's time inside this listener.</summary>
        internal static void BeginThreadCostWindow() { _threadWriteTicks = 0; }

        /// <summary>
        /// Stopwatch ticks this thread spent inside this listener since
        /// <see cref="BeginThreadCostWindow"/>. Closes the window.
        /// </summary>
        internal static long EndThreadCostWindow()
        {
            long t = _threadWriteTicks;
            _threadWriteTicks = 0;
            return t;
        }

        /// <summary>
        /// Fold one measured span into this thread's window. A static helper so
        /// the instance methods that measure themselves are not writing static
        /// state directly — the accumulator is per THREAD, not per listener, and
        /// this keeps that visible at every call site.
        /// </summary>
        private static void AddThreadCost(long enteredStamp)
        {
            _threadWriteTicks += Stopwatch.GetTimestamp() - enteredStamp;
        }

        public override void Write(string message)
        {
            if (message == null) return;
            long entered = Stopwatch.GetTimestamp();
            try
            {
                WriteCore(message);
            }
            finally
            {
                AddThreadCost(entered);
            }
        }

        private void WriteCore(string message)
        {
            lock (_sync)
            {
                if (_closed) { _tally.Refused(message); return; }
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
                    _tally.Wrote(message);
                }
                catch (Exception ex)
                {
                    // A write failure means the file is gone / disk full. Close
                    // rather than throw from a trace call — tracing must never
                    // be the thing that takes the app down. But LATCH it: the
                    // coordinator reads WriteFault after the write and tells
                    // the operator the log has stopped, which a silent close
                    // never did.
                    _tally.FaultedOn(message, failingAlreadyCounted: false);
                    Fault(ex);
                    CloseInternal();
                    return;
                }

                if (!_rotationSuppressed && RotationThresholdBytes > 0 && _bytesInPart >= _nextRotateAt)
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
            // Counted into the same per-thread window as Write: Trace.AutoFlush
            // is on, so the flush happens inside the same dispatch and is where
            // the real file I/O lands. Leaving it out would understate our own
            // cost and misattribute it to the rest of the listener set.
            long entered = Stopwatch.GetTimestamp();
            try
            {
                lock (_sync)
                {
                    if (_closed) return;
                    try { _writer.Flush(); _tally.Flushed(); }
                    catch (Exception ex) { _tally.FaultedOn(null, failingAlreadyCounted: false); Fault(ex); CloseInternal(); }
                }
            }
            finally
            {
                AddThreadCost(entered);
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
            SetClosed(true);
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
            _rotating = true;
            try { return RotateCore(); }
            finally
            {
                _rotating = false;
                _closedForReaders = _closed;
            }
        }

        /// <summary>Caller holds <c>_sync</c>, inside <see cref="RotateInternal"/>.</summary>
        private string RotateCore()
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
                _tally.PartRotated();

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
