using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track CL (#1278): the transmit compressor and limiter through
/// the app's owner (<see cref="TxAudioConditioning"/>), the noise controller's
/// rule book, the saved homes, the audition twin and the editor's model.
/// Windowless: MTA asserted in the constructor; the owner's numerical chain on
/// a radio object that is only its injected fields. No window, device or radio.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxDynamicsSurfaceTests
{
    private const int Rate = 48000;
    private const string Home = "home";

    public TxDynamicsSurfaceTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private sealed class Bridge : IDisposable
    {
        public readonly TxGateOwnerTests.Context X = new();
        public readonly NoiseFixture F = new();
        public Bridge()
        {
            typeof(NoiseFixture).GetField("_transmit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(F, X.Chain);
            F.Noise.TransmitGate = new AppTransmitGate();
            TxAudioConditioning.ApplySetup(new TxNoiseSetup());
            X.Buffer();
        }
        public NoiseController Noise => F.Noise;
        public TxAudioConditioner Cond => X.Cond;
        public void Dispose() { F.Dispose(); X.Dispose(); }
        public void Unkey() { X.Keyed = false; X.Chain.ApplyDeferred(); X.Buffer(); }
    }

    private static TxNoiseSetup Full() => new()
    {
        Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -40, RatioToOne = 2 },
        Compressor = new TxCompressorSetup
        {
            Enabled = true, ThresholdDb = -24, RatioToOne = 4, AttackMs = 3, ReleaseMs = 120, KneeDb = 6, MakeupDb = 9,
            DriveDb = 6, RotatorStages = 4,
        },
        Limiter = new TxLimiterSetup { Enabled = true, CeilingDb = -1, ReleaseMs = 80, LookAheadMs = 2 },
    };

    // ── the key discipline, through the owner ──

    [Fact]
    public void A_switch_made_keyed_waits_for_the_unkey_and_a_lever_is_live_and_never_carries_the_waiting_switch_in()
    {
        using var b = new Bridge();
        b.X.Keyed = true;
        b.X.Buffer();
        // The levers are live while keyed (S15)...
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.SetCompressorLevel("threshold", -30).Timing);
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.SetCompressorLevel("mid.ratio", 7).Timing);
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.SetLimiterLevel("ceiling", -3).Timing);
        // ...and every switch waits for the unkey.
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetCompressorEnabled(true).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetLimiterEnabled(true).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetCompressorRotator(4).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetCompressorSplitBand(true).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetLimiterLevel("lookahead", 5).Timing);
        // A lever turned once something is held goes in order behind it (the one policy every transmit lever follows).
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetCompressorLevel("makeup", 3).Timing);
        b.X.Buffer();
        var comp = b.Cond.Compressor.Settings; var lim = b.Cond.Limiter.Settings;
        Assert.Equal(-30f, comp.ThresholdDb);
        Assert.Equal(7f, comp.Mid.RatioToOne);
        Assert.Equal(-3f, lim.CeilingDb);
        // The live levers did not bring a waiting switch in with them, and the held lever has not landed.
        Assert.False(comp.Enabled);
        Assert.False(lim.Enabled);
        Assert.Equal(0, comp.RotatorStages);
        Assert.False(comp.SplitBand);
        Assert.Equal(0f, lim.LookAheadMs);
        Assert.Equal(TxCompressorSettings.DefaultMakeupDb, comp.MakeupDb);
        // The intent holds what was asked for unkeyed-or-live (settings are intents).
        Assert.Equal(-30f, TxAudioConditioning.CompressorSetup.ThresholdDb);

        b.Unkey();
        comp = b.Cond.Compressor.Settings; lim = b.Cond.Limiter.Settings;
        Assert.True(comp.Enabled);
        Assert.True(lim.Enabled);
        Assert.Equal((4, true, -30f, 7f, 3f), (comp.RotatorStages, comp.SplitBand, comp.ThresholdDb, comp.Mid.RatioToOne, comp.MakeupDb));
        Assert.True(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.True(TxAudioConditioning.LimiterSetup.Enabled);
        Assert.Equal((5f, -3f), (lim.LookAheadMs, lim.CeilingDb));
        Assert.Equal(240, b.Cond.AppliedDelayFrames);
    }

    [Fact]
    public void Every_lever_in_the_tables_reaches_its_own_field_and_an_unknown_one_is_refused()
    {
        using var b = new Bridge();
        foreach (var lever in NoiseController.CompressorLevers.Concat(NoiseController.LimiterLevers))
        {
            double value = lever.Min + lever.Step * 3;
            b.Noise.SetDynamics(lever, value, Home);
            b.Unkey();
            Assert.Equal(value, b.Noise.DynamicsNumber(lever), 3);
            // The chain holds it too (clamped to the same range, so equal here).
            var (c, l) = TxAudioConditioning.CaptureDynamics();
            Assert.Equal(TxDynamicsEditorModel.ValueOf(lever, c, l), b.Noise.DynamicsNumber(lever), 3);
            // The label and the Ctrl+F1 text are keys that exist (DRAFT PROSE until Codex words them).
            Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", Lexicon.Get(lever.LabelKey));
            Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", Lexicon.Get(lever.HelpKey));
        }
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetCompressorLevel("nope", 1).Timing);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetCompressorLevel("low.nope", 1).Timing);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetLimiterLevel("nope", 1).Timing);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetCompressorLevel("threshold", float.NaN).Timing);
        // Each lever key is distinct, and Home's rows are the ones the brief names.
        Assert.Equal(NoiseController.CompressorLevers.Length, NoiseController.CompressorLevers.Select(l => l.Key).Distinct().Count());
        Assert.Equal(new[] { "threshold", "ratio", "makeup" }, NoiseController.CompressorLevers.Where(l => l.OnHome).Select(l => l.Key));
        Assert.Equal(new[] { "ceiling" }, NoiseController.LimiterLevers.Where(l => l.OnHome).Select(l => l.Key));
    }

    // ── presets: starting points ──

    [Fact]
    public void A_preset_fills_every_field_keeps_the_switch_and_stays_the_operators()
    {
        using var b = new Bridge();
        b.Noise.LoadCompressorPreset("dx", Home);
        b.Unkey();
        var now = TxAudioConditioning.CompressorSetup;
        Assert.False(now.Enabled); // loading a preset does not switch the compressor on
        Assert.Equal(("dx", 6f, 4, 5f), (now.PresetId, now.DriveDb, now.RotatorStages, now.RatioToOne));
        Assert.Same(TxCompressorPresets.Find("dx"), b.Noise.CompressorPresetInPlace);
        Assert.Equal(6f, b.Cond.Compressor.Settings.DriveDb);
        Assert.False(b.Cond.Compressor.Settings.Enabled);

        b.Noise.SetCompressorOn(true, Home);
        b.Unkey();
        Assert.Same(TxCompressorPresets.Find("dx"), b.Noise.CompressorPresetInPlace); // on or off is not part of a preset

        // One lever turned: every field is the operator's, and it is no longer that preset.
        b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold")!, -30, Home);
        Assert.Null(b.Noise.CompressorPresetInPlace);
        Assert.Equal(6f, TxAudioConditioning.CompressorSetup.DriveDb); // the rest of the preset stays
        Assert.True(TxAudioConditioning.CompressorSetup.Enabled);

        // A preset loaded keyed waits for the unkey, as a switch.
        b.X.Keyed = true; b.X.Buffer();
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.LoadCompressorPreset("ragchew").Timing);
        b.X.Buffer();
        Assert.Equal(5f, b.Cond.Compressor.Settings.RatioToOne);
        b.Unkey();
        Assert.Equal(2.5f, b.Cond.Compressor.Settings.RatioToOne);
        Assert.True(b.Cond.Compressor.Settings.Enabled);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.LoadCompressorPreset("nope").Timing);
    }

    // ── whole setups ──

    [Fact]
    public void A_whole_setup_lands_on_one_unkeyed_buffer_with_the_gate_and_is_captured_back_the_same()
    {
        using var b = new Bridge();
        b.X.Keyed = true; b.X.Buffer();
        TxAudioConditioning.ApplySetup(Full());
        b.X.Buffer();
        Assert.False(b.Cond.Compressor.Settings.Enabled); // keyed: nothing of it yet
        Assert.False(b.Cond.Gate.Enabled);
        b.Unkey();
        Assert.True(b.Cond.Gate.Enabled);
        Assert.Equal(Full().Compressor.ToSettings(), b.Cond.Compressor.Settings);
        Assert.Equal(Full().Limiter.ToSettings(), b.Cond.Limiter.Settings);
        var captured = TxAudioConditioning.CaptureSetup("");
        Assert.True(NoiseController.SameDynamics((captured.Compressor, captured.Limiter), Full()));
        Assert.Equal(TxAudioConditioner.StageIds.DefaultOrder, captured.StageOrder);
    }

    [Fact]
    public void A_setup_saved_before_these_stages_puts_both_off_and_the_recommended_state_is_a_new_setups()
    {
        using var b = new Bridge();
        TxAudioConditioning.ApplySetup(Full());
        b.Unkey();
        // A setup with neither section (every setup saved before this track): both off, the limiter included.
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Gate = new TxNoiseGateSetup { Enabled = true } });
        b.Unkey();
        Assert.False(b.Cond.Compressor.Settings.Enabled);
        Assert.False(b.Cond.Limiter.Settings.Enabled);

        // "Back to the recommended state": the compressor off, the limiter as a NEW setup has it.
        TxAudioConditioning.ResetToRecommended();
        b.Unkey();
        Assert.False(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.Equal(TxLimiterSetup.NewSetupsStartOn, TxAudioConditioning.LimiterSetup.Enabled);
        Assert.Equal(TxLimiterSetup.NewSetupsStartOn, b.Cond.Limiter.Settings.Enabled);
        Assert.Equal(0f, b.Cond.Limiter.Settings.CeilingDb);
    }

    [Fact]
    public void A_preset_or_setup_through_the_controller_carries_both_stages_and_says_what_changed()
    {
        using var b = new Bridge();
        var setup = Full();
        setup.Name = "Contest";
        // The controller's capture is the owner's.
        TxAudioConditioning.ApplySetup(setup);
        b.Unkey();
        var captured = b.Noise.CaptureTransmitSetup("Contest");
        Assert.True(NoiseController.SameDynamics((captured.Compressor, captured.Limiter), setup));
        // Two compressor settings that differ only in the preset's note act the same.
        var noted = setup.Compressor.Clone(); noted.PresetId = "dx";
        Assert.True(NoiseController.SameCompressor(setup.Compressor, noted));
        noted.MakeupDb += 1;
        Assert.False(NoiseController.SameCompressor(setup.Compressor, noted));
        var lower = setup.Limiter.Clone(); lower.CeilingDb -= 1;
        Assert.False(NoiseController.SameLimiter(setup.Limiter, lower));
    }

    // ── the saved homes: audioConfig.xml and the handoff's setting ──

    [Fact]
    public void The_live_states_home_round_trips_both_stages_and_a_file_from_before_them_has_both_off()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjf-track-cl-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool settingWas = AudioOutputConfig.RadioProcessorOffWithCompressorInForce;
        try
        {
            var config = new AudioOutputConfig { TransmitNoise = Full() };
            config.Save(dir);
            var back = AudioOutputConfig.Load(dir);
            Assert.True(back.TransmitNoise!.SameContentAs(Full()));
            Assert.True(back.RadioProcessorOffWithCompressor); // the default: on

            // The same file as a build before this track wrote it: no Compressor, no Limiter, no setting.
            string path = Directory.GetFiles(dir, "*.xml").Single();
            var doc = System.Xml.Linq.XDocument.Load(path);
            foreach (string name in new[] { "Compressor", "Limiter", "RadioProcessorOffWithCompressor" })
                doc.Descendants(name).ToList().ForEach(x => x.Remove());
            doc.Save(path);
            Assert.DoesNotContain("Limiter", File.ReadAllText(path));
            var old = AudioOutputConfig.Load(dir);
            Assert.False(old.TransmitNoise!.Compressor.Enabled);
            Assert.False(old.TransmitNoise.Limiter.Enabled);
            Assert.True(old.TransmitNoise.Gate.Enabled);
            Assert.True(old.RadioProcessorOffWithCompressor);
            Assert.True(AudioOutputConfig.RadioProcessorOffWithCompressorInForce);

            // The setting, cleared, is saved, read back, and is what the handoff acts on.
            old.RadioProcessorOffWithCompressor = false;
            Assert.False(AudioOutputConfig.RadioProcessorOffWithCompressorInForce);
            old.Save(dir);
            AudioOutputConfig.RadioProcessorOffWithCompressorInForce = true; // as a fresh launch starts
            Assert.False(AudioOutputConfig.Load(dir).RadioProcessorOffWithCompressor);
            Assert.False(AudioOutputConfig.RadioProcessorOffWithCompressorInForce);
        }
        finally
        {
            AudioOutputConfig.RadioProcessorOffWithCompressorInForce = settingWas;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── the handoff's words ──

    [Fact]
    public void Every_handoff_notice_has_its_own_draft_sentence_and_is_said_on_the_handoffs_subject()
    {
        var keys = Enum.GetValues<ProcessorHandoffNotice>().Select(TxAudioConditioning.ProcessorHandoffKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", Lexicon.Get(k)));

        var said = new List<(string Key, string Subject)>();
        TxAudioConditioning.ProcessorHandoffSpeechForTest = (key, subject) => said.Add((key, subject));
        try
        {
            typeof(TxAudioConditioning).GetMethod("OnProcessorHandoffNotice", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { ProcessorHandoffNotice.TurnedOffShared });
        }
        finally { TxAudioConditioning.ProcessorHandoffSpeechForTest = null; }
        Assert.Equal(new[] { ("audio.processor_handoff.turned_off_shared", Radios.Speech.SpeechSubject.ProcessorHandoff) }, said);
    }

    // ── the take header's line ──

    [Fact]
    public void The_take_headers_line_names_the_setup_the_stages_and_both_stages_settings()
    {
        using var b = new Bridge();
        string dir = Path.Combine(Path.GetTempPath(), "jjf-track-cl-setups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new TxNoiseSetupStore(dir);
            var setup = Full(); setup.Name = "Contest";
            Assert.True(store.Save("1234-5678-9012-3456", setup).Succeeded);
            Assert.Equal(TxSetupApplyOutcome.Applied, TxAudioConditioning.ApplyNamedSetup("1234-5678-9012-3456", "Contest", store).Outcome);
            b.Unkey();
            string line = TxAudioConditioning.DescribePcChain();
            Assert.Contains("setup=Contest (as-applied)", line);
            Assert.Contains("stages=expander,compressor,limiter", line);
            Assert.Contains("threshold -24 dBFS peak, ratio 4, attack 3 ms, release 120 ms, knee 6 dB, makeup 9 dB, drive 6 dB, rotator 4", line);
            Assert.Contains("limiter=ceiling -1 dBFS, release 80 ms, look-ahead 2 ms", line);

            // A lever turned since: the header says the chain is no longer that setup as saved.
            TxAudioConditioning.SetCompressorLevel("makeup", 3);
            Assert.Contains("setup=Contest (changed-since-applied)", TxAudioConditioning.DescribePcChain());
            TxAudioConditioning.SetCompressorEnabled(false);
            TxAudioConditioning.SetLimiterEnabled(false);
            b.Unkey();
            string off = TxAudioConditioning.DescribePcChain();
            Assert.Contains("compressor=off; limiter=off", off);
            Assert.Contains("stages=expander;", off);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }

        // The radio-neutral provider never throws and says so when nobody has told it.
        var was = PcTransmitChain.Describe;
        try
        {
            PcTransmitChain.Describe = null;
            Assert.Equal(PcTransmitChain.NotReported, PcTransmitChain.DescribeNow());
            PcTransmitChain.Describe = () => throw new InvalidOperationException("broke");
            Assert.Equal(PcTransmitChain.NotReported, PcTransmitChain.DescribeNow());
            PcTransmitChain.Describe = TxAudioConditioning.DescribePcChain;
            Assert.StartsWith("setup=", PcTransmitChain.DescribeNow());
        }
        finally { PcTransmitChain.Describe = was; }
    }

    [Fact]
    public void The_take_header_asks_the_provider_in_the_one_dictionary_this_track_may_change()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.Listenback.cs"));
        Assert.Equal(1, source.Split("[\"pc-chain\"] = PcTransmitChain.DescribeNow(),").Length - 1);
    }

    // ── the audition twin ──

    private static ChainAuditionTwin Twin(TxNoiseSetup setup, Func<AuditionLiveState?>? live = null) =>
        new(() => setup, live ?? (() => null), () => new DenoiseEngineCatalog(), TimeSpan.FromSeconds(5));

    private static float[] Through(TxAudioConditioner c, float[] mono)
    {
        var output = new float[mono.Length];
        var buf = new float[960];
        for (int at = 0; at < mono.Length; at += 480)
        {
            for (int f = 0; f < 480; f++) { float v = at + f < mono.Length ? mono[at + f] : 0f; buf[2 * f] = v; buf[2 * f + 1] = v; }
            c.Process(buf, 960, Rate);
            for (int f = 0; f < 480 && at + f < output.Length; f++) output[at + f] = buf[2 * f];
        }
        return output;
    }

    private static TxNoiseSetup Only(bool compressor, bool limiter, float lookAheadMs = 0) => new()
    {
        Compressor = new TxCompressorSetup { Enabled = compressor, ThresholdDb = -30, RatioToOne = 4, MakeupDb = 12, DriveDb = 6, RotatorStages = 4 },
        Limiter = new TxLimiterSetup { Enabled = limiter, CeilingDb = -6, LookAheadMs = lookAheadMs },
    };

    [Theory]
    [InlineData(true, false, "compressor")]
    [InlineData(false, true, "limiter")]
    public void Either_stage_alone_renders_and_is_named(bool compressor, bool limiter, string stage)
    {
        var take = ChainAuditionTwinTests.Take(6);
        var r = Twin(Only(compressor, limiter)).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey);
        Assert.False(r.NothingOn);
        Assert.Equal(new[] { stage }, r.Stages);
        Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", Lexicon.Get("audio.audition.stage." + stage));
        // The control: with both off, nothing is rendered at all.
        Assert.True(Twin(Only(false, false)).Process((float[])take.Clone(), Rate, CancellationToken.None, null).NothingOn);
    }

    [Fact]
    public void The_twin_equals_a_live_shaped_conditioner_sample_for_sample_with_the_compressor_its_drive_and_rotator_and_the_limiter()
    {
        var setup = Full();
        var take = ChainAuditionTwinTests.Take(6);
        var r = Twin(setup).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey);
        Assert.Equal(new[] { "expander", "compressor", "limiter" }, r.Stages);

        float[] LiveShape(float threshold)
        {
            var c = new TxAudioConditioner { KeySource = () => true };
            var record = TxAudioConditioning.ToConditioner(setup);
            record.Comp = record.Comp with { ThresholdDb = threshold };
            c.SetGateInForce(record);
            return Through(c, (float[])take.Clone());
        }
        var live = LiveShape(setup.Compressor.ThresholdDb);
        for (int i = 0; i < take.Length; i++)
            if (r.Processed[i] != live[i]) Assert.Fail($"sample {i}: twin {r.Processed[i]:R}, live {live[i]:R}");
        // The comparison can see a difference: the compressor's threshold 3 dB higher.
        var higher = LiveShape(setup.Compressor.ThresholdDb + 3);
        Assert.Contains(Enumerable.Range(0, take.Length), i => higher[i] != live[i]);
        // The chain's delay is the expander's look-ahead plus the limiter's.
        Assert.Equal(TxNoiseGate.LookAheadFramesFor(setup.Gate.LookAheadMs, Rate) + TxLimiter.LookAheadFramesFor(2, Rate), r.LatencySamples);
        // And the limiter held its ceiling in the render.
        Assert.True(r.Processed.Max(Math.Abs) <= (float)Math.Pow(10, -1 / 20.0) + 1e-7f);
    }

    [Fact]
    public void What_was_removed_is_unchanged_by_the_compressor_and_the_limiter_being_on()
    {
        var take = ChainAuditionTwinTests.Take(6);
        var cleanupOnly = Full();
        cleanupOnly.Compressor = new TxCompressorSetup();
        cleanupOnly.Limiter = new TxLimiterSetup();
        var plain = ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(cleanupOnly));
        var withDynamics = ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(Full()));
        var a = plain.Render(AuditionRender.Removed);
        var b = withDynamics.Render(AuditionRender.Removed);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Contains(a!, s => s != 0f); // the expander did remove something
        // The limiter's look-ahead moves the render two milliseconds later, so the
        // two "removed" renders cover stretches that differ at the very end: compare the common part.
        int n = Math.Min(a!.Length, b!.Length);
        Assert.True(n > Rate);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) Assert.Fail($"what was removed differs at {i}: {a[i]:R} against {b[i]:R}");
        Assert.Contains("compressor", withDynamics.RenderedStages);
        // The control: what was SENT does differ.
        Assert.Contains(Enumerable.Range(0, n), i => plain.Render(AuditionRender.Cleaned)![i] != withDynamics.Render(AuditionRender.Cleaned)![i]);
    }

    [Fact]
    public void The_verdict_says_when_the_live_stream_is_not_at_the_rate_the_audition_renders_at()
    {
        var take = ChainAuditionTwinTests.Take(6);
        AuditionLiveState Live(uint rate) => ChainAuditionTwin.LiveState(false, -40f, false, 0, 8f, "USB") with { TransmitRateHz = rate };
        string At(uint rate) => ChainAudition.Verdict(ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(Full(), () => Live(rate))));
        string marker = Lexicon.Get("audio.audition.verdict.rendered_at_48k", ("rate", "16"));
        Assert.Contains(marker, At(16000));
        Assert.DoesNotContain("rendered at 48 kilohertz", At(48000));
        Assert.DoesNotContain("rendered at 48 kilohertz", At(0)); // unknown: nothing claimed
    }

    [Fact]
    public void Rendering_changes_nothing_live_or_saved()
    {
        using var b = new Bridge();
        TxAudioConditioning.ApplySetup(Only(true, true));
        b.Unkey();
        var before = TxAudioConditioning.CaptureSetup("");
        var inForce = b.Cond.GateInForce;
        var r = Twin(Full()).Process(ChainAuditionTwinTests.Take(4), Rate, CancellationToken.None, null);
        Assert.True(r.Available);
        Assert.True(TxAudioConditioning.CaptureSetup("").SameContentAs(before));
        Assert.Same(inForce, b.Cond.GateInForce);
    }

    // ── the editor's model ──

    [Fact]
    public void The_editors_fields_scale_each_lever_and_its_cancel_puts_back_the_setting_it_opened_with_as_whole_switches()
    {
        using var b = new Bridge();
        TxAudioConditioning.ApplySetup(Full());
        b.Unkey();
        var model = b.Noise.DynamicsEditor("compressor editor");
        var ratio = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "ratio")!;
        var ceiling = NoiseController.FindDynamicsLever(DynamicsLever.LimiterStage, "ceiling")!;
        var attack = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "attack")!;
        Assert.Equal((10, 200, 5, 1), TxDynamicsEditorModel.FieldRange(ratio));     // 1.0 to 20.0 by 0.5, fine 0.1
        Assert.Equal((1, 3000, 10, 1), TxDynamicsEditorModel.FieldRange(attack));   // 0.1 to 300.0 ms by 1, fine 0.1
        Assert.Equal((-120, 0, 5, 0), TxDynamicsEditorModel.FieldRange(ceiling));
        Assert.Equal(40, TxDynamicsEditorModel.ToField(ratio, 4));
        Assert.Equal(4.5, TxDynamicsEditorModel.FromField(ratio, 45));

        Assert.False(model.Restore()); // nothing changed yet: Cancel does nothing
        model.Set(ratio, 75);
        model.Set(ceiling, -60);
        b.Noise.SetCompressorOn(false, "compressor editor");
        b.Noise.LoadCompressorPreset("level", "compressor editor");
        b.Unkey();
        Assert.Equal(-6f, b.Cond.Limiter.Settings.CeilingDb);
        Assert.False(b.Cond.Compressor.Settings.Enabled);

        // Cancel while keyed: held for the unkey, then exactly the setting it opened with.
        b.X.Keyed = true; b.X.Buffer();
        Assert.True(model.Restore());
        b.X.Buffer();
        Assert.False(b.Cond.Compressor.Settings.Enabled);
        b.Unkey();
        Assert.Equal(Full().Compressor.ToSettings(), b.Cond.Compressor.Settings);
        Assert.Equal(Full().Limiter.ToSettings(), b.Cond.Limiter.Settings);
        var back = TxAudioConditioning.CaptureDynamics();
        Assert.True(NoiseController.SameDynamics(back, Full()));

        // A detached editor (the dialog harness's road) holds its own settings and applies nothing.
        var detached = TxDynamicsEditorModel.Detached();
        detached.Set(ratio, 60);
        Assert.False(detached.Setting.Compressor.Enabled);
        Assert.Equal(Full().Compressor.ToSettings(), b.Cond.Compressor.Settings);
    }

    // ── the readings ──

    [Fact]
    public void The_now_sentences_say_off_not_running_and_the_reduction()
    {
        using var b = new Bridge();
        Assert.Equal(Lexicon.Get("audio.compressor.now_off"), b.Noise.CompressorNowSentence());
        Assert.Equal(Lexicon.Get("audio.limiter.now_off"), b.Noise.LimiterNowSentence());

        TxAudioConditioning.ApplySetup(Only(true, true));
        b.Unkey();
        // A loud tone through the owner's chain, keyed: the compressor is reducing.
        b.X.Keyed = true;
        var buf = new float[960];
        for (int k = 0; k < 100; k++)
        {
            for (int f = 0; f < 480; f++) { float v = (float)(0.5 * Math.Sin(2 * Math.PI * 700 * (k * 480 + f) / Rate)); buf[2 * f] = v; buf[2 * f + 1] = v; }
            b.Cond.Process(buf, 960, Rate);
        }
        var now = TxAudioConditioning.ReadDynamicsNow();
        Assert.True(now.CompressorRunning);
        Assert.True(now.CompressorReductionDb > 10f, "reduction " + now.CompressorReductionDb);
        string sentence = b.Noise.CompressorNowSentence();
        Assert.True(sentence.Contains(Math.Round(now.CompressorReductionDb, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)),
            "reduction " + now.CompressorReductionDb.ToString("R") + " is not in: " + sentence);

        // In CW or digital the chain stands aside: on, and not running.
        b.Cond.BypassAll = true;
        Assert.False(TxAudioConditioning.ReadDynamicsNow().CompressorRunning);
        Assert.Equal(Lexicon.Get("audio.compressor.now_not_running"), b.Noise.CompressorNowSentence().Split(" WORDING")[0]);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
