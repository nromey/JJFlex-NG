using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JJPortaudio;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

public sealed class ListenbackAlignmentTests
{
    private static float[] Noise(int n, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (float)(random.NextDouble() - .5)).ToArray();
    }
    [Theory]
    [InlineData(137)]
    [InlineData(-283)]
    public void Delayed_copy_has_measured_sample_lag_and_level_difference(int lag)
    {
        const int rate = 8000;
        var sent = Noise(rate * 2, 61); var heard = new float[sent.Length];
        for (int i = 0; i < sent.Length; ++i) if (i + lag >= 0 && i + lag < heard.Length) heard[i + lag] = sent[i] * .5f;
        var alignment = AudioAlignment.Measure(sent, heard, rate, new[] { new TakeSpan(0, heard.Length) }, .5);
        Assert.True(alignment.Aligned, alignment.Reason);
        Assert.Equal(lag, alignment.Spans[0].LagSamples);
        Assert.InRange(alignment.Spans[0].CorrelationWindow.Length, rate / 5, 2 * rate);
        var difference = AudioDifferential.Compare(sent, heard, rate, alignment, 100, 3000);
        Assert.True(difference.Comparable);
        Assert.InRange(difference.LevelDb, -6.03, -6.01);
        Assert.InRange(difference.CrestDb, -.001, .001);
    }
    [Fact]
    public void A_missing_middle_span_does_not_change_the_measured_delay()
    {
        const int rate = 8000, lag = 137;
        var sent = Noise(rate * 3, 17); var heard = new float[sent.Length];
        for (int i = 0; i + lag < sent.Length; ++i) heard[i + lag] = sent[i] * .5f;
        Array.Clear(heard, rate, rate);
        var spans = TakeTimeline.WithoutGaps(new[] { new TakeSpan(0, heard.Length) }, new[] { new TakeSpan(rate, 2 * rate) });
        var result = AudioAlignment.Measure(sent, heard, rate, spans, .3);
        Assert.True(result.Aligned, result.Reason);
        Assert.Equal(2, result.Spans.Count);
        Assert.All(result.Spans, span => Assert.Equal(lag, span.LagSamples));
        Assert.InRange(AudioDifferential.Compare(sent, heard, rate, result, 100, 3000).LevelDb, -6.03, -6.01);
    }
    [Fact]
    public void Noise_and_delayed_steady_sine_do_not_claim_an_alignment()
    {
        const int rate = 8000;
        var sine = Enumerable.Range(0, rate).Select(i => (float)Math.Sin(2 * Math.PI * 500 * i / rate)).ToArray();
        var delayed = Enumerable.Range(0, rate).Select(i => (float)Math.Sin(2 * Math.PI * 500 * (i - 31) / rate)).ToArray();
        Assert.False(AudioAlignment.Measure(sine, delayed, rate, new[] { new TakeSpan(0, rate) }, .3).Aligned);
        var unrelated = AudioAlignment.Measure(Noise(rate, 9), Noise(rate, 10), rate, new[] { new TakeSpan(0, rate) }, .3);
        Assert.False(unrelated.Aligned);
        Assert.False(AudioDifferential.Compare(sine, delayed, rate, unrelated, 100, 3000).Comparable);
    }
    [Fact]
    public void Independent_gap_free_spans_detect_drift_and_unlike_takes_are_refused()
    {
        const int rate = 8000;
        var sent = Noise(rate * 3, 9); var heard = new float[sent.Length];
        for (int i = 0; i < rate - 200; ++i) heard[i + 50] = sent[i];
        for (int i = rate * 2; i < rate * 3 - 200; ++i) heard[i + 100] = sent[i];
        var result = AudioAlignment.Measure(sent, heard, rate, new[] { new TakeSpan(0, rate), new TakeSpan(2 * rate, 3 * rate) }, .3);
        Assert.False(result.Aligned); Assert.Equal("alignment-drifted", result.Reason);
        Assert.Equal(new[] { 50, 100 }, result.Spans.Select(s => s.LagSamples));
        var fields = new List<KeyValuePair<string, string>> { new("serial", "one") };
        Assert.Equal("serial", AudioDifferential.Compatibility(new("clean", "", fields),
            new("clean", "", new[] { new KeyValuePair<string, string>("serial", "two") })));
    }
    [Fact]
    public async Task Tap_captures_tone_despite_conditioner_bypass_and_observer_failure_never_interrupts_send()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-sent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tone = new TxToneGenerator { Frequency = 1000, LevelDb = -10 }; tone.Start();
            int conditioned = 0; float[] encodedSamples = null;
            var pipeline = new TxFramePipeline { Source = tone, Conditioner = (_, _, _) => conditioned++,
                Encode = b => { encodedSamples = (float[])b.Clone(); return new byte[] { 1 }; }, Handler = _ => { } };
            using var header = new TakeHeader(Path.Combine(root, "take.txt"));
            var sent = new SentAudioWriter(pipeline, root);
            var samples = new float[960];
            for (int i = 0; i < 20; ++i) Assert.True(pipeline.Emit(samples, samples.Length, 48000));
            await sent.StopAsync(header, true);
            var wav = TakeAudio.ReadMono(Path.Combine(root, "sent.wav"));
            Assert.Equal(48000, wav.Rate); Assert.Equal(20 * 480, wav.Samples.Length);
            Assert.Equal(0, conditioned); Assert.Contains(wav.Samples, s => Math.Abs(s) > .1);
            for (int i = 0; i < 480; ++i) Assert.InRange(Math.Abs(wav.Samples[^480..][i] - encodedSamples[i * 2]), 0, .00004);
            pipeline.BeforeEncode += (_, _, _) => throw new Exception("observer failure");
            Assert.True(pipeline.Emit(samples, samples.Length, 48000));
            Assert.Equal(1, pipeline.TapFailures);
        }
        finally { Directory.Delete(root, true); }
    }
}
