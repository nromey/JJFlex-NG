using System;

namespace Radios;

/// <summary>
/// Decides how many audio frames a self-clocked transmit source owes the radio,
/// from elapsed time rather than from a timer's cadence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Until 2026-08-24 the transmit stream was paced by the
/// capture device: <c>OpenOpus(input, txRate, sendOpusInput)</c> binds one
/// pipeline — capture callback fires, tone or mic fills the buffer, Opus
/// encodes, the frame goes to the radio. For a MICROPHONE that is correct; the
/// mic is the source and its clock is the natural one. For a SYNTHESISED tone it
/// is incoherent: samples computed from a formula have no clock, and we handed
/// them one belonging to a device that contributes nothing to the signal.
/// </para>
/// <para>
/// <b>What the radio actually needs.</b> <c>TXRemoteAudioStream.AddTXData</c>
/// sends immediately — no buffering, no pacing — so the cadence we call it at IS
/// the cadence on the wire. And the packets carry NO timestamp: the header flags
/// declare a sample-count timestamp, but <c>TimestampInt</c> and
/// <c>TimestampFrac</c> are never assigned, so twelve bytes of zeros ship with
/// every frame. The radio therefore has nothing to reconstruct our timing from
/// except arrival, which means it is running a jitter buffer.
/// </para>
/// <para>
/// <b>And that decides the design.</b> A jitter buffer forgives arrival jitter
/// and cannot forgive sustained rate error. Being three milliseconds late with a
/// frame is absorbed. Producing 999 frames where 1000 were due, every second,
/// forever, is not — the buffer walks to one end and something has to give. That
/// is why the fault Noel hears is a metronome at roughly 750 ms rather than
/// something ragged: a CONSTANT rate error produces a PERIODIC correction.
/// </para>
/// <para>
/// So this is an accumulator against a monotonic reference, not a periodic
/// timer. Ask it how many frames are owed; it answers from total elapsed time,
/// never from the interval since the last call. A coarse Windows timer that
/// averages the right rate is fine. A perfectly smooth one that averages 0.3
/// percent slow is not. Jitter absorbs; drift never accumulates.
/// </para>
/// <para>
/// Same reasoning as the smooth-tuning design: track the error against where
/// you SHOULD be, not the delta since last time. An accumulator of deltas
/// accumulates their errors too.
/// </para>
/// </remarks>
public sealed class TxSampleClock
{
    private readonly int _sampleRate;
    private readonly int _samplesPerFrame;

    /// <summary>Frames handed out since <see cref="Start"/>.</summary>
    public long FramesEmitted { get; private set; }

    /// <summary>Elapsed reference time at the last <see cref="Start"/>, in ticks.</summary>
    private long _startedAtTicks;
    private bool _running;

    /// <summary>
    /// Largest catch-up this will ever ask for in one go. A machine that stalls
    /// for two seconds must not then dump two hundred frames at the radio in a
    /// burst — that is a worse fault than the gap it is trying to repair, and
    /// the jitter buffer would have to discard most of it anyway.
    /// </summary>
    public const int MaxFramesPerCall = 8;

    /// <summary>
    /// True when the last <see cref="FramesDue"/> hit <see cref="MaxFramesPerCall"/>
    /// and dropped frames rather than bursting. The caller should say so once —
    /// silently swallowing time is how a rate fault stays invisible.
    /// </summary>
    public bool ClampedLastCall { get; private set; }

    /// <summary>Total frames abandoned to the clamp since Start.</summary>
    public long FramesDroppedToClamp { get; private set; }

    /// <param name="sampleRate">Encoder sample rate in Hz.</param>
    /// <param name="samplesPerFrame">
    /// Samples per channel in one Opus frame. At 10 ms — the encoder delay the
    /// TX channel is built with — that is one hundredth of the sample rate.
    /// </param>
    public TxSampleClock(int sampleRate, int samplesPerFrame)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samplesPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerFrame));
        _sampleRate = sampleRate;
        _samplesPerFrame = samplesPerFrame;
    }

    /// <summary>Samples per channel in one frame.</summary>
    public int SamplesPerFrame => _samplesPerFrame;

    /// <summary>Nominal frame duration in milliseconds, for tracing.</summary>
    public double FrameMs => 1000.0 * _samplesPerFrame / _sampleRate;

    /// <summary>
    /// Begin, or begin again. Resets the accumulator: a new transmission owes
    /// nothing for the time it spent not transmitting.
    /// </summary>
    /// <param name="nowTicks">
    /// A monotonic reference in <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// Passed in rather than read here so the clock is testable without waiting.
    /// </param>
    public void Start(long nowTicks)
    {
        _startedAtTicks = nowTicks;
        FramesEmitted = 0;
        FramesDroppedToClamp = 0;
        ClampedLastCall = false;
        _running = true;
    }

    /// <summary>Stop. <see cref="FramesDue"/> returns zero until the next Start.</summary>
    public void Stop() => _running = false;

    /// <summary>True between Start and Stop.</summary>
    public bool Running => _running;

    /// <summary>
    /// How many frames the radio is owed right now.
    /// </summary>
    /// <param name="nowTicks">Monotonic reference, same base as Start.</param>
    /// <param name="ticksPerSecond">
    /// <see cref="System.Diagnostics.Stopwatch.Frequency"/>. Passed in so a test
    /// can use a round number instead of the machine's.
    /// </param>
    /// <remarks>
    /// Computed from TOTAL elapsed time against total frames already emitted.
    /// Deliberately not "time since the last call divided by the frame period" —
    /// that form loses a fraction of a frame on every call and the losses add up
    /// into exactly the drift this class exists to prevent.
    /// </remarks>
    public int FramesDue(long nowTicks, long ticksPerSecond)
    {
        ClampedLastCall = false;
        if (!_running || ticksPerSecond <= 0) return 0;

        long elapsed = nowTicks - _startedAtTicks;
        if (elapsed <= 0) return 0;

        // Frames that SHOULD have been produced by now, from the top.
        // long arithmetic before the divide keeps the fraction that a
        // per-call delta would have thrown away.
        long shouldHave = elapsed * _sampleRate / (ticksPerSecond * (long)_samplesPerFrame);

        long owed = shouldHave - FramesEmitted;
        if (owed <= 0) return 0;

        if (owed > MaxFramesPerCall)
        {
            // Abandon the excess rather than burst. Count it BEFORE emitting so
            // the accumulator does not keep trying to repay a debt we have
            // deliberately written off.
            long dropped = owed - MaxFramesPerCall;
            FramesDroppedToClamp += dropped;
            FramesEmitted += dropped;
            ClampedLastCall = true;
            owed = MaxFramesPerCall;
        }

        FramesEmitted += owed;
        return (int)owed;
    }

    /// <summary>
    /// Realised frame rate since Start, in frames per second — what we actually
    /// achieved, not what we intended. A number to put in the trace rather than
    /// a claim to trust.
    /// </summary>
    public double RealisedFramesPerSecond(long nowTicks, long ticksPerSecond)
    {
        if (ticksPerSecond <= 0) return 0;
        long elapsed = nowTicks - _startedAtTicks;
        if (elapsed <= 0) return 0;
        return FramesEmitted * (double)ticksPerSecond / elapsed;
    }
}
