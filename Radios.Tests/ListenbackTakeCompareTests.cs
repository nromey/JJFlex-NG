#nullable enable
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

    /// <summary>
    /// Write a synthetic take. <paramref name="facts"/> overrides or adds header
    /// fields (a null value removes one, standing in for a take written before
    /// the field existed); <paramref name="events"/> writes extra header events
    /// after the keying edge, where FlexBase writes tone-at-keying and, since
    /// round 14, reference-at-keying.
    /// </summary>
    private static string Take(string root, string name, int seed, double lowGain = 1,
        double highGain = 1, string serial = "same-radio",
        IDictionary<string, string?>? facts = null, Action<TakeHeader>? events = null)
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
            var written = new Dictionary<string, string?> {
                ["serial"] = serial, ["mode"] = "USB", ["rate-echoed"] = Rate.ToString(),
                ["receive-antenna"] = "XVT-B", ["rf-gain"] = "32", ["tx-antenna"] = "XVT-A",
                ["rf-power"] = "1", ["tx-frequency-mhz"] = "14.2", ["pan-centre-mhz"] = "14.2",
                ["tx-filter-low"] = "100", ["tx-filter-high"] = "3000", ["dummy-load"] = "False",
                ["mic-input"] = "PC", ["pc-microphone-device"] = "Yeti", ["mic-profile"] = "Default",
                ["reference-audio"] = "False",
                ["render-scale"] = "1/32768; fixed; no normalization; no AGC", ["classifier"] = "noise-like"
            };
            if (facts != null) foreach (var fact in facts) written[fact.Key] = fact.Value;
            foreach (var fact in written) if (fact.Value != null) header.Write(fact.Key, fact.Value);
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = true, AtPair = 0 });
            events?.Invoke(header);
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
    public void Comparison_reuses_the_renders_that_were_played()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = Take(root, "first", 7);
            string second = Take(root, "second", 41);
            SsbTakeRenderer.Render(first);
            SsbTakeRenderer.Render(second);
            var renderedAt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            string firstWav = Path.Combine(first, "heard.wav"), secondWav = Path.Combine(second, "heard.wav");
            File.SetLastWriteTimeUtc(firstWav, renderedAt);
            File.SetLastWriteTimeUtc(secondWav, renderedAt);
            Assert.True(TakeAnalysis.CompareTakes(first, second, reuseRenders: true).Comparable);
            Assert.Equal(renderedAt, File.GetLastWriteTimeUtc(firstWav));
            Assert.Equal(renderedAt, File.GetLastWriteTimeUtc(secondWav));
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

    private static string Facts(string root, string name, int seed, params (string Key, string? Value)[] facts)
    {
        var map = new Dictionary<string, string?>();
        foreach (var (key, value) in facts) map[key] = value;
        return Take(root, name, seed, facts: map);
    }

    // ── Round 14, #794's header-and-gate half ──────────────────────────
    //
    // Found on the 2026-10-21 bench: the first voice take of an evening was
    // auto-compared with an hour-old TONE take, the newest on disk, and
    // reported "quieter by 7.3 dB ... brighter ... by 12.4 dB". That number
    // means nothing. What fed the transmitter is read from the header the way
    // CheckTone already reads it (tone-at-keying, else tone-engaged), plus the
    // reference-at-keying event FlexBase writes since this round.

    [Fact]
    public void A_tone_take_and_a_voice_take_are_not_compared()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            string voice = Take(root, "voice", 7);
            string tone = Take(root, "tone", 41, events: h => h.Event("tone-at-keying", new { Engaged = true, Frequency = 700.0 }));
            var result = TakeAnalysis.CompareTakes(voice, tone);
            Assert.False(result.Comparable, "a tone take was compared with a voice take: " + result.Reason);
            Assert.Equal("unlike-takes:source", result.Reason);
            // The refusal carries which was which, so the sentence can say so.
            var source = Assert.Single(result.SetupChanges!);
            Assert.Equal(new SetupChange("source", TakeSetup.Voice, TakeSetup.Tone), source);
            // And the other way round, which is the bench order: tone first, voice second.
            var reversed = TakeAnalysis.CompareTakes(tone, voice);
            Assert.Equal("unlike-takes:source", reversed.Reason);
            Assert.Equal(new SetupChange("source", TakeSetup.Tone, TakeSetup.Voice), Assert.Single(reversed.SetupChanges!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void An_old_take_records_its_tone_in_the_provenance_block_alone_and_is_still_recognised()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A take from before round 14: no reference-audio field, no
            // reference-at-keying event, no mic-profile, tone only in the
            // provenance block (tone-engaged / tone-hz, written at take start).
            string oldTone = Facts(root, "old-tone", 7, ("tone-engaged", "True"), ("tone-hz", "700"),
                ("reference-audio", null), ("mic-profile", null));
            string voice = Take(root, "voice", 41);
            Assert.Equal("unlike-takes:source", TakeAnalysis.CompareTakes(oldTone, voice).Reason);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_reference_take_is_compared_with_neither_a_voice_nor_a_tone_take()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            string voice = Take(root, "voice", 7);
            string reference = Take(root, "reference", 41, events: h => h.Event("reference-at-keying", new { Engaged = true }));
            string tone = Take(root, "tone", 43, events: h => h.Event("tone-at-keying", new { Engaged = true, Frequency = 700.0 }));
            Assert.Equal("unlike-takes:source", TakeAnalysis.CompareTakes(voice, reference).Reason);
            Assert.Equal("unlike-takes:source", TakeAnalysis.CompareTakes(reference, tone).Reason);
            // Two reference takes ARE alike: that is #794's point, an identical signal twice.
            string referenceAgain = Take(root, "reference-again", 47, events: h => h.Event("reference-at-keying", new { Engaged = true }));
            Assert.True(TakeAnalysis.CompareTakes(reference, referenceAgain).Comparable);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_take_from_before_this_round_counts_as_a_voice_take_when_it_recorded_no_tone()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Comparing across launches stays allowed when the sources match,
            // including with a take that predates the reference-audio field.
            string old = Facts(root, "old", 7, ("reference-audio", null), ("mic-profile", null));
            string voice = Take(root, "voice", 41);
            var result = TakeAnalysis.CompareTakes(old, voice);
            Assert.True(result.Comparable, result.Reason);
            // The old take recorded no profile, so no profile change is named:
            // what was not recorded cannot be said to have changed.
            Assert.Empty(result.SetupChanges!);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_different_filter_power_or_profile_is_compared_and_an_antenna_change_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            // RULED by Noel 2026-10-22 05:39: the transmit filter and the RF
            // power are compared and the difference named, like the profile;
            // an antenna change stays a refusal.
            string first = Take(root, "first", 7);
            string narrower = Facts(root, "narrower", 41, ("tx-filter-high", "2700"));
            var result = TakeAnalysis.CompareTakes(first, narrower);
            Assert.True(result.Comparable, "a narrower transmit filter refused the comparison");
            Assert.Equal(new SetupChange("tx-filter", "100 3000", "100 2700"), Assert.Single(result.SetupChanges!));
            string hotter = Facts(root, "hotter", 41, ("rf-power", "5"));
            result = TakeAnalysis.CompareTakes(first, hotter);
            Assert.True(result.Comparable, "a different RF power refused the comparison");
            Assert.Equal(new SetupChange("rf-power", "1", "5"), Assert.Single(result.SetupChanges!));
            string profile = Facts(root, "profile", 41, ("mic-profile", "Heil PR 781"));
            result = TakeAnalysis.CompareTakes(first, profile);
            Assert.True(result.Comparable, "a different mic profile refused the comparison");
            Assert.Equal(new SetupChange("mic-profile", "Default", "Heil PR 781"), Assert.Single(result.SetupChanges!));
            string otherMic = Facts(root, "other-mic", 41, ("pc-microphone-device", "Shure MV7"));
            result = TakeAnalysis.CompareTakes(first, otherMic);
            Assert.True(result.Comparable, "a different microphone refused the comparison");
            Assert.Equal(new SetupChange("pc-microphone-device", "Yeti", "Shure MV7"), Assert.Single(result.SetupChanges!));
            string jack = Facts(root, "jack", 41, ("mic-input", "MIC"), ("pc-microphone-device", "unavailable"));
            result = TakeAnalysis.CompareTakes(first, jack);
            Assert.True(result.Comparable, "a different mic input refused the comparison");
            // The input change explains the device change; only the input is named.
            Assert.Equal(new SetupChange("mic-input", "PC", "MIC"), Assert.Single(result.SetupChanges!));
            // Same setup, same numbers as before: nothing to name.
            Assert.Empty(TakeAnalysis.CompareTakes(first, Take(root, "same", 41)).SetupChanges!);
            // "1" and "1.0" are the same power, not a change.
            Assert.Empty(TakeAnalysis.CompareTakes(first, Facts(root, "one-point-oh", 41, ("rf-power", "1.0"))).SetupChanges!);

            string otherEars = Facts(root, "other-ears", 41, ("receive-antenna", "ANT2"));
            Assert.Equal("unlike-takes:receive-antenna", TakeAnalysis.CompareTakes(first, otherEars).Reason);
            string otherTx = Facts(root, "other-tx", 41, ("tx-antenna", "ANT1"));
            Assert.Equal("unlike-takes:tx-antenna", TakeAnalysis.CompareTakes(first, otherTx).Reason);
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
            string render = Path.Combine(first, "heard.wav");
            var renderedAt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(render, renderedAt);
            Assert.True(TakeStatistics.Measure(first, reuseRender: true).Available);
            Assert.Equal(renderedAt, File.GetLastWriteTimeUtc(render));
            using (var file = File.OpenWrite(Path.Combine(first, "rf.iq"))) file.WriteByte(0);
            Assert.False(TakeStatistics.Measure(first).Available);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
