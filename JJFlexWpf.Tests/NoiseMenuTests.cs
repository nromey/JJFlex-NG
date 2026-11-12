using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E8: what the Audio menu's noise rows say, from the
/// controller every surface shares. Windowless: the menu's own layout and
/// builder are pinned as source by DspOnAudioMenuTests in Radios.Tests.
/// </summary>
public sealed class NoiseMenuTests
{
    public NoiseMenuTests() => NoiseReductionPerChannelTests.AssertWindowless();

    [Fact]
    public void The_two_subjects_list_the_ruled_engines_in_order_radio_first()
    {
        using var f = new NoiseFixture();
        Assert.Equal(new[] { "Standard NR", "Legacy NR", "Neural NR", "Spectral NR", "NR Filter",
                             "NR2", "NR4", "Captured noise", "RNNoise", "DeepFilterNet3", "NNR", "NVIDIA NR" },
            f.Noise.Engines(DenoiseGroup.NoiseReduction).Select(e => f.Noise.NameOf(e.Id)));
        Assert.Equal(new[] { "Noise Blanker", "Wideband NB", "Standard Auto-Notch", "Legacy Auto-Notch", "FFT Auto-Notch", "Audio Peak Filter" },
            f.Noise.Engines(DenoiseGroup.BlankersAndNotches).Select(e => f.Noise.NameOf(e.Id)));
    }

    /// <summary>
    /// #838's behavioural label test (the Codex check of 2026-10-29): a gated
    /// row's label with the licence enabled, disabled and unreported, and the
    /// hardware rung, read from the same answer the row's action asks.
    /// </summary>
    [Theory]
    [InlineData("radio_nrs", RadioCapability.NoiseReductionSpectral)]
    [InlineData("radio_nrf", RadioCapability.NoiseReductionFilter)]
    [InlineData("radio_rnn", RadioCapability.NoiseReductionNeural)]
    [InlineData("radio_anft", RadioCapability.AutoNotchFft)]
    public void A_gated_rows_label_follows_the_licence_and_the_hardware(string id, RadioCapability capability)
    {
        using var f = new NoiseFixture();
        string name = f.Noise.NameOf(id);

        // Licence enabled (and, the same to the label, unreported: not knowing is not a no).
        Assert.Equal(name, f.Noise.MarkedName(id));

        // Licence disabled.
        f.Radio.Refused[capability] = "Your radio reports that this is not in its subscription";
        Assert.Equal(name + ", needs subscription", f.Noise.MarkedName(id));

        // The hardware rung wins: the model cannot do it.
        f.Radio.Refused[capability] = "This radio model does not have the DSP hardware";
        Assert.Equal(name + ", not on this radio", f.Noise.MarkedName(id));

        // And the row stays live: choosing it speaks the reason rather than doing nothing.
        f.Spoken.Clear();
        f.Noise.Toggle(id, "audio menu");
        Assert.StartsWith(name + ":", f.Spoken.Last);
        Assert.Empty(f.Radio.Writes);
    }

    [Fact]
    public void An_ungated_radio_row_never_carries_a_marker()
    {
        using var f = new NoiseFixture();
        foreach (string capabilityFree in new[] { "radio_nr", "radio_nrl", "radio_nb", "radio_wnb", "radio_anf", "radio_anfl", "radio_apf" })
            Assert.Equal(f.Noise.NameOf(capabilityFree), f.Noise.MarkedName(capabilityFree));
    }

    [Fact]
    public void A_computer_engine_that_cannot_run_is_marked_and_stays_listed()
    {
        using var f = new NoiseFixture();
        f.Unavailable["nnr"] = Unavailability.HelperMissing;
        f.Unavailable["deepfilternet3"] = Unavailability.ModelMissing;
        Assert.Equal("NNR, helper missing", f.Noise.MarkedName("nnr"));
        Assert.Equal("DeepFilterNet3, model missing", f.Noise.MarkedName("deepfilternet3"));
        Assert.Equal("NVIDIA NR, not in this version", f.Noise.MarkedName("nvidia_afx"));
        Assert.Contains(f.Noise.Engines(DenoiseGroup.NoiseReduction), e => e.Id == "nnr");
    }

    /// <summary>
    /// S10, ruled by Noel 2026-11-11 20:29 ("name first", confirmed "yep"):
    /// every noise toggle on the Audio menu reads name, then state, then the
    /// shortcut. The 2026-11-08 walk heard "On, Ctrl+J, N, N: Off". The
    /// assembled row is what a screen reader reads: the text before the tab,
    /// then the shortcut column.
    /// </summary>
    [Theory]
    [InlineData("Legacy NR\tCtrl+J, N, N", false, "Legacy NR, Off, Ctrl+J, N, N")]
    [InlineData("Legacy NR\tCtrl+J, N, N", true, "Legacy NR, On, Ctrl+J, N, N")]
    [InlineData("Neural NR, needs subscription\tCtrl+J, N, R", false, "Neural NR, needs subscription, Off, Ctrl+J, N, R")]
    [InlineData("Favourite", false, "Favourite, Off")]
    [InlineData("Neural NR favourite", true, "Neural NR favourite, On")]
    public void A_noise_toggle_reads_name_then_state_then_shortcut(string text, bool on, string heard)
    {
        string row = NativeMenuBar.NoiseToggleRowText(text, on);
        // The state sits before the tab, never inside the shortcut column.
        int tab = row.IndexOf('\t');
        if (tab >= 0) Assert.DoesNotContain(on ? "On" : "Off", row.Substring(tab));
        Assert.Equal(heard, row.Replace("\t", ", "));
    }

    [Fact]
    public void Every_noise_toggle_on_the_Audio_menu_is_built_name_first_and_the_popup_writes_it_that_way()
    {
        // A source pin, because the menu builder and its popup handler need a window.
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        string build = Body(menu, "private void BuildNoiseItems(IntPtr parent, DenoiseGroup group)", "private const string MenuSource");
        Assert.DoesNotContain("AddChecked(", build);
        Assert.Equal(4, Count(build, "AddNoiseToggle("));
        // The engine's own switch is named for the engine, not "On".
        Assert.Contains("AddNoiseToggle(sub, isRadio ? R(name + chord) : name + chord,", build);
        Assert.DoesNotContain("audio.noise_menu.on\"", build);
        // The popup handler gives these rows their own text before the shared ": On" suffix.
        string popup = Body(menu, "public void HandleInitMenuPopup(IntPtr wParam)", "public void UpdateFeatureGates");
        int ours = popup.IndexOf("_noiseToggleItems.Contains(id)", StringComparison.Ordinal);
        int shared = popup.IndexOf("$\"{label}: {stateText}\"", StringComparison.Ordinal);
        Assert.True(ours > 0 && shared > ours, "the noise rows must be handled before the shared suffix is written");
        Assert.Contains("NoiseToggleRowText(baseText, isOn)", popup);
    }

    private static string Source(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
    }

    private static string Body(string source, string from, string to)
    {
        int at = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + from);
        int end = source.IndexOf(to, at, StringComparison.Ordinal);
        Assert.True(end > at, "not found after it: " + to);
        return source.Substring(at, end - at);
    }

    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Return_to_defaults_is_offered_on_every_engine_but_the_switch_only_two()
    {
        using var f = new NoiseFixture();
        var without = f.Noise.Engines().Where(e => !f.Noise.OffersDefaults(e.Id)).Select(e => e.Id).ToArray();
        Assert.Equal(new[] { "radio_rnn", "radio_anft" }, without);
    }
}
