using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// How much receive audio never arrived, measured from the radio's own
    /// packet timestamps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A packet the NETWORK lost never reaches PortAudio, so <c>statusFlags</c>
    /// cannot see it and neither can the playback queue's own meters: our
    /// decoder simply splices two non-adjacent 10 ms packets together, and the
    /// waveform step at the splice IS a click. This is the only instrument that
    /// sees that, and Track B built the first version of it on 2026-08-18 (#29).
    /// </para>
    /// <para>
    /// <b>Rewritten 2026-09-02 (#473) because the timestamps will not carry the
    /// weight the first version put on them.</b> FlexLib forms the key as
    /// <c>TimestampInt + TimestampFrac / 2^16</c> (RXAudioStream.AddRXData). The
    /// integer half is UTC seconds — the 2026-09-01 captures print keys such as
    /// 1788304852.0000153, and 1788304852 is that evening. The fractional half
    /// is NOT a fraction of a second: measured against the wall clock in those
    /// same captures it advances only about 0.147 over a second of audio, so a
    /// step in it converts to nothing, least of all to milliseconds.
    /// </para>
    /// <para>
    /// <b>What that cost.</b> At each UTC second the key therefore jumps by the
    /// ~0.853 the fractional part never covered, and the old meter reported that
    /// jump as "consumed packet timestamps stepped 852.7 ms — audio between
    /// those timestamps never arrived". That is the figure #473 asked to have
    /// accounted for, and the account is that it is not a gap. Three
    /// confirmations, all from the evening's own traces:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// the discontinuity COUNT equals the stream's length in seconds, exactly:
    /// 12,772 packets and 128 discontinuities; 76,881 and 768; 2,607 and 26.
    /// One per second, on every stream.
    /// </description></item>
    /// <item><description>
    /// largest step + 99 x nominal step is 1.000 on every stream, to within a
    /// few percent. That is what a second boundary looks like at a hundred
    /// packets a second, and it is not what a lost-audio gap looks like.
    /// </description></item>
    /// <item><description>
    /// taken at face value it claims 108 of 128 seconds of audio never arrived,
    /// in a stream whose playback queue ran dry six times. Two instruments
    /// disagreeing by that margin means one of them is wrong, and the queue's
    /// is the one that counts device buffers it filled itself.
    /// </description></item>
    /// </list>
    /// <para>
    /// The "nominal" step it compared against was a running MINIMUM, so a single
    /// anomalously small step poisoned it for the rest of the session — one
    /// capture ended up flagging 119,428 of 130,067 packets as discontinuities.
    /// </para>
    /// <para>
    /// <b>What this measures instead.</b> Only the integer half is trusted, and
    /// it is used as a clock rather than as a ruler: count the packets consumed
    /// within each UTC second. Every complete second should hold as many as the
    /// busiest one did, and a second holding fewer lost that many packets of
    /// audio. No calibration, no assumed packet rate, and no unit conversion
    /// that can be wrong.
    /// </para>
    /// </remarks>
    public sealed class ReceiveContinuityMeter
    {
        private long _second;            // UTC second being counted, 0 = not started
        private long _inSecond;          // packets consumed in it
        private long _peakPerSecond;     // busiest complete second = the packet rate
        private long _secondsJudged;     // complete seconds counted
        private long _packetsJudged;     // packets in those seconds
        private long _shortSeconds;      // complete seconds that came up short
        private long _skippedSeconds;    // whole seconds the key stepped over
        private bool _firstSecondClosed; // the leading partial second is done
        private bool _shortLogged;
        private long _packetCount;

        /// <summary>Every packet this meter was shown.</summary>
        public long PacketCount { get { return _packetCount; } }

        /// <summary>
        /// Packets in the busiest whole second — the stream's packet rate,
        /// observed rather than assumed. Zero until a whole second has closed.
        /// </summary>
        public long PeakPerSecond { get { return _peakPerSecond; } }

        /// <summary>Whole seconds judged. The leading partial second is not one.</summary>
        public long SecondsJudged { get { return _secondsJudged; } }

        /// <summary>Whole seconds that held fewer packets than the busiest one.</summary>
        public long ShortSeconds { get { return _shortSeconds; } }

        /// <summary>Whole seconds the timestamps stepped straight over.</summary>
        public long SkippedSeconds { get { return _skippedSeconds; } }

        /// <summary>Packets that should have been in the judged seconds and were not.</summary>
        public long MissingPackets
        {
            get
            {
                if (_secondsJudged == 0 || _peakPerSecond == 0) return 0;
                long missing = (_secondsJudged * _peakPerSecond) - _packetsJudged;
                return missing > 0 ? missing : 0;
            }
        }

        /// <summary>
        /// Receive audio that never arrived, in milliseconds. Derived from the
        /// stream's own observed packet rate, so it needs no assumption about
        /// frame duration.
        /// </summary>
        public double MissingMilliseconds
        {
            get
            {
                return _peakPerSecond == 0 ? 0 : MissingPackets * 1000.0 / _peakPerSecond;
            }
        }

        /// <summary>Start over. Called at the top of every remote-audio run.</summary>
        public void Reset()
        {
            _second = 0;
            _inSecond = 0;
            _peakPerSecond = 0;
            _secondsJudged = 0;
            _packetsJudged = 0;
            _shortSeconds = 0;
            _skippedSeconds = 0;
            _firstSecondClosed = false;
            _shortLogged = false;
            _packetCount = 0;
        }

        /// <summary>
        /// Forget which second we were in, keeping the totals. Used when the
        /// poll loop deliberately jumps to the newest packet at stream start —
        /// the second it lands in is partial and must not be judged.
        /// </summary>
        public void Rearm()
        {
            _second = 0;
            _inSecond = 0;
            _firstSecondClosed = false;
        }

        /// <summary>
        /// Record one consumed packet by its timestamp key. Called on the
        /// receive poll thread, once per packet.
        /// </summary>
        public void Consume(double timestampKey)
        {
            long second = (long)Math.Floor(timestampKey);
            _packetCount++;
            if (_second == 0)
            {
                _second = second;
            }
            else if (second != _second)
            {
                CloseSecond(second);
            }
            _inSecond++;
        }

        private void CloseSecond(long nextSecond)
        {
            if (!_firstSecondClosed)
            {
                // The first second is partial — the stream started part way
                // through it — so it is closed and discarded rather than judged
                // against seconds that were whole.
                _firstSecondClosed = true;
            }
            else
            {
                _secondsJudged++;
                _packetsJudged += _inSecond;
                if (_inSecond > _peakPerSecond) _peakPerSecond = _inSecond;
                if (_peakPerSecond > 0 && _inSecond < _peakPerSecond)
                {
                    _shortSeconds++;
                    if (!_shortLogged)
                    {
                        _shortLogged = true;
                        Tracing.TraceLine("remoteAudioProc: first short second on the receive"
                            + " stream — " + _inSecond + " packets consumed where "
                            + _peakPerSecond + " is this stream's full second, so about "
                            + ((_peakPerSecond - _inSecond) * 1000.0 / _peakPerSecond).ToString("F0")
                            + " ms of audio never arrived. Splicing across it is audible as a"
                            + " click. Further short seconds are counted silently; totals are"
                            + " logged when the stream stops.", TraceLevel.Error);
                    }
                }
            }

            long skipped = nextSecond - _second - 1;
            if (skipped > 0)
            {
                // The key stepped over whole UTC seconds. That is a real outage,
                // and it is measured in seconds rather than inferred from a
                // fractional part that does not mean what it looks like.
                _skippedSeconds += skipped;
                Tracing.TraceLine("remoteAudioProc: the receive stream skipped " + skipped
                    + " whole second(s) of audio — the packet timestamps went from "
                    + _second + " to " + nextSecond, TraceLevel.Error);
            }

            _second = nextSecond;
            _inSecond = 0;
        }

        /// <summary>
        /// One line at stream shutdown: was the receive stream continuous? A
        /// clean run is evidence too — it acquits the network and points the
        /// click hunt at the playback side, where PortAudio's status flags and
        /// the playback queue's own silence meters live.
        /// </summary>
        public void TraceSummary()
        {
            if (_packetCount == 0)
            {
                Tracing.TraceLine("remoteAudioProc continuity summary: no receive packets consumed",
                    TraceLevel.Info);
                return;
            }
            if (_secondsJudged == 0 || _peakPerSecond == 0)
            {
                Tracing.TraceLine("remoteAudioProc continuity summary: "
                    + _packetCount + " packets consumed, but the stream did not last a whole"
                    + " UTC second, so there is nothing to judge its continuity against",
                    TraceLevel.Info);
                return;
            }

            long missing = MissingPackets;
            Tracing.TraceLine("remoteAudioProc continuity summary: "
                + _packetCount + " packets consumed, " + _peakPerSecond
                + " packets in a full second, " + _secondsJudged + " whole second(s) judged; "
                + _shortSeconds + " came up short and " + _skippedSeconds
                + " whole second(s) were skipped entirely — "
                + MissingMilliseconds.ToString("F0") + " ms of receive audio never arrived"
                + (missing == 0 ? " (the network delivered a continuous stream)" : ""),
                missing == 0 && _skippedSeconds == 0 ? TraceLevel.Info : TraceLevel.Error);
        }
    }
}
