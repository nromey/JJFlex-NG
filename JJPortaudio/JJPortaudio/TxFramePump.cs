using System;
using JJTrace;
using System.Diagnostics;

namespace JJPortaudio
{
    /// <summary>
    /// Produces transmit frames on demand, as many as elapsed time says are
    /// owed. No thread of its own — <see cref="TxSelfClockedSource"/> supplies
    /// that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this removes.</b> Until 2026-08-24 every transmit frame —
    /// including a synthesized test tone — was paced by the microphone's
    /// capture callback. For a microphone that is correct: the mic IS the
    /// source and its clock is the natural one. For a tone computed from a
    /// formula it is incoherent. The samples have no clock, and we handed them
    /// one belonging to a device that contributes nothing to the signal, so
    /// every property of that device — its true rate, its buffer scheduling,
    /// its dropouts — became a property of a signal it never touched.
    /// </para>
    /// <para>
    /// That is where the galloping went. A capture device whose real rate
    /// differs from its nominal one by a fraction of a percent produces a
    /// CONSTANT rate error, and a constant rate error against the radio's
    /// jitter buffer produces a PERIODIC correction — which is why the fault
    /// was heard as a metronome at roughly 750 ms rather than as something
    /// ragged.
    /// </para>
    /// <para>
    /// <b>Why the thread lives somewhere else.</b> Everything interesting here
    /// is arithmetic — how many frames are owed, what the source is handed,
    /// what happens when the encoder fails — and none of it should need a real
    /// second to test. Time arrives as a parameter, so a fifteen-minute
    /// transmission runs in microseconds and the answers are exact. A class
    /// that owned the thread too could only be tested by racing it.
    /// </para>
    /// <para>
    /// <b>The buffer is zeroed every frame, deliberately.</b> There is no
    /// microphone here, so the "microphone samples" a source sees are silence,
    /// and they have to genuinely be silence: an engaged source overwrites the
    /// buffer, but a source ramping in or out MULTIPLIES it, and multiplying
    /// stale samples from the previous frame would ramp last frame's audio
    /// back up. Clearing 960 floats a hundred times a second costs nothing
    /// measurable and removes the whole question.
    /// </para>
    /// </remarks>
    public sealed class TxFramePump
    {
        private readonly TxFramePipeline _pipeline;
        private readonly TxSampleClock _clock;
        private readonly uint _sampleRate;
        private readonly int _floatsPerFrame;
        private readonly float[] _buffer;
        /// <summary>
        /// This producer's answer to the pipeline's teardown question, cached
        /// once so the hundred-times-a-second call allocates nothing.
        /// </summary>
        private readonly Func<bool> _stillRunning;

        private volatile bool _running;
        private long _pumps;
        private long _pumpsWithNothingDue;

        /// <param name="pipeline">
        /// The shared transmit tail. Must already carry the encode step, the
        /// source, the meter and the handler — this class supplies frames and
        /// nothing else.
        /// </param>
        /// <param name="sampleRate">The rate the encoder was built for.</param>
        /// <param name="samplesPerFrame">
        /// Samples PER CHANNEL in one Opus frame — one hundredth of the sample
        /// rate at the 10 ms encoder delay the transmit channel uses.
        /// </param>
        public TxFramePump(TxFramePipeline pipeline, uint sampleRate, int samplesPerFrame)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            if (sampleRate == 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (samplesPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerFrame));

            _sampleRate = sampleRate;
            // Interleaved stereo throughout the transmit path, exactly as the
            // capture callback hands it over: a mono microphone is duplicated
            // onto both channels long before this point.
            _floatsPerFrame = samplesPerFrame * 2;
            _buffer = new float[_floatsPerFrame];
            _clock = new TxSampleClock((int)sampleRate, samplesPerFrame);
            _stillRunning = () => _running;
        }

        /// <summary>The pacing clock, for the trace and for rate checks.</summary>
        public TxSampleClock Clock => _clock;

        /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
        public bool Running => _running;

        /// <summary>The rate this pump was built for. Never changes.</summary>
        public uint SampleRate => _sampleRate;

        /// <summary>
        /// Does this pump still match the stream it is pacing?
        /// </summary>
        /// <remarks>
        /// Noel, 2026-08-24: "make sure that the sample rate doesn't change,
        /// though not often, sometimes it happens." The capture path noticed a
        /// rate change by accident — the callback changed with the device. This
        /// one has to be asked, so the caller asks before every start and
        /// rebuilds rather than reusing. Same family as #53, where the Opus
        /// encoder was built from the REQUESTED rate rather than the NEGOTIATED
        /// one.
        /// </remarks>
        public bool Matches(uint sampleRate, int samplesPerFrame) =>
            _clock.Matches((int)sampleRate, samplesPerFrame);

