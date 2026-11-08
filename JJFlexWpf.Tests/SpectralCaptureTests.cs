using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Xunit;

namespace JJFlexWpf.Tests;

public sealed class SpectralCaptureTests
{
    public SpectralCaptureTests() => NoiseReductionPerChannelTests.AssertWindowless();
    private static float[] Audio(int frame) => Enumerable.Range(0, 960).Select(i =>
        (float)(0.1 * Math.Sin((frame * 480 + i / 2) * 0.037) + (i % 2) * 0.07)).ToArray();
    private static string ProfileFile(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "profile.xml");
        var serializer = new XmlSerializer(typeof(NoiseProfileData));
        using var output = File.Create(path);
        serializer.Serialize(output, new NoiseProfileData { Name = "test", FftSize = 1024, SampleRate = 48000, Magnitudes = new double[1024] });
        return path;
    }

    [Fact]
    public void Wrapper_matches_existing_arithmetic_per_channel_with_no_carry_or_opening_gap()
    {
        var legacy = new SpectralSubtractionProvider(48000, 2) { Enabled = true };
        legacy.SetChannelProfile(0, Enumerable.Repeat(0.01, 1024).ToArray());
        legacy.SetChannelProfile(1, Enumerable.Repeat(0.03, 1024).ToArray());
        int channel = 0;
        using var bank = new PerChannelDenoiserBank(() => {
            var engine = new SpectralCaptureEngine(); engine.SetProfile(legacy.GetChannelProfile(channel++)); return engine;
        }, 2);
        for (int frame = 0; frame < 35; frame++)
        {
            var expected = Audio(frame); var actual = (float[])expected.Clone();
            legacy.ProcessInPlace(expected, 0, expected.Length, 2); bank.Process(actual, 1);
            Assert.Equal(expected, actual);
            if (frame == 0) Assert.Equal(Audio(frame), actual); // the adapter must not add a silent frame
        }
        Assert.Equal(480, bank.FrameLength); Assert.Equal(992, bank.LatencySamples);
        Assert.Equal(0, bank.CarryLatencySamples);
    }

    [Fact]
    public void No_profile_is_true_passthrough_and_clearing_stops_subtraction_on_next_buffer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-49-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var p = new RxAudioPipeline(); p.SpectralEnabled = true;
            for (int frame = 0; frame < 4; frame++) { var a = Audio(frame); p.Process(a); Assert.Equal(Audio(frame), a); }
            Assert.True(p.LoadNoiseProfile(ProfileFile(dir)));
            bool changed = false;
            for (int frame = 0; frame < 8; frame++) { var a = Audio(frame); p.Process(a); changed |= !a.SequenceEqual(Audio(frame)); }
            Assert.True(changed);
            p.ClearNoiseProfile(); Assert.False(p.HasNoiseProfile);
            var after = Audio(8); p.Process(after); Assert.Equal(Audio(8), after);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false, false, "SSB", false)]
    [InlineData(true, false, "SSB", false)]
    [InlineData(false, true, "SSB", false)]
    [InlineData(true, true, "DIGU", false)]
    [InlineData(true, false, "DIGU", true)]
    public void Capture_is_raw_fresh_and_completes_independent_of_slots_selection_and_mode(
        bool spectral, bool neural, string mode, bool otherSelection)
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-49-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var p = new RxAudioPipeline(); p.SpectralEnabled = spectral; p.RnnEnabled = neural; p.SetCurrentMode(mode);
            p.LoadNoiseProfile(ProfileFile(dir));
            for (int f = 0; f < 12; f++) p.Process(Audio(f)); // leftover subtraction state must not enter the capture
            if (otherSelection)
            {
                // Inject a different spectral selection through the catalog, without needing a new real engine.
                p.Dispose();
                var rows = new DenoiseEngineCatalog().Entries.Select(e => e.Id == "nr2" ? e with { Factory = _ => new SpectralCaptureEngine() } : e);
                using var alternate = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(rows));
                Assert.True(alternate.SelectPcEngine("nr2")); alternate.SetCurrentMode(mode);
                CaptureAndCompare(alternate, dir);
            }
            else CaptureAndCompare(p, dir);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    private static void CaptureAndCompare(RxAudioPipeline pipeline, string dir)
    {
        var reference = new SpectralSubtractionProvider(48000, 2); reference.StartSampling(1);
        pipeline.StartNoiseSampling(1);
        for (int frame = 0; frame < 130; frame++)
        {
            var raw = Audio(frame);
            reference.ProcessInPlace(raw, 0, raw.Length, 2);
            pipeline.Process(Audio(frame));
        }
        Assert.False(pipeline.IsNoiseSampling); Assert.True(pipeline.HasNoiseProfile);
        Assert.True(pipeline.SaveNoiseProfile(Path.Combine(dir, "actual.xml"), "same"));
        Assert.True(reference.SaveProfile(Path.Combine(dir, "expected.xml"), "same"));
        var serializer = new XmlSerializer(typeof(NoiseProfileData));
        using var actualStream = File.OpenRead(Path.Combine(dir, "actual.xml"));
        using var expectedStream = File.OpenRead(Path.Combine(dir, "expected.xml"));
        var actual = (NoiseProfileData)serializer.Deserialize(actualStream)!;
        var expected = (NoiseProfileData)serializer.Deserialize(expectedStream)!;
        Assert.Equal(expected.Magnitudes, actual.Magnitudes);
    }

    [Theory]
    [InlineData(true, "spectral_capture", "SSB", true, "spectral_is_using_it")]
    [InlineData(false, "spectral_capture", "SSB", true, "turn_spectral_on")]
    [InlineData(true, "nr2", "SSB", true, "turn_spectral_on")]
    // Sprint 49 Track E: no mode holds the spectral slot back any more (the
    // gate is retired, ruled 2026-11-07, question 4), so the two DIGU rows that
    // expected "held_by_mode" now expect what an SSB row expects.
    [InlineData(true, "spectral_capture", "DIGU", true, "spectral_is_using_it")]
    [InlineData(false, "spectral_capture", "DIGU", true, "turn_spectral_on")]
    [InlineData(true, "spectral_capture", "DIGU", false, "spectral_is_using_it")]
    public void Finish_fragment_is_derived_from_intent_and_mode(bool on, string selection, string mode, bool voiceOnly, string suffix)
    {
        _ = mode; _ = voiceOnly;
        Assert.Equal("audio.noise.capture." + suffix, RxAudioPipeline.CaptureFinishKey(on, selection));
    }
}
