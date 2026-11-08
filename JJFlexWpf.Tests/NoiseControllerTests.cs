using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E4: the one noise controller every surface calls.
/// Windowless: a fake radio and fake engines with the real descriptors.
/// </summary>
public sealed class NoiseControllerTests
{
    public NoiseControllerTests() => NoiseReductionPerChannelTests.AssertWindowless();

    // ── Switching and the family replace rule (S5) ──

    [Fact]
    public void A_computer_engine_replaces_the_other_engine_of_its_family_and_says_so()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr4", "test");
        Assert.True(f.Noise.IsOn("nr4"));
        Assert.Equal("NR4 on.", f.Spoken.Last);
        f.Noise.Toggle("spectral_capture", "test");
        Assert.True(f.Noise.IsOn("spectral_capture"));
        Assert.False(f.Noise.IsOn("nr4"));
        Assert.Equal("Captured noise on, replacing NR4.", f.Spoken.Last);
        Assert.Equal(SpeechSubject.NoiseEngineSwitch, f.Spoken.Said[^1].Subject);
    }

    [Fact]
    public void The_other_family_and_the_radio_are_left_alone()
    {
        using var f = new NoiseFixture();
        f.Radio.Switches["radio_nrl"] = true;
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Toggle("nr2", "test");
        f.Noise.Toggle("nr4", "test");
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.True(f.Noise.IsOn("nr4"));
        Assert.True(f.Radio.Switches["radio_nrl"]);
        Assert.DoesNotContain(f.Radio.Writes, w => w.StartsWith("radio_nrl"));
    }

    [Fact]
    public void Pressing_the_engine_that_is_on_turns_it_off_and_one_press_never_silences_a_family_by_surprise()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr2", "test");
        f.Noise.Toggle("nr2", "test");
        Assert.False(f.Noise.IsOn("nr2"));
        Assert.False(f.Pipeline.SpectralSlot.Enabled);
        Assert.Equal("NR2 off.", f.Spoken.Last);
        // NR2 on, then NR4: the press switched, it did not silence the family.
        f.Noise.Toggle("nr2", "test");
        f.Noise.Toggle("nr4", "test");
        Assert.True(f.Pipeline.SpectralSlot.Enabled);
    }

    [Fact]
    public void A_radio_engine_flips_its_own_switch()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("radio_nr", "test");
        Assert.Equal(new[] { "radio_nr=on" }, f.Radio.Writes);
        Assert.Equal("Standard NR on.", f.Spoken.Last);
        f.Noise.Toggle("radio_nr", "test");
        Assert.Equal("Standard NR off.", f.Spoken.Last);
    }

    [Fact]
    public void A_gated_radio_engine_refuses_with_the_ladder_and_writes_nothing()
    {
        using var f = new NoiseFixture();
        f.Radio.Refused[RadioCapability.NoiseReductionSpectral] = "This radio model does not have the DSP for this";
        f.Noise.Toggle("radio_nrs", "test");
        Assert.Empty(f.Radio.Writes);
        Assert.StartsWith("Spectral NR:", f.Spoken.Last);
        Assert.Equal(SpeechSubject.NoiseUnavailable, f.Spoken.Said[^1].Subject);
        // Its level is gated the same way (design part one: the level setters were not gated).
        f.Noise.Adjust(f.Noise.Levers("radio_nrs", false)[0], +1, "test");
        Assert.Empty(f.Radio.Writes);
    }

    [Fact]
    public void The_audio_peak_filter_is_CW_only_and_says_so()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("radio_apf", "test");
        Assert.Empty(f.Radio.Writes);
        Assert.Contains("CW only", f.Spoken.Last);
        f.Radio.Mode = "CW";
        f.Noise.Toggle("radio_apf", "test");
        Assert.Equal(new[] { "radio_apf=on" }, f.Radio.Writes);
    }

    [Fact]
    public void An_engine_not_in_this_build_says_so_plainly_and_changes_nothing()
    {
        using var f = new NoiseFixture();
        string before = f.Pipeline.NeuralSlot.ChosenEngineId;
        f.Noise.Toggle("nvidia_afx", "test");
        Assert.Equal("NVIDIA NR is not available in this version of JJ Flexible.", f.Spoken.Last);
        Assert.Equal(before, f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
        Assert.Equal("not in this version", f.Noise.Marker("nvidia_afx"));
    }

    [Fact]
    public void An_unavailable_engine_is_still_chosen_as_an_intent_and_says_why_and_that_the_choice_is_kept()
    {
        using var f = new NoiseFixture();
        f.Unavailable["nnr"] = Unavailability.HelperMissing;
        f.Noise.Toggle("nnr", "test");
        Assert.Equal("nnr", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.True(f.Pipeline.NeuralSlot.Enabled);
        Assert.Contains("missing from this installation", f.Spoken.Last);
        Assert.EndsWith("Your choice is kept and will start when it can.", f.Spoken.Last);
        Assert.Equal("Helper missing", f.Noise.Marker("nnr"));
    }

    [Fact]
    public void Choosing_a_helper_engine_says_its_delay_unless_the_setting_is_off()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr2", "test");
        Assert.Equal("NR2 on. It adds about 105 milliseconds.", f.Spoken.Last);
        f.Noise.Toggle("nr2", "test");
        f.Config.NoiseSayDelays = false;
        f.Noise.Toggle("nr2", "test");
        Assert.Equal("NR2 on.", f.Spoken.Last);
    }

    [Fact]
    public void PC_audio_off_keeps_the_choice_and_says_so()
    {
        using var f = new NoiseFixture();
        f.Radio.PcAudioOn = false;
        f.Noise.Toggle("nr4", "test");
        Assert.True(f.Noise.IsOn("nr4"));
        Assert.StartsWith("PC audio is off.", f.Spoken.Last);
    }

    // ── Levers ──

    [Fact]
    public void Levers_list_the_primary_first_and_Advanced_only_when_asked()
    {
        using var f = new NoiseFixture();
        Assert.Equal(new[] { "strength", "reduction_db" }, f.Noise.Levers("nr4", false).Select(l => l.Key));
        var all = f.Noise.Levers("nr4", true).Select(l => l.Key).ToList();
        Assert.Equal("strength", all[0]);
        Assert.Contains("noise_estimator", all);
        Assert.Equal("listen_removed", all[^1]);
        Assert.Equal(new[] { "level" }, f.Noise.Levers("radio_nrl", true).Select(l => l.Key));
        Assert.Empty(f.Noise.Levers("radio_rnn", true));
        Assert.Equal(new[] { "strength", "model", "mask_floor_db" }, f.Noise.Levers("nnr", false).Select(l => l.Key));
    }

    [Fact]
    public void NR2s_trained_values_appear_only_while_the_gain_curve_is_Trained()
    {
        using var f = new NoiseFixture();
        Assert.DoesNotContain(f.Noise.Levers("nr2", true), l => l.Key.StartsWith("trained_"));
        f.Pipeline.SpectralSlot.TrySetSetting("nr2", "gain_method", new(0, "trained"));
        Assert.Contains(f.Noise.Levers("nr2", true), l => l.Key == "trained_t1");
    }

    [Fact]
    public void Depth_is_the_mask_floor_shown_positive_so_up_means_more_depth()
    {
        using var f = new NoiseFixture();
        var depth = f.Noise.Levers("nnr", false).Single(l => l.Key == "mask_floor_db");
        Assert.Equal(25, f.Noise.NumberOf(depth));
        f.Noise.Adjust(depth, +1, "test");
        Assert.Equal(-26, f.Pipeline.NeuralSlot.GetSetting("nnr", "mask_floor_db").Number);
        Assert.Equal(26, f.Noise.NumberOf(depth));
        Assert.Equal("NNR depth 26 decibels. NNR is off; it will use this when you turn it on.", f.Spoken.Last);
        f.Spoken.Verbosity = VerbosityLevel.Terse;
        f.Noise.Adjust(depth, -1, "test");
        Assert.Equal("Depth 25.", f.Spoken.Last);
    }

    [Fact]
    public void A_lever_at_its_end_stays_and_says_so()
    {
        using var f = new NoiseFixture();
        var depth = f.Noise.Levers("nnr", false).Single(l => l.Key == "mask_floor_db");
        f.Noise.Place(depth, +1, "test");
        Assert.Equal(50, f.Noise.NumberOf(depth));
        f.Noise.Adjust(depth, +1, "test");
        Assert.Equal("Depth is already at 50 decibels, the most.", f.Spoken.Last);
    }

    [Fact]
    public void DeepFilterNet3s_limit_steps_stop_to_stop_and_says_unlimited_at_the_top()
    {
        using var f = new NoiseFixture();
        var limit = f.Noise.Levers("deepfilternet3", false).Single(l => l.Key == "attenuation_limit_db");
        f.Noise.Adjust(limit, +1, "test");
        Assert.Equal(12, f.Noise.NumberOf(limit));
        f.Noise.Place(limit, +1, "test");
        Assert.Equal("unlimited", f.Noise.ValueText(limit));
    }

    [Fact]
    public void Strength_steps_by_five_percent()
    {
        using var f = new NoiseFixture();
        var strength = f.Noise.Levers("rnnoise", false)[0];
        Assert.Equal(80, f.Noise.NumberOf(strength));
        f.Noise.Adjust(strength, +1, "test");
        Assert.Equal(0.85f, f.Pipeline.NeuralSlot.GetStrength("rnnoise"), 3);
    }

    [Fact]
    public void A_radio_level_steps_fine_then_coarse_and_the_unreported_ones_are_held_as_intents_per_radio()
    {
        using var f = new NoiseFixture();
        f.Radio.Levels["radio_nrl"] = 15;
        var level = f.Noise.Levers("radio_nrl", false)[0];
        f.Noise.Adjust(level, +1, "test");
        Assert.Equal(20, f.Radio.Levels["radio_nrl"]);
        Assert.Equal(20, f.Held.Get("1234-5678", "radio_nrl"));
        // A reported level is the radio's own; nothing is held for it.
        f.Noise.Adjust(f.Noise.Levers("radio_nb", false)[0], +1, "test");
        Assert.Null(f.Held.Get("1234-5678", "radio_nb"));
        // Another radio keeps its own.
        Assert.Null(f.Held.Get("9999", "radio_nrl"));
    }

    [Fact]
    public void Held_intents_are_written_on_connect_and_on_a_slice_change_and_nothing_is_written_when_never_set()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        Assert.Empty(f.Radio.Writes.Where(w => w.Contains(".level")));
        f.Held.Set("1234-5678", "radio_nrl", 30);
        f.Held.Set("1234-5678", "radio_anfl", 40);
        f.Held.Set("other-radio", "radio_nrs", 50);
        f.Noise.Attach("USB");
        Assert.Contains("radio_nrl.level=30", f.Radio.Writes);
        Assert.Contains("radio_anfl.level=40", f.Radio.Writes);
        Assert.DoesNotContain(f.Radio.Writes, w => w.StartsWith("radio_nrs") || w.StartsWith("radio_nrf") || w.StartsWith("radio_nb"));
        f.Radio.Writes.Clear();
        f.Noise.NoteActiveSlice(0);
        Assert.Empty(f.Radio.Writes);
        f.Noise.NoteActiveSlice(1);
        Assert.Equal(new[] { "radio_nrl.level=30", "radio_anfl.level=40" }, f.Radio.Writes);
    }

    // ── Return to defaults (D4) ──

    [Fact]
    public void Return_to_defaults_restores_a_computer_engine_and_leaves_it_on_or_off_as_it_was()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr4", "test");
        var reduction = f.Noise.Levers("nr4", false)[1];
        f.Noise.Adjust(reduction, +1, "test");
        f.Noise.Adjust(f.Noise.Levers("nr4", false)[0], -1, "test");
        f.Pipeline.SpectralSlot.TrySetSetting("nr4", "smoothing", new(0.25));
        f.Noise.ReturnToDefaults("nr4", "test");
        Assert.Equal(20, f.Pipeline.SpectralSlot.GetSetting("nr4", "reduction_db").Number);
        Assert.Equal(0.5, f.Pipeline.SpectralSlot.GetSetting("nr4", "smoothing").Number);
        Assert.Equal(1f, f.Pipeline.SpectralSlot.GetStrength("nr4"));
        Assert.True(f.Noise.IsOn("nr4"));
        Assert.Equal("NR4 back to its defaults: strength 100 percent, reduction 20 decibels.", f.Spoken.Last);
        Assert.Equal(SpeechSubject.NoiseDefaults, f.Spoken.Said[^1].Subject);

        f.Noise.Toggle("nr4", "test");
        f.Noise.ReturnToDefaults("nr4", "test");
        Assert.False(f.Noise.IsOn("nr4"));
    }

    [Fact]
    public void A_radio_engine_with_a_level_says_why_and_writes_nothing_and_a_switch_offers_no_row()
    {
        using var f = new NoiseFixture();
        f.Noise.ReturnToDefaults("radio_nb", "test");
        Assert.Empty(f.Radio.Writes);
        Assert.Equal("The radio does not report a factory level for Noise Blanker, so there is no default to return to. Your level stays as it is.", f.Spoken.Last);
        Assert.True(f.Noise.OffersDefaults("radio_nb"));
        Assert.False(f.Noise.OffersDefaults("radio_rnn"));
        Assert.False(f.Noise.OffersDefaults("radio_anft"));
        f.Noise.ReturnToDefaults("radio_rnn", "test");
        Assert.Equal("Neural NR is a switch, with no level to return to.", f.Spoken.Last);
        Assert.Empty(f.Radio.Writes);
    }

    // ── Favourites and the brackets ──

    [Fact]
    public void Favourites_add_remove_keep_order_and_Neural_NR_can_be_one()
    {
        using var f = new NoiseFixture();
        f.Noise.ToggleFavourite("radio_rnn", "test");
        Assert.Equal("Neural NR added to favourites.", f.Spoken.Last);
        f.Noise.ToggleFavourite("nr2", "test");
        f.Noise.ToggleFavourite("radio_anft", "test");
        Assert.Equal(new[] { "radio_rnn", "nr2", "radio_anft" }, f.Config.NoiseFavourites.Select(x => x.EngineId));
        f.Noise.MoveFavourite(2, 1);
        Assert.Equal(new[] { "radio_anft", "radio_rnn", "nr2" }, f.Config.NoiseFavourites.Select(x => x.EngineId));
        f.Noise.ToggleFavourite("radio_rnn", "test");
        Assert.Equal("Neural NR removed from favourites.", f.Spoken.Last);
        Assert.Equal(new[] { "radio_anft", "nr2" }, f.Config.NoiseFavourites.Select(x => x.EngineId));
        Assert.Equal(SpeechSubject.NoiseFavourite, f.Spoken.Said[^1].Subject);
    }

    [Fact]
    public void A_favourite_carries_the_model_it_was_marked_with_so_two_models_are_two_favourites()
    {
        using var f = new NoiseFixture();
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium_cw"));
        f.Noise.ToggleFavourite("nnr", "test");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "standard_cw"));
        f.Noise.ToggleFavourite("nnr", "test");
        Assert.Equal(new[] { "premium_cw", "standard_cw" }, f.Config.NoiseFavourites.Select(x => x.Model));
        Assert.Equal("NNR Standard CW added to favourites.", f.Spoken.Last);
        Assert.Equal("NNR Premium CW", f.Noise.FavouriteName(f.Config.NoiseFavourites[0]));
        f.Noise.RenameFavourite(0, "Weak CW");
        Assert.Equal("Weak CW", f.Noise.FavouriteName(f.Config.NoiseFavourites[0]));
    }

    [Fact]
    public void Stepping_between_two_reducers_turns_the_one_stepped_from_off_across_slots()
    {
        using var f = new NoiseFixture();
        f.Noise.ToggleFavourite("nr2", "test");
        f.Noise.ToggleFavourite("rnnoise", "test");
        f.Noise.BeginSession();
        f.Noise.Step(+1, "test");
        Assert.True(f.Noise.IsOn("nr2"));
        f.Noise.Step(+1, "test");
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.False(f.Noise.IsOn("nr2"));
        Assert.Equal("RNNoise on, replacing NR2.", f.Spoken.Last);
    }

    [Fact]
    public void Stepping_between_the_two_subjects_turns_nothing_off_and_within_blankers_and_notches_it_does()
    {
        using var f = new NoiseFixture();
        foreach (string id in new[] { "radio_nb", "nr2", "radio_wnb", "radio_anf" }) f.Noise.ToggleFavourite(id, "test");
        f.Noise.BeginSession();
        f.Noise.Step(+1, "test");                  // Noise Blanker on
        f.Noise.Step(+1, "test");                  // NR2: a reducer, so the blanker stays on
        Assert.True(f.Noise.IsOn("radio_nb"));
        Assert.True(f.Noise.IsOn("nr2"));
        f.Noise.Step(+1, "test");                  // Wideband NB: NR2 stays on
        Assert.True(f.Noise.IsOn("nr2"));
        f.Noise.Step(+1, "test");                  // Standard Auto-Notch: Wideband NB, the same subject, off
        Assert.False(f.Noise.IsOn("radio_wnb"));
        Assert.True(f.Noise.IsOn("radio_anf"));
        Assert.True(f.Noise.IsOn("radio_nb"));
    }

    [Fact]
    public void Stepping_between_two_favourites_of_one_engine_changes_the_model_in_place_and_never_turns_it_off()
    {
        using var f = new NoiseFixture();
        f.Radio.Mode = "CW";
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium_cw"));
        f.Noise.ToggleFavourite("nnr", "test");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "standard_cw"));
        f.Noise.ToggleFavourite("nnr", "test");
        f.Noise.BeginSession();
        f.Noise.Step(+1, "test");
        Assert.Equal("premium_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.True(f.Noise.IsOn("nnr"));
        f.Noise.Step(+1, "test");
        Assert.Equal("standard_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.Equal("NNR Standard CW on.", f.Spoken.Last);
    }

    [Fact]
    public void With_no_favourites_the_brackets_step_every_engine_and_say_so_once()
    {
        using var f = new NoiseFixture();
        f.Noise.BeginSession();
        f.Noise.Step(+1, "test");
        Assert.True(f.Noise.IsOn("radio_nr"));
        Assert.Contains(f.Spoken.Said, s => s.Text.StartsWith("No favourites yet"));
        f.Noise.Step(+1, "test");
        Assert.Single(f.Spoken.Said, s => s.Text.StartsWith("No favourites yet"));
    }

    [Fact]
    public void Stepping_onto_an_unavailable_favourite_says_why_and_the_next_press_moves_on()
    {
        using var f = new NoiseFixture();
        f.Radio.Refused[RadioCapability.NoiseReductionNeural] = "needs a subscription";
        foreach (string id in new[] { "radio_nr", "radio_rnn", "nr4" }) f.Noise.ToggleFavourite(id, "test");
        f.Noise.BeginSession();
        f.Noise.Step(+1, "test");
        f.Noise.Step(+1, "test");
        Assert.StartsWith("Neural NR:", f.Spoken.Last);
        Assert.False(f.Radio.Switches.GetValueOrDefault("radio_rnn"));
        f.Noise.Step(+1, "test");
        Assert.True(f.Noise.IsOn("nr4"));
    }

    // ── The 0 key, the summary, the settle, Escape ──

    [Fact]
    public void Zero_turns_every_computer_slot_off_and_back_and_leaves_the_radio_alone()
    {
        using var f = new NoiseFixture();
        f.Radio.Switches["radio_nrl"] = true;
        f.Noise.Toggle("nr2", "test");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.ComputerOffAndBack("test");
        Assert.False(f.Pipeline.SpectralSlot.Enabled);
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
        Assert.Equal("Computer noise reduction off. Press 0 again to bring back NR2 and RNNoise.", f.Spoken.Last);
        f.Noise.ComputerOffAndBack("test");
        Assert.True(f.Noise.IsOn("nr2"));
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.Equal("Computer noise reduction back on: NR2 and RNNoise.", f.Spoken.Last);
        Assert.True(f.Radio.Switches["radio_nrl"]);
        f.Noise.ComputerOffAndBack("test");
        f.Noise.ForgetZero();
        f.Noise.ComputerOffAndBack("test");
        Assert.Equal("Computer noise reduction is already off.", f.Spoken.Last);
    }

    [Fact]
    public void The_active_summary_names_the_radio_and_the_computer()
    {
        using var f = new NoiseFixture();
        Assert.Equal("No noise reduction is on.", f.Noise.ActiveSummary());
        f.Radio.Switches["radio_nrl"] = true;
        f.Radio.Levels["radio_nrl"] = 8;
        f.Noise.Toggle("nnr", "test");
        f.Settle();
        Assert.Equal("On the radio: Legacy NR, level 8. On this computer: NNR, Standard voice, depth 25 decibels.", f.Noise.ActiveSummary());
        f.Spoken.Verbosity = VerbosityLevel.Terse;
        Assert.Equal("Radio: Legacy NR 8. Computer: NNR.", f.Noise.ActiveSummary());
    }

    [Fact]
    public void Arrowing_an_engine_field_builds_nothing_until_the_choice_settles()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        f.Settle();
        int built = f.Built.Count;
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "deepfilternet3", "test");
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nnr", "test");
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nvidia_afx", "test");
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "deepfilternet3", "test");
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Equal(built, f.Built.Count);
        Assert.Equal(1, f.Clock.Pending);
        f.Clock.RunAll();
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Equal("DeepFilterNet3 on, replacing RNNoise.", f.Spoken.Last);
        Assert.Equal(700, NoiseController.EngineSettleMs);
    }

    [Fact]
    public void Escape_puts_back_switches_levers_favourites_and_slots_and_says_whether_anything_changed()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        f.Radio.Levels["radio_nb"] = 50;
        var snap = f.Noise.TakeSnapshot();
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));

        snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("radio_nb", "test");
        f.Noise.Adjust(f.Noise.Levers("radio_nb", false)[0], +1, "test");
        f.Noise.Toggle("nr4", "test");
        f.Noise.Adjust(f.Noise.Levers("rnnoise", false)[0], +1, "test");
        f.Noise.Toggle("deepfilternet3", "test");
        f.Noise.ToggleFavourite("nr4", "test");
        f.Radio.Writes.Clear();
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Contains("radio_nb=off", f.Radio.Writes);
        Assert.Contains("radio_nb.level=50", f.Radio.Writes);
        Assert.False(f.Pipeline.SpectralSlot.Enabled);
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.True(f.Pipeline.NeuralSlot.Enabled);
        Assert.Equal(0.8f, f.Pipeline.NeuralSlot.GetStrength("rnnoise"), 3);
        Assert.Empty(f.Config.NoiseFavourites);
    }

    [Fact]
    public void Escape_puts_back_an_engine_first_touched_in_the_layer_and_reading_is_not_a_change()
    {
        using var f = new NoiseFixture();
        var snap = f.Noise.TakeSnapshot();
        f.Noise.SpeakActive();                                   // reads every strength: not a change
        var levers = f.Noise.Levers("deepfilternet3", true);     // reads, creating entries
        Assert.NotEmpty(levers);
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));

        snap = f.Noise.TakeSnapshot();
        var limit = f.Noise.Levers("deepfilternet3", false).Single(l => l.Key == "attenuation_limit_db");
        f.Noise.Adjust(limit, +1, "test");
        Assert.Equal(12, f.Noise.NumberOf(limit));
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Equal(9, f.Noise.NumberOf(limit));
    }

    [Fact]
    public void Every_change_goes_through_the_one_journal()
    {
        using var f = new NoiseFixture();
        var entries = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { lock (entries) entries.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try
        {
            f.Noise.Toggle("nr4", "layer");
            f.Noise.Adjust(f.Noise.Levers("nr4", false)[1], +1, "menu");
            f.Noise.ReturnToDefaults("nr4", "home");
        }
        finally { NoiseSettingsJournal.Recorded -= Listen; }
        lock (entries)
        {
            Assert.Contains(entries, e => e.Kind == NoiseJournalKind.Switch && e.Source == "layer" && e.Changes[0].EngineId == "nr4");
            Assert.Contains(entries, e => e.Kind == NoiseJournalKind.Lever && e.Source == "menu" && e.Changes[0].Setting == "reduction_db");
            Assert.Contains(entries, e => e.Kind == NoiseJournalKind.ReturnToDefaults && e.Source == "home");
        }
    }
}
