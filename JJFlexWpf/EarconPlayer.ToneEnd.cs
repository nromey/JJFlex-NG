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
        /// way the sound timing lines read it (#855): at the read that emits
        /// the tone's LAST sample (repair 3; repair 2 predicted the whole tone
        /// at the first read), the audio the device already holds ahead of it
        /// is what it has been handed less what it reports played, and that
        /// read's samples are heard after it.
        /// </para>
        /// <para>
        /// When the device will not report its position, the end is ESTIMATED
        /// from the configured buffer, counted from that same last read (repair
        /// 1 counted from the hand-over, so a read that came late put the end
        /// before the tone could even begin): the read, plus the whole buffer
        /// chain, plus what it emitted. That is the latest the chain lets it be
        /// heard, so it keeps the tone out of a file at the cost of up to one
        /// buffer of what follows. The trace calls it an estimate.
        /// </para>
        /// <para>
        /// <b>A tone taken but not finished in time is CUT (repair 3, Codex's
        /// re-check of b6543ac1, C1a)</b>, so a stalled output can never resume
        /// it into the take; the end then counts from the cut. See
        /// <see cref="ToneEnd"/>.
        /// </para>
        /// <para>
        /// <b>The end is published at the output's handoff (repair 4, Codex's
        /// re-check 3 of 46a481c7, C1a)</b>: only once the device read that
        /// carried the tone's last sample (or its cut) has returned from the
        /// mixer and been counted, never from inside the mixer's read. See
        /// <see cref="CountingSampleProvider"/>.
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
        /// tone is either withdrawn whole or taken, and a taken tone is timed
        /// from its last read or its cut.
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
                () => SoundTiming.AlertDevice?.Invoke(), afterHandoff: AlertHandoff);
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
        /// repair 1). Settled once: by the read that emits its LAST sample; by
        /// cutting it when that has not happened in time (repair 3); or, when
        /// the mixer never reads it in time, by withdrawing it, which reports
        /// no tone (repair 2).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Repair 3 (Codex's re-check of b6543ac1, C1a).</b> Repair 2 settled
        /// the end at the mixer's FIRST read, as a prediction for the whole tone,
        /// and a taken tone could not be withdrawn; so a read of 10 ms followed by
        /// a stall left the rest in this provider, and the mixer emitted 8,714
        /// tone samples about 125 ms after the file had begun. Now the end counts
        /// only from the provider's own final sample: it is settled by the read
        /// that emits it, never before. A tone that has not emitted its last
        /// sample by the chain, its length and <see cref="NoReadGraceMs"/> after
        /// its first read is CUT: from then on it reads as finished, and the end
        /// counts from the cut plus the same output allowance.
        /// </para>
        /// <para>
        /// A read and a cut are one choice under one lock, so a cut waits for a
        /// read already in flight and reads the clock only after it: every tone
        /// sample was handed out before that reading. Each end reads the
        /// output's position FIRST and the clock after it, so a pause between
        /// the two can only make the end later, never earlier.
        /// </para>
        /// <para>
        /// <b>Repair 4 (Codex's re-check 3 of 46a481c7, C1a).</b> Repair 3
        /// published the end inside the source's read, before the samples went
        /// back up through the mixer, so a stall there (Codex held the end's
        /// own trace line) let 3,928 tone samples leave our mixer 130 ms after
        /// the boundary. Now neither the last read nor the cut publishes: each
        /// hands the settling to the output's handoff, which runs it once the
        /// device read in flight has returned and been counted, or at once
        /// when none is. Nothing between the final source read and that
        /// handoff publishes, and the end's trace line is written off the
        /// output's thread, after the end is published.
        /// </para>
        /// </remarks>
        internal sealed class ToneEnd
        {
            /// <summary>How long, past the chain and the tone's length, a tone is waited for: unread, before it is withdrawn; partly read, before it is cut.</summary>
            internal const int NoReadGraceMs = 500;

            private const int Pending = 0, Taken = 1, Withdrawn = 2, Finished = 3, Cut = 4;
            private readonly object _gate = new object();
            private int _state;              // under _gate
            private long _firstRead;         // under _gate
            private long _emittedFrames;     // under _gate
            private int _lastReadFrames;     // under _gate: the frames of the most recent read that emitted any

            private readonly int _toneFrames;
            private readonly int _rate;
            private readonly int _assumedLatencyMs;
            private readonly long _handedOver;
            private readonly Func<SoundTiming.DeviceSnapshot?> _device;
            private readonly Func<long> _clock;
            private readonly Action<Action>? _afterHandoff;
            private readonly TaskCompletionSource<long?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Timer? _noRead;

            /// <param name="toneFrames">The tone's length in frames at <paramref name="rate"/>.</param>
            /// <param name="rate">The mixer's rate.</param>
            /// <param name="assumedLatencyMs">The output's configured buffer chain, for the fallback.</param>
            /// <param name="handedOver">When the tone was handed to the mixer, on the Stopwatch clock.</param>
            /// <param name="device">The output's state at a mixer read; null when it has none.</param>
            /// <param name="clock">The Stopwatch clock; a test may give a fixed one.</param>
            /// <param name="afterHandoff">
            /// The output's handoff (<see cref="CountingSampleProvider.AfterThisHandoff"/>):
            /// the end is settled through it, once the read in flight has returned
            /// and been counted. Null when no output layer can be watched (an
            /// offline render, or a test reading the mixer bare): the end is then
            /// settled at once, with the last read's frames added on top.
            /// </param>
            internal ToneEnd(int toneFrames, int rate, int assumedLatencyMs, long handedOver, Func<SoundTiming.DeviceSnapshot?> device,
                Func<long>? clock = null, Action<Action>? afterHandoff = null)
            {
                _afterHandoff = afterHandoff;
                _toneFrames = Math.Max(0, toneFrames);
                _rate = rate > 0 ? rate : SampleRate;
                _assumedLatencyMs = Math.Max(0, assumedLatencyMs);
                _handedOver = handedOver;
                _device = device;
                _clock = clock ?? Stopwatch.GetTimestamp;
            }

            /// <summary>Completes with the moment the tone is audibly over.</summary>
            internal Task<long?> AudibleEnd => _done.Task;

            /// <summary>
            /// The end read from the output's position: <paramref name="queuedFrames"/>
            /// of audio were ahead of the tone's last samples at <paramref name="read"/>,
            /// and those <paramref name="lastFrames"/> are heard after them.
            /// </summary>
            internal static long FromDevice(long read, long queuedFrames, int lastFrames, int rate, long frequency) =>
                read + (long)Math.Round((double)(Math.Max(0, queuedFrames) + lastFrames) * frequency / rate);

            /// <summary>
            /// The end estimated from the configured chain when the output
            /// reports no position: from the read that handed out the tone's last
            /// samples (or the cut), plus the whole chain, plus those samples.
            /// </summary>
            internal static long Estimated(long read, int latencyMs, int lastFrames, int rate, long frequency) =>
                read + (long)latencyMs * frequency / 1000 + (long)Math.Round((double)lastFrames * frequency / rate);

            /// <summary>The plain beep's end, which cannot be read: <see cref="Radios.ChainAuditionTake.UnknownToneEndMs"/> after it was asked for.</summary>
            internal static long PlainBeepEstimate(long asked, long frequency) =>
                asked + (long)Radios.ChainAuditionTake.UnknownToneEndMs * frequency / 1000;

            /// <summary>
            /// The mixer is about to read the tone. True to play it; false once
            /// it was withdrawn, cut or finished, so it plays nothing. The first
            /// true answer makes it taken: it can no longer be withdrawn, only
            /// finished or cut. Settles nothing (repair 3).
            /// </summary>
            internal bool Take(long read)
            {
                lock (_gate) return TakeLocked(read);
            }

            private bool TakeLocked(long read)
            {
                if (_state == Pending) { _state = Taken; _firstRead = read; return true; }
                return _state == Taken;
            }

            /// <summary>
            /// One read of the tone, on the output's thread, under the same lock
            /// as a cut: plays nothing once the tone is withdrawn, cut or
            /// finished, and settles the end at the read that emits its last
            /// sample.
            /// </summary>
            internal int Read(ISampleProvider source, Span<float> buffer)
            {
                int n, frames;
                bool last;
                lock (_gate)
                {
                    if (!TakeLocked(_clock())) return 0;
                    n = source.Read(buffer);
                    frames = n / Math.Max(1, source.WaveFormat.Channels);
                    _emittedFrames += frames;
                    if (frames > 0) _lastReadFrames = frames;
                    last = _emittedFrames >= _toneFrames || n < buffer.Length;
                    if (last) _state = Finished;
                }
                // Repair 4: never published from here, inside the mixer's read.
                if (last) Publish(frames, "after the read that emitted its last sample");
                return n;
            }

            /// <summary>
            /// Withdraw the tone if the mixer has not read any of it: it then
            /// can never sound, and its end is reported as no tone. False when
            /// the mixer already took it.
            /// </summary>
            internal bool Withdraw()
            {
                lock (_gate)
                {
                    if (_state != Pending) return false;
                    _state = Withdrawn;
                }
                Settle(null, "withdrawn: the mixer never took it, so it can never sound, and no tone is reported");
                return true;
            }

            /// <summary>
            /// Cut a tone the mixer took but has not finished (repair 3): from
            /// now on it reads as finished, so nothing more of it can sound, and
            /// the end counts from this cut. Waits for a read in flight. False
            /// when it was not taken, or already finished.
            /// </summary>
            internal bool CutNow()
            {
                int frames;
                lock (_gate)
                {
                    if (_state != Taken) return false;
                    _state = Cut;
                    frames = _lastReadFrames;
                }
                Publish(frames, "CUT: its last sample had not been emitted in time, so the output plays nothing more of it, and the end counts from the cut");
                return true;
            }

            /// <summary>True once the tone was withdrawn.</summary>
            internal bool IsWithdrawn { get { lock (_gate) return _state == Withdrawn; } }

            /// <summary>True once the tone was cut.</summary>
            internal bool IsCut { get { lock (_gate) return _state == Cut; } }

            /// <summary>
            /// No tone sample can be handed out any more: settle the end at the
            /// output's handoff, once the device read in flight has returned and
            /// been counted (repair 4), or at once when no output layer is watched.
            /// </summary>
            private void Publish(int lastFrames, string at)
            {
                if (_afterHandoff is { } after) after(() => SettleAfter(lastFrames, at + ", at the output's handoff", counted: true));
                else SettleAfter(lastFrames, at, counted: false);
            }

            /// <summary>
            /// Settle the end once no tone sample can be handed out any more (its
            /// last read has returned, or the cut is made); the most recent read
            /// handed out <paramref name="lastFrames"/>. The output's position is
            /// read FIRST and the clock AFTER it: whatever the device plays
            /// between the two only shortens what it holds, so the pair can only
            /// put the end later. <paramref name="counted"/>: the output has
            /// already counted those frames, so its position includes them.
            /// </summary>
            private void SettleAfter(int lastFrames, string at, bool counted)
            {
                long end;
                string how;
                try
                {
                    SoundTiming.DeviceSnapshot? snap = null;
                    try { snap = _device(); } catch { }
                    long now = _clock();
                    if (snap is { } s && s.PlayedFrames >= 0 && s.ProducedFrames >= s.PlayedFrames)
                    {
                        long queued = s.ProducedFrames - s.PlayedFrames;
                        // Settled at the handoff, the last read's frames are in
                        // what the device was handed. Otherwise they are counted on
                        // top: later, never earlier.
                        end = FromDevice(now, queued, counted ? 0 : lastFrames, _rate, Stopwatch.Frequency);
                        how = at + ", from the output's position, " + (queued * 1000 / _rate) + " ms queued ahead of it";
                    }
                    else
                    {
                        end = Estimated(now, _assumedLatencyMs, lastFrames, _rate, Stopwatch.Frequency);
                        how = at + ", ESTIMATED from the " + _assumedLatencyMs + " ms buffer chain (the output reports no position)";
                    }
                    long first;
                    lock (_gate) first = _firstRead;
                    how += "; its first read came " + ((first - _handedOver) * 1000 / Stopwatch.Frequency) + " ms after it was handed over";
                }
                catch (Exception)
                {
                    end = Estimated(_clock(), _assumedLatencyMs, lastFrames, _rate, Stopwatch.Frequency);
                    how = at + ", ESTIMATED from the buffer chain after reading the output faulted";
                }
                Settle(end, how);
            }

            /// <summary>Waited from the hand-over (unread) or from the first read (taken), before a withdrawal or a cut.</summary>
            private long BoundTicks =>
                ((long)_assumedLatencyMs + (long)_toneFrames * 1000 / _rate + NoReadGraceMs) * Stopwatch.Frequency / 1000;

            /// <summary>
            /// Arm the bound: a tone the mixer has not read by the chain, its
            /// length and <see cref="NoReadGraceMs"/> after the hand-over is
            /// withdrawn (repair 2); one it took but has not finished by the same
            /// span after its first read is cut (repair 3).
            /// </summary>
            internal void WatchForNoRead()
            {
                if (_done.Task.IsCompleted) return;
                var timer = new Timer(_ => Expire(), null, Timeout.Infinite, Timeout.Infinite);
                _noRead = timer;
                Arm(timer, _handedOver + BoundTicks);
                if (_done.Task.IsCompleted) timer.Dispose();   // settled while it was being armed
            }

            private void Arm(Timer timer, long due)
            {
                long ms = Math.Max(1, (due - _clock()) * 1000 / Stopwatch.Frequency + 1);
                try { timer.Change(ms, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }

            /// <summary>The bound came: withdraw an unread tone, cut a taken one, or wait on for one taken late.</summary>
            internal void Expire()
            {
                if (Withdraw()) return;
                long due;
                lock (_gate)
                {
                    if (_state != Taken) return;
                    due = _firstRead + BoundTicks;
                }
                if (_clock() < due)
                {
                    if (_noRead is { } timer) Arm(timer, due);
                    return;
                }
                CutNow();
            }

            private void Settle(long? end, string how)
            {
                if (!_done.TrySetResult(end)) return;
                _noRead?.Dispose();
                // The trace line is written off this thread (repair 4): settling
                // can run on the output's thread, and nothing may hold the
                // device's read once its end is published.
                string line;
                if (end is not long e) line = "EarconPlayer: the recorder's start tone was " + how;
                else
                {
                    long now = Stopwatch.GetTimestamp();
                    line = "EarconPlayer: the recorder's start tone is audibly over "
                        + (e >= now ? (e - now) * 1000 / Stopwatch.Frequency + " ms from now" : (now - e) * 1000 / Stopwatch.Frequency + " ms ago")
                        + ", " + how;
                }
                ThreadPool.QueueUserWorkItem(static l => { try { Trace.WriteLine((string)l!); } catch { } }, line);
            }

            /// <summary>
            /// Passes the tone through untouched, through its
            /// <see cref="ToneEnd"/>: once the tone is withdrawn, cut or
            /// finished, it reads as finished, so the mixer drops it.
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

                public int Read(Span<float> buffer) => _end.Read(_source, buffer);
            }
        }
    }
}
