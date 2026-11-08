using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E7: the noise layer's keys, through the one controller.
/// Windowless: the key handler and the controller over fakes; the layer
/// engine's own Shift+Alt rule is ValueLayerShiftAltTests in Radios.Tests.
/// </summary>
public sealed class NoiseLayerKeysTests
{
    public NoiseLayerKeysTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static IReadOnlyList<KeyInventory.FixedKeyEntry> Rows() => KeyInventory.LayerCommands(KeyInventory.NoiseLayerContext);

    [Fact]
    public void Every_engine_key_is_a_row_in_the_inventory_and_every_engine_row_is_a_key()
    {
        var rows = Rows();
        foreach (var (chord, id) in NoiseLayerKeys.EngineKeys)
        {
            var row = rows.SingleOrDefault(r => Radios.LeaderChordParser.TryParseChord(r.KeyDisplay, out var k) && k == chord);
            Assert.True(row != null, "no inventory row for " + KeyManifest.FormatKey(chord) + " (" + id + ")");
            // The description is the engine's name and the action, nothing more.
            Assert.Equal(Lexicon.Get(new DenoiseEngineCatalog().Find(id)!.LabelKey) + " on or off", row!.Description);
            Assert.Contains("on or off", row.Description);
        }
        int engineRows = rows.Count(r => r.Description.EndsWith(" on or off", StringComparison.Ordinal));
        Assert.Equal(NoiseLayerKeys.EngineKeys.Count, engineRows);
    }

    [Theory]
    [InlineData(Keys.T)]
    [InlineData(Keys.B | Keys.Control)]
    [InlineData(Keys.W | Keys.Control)]
    [InlineData(Keys.N | Keys.Alt)]
    [InlineData(Keys.F | Keys.Alt)]
    [InlineData(Keys.A | Keys.Alt)]
    [InlineData(Keys.R | Keys.Alt)]
    public void Held_and_Alt_keys_are_not_the_layers(Keys key)
    {
        using var f = new NoiseFixture();
        Assert.False(new NoiseLayerKeys(f.Noise).Handle(key));
        Assert.False(new NoiseLayerKeys(f.Noise).HandleShiftAlt(key));
        Assert.Empty(f.Spoken.Said);
    }

    [Fact]
    public void The_Shift_Alt_chords_are_exactly_the_ruled_five_and_none_is_bound_anywhere_else_in_the_layer()
    {
        Assert.Equal(new[]
        {
            Keys.A | Keys.Shift | Keys.Alt, Keys.F | Keys.Shift | Keys.Alt, Keys.R | Keys.Shift | Keys.Alt,
            Keys.OemCloseBrackets | Keys.Shift | Keys.Alt, Keys.OemOpenBrackets | Keys.Shift | Keys.Alt,
        }, NoiseLayerKeys.ShiftAltChords);
        Assert.All(NoiseLayerKeys.ShiftAltChords, k => Assert.Equal(Keys.Shift | Keys.Alt, k & Keys.Modifiers));
        Assert.DoesNotContain(NoiseLayerKeys.ShiftAltChords, k => (k & Keys.KeyCode) == Keys.F4);
    }

    [Theory]
    [InlineData(Keys.L, "radio_nrl")]
    [InlineData(Keys.N, "radio_nr")]
    [InlineData(Keys.R, "radio_rnn")]
    [InlineData(Keys.S, "radio_nrs")]
    [InlineData(Keys.F, "radio_nrf")]
    [InlineData(Keys.B, "radio_nb")]
    [InlineData(Keys.W, "radio_wnb")]
    [InlineData(Keys.A, "radio_anft")]
    [InlineData(Keys.A | Keys.Shift | Keys.Alt, "radio_anfl")]
    [InlineData(Keys.O, "radio_anf")]
    public void Each_radio_letter_flips_its_engine(Keys key, string id)
    {
        using var f = new NoiseFixture();
        var keys = new NoiseLayerKeys(f.Noise);
        Assert.True((key & Keys.Alt) != 0 ? keys.HandleShiftAlt(key) : keys.Handle(key));
        Assert.Equal(new[] { id + "=on" }, f.Radio.Writes);
        Assert.Equal(id, f.Noise.Current);
    }