        /// <summary>
        /// Begin, or begin again. Resets the accumulator and the pipeline's
        /// counters: a new transmission owes nothing for the time it spent not
        /// transmitting.
        /// </summary>
        /// <param name="nowTicks">
        /// A monotonic reference in <see cref="Stopwatch"/> ticks. Passed in
        /// rather than read here so this is testable without waiting.
        /// </param>
        public void Start(long nowTicks)
        {
            _pumps = 0;
            _pumpsWithNothingDue = 0;
            _pipeline.ResetCounters();
            _clock.Start(nowTicks);
            _running = true;
        }

        /// <summary>
        /// Stop, immediately.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Deliberately hard.</b> Noel ratified the rule on 2026-08-24:
        /// transmit stop stops everything — tone or microphone, no drain, no
        /// tail. There is no graceful variant here on purpose, because a
        /// graceful variant would eventually get called by mistake from the
        /// unkey path, and audio continuing after an unkey is a safety fault,
        /// not a cosmetic one.
        /// </para>
        /// <para>
        /// Letting a source finish its release ramp is a DIFFERENT decision,
        /// made one level up by whoever is swapping sources, by simply not
        /// stopping until the source reports <see cref="ITxInputSource.Idle"/>.
        /// </para>
        /// </remarks>
        public void Stop()
        {
            _running = false;
            _clock.Stop();
        }

        /// <summary>
        /// Ask the clock what is owed and send it.
        /// </summary>
        /// <param name="nowTicks">Monotonic reference, same base as Start.</param>
        /// <param name="ticksPerSecond"><see cref="Stopwatch.Frequency"/>.</param>
        /// <returns>Frames actually sent.</returns>
        public int PumpOnce(long nowTicks, long ticksPerSecond)
        {
            if (!_running) return 0;
            _pumps++;

            int due = _clock.FramesDue(nowTicks, ticksPerSecond);
            if (due == 0)
            {
                _pumpsWithNothingDue++;
                return 0;
            }

            if (_clock.ClampedLastCall)
            {
                // A stall long enough to owe more than the clamp allows. Said
                // out loud once per occurrence — silently swallowing time is
                // how a rate fault stays invisible, which is the entire lesson
                // of the bug this class exists to fix.
                Tracing.TraceLine("TxFramePump: fell behind by more than "
                    + TxSampleClock.MaxFramesPerCall + " frames and abandoned the excess"
                    + " rather than bursting it at the radio (" + _clock.FramesDroppedToClamp
                    + " frames dropped so far this transmission)", TraceLevel.Warning);
            }

            int sent = 0;
            for (int i = 0; i < due; i++)
            {
                if (!_running) break;
                // Silence, freshly, every frame — see the class remarks.
                Array.Clear(_buffer, 0, _floatsPerFrame);
                // The first false answer means stop, not retry: either the
                // producer was told to stop mid-frame, or the encoder failed,
                // and hammering a broken encoder eight more times for one
                // stall helps nobody.
                if (!_pipeline.Emit(_buffer, _floatsPerFrame, _sampleRate, _stillRunning)) break;
                sent++;
            }
            return sent;
        }

        /// <summary>
        /// What actually happened, in numbers that can disagree with what was
        /// intended. A line reporting the nominal rate would tell us nothing on
        /// the day it is wrong.
        /// </summary>
        public string DescribeRun(long nowTicks, long ticksPerSecond)
        {
            double realised = _clock.RealisedFramesPerSecond(nowTicks, ticksPerSecond);
            double nominal = 1000.0 / _clock.FrameMs;
            return _clock.FramesEmitted + " frames owed, " + _pipeline.FramesSent + " sent, "
                + realised.ToString("0.###") + " frames/sec realised against " + nominal.ToString("0.##")
                + " nominal, " + _pumps + " pumps (" + _pumpsWithNothingDue + " with nothing due), "
                + _clock.FramesDroppedToClamp + " frames dropped to the stall clamp, "
                + _pipeline.FramesAbandonedAtTeardown + " abandoned at teardown, "
                + _pipeline.EncodeFailures + " encode failures";
        }
    }
}
