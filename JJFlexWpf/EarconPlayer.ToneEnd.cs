using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace JJFlexWpf
{
    public static partial class EarconPlayer
    {
        /// <summary>The recorder's start tone: two 55 ms pips 35 ms apart, each decaying inside its own step.</summary>
        private static readonly (int freq, int ms)[] RecorderStartSteps = { (400, 55), (0, 35), (400, 55) };

        /// <summary>
        /// The start tone's samples, mono at <see cref="MixerSampleRate"/>:
        /// the very buffer <see cref="RecorderStartBeep"/> plays, from the same
        /// cache, so the length the timed tone reports is the length heard.
        /// </summary>
        internal static float[] RecorderStartBeepSamples() =>
            HeldOrRender(VoicedKey("decay", EarconVoices.Press, RecorderStartSteps, VolumeNormal),
                () => RenderVoicedDecayCore(EarconVoices.Press, RecorderStartSteps, VolumeNormal));

        /// <summary>
        /// Sound the recorder's start tone, exactly as <see cref="RecorderStartBeep"/>
        /// does, and report the moment it is audibly over, on the
        /// <see cref="Stopwatch"/> clock: null when it did not sound (the
        /// recorder beeps or every sound turned off).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Sprint 49 Track Q3 repair 1</b> (Codex's check of 83932446, C1).
        /// An audition take's file begins once this tone is over, and when that
        /// is depends on the alert output: its buffers hold 90 ms by default and
        /// up to 800 ms. So the end is read from the OUTPUT'S OWN POSITION, the
        /// way the sound timing lines read it (#855): at the mixer's first read
        /// of the tone, the audio the device already holds ahead of it is what
        /// it has been handed less what it reports played, and the tone's first
        /// sample is heard that long after the read; its last, the tone's
        /// length after that.
        /// </para>
        /// <para>
        /// When the device will not report its position, the end is ESTIMATED
        /// from the configured buffer, counted from the mixer's first read of
        /// the tone (repair 2; repair 1 counted from the hand-over, so a read
        /// that came late put the end before the tone could even begin): the
        /// read, plus the whole buffer chain, plus its length. That is the
        /// latest the chain lets it be heard, so it keeps the tone out of a file
        /// at the cost of up to one buffer of what follows. The trace calls it
        /// an estimate.
        /// </para>
        /// <para>
        /// <b>A tone the mixer never takes is WITHDRAWN, never assumed over
        /// (repair 2, Codex's re-check of ca39306c, C1a).</b> Repair 1 published
        /// an assumed end after a grace period and left the tone in the mixer,
        /// so an output that came back later sounded it into the take, 455.7 ms
        /// after the file had begun. Now, if the mixer has not read the tone
        /// within the chain, its length and <see cref="ToneEnd.NoReadGraceMs"/>,
        /// it is withdrawn: it reads as finished, the mixer drops it, and it can
        /// never sound. The end is then reported as NO tone, so the file begins
        /// at its first frame and nothing the operator said is lost. The
        /// withdrawal and the mixer's first read are one atomic choice, so the
        /// tone is either withdrawn whole or timed from its read.
        /// </para>
        /// <para>
        /// <b>With no alert mixer at all</b>, the plain beep's fallback (a
        /// system beep, not the rendered cadence) has no position to read and no
        /// way to be withdrawn. When it can sound, its end is ESTIMATED as
        /// <see cref="Radios.ChainAuditionTake.UnknownToneEndMs"/> after it was
        /// asked for, the take's allowance for a tone whose end it cannot read,
        /// so the file begins safely late. When it cannot sound (every sound
        /// rendered off), no tone is reported.
        /// </para>
        /// </remarks>
        public static Task<long?> RecorderStartBeepTimed()
        {
            if (!RecorderBeepsEnabled || !MasterAdmits) return Task.FromResult<long?>(null);
            float[] buffer = RecorderStartBeepSamples();
            if (AlertMixer == null)
            {
                // No alert mixer: the plain beep's own fallback, which cannot be
                // timed or withdrawn. Nothing sounds with rendering off.
                if (!Radios.OutputChannelRecorder.RenderEnabled) return Task.FromResult<long?>(null);
                long asked = Stopwatch.GetTimestamp();
                RecorderStartBeep();
                long estimate = ToneEnd.PlainBeepEstimate(asked, Stopwatch.Frequency);
                Trace.WriteLine("EarconPlayer: the recorder's start tone went to the system beep (no alert output); its end is ESTIMATED "
                    + Radios.ChainAuditionTake.UnknownToneEndMs + " ms after it was asked for, not read");
                return Task.FromResult<long?>(estimate);
            }
            BeginUngated(nameof(RecorderStartBeep));
            var end = new ToneEnd(buffer.Length, SampleRate, AlertOutputLatencyMs, Stopwatch.GetTimestamp(),
                () => SoundTiming.AlertDevice?.Invoke());
            try
            {
                AddToMixer(new ToneEnd.Probe(new RenderedSampleProvider(buffer), end));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.RecorderStartBeepTimed failed: {ex.Message}");
            }
            end.WatchForNoRead();
            return end.AudibleEnd;
        }

        /// <summary>
        /// When one tone handed to the alert mixer is audibly over (Track Q3
        /// repair 1). Settled once: at the mixer's first read of it, or, when
        /// the mixer never reads it in time, by withdrawing it, which reports no
        /// tone (repair 2).
        /// </summary>
        internal sealed class ToneEnd
        {
            /// <summary>How long, past the chain and the tone's length, a tone the mixer has not read is waited for before it is withdrawn.</summary>
            internal const int NoReadGraceMs = 500;

            private const int Pending = 0, Taken = 1, Withdrawn = 2;
            private int _state;

            private readonly int _toneFrames;
            private readonly int _rate;
            private readonly int _assumedLatencyMs;
            private readonly long _handedOver;
            private readonly Func<SoundTiming.DeviceSnapshot?> _device;
            private readonly TaskCompletionSource<long?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Timer? _noRead;

            /// <param name="toneFrames">The tone's length in frames at <paramref name="rate"/>.</param>
            /// <param name="rate">The mixer's rate.</param>
            /// <param name="assumedLatencyMs">The output's configured buffer chain, for the fallback.</param>
            /// <param name="handedOver">When the tone was handed to the mixer, on the Stopwatch clock.</param>
            /// <param name="device">The output's state at a mixer read; null when it has none.</param>
            internal ToneEnd(int toneFrames, int rate, int assumedLatencyMs, long handedOver, Func<SoundTiming.DeviceSnapshot?> device)
            {
                _toneFrames = Math.Max(0, toneFrames);
                _rate = rate > 0 ? rate : SampleRate;
                _assumedLatencyMs = Math.Max(0, assumedLatencyMs);
                _handedOver = handedOver;
                _device = device;
            }

            /// <summary>Completes with the moment the tone is audibly over.</summary>
            internal Task<long?> AudibleEnd => _done.Task;

            /// <summary>
            /// The end read from the output's position: <paramref name="queuedFrames"/>
            /// of audio were ahead of the tone when the mixer first read it at
            /// <paramref name="read"/>, so it is heard from then on for its length.
            /// </summary>
            internal static long FromDevice(long read, long queuedFrames, int toneFrames, int rate, long frequency) =>
                read + (long)Math.Round((double)(Math.Max(0, queuedFrames) + toneFrames) * frequency / rate);

            /// <summary>
            /// The end estimated from the configured chain when the output
            /// reports no position: from the mixer's first READ of the tone
            /// (never from the hand-over, which can be long before it), plus the
            /// whole chain, plus the tone.
            /// </summary>
            internal static long Estimated(long read, int latencyMs, int toneFrames, int rate, long frequency) =>
                read + (long)latencyMs * frequency / 1000 + (long)Math.Round((double)toneFrames * frequency / rate);

            /// <summary>The plain beep's end, which cannot be read: <see cref="Radios.ChainAuditionTake.UnknownToneEndMs"/> after it was asked for.</summary>
            internal static long PlainBeepEstimate(long asked, long frequency) =>
                asked + (long)Radios.ChainAuditionTake.UnknownToneEndMs * frequency / 1000;

            /// <summary>
            /// The mixer is reading the tone, on the output's thread. True to
            /// play it; false when it was withdrawn, so it plays nothing. The
            /// first true read settles its end.
            /// </summary>
            internal bool Take(long read)
            {
                int prior = Interlocked.CompareExchange(ref _state, Taken, Pending);
                if (prior == Withdrawn) return false;
                if (prior == Pending)
                {
                    // The end must be settled whatever happens here: once taken,
                    // the tone can no longer be withdrawn, so nothing else would.
                    try { FirstRead(read); }
                    catch (Exception)
                    {
                        Settle(Estimated(read, _assumedLatencyMs, _toneFrames, _rate, Stopwatch.Frequency),
                            "ESTIMATED from the buffer chain after the first read faulted");
                    }
                }
                return true;
            }

            /// <summary>
            /// Withdraw the tone if the mixer has not read any of it: it then
            /// can never sound, and its end is reported as no tone. False when
            /// the mixer already took it (its read settles the end).
            /// </summary>
            internal bool Withdraw()
            {
                if (Interlocked.CompareExchange(ref _state, Withdrawn, Pending) != Pending) return false;
                Settle(null, "withdrawn: the mixer never took it, so it can never sound, and no tone is reported");
                return true;
            }

            /// <summary>True once the tone was withdrawn.</summary>
            internal bool IsWithdrawn => Volatile.Read(ref _state) == Withdrawn;

            /// <summary>The mixer read the tone for the first time, on the output's thread.</summary>
            internal void FirstRead(long read)
            {
                long end;
                string how;
                SoundTiming.DeviceSnapshot? snap = null;
                try { snap = _device(); } catch { }
                if (snap is { } s && s.PlayedFrames >= 0 && s.ProducedFrames >= s.PlayedFrames)
                {
                    long queued = s.ProducedFrames - s.PlayedFrames;
                    end = FromDevice(read, queued, _toneFrames, _rate, Stopwatch.Frequency);
                    how = "from the output's position, " + (queued * 1000 / _rate) + " ms queued ahead of it";
                }
                else
                {
                    end = Estimated(read, _assumedLatencyMs, _toneFrames, _rate, Stopwatch.Frequency);
                    how = "ESTIMATED from the " + _assumedLatencyMs + " ms buffer chain after the mixer's first read"
                        + " (the output reports no position), " + ((read - _handedOver) * 1000 / Stopwatch.Frequency) + " ms after it was handed over";
                }
                Settle(end, how);
            }

            /// <summary>
            /// Withdraw the tone if the mixer has not read it within the chain,
            /// its length and <see cref="NoReadGraceMs"/> (repair 2).
            /// </summary>
            internal void WatchForNoRead()
            {
                if (_done.Task.IsCompleted) return;
                int wait = _assumedLatencyMs + (int)((long)_toneFrames * 1000 / _rate) + NoReadGraceMs;
                var timer = new Timer(_ => Withdraw(), null, Timeout.Infinite, Timeout.Infinite);
                _noRead = timer;
                timer.Change(wait, Timeout.Infinite);
            }

            private void Settle(long? end, string how)
            {
                if (!_done.TrySetResult(end)) return;
                _noRead?.Dispose();
                if (end is not long e)
                {
                    Trace.WriteLine("EarconPlayer: the recorder's start tone was " + how);
                    return;
                }
                long now = Stopwatch.GetTimestamp();
                Trace.WriteLine("EarconPlayer: the recorder's start tone is audibly over "
                    + (e >= now ? (e - now) * 1000 / Stopwatch.Frequency + " ms from now" : (now - e) * 1000 / Stopwatch.Frequency + " ms ago")
                    + ", " + how);
            }

            /// <summary>
            /// Passes the tone through untouched, and tells its
            /// <see cref="ToneEnd"/> of the mixer's first read; once the tone is
            /// withdrawn, it reads as finished, so the mixer drops it unheard.
            /// </summary>
            internal sealed class Probe : ISampleProvider
            {
                private readonly ISampleProvider _source;
                private readonly ToneEnd _end;

                internal Probe(ISampleProvider source, ToneEnd end)
                {
                    _source = source;
                    _end = end;
                }

                public WaveFormat WaveFormat => _source.WaveFormat;

                public int Read(Span<float> buffer)
                {
                    bool play;
                    try { play = _end.Take(Stopwatch.GetTimestamp()); }
                    catch { play = !_end.IsWithdrawn; }
                    return play ? _source.Read(buffer) : 0;
                }
            }
        }
    }
}
