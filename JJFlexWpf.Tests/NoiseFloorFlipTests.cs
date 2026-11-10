using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track S (#1019): Strength flipped into a noise floor on every
/// computer engine. The stored quantity keeps its meaning (the wet fraction);
/// only what an operator sees, hears, steps and finds journaled turns around.
/// Windowless: the noise controller over fakes, the slot, the bank, and static
/// helpers of the two windows that show a floor.
/// </summary>
public sealed class NoiseFloorFlipTests
{
    public NoiseFloorFlipTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static readonly string[] ComputerEngines = { "nr2", "nr4", "spectral_capture", "rnnoise", "deepfilternet3", "nnr" };
    private static readonly string[] FloorFirst = { "nr2", "rnnoise" };
    private static readonly Dictionary<string, string> OwnReduction = new()
    {
        ["nr4"] = "reduction_db", ["spectral_capture"] = "subtraction_amount",
        ["deepfilternet3"] = "attenuation_limit_db", ["nnr"] = "mask_floor_db",
    };

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    private static PcDenoiseSlot SlotOf(NoiseFixture f, string id) =>
        f.Catalog.Find(id)!.Family == DenoiseFamily.Spectral ? f.Pipeline.SpectralSlot : f.Pipeline.NeuralSlot;

    private static NoiseLever Floor(NoiseFixture f, string id) => f.Noise.FloorLeverOf(id)!;

    [Fact]
    public void Every_computer_engine_in_this_build_has_Advanced_levers_so_the_Audio_menu_offers_its_Advanced_row()
    {
        // S7 (ruled 2026-11-09 21:12): NNR and Captured noise keep the floor in
        // Advanced and have no Advanced descriptor of their own; the menu's
        // Advanced row follows the controller's levers, so the floor is reachable
        // there. Scope (repair round 5): the engines in this build, the ones with
        // a factory. NVIDIA, listed but not in this version, is the next test's.
        using var f = new NoiseFixture();
        foreach (var id in ComputerEngines)
        {
            Assert.True(f.Noise.HasAdvancedLevers(id), id);
            Assert.True(f.Noise.OffersAdvancedRow(id), id);
        }
        foreach (var id in OwnReduction.Keys)
            Assert.Contains(f.Noise.Levers(id, includeAdvanced: true), l => l.Advanced && l.Kind == NoiseLeverKind.Floor);
        Assert.DoesNotContain(f.Catalog.Find("nnr")!.Settings, d => d.Advanced);
        Assert.DoesNotContain(f.Catalog.Find("spectral_capture")!.Settings, d => d.Advanced);
    }

    /// <summary>
    /// Repair round 5 (Codex's fourth check): NVIDIA's row is listed and has no
    /// factory yet (#768), so nothing an Advanced row opens for it can run. It
    /// still LISTS Advanced levers (the floor and Listen to what was removed are
    /// generic), which is how the menu, asking only HasAdvancedLevers, offered
    /// it the row. A missing library is NOT the same thing and keeps the row
    /// (#726, ruling 5: nothing is hidden for availability).
    /// </summary>
    [Fact]
    public void An_engine_not_in_this_build_gets_no_Advanced_row_and_an_unavailable_one_keeps_it()
    {
        using var f = new NoiseFixture();
        Assert.Null(f.Catalog.Find("nvidia_afx")!.Factory);
        Assert.True(f.Noise.HasAdvancedLevers("nvidia_afx"));   // positive control: why the old check offered the row
        Assert.False(f.Noise.OffersAdvancedRow("nvidia_afx"));
        f.Unavailable["deepfilternet3"] = Unavailability.LibraryMissing;
        Assert.True(f.Noise.OffersAdvancedRow("deepfilternet3"));
        Assert.False(f.Noise.OffersAdvancedRow("radio_nr"));    // a radio engine never had the row

        // And the Audio menu asks exactly that question. A source pin: the menu builder needs a window.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        string menu = File.ReadAllText(Path.Combine(dir!.FullName, "JJFlexWpf", "NativeMenuBar.cs"));
        Assert.Contains("if (!isRadio && noise.OffersAdvancedRow(id))", menu);
        Assert.DoesNotContain("noise.HasAdvancedLevers(id)", menu);
    }

    // ── The flip itself ──

    [Fact]
    public void A_stored_point_eight_shows_floor_twenty_and_writing_twenty_stores_the_same_bits()
    {
        using var f = new NoiseFixture();
        f.Pipeline.NeuralSlot.SetStrength("rnnoise", 0.8f);
        var floor = Floor(f, "rnnoise");
        Assert.Equal(NoiseLeverKind.Floor, floor.Kind);
        Assert.Equal(20, f.Noise.NumberOf(floor));
        f.Noise.Set(floor, 20, null, "test");
        Assert.Equal(Bits(0.8f), Bits(f.Pipeline.NeuralSlot.GetStrength("rnnoise")));
        f.Pipeline.NeuralSlot.SetStrength("rnnoise", 0.65f);
        Assert.Equal(35, f.Noise.NumberOf(floor));
    }

    [Fact]
    public void Every_shown_step_round_trips_bit_exactly_through_the_lever()
    {
        using var f = new NoiseFixture();
        var floor = Floor(f, "rnnoise");
        foreach (var step in f.Noise.LeverSteps(floor))
        {
            f.Noise.Set(floor, step.Shown, null, "test");
            float stored = f.Pipeline.NeuralSlot.GetStrength("rnnoise");
            Assert.Equal(step.Shown, f.Noise.NumberOf(floor));
            Assert.Equal(Bits(NoiseFloor.ToStrength(step.Shown!.Value)), Bits(stored));
            f.Noise.Set(floor, f.Noise.NumberOf(floor), null, "test");
            Assert.Equal(Bits(stored), Bits(f.Pipeline.NeuralSlot.GetStrength("rnnoise")));
        }
        Assert.Equal(21, f.Noise.LeverSteps(floor).Count);
    }

    [Fact]
    public void The_floor_steps_by_five_from_the_new_default()
    {
        using var f = new NoiseFixture();
        var floor = f.Noise.Levers("rnnoise", false)[0];
        Assert.Equal(NoiseLeverKind.Floor, floor.Kind);
        Assert.Equal(10, f.Noise.NumberOf(floor));
        f.Noise.Adjust(floor, +1, "test");
        Assert.Equal(15, f.Noise.NumberOf(floor));
        Assert.Equal(Bits(0.85f), Bits(f.Pipeline.NeuralSlot.GetStrength("rnnoise")));
    }

    [Fact]
    public void The_journal_records_the_floor_under_a_name_that_says_floor()
    {
        using var f = new NoiseFixture();
        var entries = new List<NoiseJournalEntry>();
        void Take(NoiseJournalEntry e) => entries.Add(e);
        NoiseSettingsJournal.Recorded += Take;
        try
        {
            f.Noise.Adjust(Floor(f, "rnnoise"), +1, "test");
            f.Noise.Set(Floor(f, "nr4"), 25, null, "test");
            f.Noise.ReturnToDefaults("nr4", "test");
        }
        finally { NoiseSettingsJournal.Recorded -= Take; }
        Assert.Contains(entries, e => e.Kind == NoiseJournalKind.Floor && e.Changes.Single() == new NoiseJournalChange("rnnoise", "floor", "10", "15"));
        Assert.Contains(entries, e => e.Kind == NoiseJournalKind.Floor && e.Changes.Single() == new NoiseJournalChange("nr4", "floor", "0", "25"));
        Assert.Contains(entries, e => e.Kind == NoiseJournalKind.ReturnToDefaults && e.Changes.Contains(new NoiseJournalChange("nr4", "floor", "25", "0")));
        Assert.DoesNotContain(entries.SelectMany(e => e.Changes), c => c.Setting == "strength");
    }

    [Theory]
    [InlineData(0, 0.05f)]     // fully processed: the fake engine halves the signal
    [InlineData(100, 0.1f)]    // unprocessed: the original only
    [InlineData(30, 0.065f)]   // 0.3 of the original plus 0.7 of the processed
    public void The_floor_is_the_share_of_the_original_the_bank_mixes_back(int floor, float expected)
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr4", "test");
        f.Settle();
        f.Noise.Set(Floor(f, "nr4"), floor, null, "test");
        var audio = new float[960];
        float last = 0;
        for (int i = 0; i < 20; i++)
        {
            Array.Fill(audio, 0.1f);
            f.Pipeline.SpectralSlot.Process(audio, true, true, false, null);
            last = audio[^1];
        }
        Assert.Equal(expected, last, 5);
    }

    // ── One source for every default ──

    [Fact]
    public void Every_default_agrees_with_the_one_source()
    {
        using var f = new NoiseFixture();
        foreach (string id in ComputerEngines)
        {
            float source = NoiseFloor.DefaultStrength(id, DenoiseDirection.Receive);
            Assert.Equal(Bits(source), Bits(NoiseController.DefaultStrength(id)));
            Assert.Equal(Bits(source), Bits(SlotOf(f, id).GetStrength(id)));
            Assert.Equal(NoiseFloor.DefaultFloor(id, DenoiseDirection.Receive), f.Noise.NumberOf(Floor(f, id)));
        }
        Assert.Equal(10, NoiseFloor.DefaultFloor("rnnoise", DenoiseDirection.Receive));

        // A brand-new setup takes the legacy load branch (version 0), so the
        // receive RNNoise default arrives through AudioOutputConfig's initialiser.
        using var fresh = new RxAudioPipeline();
        ReceiveDenoiseConfig.Map(new AudioOutputConfig(), fresh, save: false);
        Assert.Equal(Bits(NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Receive)), Bits(fresh.RnnStrength));

        // Transmit: the provider, the conditioning default, the profile and the field.
        float tx = NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Transmit);
        Assert.Equal(Bits(tx), Bits(TxAudioConditioning.DefaultNrStrength));
        Assert.Equal(Bits(tx), Bits(new TxConditioningSettings().NrStrength));
        using (var provider = TxAudioConditioning.CreateNoiseReductionProvider()) Assert.Equal(Bits(tx), Bits(provider.Strength));
        Assert.Equal(NoiseFloor.DefaultFloor("rnnoise", DenoiseDirection.Transmit), AudioWorkshopDialog.TxFloorField().Initial);
        Assert.Equal(20, AudioWorkshopDialog.TxFloorField().Initial);
    }

    [Fact]
    public void Reading_an_untouched_engine_stores_nothing()
    {
        using var f = new NoiseFixture();
        foreach (string id in ComputerEngines)
        {
            _ = SlotOf(f, id).GetStrength(id);
            _ = f.Noise.NumberOf(Floor(f, id));
            _ = f.Noise.Levers(id, true).Select(l => f.Noise.ValueText(l)).ToList();
        }
        f.Noise.SpeakActive();
        Assert.Empty(f.Pipeline.NeuralSlot.ExportSettings());
        Assert.Empty(f.Pipeline.SpectralSlot.ExportSettings());
    }

    [Fact]
    public void An_untouched_engine_reads_one_default_instance_and_allocates_nothing_on_the_audio_path()
    {
        // Repair round 1 (Codex's merge blocker): the defaults read live must be ONE
        // instance per engine, built off the audio thread, or every buffer allocates
        // and ApplyParameters' reference check never holds.
        using var f = new NoiseFixture();
        foreach (string id in ComputerEngines)
        {
            var slot = SlotOf(f, id);
            Assert.Same(slot.ParametersForTest(id), slot.ParametersForTest(id));
        }
        f.Noise.Toggle("rnnoise", "test");
        f.Settle();
        var neural = f.Pipeline.NeuralSlot;
        var audio = new float[960];
        for (int i = 0; i < 50; i++) { Array.Fill(audio, 0.1f); neural.Process(audio, true, true, false, null); }   // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) _ = neural.GetStrength("rnnoise");
        for (int i = 0; i < 200; i++) { Array.Fill(audio, 0.1f); neural.Process(audio, true, true, false, null); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Empty(neural.ExportSettings());   // and reading still stores nothing
    }

    [Fact]
    public void A_floor_change_on_an_engine_with_a_stored_model_is_applied_with_no_allocation()
    {
        // Repair round 2 (Codex's second check): once RNNoise holds a stored model
        // (a model choice or Return to defaults stores one), every floor change
        // makes a new Parameters record, and applying it on the audio thread must
        // allocate nothing. The scenario is Codex's: choose the default model
        // explicitly, let the bank be replaced, warm up, set Strength, then count.
        using var f = new NoiseFixture();
        var neural = f.Pipeline.NeuralSlot;
        f.Noise.Toggle("rnnoise", "test");
        f.Settle();
        int built = f.Built.Count(b => b == "rnnoise");
        Assert.True(neural.TrySetSetting("rnnoise", "model", new(0, RnnoiseModels.Newest)));
        var audio = new float[960];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (f.Built.Count(b => b == "rnnoise") <= built && clock.ElapsedMilliseconds < 5000)
        {
            Array.Fill(audio, 0.1f); neural.Process(audio, true, true, false, null);
            System.Threading.Thread.Sleep(1);
        }
        for (int i = 0; i < 400; i++) { Array.Fill(audio, 0.1f); neural.Process(audio, true, true, false, null); }   // the handover and warm-up
        long allocated = 0;
        foreach (float strength in new[] { 0.85f, 0.8f, 0.75f })
        {
            neural.SetStrength("rnnoise", strength);                    // the control side makes the new record
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) { Array.Fill(audio, 0.1f); neural.Process(audio, true, true, false, null); }
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(RnnoiseModels.Newest, neural.GetSetting("rnnoise", "model").Choice);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_first_setting_on_an_untouched_engine_keeps_its_default_floor()
    {
        using var f = new NoiseFixture();
        // NR4 has a default floor of 0, so the old defect (an entry created at
        // Strength 1 whatever the engine) hides there; RNNoise's model is the
        // case it would have bitten (A3), checked in the model tests. Here: a
        // stored floor survives a first setting on a SPECTRAL engine too.
        Assert.True(f.Pipeline.SpectralSlot.TrySetSetting("nr4", "smoothing", new(0.25)));
        Assert.Equal(Bits(NoiseFloor.DefaultStrength("nr4", DenoiseDirection.Receive)), Bits(f.Pipeline.SpectralSlot.GetStrength("nr4")));
        Assert.True(f.Pipeline.NeuralSlot.TrySetSetting("nnr", "mask_floor_db", new(-30)));
        Assert.Equal(Bits(NoiseFloor.DefaultStrength("nnr", DenoiseDirection.Receive)), Bits(f.Pipeline.NeuralSlot.GetStrength("nnr")));
    }

    // ── The floor on all six engines (S2) ──

    [Fact]
    public void Every_computer_engine_has_a_floor_and_four_keep_it_in_Advanced_after_their_own_reduction()
    {
        using var f = new NoiseFixture();
        foreach (string id in ComputerEngines)
        {
            var main = f.Noise.Levers(id, includeAdvanced: false);
            var all = f.Noise.Levers(id, includeAdvanced: true);
            var floor = all.Single(l => l.Kind == NoiseLeverKind.Floor);
            Assert.Equal(NoiseController.FloorKey, floor.Key);
            if (FloorFirst.Contains(id))
            {
                Assert.False(floor.Advanced);
                Assert.Equal(NoiseLeverKind.Floor, main[0].Kind);
            }
            else
            {
                Assert.True(floor.Advanced, id);
                Assert.DoesNotContain(main, l => l.Kind == NoiseLeverKind.Floor);
                Assert.Equal(OwnReduction[id], main[0].Key);
                Assert.Equal(OwnReduction[id], all[0].Key);
                // The layer orders it after every main lever, first among the Advanced ones.
                int at = all.ToList().IndexOf(floor);
                Assert.All(all.Take(at), l => Assert.False(l.Advanced));
                Assert.Equal(main.Count, at);
                Assert.Equal(0, f.Noise.NumberOf(floor));
            }
        }
    }

    [Fact]
    public void A_stored_strength_on_an_engine_whose_floor_is_Advanced_is_applied_as_stored()
    {
        using var f = new NoiseFixture();
        f.Pipeline.NeuralSlot.SetStrength("deepfilternet3", 0.7f);
        f.Noise.Toggle("deepfilternet3", "test");
        f.Settle();
        var audio = new float[960];
        for (int i = 0; i < 20; i++) { Array.Fill(audio, 0.1f); f.Pipeline.NeuralSlot.Process(audio, true, true, false, null); }
        Assert.Equal(0.1f * 0.3f + 0.05f * 0.7f, audio[^1], 5);
    }

    [Fact]
    public void Return_to_defaults_sets_every_engines_floor_whether_or_not_it_is_shown()
    {
        using var f = new NoiseFixture();
        foreach (string id in ComputerEngines)
        {
            f.Noise.Set(Floor(f, id), 55, null, "test");
            Assert.Equal(55, f.Noise.NumberOf(Floor(f, id)));
            f.Noise.ReturnToDefaults(id, "test");
            Assert.Equal(NoiseFloor.DefaultFloor(id, DenoiseDirection.Receive), f.Noise.NumberOf(Floor(f, id)));
        }
    }

    [Fact]
    public void The_Noise_Profiles_window_reads_and_writes_the_floor_even_where_it_is_Advanced()
    {
        using var f = new NoiseFixture();
        f.Pipeline.SpectralSlot.Select("nr4", out _);
        Assert.Equal(0, NoiseProfilesDialog.FloorFor(f.Noise, f.Pipeline.SpectralSlot));
        NoiseProfilesDialog.SetFloorFor(f.Noise, f.Pipeline.SpectralSlot, 40);
        Assert.Equal(40, NoiseProfilesDialog.FloorFor(f.Noise, f.Pipeline.SpectralSlot));
        Assert.Equal(Bits(0.6f), Bits(f.Pipeline.SpectralSlot.GetStrength("nr4")));
        // RNNoise's floor is a main lever; the window reads it the same way.
        Assert.Equal(10, NoiseProfilesDialog.FloorFor(f.Noise, f.Pipeline.NeuralSlot));
    }

    // ── The direction switch (S7) ──

    [Fact]
    public void Up_raises_the_floor_on_every_surface_and_flipping_the_one_switch_inverts_them_together()
    {
        Assert.True(NoiseController.UpRaisesFloor, "the ruled direction is Up raises the floor");
        AssertDirection(upRaises: true);
        try
        {
            NoiseController.UpRaisesFloor = false;
            AssertDirection(upRaises: false);
        }
        finally { NoiseController.UpRaisesFloor = true; }
    }

    private static void AssertDirection(bool upRaises)
    {
        using var f = new NoiseFixture();
        int sign = upRaises ? 1 : -1;
        var floor = Floor(f, "rnnoise");

        // The noise layer's Up, Down, Home and End, through its own key handler:
        // Ctrl+R turns RNNoise on and leaves the layer on its first lever, the floor.
        var keys = new NoiseLayerKeys(f.Noise);
        keys.Handle(System.Windows.Forms.Keys.R | System.Windows.Forms.Keys.Control);
        Assert.Equal(NoiseLeverKind.Floor, f.Noise.CurrentLever!.Kind);
        keys.Handle(System.Windows.Forms.Keys.Up);
        Assert.Equal(10 + 5 * sign, f.Noise.NumberOf(floor));
        keys.Handle(System.Windows.Forms.Keys.Down);
        Assert.Equal(10, f.Noise.NumberOf(floor));
        keys.Handle(System.Windows.Forms.Keys.End);
        Assert.Equal(upRaises ? 100 : 0, f.Noise.NumberOf(floor));
        keys.Handle(System.Windows.Forms.Keys.Home);
        Assert.Equal(upRaises ? 0 : 100, f.Noise.NumberOf(floor));

        // The Audio menu's Up row calls the same Adjust.
        f.Noise.Set(floor, 50, null, "test");
        f.Noise.Adjust(floor, +1, "menu");
        Assert.Equal(50 + 5 * sign, f.Noise.NumberOf(floor));

        // Home and End.
        f.Noise.Place(floor, -1, "test");
        Assert.Equal(upRaises ? 0 : 100, f.Noise.NumberOf(floor));
        f.Noise.Place(floor, +1, "test");
        Assert.Equal(upRaises ? 100 : 0, f.Noise.NumberOf(floor));

        // Home's field arrows through LeverSteps: Up is the next index.
        var steps = f.Noise.LeverSteps(floor);
        Assert.Equal(upRaises ? 0 : 100, steps[0].Shown);
        Assert.Equal(upRaises ? 5 : 95, steps[1].Shown);

        // The transmit field and the Noise Profiles floors read the same switch.
        Assert.Equal(!upRaises, AudioWorkshopDialog.TxFloorField().UpLowers);
    }

    // ── Nobody else does arithmetic ──

    /// <summary>Files allowed to call the slot's Strength: the slot itself, the controller (the lever and its presets), and the pipeline's legacy RNNoise property the receive config mapping reads.</summary>
    private static readonly string[] StrengthCallersAllowed =
    {
        "JJFlexWpf/PcDenoiseSlot.cs", "JJFlexWpf/NoiseController.Levers.cs", "JJFlexWpf/NoiseController.Presets.cs",
        "JJFlexWpf/RxAudioPipeline.cs",
    };

    internal static IReadOnlyList<string> StrengthCallers(string root)
    {
        var call = new Regex(@"\b(Get|Set)Strength\s*\(", RegexOptions.Compiled);
        var found = new List<string>();
        foreach (string dir in new[] { "JJFlexWpf", "Radios" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                if (call.IsMatch(File.ReadAllText(file))) found.Add(rel);
            }
        foreach (string file in Directory.EnumerateFiles(root, "*.vb", SearchOption.TopDirectoryOnly))
            if (call.IsMatch(File.ReadAllText(file))) found.Add(Path.GetFileName(file));
        return found;
    }

    [Fact]
    public void No_surface_reads_or_writes_the_slots_Strength_on_its_own()
    {
        var callers = StrengthCallers(RepoPaths.RequireRoot());
        // Positive control: the scan sees the known call in the controller.
        Assert.Contains("JJFlexWpf/NoiseController.Levers.cs", callers);
        Assert.Empty(callers.Except(StrengthCallersAllowed));
    }
}
