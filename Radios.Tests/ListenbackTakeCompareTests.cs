using System;
using System.Collections.Generic;
using System.IO;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

public sealed class ListenbackTakeCompareTests
{
    private const int Rate = 8000;
    private const int Samples = Rate * 2;

    private static string Take(string root, string name, int seed, double lowGain = 1,
        double highGain = 1, string serial = "same-radio")
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var random = new Random(seed);
        double[] frequencies = new double[80];
        for (int tone = 0; tone < frequencies.Length; tone++)
            frequencies[tone] = tone < 20 ? 105 + tone * 9.1 : 1020 + (tone - 20) * 30.1;
        double[] phases = new double[frequencies.Length], envelopes = new double[frequencies.Length];
        for (int tone = 0; tone < frequencies.Length; tone++) phases[tone] = random.NextDouble() * 2 * Math.PI;
        string iq = Path.Combine(directory, "rf.iq");
        using (var writer = new BinaryWriter(File.Create(iq)))
        {
            for (int i = 0; i < Samples; i++)
            {
                if (i % 240 == 0)
                    for (int tone = 0; tone < frequencies.Length; tone++)
                        envelopes[tone] = 0.45 + random.NextDouble() * 0.55;
                double real = 0, imag = 0;
                for (int tone = 0; tone < frequencies.Length; tone++)
                {
                    double gain = tone < 20 ? lowGain / Math.Sqrt(20) : highGain / Math.Sqrt(60);
                    double phase = phases[tone] + 2 * Math.PI * frequencies[tone] * i / Rate;
                    real += gain * envelopes[tone] * Math.Cos(phase);
                    imag += gain * envelopes[tone] * Math.Sin(phase);
                }
                writer.Write((float)(32768 * 0.025 * real));
                writer.Write((float)(32768 * 0.025 * imag));
            }
        }
        using (var header = new TakeHeader(Path.Combine(directory, "take.txt")))
        {
            var facts = new Dictionary<string, string> {
                ["serial"] = serial, ["mode"] = "USB", ["rate-echoed"] = Rate.ToString(),
                ["receive-antenna"] = "XVT-B", ["rf-gain"] = "32", ["tx-antenna"] = "XVT-A",
                ["rf-power"] = "1", ["tx-frequency-mhz"] = "14.2", ["pan-centre-mhz"] = "14.2",
                ["tx-filter-low"] = "100", ["tx-filter-high"] = "3000", ["dummy-load"] = "False",
                ["render-scale"] = "1/32768; fixed; no normalization; no AGC", ["classifier"] = "noise-like"
            };
            foreach (var fact in facts) header.Write(fact.Key, fact.Value);
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = true, AtPair = 0 });
            header.Event("gap", new { AtPair = 7000, FillPairs = 1000 });
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = false, AtPair = Samples });
            header.Write("pairs-written", Samples);
            header.Write("sha256", TakeHeader.Checksum(iq));
            header.Write("closed", "clean");
        }
        return directory;
    }

    [Fact]
    public void Same_audio_uses_confident_sample_alignment()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = Take(root, "first", 7);
            string second = Take(root, "second", 7);
            var result = TakeAnalysis.CompareTakes(first, second);
            Assert.True(result.Comparable, result.Reason);
            Assert.Equal("aligned-spans", result.Reason);
            Assert.InRange(result.LevelDb, -0.15, 0.15);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Different_speech_uses_gap_free_long_term_level_and_tilt()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = Take(root, "first", 7);
            string second = Take(root, "second", 41, lowGain: 0.5, highGain: 1.5);
            var result = TakeAnalysis.CompareTakes(first, second);
            Assert.True(result.Comparable, result.Reason);
            Assert.Equal("overall-average", result.Reason);
            Assert.InRange(result.LevelDb, -0.6, 2.5); // expected +1.0 dB, allowing speech-envelope variation
            Assert.InRange(result.TiltDb, 7.5, 11.5); // expected +9.5 dB from the 3:1 high/low ratio
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Corrupt_and_unlike_takes_still_refuse_numbers()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = Take(root, "first", 7);
            string unlike = Take(root, "unlike", 41, serial: "other-radio");
            Assert.Equal("unlike-takes:serial", TakeAnalysis.CompareTakes(first, unlike).Reason);
            string corrupt = Take(root, "corrupt", 41);
            using (var file = File.OpenWrite(Path.Combine(corrupt, "rf.iq"))) file.WriteByte(0);
            var result = TakeAnalysis.CompareTakes(first, corrupt);
            Assert.False(result.Comparable);
            Assert.Equal("corrupt-take", result.Reason);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Take_statistics_measure_keyed_gap_free_audio_and_refuse_corrupt_input()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-statistics-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = Take(root, "first", 7);
            var stats = TakeStatistics.Measure(first);
            Assert.True(stats.Available, stats.Reason);
            Assert.True(double.IsFinite(stats.Lufs));
            Assert.True(double.IsFinite(stats.PeakDbfs));
            Assert.True(double.IsFinite(stats.RmsDbfs));
            Assert.True(double.IsFinite(stats.BrightnessDb));
            Assert.True(stats.PunchDb >= 0);
            Assert.True(double.IsFinite(stats.SplatterDbfs));
            using (var file = File.OpenWrite(Path.Combine(first, "rf.iq"))) file.WriteByte(0);
            Assert.False(TakeStatistics.Measure(first).Available);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
