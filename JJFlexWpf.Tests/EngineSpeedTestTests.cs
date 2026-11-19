using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using Engine = JJFlexWpf.Tests.EngineKeepUpPipelineTests.Engine;

namespace JJFlexWpf.Tests;

/// <summary>
/// The Audio Workshop's engine speed test and the one measuring core it shares
/// with the console kit (Sprint 49 Track L, #866). Windowless: the section's
/// controls are checked from source, never by building the dialog.
/// </summary>
public sealed class EngineSpeedTestTests
{
    public EngineSpeedTestTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static DenoiseEngineEntry Row(string id, DenoiseFamily family, Func<IDenoiseEngine> make, Unavailability probe = Unavailability.None) =>
        new(id, DenoiseSide.Pc, family, "audio.denoise." + id, new[] { DenoiseDirection.Receive },
            (_, _) => make(), () => probe);

    private static readonly float[] OneSecond = EngineSpeedCore.GeneratedNoise(1);

    private static EngineSpeedResult TimeOne(double spinMsPerChannel) =>
        EngineSpeedTest.Time(new[] { Row("x", DenoiseFamily.Neural, () => new Engine("x", () => spinMsPerChannel)) }, OneSecond, () => throw new InvalidOperationException());

    [Fact]
    public void The_verdict_uses_the_self_benchmarks_half_period_margin()
    {
        Assert.Equal(EngineSpeedVerdict.KeepsUp, EngineSpeedTest.Judge(1, 4.99));
        Assert.Equal(EngineSpeedVerdict.Tight, EngineSpeedTest.Judge(1, 5));
        Assert.Equal(EngineSpeedVerdict.Tight, EngineSpeedTest.Judge(9.99, 20));
        Assert.Equal(EngineSpeedVerdict.FallsBehind, EngineSpeedTest.Judge(10, 10));
    }

    [Fact]
    public void A_fast_engine_keeps_up_and_a_deliberately_slow_one_falls_behind()
    {
        var fast = TimeOne(0);
        Assert.Equal(EngineSpeedVerdict.KeepsUp, fast.Verdict);
        Assert.Equal(100, fast.Figures!.Frames);
        // Positive controls: 3 ms per channel is 6 ms a stereo packet; 6 ms is 12.
        var tight = TimeOne(3);
        Assert.Equal(EngineSpeedVerdict.Tight, tight.Verdict);
        var slow = TimeOne(6);
        Assert.Equal(EngineSpeedVerdict.FallsBehind, slow.Verdict);
        Assert.True(slow.PercentUsed >= 100, slow.PercentUsed.ToString());
    }

    [Fact]
    public void Run_times_each_engine_then_each_pairing_reports_the_helper_from_its_own_figures_and_never_builds_it()
    {
        var built = new List<string>();
        IDenoiseEngine Make(string id) { built.Add(id); return new Engine(id); }
        var catalog = new DenoiseEngineCatalog(new[]
        {
            Row("s1", DenoiseFamily.Spectral, () => Make("s1")),
            Row("n1", DenoiseFamily.Neural, () => Make("n1")),
            Row("n2", DenoiseFamily.Neural, () => throw new InvalidOperationException("an unavailable engine must not be built"), Unavailability.LibraryMissing),
            Row("nnr", DenoiseFamily.Neural, () => throw new InvalidOperationException("the helper must never be started by the speed test")),
            new DenoiseEngineEntry("radio_nr", DenoiseSide.Radio, null, "audio.denoise.radio_nr", new[] { DenoiseDirection.Receive }),
        });
        var costs = new HelperModelCostRecord
        {
            Architecture = HelperModelCostStore.ThisArchitecture,
            Slots =
            {
                new() { Engine = "nnr", Slot = 0, Option = "standard", Frames = 200, MedianNanos = 1_700_000, P99Nanos = 2_400_000 },
                new() { Engine = "nnr", Slot = 3, Option = "premium_cw", Frames = 0 },
                new() { Engine = "nnr", Slot = 1, Option = "premium", Frames = 200, MedianNanos = 4_000_000, P99Nanos = 7_000_000 },
            },
        };
        var seen = new List<EngineSpeedResult>();
        var results = EngineSpeedTest.Run(catalog, costs, seen.Add, seconds: 1);
        Assert.Equal(results, seen);
        Assert.Equal(new[] { "s1", "n1", "n2", "s1+n1", "s1+n2", "nnr", "nnr" }, results.Select(r => string.Join("+", r.EngineIds)));
        Assert.Equal(EngineSpeedVerdict.Unavailable, results[2].Verdict);
        Assert.Equal(Unavailability.LibraryMissing, results[2].Reason);
        Assert.Equal(EngineSpeedVerdict.Unavailable, results[4].Verdict);
        Assert.Equal(EngineSpeedVerdict.KeepsUp, results[3].Verdict);
        Assert.Equal(("standard", EngineSpeedVerdict.KeepsUp), (results[5].Model, results[5].Verdict));
        Assert.Equal(("premium", EngineSpeedVerdict.Tight), (results[6].Model, results[6].Verdict));
        Assert.Null(results[5].Figures);
        Assert.DoesNotContain("nnr", built);
        Assert.DoesNotContain("n2", built);

        // Not measured yet: one line saying so.
        var none = EngineSpeedTest.HelperLines(catalog.Find("nnr")!, null).Single();
        Assert.Equal(EngineSpeedVerdict.NotMeasured, none.Verdict);
    }

