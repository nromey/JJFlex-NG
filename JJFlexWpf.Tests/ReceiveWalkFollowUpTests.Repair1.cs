using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N2 repair 1: Codex's check of d911b973, its reproductions
/// adopted (N2ReviewProbeTests, saved with its evidence). The status tests run
/// on a real FlexBase over vendor objects (NoiseSliceJumpSeamTests' bench: no
/// socket, isolated settings, globals restored); everything here is windowless
/// on the MTA test thread, asserted by the class constructor.
/// </summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    // ── F1: the landing's text and its answer come from one reading ──

    [Fact]
    public void A_landing_whose_radio_reported_elsewhere_after_it_was_built_is_not_certified()
    {
        // Codex's F1: the text was built at 10.125, then a background report of
        // 14.100 arrived before the guard read the radio, so the guard certified
        // 14.100 for a sentence saying 10.125. One capture now builds both.
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 10_125_000UL);
        Put(rig, "_RXMode", "USB");
        var status = RadioStatusBuilder.CaptureSpokenStatus(rig, null);
        Assert.Contains("10.125", status.Text);
        Assert.True(status.StillValid());
        Put(rig, "_RXFrequency", 14_100_000UL);
        Assert.Contains("14.100", RadioStatusBuilder.BuildSpokenStatus(rig));
        Assert.False(status.StillValid(), "the 10.125 sentence is certified while the radio reports 14.100");
    }

    [Fact]
    public void A_landing_holds_while_the_radio_stays_where_it_says()
    {
        // The positive control: unchanged, and the signal moving does not matter.
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        var status = RadioStatusBuilder.CaptureSpokenStatus(rig, null);
        Assert.Equal(RadioStatusBuilder.BuildSpokenStatus(rig), status.Text);
        Assert.True(status.StillValid());
        Assert.True(status.StillValid());
    }

    // ── F2: a recall's frequency and mode arrive as separate reports ──

    [Fact]
    public void A_recall_landing_survives_the_requested_frequency_arriving_before_its_mode()
    {
        // Codex's F2 reproduction: 14.100 USB, a recall of 7.030 CW. The radio
        // reports the frequency first; the sentence must survive that.
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        var status = RadioStatusBuilder.CaptureSpokenStatus(rig, new StatusAsk(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), () => true));
        Assert.Contains("7.030", status.Text);
        Assert.Contains("CW", status.Text);
        Assert.True(status.StillValid());
        Put(rig, "_RXFrequency", 7_030_000UL);
        Assert.True(status.StillValid(), "the requested frequency with the old mode, mid-recall");
        Put(rig, "_RXMode", "CW");
        Assert.True(status.StillValid());
    }

    [Fact]
    public void A_recall_landing_survives_the_requested_mode_arriving_before_its_frequency()
    {
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        var status = RadioStatusBuilder.CaptureSpokenStatus(rig, new StatusAsk(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), () => true));
        Put(rig, "_RXMode", "CW");
        Assert.True(status.StillValid(), "the requested mode with the old frequency, mid-recall");
        Put(rig, "_RXFrequency", 7_030_000UL);
        Assert.True(status.StillValid());
    }

    [Fact]
    public void A_recall_landing_is_dropped_for_a_change_the_recall_did_not_ask_for()
    {
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        var elsewhere = RadioStatusBuilder.CaptureSpokenStatus(rig, new StatusAsk(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), () => true));
        Put(rig, "_RXFrequency", 10_125_000UL);
        Assert.False(elsewhere.StillValid(), "a frequency neither asked for nor reported before");

        Put(rig, "_RXFrequency", 14_100_000UL);
        var otherMode = RadioStatusBuilder.CaptureSpokenStatus(rig, new StatusAsk(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), () => true));
        Put(rig, "_RXFrequency", 7_030_000UL);
        Put(rig, "_RXMode", "LSB");
        Assert.False(otherMode.StillValid(), "a mode neither asked for nor reported before");
    }

    [Fact]
    public void A_recall_superseded_or_outlived_allows_only_the_sentence_itself()
    {
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        bool pending = true;
        var status = RadioStatusBuilder.CaptureSpokenStatus(rig, new StatusAsk(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), () => pending));
        Put(rig, "_RXFrequency", 7_030_000UL);
        Assert.True(status.StillValid());
        // A later mode choice (another recall, a mode key) or the recall's lifetime ending.
        pending = false;
        Assert.False(status.StillValid(), "half a recall that is no longer the live request");

        // A recall already outlived when the landing runs is not spoken at all
        // (repair 2, Codex's R1): the producer asks before the first sentence,
        // so the landing says where the radio reports it is.
        var outlived = RadioStatusBuilder.CaptureSpokenStatus(rig, new StatusAsk(3_573_000, "DIGU", RadioStatusBuilder.BuildDetailedStatus(rig), () => false));
        Assert.DoesNotContain("3.573", outlived.Text);
        Assert.Equal(RadioStatusBuilder.BuildSpokenStatus(rig), outlived.Text);
        Assert.True(outlived.StillValid(), "the radio's own report, still where it is");
        Put(rig, "_RXFrequency", 3_573_000UL);
        Put(rig, "_RXMode", "DIGU");
        Assert.False(outlived.StillValid(), "the outlived recall's values arriving later are a change to that sentence");
    }

    // ── F3: Return to defaults says why the CW model rule chose ──

    [Fact]
    public void Return_to_defaults_carries_the_CW_model_reason_in_its_one_entry()
    {
        // Codex's F3 reproduction, adopted: measured 8 ms, Premium CW in CW.
        using var f = new NoiseFixture();
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.Attach("CW");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium_cw"));
        var (journal, trace) = Capture(() => f.Noise.ReturnToDefaults("nnr", "n2-probe"));
        Assert.Equal("standard_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        var entry = Assert.Single(journal, e => e.Changes.Any(c => c.EngineId == "nnr" && c.Setting == "model"));
        Assert.Equal(NoiseJournalKind.ReturnToDefaults, entry.Kind);
        Assert.Contains("Premium CW measured 8.00 ms", entry.Label);
        Assert.StartsWith(NnrName(f), entry.Label);
        // One entry, no second model operation.
        Assert.Single(journal);
        Assert.Single(trace, l => l.StartsWith("NoiseController: the CW model rule chose standard_cw (n2-probe): ", StringComparison.Ordinal));
    }

    [Fact]
    public void Return_to_defaults_outside_CW_carries_no_CW_reason()
    {
        // The positive control: the voice road has no rule to explain.
        using var f = new NoiseFixture();
        f.Cost.PremiumCw99thPercentileMs = 8.0;
        f.Noise.Attach("USB");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
        var (journal, _) = Capture(() => f.Noise.ReturnToDefaults("nnr", "n2-probe"));
        var entry = Assert.Single(journal);
        Assert.Equal(NnrName(f), entry.Label);
    }

    // ── F4: the model-cost test runs only against a helper named for it ──

    [Fact]
    public void The_model_cost_test_does_not_assume_the_default_helper_has_model_costs()
    {
        const string def = @"C:\helpers\default.exe";
        const string named = @"C:\helpers\named.exe";
        // Unset, default built: NOT RUN, naming the default and why.
        Assert.Null(HelperLifecycleTests.ExplicitRealHelper(null, def, p => p == def, out string why));
        Assert.Contains(def, why);
        Assert.Contains("JJFLEX_WDSP_HELPER", why);
        Assert.Null(HelperLifecycleTests.ExplicitRealHelper("", def, p => p == def, out why));
        Assert.Contains(def, why);
        // Unset, nothing built.
        Assert.Null(HelperLifecycleTests.ExplicitRealHelper(null, def, _ => false, out why));
        Assert.Contains(def, why);
        // Named but missing.
        Assert.Null(HelperLifecycleTests.ExplicitRealHelper(named, def, p => p == def, out why));
        Assert.Contains(named, why);
        // Named and present: it runs, strictly.
        Assert.Equal(named, HelperLifecycleTests.ExplicitRealHelper(named, def, _ => true, out why));
        Assert.Equal("", why);
    }

    [Fact]
    public void The_model_cost_test_asks_for_the_explicit_helper_and_the_lifecycle_tests_keep_the_default()
    {
        string cost = Source("JJFlexWpf.Tests", "HelperModelCostTests.cs");
        string test = Body(cost, "public void Real_helper_reports_every_model_slots_cost_and_it_is_remembered()", "HelperTestRig.Until(");
        Assert.Contains("HelperLifecycleTests.ExplicitRealHelper(out string notRun)", test);
        Assert.DoesNotContain("HelperLifecycleTests.RealHelper()", test);
        Assert.Contains("\"the real helper's ModelCosts\", 60000", cost);
        string life = Source("JJFlexWpf.Tests", "HelperLifecycleTests.RealHelper.cs");
        Assert.Contains("path ??= DefaultRealHelper();", life);
    }

    // ── The landing's wiring (MainWindow cannot be built in a test) ──

    [Fact]
    public void The_landing_builds_its_text_and_its_answer_in_one_capture_and_the_recall_carries_its_identity()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        string land = Body(main, "private void RunReturnToAppLanding(", "#endregion");
        Assert.Contains("var status = Radios.RadioStatusBuilder.CaptureSpokenStatus(rig, asked);", land);
        Assert.Contains("stillValid: status.StillValid);", land);
        Assert.DoesNotContain("BuildSpokenStatus(", land);
        Assert.DoesNotContain("StillDescribes(", land);
        // Repair 2 (Codex's R1): the recall is bound to the action. Its before
        // is read ahead of the request, its marks after it, and the landing
        // takes it only while it still owns the radio.
        string take = Body(main, "private Radios.StatusAsk? TakeRecallAskedFor()", "private void RunReturnToAppLanding(");
        Assert.Contains("return recall?.AskIfStillOwned(RecallMarksNow, () => Environment.TickCount64);", take);
        string marks = Body(main, "private Radios.RecallMarks RecallMarksNow()", "private Radios.StatusAsk? TakeRecallAskedFor()");
        Assert.Contains("new Radios.RecallMarks(rig, rig?.RXVFO ?? -1, ModeChoices, rig?.RXFrequencyRequests ?? -1)", marks);
        string select = Body(main, "dialog.SelectMemory = (memRef) =>", "dialog.FormatFrequency");
        int before = select.IndexOf("var before = rig == null ? null : Radios.RadioStatusBuilder.BuildDetailedStatus(rig);", StringComparison.Ordinal);
        int ask = select.IndexOf("AskRadioForMode(() => mem.Select());", StringComparison.Ordinal);
        int bound = select.IndexOf("_pendingRecall = new Radios.PendingRecall((ulong)Math.Round(mem.Freq * 1e6), mem.Mode ?? \"\", before, at, RecallMarksNow());", StringComparison.Ordinal);
        Assert.True(before >= 0 && ask > before, "the recall's before is the radio's reading at the action, ahead of its request");
        Assert.True(bound > ask, "the recall's marks are taken after its own request, so its own mode choice is counted");
    }

    /// <summary>NNR's own name, as the defaults entry labels itself (its key read off the catalog).</summary>
    private static string NnrName(NoiseFixture f)
    {
        string key = f.Noise.Catalog.Find("nnr")!.LabelKey;
        return Lexicon.Get(key);
    }

    private static IDisposable NewStatusBench(out FlexBase rig)
    {
        // NoiseSliceJumpSeamTests' bench, as Codex's probes reached it: its
        // capture refuses Connect, its settings are isolated, and Dispose puts
        // the FlexBase globals back.
        var type = typeof(NoiseSliceJumpSeamTests).GetNestedType("Bench", BindingFlags.NonPublic)!;
        var bench = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        rig = (FlexBase)type.GetField("Rig")!.GetValue(bench)!;
        return bench;
    }

    private static void Put(FlexBase rig, string name, object value) =>
        typeof(FlexBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, value);
}
