using System;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace JJPortaudio
{
    /// <summary>
    /// A thread that keeps a <see cref="TxFramePump"/> running, so a transmit
    /// source with no clock of its own does not have to borrow the
    /// microphone's (#208).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reasoning lives on <see cref="TxFramePump"/>, which is where the
    /// work happens. This class is only the thread around it — deliberately,
    /// so the arithmetic can be tested without racing a real clock.
    /// </para>
    /// <para>
    /// <b>On cadence, and why bursts are not a worry.</b> The capture path has
    /// always delivered in bursts: <c>Audio.Open</c> sizes the PortAudio buffer
    /// for ten callbacks a second, and each callback loops out roughly ten
    /// 10 ms Opus frames back to back. So the radio has been receiving
    /// hundred-millisecond bursts for as long as this feature has existed and
    /// is plainly fine with them. This source emits one or two frames per
    /// wake-up instead, which is strictly smoother — and, more to the point,
    /// carries no rate error at all.
    /// </para>
    /// </remarks>
    public sealed class TxSelfClockedSource
    {
        private readonly TxFramePump _pump;
        private readonly object _lifecycle = new object();
        private Thread _thread;

        /// <summary>
        /// How long <see cref="Stop"/> waits for the thread to leave the loop
        /// before giving up and saying so. Twenty frame periods: long enough
        /// that only a genuinely wedged thread reaches it, short enough that a
        /// wedged one does not hold up an unkey.
        /// </summary>
        public const int StopJoinMs = 200;

        /// <param name="pipeline">The shared transmit tail.</param>
        /// <param name="sampleRate">The rate the encoder was built for.</param>
        /// <param name="samplesPerFrame">Samples per channel in one Opus frame.</param>
        public TxSelfClockedSource(TxFramePipeline pipeline, uint sampleRate, int samplesPerFrame)
        {
            _pump = new TxFramePump(pipeline, sampleRate, samplesPerFrame);
        }

        /// <summary>The pump this thread drives.</summary>
        public TxFramePump Pump => _pump;

        /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
        public bool Running => _pump.Running;

        /// <inheritdoc cref="TxFramePump.Matches"/>
        public bool Matches(uint sampleRate, int samplesPerFrame) =>
            _pump.Matches(sampleRate, samplesPerFrame);

        /// <summary>Begin producing frames.</summary>
        /// <returns>False if already running.</returns>
        public bool Start()
        {
            lock (_lifecycle)
            {
                if (_pump.Running) return false;

                _pump.Start(Stopwatch.GetTimestamp());

                _thread = new Thread(Loop)
                {
                    Name = "JJFlex TX self-clock",
                    // Background, and this is not a detail. A foreground audio
                    // thread that outlives its Join is exactly how the app
                    // learned about orphan jjflexible.exe processes (#14): the
                    // process cannot exit while one is alive, and nothing on
                    // screen says why. A background thread cannot pin the
                    // process no matter how it ends.
                    IsBackground = true,
                    // The same priority the rest of the audio engine runs at.
                    // The work per wake-up is one Opus encode of a 10 ms frame
                    // — tens of microseconds — so this starves nothing.
                    Priority = ThreadPriority.Highest,
                };
                _thread.Start();

                Tracing.TraceLine("TxSelfClockedSource: started at " + _pump.SampleRate + " Hz, "
                    + _pump.Clock.SamplesPerFrame + " samples per frame ("
                    + _pump.Clock.FrameMs.ToString("0.##") + " ms), pacing from elapsed time"
                    + " rather than from a capture device", TraceLevel.Info);
                return true;
            }
        }

        /// <summary>
        /// Stop producing frames, immediately — no drain, no tail. See
        /// <see cref="TxFramePump.Stop"/> for why there is no graceful variant.
        /// </summary>
        public void Stop()
        {
            Thread t;
            lock (_lifecycle)
            {
                if (!_pump.Running) return;
                _pump.Stop();
                t = _thread;
                _thread = null;
            }

            if (t != null && !t.Join(StopJoinMs))
            {
                Tracing.TraceLine("TxSelfClockedSource: pump thread did not stop within "
                    + StopJoinMs + " ms; abandoning the wait. It is a background thread,"
                    + " so it cannot hold the process open, but say so rather than"
                    + " leaving a silent one running", TraceLevel.Error);
            }

            Tracing.TraceLine("TxSelfClockedSource: stopped — "
                + _pump.DescribeRun(Stopwatch.GetTimestamp(), Stopwatch.Frequency), TraceLevel.Info);
        }

        private void Loop()
        {
            try
            {
                long tps = Stopwatch.Frequency;
                while (_pump.Running)
                {
                    int sent = _pump.PumpOnce(Stopwatch.GetTimestamp(), tps);
                    // Only yield when there was nothing to do. With frames owed
                    // — a catch-up after a scheduling gap — keep going, because
                    // the clock has already decided how many are owed and
                    // sleeping between them just makes the next call owe more.
                    //
                    // Sleep(1) is nominal: Windows may hold us for up to about
                    // 15 ms depending on the system timer resolution, which no
                    // process should be raising on its own. That coarseness is
                    // fine and is a tested property of the clock — a coarse
                    // timer that averages the right rate is absorbed by the
                    // radio's jitter buffer; a smooth one that averages the
                    // wrong rate is not.
                    if (sent == 0) Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxSelfClockedSource: pump thread failed, " + ex.Message
                    + " — transmit audio from this source has stopped", TraceLevel.Error);
                _pump.Stop();
            }
        }
    }
}
