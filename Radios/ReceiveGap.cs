using System;
using System.Diagnostics;
using JJTrace;
using POpusCodec;

namespace Radios
{
    /// <summary>
    /// How many receive packets are missing between two the loop consumed,
    /// from the radio's own 4-bit VITA packet count and the timestamp's whole
    /// UTC second (#1155). Pure; the one copy of this arithmetic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Extracted from <see cref="ComputerFlacRecorder"/>,</b> whose writer
    /// computed exactly this before deciding how much silence to put on disk.
    /// The recorder now calls this and keeps its own arrival-time rule on top
    /// (<see cref="ComputerFlacRecorder.SilentFramesBefore"/>), so there is one
    /// copy of the counter and second arithmetic, not two.
    /// </para>
    /// <para>
    /// <b>The counter wraps every 16 packets</b>, so a gap of 17 reads as 1 and
    /// a duplicate reads as 15. Only the integer half of the timestamp key is
    /// trusted (see <see cref="ReceiveContinuityMeter"/>): a step of two or
    /// more whole seconds adds a hundred packets per skipped second.
    /// </para>
    /// <para>
    /// <b>For the merge:</b> main carries <c>Radios/Listenback/AudioPacketContinuity</c>,
    /// a fuller estimator of the same stream. This branch lacks it; reconcile
    /// the two at the merge rather than keeping three copies.
    /// </para>
    /// </remarks>
    public static class ReceiveGap
    {
        /// <summary>
        /// Packets missing between <paramref name="previousSequence"/> (null
        /// when there is none yet) and <paramref name="sequence"/>. Zero for
        /// consecutive packets.
        /// </summary>
        public static int MissingFrames(int? previousSequence, int sequence, long previousSecond, long second)
        {
            int missing = previousSequence.HasValue ? (sequence - previousSequence.Value + 15) & 15 : 0;
            if (previousSecond > 0 && second > previousSecond + 1)
                missing += (int)Math.Min(1000, (second - previousSecond - 1) * 100);
            return missing;
        }

        /// <summary>The whole UTC second of a FlexLib receive timestamp key.</summary>
        public static long SecondOf(double timestamp) => (long)Math.Floor(timestamp);

        /// <summary>
        /// The most lost frames the receive loop will make up. 40 ms, against
        /// the 60 ms default receive reserve. Over it the loop splices, as it
        /// always did: the 4-bit counter cannot tell a gap of 17 from a gap of
        /// 1, a long fabricated stretch is worse than a short silence, and
        /// filling a long gap would add standing latency, which is #473's to
        /// change and not this. The number is the design's judgement; the
        /// forced-loss bench step measures it.
        /// </summary>
        public const int ConcealCapFrames = 4;
    }

    /// <summary>What the receive loop does about one consumed packet's gap.</summary>
    public enum ReceiveGapAction
    {
        /// <summary>No gap.</summary>
        None,
        /// <summary>Make up the missing frames, then play the packet.</summary>
        Conceal,
        /// <summary>Longer than the cap: splice, as before.</summary>
        SpliceOverCap,
        /// <summary>Concealment is switched off for an A-to-B test: splice.</summary>
        SpliceSwitchedOff,
        /// <summary>Local take playback mutes decoding: conceal nothing.</summary>
        Muted,
    }

    /// <summary>
    /// The receive loop's gap state for one PC-audio run: the previous
    /// sequence and second, and the counts traced at the run's end (#1155).
    /// </summary>
    /// <remarks>
    /// Every consumed packet goes through <see cref="Observe"/>, muted or not,
    /// so a packet after a muted stretch is not read as a gap. The run seeds
    /// it from the packet it skips to when playback (re)starts
    /// (<see cref="Seed"/>), so the next packet reads as consecutive.
    /// </remarks>
    public sealed class ReceiveGapTracker
    {
        private int? _previousSequence;
        private long _previousSecond;

        public bool ConcealEnabled { get; }
        public int? PreviousSequence => _previousSequence;

        public long GapsConcealed { get; private set; }
        public long FramesConcealed { get; private set; }
        public long FramesRecovered { get; private set; }
        public long GapsOverCap { get; private set; }
        public long FramesOverCap { get; private set; }
        public long GapsSwitchedOff { get; private set; }
        public long GapsWhileMuted { get; private set; }
        public long PacketsDroppedForTest { get; private set; }

