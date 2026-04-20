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

        public EarconCwOutput()
        {
            _consumerLoop = Task.Run(ConsumerLoop);
        }

        public Task PlayElementsAsync(
            IReadOnlyList<CwElement> elements,
            int sidetoneHz,
            float volume,
            int riseFallMs,
            CancellationToken ct)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            if (elements.Count == 0) return Task.CompletedTask;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new QueuedSequence(elements, sidetoneHz, volume, riseFallMs, ct, tcs);

            if (!_queue.Writer.TryWrite(item))
            {
                // Writer is completed (we're being disposed) — treat as cancelled.
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
                    await PlayOne(item).ConfigureAwait(false);
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
                        sr, item.SidetoneHz, el.DurationMs, item.RiseFallMs, item.Volume));
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

            // Small tail after the computed duration so the mixer finishes
            // consuming the last samples before the caller's Task resolves.
            int waitMs = totalMs + 50;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                item.CallerToken, _shutdown.Token);

            try
            {
                await Task.Delay(waitMs, linked.Token).ConfigureAwait(false);
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

        private readonly struct QueuedSequence
        {
            public QueuedSequence(
                IReadOnlyList<CwElement> elements,
                int sidetoneHz, float volume, int riseFallMs,
                CancellationToken callerToken,
                TaskCompletionSource completion)
            {
                Elements = elements;
                SidetoneHz = sidetoneHz;
                Volume = volume;
                RiseFallMs = riseFallMs;
                CallerToken = callerToken;
                Completion = completion;
            }

            public IReadOnlyList<CwElement> Elements { get; }
            public int SidetoneHz { get; }
            public float Volume { get; }
            public int RiseFallMs { get; }
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

        public CancellableCwProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_cancelled) return 0;
            return _source.Read(buffer, offset, count);
        }

        public void Dispose() => _cancelled = true;
    }
}
