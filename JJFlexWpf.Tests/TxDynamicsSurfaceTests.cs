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
    [InlineData(true, false, "compressor", "audio.audition.stage.compressor")]
    [InlineData(false, true, "limiter", "audio.audition.stage.limiter")]
    public void Either_stage_alone_renders_and_is_named(bool compressor, bool limiter, string stage, string nameKey)
    {
        var take = ChainAuditionTwinTests.Take(6);
        var r = Twin(Only(compressor, limiter)).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey);
        Assert.False(r.NothingOn);
        Assert.Equal(new[] { stage }, r.Stages);
        Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", Lexicon.Get(nameKey));
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

        Assert.False(model.Restore()); // nothing changed in it: Cancel does nothing
        model = b.Noise.DynamicsEditor("compressor editor"); // an editor is done with once it has closed: this is the next one
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

    // ── repair 1, B4: the layer's Escape owns the compressor and the limiter ──

    private const string Layer = NoiseController.LayerSource;
    private const string Radio8600 = "1234-5678-9012-3456";

    /// <summary>A bridge whose controller has a setup store of its own, holding one setup with both stages on.</summary>
    private sealed class LayerBench : IDisposable
    {
        public readonly Bridge B = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjf-track-cl-layer-" + Guid.NewGuid().ToString("N"));
        public LayerBench(bool withGate)
        {
            Directory.CreateDirectory(_dir);
            var store = new TxNoiseSetupStore(_dir);
            B.F.Radio.Serial = Radio8600;
            B.Noise.TransmitSetupStore = () => store;
            var setup = new TxNoiseSetup
            {
                Name = "Loud",
                Compressor = new TxCompressorSetup { Enabled = true, ThresholdDb = -24, RatioToOne = 4, MakeupDb = 9 },
                Limiter = new TxLimiterSetup { Enabled = true, CeilingDb = -3 },
            };
            if (withGate) setup.Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -40, RatioToOne = 3 };
            Assert.True(store.Save(Radio8600, setup).Succeeded); // the positive control Codex's first probe lacked: the save took
        }
        public void Dispose()
        {
            B.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
        public bool CompressorOn => TxAudioConditioning.CompressorSetup.Enabled && B.Cond.Compressor.Settings.Enabled;
        public bool LimiterOn => TxAudioConditioning.LimiterSetup.Enabled && B.Cond.Limiter.Settings.Enabled;
        public bool BothOff => !TxAudioConditioning.CompressorSetup.Enabled && !B.Cond.Compressor.Settings.Enabled
            && !TxAudioConditioning.LimiterSetup.Enabled && !B.Cond.Limiter.Settings.Enabled;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_setup_applied_from_the_layer_is_put_back_by_its_Escape_compressor_and_limiter_included(bool withGateChange)
    {
        using var l = new LayerBench(withGateChange);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.ApplyTransmitSetup("Loud", Layer);
        b.Unkey();
        Assert.True(l.CompressorOn, "the setup did not switch the compressor on: the test proves nothing");
        Assert.True(l.LimiterOn);
        Assert.Equal(withGateChange, b.Cond.Gate.Enabled);

        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.True(l.BothOff, "Escape left a stage on that the layer's setup switched on");
        Assert.Equal(TxCompressorSettings.DefaultThresholdDb, TxAudioConditioning.CompressorSetup.ThresholdDb);
        Assert.Equal(0f, TxAudioConditioning.LimiterSetup.CeilingDb);
        Assert.False(b.Cond.Gate.Enabled);
        Assert.False(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void The_layers_setup_goes_back_and_a_de_esser_switched_on_at_home_afterwards_stays()
    {
        using var l = new LayerBench(withGate: false);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.ApplyTransmitSetup("Loud", Layer);
        b.Unkey();
        b.Noise.SetDeEsserOn(true, Home);
        b.Unkey();
        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.True(l.BothOff);
        Assert.True(TxAudioConditioning.DeEsserSetup.Enabled); // the outside change to another stage is kept
        Assert.True(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void A_change_made_at_home_to_the_compressor_after_the_layers_is_kept_and_the_limiter_still_goes_back()
    {
        using var l = new LayerBench(withGate: false);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.ApplyTransmitSetup("Loud", Layer);
        b.Unkey();
        b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold")!, -40, Home);
        b.Unkey();
        b.Noise.RestoreSnapshot(snap, Layer);
        b.Unkey();
        // The compressor is no longer as the layer left it: somebody else's change, kept (each stage on its own).
        Assert.True(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.Equal(-40f, TxAudioConditioning.CompressorSetup.ThresholdDb);
        // The limiter is still the layer's, and goes back.
        Assert.False(TxAudioConditioning.LimiterSetup.Enabled);
        Assert.False(b.Cond.Limiter.Settings.Enabled);
        Assert.True(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void The_layers_change_to_one_stage_goes_back_while_homes_change_to_the_other_stays()
    {
        // Home switches the limiter on BEFORE the layer's setup; the layer's setup changes both; the
        // limiter's restore target is what Home left, the compressor's is the entry picture.
        using var l = new LayerBench(withGate: false);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.SetLimiterOn(true, Home);
        b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.LimiterStage, "ceiling")!, -6, Home);
        b.Unkey();
        b.Noise.ApplyTransmitSetup("Loud", Layer);
        b.Unkey();
        Assert.Equal(-3f, b.Cond.Limiter.Settings.CeilingDb);
        b.Noise.RestoreSnapshot(snap, Layer);
        b.Unkey();
        Assert.False(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.True(TxAudioConditioning.LimiterSetup.Enabled);          // back to what Home left, not to the entry
        Assert.Equal(-6f, b.Cond.Limiter.Settings.CeilingDb);
        Assert.True(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void A_layers_setup_still_held_for_the_unkey_is_withdrawn_by_Escape()
    {
        using var l = new LayerBench(withGate: true);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.ApplyTransmitSetup("Loud", Layer);
        Assert.True(b.X.Chain.HasDeferredChanges);
        Assert.False(b.Cond.Compressor.Settings.Enabled); // nothing landed yet
        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.True(l.BothOff);
        Assert.False(b.Cond.Gate.Enabled);
    }

    [Fact]
    public void A_put_back_held_for_the_unkey_restores_both_stages_when_it_runs()
    {
        using var l = new LayerBench(withGate: false);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.ApplyTransmitSetup("Loud", Layer);
        b.Unkey();
        b.X.Keyed = true; b.X.Buffer();
        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        Assert.Equal(TxChangeTiming.AtUnkey, b.Noise.LastRestoreTiming);
        b.X.Buffer();
        Assert.True(b.Cond.Compressor.Settings.Enabled);  // still on under the over
        b.Unkey();
        Assert.True(l.BothOff);
    }

    // ── repair 1, B5: the editor ──

    [Fact]
    public void Every_way_out_of_the_editor_but_Close_puts_back_both_stages_and_the_speech_processor_setting()
    {
        bool settingWas = AudioOutputConfig.RadioProcessorOffWithCompressorInForce;
        try
        {
            using var b = new Bridge();
            const string editor = "compressor editor";
            b.Noise.SetProcessorHandoffSetting(true, Home);
            var model = b.Noise.DynamicsEditor(editor);
            Assert.True(model.OpenedWithHandoff);

            // In the editor: the setting cleared, the compressor switched on, a preset loaded.
            b.Noise.SetProcessorHandoffSetting(false, editor);
            b.Noise.SetCompressorOn(true, editor);
            b.Noise.LoadCompressorPreset("dx", editor);
            b.Unkey();
            Assert.False(b.Noise.ProcessorHandoffSetting);
            Assert.False(AudioOutputConfig.RadioProcessorOffWithCompressorInForce);

            // Close keeps everything.
            Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: true));
            b.Unkey();
            Assert.False(b.Noise.ProcessorHandoffSetting);
            Assert.True(b.Noise.CompressorOn);

            // The window closing any other way (Escape, the title bar, Alt+F4, Cancel) puts all of it back.
            Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false));
            b.Unkey();
            Assert.True(b.Noise.ProcessorHandoffSetting);
            Assert.True(AudioOutputConfig.RadioProcessorOffWithCompressorInForce);
            Assert.False(b.Noise.CompressorOn);
            Assert.Equal("", TxAudioConditioning.CompressorSetup.PresetId);
            Assert.Equal(0f, TxAudioConditioning.CompressorSetup.DriveDb);
            // And with nothing left to put back, a second closing does nothing.
            Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: false));
        }
        finally { AudioOutputConfig.RadioProcessorOffWithCompressorInForce = settingWas; }
    }

    [Fact]
    public void The_speech_processor_settings_put_back_waits_for_the_unkey_like_the_stages_it_goes_back_with()
    {
        bool settingWas = AudioOutputConfig.RadioProcessorOffWithCompressorInForce;
        try
        {
            using var b = new Bridge();
            b.Noise.SetProcessorHandoffSetting(true, Home);
            var model = b.Noise.DynamicsEditor("compressor editor");
            b.Noise.SetProcessorHandoffSetting(false, "compressor editor");
            b.X.Keyed = true; b.X.Buffer();
            Assert.True(model.Restore());
            Assert.False(b.Noise.ProcessorHandoffSetting);  // held: the setting does not change under an over
            b.Unkey();
            Assert.True(b.Noise.ProcessorHandoffSetting);
        }
        finally { AudioOutputConfig.RadioProcessorOffWithCompressorInForce = settingWas; }
    }

    // ── repair 2, B5: what the editor asked for while keyed is still waiting when it is cancelled ──

    private static void EditWhileKeyed(Bridge b, string change, string source)
    {
        switch (change)
        {
            case "compressor-on": b.Noise.SetCompressorOn(true, source); break;
            case "limiter-on": b.Noise.SetLimiterOn(true, source); break;
            case "preset": b.Noise.LoadCompressorPreset("dx", source); break;
            case "split-band": b.Noise.SetCompressorSplitBand(true, source); break;
            case "rotator": b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "rotator")!, 4, source); break;
            // A continuous lever is live while keyed, but once something is waiting it goes in order behind it.
            case "lever-behind-a-switch":
                b.Noise.SetLimiterOn(true, source);
                b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "makeup")!, 3, source);
                break;
            default: b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.LimiterStage, "lookahead")!, 5, source); break;
        }
    }

    private static (bool Compressor, bool Limiter, string Preset, float LookAhead, bool SplitBand, int Rotator, float Makeup) InForce(Bridge b)
        => (b.Cond.Compressor.Settings.Enabled, b.Cond.Limiter.Settings.Enabled, TxAudioConditioning.CompressorSetup.PresetId, b.Cond.Limiter.Settings.LookAheadMs,
            b.Cond.Compressor.Settings.SplitBand, b.Cond.Compressor.Settings.RotatorStages, b.Cond.Compressor.Settings.MakeupDb);

    [Theory]
    [InlineData("compressor-on")]
    [InlineData("limiter-on")]
    [InlineData("preset")]
    [InlineData("lookahead")]
    [InlineData("split-band")]
    [InlineData("rotator")]
    [InlineData("lever-behind-a-switch")]
    public void An_edit_made_keyed_and_cancelled_before_the_unkey_never_lands(string change)
    {
        const string editor = "compressor editor";
        using var b = new Bridge();
        var opened = InForce(b);
        Assert.Equal((false, false, "", 0f, false, 0, TxCompressorSettings.DefaultMakeupDb), opened);
        var model = b.Noise.DynamicsEditor(editor);
        b.X.Keyed = true; b.X.Buffer();

        EditWhileKeyed(b, change, editor);
        Assert.True(b.X.Chain.HasDeferredChanges);              // positive control: the edit is waiting for the unkey
        Assert.Equal(opened, InForce(b));                       // and has not landed

        Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false)); // Cancel, Escape, the title bar: there was something to take back
        Assert.False(b.X.Chain.HasDeferredChanges);             // nothing of the editor's is left waiting
        b.Unkey();
        Assert.Equal(opened, InForce(b));                       // the cancelled edit never lands
    }

    [Theory]
    [InlineData("compressor-on")]
    [InlineData("limiter-on")]
    [InlineData("preset")]
    [InlineData("lookahead")]
    [InlineData("split-band")]
    [InlineData("rotator")]
    [InlineData("lever-behind-a-switch")]
    public void An_edit_made_keyed_and_kept_with_Close_lands_at_the_unkey(string change)
    {
        // The positive control for the theory above: the same edit, kept, does land.
        const string editor = "compressor editor";
        using var b = new Bridge();
        var opened = InForce(b);
        var model = b.Noise.DynamicsEditor(editor);
        b.X.Keyed = true; b.X.Buffer();
        EditWhileKeyed(b, change, editor);
        Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: true));
        Assert.True(b.X.Chain.HasDeferredChanges);
        b.Unkey();
        Assert.NotEqual(opened, InForce(b));
    }

    [Fact]
    public void Cancelling_the_editor_keeps_what_somebody_else_asked_for_while_keyed_and_takes_back_only_its_own()
    {
        const string editor = "compressor editor";
        using var b = new Bridge();
        var model = b.Noise.DynamicsEditor(editor);
        b.X.Keyed = true; b.X.Buffer();

        b.Noise.SetLimiterOn(true, Home);                       // asked from Home, before the editor's edits: waiting
        b.Noise.SetCompressorOn(true, editor);                  // the editor's own, waiting behind it
        b.Noise.LoadCompressorPreset("dx", editor);
        b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "rotator")!, 4, Home); // Home again, behind the editor's
        Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false));
        Assert.True(b.X.Chain.HasDeferredChanges);              // Home's two are still waiting
        b.Unkey();
        Assert.True(b.Cond.Limiter.Settings.Enabled);           // Home's limiter came on
        Assert.Equal(4, b.Cond.Compressor.Settings.RotatorStages); // and Home's rotator
        Assert.False(b.Cond.Compressor.Settings.Enabled);       // the editor's cancelled switch did not
        Assert.Equal("", TxAudioConditioning.CompressorSetup.PresetId); // nor its preset
    }

    [Fact]
    public void An_edit_that_landed_and_one_still_waiting_are_both_taken_back_by_one_cancel()
    {
        const string editor = "compressor editor";
        using var b = new Bridge();
        var model = b.Noise.DynamicsEditor(editor);
        b.Noise.SetCompressorOn(true, editor);                  // unkeyed: lands at once
        b.Unkey();
        Assert.True(b.Cond.Compressor.Settings.Enabled);
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.SetLimiterOn(true, editor);                     // keyed: waits
        Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false));
        Assert.True(b.Cond.Compressor.Settings.Enabled);        // a switch does not change under an over: the put-back waits
        Assert.True(b.Noise.CompressorOn);                      // as a change in the chain's order, not even as an intent yet
        b.Unkey();
        Assert.False(b.Cond.Compressor.Settings.Enabled);       // put back at the unkey
        Assert.False(b.Cond.Limiter.Settings.Enabled);          // and the waiting one never landed
        // A second closing finds nothing to do, and does not take back the put-back.
        Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: false));
    }

    // ── repair 3, B5: an editor's Cancel undoes only what that editor did ──

    private const string Editor = "compressor editor";

    [Fact]
    public void An_untouched_editors_cancel_keeps_what_an_earlier_editor_kept_while_keyed_and_landed_while_it_was_open()
    {
        using var b = new Bridge();
        b.X.Keyed = true; b.X.Buffer();
        var first = b.Noise.DynamicsEditor(Editor);
        b.Noise.SetCompressorOn(true, Editor);                  // waiting for the unkey
        Assert.False(TxDynamicsEditorDialog.Dismissed(first, kept: true)); // Close: kept

        var second = b.Noise.DynamicsEditor(Editor);            // opened before the unkey; nothing is edited in it
        b.Unkey();
        Assert.True(b.Cond.Compressor.Settings.Enabled);        // positive control: the first editor's kept choice landed
        Assert.False(TxDynamicsEditorDialog.Dismissed(second, kept: false)); // an untouched editor has nothing to take back
        b.Unkey();
        Assert.True(b.Cond.Compressor.Settings.Enabled);        // and takes nothing back
    }

    [Fact]
    public void An_untouched_editors_cancel_does_not_bring_back_what_an_earlier_editor_cancelled()
    {
        using var b = new Bridge();
        var first = b.Noise.DynamicsEditor(Editor);
        b.Noise.SetCompressorOn(true, Editor);                  // unkeyed: lands at once
        b.Unkey();
        Assert.True(b.Cond.Compressor.Settings.Enabled);
        b.X.Keyed = true; b.X.Buffer();
        Assert.True(TxDynamicsEditorDialog.Dismissed(first, kept: false)); // cancelled while keyed: its put-back waits

        var second = b.Noise.DynamicsEditor(Editor);            // opened before the unkey; nothing is edited in it
        b.Unkey();
        Assert.False(b.Cond.Compressor.Settings.Enabled);       // positive control: the first editor's cancel landed
        Assert.False(TxDynamicsEditorDialog.Dismissed(second, kept: false));
        b.Unkey();
        Assert.False(b.Cond.Compressor.Settings.Enabled);       // the cancelled choice is not reinstated
    }

    [Fact]
    public void An_untouched_editors_cancel_keeps_what_Home_asked_for_before_it_opened()
    {
        using var b = new Bridge();
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.SetLimiterOn(true, Home);                       // Home's, waiting for the unkey
        var model = b.Noise.DynamicsEditor(Editor);             // nothing is edited in it
        b.Unkey();
        Assert.True(b.Cond.Limiter.Settings.Enabled);           // positive control: Home's limiter came on
        Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: false));
        b.Unkey();
        Assert.True(b.Cond.Limiter.Settings.Enabled);           // and stays on
    }

    [Fact]
    public void A_cancel_takes_back_the_editors_own_change_and_keeps_an_earlier_choice_that_landed_while_it_was_open()
    {
        // The editor did change something: its own change goes back, and only to where IT started from.
        using var b = new Bridge();
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.SetLimiterOn(true, Home);                       // Home's, asked before the editor opened
        b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "rotator")!, 4, Home);
        var model = b.Noise.DynamicsEditor(Editor);
        b.Noise.SetCompressorOn(true, Editor);                  // the editor's own, behind Home's
        b.Unkey();                                              // all three land, in order
        Assert.True(b.Cond.Compressor.Settings.Enabled);
        Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false));
        b.Unkey();
        Assert.False(b.Cond.Compressor.Settings.Enabled);       // the editor's switch went back
        Assert.Equal(4, b.Cond.Compressor.Settings.RotatorStages); // to the compressor as Home had left it, not as it was when the editor opened
        Assert.True(b.Cond.Limiter.Settings.Enabled);           // and Home's limiter was never the editor's
    }

    [Fact]
    public void A_cancel_takes_back_each_thing_the_editor_changed_and_keeps_what_somebody_else_changed_after_it_on_the_same_stage()
    {
        using var b = new Bridge();
        var threshold = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold")!;
        var ratio = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "ratio")!;
        var model = b.Noise.DynamicsEditor(Editor);
        b.Noise.SetCompressorOn(true, Editor);                  // the editor's
        b.Noise.SetDynamics(threshold, -30, Editor);            // the editor's
        b.Noise.SetLimiterOn(true, Editor);                     // the editor's
        b.Noise.SetDynamics(threshold, -33, Home);              // Home's, after the editor's, on the very lever the editor moved
        b.Noise.SetDynamics(ratio, 7, Home);                    // Home's, on a lever the editor never touched
        b.Unkey();
        Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false));
        b.Unkey();
        Assert.False(b.Cond.Compressor.Settings.Enabled);       // the editor's switch went back
        Assert.Equal(-33f, b.Cond.Compressor.Settings.ThresholdDb); // the lever Home moved after the editor is Home's
        Assert.Equal(7f, b.Cond.Compressor.Settings.RatioToOne);    // and so is the one the editor never touched
        Assert.False(b.Cond.Limiter.Settings.Enabled);          // the editor's limiter switch went back
    }

    [Fact]
    public void A_waiting_cancel_asks_again_when_it_runs_and_leaves_what_Home_changed_meanwhile()
    {
        using var b = new Bridge();
        var threshold = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold")!;
        var model = b.Noise.DynamicsEditor(Editor);
        b.Noise.SetCompressorOn(true, Editor);
        b.Noise.SetDynamics(threshold, -30, Editor);
        b.Unkey();
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.SetCompressorSplitBand(true, Home);             // Home's, waiting
        b.Noise.SetDynamics(threshold, -33, Home);              // Home's, waiting behind it: the lever the editor moved
        Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false)); // the cancel waits behind both
        Assert.Equal(-30f, b.Cond.Compressor.Settings.ThresholdDb); // positive control: when Cancel was pressed the lever still read as the editor left it
        b.Unkey();
        Assert.True(b.Cond.Compressor.Settings.SplitBand);      // Home's mode landed and stays
        Assert.Equal(-33f, b.Cond.Compressor.Settings.ThresholdDb); // Home's lever, moved since: not written over by the cancel that ran after it
        Assert.False(b.Cond.Compressor.Settings.Enabled);       // the editor's own switch went back
    }

    [Fact]
    public void An_untouched_editors_cancel_leaves_the_speech_processor_setting_somebody_else_switched_while_it_was_open()
    {
        bool settingWas = AudioOutputConfig.RadioProcessorOffWithCompressorInForce;
        try
        {
            using var b = new Bridge();
            b.Noise.SetProcessorHandoffSetting(true, Home);
            var model = b.Noise.DynamicsEditor(Editor);
            Assert.True(model.OpenedWithHandoff);
            b.Noise.SetProcessorHandoffSetting(false, Home);    // Home's, while the editor is open
            Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: false));
            b.Unkey();
            Assert.False(b.Noise.ProcessorHandoffSetting);      // not the editor's to put back

            // The editor's own switch of it, and then Home's on top: Home's stays.
            var second = b.Noise.DynamicsEditor(Editor);
            b.Noise.SetProcessorHandoffSetting(true, Editor);
            b.Noise.SetProcessorHandoffSetting(false, Home);
            Assert.False(TxDynamicsEditorDialog.Dismissed(second, kept: false));
            b.Unkey();
            Assert.False(b.Noise.ProcessorHandoffSetting);

            // The positive control: the editor's own switch, untouched since, does go back.
            var third = b.Noise.DynamicsEditor(Editor);
            b.Noise.SetProcessorHandoffSetting(true, Editor);
            Assert.True(TxDynamicsEditorDialog.Dismissed(third, kept: false));
            b.Unkey();
            Assert.False(b.Noise.ProcessorHandoffSetting);
        }
        finally { AudioOutputConfig.RadioProcessorOffWithCompressorInForce = settingWas; }
    }

    [Fact]
    public void A_field_the_editor_changed_twice_with_somebody_elses_change_in_between_goes_back_to_what_they_left()
    {
        using var b = new Bridge();
        var threshold = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold")!;
        var model = b.Noise.DynamicsEditor(Editor);
        b.Noise.SetDynamics(threshold, -30, Editor);            // the editor's
        b.Noise.SetDynamics(threshold, -33, Home);              // Home's: the floor from here
        b.Noise.SetDynamics(threshold, -36, Editor);            // the editor's again
        b.Unkey();
        Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false));
        b.Unkey();
        Assert.Equal(-33f, b.Cond.Compressor.Settings.ThresholdDb); // back to Home's, not to where the editor first found it
    }

    // ── the field-by-field walk every setting class goes through ──

    private static object Other(object? value) => value switch
    {
        bool flag => !flag,
        float number => number + 1.5f,
        int count => count + 1,
        string word => word + "x",
        _ => throw new NotSupportedException("no other value for " + value?.GetType().Name),
    };

    private static IEnumerable<(Func<object, object> Holder, System.Reflection.PropertyInfo Field, string Name)> ValueFields(Type type, Func<object, object> holder, string path)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            if (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string)) yield return (holder, p, path + p.Name);
            else foreach (var inner in ValueFields(p.PropertyType, root => p.GetValue(holder(root))!, path + p.Name + ".")) yield return inner;
        }
    }

    [Fact]
    public void Every_field_of_the_compressor_and_the_limiter_is_noted_and_taken_back_on_its_own()
    {
        var cases = ValueFields(typeof(TxCompressorSetup), root => root, "compressor.").Select(f => (Make: (Func<object>)(() => new TxCompressorSetup()), f.Holder, f.Field, f.Name))
            .Concat(ValueFields(typeof(TxLimiterSetup), root => root, "limiter.").Select(f => (Make: (Func<object>)(() => new TxLimiterSetup()), f.Holder, f.Field, f.Name))).ToList();
        // Positive control: the walk finds the fields, the three bands' included (13 of the compressor's own, 5 in each band, 4 of the limiter's).
        Assert.Equal(13 + 3 * 5 + 4, cases.Count);
        Assert.Contains(cases, c => c.Name == "compressor.MidBand.RatioToOne");
        Assert.Contains(cases, c => c.Name == "compressor.PresetId");
        Assert.Contains(cases, c => c.Name == "limiter.LookAheadMs");

        foreach (var (make, holder, field, name) in cases)
        {
            object before = make(), after = make(), floor = make(), left = make();
            object was = field.GetValue(holder(before))!;
            object theirs = Other(was), someoneElses = Other(theirs);
            field.SetValue(holder(after), theirs);

            // The writer changed this one field.
            Assert.False(OwnedFields.Differ(floor, left), name);
            OwnedFields.Note(floor, left, before, after);
            Assert.True(OwnedFields.Differ(floor, left), name + ": the change was not noted");
            Assert.Equal(theirs, field.GetValue(holder(left)));
            Assert.Equal(was, field.GetValue(holder(floor)));

            // Still as the writer left it: it goes back, and nothing else moves.
            object now = make();
            field.SetValue(holder(now), theirs);
            Assert.Equal(1, OwnedFields.TakeBack(floor, left, now));
            Assert.False(OwnedFields.Differ(now, make()), name + ": taking it back did not restore the setting exactly");

            // Changed by somebody else since: it stays theirs.
            object changedSince = make();
            field.SetValue(holder(changedSince), someoneElses);
            Assert.Equal(0, OwnedFields.TakeBack(floor, left, changedSince));
            Assert.Equal(someoneElses, field.GetValue(holder(changedSince)));

            // Somebody else's change between two of the writer's: their value is the floor from then on.
            object second = make(), third = make();
            field.SetValue(holder(second), someoneElses);
            field.SetValue(holder(third), Other(someoneElses));
            OwnedFields.Note(floor, left, second, third);
            Assert.Equal(someoneElses, field.GetValue(holder(floor)));
        }
    }

    [Fact]
    public void A_kind_of_field_the_walk_cannot_take_back_fails_loudly_instead_of_being_skipped()
    {
        var a = new HoldsAList(); var b = new HoldsAList();
        Assert.Throws<NotSupportedException>(() => OwnedFields.Differ(a, b));
    }

    private sealed class HoldsAList { public string[] Order { get; set; } = Array.Empty<string>(); }

    [Fact]
    public void The_editors_window_puts_back_on_closing_not_on_one_button_and_its_Say_buttons_speak()
    {
        // What a windowless test can reach of the window itself: its source. The harness walk presses the keys.
        string dialog = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "TxDynamicsEditorDialog.cs"));
        Assert.Contains("Closing += (_, _) => Dismissed(_model, _kept);", dialog);
        Assert.Contains("() => { _kept = true; DialogResult = true; }", dialog);
        Assert.Equal(0, dialog.Split("_model.Restore()").Length - 1);   // no button restores by itself: every way out goes through the closing
        Assert.Contains("_noise?.SayCompressorNow();", dialog);
        Assert.Contains("_noise?.SayLimiterNow();", dialog);
        // The base dialog closes on Escape in its own preview handler; the closing event is what both reach.
        string baseDialog = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "JJFlexDialog.cs"));
        Assert.Contains("PreviewKeyDown", baseDialog);
    }

    [Fact]
    public void The_Say_doors_speak_the_reading_on_each_stages_own_subject()
    {
        using var b = new Bridge();
        b.F.Spoken.Clear();
        b.Noise.SayCompressorNow();
        b.Noise.SayLimiterNow();
        Assert.Equal(new[] { Radios.Speech.SpeechSubject.TransmitCompressor, Radios.Speech.SpeechSubject.TransmitLimiter },
            b.F.Spoken.Said.Select(s => s.Subject));
        Assert.Equal(Lexicon.Get("audio.compressor.now_off"), b.F.Spoken.Said[0].Text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