    [Fact]
    public void Every_line_has_words_that_name_its_engine()
    {
        string Name(string id) => "Engine-" + id;
        var lines = new[]
        {
            new EngineSpeedResult(new[] { "a" }, null, EngineSpeedVerdict.KeepsUp, TimeOne(0).Figures, 1, 2),
            new EngineSpeedResult(new[] { "a", "b" }, null, EngineSpeedVerdict.Tight, TimeOne(0).Figures, 4, 6),
            new EngineSpeedResult(new[] { "a" }, null, EngineSpeedVerdict.FallsBehind, TimeOne(0).Figures, 12, 14),
            new EngineSpeedResult(new[] { "nnr" }, "premium", EngineSpeedVerdict.KeepsUp, null, 1, 2),
            new EngineSpeedResult(new[] { "nnr" }, "premium", EngineSpeedVerdict.Tight, null, 4, 6),
            new EngineSpeedResult(new[] { "nnr" }, "premium", EngineSpeedVerdict.FallsBehind, null, 12, 14),
            new EngineSpeedResult(new[] { "nr2" }, null, EngineSpeedVerdict.NotMeasured, null, 0, 0),
            new EngineSpeedResult(new[] { "a" }, null, EngineSpeedVerdict.Unavailable, null, 0, 0, Unavailability.LibraryMissing),
            new EngineSpeedResult(new[] { "a" }, null, EngineSpeedVerdict.Failed, null, 0, 0),
        };
        foreach (var line in lines)
        {
            string words = EngineSpeedTest.Words(line, Name);
            Assert.DoesNotMatch(@"^audio\.", words);
            Assert.DoesNotContain("{", words);
            Assert.Contains("Engine-" + line.EngineIds[0], words);
            if (line.EngineIds.Count > 1) Assert.Contains("Engine-" + line.EngineIds[1], words);
            if (line.Model == "premium") Assert.Contains(Lexicon.Get("audio.denoise.nnr.model.premium"), words);
        }
    }

    [Fact]
    public void The_generated_noise_is_the_kits_signal_sample_for_sample()
    {
        // The kit's own formula before it moved into the shared core.
        var r = new Random(49);
        double Gaussian() { double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble(); return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2); }
        var old = Enumerable.Range(0, 2 * 48000).Select(_ => (float)(Gaussian() * .025)).ToArray();
        Assert.Equal(old, EngineSpeedCore.GeneratedNoise(2));
    }

    [Fact]
    public void The_console_kit_measures_through_the_shared_core_not_a_loop_of_its_own()
    {
        string root = RepoPaths.RequireRoot();
        string project = File.ReadAllText(Path.Combine(root, "tools", "nrbench", "nrbench.csproj"));
        Assert.Contains(@"..\..\JJFlexWpf\EngineSpeedCore.cs", project);
        string cpu = File.ReadAllText(Path.Combine(root, "tools", "nrbench", "Cpu.cs"));
        Assert.Contains("EngineSpeedCore.Time(", cpu);
        Assert.DoesNotContain("Stopwatch.GetTimestamp", cpu);
        string measures = File.ReadAllText(Path.Combine(root, "tools", "nrbench", "Measures.cs"));
        Assert.Contains("EngineSpeedCore.GeneratedNoise(", measures);
    }

    [Fact]
    public void The_speed_test_is_its_own_section_beside_bench_tools_and_needs_no_radio()
    {
        string dialogs = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Dialogs");
        string section = File.ReadAllText(Path.Combine(dialogs, "AudioWorkshopDialog.EngineSpeed.cs"));
        string code = Regex.Replace(section, @"//.*|/\*[\s\S]*?\*/|<[^>]*>", "");
        Assert.Contains("AddSectionHeader(DiagnosticsContent", code);
        Assert.DoesNotContain("AddRadioSection(", code);
        Assert.DoesNotContain("AddRadioControl(", code);
        string page = File.ReadAllText(Path.Combine(dialogs, "AudioWorkshopDialog.Diagnostics.cs"));
        int bench = page.IndexOf("BuildBenchToolsSection();", StringComparison.Ordinal);
        int speed = page.IndexOf("BuildEngineSpeedSection();", StringComparison.Ordinal);
        Assert.True(bench >= 0 && speed > bench, "the speed test section is built right after Bench tools");
    }
}
