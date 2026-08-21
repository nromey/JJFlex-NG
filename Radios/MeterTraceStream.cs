using System;
using System.Collections.Generic;
using System.Globalization;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The per-packet meter stream, made opt-in and coalesced — task #170.
    ///
    /// <para><b>Why this exists.</b> Until 2026-08-21 every meter packet wrote
    /// its values straight into the trace at Verbose: micData, micPeakData,
    /// compPeakData, hwALCData, forwardPower, reflectedPower, SWRData and
    /// sMeterData, several lines per packet, continuously, transmitting or not.
    /// Measured that morning: 418,004 such lines in one 50-minute capture —
    /// 25.7 MB of its 52.4 MB, at about 139 lines a second. The volume was not
    /// cosmetic. Any consumer that reads "the last N bytes" of the log reads a
    /// window this stream had already flushed: the capture-state check found
    /// zero speech lines in any 64 KB window of a healthy capture and toggled
    /// the operator's capture OFF, and jjprobe reported "no DoCommand lines"
    /// for a session whose DoCommand lines sat 45 seconds in. A byte-scoped
    /// window is a time window whose duration is set by the noisiest
    /// subsystem, and this was the noisiest subsystem.</para>
    ///
    /// <para><b>Why it is not simply deleted.</b> The stream is the evidence
    /// for transmit-chain diagnosis — SWR against forward power, mic level
    /// against ALC, on a bench with a dummy load. So it becomes a thing you
    /// turn on for a bench session: "Record the meter stream" on Settings →
    /// Diagnostics (persisted in DiagnosticsConfig.RecordMeterStream), off by
    /// default.</para>
    ///
    /// <para><b>Why coalesced.</b> One line per meter per second carries the
    /// same diagnostic value as forty — provided the peaks survive, because
    /// transients are exactly what transmit diagnosis cares about. So each
    /// line reports min, max and last over its window plus the sample count:
    /// <c>micData: min=-120 max=-118.4 last=-119 n=34</c>. The window closes
    /// on the first sample that arrives at least a second after it opened;
    /// meters stream continuously while connected, so a window never waits
    /// long for its closing sample. tools/TxFactAudit (TraceMeters.cs) parses
    /// this format and the raw per-packet one older traces carry — change
    /// either side only in step with the other.</para>
    ///
    /// <para><b>Why the lines carry no TraceLevel.</b> They use the
    /// unconditional TraceLine overload on purpose, same reasoning as the
    /// CaptureState marker: the operator explicitly asked for this stream, and
    /// a stream that also needs the standing detail level to be Detailed is a
    /// switch that silently does nothing at Normal — on a bench, mid-session,
    /// with no way to tell why.</para>
    /// </summary>
    public sealed class MeterTraceStream
    {
        /// <summary>
        /// Whether the meter stream is being recorded at all. App-global, set
        /// at boot from DiagnosticsConfig.RecordMeterStream and flipped live by
        /// the Diagnostics tab. Volatile: it is read on FlexLib's meter packet
        /// thread and written on the UI thread.
        /// </summary>
        private static volatile bool _enabled;

        /// <summary>See <see cref="_enabled"/>.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        private sealed class Channel
        {
            public float Min, Max, Last;
            public int Count;
            public int WindowStart;
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Channel> channels =
            new Dictionary<string, Channel>(StringComparer.Ordinal);

        /// <summary>
        /// Milliseconds a window stays open. One second, per the design above.
        /// </summary>
        private const int WindowMs = 1000;

        /// <summary>
        /// Feed one meter reading. Called at meter rate on FlexLib's packet
        /// thread, so the disabled path is a single volatile read and the
        /// enabled path is a dictionary lookup and a few compares; the file
        /// write happens at most once a second per channel, outside the lock —
        /// Trace.AutoFlush is on, so a write inside the lock would serialize
        /// every meter channel behind disk latency.
        /// </summary>
        /// <param name="channel">The line key EXACTLY as it should open the
        /// line, e.g. "micData:" — or "sMeterData:0" for the per-slice
        /// S-meter, matching the key shape the raw lines always had. The
        /// summary is the key plus a space plus the fields, so "micData:"
        /// yields <c>micData: min=…</c> and "sMeterData:0" yields
        /// <c>sMeterData:0 min=…</c>.</param>
        /// <param name="value">The reading as FlexLib delivered it.</param>
        public void Report(string channel, float value)
        {
            if (!_enabled) return;

            string line = null;
            lock (gate)
            {
                if (!channels.TryGetValue(channel, out Channel c))
                {
                    c = new Channel();
                    channels[channel] = c;
                    c.Count = 0;
                }

                if (c.Count == 0)
                {
                    c.WindowStart = Environment.TickCount;
                    c.Min = c.Max = c.Last = value;
                    c.Count = 1;
                }
                else
                {
                    if (value < c.Min) c.Min = value;
                    if (value > c.Max) c.Max = value;
                    c.Last = value;
                    c.Count++;
                }

                if ((Environment.TickCount - c.WindowStart) >= WindowMs)
                {
                    line = channel
                        + " min=" + c.Min.ToString("0.###", CultureInfo.InvariantCulture)
                        + " max=" + c.Max.ToString("0.###", CultureInfo.InvariantCulture)
                        + " last=" + c.Last.ToString("0.###", CultureInfo.InvariantCulture)
                        + " n=" + c.Count.ToString(CultureInfo.InvariantCulture);
                    c.Count = 0;
                }
            }

            if (line != null) Tracing.TraceLine(line);
        }
    }
}