        public ReceiveGapTracker(bool concealEnabled = true) { ConcealEnabled = concealEnabled; }

        /// <summary>Playback (re)started at this packet; the next one is consecutive to it.</summary>
        public void Seed(int sequence, double timestamp)
        {
            _previousSequence = sequence & 15;
            _previousSecond = ReceiveGap.SecondOf(timestamp);
        }

        /// <summary>
        /// Note a consumed packet and say what to do about any gap before it.
        /// <paramref name="missing"/> is the number of lost frames.
        /// </summary>
        public ReceiveGapAction Observe(int sequence, double timestamp, bool muted, out int missing)
        {
            long second = ReceiveGap.SecondOf(timestamp);
            missing = ReceiveGap.MissingFrames(_previousSequence, sequence, _previousSecond, second);
            _previousSequence = sequence & 15;
            _previousSecond = second;
            if (missing <= 0) return ReceiveGapAction.None;
            if (muted) { GapsWhileMuted++; return ReceiveGapAction.Muted; }
            if (missing > ReceiveGap.ConcealCapFrames)
            {
                GapsOverCap++;
                FramesOverCap += missing;
                return ReceiveGapAction.SpliceOverCap;
            }
            if (!ConcealEnabled) { GapsSwitchedOff++; return ReceiveGapAction.SpliceSwitchedOff; }
            return ReceiveGapAction.Conceal;
        }

        /// <summary>Record what a fill actually produced.</summary>
        public void NoteFilled(int concealed, int recovered)
        {
            if (concealed + recovered > 0) GapsConcealed++;
            FramesConcealed += concealed;
            FramesRecovered += recovered;
        }

        public void NoteDroppedForTest() => PacketsDroppedForTest++;

        /// <summary>The once-per-run summary line.</summary>
        public string Summary()
        {
            return "receive gaps this run: " + FramesConcealed + " frame(s) concealed and "
                + FramesRecovered + " recovered by FEC, across " + GapsConcealed + " gap(s); "
                + GapsOverCap + " gap(s) over the " + ReceiveGap.ConcealCapFrames + "-frame cap spliced ("
                + FramesOverCap + " frame(s))"
                + (ConcealEnabled ? "" : "; concealment SWITCHED OFF by JJFLEX_RX_CONCEAL, "
                    + GapsSwitchedOff + " gap(s) spliced")
                + "; " + GapsWhileMuted + " gap(s) while muted for take playback"
                + (PacketsDroppedForTest > 0 ? "; " + PacketsDroppedForTest + " packet(s) dropped by JJFLEX_RX_DROP_TEST" : "");
        }
    }

    /// <summary>
    /// A one-time look at what the radio's receive Opus actually is, over the
    /// first <see cref="PacketsToSample"/> packets of a run: coding mode, frame
    /// length, and whether the packets carry in-band FEC (LBRR). Whether FEC
    /// can ever recover a lost frame depends on the last of those, and it was
    /// not established when #1155 was built; this line settles it from any
    /// bench trace, on LAN and over SmartLink.
    /// </summary>
    public sealed class ReceiveOpusCensus
    {
        public const int PacketsToSample = 500;

        public int Packets { get; private set; }
        public int Silk { get; private set; }
        public int Hybrid { get; private set; }
        public int Celt { get; private set; }
        public int WithLbrr { get; private set; }
        public int Empty { get; private set; }
        public double ShortestMs { get; private set; } = double.MaxValue;
        public double LongestMs { get; private set; }
        public bool Reported { get; private set; }

        /// <summary>The coding mode from an Opus TOC byte (RFC 6716, section 3.1).</summary>
        public static string ModeOf(byte toc)
        {
            int config = toc >> 3;
            if (config <= 11) return "SILK";
            if (config <= 15) return "hybrid";
            return "CELT";
        }

