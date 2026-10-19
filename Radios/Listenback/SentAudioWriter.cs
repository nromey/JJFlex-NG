using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>One instance's pre-Opus tap. The callback only copies to the bounded feed.</summary>
public sealed class SentAudioWriter
{
    private readonly TxFramePipeline _pipeline;
    private readonly IqFeed _feed = new(48000);
    private readonly IDisposable _subscription;
    private readonly TakeHeader _header;
    private readonly string _directory;
    private WavWriter _wav;
    private long _firstTicks, _frames, _lastDrops;
    private int _rate, _stopping, _inCallback;
    private string _failure;
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SentAudioWriter(TxFramePipeline pipeline, string directory)
    {
        _directory = directory; _pipeline = pipeline;
        _header = new TakeHeader(Path.Combine(directory, "sent.txt"), "jjflex-sent-1");
        _subscription = _feed.Subscribe(Accept);
        pipeline.BeforeEncode += Copy;
    }
    private void Copy(float[] samples, int count, uint rate)
    {
        Interlocked.Increment(ref _inCallback);
        try
        {
            if (Volatile.Read(ref _stopping) == 0)
                _feed.Offer(new(rate, 0, 0, 0, 0, 0, 0, Stopwatch.GetTimestamp()), samples, count);
        }
        finally { Interlocked.Decrement(ref _inCallback); }
    }
    private void Accept(IqBlock block)
    {
        if (_failure != null) return;
        try
        {
            int rate = (int)block.Header.StreamId;
            if (_wav == null)
            {
                _rate = rate; _firstTicks = block.Header.ArrivalTicks;
                _wav = new WavWriter(Path.Combine(_directory, "sent.wav"), rate, 1);
                _header.Write("rate", rate); _header.Write("first-frame-ticks", _firstTicks);
                _header.Write("source", "post-injection/post-conditioning/pre-Opus; stereo averaged to mono; no normalization");
            }
            if (rate != _rate) { _failure = "rate-changed"; return; }
            // Preserve pauses between separate keyings on the same wall clock.
            long expected = (long)Math.Round((block.Header.ArrivalTicks - _firstTicks) * (double)rate / Stopwatch.Frequency);
            long gap = Math.Max(0, block.DroppedPairs - _lastDrops);
            if (expected - _frames > rate / 10) gap = Math.Max(gap, expected - _frames);
            if (gap > rate * 3600L) { _failure = "clock-discontinuity"; return; }
            if (gap > 0)
            {
                _header.Event("gap", new { AtSample = _frames, Samples = gap, Basis = "callback-clock-or-exact-overrun" });
                Fill(gap);
            }
            var stereo = block.Samples.Span;
            var mono = new float[stereo.Length / 2];
            for (int i = 0; i < mono.Length; ++i)
            {
                mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) / 2;
                if (!float.IsFinite(mono[i])) { _failure = "non-finite"; return; }
            }
            _wav.Write(mono, mono.Length); _frames += mono.Length;
            _lastDrops = block.DroppedPairs;
        }
        catch (Exception ex) { _failure = ex.GetType().Name; }
    }
    private void Fill(long frames)
    {
        var zeros = new float[4096];
        while (frames > 0) { int n = (int)Math.Min(frames, zeros.Length); _wav.Write(zeros, n); _frames += n; frames -= n; }
    }
    public async Task StopAsync(TakeHeader takeHeader, bool pipelineUnchanged)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) { await _stopped.Task.ConfigureAwait(false); return; }
        try
        {
            _pipeline.BeforeEncode -= Copy;
            while (Volatile.Read(ref _inCallback) != 0) await Task.Delay(1).ConfigureAwait(false);
            await Task.Run(_feed.Dispose).ConfigureAwait(false);
            _subscription.Dispose();
            if (_wav != null)
            {
                Fill(Math.Max(0, _feed.DroppedPairs - _lastDrops));
                _wav.Finish(); _wav.Dispose();
                _header.Write("samples", _frames);
                _header.Write("sha256", TakeHeader.Checksum(Path.Combine(_directory, "sent.wav")));
            }
            bool clean = _wav != null && _failure == null && _feed.DroppedPackets == 0 && _feed.SubscriberFailures == 0 && pipelineUnchanged;
            _header.Write("overrun-packets", _feed.DroppedPackets);
            _header.Write("closed", clean ? "clean" : "incomplete");
            takeHeader.Write("sent-status", clean ? "clean" : "incomplete");
            takeHeader.Write("sent-first-frame-ticks", _firstTicks);
            takeHeader.Write("sent-rate", _rate);
            takeHeader.Write("sent-reason", _failure ?? (_wav == null ? "no-pc-frames" : !pipelineUnchanged ? "pipeline-replaced" : "post-conditioning/pre-Opus"));
            _header.Dispose(); _stopped.TrySetResult(true);
        }
        catch (Exception ex) { _wav?.Dispose(); _header.Dispose(); _stopped.TrySetException(ex); throw; }
    }
}
