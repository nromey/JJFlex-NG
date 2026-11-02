using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// NR4 (Sprint 49 Track C): libspecbleach behind <see cref="IDenoiseEngine"/>,
/// against the DLL actually committed under <c>runtimes\</c> for the RUNNING
/// process's architecture. Run on x64 and on x86: the parameter block's
/// layout differs between them, and a wrong layout is a native fault, not a
/// test failure, once an operator can select the engine.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> Frames in, frames out, on an MTA thread
/// (asserted in the constructor), where WPF refuses to build any element.
/// Nothing is played and no audio device is opened. The speech used for the
/// latency measurement is read from disk.
/// </remarks>
public sealed class SpecbleachEngineTests
{
    private const int Rate = 48000;
    private const int Block = 480;
    private readonly ITestOutputHelper _output;

    public SpecbleachEngineTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _output = output;
    }

    internal static string LibraryPath()
    {
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        return Path.Combine(RepoPaths.RequireRoot(), "runtimes", $"win-{arch}", "native", SpecbleachEngine.LibraryFileName);
    }

    private static SpecbleachEngine Engine(double? reductionDb = null)
    {
        var initial = reductionDb == null ? null
            : new Dictionary<string, EngineSettingValue> { ["reduction_db"] = new(reductionDb.Value) };
        var e = new SpecbleachEngine(initial, LibraryPath());
        Assert.True(e.IsAvailable, "NR4 did not start, so this proves nothing: " + e.UnavailabilityDetail);
        return e;
    }

    // ---- Loading, and the parameter block ----

    [Fact]
    public void LoadsOnItsOwnArchitectureAndReportsItsShape()
    {
        using var e = Engine();
        Assert.Equal("nr4", e.Id);
        Assert.Equal(48000, e.SampleRate);
        Assert.Equal(480, e.FrameLength);
        Assert.Equal(Unavailability.None, e.Unavailability);
        Assert.Equal("", e.UnavailabilityDetail);
        Assert.Equal("libspecbleach 0.4.1", e.LibraryVersion);
        Assert.Equal(512, e.LibraryFrameSize);
        Assert.Equal(512, e.LibraryFftSize);
        // The first load_parameters ran in the constructor and returned true:
        // a refused block leaves the engine unavailable.
        Assert.Equal(1, e.ParameterLoads);
        _output.WriteLine($"{(Environment.Is64BitProcess ? "x64" : "x86")}: {e.LibraryVersion}, frame {e.LibraryFrameSize}, FFT {e.LibraryFftSize}, latency {e.LatencySamples}, block {SpecbleachEngine.ParametersSize} bytes.");
    }

    [Fact]
    public void TheParameterBlockHasTheHeadersLayoutOnThisArchitecture()
    {
        bool x64 = Environment.Is64BitProcess;
        // Hand-counted from include/specbleach_denoiser.h at v0.4.1: four-byte
        // enums and floats, one-byte bools padded to the next four, a pointer
        // padded to its own size.
        var expected = new (string Field, int X64, int X86)[] {
            ("LearnNoise", 0, 0), ("ResidualListen", 4, 4), ("ReductionGain", 8, 8),
            ("SmoothingFactor", 12, 12), ("SmoothingMode", 16, 16), ("DfttStrength", 20, 20),
            ("WhiteningFactor", 24, 24), ("AdaptiveNoise", 28, 28), ("NoiseEstimationMethod", 32, 32),
            ("MaskingDepth", 36, 36), ("SuppressionStrength", 40, 40), ("Aggressiveness", 44, 44),
            ("TonalReductionGain", 48, 48), ("TransientProtectionEnable", 52, 52), ("NoiseProfileScale", 56, 56),
            ("ReductionCurveBias", 64, 60), ("ReductionCurveEnabled", 72, 64), ("ReductionCurveSize", 76, 68),
            ("TonalNoiseProfileScale", 80, 72),
        };
        foreach (var (field, o64, o86) in expected)
            Assert.True((x64 ? o64 : o86) == (int)Marshal.OffsetOf<SpecbleachDenoiserParameters>(field),
                $"{field} sits at {Marshal.OffsetOf<SpecbleachDenoiserParameters>(field)}, expected {(x64 ? o64 : o86)}");
        Assert.Equal(expected.Length, typeof(SpecbleachDenoiserParameters).GetFields().Length);
        Assert.Equal(x64 ? 88u : 76u, SpecbleachEngine.ParametersSize);
    }

    [Fact]
    public void TheLibrarysDefaultsReadBackThroughTheStructByValue()
    {
        using var e = Engine();
        var d = e.LibraryDefaults;
        // specbleach_denoiser_get_default_parameters, src/processors/specbleach_denoiser.c lines 112 to 133.
        Assert.Equal(0.1f, d.ReductionGain);
        Assert.Equal(0.5f, d.SmoothingFactor);
        Assert.Equal(0.5f, d.MaskingDepth);
        Assert.Equal(1.0f, d.SuppressionStrength);
        Assert.Equal(0.1f, d.TonalReductionGain);
        Assert.Equal(1.0f, d.NoiseProfileScale);
        Assert.Equal(1.0f, d.TonalNoiseProfileScale);
        Assert.Equal(IntPtr.Zero, d.ReductionCurveBias);
        Assert.Equal(0, d.LearnNoise);
        Assert.Equal(0, d.ResidualListen);
        Assert.Equal(0, d.SmoothingMode);
        Assert.Equal(0f, d.DfttStrength);
        Assert.Equal(0f, d.WhiteningFactor);
        Assert.Equal(0, d.AdaptiveNoise);
        Assert.Equal(0, d.NoiseEstimationMethod);
        Assert.Equal(0f, d.Aggressiveness);
        Assert.Equal(0, d.TransientProtectionEnable);
        Assert.Equal(0, d.ReductionCurveEnabled);
        Assert.Equal(0u, d.ReductionCurveSize);
    }

    [Fact]
    public void TheEngineOverridesOnlyWhatItNames()
    {
        using var e = Engine();
        var applied = e.AppliedParameters;
        var d = e.LibraryDefaults;
        Assert.Equal(1, applied.AdaptiveNoise);
        Assert.Equal(0, applied.LearnNoise);
        Assert.Equal(0, applied.ResidualListen);
        Assert.Equal(0, applied.SmoothingMode);
        Assert.Equal(0, applied.ReductionCurveEnabled);
        Assert.Equal(IntPtr.Zero, applied.ReductionCurveBias);
        Assert.Equal((int)SpecbleachNoiseEstimator.SppMmse, applied.NoiseEstimationMethod);
        // Everything not named is the library's own.
        Assert.Equal(d.TonalReductionGain, applied.TonalReductionGain);
        Assert.Equal(d.NoiseProfileScale, applied.NoiseProfileScale);
        Assert.Equal(d.TonalNoiseProfileScale, applied.TonalNoiseProfileScale);
        Assert.Equal(d.Aggressiveness, applied.Aggressiveness);
        Assert.Equal(d.DfttStrength, applied.DfttStrength);
        Assert.Equal(d.TransientProtectionEnable, applied.TransientProtectionEnable);
    }

    [Fact]
    public void TheLibraryRefusesABlockOfAnyOtherSize()
    {
        using var e = Engine();
        var block = e.AppliedParameters;
        Assert.True(e.LoadParametersForTest(block, SpecbleachEngine.ParametersSize));
        Assert.False(e.LoadParametersForTest(block, SpecbleachEngine.ParametersSize + 4));
        Assert.False(e.LoadParametersForTest(block, SpecbleachEngine.ParametersSize - 4));
    }

    [Fact]
    public void EachDescriptorsDefaultIsTheLibrarysDefault()
    {
        using var e = Engine();
        var d = e.LibraryDefaults;
        double Default(string name) => SpecbleachEngine.SettingDescriptors.Single(s => s.Name == name).Default.Number;
        Assert.Equal(-20 * Math.Log10(d.ReductionGain), Default("reduction_db"), 3);
        Assert.Equal(d.SmoothingFactor, Default("smoothing"), 6);
        Assert.Equal(d.WhiteningFactor, Default("whitening"), 6);
        Assert.Equal(d.MaskingDepth, Default("masking_depth"), 6);
        Assert.Equal(d.SuppressionStrength, Default("suppression"), 6);
        Assert.All(SpecbleachEngine.SettingDescriptors, s => Assert.True(s.Accepts(s.Default), s.Name));
        Assert.All(SpecbleachEngine.SettingDescriptors, s => Assert.False(s.ChangesLatency, s.Name));
        Assert.All(SpecbleachEngine.SettingDescriptors, s => Assert.NotEqual(s.LabelKey, Lexicon.Get(s.LabelKey)));
        Assert.Equal("audio.denoise.db", SpecbleachEngine.SettingDescriptors.Single(s => s.Name == "reduction_db").UnitKey);
    }

    // ---- Failing cleanly ----

    [Fact]
    public void AMissingLibraryIsUnavailableWithAReasonAndNoThrow()
    {
        string nowhere = Path.Combine(Path.GetTempPath(), "jjflex-no-such-dir-" + Guid.NewGuid().ToString("N"), SpecbleachEngine.LibraryFileName);
        using var e = new SpecbleachEngine(libraryPath: nowhere);
        Assert.False(e.IsAvailable);
        Assert.Equal(Unavailability.LibraryMissing, e.Unavailability);
        Assert.Contains("was not found", e.UnavailabilityDetail);
        Assert.Equal(Unavailability.LibraryMissing, SpecbleachEngine.ProbeAt(nowhere));
        Assert.Throws<InvalidOperationException>(() => e.ProcessFrame(new float[Block], new float[Block]));
    }

    /// <summary>
    /// The pair that proves the probe never loads the library: on a file that
    /// is present but is not a DLL, the probe (which only looks) says
    /// available, and the engine (which loads) says it could not start.
    /// </summary>
    [Fact]
    public void AFileThatIsNotADllPassesTheProbeAndFailsTheEngine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-not-a-dll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string fake = Path.Combine(dir, SpecbleachEngine.LibraryFileName);
        File.WriteAllText(fake, "This is not a DLL.");
        try
        {
            Assert.Equal(Unavailability.None, SpecbleachEngine.ProbeAt(fake));
            using var e = new SpecbleachEngine(libraryPath: fake);
            Assert.False(e.IsAvailable);
            Assert.Equal(Unavailability.InitFailed, e.Unavailability);
            Assert.Contains("would not load", e.UnavailabilityDetail);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- What it does to audio ----

    [Fact]
    public void ItsDelayIsWhatItReports()
    {
        using var e = Engine();
        Assert.Equal(512, e.LatencySamples);
        float[] speech = ReferenceSpeech(startSeconds: 12, seconds: 4);
        var output = Run(e, speech);
        int measured = PeakLag(speech, output, maxLag: 4800);
        _output.WriteLine($"NR4 latency: reported {e.LatencySamples}, measured {measured} by cross-correlation on speech.");
        Assert.InRange(measured, e.LatencySamples - 24, e.LatencySamples + 24);
    }

    /// <summary>
    /// The threshold below was set from the first measurement (recorded in the
    /// track report): the noise between bursts falls by well over the
    /// threshold at the default 20 dB, and the 0 dB run, the positive
    /// control, does not reach it.
    /// </summary>
    [Fact]
    public void ItRemovesNoiseBetweenBurstsAndKeepsTheTone()
    {
        var signal = ToneBurstsInNoise(seconds: 10, out bool[] toneOn);
        var at20 = Measure(Engine(20), signal, toneOn);
        var at0 = Measure(Engine(0), signal, toneOn);
        _output.WriteLine($"NR4 at 20 dB: noise between bursts down {at20.NoiseDropDb:F1} dB, tone {at20.ToneChangeDb:+0.00;-0.00;0.00} dB.");
        _output.WriteLine($"NR4 at 0 dB (positive control): noise down {at0.NoiseDropDb:F1} dB, tone {at0.ToneChangeDb:+0.00;-0.00;0.00} dB.");
        Assert.True(RemovesNoise(at20), $"NR4 at 20 dB took the noise down only {at20.NoiseDropDb:F1} dB");
        Assert.InRange(at20.ToneChangeDb, -ToneToleranceDb, ToneToleranceDb);
        // The same assertion, at no reduction, must fail: otherwise it would
        // pass on an engine that does nothing.
        Assert.False(RemovesNoise(at0), $"the 0 dB control passed the removal threshold ({at0.NoiseDropDb:F1} dB)");
    }

    private const double NoiseDropThresholdDb = 10;
    private const double ToneToleranceDb = 3;
    private static bool RemovesNoise((double NoiseDropDb, double ToneChangeDb) m) => m.NoiseDropDb >= NoiseDropThresholdDb;

    // ---- Settings ----

    [Fact]
    public void BadSettingsAreRefusedAndChangeNothing()
    {
        using var e = Engine();
        var before = SpecbleachEngine.SettingDescriptors.Select(s => e.GetSetting(s.Name)).ToArray();
        Assert.False(e.TrySetSetting("unknown", new(1)));
        Assert.False(e.TrySetSetting("noise_estimator", new(0, "brandt")));
        Assert.False(e.TrySetSetting("reduction_db", new(41)));
        Assert.False(e.TrySetSetting("reduction_db", new(-1)));
        Assert.False(e.TrySetSetting("reduction_db", new(20.5)));
        Assert.False(e.TrySetSetting("reduction_db", new(double.NaN)));
        Assert.False(e.TrySetSetting("reduction_db", new(20, "a choice")));
        Assert.False(e.TrySetSetting("smoothing", new(1.05)));
        Assert.False(e.TrySetSetting("whitening", new(0.33)));
        Assert.False(e.TrySetSetting("masking_depth", new(-0.05)));
        Assert.False(e.TrySetSetting("suppression", new(2)));
        Assert.Throws<ArgumentException>(() => e.GetSetting("unknown"));
        Assert.Equal(before, SpecbleachEngine.SettingDescriptors.Select(s => e.GetSetting(s.Name)).ToArray());
        e.ProcessFrame(new float[Block], new float[Block]);
        Assert.Equal(1, e.ParameterLoads);
    }

    [Fact]
    public void AChangeWaitsForTheNextFrameAndThenTakesEffect()
    {
        using var e = Engine(0);
        var noise = WhiteNoise(seconds: 6, rms: 0.05, seed: 4);
        var out1 = Run(e, noise.AsSpan(0, 3 * Rate).ToArray());

        Assert.Equal(1, e.ParameterLoads);
        Assert.True(e.TrySetSetting("reduction_db", new(30)));
        Assert.Equal(30, e.GetSetting("reduction_db").Number);
        // Recorded, not applied: nothing has been handed to the library yet.
        Assert.Equal(1, e.ParameterLoads);
        Assert.Equal(1f, e.AppliedParameters.ReductionGain);

        var out2 = Run(e, noise.AsSpan(3 * Rate, 3 * Rate).ToArray());
        Assert.Equal(2, e.ParameterLoads);
        Assert.Equal((float)Math.Pow(10, -30 / 20.0), e.AppliedParameters.ReductionGain);
        // Setting the same value again publishes nothing.
        Assert.True(e.TrySetSetting("reduction_db", new(30)));
        e.ProcessFrame(new float[Block], new float[Block]);
        Assert.Equal(2, e.ParameterLoads);

        double before = Rms(out1, 2 * Rate, Rate), after = Rms(out2, 2 * Rate, Rate);
        _output.WriteLine($"Noise RMS at 0 dB {before:G3}, after the change to 30 dB {after:G3} ({20 * Math.Log10(before / after):F1} dB lower).");
        Assert.True(20 * Math.Log10(before / after) >= 10, "the change to 30 dB had no measurable effect");
    }

    [Fact]
    public void TheSlotsCallPatternAllocatesNothing()
    {
        using var e = Engine();
        var input = WhiteNoise(seconds: 1, rms: 0.05, seed: 9).AsSpan(0, Block).ToArray();
        var output = new float[Block];
        var stored = SpecbleachEngine.SettingDescriptors.Select(s => (s.Name, s.Default)).ToArray();
        void OneBuffer()
        {
            foreach (var (name, value) in stored) e.TrySetSetting(name, value);
            e.ProcessFrame(input, output);
        }
        for (int i = 0; i < 50; i++) OneBuffer();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) OneBuffer();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        // A real change reloads the block between frames, and that allocates nothing either.
        e.TrySetSetting("smoothing", new(0.25)); OneBuffer();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) { e.TrySetSetting("reduction_db", new(i % 2 == 0 ? 25 : 15)); e.ProcessFrame(input, output); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void CpuPerFrameIsReported()
    {
        using var e = Engine();
        var noise = WhiteNoise(seconds: 10, rms: 0.05, seed: 11);
        var output = new float[Block];
        for (int pos = 0; pos + Block <= Rate; pos += Block) e.ProcessFrame(noise.AsSpan(pos, Block), output);
        var watch = Stopwatch.StartNew();
        int frames = 0;
        for (int pos = 0; pos + Block <= noise.Length; pos += Block, frames++) e.ProcessFrame(noise.AsSpan(pos, Block), output);
        watch.Stop();
        double usPerFrame = watch.Elapsed.TotalMilliseconds * 1000 / frames;
        _output.WriteLine($"NR4 CPU: {usPerFrame:F1} microseconds per 10 ms frame, {(Environment.Is64BitProcess ? "x64" : "x86")}, one channel ({usPerFrame / 100:F2} percent of real time).");
        Assert.True(usPerFrame < 10_000, "NR4 is slower than real time on this machine");
    }

    // ---- Lifetime ----

    [Fact]
    public void DisposeIsIdempotentAndAFrameAfterItThrows()
    {
        var e = Engine();
        e.ProcessFrame(new float[Block], new float[Block]);
        e.Dispose();
        Assert.False(e.IsAvailable);
        e.Dispose();
        Assert.Throws<InvalidOperationException>(() => e.ProcessFrame(new float[Block], new float[Block]));
    }

    [Fact]
    public void ResetStartsAgainAndKeepsTheSettings()
    {
        using var e = Engine();
        Assert.True(e.TrySetSetting("reduction_db", new(12)));
        e.ProcessFrame(new float[Block], new float[Block]);
        e.Reset();
        Assert.True(e.IsAvailable, e.UnavailabilityDetail);
        Assert.Equal(0, e.FramesProcessed);
        Assert.Equal(12, e.GetSetting("reduction_db").Number);
        Assert.Equal((float)Math.Pow(10, -12 / 20.0), e.AppliedParameters.ReductionGain);
        Assert.Equal(512, e.LatencySamples);
    }

    // ---- The catalog, the slot and the mode policy ----

    [Fact]
    public void TheCatalogRowBuildsItAndIsSpectralForBothDirections()
    {
        var row = new DenoiseEngineCatalog().Find("nr4")!;
        Assert.NotNull(row.Factory);
        Assert.Equal(DenoiseSide.Pc, row.Side);
        Assert.Equal(DenoiseFamily.Spectral, row.Family);
        Assert.True(row.Serves(DenoiseDirection.Receive));
        Assert.True(row.Serves(DenoiseDirection.Transmit));
        Assert.Same(SpecbleachEngine.SettingDescriptors, row.Settings);
        Assert.NotEqual(row.LabelKey, Lexicon.Get(row.LabelKey));
        Assert.All(row.Settings, s => Assert.NotEqual(s.LabelKey, Lexicon.Get(s.LabelKey)));
    }

    [Theory]
    [InlineData("CW", true, true)]
    [InlineData("CWL", true, true)]
    [InlineData("DIGU", true, false)]
    [InlineData("DIGU", false, true)]
    [InlineData("RTTY", true, false)]
    public void TheModePolicyKeepsItInCwAndHoldsItInDigitalModes(string mode, bool voiceModesOnly, bool runs)
    {
        using var p = new RxAudioPipeline(catalog: CatalogWithRealNr4(new ConcurrentBag<SpecbleachEngine>()));
        p.SpectralEnabled = true;
        p.SetCurrentMode("USB");
        Assert.True(p.SelectPcEngine("nr4"));
        Until(() => p.SpectralSlot.RunningEngineId == "nr4", () => p.Process(Stereo(0)));
        p.VoiceModesOnly = voiceModesOnly;
        p.SetCurrentMode(mode);
        bool changed = false;
        for (int i = 1; i <= 20; i++)
        {
            var audio = Stereo(i);
            p.Process(audio);
            changed |= !audio.SequenceEqual(Stereo(i));
        }
        Assert.Equal("nr4", p.SpectralSlot.RunningEngineId);
        Assert.Equal(runs, changed);
    }

    /// <summary>
    /// The real engine through the real slot, switched back and forth with
    /// the old captured-noise engine while a background thread feeds audio,
    /// including abandoning NR4 mid-warm-up (twice in a row), then disposed
    /// while buffers are still flowing.
    /// </summary>
    /// <remarks>
    /// <b>How the test knows a warm-up was abandoned:</b> the slot installs a
    /// warming bank after a tenth of a second (ten 480-frame buffers). The
    /// abandoned NR4 pair is fed exactly three buffers, so each of its two
    /// engines has processed exactly three frames, NR4 never became the
    /// running engine, no "processing" result for it was ever published, and
    /// both engines end up freed. All four are asserted.
    /// </remarks>
    [Fact]
    public void TheRealEngineSurvivesSwitchingAbandonedWarmUpsAndDisconnect()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-nr4-stress-" + Guid.NewGuid().ToString("N"));
        var made = new ConcurrentBag<SpecbleachEngine>();
        using var buildEntered = new ManualResetEventSlim(); using var buildRelease = new ManualResetEventSlim();
        bool holdBuild = false;
        var catalog = CatalogWithRealNr4(made, () => {
            if (Volatile.Read(ref holdBuild)) { buildEntered.Set(); Assert.True(buildRelease.Wait(5000)); }
        });
        var p = new RxAudioPipeline(catalog: catalog);
        p.SpectralEnabled = true;
        Assert.True(p.LoadNoiseProfile(ProfileFile(dir)));
        Assert.Equal("spectral_capture", p.SpectralSlot.RunningEngineId);
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        using var run = new AutoResetEvent(false); using var done = new AutoResetEvent(false);
        bool stop = false, freeRun = false; int requested = 0;
        var processing = Task.Run(() => {
            int n = 0;
            while (true) {
                if (!Volatile.Read(ref freeRun)) { run.WaitOne(); if (Volatile.Read(ref stop)) return; }
                else if (Volatile.Read(ref stop)) return;
                int count = Volatile.Read(ref freeRun) ? 1 : Volatile.Read(ref requested);
                for (int i = 0; i < count; i++) p.Process(Stereo(n++));
                if (!Volatile.Read(ref freeRun)) done.Set();
            }
        });
        void Step(int buffers) {
            Volatile.Write(ref requested, buffers); run.Set();
            Assert.True(done.WaitOne(5000), "background audio did not finish its batch");
        }
        try
        {
            Step(1);
            for (int round = 0; round < 3; round++)
            {
                while (events.TryDequeue(out _)) { }
                var previous = made.ToArray();

                // A warm-up abandoned by switching away and straight back
                // again before the slot has seen a single buffer in between,
                // then the second warm-up abandoned too.
                var before = made.ToArray();
                Assert.True(p.SelectPcEngine("nr4")); Until(() => p.SpectralSlot.BuildDelivered("nr4"));
                Step(3); // fewer than ten: processing, but cannot install
                var first = made.Where(e => !before.Contains(e)).ToArray();
                Assert.Equal(2, first.Length);
                Assert.All(first, e => Assert.Equal(3, e.FramesProcessed));
                Assert.Equal("spectral_capture", p.SpectralSlot.RunningEngineId);
                Assert.True(p.SelectPcEngine("spectral_capture"));
                Assert.True(p.SelectPcEngine("nr4")); Until(() => p.SpectralSlot.BuildDelivered("nr4"));
                Step(3);
                var second = made.Where(e => !before.Contains(e) && !first.Contains(e)).ToArray();
                Assert.Equal(2, second.Length);
                Assert.All(second, e => Assert.Equal(3, e.FramesProcessed));
                Assert.All(first, e => Assert.Equal(3, e.FramesProcessed)); // never fed again once replaced
                Assert.Equal("spectral_capture", p.SpectralSlot.RunningEngineId);
                Until(() => first.All(e => !e.IsAvailable));
                Assert.True(p.SelectPcEngine("spectral_capture")); Step(1);
                Until(() => second.All(e => !e.IsAvailable));
                Assert.DoesNotContain(events, e => e.EngineId == "nr4");

                // Now let it finish, then go back.
                Assert.True(p.SelectPcEngine("nr4")); Until(() => p.SpectralSlot.BuildDelivered("nr4"));
                Step(10); Assert.Equal("nr4", p.SpectralSlot.RunningEngineId);
                Until(() => events.Any(e => e.EngineId == "nr4" && e.Processing));
                Assert.True(p.SelectPcEngine("spectral_capture")); Until(() => p.SpectralSlot.BuildDelivered("spectral_capture"));
                Step(10); Assert.Equal("spectral_capture", p.SpectralSlot.RunningEngineId);
                var thisRound = made.Where(e => !previous.Contains(e)).ToArray();
                Assert.Equal(6, thisRound.Length);
                Until(() => thisRound.All(e => !e.IsAvailable));
            }

            // Disconnect with NR4 running and audio flowing freely: the
            // pipeline must wait for the buffer in flight before freeing it.
            Assert.True(p.SelectPcEngine("nr4")); Until(() => p.SpectralSlot.BuildDelivered("nr4"));
            Step(10); Assert.Equal("nr4", p.SpectralSlot.RunningEngineId);
            var live = made.Where(e => e.IsAvailable).ToArray();
            Assert.Equal(2, live.Length);
            Volatile.Write(ref freeRun, true); run.Set();
            Until(() => live.All(e => e.FramesProcessed > 20));
            p.Dispose();
            Until(() => made.All(e => !e.IsAvailable));
            Volatile.Write(ref stop, true);
            Assert.True(processing.Wait(5000));

            // And a build still inside the factory when the disconnect comes:
            // it finishes after the slot has closed, and is freed, never installed.
            using var q = new RxAudioPipeline(catalog: catalog);
            q.SpectralEnabled = true;
            int count = made.Count;
            Volatile.Write(ref holdBuild, true);
            Assert.True(q.SelectPcEngine("nr4"));
            Assert.True(buildEntered.Wait(5000));
            q.Dispose();
            buildRelease.Set();
            Until(() => made.Count == count + 2 && made.All(e => !e.IsAvailable));
            Assert.NotEqual("nr4", q.SpectralSlot.RunningEngineId);
            _output.WriteLine($"Built and freed {made.Count} NR4 engines across the run.");
        }
        finally
        {
            buildRelease.Set(); Volatile.Write(ref stop, true); run.Set();
            Assert.True(processing.Wait(5000));
            p.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---- Helpers ----

    internal static DenoiseEngineCatalog CatalogWithRealNr4(ConcurrentBag<SpecbleachEngine> made, Action? beforeBuild = null)
    {
        string lib = LibraryPath();
        var defaults = new DenoiseEngineCatalog();
        return new DenoiseEngineCatalog(defaults.Entries.Select(e => e.Id == "nr4"
            ? e with { Factory = initial => {
                beforeBuild?.Invoke();
                var engine = new SpecbleachEngine(initial, lib);
                Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
                made.Add(engine); return engine;
            } } : e));
    }

    private static string ProfileFile(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "profile.xml");
        var serializer = new XmlSerializer(typeof(NoiseProfileData));
        using var output = File.Create(path);
        serializer.Serialize(output, new NoiseProfileData { Name = "test", FftSize = 1024, SampleRate = 48000, Magnitudes = Enumerable.Repeat(0.01, 1024).ToArray() });
        return path;
    }

    private static void Until(Func<bool> predicate, Action? step = null)
    {
        var time = Stopwatch.StartNew();
        while (!predicate() && time.ElapsedMilliseconds < 5000) { step?.Invoke(); Thread.Sleep(1); }
        Assert.True(predicate(), "condition did not become true");
    }

    /// <summary>One 10 ms stereo packet of tone and noise, deterministic by index.</summary>
    private static float[] Stereo(int index)
    {
        var rng = new Random(index);
        var a = new float[2 * Block];
        for (int i = 0; i < Block; i++)
        {
            int n = index * Block + i;
            float s = (float)(0.2 * Math.Sin(2 * Math.PI * 700 * n / Rate) + (rng.NextDouble() - 0.5) * 0.05);
            a[2 * i] = s; a[2 * i + 1] = s;
        }
        return a;
    }

    private static float[] Run(IDenoiseEngine e, float[] input)
    {
        var output = new float[input.Length / Block * Block];
        for (int pos = 0; pos + Block <= output.Length; pos += Block)
            e.ProcessFrame(input.AsSpan(pos, Block), output.AsSpan(pos, Block));
        return output;
    }

    internal static float[] WhiteNoise(double seconds, double rms, int seed)
    {
        var rng = new Random(seed);
        var a = new float[(int)(seconds * Rate)];
        double scale = rms * Math.Sqrt(12); // uniform on [-0.5, 0.5] has RMS 1/sqrt(12)
        for (int i = 0; i < a.Length; i++) a[i] = (float)((rng.NextDouble() - 0.5) * scale);
        return a;
    }

    /// <summary>
    /// A 1 kHz tone at 0.2 peak, on for half a second and off for half a
    /// second, over steady white noise at 0.02 RMS: the noise between the
    /// bursts is what a noise reducer should take away, the tone is what it
    /// should leave.
    /// </summary>
    internal static float[] ToneBurstsInNoise(double seconds, out bool[] toneOn)
    {
        var a = WhiteNoise(seconds, 0.02, seed: 808);
        toneOn = new bool[a.Length];
        for (int n = 0; n < a.Length; n++)
        {
            toneOn[n] = (n / (Rate / 2)) % 2 == 0;
            if (toneOn[n]) a[n] += (float)(0.2 * Math.Sin(2 * Math.PI * 1000 * n / Rate));
        }
        return a;
    }

    /// <summary>
    /// Over the last half of the run (the estimator settled), the noise RMS
    /// in the middle of each gap, output against input, and the tone's level
    /// at 1 kHz in the middle of each burst, output against input, both in
    /// dB. The output is aligned by the engine's reported delay.
    /// </summary>
    internal static (double NoiseDropDb, double ToneChangeDb) Measure(IDenoiseEngine e, float[] input, bool[] toneOn)
    {
        float[] output;
        int delay;
        using (e) { output = Run(e, input); delay = e.LatencySamples; }
        double gapIn = 0, gapOut = 0; int gapN = 0;
        double toneIn = 0, toneOut = 0; int bursts = 0;
        int half = Rate / 2, guard = Rate / 10;
        for (int start = input.Length / 2 / half * half; start + half + delay <= output.Length; start += half)
        {
            int from = start + guard, len = half - 2 * guard;
            if (toneOn[start])
            {
                toneIn += Goertzel(input, from, len, 1000);
                toneOut += Goertzel(output, from + delay, len, 1000);
                bursts++;
            }
            else
            {
                for (int i = 0; i < len; i++)
                {
                    gapIn += (double)input[from + i] * input[from + i];
                    gapOut += (double)output[from + delay + i] * output[from + delay + i];
                }
                gapN += len;
            }
        }
        Assert.True(gapN > 0 && bursts > 0);
        return (10 * Math.Log10(gapIn / Math.Max(gapOut, 1e-30)), 20 * Math.Log10(toneOut / toneIn));
    }

    /// <summary>Amplitude of one frequency over a span (Goertzel, normalised so a sine of amplitude A reads A).</summary>
    internal static double Goertzel(float[] x, int from, int len, double hz)
    {
        double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = 0; i < len; i++) { double s0 = x[from + i] + c * s1 - s2; s2 = s1; s1 = s0; }
        double power = s1 * s1 + s2 * s2 - c * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(power, 0)) / len;
    }

    private static double Rms(float[] x, int from, int len)
    {
        double sum = 0;
        for (int i = 0; i < len; i++) sum += (double)x[from + i] * x[from + i];
        return Math.Sqrt(sum / len);
    }

    private static float[] ReferenceSpeech(double startSeconds, double seconds)
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");
        using var reader = new WaveFileReader(path);
        var sp = reader.ToSampleProvider();
        Assert.Equal(48000, sp.WaveFormat.SampleRate);
        Assert.Equal(1, sp.WaveFormat.Channels);
        var all = new float[(int)reader.SampleCount];
        int got = 0, n;
        while (got < all.Length && (n = sp.Read(all.AsSpan(got))) > 0) got += n;
        int start = (int)(startSeconds * 48000);
        int len = Math.Min((int)(seconds * 48000), got - start);
        return all.AsSpan(start, len).ToArray();
    }

    private static int PeakLag(float[] a, float[] b, int maxLag)
    {
        int n = Math.Min(a.Length, b.Length) - maxLag;
        int best = 0;
        double bestV = double.MinValue;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (double)a[i] * b[i + lag];
            if (sum > bestV) { bestV = sum; best = lag; }
        }
        return best;
    }
}
