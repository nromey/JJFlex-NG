using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JJPortaudio;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

public sealed class ListenbackDemodulationTests
{
    [Theory]
    [InlineData("USB", 1000, 0, 1000)]
    [InlineData("LSB", -1000, 0, 1000)]
    [InlineData("USB", 1500, 0, 1500)]
    [InlineData("USB", 1500, 1000, 500)]
    [InlineData("USB", 1500, -1000, 2500)]
    [InlineData("LSB", -1500, 1000, 2500)]
    [InlineData("LSB", -1500, -1000, 500)]
    public void Sideband_and_detune_preserve_fixed_scale(string mode, int inputHz, int detune, int outputHz)
    {
        const int rate = 24000;
        var demod = new SsbDemodulator(rate, mode, 100, 3000, detune);
        double real = 0, imag = 0, oppositePower = 0;
        for (int i = 0; i < rate + demod.Delay; ++i)
        {
            double phase = 2 * Math.PI * inputHz * i / rate;
            float sample = demod.Push((float)(8192 * Math.Cos(phase)), (float)(8192 * Math.Sin(phase)));
            if (i < 2000) continue;
            double angle = 2 * Math.PI * outputHz * (i - demod.Delay) / rate;
            real += sample * Math.Cos(angle); imag += sample * Math.Sin(angle); oppositePower++;
        }
        double amplitude = 2 * Math.Sqrt(real * real + imag * imag) / oppositePower;
        Assert.InRange(amplitude, .245, .255);
    }

    [Fact]
    public void Opposite_sideband_is_rejected_and_gap_splits_preserve_the_sample_clock()
    {
        const int rate = 24000;
        var demod = new SsbDemodulator(rate, "USB", 100, 3000);
        double sum = 0;
        for (int i = 0; i < rate; ++i)
        {
            double phase = -2 * Math.PI * 1500 * i / rate;
            float sample = demod.Push((float)(8192 * Math.Cos(phase)), (float)(8192 * Math.Sin(phase)));
            if (i > 2000) sum += sample * sample;
        }
        Assert.True(sum / (rate - 2001) < 1e-8);
        Assert.Equal(new[] { new TakeSpan(10, 20), new TakeSpan(25, 50), new TakeSpan(60, 90) },
            TakeTimeline.WithoutGaps(new[] { new TakeSpan(10, 90) }, new[] { new TakeSpan(20, 25), new TakeSpan(50, 60) }));
    }

    [Fact]
    public void Complete_tone_take_renders_at_fixed_level_identifies_RF_and_refuses_periodic_alignment()
    {
        const int rate = 24000;
        string directory = Path.Combine(Path.GetTempPath(), "jjflex-render-" + Guid.NewGuid().ToString("N"));
        long ticks = Stopwatch.GetTimestamp();
        var facts = new Dictionary<string, string> { ["mode"] = "USB", ["tx-filter-low"] = "100", ["tx-filter-high"] = "3000",
            ["pan-centre-mhz"] = "14.2", ["tx-frequency-mhz"] = "14.2", ["tone-engaged"] = "True", ["tone-hz"] = "1000" };
        try
        {
            using (var writer = new IqTakeWriter(directory, rate, facts) { Active = true, EchoedRate = rate })
            {
                var random = new Random(11);
                for (int second = 0; second < 3; ++second)
                {
                    if (second > 0) writer.Header.Event("keying", new { Source = "radio-TransmitChange", Keyed = second == 1, AtPair = second * rate });
                    var iq = new float[rate * 2];
                    for (int i = 0; i < rate; ++i)
                    {
                        iq[i * 2] = second == 1 ? (float)(.05 * Math.Cos(2 * Math.PI * 1000 * i / rate)) : random.Next(-30, 31) * 16f / 32768;
                        iq[i * 2 + 1] = second == 1 ? (float)(.05 * Math.Sin(2 * Math.PI * 1000 * i / rate)) : random.Next(-30, 31) * 16f / 32768;
                    }
                    writer.Accept(new(new(1, (byte)second, 0x02e3, 1, 2, (uint)(100 + second), 0, ticks + second * Stopwatch.Frequency), iq, 0, 0));
                }
                using (var sent = new WavWriter(Path.Combine(directory, "sent.wav"), rate, 1))
                {
                    var reference = Enumerable.Range(0, 3 * rate).Select(i => i >= rate && i < 2 * rate
                        ? (float)(.05 * Math.Cos(2 * Math.PI * 1000 * i / rate)) : 0).ToArray();
                    sent.Write(reference, reference.Length); sent.Finish();
                }
                using (var sentFacts = new TakeHeader(Path.Combine(directory, "sent.txt"), "jjflex-sent-1"))
                { sentFacts.Write("sha256", TakeHeader.Checksum(Path.Combine(directory, "sent.wav"))); sentFacts.Write("closed", "clean"); }
                writer.Header.Write("sent-status", "clean"); writer.Header.Write("sent-first-frame-ticks", ticks);
                writer.Finish("operator-stop");
            }
            var render = SsbTakeRenderer.Render(directory);
            Assert.Equal(3 * rate, render.Samples);
            Assert.InRange(render.MeasuredKeyedSamples, rate - 300, rate);
            Assert.InRange(render.RmsDb, -29.1, -28.9);
            var wav = TakeAudio.ReadMono(render.Path);
            Assert.Equal(rate, wav.Rate);
            Assert.InRange(wav.Samples.Skip(rate + 1000).Take(rate - 2000).Max(), .049, .051);
            var result = TakeAnalysis.Run(directory);
            Assert.True(result.Tone.Identified, result.Tone.Reason);
            Assert.True(result.Tone.AboveRestDb >= 20);
            Assert.Equal(1000, result.Tone.OffsetHz);
            Assert.False(result.Alignment.Aligned);
            Assert.Contains("aligned=no", File.ReadAllText(Path.Combine(directory, "align.txt")));
            Assert.False(TakeAnalysis.Run(directory).Alignment.Aligned); // atomic rerun also works
            var analysis = TakeHeader.Read(Path.Combine(directory, "analysis.txt"));
            Assert.Contains(analysis, f => f.Key == "energy-release-pair");
            // A reference becoming unavailable cannot leave a stale success/refusal reason.
            File.Delete(Path.Combine(directory, "sent.wav"));
            Assert.False(TakeAnalysis.Run(directory).Alignment.Aligned);
            Assert.Contains("sent-unavailable-or-incomplete", File.ReadAllText(Path.Combine(directory, "align.txt")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
