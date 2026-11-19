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

    private static DenoiseEngineEntry HelperRow(string id, DenoiseFamily family) =>
        new(id, DenoiseSide.Pc, family, "audio.denoise." + id, new[] { DenoiseDirection.Receive },
            (_, _) => throw new InvalidOperationException("the helper must never be started by the speed test"), () => Unavailability.None,
            SettingDescriptors: DenoiseEngineCatalog.HelperMirror(id));

    private static WdspHelloAck Hello(params (string Id, uint Median, uint P99, ushort Frames)[] engines) =>
        new(1, "test", "0", new string('0', 40), "none", 16, Array.Empty<WdspFingerprint>(),
            engines.Select(e => new WdspEngineOffer(e.Id, 3, 48000, 480, 0, e.Frames, e.Median, e.P99, Array.Empty<EngineSettingDescriptor>())).ToArray());

    private static HelperModelCostRecord NnrModels() => new()
    {
        Architecture = HelperModelCostStore.ThisArchitecture,
        Slots =
        {
            new() { Engine = "nnr", Slot = 0, Option = "standard", Frames = 200, MedianNanos = 1_700_000, P99Nanos = 2_400_000 },
            new() { Engine = "nnr", Slot = 3, Option = "premium_cw", Frames = 0 },
            new() { Engine = "nnr", Slot = 1, Option = "premium", Frames = 200, MedianNanos = 4_000_000, P99Nanos = 7_000_000 },
        },
    };

    [Fact]
    public void Run_times_each_engine_then_each_pairing_reports_the_helper_from_its_own_figures_and_never_builds_it()
    {
        var built = new List<string>();
        IDenoiseEngine Make(string id) { built.Add(id); return new Engine(id); }
        var catalog = new DenoiseEngineCatalog(new[]
        {
            Row("s1", DenoiseFamily.Spectral, () => Make("s1")),
            HelperRow("nr2", DenoiseFamily.Spectral),
            Row("n1", DenoiseFamily.Neural, () => Make("n1")),
            Row("n2", DenoiseFamily.Neural, () => throw new InvalidOperationException("an unavailable engine must not be built"), Unavailability.LibraryMissing),
            HelperRow("nnr", DenoiseFamily.Neural),
            new DenoiseEngineEntry("radio_nr", DenoiseSide.Radio, null, "audio.denoise.radio_nr", new[] { DenoiseDirection.Receive }),
        });
        // NNR's models remembered; NR2 only in the running helper's handshake (NNR's there too, and not used: its models are).
        var figures = HelperSpeedFigures.From(NnrModels(), Hello(("nr2", 1_000_000, 2_000_000, 100), ("nnr", 1_500_000, 2_000_000, 100)));
        var seen = new List<EngineSpeedResult>();
        var results = EngineSpeedTest.Run(catalog, figures, seen.Add, seconds: 1);
        Assert.Equal(results, seen);
        Assert.Equal(new[] { "s1", "n1", "n2", "s1+n1", "s1+n2", "nr2", "nnr", "nnr", "s1+nnr", "nr2+n1", "nr2+n2", "nr2+nnr" },
            results.Select(r => string.Join("+", r.EngineIds)));
        EngineSpeedResult Line(string ids, string? model = null) => results.Single(r => string.Join("+", r.EngineIds) == ids && r.Model == model);
        Assert.Equal(EngineSpeedVerdict.Unavailable, Line("n2").Verdict);
        Assert.Equal(Unavailability.LibraryMissing, Line("n2").Reason);
        Assert.Equal(EngineSpeedVerdict.Unavailable, Line("s1+n2").Verdict);
        Assert.Equal(EngineSpeedVerdict.KeepsUp, Line("s1+n1").Verdict);
        Assert.Equal(EngineSpeedSource.Timed, Line("s1+n1").Source);

        // The helper's lines are stereo: two channels on its one worker.
        Assert.Equal((EngineSpeedVerdict.KeepsUp, EngineSpeedSource.Helper, 2.0), (Line("nr2").Verdict, Line("nr2").Source, Line("nr2").MedianMs));
        Assert.Equal((EngineSpeedVerdict.KeepsUp, 3.4, 4.8), (Line("nnr", "standard").Verdict, Line("nnr", "standard").MedianMs, Line("nnr", "standard").P99Ms));
        Assert.Equal(EngineSpeedVerdict.Tight, Line("nnr", "premium").Verdict);
        Assert.Null(Line("nnr", "standard").Figures);

        // A local half beside a helper half: both keep up, the margin is the busier half's.
        var s1nnr = Line("s1+nnr");
        Assert.Equal((EngineSpeedVerdict.KeepsUp, EngineSpeedSource.Composed, 3.4), (s1nnr.Verdict, s1nnr.Source, s1nnr.MedianMs));
        Assert.Equal(2.0, Line("nr2+n1").MedianMs, 6);
        // A local half that cannot run makes the pairing unavailable, for the same reason.
        Assert.Equal((EngineSpeedVerdict.Unavailable, Unavailability.LibraryMissing), (Line("nr2+n2").Verdict, Line("nr2+n2").Reason));
        // Two helper halves share its one worker: each alone keeps up, together their work adds to little room.
        var both = Line("nr2+nnr");
        Assert.Equal((EngineSpeedVerdict.Tight, 5.4, 8.8), (both.Verdict, Math.Round(both.MedianMs, 6), Math.Round(both.P99Ms, 6)));

        Assert.DoesNotContain("nnr", built);
        Assert.DoesNotContain("nr2", built);
        Assert.DoesNotContain("n2", built);
    }

    [Fact]
    public void A_helper_engine_with_no_model_costs_falls_back_to_the_handshake_and_one_with_no_figures_at_all_is_unknown()
    {
        var nnr = HelperRow("nnr", DenoiseFamily.Neural);
        var nr2 = HelperRow("nr2", DenoiseFamily.Spectral);
        var n1 = Row("n1", DenoiseFamily.Neural, () => new Engine("n1"));

        // An older helper: no model costs, only the handshake's per-engine figure.
        var older = HelperSpeedFigures.From(null, Hello(("nnr", 1_700_000, 2_400_000, 100), ("nr2", 0, 0, 0)));
        var line = Assert.Single(EngineSpeedTest.HelperLines(nnr, older));
        Assert.Equal((EngineSpeedVerdict.KeepsUp, (string?)null, 3.4), (line.Verdict, line.Model, line.MedianMs));
        // A handshake figure with no calibration frames was not measured.
        Assert.Equal(EngineSpeedVerdict.NotMeasured, Assert.Single(EngineSpeedTest.HelperLines(nr2, older)).Verdict);

        // No helper running and nothing remembered: unknown, never guessed, and the pairing says which half.
        Assert.Equal(EngineSpeedVerdict.NotMeasured, Assert.Single(EngineSpeedTest.HelperLines(nnr, null)).Verdict);
        var alone = new Dictionary<string, EngineSpeedResult>
        {
            ["n1"] = new(new[] { "n1" }, null, EngineSpeedVerdict.KeepsUp, null, 1, 2),
        };
        var unknown = EngineSpeedTest.Compose(nr2, n1, alone, HelperSpeedFigures.None);
        Assert.Equal((EngineSpeedVerdict.NotMeasured, "nr2", EngineSpeedSource.Composed), (unknown.Verdict, unknown.Detail, unknown.Source));
        Assert.Equal(EngineSpeedVerdict.NotMeasured, EngineSpeedTest.Compose(nr2, nnr, alone, older).Verdict);

        // Positive control: with both figures it is known, and a busy local half sets the margin.
        alone["n1"] = new(new[] { "n1" }, null, EngineSpeedVerdict.Tight, null, 4, 6);
        var known = EngineSpeedTest.Compose(nr2, n1, alone, HelperSpeedFigures.From(null, Hello(("nr2", 1_000_000, 2_000_000, 100))));
        Assert.Equal((EngineSpeedVerdict.Tight, 4.0, 6.0), (known.Verdict, known.MedianMs, known.P99Ms));

        // A pairing runs NNR at its default model: the standard model's own figure when there is one.
        var models = HelperSpeedFigures.From(NnrModels(), Hello(("nnr", 9_000_000, 9_500_000, 100), ("nr2", 1_000_000, 2_000_000, 100)));
        Assert.Equal("standard", EngineSpeedTest.DefaultModel(nnr));
        Assert.Equal((EngineSpeedVerdict.Tight, 5.4), (EngineSpeedTest.Compose(nr2, nnr, alone, models).Verdict, Math.Round(EngineSpeedTest.Compose(nr2, nnr, alone, models).MedianMs, 6)));
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
            new EngineSpeedResult(new[] { "nnr" }, "premium", EngineSpeedVerdict.KeepsUp, null, 1, 2, Source: EngineSpeedSource.Helper),
            new EngineSpeedResult(new[] { "nnr" }, "premium", EngineSpeedVerdict.Tight, null, 4, 6, Source: EngineSpeedSource.Helper),
            new EngineSpeedResult(new[] { "nnr" }, "premium", EngineSpeedVerdict.FallsBehind, null, 12, 14, Source: EngineSpeedSource.Helper),
            new EngineSpeedResult(new[] { "nr2" }, null, EngineSpeedVerdict.NotMeasured, null, 0, 0, Source: EngineSpeedSource.Helper),
            new EngineSpeedResult(new[] { "a", "nnr" }, null, EngineSpeedVerdict.KeepsUp, null, 1, 2, Source: EngineSpeedSource.Composed),
            new EngineSpeedResult(new[] { "a", "nnr" }, null, EngineSpeedVerdict.Tight, null, 4, 6, Source: EngineSpeedSource.Composed),
            new EngineSpeedResult(new[] { "a", "nnr" }, null, EngineSpeedVerdict.FallsBehind, null, 12, 14, Source: EngineSpeedSource.Composed),
            new EngineSpeedResult(new[] { "a", "nr2" }, null, EngineSpeedVerdict.NotMeasured, null, 0, 0, Detail: "nr2", Source: EngineSpeedSource.Composed),
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
        // Each source has words of its own, so a composed line never reads as one timed here.
        string Key(EngineSpeedResult r) => EngineSpeedTest.Words(r, Name);
        var timed = new EngineSpeedResult(new[] { "a", "nnr" }, null, EngineSpeedVerdict.KeepsUp, null, 1, 2);
        Assert.Equal(3, new[] { timed, timed with { Source = EngineSpeedSource.Helper }, timed with { Source = EngineSpeedSource.Composed } }
            .Select(Key).Distinct().Count());
        Assert.Equal(Lexicon.Get("audio.diagnostics.speed.pair_not_measured", ("engine", Lexicon.Get("audio.diagnostics.speed.pair", ("first", "Engine-a"), ("second", "Engine-nr2"))), ("missing", "Engine-nr2")),
            Key(lines.Single(l => l.Source == EngineSpeedSource.Composed && l.Verdict == EngineSpeedVerdict.NotMeasured)));
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
