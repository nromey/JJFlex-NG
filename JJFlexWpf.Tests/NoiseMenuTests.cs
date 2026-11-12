using System;
using System.IO;
using System.Linq;
using System.Threading;
using JJFlexWpf.Controls;
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
        Assert.Contains("RewriteNoiseToggle(itemPopup, id, baseText, isOn);", popup);
    }

    /// <summary>
    /// Track E follow-up E3 (S11): after the Audio menu's "Advanced
    /// settings..." focus stayed on the frequency display (the Track S walk).
    /// Where focus is meant to land: the first Advanced lever when Advanced was
    /// asked for and the engine has one, else the Engine field. Controls built
    /// unhosted on a private STA thread; no window.
    /// </summary>
    [Fact]
    public void Advanced_settings_lands_on_the_first_Advanced_lever_and_otherwise_on_the_Engine_field()
    {
        RunOnSta(() =>
        {
            var engine = new System.Windows.Controls.Button();
            var levers = new System.Windows.Controls.StackPanel();
            Assert.Same(engine, ScreenFieldsPanel.NoiseOpenTarget(advanced: true, levers, engine));
            var first = new System.Windows.Controls.Button();
            levers.Children.Add(first);
            levers.Children.Add(new System.Windows.Controls.Button());
            Assert.Same(first, ScreenFieldsPanel.NoiseOpenTarget(advanced: true, levers, engine));
            Assert.Same(engine, ScreenFieldsPanel.NoiseOpenTarget(advanced: false, levers, engine));
        });
    }

    /// <summary>
    /// E3, the part a windowless test can reach: the focus is asked for after
    /// layout, not at Normal priority before the new fields exist to WPF, on
    /// every branch of OpenNoise, and the panel is shown first. Whether focus
    /// really lands is the orchestrator's live check with jjprobe.
    /// </summary>
    [Fact]
    public void Opening_noise_on_Home_asks_for_focus_only_after_layout_on_every_branch()
    {
        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.Noise.cs");
        string open = Body(panel, "public void OpenNoise(string? engineId, bool advanced)", "internal static UIElement NoiseOpenTarget(");
        Assert.Contains("if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;", open);
        Assert.Equal(3, Count(open, "FocusNoiseWhenLaidOut("));
        Assert.DoesNotContain(".Focus()", open);
        Assert.DoesNotContain("Dispatcher.BeginInvoke(", open);
        // Every branch goes through the one owned request (repair round 1), wired
        // to layout, WPF's keyboard focus, the engine shown and the panel's state.
        string focus = Body(panel, "private void FocusNoiseWhenLaidOut(", "private readonly List<(CycleFieldControl Field, NoiseLever Lever");
        Assert.Contains("NoiseFocus.Request(", focus);
        Assert.Contains("DispatcherPriority.Loaded, () => { UpdateLayout(); act(); }", focus);
        Assert.Contains("DispatcherPriority.ApplicationIdle", focus);
        Assert.Contains("focused: () => System.Windows.Input.Keyboard.FocusedElement", focus);
        Assert.Contains("shownEngine: () => _noiseFieldsFor", focus);
        Assert.Contains("IsVisibleChanged += (s, e) => { if (e.NewValue is false) _noiseFocus.Cancel(); };", focus);
        Assert.Contains("Unloaded += (s, e) => _noiseFocus.Cancel();", focus);
        Assert.Contains("_noiseFocus?.Cancel();", Body(panel, "private void OnDspExpanderCollapsed()", "\n    }"));
    }

    // ── Repair round 1: the deferred focus request is owned and cancellable ──

    private sealed class FocusRig
    {
        public readonly System.Collections.Generic.Queue<Action> Layout = new();
        public readonly System.Collections.Generic.Queue<Action> Idle = new();
        public readonly System.Collections.Generic.List<string> Trace = new();
        public object? Focused = "frequency display";
        public string? Engine = "nnr";
        public bool Ready = true;
        public int Calls;
        public bool Lands = true;
        public readonly DeferredNoiseFocus Focus;
        public FocusRig() => Focus = new DeferredNoiseFocus(Layout.Enqueue, Idle.Enqueue, () => Focused, () => Engine, () => Ready, Trace.Add);
        public Func<Func<bool>?> Target(string name) => () => () => { Calls++; if (Lands) Focused = name; return Lands; };
        public void RunLayout() { while (Layout.Count > 0) Layout.Dequeue()(); }
        public void RunIdle() { while (Idle.Count > 0) Idle.Dequeue()(); }
    }

    [Fact]
    public void A_deferred_noise_focus_lands_after_layout_and_retries_once_at_idle()
    {
        var rig = new FocusRig();
        rig.Focus.Request(rig.Target("floor"), "floor");
        Assert.Equal(0, rig.Calls);                       // nothing before layout
        rig.RunLayout();
        Assert.Equal(1, rig.Calls);
        Assert.Equal("floor", rig.Focused);
        Assert.Empty(rig.Idle);

        // Not laid out yet on the first try: one more at idle, then it gives up and says so.
        rig = new FocusRig { Lands = false };
        rig.Focus.Request(rig.Target("floor"), "floor");
        rig.RunLayout();
        rig.RunIdle();
        Assert.Equal(2, rig.Calls);
        Assert.Contains(rig.Trace, t => t.Contains("did not land"));
    }

    [Fact]
    public void A_deferred_noise_focus_never_lands_after_the_operator_moves_focus_between_tries()
    {
        var rig = new FocusRig { Lands = false };
        rig.Focus.Request(rig.Target("floor"), "floor");
        rig.RunLayout();                                  // first try fails
        rig.Lands = true;
        rig.Focused = "mode field";                       // the operator moved on before idle
        rig.RunIdle();
        Assert.Equal(1, rig.Calls);
        Assert.Equal("mode field", rig.Focused);
        Assert.Contains(rig.Trace, t => t.Contains("cancelled"));

        // And before the first try too: the app lost focus to another window.
        rig = new FocusRig();
        rig.Focus.Request(rig.Target("floor"), "floor");
        rig.Focused = null;
        rig.RunLayout();
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public void A_deferred_noise_focus_is_dropped_by_a_later_request_an_engine_change_or_the_panel_going_away()
    {
        // A later request: the older one never calls focus, even though it was queued first.
        var rig = new FocusRig();
        int older = 0;
        rig.Focus.Request(() => () => { older++; return true; }, "older");
        rig.Focus.Request(rig.Target("newer"), "newer");
        rig.RunLayout();
        Assert.Equal(0, older);
        Assert.Equal(1, rig.Calls);

        // The fields now show another engine.
        rig = new FocusRig { Lands = false };
        rig.Focus.Request(rig.Target("floor"), "floor");
        rig.RunLayout();
        rig.Engine = "rnnoise";
        rig.Lands = true;
        rig.RunIdle();
        Assert.Equal(1, rig.Calls);

        // Hidden, then shown again before idle: the cancel holds.
        rig = new FocusRig { Lands = false };
        rig.Focus.Request(rig.Target("floor"), "floor");
        rig.RunLayout();
        rig.Focus.Cancel();
        rig.Lands = true;
        rig.RunIdle();
        Assert.Equal(1, rig.Calls);

        // Not showing at all.
        rig = new FocusRig { Ready = false };
        rig.Focus.Request(rig.Target("floor"), "floor");
        rig.RunLayout();
        Assert.Equal(0, rig.Calls);
    }

    // ── Repair round 1: a noise row keeps its native check mark ──

    [Theory]
    [InlineData(true, "Legacy NR, On\tCtrl+J, N, N")]
    [InlineData(false, "Legacy NR, Off\tCtrl+J, N, N")]
    public void A_noise_row_rewritten_on_opening_keeps_its_native_check_state(bool on, string text)
    {
        // Codex's probe, kept: an unshown native popup and a menu bar holding only
        // its check-item collections. No window, app, sound or radio.
        IntPtr popup = CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, popup);
        try
        {
            const int id = 42;
            const string label = "Legacy NR\tCtrl+J, N, N";
            Assert.True(AppendMenuW(popup, 0, (UIntPtr)id, label));
            var menu = (NativeMenuBar)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NativeMenuBar));
            void Put(string name, object value) => typeof(NativeMenuBar).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(menu, value);
            Put("_checkItems", new System.Collections.Generic.List<(IntPtr, int, Func<bool>, string, bool)> { (popup, id, () => on, label, true) });
            Put("_radioGroupItems", new System.Collections.Generic.HashSet<int>());
            Put("_noiseToggleItems", new System.Collections.Generic.HashSet<int> { id });
            Put("_itemNames", new System.Collections.Generic.Dictionary<int, string>());
            menu.HandleInitMenuPopup(popup);
            var buffer = new System.Text.StringBuilder(256);
            Assert.True(GetMenuStringW(popup, (uint)id, buffer, buffer.Capacity, 0) > 0);
            Assert.Equal(text, buffer.ToString());
            uint state = GetMenuState(popup, (uint)id, 0);
            Assert.True(((state & 8) != 0) == on, "MF_CHECKED does not match the state; native flags = 0x" + state.ToString("X"));

            // A second opening after the state changed: still in step.
            Put("_checkItems", new System.Collections.Generic.List<(IntPtr, int, Func<bool>, string, bool)> { (popup, id, () => !on, label, true) });
            menu.HandleInitMenuPopup(popup);
            Assert.True(((GetMenuState(popup, (uint)id, 0) & 8) != 0) == !on);
        }
        finally { DestroyMenu(popup); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint id, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr menu, uint id, System.Text.StringBuilder text, int count, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
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
