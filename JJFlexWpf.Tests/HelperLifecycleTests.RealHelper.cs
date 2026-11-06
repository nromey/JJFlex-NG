using System;
using System.IO;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// JJ Flexible's real client against the real WDSP helper, built in its own
/// repository: the interoperation test Track A's report leaves to this side.
/// Windowless and silent (the helper is a hidden console program with no
/// audio device), in its own job like every stub. The path comes from
/// JJFLEX_WDSP_HELPER, else the helper repository's Release build for this
/// architecture. When neither exists the tests say so in their output and
/// pass, because the helper is a separate repository a machine may not hold.
/// </summary>
public sealed partial class HelperLifecycleTests
{
    private static string? RealHelper()
    {
        string? path = Environment.GetEnvironmentVariable("JJFLEX_WDSP_HELPER");
        path ??= Path.Combine(@"C:\dev\jjflex-wdsp-helper\build", (Environment.Is64BitProcess ? "x64" : "x86") + "-Release", "jjflex-wdsp-helper.exe");
        return File.Exists(path) ? path : null;
    }

    private bool NoRealHelper()
    {
        if (RealHelper() != null) return false;
        _out.WriteLine("NOT RUN: the real helper is not built on this machine (set JJFLEX_WDSP_HELPER to its path)");
        return true;
    }

    /// <summary>
    /// The handshake validates, both engines open with the delays Track A
    /// measured (NNR 2,454 samples on its standard model, NR2 4,064), every
    /// frame is answered once the pipeline is full, the output is finite, and
    /// a setting round-trips through its ack.
    /// </summary>
    [Fact]
    public void Real_helper_both_engines_open_answer_every_frame_and_take_a_setting()
    {
        if (NoRealHelper()) return;
        using var rig = new HelperTestRig(path: RealHelper());
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        _out.WriteLine($"nnr: {nnr.UnavailabilityDetail}; nr2: {nr2.UnavailabilityDetail}");
        Assert.True(nnr.IsAvailable, nnr.UnavailabilityDetail);
        Assert.True(nr2.IsAvailable, nr2.UnavailabilityDetail);
        var hello = rig.Host.CurrentConnection!.Hello!;
        _out.WriteLine($"helper {hello.HelperVersion}, WDSP {hello.WdspVersion}, FFTW {hello.FftwVersion}, {hello.Fingerprints.Count} fingerprints");
        Assert.Equal(4, hello.Fingerprints.Count);
        Assert.Equal(2454 + 2 * 480, nnr.LatencySamples);
        Assert.Equal(4064 + 2 * 480, nr2.LatencySamples);
        Assert.Equal("standard", nnr.GetSetting("model").Choice);
        Assert.Equal(new[] { "standard", "premium", "standard_cw", "premium_cw" },
            nnr.Settings.First(d => d.Name == "model").Options!.Select(o => o.Name));

        var rng = new Random(49);
        var input = new float[480]; var output = new float[480];
        int answered = 0, frames = 60;
        for (int i = 0; i < frames; i++)
        {
            for (int s = 0; s < input.Length; s++) input[s] = (float)(rng.NextDouble() - 0.5) * 0.2f;
            foreach (var e in new[] { nnr, nr2 })
            {
                HelperTestRig.Pace(e);
                if (e.ProcessFrame(input, output))
                {
                    answered++;
                    Assert.All(output, x => Assert.True(float.IsFinite(x)));
                }
            }
        }
        // Each engine's first k answers are discarded start-up history (its delay rounded up to whole frames, plus k).
        int expected = 2 * frames - 2 * 2 - (2454 + 479) / 480 - (4064 + 479) / 480;
        _out.WriteLine($"{answered} answered, {expected} expected");
        Assert.True(answered >= expected, $"{answered} frames answered, {expected} expected");

        Assert.True(nr2.TrySetSetting("gain_method", new EngineSettingValue(0, "trained")));
        Assert.True(nnr.TrySetSetting("mask_floor_db", new EngineSettingValue(-30)));
        HelperTestRig.Until(() => nr2.GetSetting("gain_method").Choice == "trained" && nnr.GetSetting("mask_floor_db").Number == -30,
            "both settings acknowledged");
        Assert.Equal(0, Interlocked.Read(ref rig.Host.CloseAcksUnexpected));
        Assert.Equal(0, rig.Host.Failures);
    }

    /// <summary>The real helper's own exit before it connects (2, a bad command line) is named, not left a bare number.</summary>
    [Fact]
    public void Real_helper_an_exit_before_connecting_is_named()
    {
        if (NoRealHelper()) return;
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--no-such-argument" } : Array.Empty<string>(),
            SlowRestart(TimeSpan.FromSeconds(3)), RealHelper());
        var engine = rig.Engine("nr2");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to exit");
        _out.WriteLine(engine.UnavailabilityDetail);
        Assert.Contains("the helper exited with code 2 (a bad command line)", engine.UnavailabilityDetail);
        Assert.Equal(WdspCondition.ProcessExited, rig.Host.LastFailureCondition);
    }

    /// <summary>
    /// A value changed as fast as a thread can change it, on both engines of a
    /// real helper running audio, never takes the helper near its limit: no
    /// protocol error, the cap holds, and the last value is the one in effect.
    /// </summary>
    [Fact]
    public void Real_helper_a_flood_of_setting_changes_never_ends_the_connection()
    {
        if (NoRealHelper()) return;
        using var rig = new HelperTestRig(path: RealHelper());
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        Assert.True(nnr.IsAvailable && nr2.IsAvailable);
        int changes = 0;
        using (new Feeder(nnr, nr2))
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            double[] stops = { -40, -35, -30, -25, -20, -15 };
            while (t.ElapsedMilliseconds < 3000)
            {
                nnr.TrySetSetting("mask_floor_db", new EngineSettingValue(stops[changes % stops.Length]));
                nr2.TrySetSetting("trained_t2", new EngineSettingValue(0.02 + (changes % 28) * 0.01));
                changes++;
            }
            Assert.True(nnr.TrySetSetting("mask_floor_db", new EngineSettingValue(-12)));
            Assert.True(nr2.TrySetSetting("trained_t2", new EngineSettingValue(0.3)));
            HelperTestRig.Until(() => nnr.GetSetting("mask_floor_db").Number == -12 && nr2.GetSetting("trained_t2").Number == 0.3,
                "the last values in effect", 15000);
        }
        _out.WriteLine($"{changes} changes per engine, {Interlocked.Read(ref rig.Host.SetSettingsSent)} sent, " +
            $"peak in flight {Interlocked.Read(ref rig.Host.PeakSettingsInFlight)}");
        Assert.Equal(0, rig.Host.Failures);
        Assert.Single(rig.Host.LaunchedProcessIds);
        Assert.InRange(Interlocked.Read(ref rig.Host.PeakSettingsInFlight), 1, WdspHostPolicy.MaxSettingsInFlightDefault);
    }
}
