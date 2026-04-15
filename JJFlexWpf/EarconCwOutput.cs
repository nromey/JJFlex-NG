using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JJFlexWpf
{
    /// <summary>
    /// Speaker-based CW notification output. Builds one concatenated
    /// ISampleProvider for the whole element sequence — the audio engine
    /// drives inter-element timing at sample-accurate resolution, so dits
    /// and dahs keep their PARIS ratio even when the UI thread is busy.
    /// </summary>
    /// <remarks>
    /// Each mark element is rendered by a <see cref="CwToneSampleProvider"/>
    /// (sine wave with raised-cosine attack/release envelope). Each gap is a
    /// silence of the specified duration. The sequence goes to the alert
    /// mixer in one submission via <see cref="EarconPlayer.SubmitCwSequence"/>.
    ///
    /// The previous implementation iterated tone-by-tone using Task.Delay
    /// between elements. Two bugs fell out of that design: (1) a
    /// FadeInOutSampleProvider misuse that turned every tone into an almost-
    /// entirely-fading envelope with no sustain, making dits sound weak and
    /// dahs sound short; (2) Task.Delay's ~15 ms Windows timer granularity
    /// corrupted timing at speeds above about 12 WPM. Batching the whole
    /// sequence into one sample provider eliminates both problems.
    /// </remarks>
    public class EarconCwOutput : ICwNotificationOutput
    {
        private IDisposable? _currentSequence;
        private readonly object _lock = new();

        public Task PlayElementsAsync(
            IReadOnlyList<CwElement> elements,
            int sidetoneHz,
            float volume,
            int riseFallMs,
            CancellationToken ct)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            if (elements.Count == 0) return Task.CompletedTask;

            Cancel(); // interrupt any in-flight sequence

            int totalMs = 0;
            var providers = new List<ISampleProvider>(elements.Count);
            int sr = EarconPlayer.MixerSampleRate;

            foreach (var el in elements)
            {
                if (el.DurationMs <= 0) continue;
                totalMs += el.DurationMs;
                if (el.Type == CwElementType.Mark)
                {
                    providers.Add(new CwToneSampleProvider(
                        sr, sidetoneHz, el.DurationMs, riseFallMs, volume));
                }
                else
                {
                    providers.Add(new SilenceProvider(new WaveFormat(sr, 1))
                        .ToSampleProvider()
                        .Take(TimeSpan.FromMilliseconds(el.DurationMs)));
                }
            }

            if (providers.Count == 0) return Task.CompletedTask;

            var concat = new ConcatenatingSampleProvider(providers);
            IDisposable handle;
            try
            {
                handle = EarconPlayer.SubmitCwSequence(concat);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconCwOutput.PlayElementsAsync: submit failed: {ex.Message}");
                return Task.CompletedTask;
            }

            lock (_lock) { _currentSequence = handle; }

            return WaitForCompletion(totalMs, handle, ct);
        }

        public void Cancel()
        {
            IDisposable? h;
            lock (_lock) { h = _currentSequence; _currentSequence = null; }
            try { h?.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"EarconCwOutput.Cancel: {ex.Message}"); }
        }

        private async Task WaitForCompletion(int totalMs, IDisposable handle, CancellationToken ct)
        {
            // Add a small tail so the audio engine finishes consuming the last
            // samples before we consider the sequence "done" from the caller's
            // perspective (useful when the caller chains PlaySK() → Close()).
            int waitMs = totalMs + 50;

            try
            {
                await Task.Delay(waitMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller cancelled — stop the sequence mid-playback.
                try { handle.Dispose(); } catch { }
                throw;
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_currentSequence, handle))
                        _currentSequence = null;
                }
            }
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
