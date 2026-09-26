using System;
using System.Globalization;

namespace JJTrace
{
    /// <summary>
    /// The two kinds of meter evidence a trace file can hold, recognised by
    /// the sink as each line goes through it.
    ///
    /// <para><b>Why the sink classifies, and why the markers live here.</b>
    /// The operator's drop window promises what the archived file contains —
    /// "including the last readings the radio sent: forward power, reflected
    /// power and the temperature of the amplifier" — and until Track H10 that
    /// promise was made on no evidence at all: <c>txMeters:</c> lines exist
    /// only while transmitting or tuning, and a temperature window can render
    /// <c>paTemp none n=0</c> (Sol's review of H9, blocker 2). The only thing
    /// that knows what reached a file is the sink that wrote it, so the sink
    /// keeps the tally, and the markers it recognises are pinned here rather
    /// than installed from the application: a classifier that has to be
    /// installed can be installed late, and the boot trace opens before the
    /// application has installed anything. <c>Radios.Tests</c> pins these
    /// markers against the constants in <c>Radios.CaptureMeterSet</c> and
    /// against a really rendered line, so a rename there turns a test red
    /// rather than silently making every file read as empty.</para>
    /// </summary>
    public static class TraceLineKinds
    {
        /// <summary>A forward and reflected power reading: the transmit
        /// meter line <c>Radios.FlexBase.traceTxMeters</c> writes, one a
        /// second while transmitting, four a second while tuning.</summary>
        public const string Power = "power";

        /// <summary>An amplifier temperature window with at least one sample
        /// in it: the <c>captureMeters:</c> line <c>Radios.CaptureMeterSet</c>
        /// renders. A window with <c>n=0</c> is not a reading — it records
        /// that the radio sent none.</summary>
        public const string Temperature = "temperature";

        /// <summary>The transmit meter line's fixed head. The census and
        /// election lines share the <c>txMeters:</c> prefix and are not
        /// readings; the <c>state=</c> field is what only a reading has.</summary>
        internal const string PowerMarker = "txMeters: state=";

        /// <summary>The temperature window's prefix.</summary>
        internal const string TemperatureMarker = "captureMeters:";

        /// <summary>A refusal record carries another session's line inside
        /// it. It is that session's evidence, kept here so the moment is not
        /// lost — not this file's own reading.</summary>
        internal const string RefusalMarker = "TraceDeferred: REFUSED";

        /// <summary>
        /// What kind of reading this line is, or null for none. The line
        /// carries the trace prefix; the markers are searched for, not
        /// anchored.
        /// </summary>
        public static string Classify(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (line.IndexOf(RefusalMarker, StringComparison.Ordinal) >= 0) return null;
            if (line.IndexOf(PowerMarker, StringComparison.Ordinal) >= 0) return Power;
            int at = line.IndexOf(TemperatureMarker, StringComparison.Ordinal);
            if (at < 0) return null;
            return SampleCount(line, at) > 0 ? Temperature : null;
        }

        /// <summary>The <c>n=</c> field of a temperature window, or 0 when
        /// it cannot be read — a window whose count cannot be read is not
        /// claimed as a reading.</summary>
        private static int SampleCount(string line, int from)
        {
            int n = line.IndexOf(" n=", from, StringComparison.Ordinal);
            if (n < 0) return 0;
            int start = n + 3, end = start;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            return end > start && int.TryParse(line.Substring(start, end - start),
                                               NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                ? count
                : 0;
        }
    }

    /// <summary>
    /// What an archived trace file is KNOWN to contain, frozen at the archive by the
    /// sink that wrote it. Facts, not prose: the operator's window chooses
    /// its paragraphs from these, and makes no claim that no fact here
    /// establishes (Sol's review of H9, blocker 2).
    ///
    /// <para><b>"Written" means written and then flushed without a fault.</b>
    /// A line handed to the sink sits in a buffer until the next flush; the
    /// ordinary write flushes at once, but a flush that finds a transition
    /// holding the gate is skipped rather than blocked, and the archive's own
    /// drain writes several lines and flushes once. So a fault can lose
    /// lines that were "written" before it — which is why
    /// <see cref="LinesUnflushedAtFault"/> exists, and why the window may
    /// tell the operator that nothing before the fault was lost only when
    /// these facts say so.</para>
    ///
    /// <para><b>Per part.</b> A session that rotated has several files and
    /// the window names one of them, the last; the reading facts describe
    /// that part. The fault facts describe the sink, which is the same
    /// thing: a faulted sink never rotates again.</para>
    /// </summary>
    public sealed class TraceFileFacts
    {
        internal TraceFileFacts(bool powerWritten, bool temperatureWritten, bool faulted,
                                int linesUnflushedAtFault, bool readingsLostAtFault,
                                bool readingsRefusedAfterFault)
        {
            PowerReadingsWritten = powerWritten;
            TemperatureReadingsWritten = temperatureWritten;
            Faulted = faulted;
            LinesUnflushedAtFault = linesUnflushedAtFault;
            ReadingsLostAtFault = readingsLostAtFault;
            ReadingsRefusedAfterFault = readingsRefusedAfterFault;
        }

        /// <summary>At least one forward-and-reflected power reading reached
        /// this part of the file and was flushed before any fault.</summary>
        public bool PowerReadingsWritten { get; }