    [Theory]
    [InlineData(Keys.R | Keys.Control, "rnnoise")]
    [InlineData(Keys.D | Keys.Control, "deepfilternet3")]
    [InlineData(Keys.N | Keys.Control, "nnr")]
    [InlineData(Keys.D2 | Keys.Control, "nr2")]
    [InlineData(Keys.D4 | Keys.Control, "nr4")]
    [InlineData(Keys.S | Keys.Control, "spectral_capture")]
    public void Each_computer_chord_turns_its_engine_on(Keys key, string id)
    {
        using var f = new NoiseFixture();
        Assert.True(new NoiseLayerKeys(f.Noise).Handle(key));
        Assert.True(f.Noise.IsOn(id));
        Assert.Empty(f.Radio.Writes);
    }

    [Fact]
    public void The_layer_says_what_the_menu_says_because_both_are_the_controller()
    {
        using var f = new NoiseFixture();
        new NoiseLayerKeys(f.Noise).Handle(Keys.D4 | Keys.Control);
        string fromLayer = f.Spoken.Last;
        f.Noise.Toggle("nr4", "test");
        f.Noise.Toggle("nr4", "menu");
        Assert.Equal(fromLayer, f.Spoken.Last);
    }

    [Fact]
    public void Up_Down_Tab_Home_and_End_work_the_levers_of_the_engine_last_touched()
    {
        using var f = new NoiseFixture();
        var keys = new NoiseLayerKeys(f.Noise);
        keys.Handle(Keys.Up);
        Assert.StartsWith("Choose an engine first", f.Spoken.Last);
        keys.Handle(Keys.N | Keys.Control);          // NNR: Strength, Model, Depth
        keys.Handle(Keys.Down);                      // Strength 95
        Assert.Equal(0.95f, f.Pipeline.NeuralSlot.GetStrength("nnr"), 3);
        keys.Handle(Keys.Tab);
        Assert.Equal("Model, Standard voice.", f.Spoken.Last);
        keys.Handle(Keys.Tab);
        Assert.Equal("Depth, 25 decibels.", f.Spoken.Last);
        keys.Handle(Keys.Up);
        Assert.Equal(-26, f.Pipeline.NeuralSlot.GetSetting("nnr", "mask_floor_db").Number);
        keys.Handle(Keys.End);
        Assert.Equal(-50, f.Pipeline.NeuralSlot.GetSetting("nnr", "mask_floor_db").Number);
        keys.Handle(Keys.Home);
        Assert.Equal(-10, f.Pipeline.NeuralSlot.GetSetting("nnr", "mask_floor_db").Number);
        keys.Handle(Keys.Tab | Keys.Shift);
        Assert.Equal("Model, Standard voice.", f.Spoken.Last);
    }

    [Fact]
    public void Zero_Space_brackets_favourite_and_defaults_reach_the_controller()
    {
        using var f = new NoiseFixture();
        var keys = new NoiseLayerKeys(f.Noise);
        keys.Handle(Keys.D4 | Keys.Control);
        keys.Handle(Keys.D0);
        Assert.False(f.Pipeline.SpectralSlot.Enabled);
        keys.Handle(Keys.D0);
        Assert.True(f.Noise.IsOn("nr4"));
        keys.Handle(Keys.Space);
        Assert.StartsWith("On this computer: NR4", f.Spoken.Last);
        keys.HandleShiftAlt(NoiseLayerKeys.FavouriteKey);
        Assert.Equal("NR4 added to favourites.", f.Spoken.Last);
        keys.HandleShiftAlt(NoiseLayerKeys.DefaultsKey);
        Assert.StartsWith("NR4 back to its defaults", f.Spoken.Last);
        f.Noise.BeginSession();
        keys.Handle(Keys.OemCloseBrackets);
        Assert.True(f.Noise.IsOn("nr4"));
    }
}