        /// <summary>
        /// Count one packet. Returns the census line once, when the sample is
        /// complete; null otherwise.
        /// </summary>
        public string? Observe(byte[] packet)
        {
            if (Reported || Packets >= PacketsToSample) return null;
            Packets++;
            if (packet == null || packet.Length == 0) Empty++;
            else
            {
                switch (ModeOf(packet[0]))
                {
                    case "SILK": Silk++; break;
                    case "hybrid": Hybrid++; break;
                    default: Celt++; break;
                }
                if (OpusDecoder.HasLbrr(packet)) WithLbrr++;
                int samples = OpusDecoder.PacketSamples(packet, 48000);
                if (samples > 0)
                {
                    double ms = samples / 48.0;
                    if (ms < ShortestMs) ShortestMs = ms;
                    if (ms > LongestMs) LongestMs = ms;
                }
            }
            return Packets >= PacketsToSample ? Report() : null;
        }

        /// <summary>The census line, marked as reported. Null if there was nothing to count or it was already said.</summary>
        public string? Report()
        {
            if (Reported || Packets == 0) return null;
            Reported = true;
            return "receive Opus census over the first " + Packets + " packet(s)"
                + (Packets < PacketsToSample ? " (the run ended before " + PacketsToSample + ")" : "")
                + ": " + Silk + " SILK, " + Hybrid + " hybrid, " + Celt + " CELT, " + Empty + " empty; "
                + WithLbrr + " carry in-band FEC (LBRR)"
                + (WithLbrr == 0 ? ", so a lost frame can only be concealed, never recovered" : "")
                + "; frame length "
                + (LongestMs > 0 ? ShortestMs.ToString("F1") + " to " + LongestMs.ToString("F1") + " ms" : "unreadable");
        }
    }

    /// <summary>
    /// The receive-side test-only fault switches (#1155), read once per
    /// PC-audio run and traced. Environment variables, never surfaced in any
    /// interface, in the manner of <c>JJFLEX_RX_PRIME_MS</c>.
    /// </summary>
    public static class ReceiveTestSwitches
    {
        public const string DropVariable = "JJFLEX_RX_DROP_TEST";
        public const string ConcealVariable = "JJFLEX_RX_CONCEAL";

        /// <summary>
        /// <c>every:N</c> discards every Nth receive packet in the loop, before
        /// the continuity meter, the recorder and the gap tracker see it, so the
        /// drop looks like a real loss to all three. Returns N, or 0 when
        /// unset or malformed (N must be 2 or more).
        /// </summary>
        public static int ParseDropEvery(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            const string prefix = "every:";
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
            return int.TryParse(s.Substring(prefix.Length).Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 2 ? n : 0;
        }

        /// <summary><c>off</c> turns concealment off for an A-to-B comparison. Anything else leaves it on.</summary>
        public static bool ParseConcealEnabled(string? raw) =>
            !string.Equals(raw?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

        /// <summary>Read both switches for this run, tracing any that is set.</summary>
        public static (int DropEvery, bool ConcealEnabled) ReadForRun()
        {
            string? drop = Read(DropVariable);
            string? conceal = Read(ConcealVariable);
            int every = ParseDropEvery(drop);
            bool enabled = ParseConcealEnabled(conceal);
            if (!string.IsNullOrWhiteSpace(drop))
                Tracing.TraceLine(every > 0
                    ? "test switch " + DropVariable + "=" + drop + ": every " + every
                        + "th receive packet is discarded this run, before the meters, the recorder and the gap tracker"
                    : "test switch " + DropVariable + "=\"" + drop + "\" is not every:N with N of 2 or more; ignored",
                    TraceLevel.Error);
            if (!string.IsNullOrWhiteSpace(conceal))
                Tracing.TraceLine("test switch " + ConcealVariable + "=" + conceal + ": receive gap concealment is "
                    + (enabled ? "on (only \"off\" turns it off)" : "OFF this run; gaps are spliced"), TraceLevel.Error);
            return (every, enabled);
        }

        private static string? Read(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch (Exception ex)
            {
                Tracing.TraceLine("could not read " + name + ": " + ex.Message, TraceLevel.Error);
                return null;
            }
        }
    }

    /// <summary>Counts packets for <c>JJFLEX_RX_DROP_TEST</c>. Inert at zero.</summary>
    public sealed class ReceiveDropTest
    {
        private readonly int _every;
        private long _count;
        public ReceiveDropTest(int every) { _every = every; }
        public bool Active => _every >= 2;
        /// <summary>True for every Nth packet when active.</summary>
        public bool ShouldDrop() => _every >= 2 && ++_count % _every == 0;
    }
}