        /// <summary>At least one amplifier temperature window with a sample
        /// in it reached this part of the file and was flushed before any
        /// fault.</summary>
        public bool TemperatureReadingsWritten { get; }

        /// <summary>Either kind of reading is in the file.</summary>
        public bool AnyReadingsWritten => PowerReadingsWritten || TemperatureReadingsWritten;

        /// <summary>The sink latched a write or flush fault at some point in
        /// its life. Everything below is meaningful only when this is true.</summary>
        public bool Faulted { get; }

        /// <summary>
        /// How many lines had been handed to the sink after its last
        /// successful flush when the fault hit — lines that were "written"
        /// and are not in the file, over and above the line whose write
        /// failed (a fault in a flush has no failing line of its own). Zero
        /// means the fault took nothing that came before it, which is what
        /// the window needs before it may say that what came before the
        /// fault is all there.
        /// </summary>
        public int LinesUnflushedAtFault { get; }

        /// <summary>A reading was among the lines the fault took: buffered
        /// and unflushed when it hit, or the very line whose write failed.</summary>
        public bool ReadingsLostAtFault { get; }

        /// <summary>A reading was handed to the sink after it had closed
        /// itself over the fault, and was refused. The radio sent it; the
        /// file does not have it.</summary>
        public bool ReadingsRefusedAfterFault { get; }

        /// <summary>Any reading the radio sent from the fault onward is
        /// missing from the file — lost with the fault or refused after it.
        /// False with a fault means every reading the radio sent came
        /// before it and was flushed.</summary>
        public bool ReadingsMissingSinceFault => ReadingsLostAtFault || ReadingsRefusedAfterFault;

        /// <summary>One line for the trace.</summary>
        public override string ToString() =>
            "power=" + (PowerReadingsWritten ? "yes" : "no")
            + " temperature=" + (TemperatureReadingsWritten ? "yes" : "no")
            + (Faulted
                ? " faulted unflushedAtFault=" + LinesUnflushedAtFault.ToString(CultureInfo.InvariantCulture)
                  + " readingsLostAtFault=" + (ReadingsLostAtFault ? "yes" : "no")
                  + " readingsRefusedAfterFault=" + (ReadingsRefusedAfterFault ? "yes" : "no")
                : " no fault");
    }

    /// <summary>
    /// The running tally behind <see cref="TraceFileFacts"/>, owned by one
    /// <see cref="RotatingTraceListener"/> and touched only under that
    /// listener's own lock. Counts what was handed over, confirms it at each
    /// successful flush, and freezes what a fault took.
    /// </summary>
    internal sealed class TraceFileTally
    {
        private bool _power, _temperature;            // confirmed, this part
        private bool _pendingPower, _pendingTemperature; // handed over since the last flush
        private int _unflushed;
        private bool _faulted;
        private int _unflushedAtFault;
        private bool _readingsLostAtFault;
        private bool _readingsRefusedAfterFault;

        /// <summary>A line (or fragment) went into the writer's buffer.</summary>
        public void Wrote(string text)
        {
            _unflushed++;
            string kind = TraceLineKinds.Classify(text);
            if (kind == TraceLineKinds.Power) _pendingPower = true;
            else if (kind == TraceLineKinds.Temperature) _pendingTemperature = true;
        }

        /// <summary>A flush succeeded: everything handed over so far is in
        /// the file.</summary>
        public void Flushed()
        {
            _power |= _pendingPower;
            _temperature |= _pendingTemperature;
            _pendingPower = _pendingTemperature = false;
            _unflushed = 0;
        }

        /// <summary>
        /// A write or flush failed. <paramref name="failingText"/> is the
        /// line whose write or flush threw, or null when a bare flush did;
        /// <paramref name="failingAlreadyCounted"/> says whether that line
        /// had already gone through <see cref="Wrote"/> (its write succeeded
        /// and its own flush failed), so it is not counted twice — the
        /// unflushed figure is what the fault took OVER AND ABOVE the failing
        /// line. Only the first fault is recorded; the sink closes itself on
        /// it.
        /// </summary>
        public void FaultedOn(string failingText, bool failingAlreadyCounted)
        {
            if (_faulted) return;
            _faulted = true;
            _unflushedAtFault = failingAlreadyCounted ? Math.Max(0, _unflushed - 1) : _unflushed;
            _readingsLostAtFault = _pendingPower || _pendingTemperature
                                   || TraceLineKinds.Classify(failingText) != null;
        }

        /// <summary>A line was handed to a sink that had already closed
        /// itself over a fault, and was not written.</summary>
        public void Refused(string text)
        {
            if (TraceLineKinds.Classify(text) != null) _readingsRefusedAfterFault = true;
        }

        /// <summary>A rotation closed the part being counted and opened the
        /// next: the closing part's buffer was flushed on the way (a failure
        /// there is swallowed by the rotation, as it always was), and the
        /// new part starts with nothing in it.</summary>
        public void PartRotated()
        {
            Flushed();
            _power = _temperature = false;
        }

        public TraceFileFacts Snapshot() =>
            new TraceFileFacts(_power, _temperature, _faulted, _unflushedAtFault,
                               _readingsLostAtFault, _readingsRefusedAfterFault);
    }
}
