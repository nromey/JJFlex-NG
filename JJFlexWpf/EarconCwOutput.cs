using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JJFlexWpf
{
    /// <summary>
    /// Speaker-based CW notification output. Each sequence is rendered as one
    /// sample-accurate <see cref="ConcatenatingSampleProvider"/> (sine +
    /// raised-cosine envelope) and played through a single-consumer FIFO
    /// queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The queue exists to serialize rapid notification events (AS on slow
    /// connect, BT on connected, mode-change Morse right after BT, etc.).
    /// The prior implementation cancelled any in-flight sequence at the
    /// start of each new Play call, but the alert mixer has a roughly
    /// 50 ms buffer window before playback begins — a second event fired
    /// in that window would cancel the first before any audio reached the
    /// speaker. On a real connect sequence only SK (the last event) ever
    /// played. The queue fixes that by playing every sequence to
    /// completion and dequeuing the next one. See BUG-057 and the
    /// "Cancellation — and why the next revision replaces it with a queue"
    /// section of <c>docs/planning/design/cw-keying-design.md</c>.
    /// </para>
    /// <para>
    /// The same primitive is the foundation for future on-air CW message
    /// send, iambic keyer element streams, and code-practice-tutor pacing —
    /// each of those will enqueue CwElement sequences and let the consumer
    /// loop drain them in order at PARIS timing.
    /// </para>
    /// <para>
    /// <see cref="Cancel"/> is retained for shutdown-style interrupts (app
    /// close, user-initiated stop): it disposes the in-flight handle and
    /// drains any pending queue items as cancelled. Normal Play calls
    /// never cancel — they enqueue and await their own completion.
    /// </para>
    /// </remarks>
    public class EarconCwOutput : ICwNotificationOutput, IDisposable
    {
        private readonly Channel<QueuedSequence> _queue =
            Channel.CreateUnbounded<QueuedSequence>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _consumerLoop;

        private IDisposable? _currentHandle;
        private readonly object _lock = new();
        private int _outstanding;

        /// <summary>
        /// True while a sequence is playing OR waiting to play.
        ///
        /// Exists so shutdown can let a character finish instead of cutting it
        /// mid-element. Tearing the audio stack down while CW was keying
        /// truncated it audibly - reported 2026-08-18 - and a half-sent
        /// character is worse than a slightly slower exit, because an operator
        /// cannot tell a clipped exit from a crash.
        /// </summary>
        public bool IsBusy => System.Threading.Volatile.Read(ref _outstanding) > 0;

        /// <summary>
        /// Wait for in-flight CW to drain, up to <paramref name="maxWaitMs"/>.
        /// Returns true when it drained, false when the deadline won.
        ///
        /// Bounded on purpose: a wedged audio device must never be able to stop
        /// the application closing. A truncated character is a papercut; an
        /// exit that hangs is a support call.
        /// </summary>
        public bool WaitForIdle(int maxWaitMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
            while (IsBusy && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(20);
            }
            return !IsBusy;
        }

        public EarconCwOutput()
        {
            _consumerLoop = Task.Run(ConsumerLoop);
        }

        public Task PlayElementsAsync(
            IReadOnlyList<CwElement> elements,
            int sidetoneHz,
            float volume,
            int riseFallMs,
            MeterVoice? markVoice,
            CancellationToken ct)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            if (elements.Count == 0) return Task.CompletedTask;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new QueuedSequence(elements, sidetoneHz, volume, riseFallMs, markVoice, ct, tcs);

            System.Threading.Interlocked.Increment(ref _outstanding);
            if (!_queue.Writer.TryWrite(item))
            {
                // Writer is completed (we're being disposed) — treat as cancelled.
                // Traced because this is a SUPPRESSION path: the caller's Task
                // resolves instantly and no audio will ever exist, which from the
                // operator's seat is indistinguishable from a working play call.
                Trace.WriteLine("EarconCwOutput.PlayElementsAsync: queue writer completed — sequence dropped as cancelled");
                System.Threading.Interlocked.Decrement(ref _outstanding);
                tcs.TrySetCanceled();
            }
            return tcs.Task;
        }

        /// <summary>
        /// Shutdown-style interrupt: kill the in-flight sequence and drop any
        /// queued-but-not-yet-playing sequences. New Play calls made after
        /// this will still enqueue normally — the consumer loop is not
        /// terminated. Use <see cref="Dispose"/> to shut down the output
        /// entirely.
        /// </summary>
        public void Cancel()
        {
            IDisposable? h;
            lock (_lock) { h = _currentHandle; _currentHandle = null; }
            try { h?.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"EarconCwOutput.Cancel: dispose in-flight: {ex.Message}"); }

            while (_queue.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetCanceled();
            }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            _shutdown.Cancel();
            try { _consumerLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _shutdown.Dispose();
        }

        private async Task ConsumerLoop()
        {
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await PlayOne(item).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Single decrement point. PlayOne has several early
                        // returns; counting here means none of them can leak a
                        // permanently-busy output that would make shutdown wait
                        // out its full deadline every time.
                        System.Threading.Interlocked.Decrement(ref _outstanding);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconCwOutput.ConsumerLoop: {ex.Message}");
            }
        }

        private async Task PlayOne(QueuedSequence item)
        {
            if (item.CallerToken.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.CallerToken);
                return;
            }

            // #171 silent verification channel: with render off there is no
            // mixer and nothing to hear, so don't burn wall-clock time pacing
            // a waveform that doesn't exist - a 20 WPM prosign is ~1.5 s per
            // event, which is exactly the settle-window tax the silent channel
            // exists to kill. The notification was already recorded at the
            // MorseNotifier level. (When render is ON but earcons are gated
            // off, the wait still runs - production timing is untouched.)
            if (!Radios.OutputChannelRecorder.RenderEnabled)
            {
                item.Completion.TrySetResult();
                return;
            }

            int totalMs = 0;
            var providers = new List<ISampleProvider>(item.Elements.Count);
            int sr = EarconPlayer.MixerSampleRate;

            foreach (var el in item.Elements)
            {
                if (el.DurationMs <= 0) continue;
                totalMs += el.DurationMs;
                if (el.Type == CwElementType.Mark)
                {
                    providers.Add(new CwToneSampleProvider(
                        sr, item.SidetoneHz, el.DurationMs, item.RiseFallMs, item.Volume,
                        item.MarkVoice));
                }
                else
                {
                    providers.Add(new SilenceProvider(new WaveFormat(sr, 1))
                        .ToSampleProvider()
                        .Take(TimeSpan.FromMilliseconds(el.DurationMs)));
                }
            }

            if (providers.Count == 0)
            {
                item.Completion.TrySetResult();
                return;
            }

            var concat = new ConcatenatingSampleProvider(providers);
            IDisposable handle;
            try
            {
                handle = EarconPlayer.SubmitCwSequence(concat);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconCwOutput.PlayOne: submit failed: {ex.Message}");
                item.Completion.TrySetResult();
                return;
            }

            lock (_lock) { _currentHandle = handle; }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                item.CallerToken, _shutdown.Token);

            try
            {
                await WaitForDrain(handle, totalMs, linked.Token).ConfigureAwait(false);
                item.Completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                try { handle.Dispose(); } catch { }
                item.Completion.TrySetCanceled();
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_currentHandle, handle))
                        _currentHandle = null;
                }
            }
        }

        // ── The completion contract, and why a computed duration was wrong ──
        //
        // Sprint 32 Track H (H4a). This method used to be three lines:
        //
        //     int waitMs = totalMs + 50;
        //     await Task.Delay(waitMs, linked.Token);
        //     item.Completion.TrySetResult();
        //
        // It resolved on a COMPUTED duration and never asked the device whether
        // anything had actually been heard. That is why the exit farewell lost
        // its final dit no matter how generous the caller's timeout was:
        // ApplicationEvents' Wait(5000) was being SATISFIED EARLY rather than
        // expiring, and the next statements — EarconPlayer.Dispose() and
        // ScreenReaderOutput.Shutdown() — tore the output device down while the
        // tail was still sitting in hardware.
        //
        // RAISING THE TIMEOUT CANNOT FIX THIS, and trying it is actively
        // misleading: a generous timeout combined with an optimistic completion
        // signal produces exactly the same symptom, so the experiment "looks"
        // like it disproves the diagnosis. The window was already 5000 ms for a
        // sub-second string.
        //
        // 50 ms was also simply less than the buffering in front of the speaker:
        // the alert channel runs WaveOut with BufferMilliseconds 100 and the
        // default two buffers, so about 200 ms of audio can be queued past the
        // mixer at any moment. The final dit is the most vulnerable element in
        // the string — shortest, and last.
        //
        // So completion is now OBSERVED, in two stages, each of which asks
        // something that is not a clock:
        //
        //  1. The MIXER tells us it has consumed every sample. CancellableCwProvider
        //     reports end-of-source the first time its inner provider returns a
        //     short read, which is the same moment MixingSampleProvider drops the
        //     input. Nothing is computed; the consumer says when it is done.
        //  2. The DEVICE tells us it has played what it was holding. We snapshot
        //     WaveOut's own reported play position at the instant of (1) and wait
        //     for it to advance by the depth of the buffer chain. A stalled device
        //     stops advancing, so we keep waiting rather than resolving early and
        //     letting the tail be destroyed.
        //
        // Both stages are bounded, because a wedged audio device must never be
        // able to stop the application closing. A truncated character is a
        // papercut; an exit that hangs is a support call. When either bound is
        // hit we resolve normally: the caller's own timeout is not the right
        // place to express "the sound card is broken".
        //
        // A trailing silence element on this one string would have masked the
        // symptom and left every other exit-time utterance exposed. The defect
        // was in the completion contract, so the contract is what changed.
        private async Task WaitForDrain(IDisposable handle, int totalMs, CancellationToken ct)
        {
            // Nothing was actually submitted (earcons off, or no mixer): there is
            // no drain to observe and never will be, so do not make the caller
            // wait out the sequence duration for silence.
            if (handle is not CancellableCwProvider provider)
                return;

            // Stage 1 — wait for the mixer to consume the sequence. Bounded well
            // past the honest duration: the audio has to travel through the
            // device buffer before it is even started, so the sequence cannot
            // finish sooner than totalMs and should not take much longer.
            int stageOneBudget = totalMs + EarconPlayer.AlertOutputLatencyMs + DrainGraceMs;
            if (!await provider.WaitForEndOfSource(stageOneBudget, ct).ConfigureAwait(false))
            {
                Trace.WriteLine(
                    "EarconCwOutput.WaitForDrain: mixer did not consume the sequence within "
                    + stageOneBudget + "ms — resolving anyway.");
                return;
            }

            // Stage 2 — the samples are now inside the device's own buffer chain.
            // Wait for its reported play position to advance by the chain depth.
            int latencyMs = EarconPlayer.AlertOutputLatencyMs;
            long bytesPerMs = Math.Max(1, EarconPlayer.AlertBytesPerSecond / 1000);
            long start = EarconPlayer.AlertPlayedBytes;
            if (start < 0)
            {
                // The device will not report a position (no channel, or the driver
                // refused). Fall back to its declared latency — still better than
                // the 50 ms this replaced, and honest about being a fallback.
                await Task.Delay(latencyMs, ct).ConfigureAwait(false);
                return;
            }

            long needed = latencyMs * bytesPerMs;
            int waited = 0;
            int budget = latencyMs + DrainGraceMs;
            while (waited < budget)
            {
                long now = EarconPlayer.AlertPlayedBytes;
                if (now < 0) return;

                // waveOutGetPosition counts bytes in a 32-bit field, so at this
                // mixer's rate it wraps roughly every three and a half hours.
                // A subtraction is enough to survive one wrap and costs a line;
                // without it a farewell that happened to land on the wrap would
                // wait out the whole budget for no reason.
                long delta = now - start;
                if (delta < 0) delta += 0x1_0000_0000L;
                if (delta >= needed) return;

                await Task.Delay(DrainPollMs, ct).ConfigureAwait(false);
                waited += DrainPollMs;
            }

            Trace.WriteLine(
                "EarconCwOutput.WaitForDrain: device play position did not advance "
                + latencyMs + "ms within its budget — resolving anyway.");
        }

        /// <summary>Extra head-room on each drain stage before we stop waiting.</summary>
        private const int DrainGraceMs = 500;

        /// <summary>How often the device's reported play position is re-read.</summary>
        private const int DrainPollMs = 10;

        private readonly struct QueuedSequence
        {
            public QueuedSequence(
                IReadOnlyList<CwElement> elements,
                int sidetoneHz, float volume, int riseFallMs,
                MeterVoice? markVoice,
                CancellationToken callerToken,
                TaskCompletionSource completion)
            {
                Elements = elements;
                SidetoneHz = sidetoneHz;
                Volume = volume;
                RiseFallMs = riseFallMs;
                MarkVoice = markVoice;
                CallerToken = callerToken;
                Completion = completion;
            }

            public IReadOnlyList<CwElement> Elements { get; }
            public int SidetoneHz { get; }
            public float Volume { get; }
            public int RiseFallMs { get; }

            /// <summary>
            /// The spectrum captured when the sequence was ENQUEUED, not when it
            /// plays. An operator auditioning waveforms in Settings can change
            /// the choice while a queued sequence is still waiting, and a
            /// sequence that changed timbre halfway down the queue would be a
            /// puzzle rather than a preview.
            /// </summary>
            public MeterVoice? MarkVoice { get; }
            public CancellationToken CallerToken { get; }
            public TaskCompletionSource Completion { get; }
        }
    }

    /// <summary>
    /// Wraps a CW sample-provider sequence so the submitter can cancel it
    /// mid-stream via Dispose(). When cancelled, subsequent Read calls return
    /// zero samples, which signals end-of-stream to the mixer and
    /// MixingSampleProvider auto-removes it.
    /// </summary>
    internal sealed class CancellableCwProvider : ISampleProvider, IDisposable
    {
        private readonly ISampleProvider _source;
        private volatile bool _cancelled;

        // Set once, the first time the inner provider cannot fill the request —
        // which is exactly the moment MixingSampleProvider stops asking and drops
        // this input. See WaitForEndOfSource.
        private readonly TaskCompletionSource _endOfSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellableCwProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        /// <summary>
        /// Wait until the mixer has pulled every sample out of this provider, or
        /// until <paramref name="timeoutMs"/> elapses. True when the source
        /// really ended; false when the deadline won.
        ///
        /// This is the honest half of "has it finished playing" — it is the
        /// consumer reporting what it consumed, rather than us predicting how
        /// long consuming ought to take. It is NOT the whole answer: samples the
        /// mixer has read are still ahead of the speaker inside the output
        /// device's buffers, which is the second stage of
        /// <see cref="EarconCwOutput"/>'s drain wait.
        /// </summary>
        public async Task<bool> WaitForEndOfSource(int timeoutMs, CancellationToken ct)
        {
            var completed = await Task.WhenAny(
                _endOfSource.Task,
                Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (ReferenceEquals(completed, _endOfSource.Task)) return true;
            // The delay lost by being CANCELLED, not by elapsing — that is a
            // shutdown or a caller withdrawing, and it must surface as
            // cancellation so PlayOne tears the handle down instead of
            // resolving the sequence as if it had been heard.
            ct.ThrowIfCancellationRequested();
            return false;
        }

        // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
        // are re-declared here so the body's index arithmetic is unchanged -
        // buffer[offset + n] indexes a Span exactly as it did an array.
        public int Read(Span<float> buffer)
        {
            int offset = 0;
            int count = buffer.Length;
            if (_cancelled)
            {
                _endOfSource.TrySetResult();
                return 0;
            }
            int read = _source.Read(buffer.Slice(offset, count));
            // A short read means the concatenated sequence is exhausted. The
            // mixer treats it the same way and removes us, so there is no later
            // call in which to notice — this is the one chance to record it.
            if (read < count) _endOfSource.TrySetResult();
            return read;
        }

        public void Dispose()
        {
            _cancelled = true;
            _endOfSource.TrySetResult();
        }
    }
}
