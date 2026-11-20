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
        /// When the device will not report its position, the end is ASSUMED
        /// from the configured buffer: the moment the tone was handed over,
        /// plus the whole buffer chain, plus its length. That is the latest it
        /// could be heard, so it keeps the tone out of a file at the cost of up
        /// to one buffer of what follows. The same assumption stands if the
        /// mixer never reads the tone within the chain, its length and half a
        /// second, and the trace says so.
        /// </para>
        /// </remarks>
        public static Task<long?> RecorderStartBeepTimed()
        {
            if (!RecorderBeepsEnabled || !MasterAdmits) return Task.FromResult<long?>(null);
            float[] buffer = RecorderStartBeepSamples();
            if (AlertMixer == null)
            {
                // No alert mixer: whatever fallback the plain beep has, with its
                // end assumed from the default chain.
                long asked = Stopwatch.GetTimestamp();
                RecorderStartBeep();
                return Task.FromResult<long?>(ToneEnd.Assumed(asked, AlertOutputLatencyMs, buffer.Length, SampleRate, Stopwatch.Frequency));
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
        /// repair 1). Settled once, at the mixer's first read of it or by its
        /// fallback.
        /// </summary>
        internal sealed class ToneEnd
        {
            /// <summary>How long past its assumed end a tone the mixer never read is waited for before the end is assumed.</summary>
            internal const int NoReadGraceMs = 500;

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

            /// <summary>The end assumed from the configured chain: handed over, plus the whole chain, plus the tone.</summary>
            internal static long Assumed(long handedOver, int latencyMs, int toneFrames, int rate, long frequency) =>
                handedOver + (long)latencyMs * frequency / 1000 + (long)Math.Round((double)toneFrames * frequency / rate);

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
                    end = Assumed(_handedOver, _assumedLatencyMs, _toneFrames, _rate, Stopwatch.Frequency);
                    how = "assumed from the " + _assumedLatencyMs + " ms buffer chain (the output reports no position)";
                }
                Settle(end, how);
            }

            /// <summary>Start the fallback for a tone the mixer never reads.</summary>
            internal void WatchForNoRead()
            {
                if (_done.Task.IsCompleted) return;
                int wait = _assumedLatencyMs + (int)((long)_toneFrames * 1000 / _rate) + NoReadGraceMs;
                _noRead = new Timer(_ => Settle(Assumed(_handedOver, _assumedLatencyMs, _toneFrames, _rate, Stopwatch.Frequency),
                    "assumed: the mixer never read the tone"), null, wait, Timeout.Infinite);
            }

            private void Settle(long end, string how)
            {
                if (!_done.TrySetResult(end)) return;
                _noRead?.Dispose();
                long now = Stopwatch.GetTimestamp();
                Trace.WriteLine("EarconPlayer: the recorder's start tone is audibly over "
                    + (end >= now ? (end - now) * 1000 / Stopwatch.Frequency + " ms from now" : (now - end) * 1000 / Stopwatch.Frequency + " ms ago")
                    + ", " + how);
            }

            /// <summary>Passes the tone through untouched, and tells its <see cref="ToneEnd"/> of the mixer's first read.</summary>
            internal sealed class Probe : ISampleProvider
            {
                private readonly ISampleProvider _source;
                private readonly ToneEnd _end;
                private int _read;

                internal Probe(ISampleProvider source, ToneEnd end)
                {
                    _source = source;
                    _end = end;
                }

                public WaveFormat WaveFormat => _source.WaveFormat;

                public int Read(Span<float> buffer)
                {
                    if (Interlocked.Exchange(ref _read, 1) == 0)
                    {
                        try { _end.FirstRead(Stopwatch.GetTimestamp()); } catch { }
                    }
                    return _source.Read(buffer);
                }
            }
        }
    }
}
