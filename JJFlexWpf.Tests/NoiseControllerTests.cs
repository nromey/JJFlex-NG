using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// #1018 item 2 (Track E follow-up C): the two refusals that still put the
    /// pressed engine in its slot (it cannot run here; PC audio is off) used to
    /// say only their own sentence, so the engine pushed out went silently.
    /// Each now names it, at every verbosity; and the journal records the swap
    /// as one entry carrying both engines, not a bare "off -> on".
    /// </summary>
    [Theory]
    [InlineData(VerbosityLevel.Chatty)]
    [InlineData(VerbosityLevel.Terse)]
    [InlineData(VerbosityLevel.Critical)]
    public void A_refused_start_that_still_swaps_engines_names_the_one_it_replaced_and_journals_both(VerbosityLevel verbosity)
    {
        using var f = new NoiseFixture();
        f.Spoken.Verbosity = verbosity;
        var entries = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { if (e.Source == "swap-test") lock (entries) entries.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try
        {
            // The helper is missing: NNR is kept as the choice, and RNNoise has stopped.
            f.Noise.Toggle("rnnoise", "swap-test");
            f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Noise.Toggle("nnr", "swap-test");
            Assert.Equal("nnr", f.Pipeline.NeuralSlot.ChosenEngineId);
            Assert.Contains(Lexicon.Get("audio.noise_surface.replaced_clause", ("other", "RNNoise")), f.Spoken.Last);
            Assert.Equal(SpeechSubject.NoiseUnavailable, f.Spoken.Said[^1].Subject);

            // PC audio is off: DeepFilterNet3 is kept as the choice, and NNR (the slot's last) has gone.
            f.Radio.PcAudioOn = false;
            f.Noise.Toggle("deepfilternet3", "swap-test");
            Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ChosenEngineId);
            Assert.Contains(Lexicon.Get("audio.noise_surface.replaced_clause", ("other", "NNR")), f.Spoken.Last);
        }
        finally { NoiseSettingsJournal.Recorded -= Listen; }
        lock (entries)
        {
            var swaps = entries.Where(e => e.Kind == NoiseJournalKind.Switch && e.Changes.Count == 2).ToList();
            Assert.Equal(2, swaps.Count);
            Assert.Equal(new NoiseJournalChange("nnr", "", "off", "on"), swaps[0].Changes[0]);
            Assert.Equal(new NoiseJournalChange("rnnoise", "", "on", "off"), swaps[0].Changes[1]);
            Assert.Equal(new NoiseJournalChange("deepfilternet3", "", "off", "on"), swaps[1].Changes[0]);
            Assert.Equal(new NoiseJournalChange("nnr", "", "on", "off"), swaps[1].Changes[1]);
        }
    }

    /// <summary>
    /// Repair round 1, Codex's third finding (its probe, kept): entering CW for
    /// the first time with RNNoise on puts NNR in the neural slot through the
    /// starting point, not through TurnComputerOn. With NNR's helper missing
    /// the refusal named only NNR, and the StartingPoint entry held only
    /// "nnr off -> on". It now names RNNoise and journals it "on -> off".
    /// </summary>
    [Fact]
    public void A_first_CW_refusal_names_and_journals_the_engine_it_replaced()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "cw-swap-test");
        f.Settle();
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.RunningEngineId);
        f.Spoken.Clear();
        f.Unavailable["nnr"] = Unavailability.HelperMissing;
        var entries = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { if (e.Kind == NoiseJournalKind.StartingPoint && e.Label == "cw") lock (entries) entries.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try
        {
            f.Radio.Mode = "CW";
            f.Noise.OnModeChanged("CW");
        }
        finally { NoiseSettingsJournal.Recorded -= Listen; }
        Assert.Equal("nnr", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Contains(Lexicon.Get("audio.noise_surface.replaced_clause", ("other", "RNNoise")), f.Spoken.Last);
        // Part of the mode change's one utterance, queued behind the mode word (#1117).
        Assert.Equal(SpeechSubject.NoiseModeStart, f.Spoken.Said[^1].Subject);
        Assert.Contains(f.Spoken.Said.Count - 1, f.Spoken.SaidAfter);
        lock (entries)
        {
            // The journal is one static stream shared by every test class, so look
            // for this entry rather than assume it is the only one.
            var expected = new[] { new NoiseJournalChange("nnr", "", "off", "on"), new NoiseJournalChange("rnnoise", "", "on", "off") };
            Assert.Contains(entries, e => e.Changes.SequenceEqual(expected));
        }
    }

    /// <summary>
    /// The same starting point when NNR can run: its sentence names RNNoise too,
    /// and coming back to voice (a remembered group) puts RNNoise back saying
    /// both "NNR off" and "RNNoise on", journalling both (repair round 1: every
    /// swap outside TurnComputerOn, found by searching for the slot's Select).
    /// </summary>
    [Fact]
    public void A_starting_point_and_a_mode_restore_that_swap_engines_name_and_journal_both()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "swap-modes");
        var entries = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { if (e.Kind is NoiseJournalKind.StartingPoint or NoiseJournalKind.ModeRestore) lock (entries) entries.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try
        {
            f.Radio.Mode = "CW";
            f.Noise.OnModeChanged("CW");
            Assert.Equal("nnr", f.Pipeline.NeuralSlot.ChosenEngineId);
            Assert.Contains(Lexicon.Get("audio.noise_surface.replaced_clause", ("other", "RNNoise")),
                f.Spoken.Said.Last(s => s.Subject == SpeechSubject.NoiseModeStart).Text);

            f.Spoken.Clear();
            f.Radio.Mode = "USB";
            f.Noise.OnModeChanged("USB");
            Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.ChosenEngineId);
            string restore = f.Spoken.Said.Last(s => s.Subject == SpeechSubject.NoiseModeStart).Text;
            Assert.Contains(Lexicon.Get("audio.noise_surface.phrase_off", ("engine", "NNR")), restore);
            Assert.Contains(Lexicon.Get("audio.noise_surface.phrase_on", ("engine", "RNNoise")), restore);
        }
        finally { NoiseSettingsJournal.Recorded -= Listen; }
        lock (entries)
        {
            Assert.Contains(entries, e => e.Kind == NoiseJournalKind.StartingPoint
                && e.Changes.Contains(new NoiseJournalChange("rnnoise", "", "on", "off")) && e.Changes.Contains(new NoiseJournalChange("nnr", "", "off", "on")));
            Assert.Contains(entries, e => e.Kind == NoiseJournalKind.ModeRestore
                && e.Changes.Contains(new NoiseJournalChange("nnr", "", "on", "off")) && e.Changes.Contains(new NoiseJournalChange("rnnoise", "", "off", "on")));
        }
    }

    /// <summary>
    /// Repair round 1, the harness check on the 8600: with the helper missing,
    /// the refusal that names the replaced engine was cut at its first word,
    /// 27 ms in, by the slot's own "could not start" sentence, which
    /// interrupted. A result nobody asked for now waits its turn
    /// (<see cref="INoiseSpeaker.SayAfter"/>), on every path that refuses and
    /// still swaps: the direct switch and the first-CW starting point.
    /// </summary>
    [Fact]
    public void A_failed_start_after_a_refusal_is_said_after_it_and_never_over_it()
    {
        foreach (bool viaCw in new[] { false, true })
        {
            using var f = new NoiseFixture();
            f.Noise.Attach("USB");
            f.Noise.Toggle("rnnoise", "test");
            f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Spoken.Clear();
            if (viaCw) { f.Radio.Mode = "CW"; f.Noise.OnModeChanged("CW"); }
            else f.Noise.Toggle("nnr", "test");
            int refusal = f.Spoken.Said.FindLastIndex(s => s.Text.Contains(Lexicon.Get("audio.noise_surface.replaced_clause", ("other", "RNNoise"))));
            Assert.True(refusal >= 0, "no refusal naming RNNoise (" + (viaCw ? "first CW" : "direct") + ")");
            // The answer to a press still speaks at once; a mode change's answer
            // waits behind the mode word the mode path has just said (#1117).
            if (viaCw) Assert.Contains(refusal, f.Spoken.SaidAfter);
            else Assert.DoesNotContain(refusal, f.Spoken.SaidAfter);

            f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", false, "helper missing") { SlotStopped = true });
            Assert.Equal(L("audio.noise_surface.start_failed", VerbosityLevel.Chatty), f.Spoken.Last);
            Assert.Contains(f.Spoken.Said.Count - 1, f.Spoken.SaidAfter);
        }

        // The helper's own unasked sentences wait their turn too.
        using var g = new NoiseFixture();
        g.Noise.OnHelperStateChanged(WdspHelperStateChange.CapReached, Array.Empty<string>());
        Assert.Contains(g.Spoken.Said.Count - 1, g.Spoken.SaidAfter);
    }

    [Fact]
    public void The_screen_readers_noise_speaker_queues_what_it_says_after()
    {
        // The queue itself is the arbiter's (SpeechIntent.Queue: never cuts what is speaking).
        // A source pin, because ScreenReaderOutput is the live speech path.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "NoiseController.cs")).Replace("\r\n", "\n");
        Assert.Contains("public void SayAfter(string text, string subject, VerbosityLevel level) =>\n            ScreenReaderOutput.Speak(text, SpeechIntent.Queue, level, subject: subject);", source);
    }

    private static string L(string key, VerbosityLevel level) => Lexicon.Get(key, level);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>
    /// Repair round 2, the live harness's R2-4 shape: a quick visit to CW queues
    /// NNR's failure, and coming back to voice restores RNNoise. The queued
    /// failure was about the old picture; it is retired when the group changes,
    /// before the restore speaks, so it cannot be rescued behind "RNNoise on".
    /// The same for a replacement, a selection that recovers, and the helper's
    /// "back" sentence.
    /// </summary>
    [Fact]
    public void A_queued_noise_notice_is_retired_once_the_slots_picture_it_described_changes()
    {
        // A mode group change.
        using (var f = new NoiseFixture())
        {
            f.Noise.Attach("USB");
            f.Noise.Toggle("rnnoise", "test");
            f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Radio.Mode = "CW"; f.Noise.OnModeChanged("CW");
            f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", false, "helper missing") { SlotStopped = true });
            Assert.Empty(f.Spoken.Retired);
            int before = f.Spoken.Said.Count;
            f.Radio.Mode = "USB"; f.Noise.OnModeChanged("USB");
            Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.ChosenEngineId);
            Assert.Contains(f.Spoken.Retired, r => r.Subject == SpeechSubject.NoiseHelperState);
            Assert.True(f.Spoken.Said.Count > before);     // the restore still speaks
        }

        // A replacement, and a selection that now processes.
        foreach (bool byResult in new[] { false, true })
        {
            using var f = new NoiseFixture();
            f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Noise.Toggle("nnr", "test");
            f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", false, "helper missing") { SlotStopped = true });
            if (byResult) f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", true));
            else f.Noise.Toggle("rnnoise", "test");
            Assert.Single(f.Spoken.Retired, r => r.Subject == SpeechSubject.NoiseHelperState);
        }

        // The helper's "back" sentence is owned the same way.
        using (var f = new NoiseFixture())
        {
            f.Noise.OnHelperStateChanged(WdspHelperStateChange.BackAfterCap, new[] { "nnr" });
            Assert.Contains(f.Spoken.Said.Count - 1, f.Spoken.SaidAfter);
            f.Noise.Toggle("nr4", "test");
            Assert.Single(f.Spoken.Retired, r => r.Subject == SpeechSubject.NoiseHelperState);
        }

        // Nothing queued: nothing is retired, so no other speech is ever touched.
        using (var f = new NoiseFixture())
        {
            f.Noise.Toggle("rnnoise", "test");
            f.Noise.Toggle("nr4", "test");
            f.Radio.Mode = "CW"; f.Noise.OnModeChanged("CW");
            Assert.Empty(f.Spoken.Retired);
        }
    }

    // ── Repair round 2: Codex's real-arbiter probe, kept ──

    /// <summary>
    /// The real SpeechArbiter, isolated behind an inert sink: no screen reader,
    /// no process-wide speech state, no window, sound or radio. Reflection
    /// reaches the arbiter so production needs no InternalsVisibleTo for a test.
    /// </summary>
    private sealed class SilentArbiterSpeaker : INoiseSpeaker
    {
        private readonly object _arbiter;
        private readonly Type _type;
        private long _ticket;
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, bool Interrupt, bool Salvaged)> Calls = new();
        public VerbosityLevel Verbosity => VerbosityLevel.Chatty;
        public readonly System.Threading.ManualResetEventSlim Replayed = new(false);

        public SilentArbiterSpeaker()
        {
            var assembly = typeof(ScreenReaderOutput).Assembly;
            _type = assembly.GetType("Radios.Speech.SpeechArbiter")!;
            var ctor = _type.GetConstructors().Single();
            var sinkType = ctor.GetParameters()[2].ParameterType;
            var invoke = sinkType.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var ticket = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(this),
                typeof(SilentArbiterSpeaker).GetMethod(nameof(Sink))!, parameters[0], parameters[1], parameters[5]);
            var handoff = System.Linq.Expressions.Expression.Call(invoke.ReturnType.GetMethod("TrackedAs",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!, ticket);
            var sink = System.Linq.Expressions.Expression.Lambda(sinkType, handoff, parameters).Compile();
            var clock = Activator.CreateInstance(assembly.GetType("Radios.Speech.SystemSpeechClock")!);
            _arbiter = ctor.Invoke(new object?[] { clock, (Func<VerbosityLevel>)(() => Verbosity), sink,
                (Action)(() => { }), (Action<string, VerbosityLevel, SpeechIntent?, string?>)((_, _, _, _) => { }), null, null, null });
        }

        public long Sink(string text, bool interrupt, bool salvaged)
        {
            Calls.Enqueue((text, interrupt, salvaged));
            if (salvaged) Replayed.Set();
            return System.Threading.Interlocked.Increment(ref _ticket);
        }

        public void Say(string text, string subject, VerbosityLevel level) => Emit(text, subject, level, true);
        /// <summary>The mode word as the mode path says it (MainWindow.SetMode): an interrupt with no subject.</summary>
        public void ModeWord(string text) =>
            _type.GetMethod("Emit")!.Invoke(_arbiter, new object?[] { text, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "mode test", null, false });
        public void SayAfter(string text, string subject, VerbosityLevel level) => Emit(text, subject, level, false);
        public void Retire(string subject, string why) => _type.GetMethod("Supersede")!.Invoke(_arbiter, new object?[] { subject, why, "noise test" });
        private void Emit(string text, string subject, VerbosityLevel level, bool interrupt) =>
            _type.GetMethod("Emit")!.Invoke(_arbiter, new object?[] { text, interrupt,
                interrupt ? SpeechIntent.Latest : SpeechIntent.Queue, level, "noise test", subject, false });
        public void Dispose() => _type.GetMethod("DiscardAll")!.Invoke(_arbiter, null);
    }

    [Fact]
    public void A_queued_failure_is_not_rescued_after_a_successful_engine_change_through_the_real_arbiter()
    {
        using var f = new NoiseFixture();
        var speaker = new SilentArbiterSpeaker();
        typeof(NoiseController).GetField("_speaker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(f.Noise, speaker);
        try
        {
            f.Noise.Attach("USB");
            f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Noise.Toggle("nnr", "test");
            f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural,
                "nnr", false, "helper missing") { SlotStopped = true });
            string failure = Lexicon.Get("audio.noise_surface.start_failed", VerbosityLevel.Chatty);
            Assert.Contains(speaker.Calls, c => c.Text == failure && !c.Interrupt && !c.Salvaged);
            f.Noise.Toggle("rnnoise", "test");
            f.Settle();
            Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.RunningEngineId);
            f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "rnnoise", true));
            speaker.Replayed.Wait(2500);
            Assert.DoesNotContain(speaker.Calls, c => c.Text == failure && c.Salvaged);
        }
        finally { speaker.Dispose(); }
    }

    /// <summary>The instrument's positive control (Codex's): without a retirement, the real arbiter DOES rescue a queued sentence after an interrupt on another subject.</summary>
    [Fact]
    public void The_real_arbiter_rescues_an_unretired_queued_notice_and_drops_a_retired_one()
    {
        var kept = new SilentArbiterSpeaker();
        try
        {
            kept.SayAfter("old helper state", SpeechSubject.NoiseHelperState, VerbosityLevel.Terse);
            kept.Say("engine running", SpeechSubject.NoiseEngineSwitch, VerbosityLevel.Terse);
            Assert.True(kept.Replayed.Wait(2500), "the arbiter never rescued the unretired notice, so this instrument cannot see the defect");
        }
        finally { kept.Dispose(); }

        var retired = new SilentArbiterSpeaker();
        try
        {
            retired.SayAfter("old helper state", SpeechSubject.NoiseHelperState, VerbosityLevel.Terse);
            retired.Retire(SpeechSubject.NoiseHelperState, "positive control");
            retired.Say("engine running", SpeechSubject.NoiseEngineSwitch, VerbosityLevel.Terse);
            retired.Replayed.Wait(900);
            Assert.DoesNotContain(retired.Calls, c => c.Salvaged);
        }
        finally { retired.Dispose(); }
    }

    [Fact]
    public void An_ordinary_swap_journals_both_engines_and_a_start_with_nothing_replaced_journals_one()
    {
        using var f = new NoiseFixture();
        var entries = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { if (e.Source == "swap-test-2") lock (entries) entries.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try
        {
            f.Noise.Toggle("nr4", "swap-test-2");
            f.Noise.Toggle("spectral_capture", "swap-test-2");
        }
        finally { NoiseSettingsJournal.Recorded -= Listen; }
        lock (entries)
        {
            Assert.Equal(2, entries.Count);
            Assert.Equal(new[] { new NoiseJournalChange("nr4", "", "off", "on") }, entries[0].Changes);
            Assert.Equal(new[] { new NoiseJournalChange("spectral_capture", "", "off", "on"), new NoiseJournalChange("nr4", "", "on", "off") },
                entries[1].Changes);
        }
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
        // #1019 (S2): NR4 leads with its own reduction and keeps the noise floor
        // first among its Advanced levers; it led with Strength before the flip.
        Assert.Equal(new[] { "reduction_db" }, f.Noise.Levers("nr4", false).Select(l => l.Key));
        var all = f.Noise.Levers("nr4", true).Select(l => l.Key).ToList();
        Assert.Equal("reduction_db", all[0]);
        Assert.Equal("floor", all[1]);
        Assert.Contains("noise_estimator", all);
        Assert.Equal("listen_removed", all[^1]);
        Assert.Equal(new[] { "level" }, f.Noise.Levers("radio_nrl", true).Select(l => l.Key));
        Assert.Empty(f.Noise.Levers("radio_rnn", true));
        // #1019: NNR's first lever is its Depth, so the layer never starts on its model.
        Assert.Equal(new[] { "mask_floor_db", "model" }, f.Noise.Levers("nnr", false).Select(l => l.Key));
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
    public void The_noise_floor_steps_by_five_percent()
    {
        // #1019: Strength 80 became a noise floor whose default is 10 (stored
        // 0.9); Up raises it to 15, which stores 0.85.
        using var f = new NoiseFixture();
        var floor = f.Noise.Levers("rnnoise", false)[0];
        Assert.Equal(10, f.Noise.NumberOf(floor));
        f.Noise.Adjust(floor, +1, "test");
        Assert.Equal(0.85f, f.Pipeline.NeuralSlot.GetStrength("rnnoise"), 3);
    }

    [Fact]
    public void A_radio_level_steps_fine_then_coarse_and_the_unreported_one_is_held_as_an_intent_per_radio()
    {
        using var f = new NoiseFixture();
        f.Radio.Levels["radio_nrf"] = 15;
        var level = f.Noise.Levers("radio_nrf", false)[0];
        f.Noise.Adjust(level, +1, "test");
        Assert.Equal(20, f.Radio.Levels["radio_nrf"]);
        Assert.Equal(20, f.Held.Get("1234-5678", "radio_nrf"));
        // A reported level is the radio's own; nothing is held for it, and
        // Legacy NR is reported since the Track E follow-up (#1012).
        f.Noise.Adjust(f.Noise.Levers("radio_nb", false)[0], +1, "test");
        Assert.Null(f.Held.Get("1234-5678", "radio_nb"));
        f.Radio.Levels["radio_nrl"] = 15;
        f.Noise.Adjust(f.Noise.Levers("radio_nrl", false)[0], +1, "test");
        Assert.Equal(20, f.Radio.Levels["radio_nrl"]);
        Assert.Null(f.Held.Get("1234-5678", "radio_nrl"));
        // Another radio keeps its own.
        Assert.Null(f.Held.Get("9999", "radio_nrf"));
    }

    [Fact]
    public void Held_intents_are_written_on_connect_and_on_a_slice_change_and_nothing_is_written_when_never_set()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        Assert.Empty(f.Radio.Writes.Where(w => w.Contains(".level")));
        f.Held.Set("1234-5678", "radio_nrf", 30);
        f.Held.Set("other-radio", "radio_nrf", 50);
        f.Noise.Attach("USB");
        Assert.Equal(new[] { "radio_nrf.level=30" }, f.Radio.Writes.Where(w => w.Contains(".level")));
        f.Radio.Writes.Clear();
        f.Noise.NoteActiveSlice(0);
        Assert.Empty(f.Radio.Writes);
        f.Noise.NoteActiveSlice(1);
        Assert.Equal(new[] { "radio_nrf.level=30" }, f.Radio.Writes);
    }

    /// <summary>
    /// S12, ruled by Noel 2026-11-11 20:30 ("go with radio"): once Legacy NR,
    /// Legacy Auto-Notch and Spectral NR are reported (#1012), a level saved
    /// for them before is never written on connect or on a slice change; they
    /// follow whatever the radio already has. NR Filter is still restored.
    /// And what Space reports is the radio's level, not the stale intent (the
    /// 2026-11-08 walk heard "Legacy NR, level 1" while the radio ran 100).
    /// </summary>
    [Fact]
    public void The_three_reported_levels_follow_the_radio_on_connect_and_Space_reports_the_radios_level()
    {
        using var f = new NoiseFixture();
        foreach (var id in new[] { "radio_nrl", "radio_anfl", "radio_nrs" }) f.Held.Set("1234-5678", id, 1);
        f.Held.Set("1234-5678", "radio_nrf", 30);
        f.Radio.Levels["radio_nrl"] = 100;
        f.Radio.Levels["radio_anfl"] = 40;
        f.Radio.Levels["radio_nrs"] = 25;

        f.Noise.Attach("USB");
        f.Noise.NoteActiveSlice(0);
        f.Noise.NoteActiveSlice(1);

        Assert.Equal(new[] { "radio_nrf.level=30", "radio_nrf.level=30" }, f.Radio.Writes.Where(w => w.Contains(".level")));
        Assert.Equal(100, f.Radio.Levels["radio_nrl"]);
        Assert.Equal(40, f.Radio.Levels["radio_anfl"]);
        Assert.Equal(25, f.Radio.Levels["radio_nrs"]);

        f.Radio.Switches["radio_nrl"] = true;
        f.Radio.Switches["radio_anfl"] = true;
        f.Radio.Switches["radio_nrs"] = true;
        string summary = f.Noise.ActiveSummary();
        Assert.Contains("Legacy NR, level 100", summary);
        Assert.Contains("Legacy Auto-Notch, level 40", summary);
        Assert.Contains("Spectral NR, level 25", summary);
        Assert.Equal(100, f.Noise.NumberOf(f.Noise.Levers("radio_nrl", false)[0]));
    }

    // ── Return to defaults (D4) ──

    [Fact]
    public void Return_to_defaults_restores_a_computer_engine_and_leaves_it_on_or_off_as_it_was()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr4", "test");
        var reduction = f.Noise.Levers("nr4", false)[0];
        f.Noise.Adjust(reduction, +1, "test");
        f.Noise.Adjust(f.Noise.FloorLeverOf("nr4")!, +1, "test");
        f.Pipeline.SpectralSlot.TrySetSetting("nr4", "smoothing", new(0.25));
        f.Noise.ReturnToDefaults("nr4", "test");
        Assert.Equal(20, f.Pipeline.SpectralSlot.GetSetting("nr4", "reduction_db").Number);
        Assert.Equal(0.5, f.Pipeline.SpectralSlot.GetSetting("nr4", "smoothing").Number);
        Assert.Equal(1f, f.Pipeline.SpectralSlot.GetStrength("nr4"));
        Assert.True(f.Noise.IsOn("nr4"));
        // The floor is Advanced on NR4 (#1019). It was reset but not named; since
        // the Track E follow-up (E2, S11) the sentence names it after the main levers.
        Assert.Equal("NR4 back to its defaults: reduction 20 decibels, "
            + Lexicon.Get("audio.noise_surface.defaults_floor", ("value", f.Noise.ValueText(f.Noise.FloorLeverOf("nr4")!))) + ".", f.Spoken.Last);
        Assert.Equal(SpeechSubject.NoiseDefaults, f.Spoken.Said[^1].Subject);

        f.Noise.Toggle("nr4", "test");
        f.Noise.ReturnToDefaults("nr4", "test");
        Assert.False(f.Noise.IsOn("nr4"));
    }

    /// <summary>
    /// Track E follow-up E2 (S11): the Track S walk heard "NNR back to its
    /// defaults: depth 25 decibels, model Standard voice." with the Advanced
    /// noise floor reset but not named. Every engine whose floor is Advanced
    /// now names it once; an engine whose floor is its main lever (RNNoise,
    /// NR2) already named it and does not name it twice.
    /// </summary>
    [Theory]
    [InlineData("nnr", true)]
    [InlineData("deepfilternet3", true)]
    [InlineData("nr4", true)]
    [InlineData("spectral_capture", true)]
    [InlineData("rnnoise", false)]
    [InlineData("nr2", false)]
    public void Return_to_defaults_names_the_noise_floor_it_reset_once(string id, bool advancedFloor)
    {
        using var f = new NoiseFixture();
        var floor = f.Noise.FloorLeverOf(id)!;
        Assert.Equal(advancedFloor, floor.Advanced);
        f.Noise.Set(floor, 55, null, "test");
        f.Noise.ReturnToDefaults(id, "test");
        Assert.Equal(NoiseFloor.DefaultFloor(id, DenoiseDirection.Receive), f.Noise.NumberOf(floor));
        // The whole sentence: the main levers by name and value (the floor among
        // them where it is main), then the Advanced floor's own fact where it is not.
        var values = f.Noise.Levers(id, includeAdvanced: false)
            .Select(l => f.Noise.LeverName(l).ToLowerInvariant() + " " + f.Noise.ValueText(l)).ToList();
        Assert.Equal(!advancedFloor, f.Noise.Levers(id, includeAdvanced: false).Contains(floor));
        if (advancedFloor) values.Add(Lexicon.Get("audio.noise_surface.defaults_floor", ("value", f.Noise.ValueText(floor))));
        Assert.Equal(Lexicon.Get("audio.noise_surface.defaults", VerbosityLevel.Chatty,
            ("engine", f.Noise.NameOf(id)), ("values", string.Join(", ", values))), f.Spoken.Last);
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
        // #1019: NNR's levers lead with its Depth, so the summary reads them in that order.
        // Track E follow-up E1: then the noise floor (NNR's model is already a main lever, so it is not named twice).
        Assert.Equal("On the radio: Legacy NR, level 8. On this computer: NNR, depth 25 decibels, Standard voice, "
            + Lexicon.Get("audio.noise_surface.active.floor", ("value", f.Noise.ValueText(f.Noise.FloorLeverOf("nnr")!))) + ".", f.Noise.ActiveSummary());
        f.Spoken.Verbosity = VerbosityLevel.Terse;
        Assert.Equal("Radio: Legacy NR 8. Computer: NNR.", f.Noise.ActiveSummary());
    }

    /// <summary>
    /// Track E follow-up E1 (S11, "yep fold em"): the Track S walk heard "On
    /// this computer: RNNoise." after choosing a model, with no model and no
    /// noise floor. RNNoise's model is an Advanced lever and its floor is its
    /// one main lever, a Floor rather than a Setting, so the summary named
    /// neither. Both are now facts of the summary (placeholder words for
    /// Codex), at Chatty only, like the other details; a model already among
    /// the main levers (NNR's) is not named twice.
    /// </summary>
    [Fact]
    public void The_active_summary_names_RNNoises_model_and_every_engines_noise_floor()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Adjust(f.Noise.FloorLeverOf("rnnoise")!, +1, "test");
        f.Settle();
        var model = f.Noise.Levers("rnnoise", true).Single(l => l.Key == "model");
        string summary = f.Noise.ActiveSummary();
        string rnFloor = Lexicon.Get("audio.noise_surface.active.floor", ("value", f.Noise.ValueText(f.Noise.FloorLeverOf("rnnoise")!)));
        Assert.Contains("RNNoise, " + Lexicon.Get("audio.noise_surface.active.model", ("model", f.Noise.ValueText(model))) + ", " + rnFloor, summary);
        Assert.Contains("15", f.Noise.ValueText(f.Noise.FloorLeverOf("rnnoise")!));

        // Every computer engine's floor, in both slots; the model only where one is not already named.
        f.Noise.Toggle("nr4", "test");
        f.Settle();
        string nr4Floor = Lexicon.Get("audio.noise_surface.active.floor", ("value", f.Noise.ValueText(f.Noise.FloorLeverOf("nr4")!)));
        Assert.Contains("NR4, ", f.Noise.ActiveSummary());
        Assert.Contains(nr4Floor, f.Noise.ActiveSummary());
        Assert.Contains(rnFloor, f.Noise.ActiveSummary());

        f.Spoken.Verbosity = VerbosityLevel.Terse;
        Assert.DoesNotContain(rnFloor, f.Noise.ActiveSummary());
        Assert.DoesNotContain(nr4Floor, f.Noise.ActiveSummary());
    }

    private static int CountOf(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
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
        Assert.Equal(0.9f, f.Pipeline.NeuralSlot.GetStrength("rnnoise"), 3); // #1019: the receive default is floor 10
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

    // ── One action, one utterance (#1110) ──

    /// <summary>
    /// What one action said as interrupts: every sentence after <paramref name="from"/>
    /// that was not queued behind what was speaking.
    /// </summary>
    private static List<(string Text, string Subject)> InterruptsSince(NoiseFixture f, int from) =>
        f.Spoken.Said.Select((s, i) => (s, i)).Where(x => x.i >= from && !f.Spoken.SaidAfter.Contains(x.i))
            .Select(x => (x.s.Text, x.s.Subject)).ToList();

    /// <summary>
    /// Run one action and hold it to the rule: every noise sentence goes out as an
    /// interrupt, so a second interrupt from the same action cuts the first (#1110,
    /// heard live: "Back in CW: RNNoise off, NNR on." cut at its first word by
    /// "NNR switched to Premium CW for CW; ..."). Returns what it said.
    /// </summary>
    private static List<(string Text, string Subject)> OneAction(NoiseFixture f, string what, Action act)
    {
        int from = f.Spoken.Said.Count;
        act();
        var said = InterruptsSince(f, from);
        Assert.True(said.Count <= 1, what + " said " + said.Count + " interrupts, so the later ones cut the earlier: "
            + string.Join(" | ", said.Select(s => s.Text)));
        return said;
    }

    /// <summary>
    /// What one mode change said, held to #1117's rule: the mode path has just
    /// said the mode word ("CW", "Mode CW selected"), so the noise controller's
    /// answer to the change must never interrupt (it cut the mode word at word 0
    /// or 1, live on the 8600), and must be at most one utterance, queued behind
    /// the mode word, on the mode-change subject. Returns the queued sentences.
    /// </summary>
    private static List<(string Text, string Subject)> OneModeChange(NoiseFixture f, string what, string mode)
    {
        int from = f.Spoken.Said.Count;
        Mode(f, mode);
        var interrupts = InterruptsSince(f, from);
        Assert.True(interrupts.Count == 0, what + " interrupted, so it cut the mode word: " + string.Join(" | ", interrupts.Select(s => s.Text)));
        var queued = f.Spoken.Said.Select((s, i) => (s, i)).Where(x => x.i >= from).Select(x => (x.s.Text, x.s.Subject)).ToList();
        Assert.True(queued.Count <= 1, what + " said " + queued.Count + " sentences: " + string.Join(" | ", queued.Select(s => s.Text)));
        Assert.All(queued, s => Assert.Equal(SpeechSubject.NoiseModeStart, s.Subject));
        return queued;
    }

    private static string ModelLabel(NoiseFixture f, string model)
    {
        // The label key is read first, so the lexicon coverage scan never reads an engine id as a key.
        string labelKey = f.Catalog.Find("nnr")!.Settings.First(d => d.Name == "model").Options!.First(o => o.Name == model).LabelKey;
        return Lexicon.Get(labelKey);
    }

    private static string NnrModel(NoiseFixture f) => f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice!;

    private static void Mode(NoiseFixture f, string mode) { f.Radio.Mode = mode; f.Noise.OnModeChanged(mode); }

    private static string RestoreSentence(string groupKey, string offEngine, string onEngine) =>
        Lexicon.Get("audio.noise_surface.mode_restore", VerbosityLevel.Chatty,
            ("group", Lexicon.Get(groupKey, VerbosityLevel.Chatty)),
            ("changes", Lexicon.Get("audio.noise_surface.phrase_off", VerbosityLevel.Chatty, ("engine", offEngine)) + ", "
                + Lexicon.Get("audio.noise_surface.phrase_on", VerbosityLevel.Chatty, ("engine", onEngine))));

    /// <summary>
    /// #1110 as the 8600 heard it: CW remembers NNR, voice remembers RNNoise, and
    /// leaving CW gave NNR its voice model back. Coming back into CW restores the
    /// group AND switches NNR to its CW model. Both facts are one utterance, the
    /// swap first, under the restore's subject; neither cuts the other. And it
    /// is queued behind the mode word, never over it (#1117).
    /// </summary>
    [Fact]
    public void Coming_back_into_CW_says_the_swap_and_the_CW_model_as_one_utterance()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        Mode(f, "CW");                 // the first time: NNR's starting point, replacing RNNoise
        Mode(f, "USB");                // back: RNNoise on, NNR's voice model given back
        Assert.False(NoiseModeRules.IsCwModel(NnrModel(f)));

        var said = OneModeChange(f, "entering CW", "CW");

        Assert.True(NoiseModeRules.IsCwModel(NnrModel(f)));
        string model = Lexicon.Get("audio.noise_surface.cw_model", VerbosityLevel.Chatty, ("model", ModelLabel(f, NnrModel(f))));
        Assert.Equal(RestoreSentence("audio.noise_surface.group.cw", "RNNoise", "NNR") + " " + model, said.Single().Text);
        Assert.Equal(SpeechSubject.NoiseModeStart, said.Single().Subject);
    }

    /// <summary>
    /// The way back out, when voice remembers NNR and CW ran RNNoise: the restore
    /// ("RNNoise off, NNR on") and NNR's voice model coming back are one utterance.
    /// </summary>
    [Fact]
    public void Leaving_CW_says_the_swap_and_the_voice_model_back_as_one_utterance()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
        f.Noise.Toggle("nnr", "test");
        Mode(f, "CW");                 // NNR stays, on a CW model; the voice model is owed back
        f.Noise.Toggle("rnnoise", "test");

        var said = OneModeChange(f, "leaving CW", "USB");

        Assert.Equal("premium", NnrModel(f));
        Assert.Equal(RestoreSentence("audio.noise_surface.group.voice", "RNNoise", "NNR") + " "
            + Lexicon.Get("audio.noise_surface.voice_model_back", VerbosityLevel.Chatty, ("model", ModelLabel(f, "premium"))), said.Single().Text);
    }

    /// <summary>
    /// Choosing NNR in CW while it holds a voice model: the model switch and the
    /// switch's own answer used to be two interrupts, the model first, so "NNR on"
    /// cut "NNR switched to ... for CW". Now one utterance, the answer to the press
    /// first and the model after it, on every road that turns an engine on (the
    /// layer's switch, Home's settled engine choice), and on the refusal too.
    /// </summary>
    [Fact]
    public void Choosing_NNR_in_CW_says_the_switch_then_the_CW_model_as_one_utterance()
    {
        // The switch.
        using (var f = new NoiseFixture())
        {
            f.Radio.Mode = "CW";
            f.Noise.Attach("CW");
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            var said = OneAction(f, "turning NNR on in CW", () => f.Noise.Toggle("nnr", "test"));
            string text = said.Single().Text;
            Assert.StartsWith(Lexicon.Get("audio.noise_surface.on", VerbosityLevel.Chatty, ("engine", "NNR")), text);
            Assert.EndsWith(Lexicon.Get("audio.noise_surface.cw_model", VerbosityLevel.Chatty, ("model", ModelLabel(f, NnrModel(f)))), text);
            Assert.Equal(SpeechSubject.NoiseEngineSwitch, said.Single().Subject);
        }
        // Home's engine field, settled, with the slot already on another engine.
        using (var f = new NoiseFixture())
        {
            f.Radio.Mode = "CW";
            f.Noise.Attach("CW");
            f.Noise.Toggle("rnnoise", "test");
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
            var said = OneAction(f, "Home's engine choice settling on NNR in CW", () => f.Clock.RunAll());
            string text = said.Single().Text;
            Assert.StartsWith(Lexicon.Get("audio.noise_surface.on_replacing", VerbosityLevel.Chatty, ("engine", "NNR"), ("other", "RNNoise")), text);
            Assert.EndsWith(Lexicon.Get("audio.noise_surface.cw_model", VerbosityLevel.Chatty, ("model", ModelLabel(f, NnrModel(f)))), text);
        }
        // The refusal: the helper is missing, NNR is kept as the choice and its model still changes.
        using (var f = new NoiseFixture())
        {
            f.Radio.Mode = "CW";
            f.Noise.Attach("CW");
            f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            var said = OneAction(f, "refusing NNR in CW", () => f.Noise.Toggle("nnr", "test"));
            string text = said.Single().Text;
            Assert.StartsWith(Lexicon.Get("audio.noise_surface.unavailable.helpermissing", VerbosityLevel.Chatty, ("engine", "NNR")), text);
            Assert.EndsWith(Lexicon.Get("audio.noise_surface.cw_model", VerbosityLevel.Chatty, ("model", ModelLabel(f, NnrModel(f)))), text);
        }
    }

    /// <summary>
    /// The brackets with no favourites: the once-per-session explanation and the
    /// step's answer are one utterance, so the answer no longer cuts the explanation.
    /// A part this verbosity would not speak is left out of it, as the screen reader
    /// would have dropped it.
    /// </summary>
    [Fact]
    public void The_first_bracket_with_no_favourites_says_the_explanation_and_the_step_as_one_utterance()
    {
        using (var f = new NoiseFixture())
        {
            f.Noise.BeginSession();
            var said = OneAction(f, "the first bracket", () => f.Noise.Step(+1, "test"));
            Assert.Equal(Lexicon.Get("audio.noise_surface.no_favourites", VerbosityLevel.Chatty) + " "
                + Lexicon.Get("audio.noise_surface.on", VerbosityLevel.Chatty, ("engine", f.Noise.NameOf("radio_nr"))), said.Single().Text);
        }
        using (var f = new NoiseFixture())
        {
            f.Spoken.Verbosity = VerbosityLevel.Terse;
            f.Noise.BeginSession();
            var said = OneAction(f, "the first bracket at Terse", () => f.Noise.Step(+1, "test"));
            Assert.Equal(Lexicon.Get("audio.noise_surface.on", VerbosityLevel.Terse, ("engine", f.Noise.NameOf("radio_nr"))), said.Single().Text);
            Assert.Equal(VerbosityLevel.Terse, f.Spoken.Said.Single().Level);
        }
    }

    /// <summary>
    /// The class, not one pair (#1110): every road that changes noise settings,
    /// walked through the states where they say the most (CW with NNR's model to
    /// swap, refusals, starting points, presets, Escape, the 0 key, levers,
    /// defaults, favourites, PC audio off), says at most one interrupt per action.
    /// A mode change says none: its one utterance is queued behind the mode word
    /// (#1117).
    /// </summary>
    [Fact]
    public void Every_noise_action_says_at_most_one_interrupt()
    {
        foreach (bool helperMissing in new[] { false, true })
        {
            using var f = new NoiseFixture();
            if (helperMissing) f.Unavailable["nnr"] = Unavailability.HelperMissing;
            OneAction(f, "connect in voice", () => f.Noise.Attach("USB"));
            OneAction(f, "the neural slot on (voice starting point)", () => f.Noise.SetSlotOn(DenoiseFamily.Neural, true, "test"));
            OneAction(f, "the spectral slot on", () => f.Noise.SetSlotOn(DenoiseFamily.Spectral, true, "test"));
            OneAction(f, "RNNoise on", () => f.Noise.Toggle("rnnoise", "test"));
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            OneModeChange(f, "first time in CW", "CW");
            OneModeChange(f, "back to voice", "USB");
            OneModeChange(f, "CW again", "CW");
            OneAction(f, "RNNoise in CW", () => f.Noise.Toggle("rnnoise", "test"));
            OneAction(f, "NNR in CW", () => f.Noise.SetOn("nnr", true, "test"));
            OneAction(f, "NNR again in CW", () => f.Noise.SetOn("nnr", true, "test"));
            if (f.Noise.Levers("nnr", false).FirstOrDefault(l => l.Key == "model") is { } model)
            {
                OneAction(f, "NNR's model lever in CW", () => f.Noise.Adjust(model, +1, "test"));
                OneAction(f, "NNR's model to its edge in CW", () => f.Noise.Place(model, -1, "test"));
            }
            OneAction(f, "NNR's defaults in CW", () => f.Noise.ReturnToDefaults("nnr", "test"));
            OneAction(f, "favourite NNR", () => f.Noise.ToggleFavourite("nnr", "test"));
            OneAction(f, "favourite RNNoise", () => f.Noise.ToggleFavourite("rnnoise", "test"));
            f.Noise.BeginSession();
            OneAction(f, "a bracket", () => f.Noise.Step(+1, "test"));
            OneAction(f, "another bracket", () => f.Noise.Step(+1, "test"));
            OneAction(f, "a preset step", () => f.Noise.StepPreset(+1, "test"));
            var snap = f.Noise.TakeSnapshot();
            OneAction(f, "the 0 key", () => f.Noise.ComputerOffAndBack("test"));
            OneAction(f, "the 0 key again", () => f.Noise.ComputerOffAndBack("test"));
            OneAction(f, "Escape's revert", () => f.Noise.RestoreSnapshot(snap, "test"));
            OneAction(f, "a radio engine", () => f.Noise.Toggle("radio_nrl", "test"));
            OneModeChange(f, "digital", "DIGU");
            OneModeChange(f, "voice from digital", "USB");
            OneModeChange(f, "CW from voice", "CW");
            OneAction(f, "the neural slot off", () => f.Noise.SetSlotOn(DenoiseFamily.Neural, false, "test"));
            OneAction(f, "the neural slot on in CW", () => f.Noise.SetSlotOn(DenoiseFamily.Neural, true, "test"));
            OneAction(f, "PC audio off, then RNNoise", () => { f.Radio.PcAudioOn = false; f.Noise.Toggle("rnnoise", "test"); });
            OneAction(f, "PC audio off, then NNR", () => f.Noise.Toggle("nnr", "test"));
        }
    }

    // ── The mode word first (#1117) ──

    /// <summary>
    /// #1117, heard on the 8600: changing mode speaks the mode word ("CW", "USB"),
    /// and the noise controller's answer to the change interrupted it at word 0 or
    /// 1, so the operator heard the noise sentence and not the mode they chose.
    /// Every shape a mode change says (a starting point, its refusal naming the
    /// engine it replaced, a remembered group put back, CW's model, digital) now
    /// goes out as ONE utterance queued behind the mode word, on the mode-change
    /// subject; a change inside a group says nothing, so the mode word stands
    /// alone as before.
    /// </summary>
    [Fact]
    public void Every_mode_change_says_one_utterance_queued_behind_the_mode_word()
    {
        foreach (bool helperMissing in new[] { false, true })
        {
            using var f = new NoiseFixture();
            if (helperMissing) f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Noise.Attach("USB");
            f.Noise.Toggle("rnnoise", "test");
            Assert.Single(OneModeChange(f, "the first CW, its starting point replacing RNNoise", "CW"));
            Assert.Single(OneModeChange(f, "back to voice, RNNoise put back", "USB"));
            Assert.Empty(OneModeChange(f, "LSB, the same group", "LSB"));
            Assert.Single(OneModeChange(f, "CW again, remembered", "CW"));
            Assert.Empty(OneModeChange(f, "CWL, the same group", "CWL"));
            f.Noise.Toggle("rnnoise", "test");
            Assert.Single(OneModeChange(f, "digital, its starting point", "DIGU"));
            Assert.Single(OneModeChange(f, "voice from digital", "USB"));
        }
    }

    /// <summary>
    /// A queued mode sentence has an owner, as every queued noise notice does: it
    /// is retired, before the change's own sentence, once the computer slots it
    /// described are changed (an engine chosen or turned off, NNR's model moved,
    /// the 0 key, Escape, another mode group, a disconnect), so the arbiter cannot
    /// rescue it behind that change's answer. An engine that starts processing
    /// makes it truer and must NOT retire it (that result arrives within
    /// milliseconds, while the mode word may still be speaking); nor does a radio
    /// switch, which leaves it true, nor a mode in the same group.
    /// </summary>
    [Fact]
    public void A_queued_mode_sentence_is_retired_once_the_slots_it_described_change_and_only_then()
    {
        static NoiseFixture QueuedInCw(out NoiseSnapshot voice)
        {
            var f = new NoiseFixture();
            f.Noise.Attach("USB");
            f.Noise.Toggle("rnnoise", "test");
            voice = f.Noise.TakeSnapshot();
            Mode(f, "CW");                                     // NNR's starting point, queued
            Assert.Contains(f.Spoken.Said.Count - 1, f.Spoken.SaidAfter);
            return f;
        }
        static List<int> ModeRetirements(NoiseFixture f) =>
            f.Spoken.Retired.Select((r, i) => (r, i)).Where(x => x.r.Subject == SpeechSubject.NoiseModeStart).Select(x => f.Spoken.RetiredAt[x.i]).ToList();

        using (var f = QueuedInCw(out _))
        {
            f.Noise.OnSelectionResult(new DenoiseSelectionResult(DenoiseDirection.Receive, DenoiseFamily.Neural, "nnr", true));
            f.Noise.Toggle("radio_nrl", "test");
            Mode(f, "CWL");
            Assert.Empty(ModeRetirements(f));
        }

        foreach (var (what, act, answerFollows) in new (string, Action<NoiseFixture, NoiseSnapshot>, bool)[]
        {
            ("RNNoise chosen", (g, _) => g.Noise.Toggle("rnnoise", "test"), true),
            ("NNR turned off", (g, _) => g.Noise.Toggle("nnr", "test"), true),
            ("the 0 key", (g, _) => g.Noise.ComputerOffAndBack("test"), true),
            ("another mode group", (g, _) => Mode(g, "USB"), true),
            ("Escape's revert", (g, snap) => g.Noise.RestoreSnapshot(snap, "test"), false),
            ("a disconnect", (g, _) => g.Noise.Detach(), false),
            ("NNR's model moved", (g, _) =>
            {
                var model = g.Noise.Levers("nnr", true).First(l => l.Key == "model");
                g.Noise.Adjust(model, +1, "test");
                g.Noise.Adjust(model, -1, "test");
            }, false),
        })
        {
            using var f = QueuedInCw(out var voice);
            int before = f.Spoken.Said.Count;
            act(f, voice);
            var at = ModeRetirements(f);
            Assert.True(at.Count == 1, what + " retired the queued mode sentence " + at.Count + " times");
            if (answerFollows)
            {
                Assert.Equal(before, at[0]);                       // before the change's own sentence
                Assert.True(f.Spoken.Said.Count > before, what + " said nothing");
            }
        }
    }

    /// <summary>
    /// The same two facts through the real SpeechArbiter: the mode word the mode
    /// path says (an interrupt with no subject, as MainWindow.SetMode says it) is
    /// followed by the noise sentence handed over WITHOUT an interrupt, so it
    /// cannot cut the mode word; and once the operator chooses another engine
    /// before it was heard, it is not rescued behind that answer. The control: a
    /// radio switch retires nothing, and the same arbiter does rescue the mode
    /// sentence behind its answer, still true.
    /// </summary>
    [Fact]
    public void Through_the_real_arbiter_the_mode_word_is_not_cut_and_an_obsolete_mode_sentence_is_not_rescued()
    {
        foreach (bool obsolete in new[] { true, false })
        {
            using var f = new NoiseFixture();
            f.Noise.Attach("USB");
            f.Noise.Toggle("rnnoise", "test");
            var speaker = new SilentArbiterSpeaker();
            typeof(NoiseController).GetField("_speaker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(f.Noise, speaker);
            try
            {
                speaker.ModeWord("CW");
                Mode(f, "CW");
                var calls = speaker.Calls.ToList();
                Assert.Equal(("CW", true, false), calls[0]);
                var noise = Assert.Single(calls.Skip(1));
                Assert.False(noise.Interrupt, "the noise sentence interrupted the mode word: " + noise.Text);

                // The arbiter holds a rescue for its settle window (600 ms) before handing it over.
                bool Rescued(int ms) => System.Threading.SpinWait.SpinUntil(() => speaker.Calls.Any(c => c.Text == noise.Text && c.Salvaged), ms);
                if (obsolete)
                {
                    f.Noise.Toggle("rnnoise", "test");
                    Assert.False(Rescued(2000), "the obsolete mode sentence was rescued behind the new engine's answer");
                }
                else
                {
                    f.Noise.Toggle("radio_nrl", "test");
                    Assert.True(Rescued(2500), "the arbiter never rescued the unretired mode sentence, so this instrument cannot see the defect");
                }
            }
            finally { speaker.Dispose(); }
        }
    }

    /// <summary>
    /// Repair round 1, B1 (Codex): e4 moved the Slice menu's mode write half a
    /// second after the click, so a newer mode choice, another slice or another
    /// radio could be overwritten by the older click. The write is back AT THE
    /// CLICK (FlexBase's setter captures its slice there), and only the sentence
    /// waits for the menu to close. The harness's checks 7 and 8: the row's
    /// interrupt lost the NVDA cancel race and took the noise sentence with it,
    /// and Next and Previous Mode were cut by the menu's close, so the sentence
    /// is now QUEUED after the close, with the noise answer held behind it. A
    /// source pin: the menu cannot be driven without a window.
    /// </summary>
    [Fact]
    public void The_Slice_menu_asks_the_radio_at_the_click_and_says_the_mode_queued_after_the_menu_closes()
    {
        string menu = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs")).Replace("\r\n", "\n");
        string Body(string start, string end)
        {
            int from = menu.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, "not found: " + start);
            int to = menu.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.True(to > from, "the end of " + start + " was not found");
            return menu.Substring(from, to - from);
        }

        // The mode rows: hold the noise answer, then write at the click, then hand the sentence on. No wait before the write.
        string row = Body("AddRadioChecked(modeSub, m + accel, () =>", "() => string.Equals(Rig?.Mode, m");
        int hold = row.IndexOf("_window.Noise.HoldModeAnswer()", StringComparison.Ordinal);
        int write = row.IndexOf("rig.Mode = m;", StringComparison.Ordinal);
        int say = row.IndexOf("SayModeAfterMenuClose(", StringComparison.Ordinal);
        Assert.True(hold >= 0 && write > hold && say > write, "hold " + hold + ", write " + write + ", say " + say);
        Assert.DoesNotContain("Delay", row);
        Assert.DoesNotContain("BeginInvoke", row);

        // Next and Previous Mode: the same, through CycleModeQuietly.
        // The write at the click, in the row itself; the word and the hold after it.
        Assert.Contains("AddWired(modeSub, \"Next Mode\\tAlt+M\", () => SayCycledModeAfterMenuClose(_window.CycleModeQuietly(1)));", menu);
        Assert.Contains("AddWired(modeSub, \"Previous Mode\\tAlt+Shift+M\", () => SayCycledModeAfterMenuClose(_window.CycleModeQuietly(-1)));", menu);
        string cycle = Body("private void SayCycledModeAfterMenuClose(string? mode)", "\n    }\n");
        Assert.Contains("SayModeAfterMenuClose(mode, rig, rig.RXVFO, _window.ModeChoices, _window.Noise.HoldModeAnswer());", cycle);
        Assert.DoesNotContain("Mode =", cycle);

        // The sentence: after the close, one interrupting utterance on its own subject (repair round 2),
        // dropped if a newer choice, radio or slice came first.
        string sayer = Body("private void SayModeAfterMenuClose(", "\n    }\n");
        Assert.Contains("MenuModeSentence.SayWhenTheMenuHasClosed(", sayer);
        Assert.Contains("SpeechIntent.Interrupt", sayer);
        Assert.Contains("subject: Radios.Speech.SpeechSubject.ModeChoice", sayer);
        Assert.DoesNotContain("SpeechIntent.Queue", sayer);
        Assert.Contains("_window.ModeChoices == choice", sayer);
        Assert.Contains("ReferenceEquals(Rig, rig)", sayer);
        Assert.Contains("rig.RXVFO == slice", sayer);
    }

    /// <summary>
    /// The menu's sentence first, the noise controller's answer right after it,
    /// as ONE utterance (repair round 2). Round 1 queued the two separately: the
    /// menu's sentence then waited behind the reader's own five seconds of close
    /// announcements (NVDA's log, the 8600 harness), and the answer behind it.
    /// Joined and said as one interrupt under <see cref="SpeechSubject.ModeChoice"/>,
    /// the mode comes at once and nothing of ours waits queued behind it to be
    /// withdrawn by the cancel race. The controller still owns the joined
    /// sentence: a change to what its noise part states retires its subject.
    /// </summary>
    [Fact]
    public void A_menu_mode_sentence_and_its_noise_answer_are_one_interrupt_the_controller_still_owns()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        var speaker = new SilentArbiterSpeaker();
        typeof(NoiseController).GetField("_speaker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(f.Noise, speaker);
        try
        {
            var hold = f.Noise.HoldModeAnswer();
            Mode(f, "CW");                                         // the radio confirms the click
            Assert.Empty(speaker.Calls);                            // held: nothing said before the menu's own sentence
            MenuModeSentence.SayWhenTheMenuHasClosed("Mode CW selected", () => true, hold, _ => System.Threading.Tasks.Task.CompletedTask,
                (text, level) => speaker.Say(text, SpeechSubject.ModeChoice, level)).GetAwaiter().GetResult();
            var said = Assert.Single(speaker.Calls);                // one utterance
            Assert.True(said.Interrupt);
            Assert.StartsWith("Mode CW selected ", said.Text);       // the mode first
            Assert.Contains("NNR", said.Text);                      // then CW's starting point
            hold.Dispose();                                         // after taking: nothing more
            Assert.Single(speaker.Calls);
        }
        finally { speaker.Dispose(); }

        // The controller owns it: what its noise part states changes, its subject is retired.
        using var g = new NoiseFixture();
        g.Noise.Attach("USB");
        g.Noise.Toggle("rnnoise", "test");
        g.Spoken.Clear();
        var gHold = g.Noise.HoldModeAnswer();
        Mode(g, "CW");
        string? joined = null;
        MenuModeSentence.SayWhenTheMenuHasClosed("Mode CW selected", () => true, gHold, _ => System.Threading.Tasks.Task.CompletedTask,
            (text, level) => joined = text).GetAwaiter().GetResult();
        Assert.NotNull(joined);
        Assert.Empty(g.Spoken.Said);                                // nothing queued separately
        Assert.DoesNotContain(g.Spoken.Retired, r => r.Subject == SpeechSubject.ModeChoice);
        g.Noise.Toggle("rnnoise", "test");
        Assert.Contains(g.Spoken.Retired, r => r.Subject == SpeechSubject.ModeChoice);
    }

    /// <summary>
    /// A newer mode choice before the menu closed: the menu's sentence about the
    /// older choice is dropped, the held answer about the older group is dropped
    /// when the newer group is entered, and only the newer group's answer goes
    /// out, queued. Another radio or slice is the same rule (stillCurrent).
    /// </summary>
    [Fact]
    public void A_newer_mode_choice_before_the_menu_closes_drops_the_older_sentence_and_its_noise_answer()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        Mode(f, "CW");                                             // CW remembers its starting point
        Mode(f, "USB");
        f.Spoken.Clear();
        var hold = f.Noise.HoldModeAnswer();
        Mode(f, "CW");                                             // the menu's CW, confirmed, held
        Assert.Empty(f.Spoken.Said);
        Mode(f, "USB");                                            // Alt+U before the menu closed
        MenuModeSentence.SayWhenTheMenuHasClosed("Mode CW selected", () => false, hold, _ => System.Threading.Tasks.Task.CompletedTask,
            (text, level) => f.Spoken.Say(text, SpeechSubject.ModeChoice, level)).GetAwaiter().GetResult();
        var said = Assert.Single(f.Spoken.Said);                    // only the voice answer
        Assert.StartsWith(Lexicon.Get("audio.noise_surface.mode_restore", VerbosityLevel.Chatty,
            ("group", Lexicon.Get("audio.noise_surface.group.voice", VerbosityLevel.Chatty)), ("changes", "")).Split(':')[0], said.Text);
        Assert.Contains(0, f.Spoken.SaidAfter);
    }

    /// <summary>
    /// A hold nobody releases ends by itself, and a held answer whose facts
    /// changed while it was held is dropped, not delivered.
    /// </summary>
    [Fact]
    public void A_held_noise_answer_is_released_by_its_time_limit_and_dropped_if_what_it_states_changed()
    {
        using (var f = new NoiseFixture())
        {
            f.Noise.Attach("USB");
            f.Noise.Toggle("rnnoise", "test");
            f.Spoken.Clear();
            f.Noise.HoldModeAnswer();
            Mode(f, "CW");
            Assert.Empty(f.Spoken.Said);
            f.Clock.RunAll();                                      // the hold's own time limit
            Assert.Single(f.Spoken.Said);
            Assert.Contains(0, f.Spoken.SaidAfter);
        }
        using (var f = new NoiseFixture())
        {
            f.Noise.Attach("USB");
            f.Noise.Toggle("rnnoise", "test");
            f.Spoken.Clear();
            var hold = f.Noise.HoldModeAnswer();
            Mode(f, "CW");                                         // "First time in CW: NNR ... on", held
            string other = NnrModel(f) == "premium_cw" ? "standard_cw" : "premium_cw";
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, other));   // what it states is no longer true
            hold.Dispose();
            Assert.Empty(f.Spoken.Said);
        }
    }

    // ── Repair round 2 (Codex's second run, and the 8600 harness's second run) ──

    /// <summary>
    /// Codex's probe (repair round 2), kept with its control: round 1 queued the
    /// menu's sentence with no subject, so once handed over nothing could retire
    /// it, and the real arbiter rescued an older "Mode CW selected" after a newer
    /// "USB" (the control case reproduces exactly that). Said as round 2 says it
    /// (one interrupt, under <see cref="SpeechSubject.ModeChoice"/>, with the newer
    /// choice noted by <see cref="ModeChoiceLedger"/>), it is never rescued: the
    /// arbiter keeps no interrupt to rescue, and the subject is retired anyway, so
    /// the rule holds if the menu ever queues again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_said_menu_sentence_is_not_rescued_after_a_newer_mode_choice(bool asRoundOneSaidIt)
    {
        using var f = new NoiseFixture();
        var speaker = new SilentArbiterSpeaker();
        try
        {
            var ledger = new ModeChoiceLedger(speaker.Retire);
            speaker.ModeWord("earlier reader speech");
            string old = "Mode CW selected";
            ledger.Note();                                          // the menu row's own choice
            MenuModeSentence.SayWhenTheMenuHasClosed(old, () => true, f.Noise.HoldModeAnswer(), _ => System.Threading.Tasks.Task.CompletedTask,
                (text, level) =>
                {
                    if (asRoundOneSaidIt) speaker.SayAfter(text, null!, level);          // round 1: queued, no subject
                    else speaker.Say(text, SpeechSubject.ModeChoice, level);             // round 2, as NativeMenuBar says it
                }).GetAwaiter().GetResult();
            ledger.Note();                                          // Alt+U: SetMode notes the choice before it speaks
            speaker.ModeWord("USB");
            bool rescued = System.Threading.SpinWait.SpinUntil(() => speaker.Calls.Any(c => c.Text == old && c.Salvaged), 2500);
            Assert.Equal(asRoundOneSaidIt, rescued);
        }
        finally { speaker.Dispose(); }
    }

    /// <summary>The ledger: every mode choice is counted and retires the menu's owed sentence on <see cref="SpeechSubject.ModeChoice"/>.</summary>
    [Fact]
    public void Every_mode_choice_retires_the_menus_owed_sentence()
    {
        var retired = new List<(string Subject, string Why)>();
        var ledger = new ModeChoiceLedger((subject, why) => retired.Add((subject, why)));
        Assert.Equal(1, ledger.Note());
        Assert.Equal(2, ledger.Note());
        Assert.Equal(2, ledger.Count);
        Assert.Equal(2, retired.Count(r => r.Subject == SpeechSubject.ModeChoice));
    }

    /// <summary>
    /// Every road that asks the radio for the active slice's mode counts a mode
    /// choice (repair round 2, Codex: the band jump's 60 m correction did not, so
    /// an older menu sentence was still said after it). Found by search, not by
    /// a list: every write of a mode, every 60 m correction and every memory
    /// recall in the WPF project and the app's own VB files must have
    /// NoteModeChoice in the lines just before it. Code inside "#If 0" is not
    /// compiled and is skipped. Not roads: a memory's stored mode edited in the
    /// Memories dialog (mem.Mode, the radio is not asked), and the transmit mode
    /// the Fixer and the tone ladder set and put back inside a running check.
    /// </summary>
    [Fact]
    public void Every_road_that_asks_the_radio_for_a_mode_counts_a_mode_choice()
    {
        string root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Concat(Directory.GetFiles(root, "*.vb", SearchOption.TopDirectoryOnly));
        var road = new System.Text.RegularExpressions.Regex(
            @"(\b(RigControl|Rig|rig)\.Mode\s*=[^=])|SixtyMeterModeDelivery\.Deliver\(|\bmem\.Select\(\)|SelectMemoryByName\(|SelectMemory\(\)");
        var found = new List<string>();
        var missing = new List<string>();
        foreach (string file in files)
        {
            var lines = File.ReadAllLines(file);
            bool disabled = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("#If 0", StringComparison.OrdinalIgnoreCase)) { disabled = true; continue; }
                if (disabled && t.StartsWith("#End If", StringComparison.OrdinalIgnoreCase)) { disabled = false; continue; }
                if (disabled || t.StartsWith("//") || t.StartsWith("'") || t.StartsWith("///") || !road.IsMatch(lines[i])) continue;
                if (lines[i].Contains("public ") || lines[i].Contains("Function ") || lines[i].Contains("Sub ")) continue;   // a declaration, not a call
                string where = Path.GetFileName(file) + ":" + (i + 1) + "  " + t;
                found.Add(where);
                bool counted = Enumerable.Range(Math.Max(0, i - 25), i - Math.Max(0, i - 25)).Any(j => lines[j].Contains("NoteModeChoice"));
                if (!counted) missing.Add(where);
            }
        }
        Assert.True(found.Count >= 10, "the search found only " + found.Count + " roads, so it is not seeing them: " + string.Join(" | ", found));
        Assert.True(missing.Count == 0, "roads that ask the radio for a mode without counting a mode choice:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// Codex's probe, kept: the reason a refusal is RECORDED is the reason it was
    /// SAID. The helper reports "not running" when the CW starting point composes
    /// its refusal and recovers before the action ends; round 1 probed it again
    /// there and recorded "runs", so when NNR then processed nothing looked
    /// false and the real arbiter replayed "helper ... not running".
    /// </summary>
    [Fact]
    public void A_refusal_records_the_reason_it_said_not_a_later_probe()
    {
        using var f = new NoiseFixture();
        int probes = 0;
        var rows = f.Catalog.Entries.Select(e => e.Id == "nnr" ? e with { Probe = () => ++probes == 1 ? Unavailability.HelperNotRunning : Unavailability.None } : e);
        var catalog = new DenoiseEngineCatalog(rows);
        var speaker = new SilentArbiterSpeaker();
        try
        {
            var noise = new NoiseController(catalog, () => f.Radio, () => f.Pipeline, () => f.Config, () => { }, speaker, f.Held, f.Clock, f.Cost);
            noise.Attach("USB"); noise.Toggle("rnnoise", "test");
            speaker.ModeWord("CW"); f.Radio.Mode = "CW"; noise.OnModeChanged("CW");
            string old = speaker.Calls.Last().Text;
            Assert.Contains("not running", old);
            var results = new System.Collections.Concurrent.ConcurrentQueue<DenoiseSelectionResult>();
            f.Pipeline.SelectionResult += (_, r) => results.Enqueue(r);
            f.Settle();
            Assert.Equal("nnr", f.Pipeline.NeuralSlot.ProcessingEngineId);
            Assert.True(System.Threading.SpinWait.SpinUntil(() => results.Any(r => r.EngineId == "nnr" && r.Processing), 1500));
            noise.OnSelectionResult(results.First(r => r.EngineId == "nnr" && r.Processing));
            noise.Toggle("radio_nrl", "test");
            bool rescued = System.Threading.SpinWait.SpinUntil(() => speaker.Calls.Any(c => c.Text == old && c.Salvaged), 2500);
            Assert.False(rescued, "the false helper refusal was replayed after NNR processed: " + old);
        }
        finally { speaker.Dispose(); }
    }

    /// <summary>
    /// Codex's probe, kept: the RNNoise starting point says DeepFilterNet3 cannot
    /// run here. That is a fact it states, so once DeepFilterNet3 can run (its
    /// files repaired) the sentence is retired and not rescued.
    /// </summary>
    [Fact]
    public void The_RNNoise_fallback_sentence_records_the_engine_it_says_cannot_run()
    {
        using var f = new NoiseFixture();
        f.Radio.Mode = "CW"; f.Noise.Attach("CW"); f.Noise.Toggle("rnnoise", "test");
        f.Unavailable["deepfilternet3"] = Unavailability.LibraryMissing;
        var speaker = new SilentArbiterSpeaker();
        typeof(NoiseController).GetField("_speaker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(f.Noise, speaker);
        try
        {
            speaker.ModeWord("USB"); Mode(f, "USB");
            string old = speaker.Calls.Last().Text;
            Assert.Contains("DeepFilterNet3 cannot run here", old);
            f.Unavailable.Remove("deepfilternet3");                 // its files repaired; no slot setting changed
            f.Noise.Toggle("radio_nrl", "test");
            bool rescued = System.Threading.SpinWait.SpinUntil(() => speaker.Calls.Any(c => c.Text == old && c.Salvaged), 2500);
            Assert.False(rescued, "the fallback still says DeepFilterNet3 cannot run after it can: " + old);
        }
        finally { speaker.Dispose(); }
    }

    /// <summary>
    /// Codex's probe, kept, and the count it noted (repair round 2): expiry and
    /// release are idempotent, a disconnect drops the held sentence, and a hold
    /// taken before a disconnect, released after it, does not release a hold
    /// taken on the next connection.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Holds_are_idempotent_and_none_outlives_a_disconnect(bool expiryFirst)
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB"); f.Noise.Toggle("rnnoise", "test"); f.Spoken.Clear();
        var hold = f.Noise.HoldModeAnswer(); Mode(f, "CW"); Assert.Empty(f.Spoken.Said);
        if (expiryFirst) f.Clock.RunAll();
        hold.Dispose(); hold.Dispose(); f.Clock.RunAll();
        Assert.Single(f.Spoken.Said);
        f.Spoken.Clear();

        var old = f.Noise.HoldModeAnswer(); Mode(f, "USB"); f.Noise.Detach();
        old.Dispose(); Assert.Empty(f.Spoken.Said);                 // dropped by the disconnect

        // The next connection, two ways: an old hold never released must not keep
        // holding the new connection's answer, and an old hold released late must
        // not end a hold taken on the new connection.
        f.Noise.HoldModeAnswer();                                   // abandoned before the disconnect
        f.Noise.Detach();
        f.Noise.Attach("USB");
        f.Spoken.Clear();                                           // connect's own sentence, if any
        var current = f.Noise.HoldModeAnswer();
        Mode(f, "CW");
        current.Dispose();
        Assert.Single(f.Spoken.Said);                               // released by its own surface, at once
        f.Spoken.Clear();

        var older = f.Noise.HoldModeAnswer();
        f.Noise.Detach();
        f.Noise.Attach("USB");
        f.Spoken.Clear();
        var next = f.Noise.HoldModeAnswer();
        Mode(f, "CW");
        older.Dispose();
        Assert.Empty(f.Spoken.Said);                                // still held for the current surface
        next.Dispose();
        Assert.Single(f.Spoken.Said);
    }

    // ── Repair round 1, B2 (Codex's probes, kept): a queued mode sentence is retired when anything it states becomes false ──

    /// <summary>
    /// The first voice visit's sentence names DeepFilterNet3's limit. Changing the
    /// limit before it was heard retired only on a model change, so the old
    /// "limit at 9 decibels" was rescued after the new value. Now any fact it
    /// states that changes retires it, before the change's own answer.
    /// </summary>
    [Fact]
    public void A_changed_limit_retires_the_first_voice_sentence_before_its_answer()
    {
        using var f = new NoiseFixture();
        f.Radio.Mode = "CW"; f.Noise.Attach("CW");
        f.Noise.Toggle("rnnoise", "test");
        Mode(f, "USB");
        string old = f.Spoken.Last;
        Assert.Contains("limit at", old);
        var limit = f.Noise.Levers("deepfilternet3", true).Single(l => l.Key == "attenuation_limit_db");
        double before = f.Pipeline.NeuralSlot.GetSetting("deepfilternet3", "attenuation_limit_db").Number;
        int answer = f.Spoken.Said.Count;
        f.Noise.Adjust(limit, 1, "test");
        Assert.NotEqual(before, f.Pipeline.NeuralSlot.GetSetting("deepfilternet3", "attenuation_limit_db").Number);
        Assert.True(f.Spoken.Said.Count > answer);
        Assert.Contains(f.Spoken.Retired.Select((r, i) => (r, i)), x => x.r.Subject == SpeechSubject.NoiseModeStart && f.Spoken.RetiredAt[x.i] == answer);
    }

    [Fact]
    public void Through_the_real_arbiter_the_old_voice_limit_is_not_rescued_after_the_new_limit()
    {
        using var f = new NoiseFixture();
        f.Radio.Mode = "CW"; f.Noise.Attach("CW");
        f.Noise.Toggle("rnnoise", "test");
        var speaker = new SilentArbiterSpeaker();
        typeof(NoiseController).GetField("_speaker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(f.Noise, speaker);
        try
        {
            speaker.ModeWord("USB"); Mode(f, "USB");
            string old = speaker.Calls.Last().Text;
            Assert.Contains("limit at", old);
            var limit = f.Noise.Levers("deepfilternet3", true).Single(l => l.Key == "attenuation_limit_db");
            f.Noise.Adjust(limit, 1, "test");
            bool rescued = System.Threading.SpinWait.SpinUntil(() => speaker.Calls.Any(c => c.Text == old && c.Salvaged), 2500);
            Assert.False(rescued, "the old starting point was rescued after the limit changed: " + old);
        }
        finally { speaker.Dispose(); }
    }

    /// <summary>
    /// A selection that starts processing keeps a mode sentence that named its
    /// engine on (still true), and retires one that said that engine's helper was
    /// not running (now false): through the real slot, its real success result,
    /// and the real arbiter, which rescues an owed sentence behind a later radio
    /// action. Reachable: the starting point probes availability while the build
    /// it queued can still succeed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_selection_that_starts_processing_keeps_an_on_sentence_and_retires_a_refusal_it_made_false(bool refusal)
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        if (refusal) f.Unavailable["nnr"] = Unavailability.HelperNotRunning;
        var speaker = new SilentArbiterSpeaker();
        typeof(NoiseController).GetField("_speaker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(f.Noise, speaker);
        try
        {
            speaker.ModeWord("CW"); Mode(f, "CW");
            string old = speaker.Calls.Last().Text;
            Assert.Equal(refusal, old.Contains("not running"));
            f.Unavailable.Remove("nnr");
            var results = new System.Collections.Concurrent.ConcurrentQueue<DenoiseSelectionResult>();
            f.Pipeline.SelectionResult += (_, r) => results.Enqueue(r);
            f.Settle();
            Assert.Equal("nnr", f.Pipeline.NeuralSlot.ProcessingEngineId);
            Assert.True(System.Threading.SpinWait.SpinUntil(() => results.Any(r => r.EngineId == "nnr" && r.Processing), 1500));
            f.Noise.OnSelectionResult(results.First(r => r.EngineId == "nnr" && r.Processing));
            f.Noise.Toggle("radio_nrl", "test");                 // a later radio action: the arbiter rescues any owed mode sentence
            bool rescued = System.Threading.SpinWait.SpinUntil(() => speaker.Calls.Any(c => c.Text == old && c.Salvaged), 2500);
            Assert.Equal(!refusal, rescued);
        }
        finally { speaker.Dispose(); }
    }

    /// <summary>Codex's journal-road probe, kept: the menu, a favourite and a bracket each journal NNR's CW model under their own road, after their switch (#1118).</summary>
    [Theory]
    [InlineData("menu")]
    [InlineData("favourite")]
    [InlineData("bracket")]
    public void The_CW_model_journal_follows_the_menu_favourite_and_bracket_roads(string road)
    {
        using var f = new NoiseFixture();
        f.Radio.Mode = "CW"; f.Noise.Attach("CW");
        f.Noise.Toggle("rnnoise", "test");
        f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
        string source = "j1118-road-" + road;
        if (road != "menu") f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nnr", Model = "premium" });
        var entries = JournalOf(source, () => { if (road == "menu") f.Noise.Toggle("nnr", source); else f.Noise.Step(1, source); });
        Assert.Equal(2, entries.Count);
        Assert.Equal(NoiseJournalKind.Switch, entries[0].Kind);
        Assert.True(IsCwModelEntry(entries[1]));
    }

    /// <summary>Codex's preset probe, kept: a preset retires the queued mode sentence before its own answer.</summary>
    [Fact]
    public void A_preset_retires_the_queued_mode_sentence_before_its_answer()
    {
        using var f = new NoiseFixture();
        f.Noise.PresetStore = new RoundTwoPresetStore();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        Assert.NotNull(f.Noise.SavePreset("voice", true, false, PresetCollision.Ask, "test"));
        Mode(f, "CW");
        int before = f.Spoken.Said.Count;
        f.Noise.ApplyPreset("voice", "test");
        Assert.Contains(f.Spoken.Retired.Select((r, i) => (r, i)), x => x.r.Subject == SpeechSubject.NoiseModeStart && f.Spoken.RetiredAt[x.i] == before);
    }

    // ── The model the mode's rule chose is journalled under its cause (#1118) ──

    /// <summary>
    /// The entries one road journalled, in order, read from the static journal
    /// by a source no other test uses.
    /// </summary>
    private static List<NoiseJournalEntry> JournalOf(string source, Action act)
    {
        var entries = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { if (e.Source == source) lock (entries) entries.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try { act(); }
        finally { NoiseSettingsJournal.Recorded -= Listen; }
        lock (entries) return entries.ToList();
    }

    private static bool IsCwModelEntry(NoiseJournalEntry e) =>
        e.Kind == NoiseJournalKind.Model && e.ByJjFlexible
        && e.Changes.Single() is { EngineId: "nnr", Setting: "model" } c && !NoiseModeRules.IsCwModel(c.OldValue) && NoiseModeRules.IsCwModel(c.NewValue);

    /// <summary>
    /// #1118, the e3 harness on the 8600: choosing NNR in CW while it held a voice
    /// model journalled the model switch as "Model by JJ Flexible from mode change",
    /// and BEFORE the "Switch from noise layer" it belonged to. It is now journalled
    /// under the road that chose NNR, after that road's own entry, on every road a
    /// voice model meets CW's rule: the layer's switch (with the helper present and
    /// missing), Home's settled engine choice, a preset, and Escape's revert. A real
    /// mode change still journals "from mode change".
    /// </summary>
    [Fact]
    public void A_model_the_CW_rule_chose_is_journalled_under_the_road_that_chose_it_after_that_roads_entry()
    {
        // The layer's switch, as heard: RNNoise on, NNR on a voice model, NNR chosen in CW.
        foreach (bool helperMissing in new[] { false, true })
        {
            using var f = new NoiseFixture();
            f.Radio.Mode = "CW";
            f.Noise.Attach("CW");
            f.Noise.Toggle("rnnoise", "test");
            if (helperMissing) f.Unavailable["nnr"] = Unavailability.HelperMissing;
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            string source = "j1118-layer-" + helperMissing;
            var entries = JournalOf(source, () => f.Noise.Toggle("nnr", source));
            Assert.True(entries.Count == 2, "the layer's switch journalled " + entries.Count + " entries under its own road: "
                + string.Join(" | ", entries.Select(e => e.Kind + " " + string.Join(",", e.Changes))));
            Assert.Equal(NoiseJournalKind.Switch, entries[0].Kind);
            Assert.Contains(new NoiseJournalChange("nnr", "", "off", "on"), entries[0].Changes);
            Assert.True(IsCwModelEntry(entries[1]), "the second entry is not NNR's CW model: " + entries[1]);
            Assert.Equal("premium", entries[1].Changes.Single().OldValue);
        }

        // Home's engine field, settled.
        using (var f = new NoiseFixture())
        {
            f.Radio.Mode = "CW";
            f.Noise.Attach("CW");
            f.Noise.Toggle("rnnoise", "test");
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nnr", "j1118-home");
            var entries = JournalOf("j1118-home", () => f.Clock.RunAll());
            Assert.Equal(2, entries.Count);
            Assert.Equal(NoiseJournalKind.Switch, entries[0].Kind);
            Assert.True(IsCwModelEntry(entries[1]));
        }

        // A preset saved in voice holding NNR on its voice model, applied in CW.
        using (var f = new NoiseFixture())
        {
            f.Noise.PresetStore = new RoundTwoPresetStore();
            f.Noise.Attach("USB");
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            f.Noise.Toggle("nnr", "test");
            Assert.NotNull(f.Noise.SavePreset("NNR voice", true, false, PresetCollision.Ask, "test"));
            Mode(f, "CW");
            f.Noise.Toggle("rnnoise", "test");
            var entries = JournalOf("j1118-preset", () => f.Noise.ApplyPreset("NNR voice", "j1118-preset"));
            int applied = entries.FindIndex(e => e.Kind == NoiseJournalKind.PresetApplied);
            int model = entries.FindIndex(IsCwModelEntry);
            Assert.True(applied >= 0 && model > applied, "the preset's entry at " + applied + ", NNR's CW model at " + model);
        }

        // Escape's revert to a picture taken in voice, put back in CW: no action is
        // open, so it is journalled at once, under Escape's road.
        using (var f = new NoiseFixture())
        {
            f.Noise.Attach("USB");
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            f.Noise.Toggle("nnr", "test");
            var snap = f.Noise.TakeSnapshot();
            Mode(f, "CW");
            f.Noise.Toggle("rnnoise", "test");
            var entries = JournalOf("j1118-escape", () => f.Noise.RestoreSnapshot(snap, "j1118-escape"));
            Assert.Contains(entries, IsCwModelEntry);
        }

        // A real mode change keeps its own road: CW remembered with NNR, entered
        // again after leaving gave the voice model back.
        using (var f = new NoiseFixture())
        {
            f.Noise.Attach("USB");
            f.Pipeline.NeuralSlot.TrySetSetting("nnr", "model", new(0, "premium"));
            f.Noise.Toggle("nnr", "test");
            Mode(f, "CW");
            Mode(f, "USB");
            Assert.Equal("premium", NnrModel(f));
            var entries = JournalOf("mode change", () => Mode(f, "CW"));
            Assert.Contains(entries, IsCwModelEntry);
        }
    }

    // ── The summary never reads a stopped engine as working (#1103) ──

    /// <summary>
    /// #1103, heard on the 8600: with PC audio off, Space read "On this computer:
    /// DeepFilterNet3, limit 9 decibels, ..." as if it were working. With PC audio
    /// off nothing reaches the computer's engines, so the summary says the engine
    /// is chosen and waiting for PC audio (placeholder for Codex), whether it was
    /// chosen with PC audio off or was running before PC audio went off, at every
    /// verbosity; the radio's own engines are still read as they are.
    /// </summary>
    [Fact]
    public void With_PC_audio_off_the_summary_says_the_engine_waits_for_PC_audio()
    {
        foreach (bool runningFirst in new[] { false, true })
        {
            using var f = new NoiseFixture();
            f.Radio.Switches["radio_nrl"] = true;
            f.Radio.Levels["radio_nrl"] = 8;
            if (runningFirst)
            {
                f.Noise.Toggle("deepfilternet3", "test");
                f.Settle();
                Assert.Contains("DeepFilterNet3, ", f.Noise.ActiveSummary());
                f.Radio.PcAudioOn = false;
            }
            else
            {
                f.Radio.PcAudioOn = false;
                f.Noise.Toggle("deepfilternet3", "test");
                f.Settle();
            }
            foreach (var verbosity in new[] { VerbosityLevel.Chatty, VerbosityLevel.Terse, VerbosityLevel.Critical })
            {
                f.Spoken.Verbosity = verbosity;
                string summary = f.Noise.ActiveSummary();
                Assert.Contains(Lexicon.Get("audio.noise_surface.active.computer", verbosity,
                    ("list", Lexicon.Get("audio.noise_surface.active.waiting_pc_audio", verbosity, ("engine", "DeepFilterNet3")))), summary);
                Assert.Contains("Legacy NR", summary);
                Assert.DoesNotContain(f.Noise.LeverName(f.Noise.Levers("deepfilternet3", false)[0]).ToLowerInvariant(), summary);
            }
            f.Spoken.Verbosity = VerbosityLevel.Chatty;
            f.Radio.PcAudioOn = true;
            f.Settle();
            Assert.Contains("DeepFilterNet3, ", f.Noise.ActiveSummary());
        }
    }

    /// <summary>
    /// #1103's other not-running states: an engine chosen and on that the slot
    /// has not started (still building or warming, no receive audio yet, a failed
    /// start, Captured noise with no profile) reads "not running yet" (the
    /// existing words), not its settings; once it runs, its settings. An engine
    /// that cannot run here reads the same, as before.
    /// </summary>
    [Fact]
    public void An_engine_the_slot_has_not_started_reads_as_not_running_yet()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("deepfilternet3", "test");    // no audio fed: chosen, on, not yet built
        Assert.NotEqual("deepfilternet3", f.Pipeline.NeuralSlot.RunningEngineId);
        string waiting = Lexicon.Get("audio.noise_surface.active.waiting", VerbosityLevel.Chatty, ("engine", "DeepFilterNet3"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.active.computer", VerbosityLevel.Chatty, ("list", waiting)), f.Noise.ActiveSummary());
        f.Settle();
        Assert.Contains("DeepFilterNet3, ", f.Noise.ActiveSummary());
        Assert.DoesNotContain(waiting, f.Noise.ActiveSummary());

        // Unavailable: as before.
        f.Unavailable["nnr"] = Unavailability.HelperMissing;
        f.Noise.Toggle("nnr", "test");
        Assert.Contains(Lexicon.Get("audio.noise_surface.active.waiting", VerbosityLevel.Chatty, ("engine", "NNR")), f.Noise.ActiveSummary());
    }

    // ── Repair round 1 (Codex's check of track e3) ──

    private static float[] Buffer() => Enumerable.Repeat(0.1f, 960).ToArray();

    private static void CaptureProfile(NoiseFixture f)
    {
        f.Pipeline.StartNoiseSampling(1);
        for (int i = 0; i < 130 && !f.Pipeline.HasNoiseProfile; i++) f.Pipeline.Process(Buffer());
        Assert.True(f.Pipeline.HasNoiseProfile);
    }

    private static string WaitingSummary(NoiseFixture f, string id) =>
        Lexicon.Get("audio.noise_surface.active.waiting", VerbosityLevel.Chatty, ("engine", f.Noise.NameOf(id)));

    /// <summary>
    /// Codex's probe, kept: Captured noise holds a built bank under its id from
    /// the moment the slot exists, and keeps it after its profile is cleared, but
    /// with no profile the slot passes the audio through untouched. The summary
    /// read "On this computer: Captured noise, amount ..." in both cases. It now
    /// reads what the slot processed: "not running yet". The audio is checked
    /// byte for byte, so the test proves nothing was processed, and the cleared
    /// case first proves that something was.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Captured_noise_without_a_profile_reads_as_not_running_even_with_a_built_bank(bool clearAfterRunning)
    {
        using var f = new NoiseFixture();
        if (clearAfterRunning) CaptureProfile(f);
        f.Noise.Toggle("spectral_capture", "test");
        var audio = Buffer();
        if (clearAfterRunning)
        {
            f.Pipeline.Process(audio);
            Assert.Contains(audio, x => x != 0.1f);
            Assert.DoesNotContain(WaitingSummary(f, "spectral_capture"), f.Noise.ActiveSummary());
            f.Pipeline.ClearNoiseProfile();
        }
        Assert.False(f.Pipeline.HasNoiseProfile);
        Assert.Equal("spectral_capture", f.Pipeline.SpectralSlot.RunningEngineId);
        audio = Buffer();
        f.Pipeline.Process(audio);
        Assert.All(audio, x => Assert.Equal(0.1f, x));
        Assert.Contains(WaitingSummary(f, "spectral_capture"), f.Noise.ActiveSummary());
    }

    /// <summary>
    /// The other gates where the slot keeps an engine's id and processes nothing:
    /// the spectral slot pauses while a noise profile is captured, and an engine
    /// that becomes unavailable mid-run (a helper that died) passes audio through.
    /// Each reads "not running yet" while it lasts, and its settings again after.
    /// </summary>
    [Fact]
    public void A_paused_or_passing_through_engine_reads_as_not_running_yet()
    {
        using var f = new NoiseFixture();
        CaptureProfile(f);
        f.Noise.Toggle("spectral_capture", "test");
        f.Pipeline.Process(Buffer());
        Assert.DoesNotContain(WaitingSummary(f, "spectral_capture"), f.Noise.ActiveSummary());

        // Capturing a new profile: the spectral slot is paused, its id kept.
        f.Pipeline.StartNoiseSampling(1);
        var audio = Buffer();
        f.Pipeline.Process(audio);
        Assert.True(f.Pipeline.IsNoiseSampling);
        Assert.Equal("spectral_capture", f.Pipeline.SpectralSlot.RunningEngineId);
        Assert.All(audio, x => Assert.Equal(0.1f, x));
        Assert.Contains(WaitingSummary(f, "spectral_capture"), f.Noise.ActiveSummary());
        for (int i = 0; i < 130 && f.Pipeline.IsNoiseSampling; i++) f.Pipeline.Process(Buffer());
        f.Pipeline.Process(Buffer());
        Assert.DoesNotContain(WaitingSummary(f, "spectral_capture"), f.Noise.ActiveSummary());

        // An engine that dies mid-run: the id stays, the audio passes through.
        f.Noise.Toggle("deepfilternet3", "test");
        f.Settle();
        Assert.DoesNotContain(WaitingSummary(f, "deepfilternet3"), f.Noise.ActiveSummary());
        f.Dead["deepfilternet3"] = true;
        f.Pipeline.Process(Buffer());
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.RunningEngineId);
        Assert.Contains(WaitingSummary(f, "deepfilternet3"), f.Noise.ActiveSummary());
        f.Dead.TryRemove("deepfilternet3", out _);
        f.Pipeline.Process(Buffer());
        Assert.DoesNotContain(WaitingSummary(f, "deepfilternet3"), f.Noise.ActiveSummary());
    }

    /// <summary>
    /// Codex's probe, kept: the sibling check's one firing said one interrupt per
    /// sibling the radio turned off, so "The radio turned Standard NR off." was cut
    /// by "The radio turned Legacy NR off.". One firing is now one utterance, and
    /// it is queued behind whatever is being said, since nobody asked for it. A
    /// single sibling is the same, queued.
    /// </summary>
    [Fact]
    public void One_sibling_check_says_one_queued_utterance()
    {
        using (var f = new NoiseFixture())
        {
            f.Radio.Switches["radio_nr"] = true;
            f.Radio.Switches["radio_nrl"] = true;
            f.Noise.Toggle("radio_nrs", "test");
            f.Radio.Switches["radio_nr"] = false;
            f.Radio.Switches["radio_nrl"] = false;
            int from = f.Spoken.Said.Count;
            var said = OneAction(f, "one sibling check", () => f.Clock.RunAll());
            Assert.Empty(said);                                  // no interrupt at all
            Assert.Single(f.Spoken.Said.Skip(from));
            Assert.Contains(from, f.Spoken.SaidAfter);
            Assert.Equal(Lexicon.Get("audio.noise_surface.radio_turned_off", VerbosityLevel.Chatty, ("engine", "Standard NR")) + " "
                + Lexicon.Get("audio.noise_surface.radio_turned_off", VerbosityLevel.Chatty, ("engine", "Legacy NR")), f.Spoken.Last);
        }
        using (var f = new NoiseFixture())
        {
            f.Radio.Switches["radio_nr"] = true;
            f.Noise.Toggle("radio_nrs", "test");
            f.Radio.Switches["radio_nr"] = false;
            int from = f.Spoken.Said.Count;
            f.Clock.RunAll();
            Assert.Single(f.Spoken.Said.Skip(from));
            Assert.Contains(from, f.Spoken.SaidAfter);
            Assert.Equal(Lexicon.Get("audio.noise_surface.radio_turned_off", VerbosityLevel.Chatty, ("engine", "Standard NR")), f.Spoken.Last);
        }
    }

    /// <summary>
    /// Codex's control, kept: two quick actions are two utterances (the scope
    /// closes with each action), and at Critical a step whose every part is gated
    /// still hands the speaker one non-empty sentence at its own level, never a
    /// blank one and never promoted to Critical.
    /// </summary>
    [Fact]
    public void Quick_actions_stay_separate_and_a_wholly_gated_action_hands_over_one_nonempty_sentence()
    {
        using var f = new NoiseFixture();
        f.Noise.BeginSession();
        f.Spoken.Verbosity = VerbosityLevel.Critical;
        f.Noise.Step(+1, "test");
        Assert.Single(f.Spoken.Said);
        Assert.False(string.IsNullOrWhiteSpace(f.Spoken.Last));
        Assert.Equal(VerbosityLevel.Terse, f.Spoken.Said[0].Level);
        f.Spoken.Clear();
        f.Spoken.Verbosity = VerbosityLevel.Chatty;
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Toggle("rnnoise", "test");
        Assert.Equal(2, f.Spoken.Said.Count);
        Assert.Equal("RNNoise off.", f.Spoken.Last);
    }

    // ── Repair round 2 (Codex's second check of track e3) ──

    private sealed class RoundTwoPresetStore : INoisePresetStore
    {
        private AudioChainPresets _store = AudioChainPresets.CreateDefaults();
        public AudioChainPresets Load() => _store;
        public bool Save(AudioChainPresets store) { _store = store; return true; }
    }

    /// <summary>Standard NR on, Spectral NR turned on, and the radio turns Standard NR off: the sibling check queues its observation.</summary>
    private static string QueueSiblingNotice(NoiseFixture f)
    {
        f.Radio.Switches["radio_nr"] = true;
        f.Noise.Toggle("radio_nrs", "test");
        f.Radio.Switches["radio_nr"] = false;
        f.Clock.RunAll();
        return Lexicon.Get("audio.noise_surface.radio_turned_off", VerbosityLevel.Chatty, ("engine", "Standard NR"));
    }

    /// <summary>
    /// Codex's probe, kept, through the REAL arbiter: the queued "The radio turned
    /// Standard NR off." survived a preset that turned Standard NR back on (the
    /// preset retired only the helper's notice and spoke on its own subject), and
    /// was rescued about 0.63 s after the preset's summary, an "off" right after
    /// the opposite outcome. The sibling notice now has its own subject and an
    /// owner that retires it before the preset's sentence. The positive control
    /// shows the same arbiter DOES rescue it after an unrelated interrupt, so the
    /// instrument can see the defect.
    /// </summary>
    [Fact]
    public void A_queued_sibling_notice_is_not_rescued_after_a_preset_turns_that_engine_back_on()
    {
        using (var f = new NoiseFixture())
        {
            var speaker = new SilentArbiterSpeaker();
            typeof(NoiseController).GetField("_speaker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(f.Noise, speaker);
            try
            {
                f.Noise.PresetStore = new RoundTwoPresetStore();
                f.Radio.Switches["radio_nr"] = true;
                Assert.NotNull(f.Noise.SavePreset("Standard back", true, false, PresetCollision.Ask, "test"));
                string old = QueueSiblingNotice(f);
                Assert.Contains(speaker.Calls, c => c.Text == old && !c.Interrupt && !c.Salvaged);
                f.Noise.ApplyPreset("Standard back", "test");
                Assert.True(f.Radio.Switches["radio_nr"]);
                speaker.Replayed.Wait(2500);
                Assert.DoesNotContain(speaker.Calls, c => c.Text == old && c.Salvaged);
            }
            finally { speaker.Dispose(); }
        }
        // Positive control: the same notice, an interrupt that changes nothing it describes.
        var kept = new SilentArbiterSpeaker();
        try
        {
            kept.SayAfter("The radio turned Standard NR off.", SpeechSubject.NoiseRadioSibling, VerbosityLevel.Terse);
            kept.Say("level 50", SpeechSubject.NoiseLevel, VerbosityLevel.Terse);
            Assert.True(kept.Replayed.Wait(2500), "the arbiter never rescued the unretired sibling notice, so this instrument cannot see the defect");
        }
        finally { kept.Dispose(); }
    }

    /// <summary>
    /// Every change to the radio's noise state or the picture the sibling notice
    /// describes retires it, before that change's own sentence: a preset, Escape's
    /// revert, a radio switch from the layer, the slice the radio reads changing,
    /// a mode group entered, the 0 key, a computer engine switch, a disconnect.
    /// Only its own subject is retired, so no switch answer is ever discarded.
    /// </summary>
    [Theory]
    [InlineData("preset")]
    [InlineData("escape")]
    [InlineData("radio switch")]
    [InlineData("slice change")]
    [InlineData("mode group")]
    [InlineData("zero key")]
    [InlineData("computer switch")]
    [InlineData("disconnect")]
    public void Every_change_to_what_a_sibling_notice_described_retires_it_first(string change)
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.PresetStore = new RoundTwoPresetStore();
        f.Radio.Switches["radio_nr"] = true;
        f.Noise.SavePreset("Standard back", true, false, PresetCollision.Ask, "test");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("rnnoise", "test");
        QueueSiblingNotice(f);
        Assert.Empty(f.Spoken.Retired);
        int saidBefore = f.Spoken.Said.Count;
        switch (change)
        {
            case "preset": f.Noise.ApplyPreset("Standard back", "test"); break;
            case "escape": f.Noise.RestoreSnapshot(snap, "test"); break;
            case "radio switch": f.Noise.Toggle("radio_nrl", "test"); break;
            case "slice change": f.Radio.SliceIdentity = "slice B"; f.Noise.NoteActiveSlice(1); break;
            case "mode group": f.Radio.Mode = "CW"; f.Noise.OnModeChanged("CW"); break;
            case "zero key": f.Noise.ComputerOffAndBack("test"); break;
            case "computer switch": f.Noise.Toggle("nr4", "test"); break;
            case "disconnect": f.Noise.Detach(); break;
        }
        var retired = Assert.Single(f.Spoken.Retired);
        Assert.Equal(SpeechSubject.NoiseRadioSibling, retired.Subject);
        // Retiring it a second time does nothing: nothing is owed now.
        f.Noise.Toggle("radio_nrl", "test");
        Assert.Single(f.Spoken.Retired);
        Assert.True(f.Spoken.Said.Count >= saidBefore);
    }

    /// <summary>
    /// Codex's probe, kept: after a dead engine reads as not processing on a full
    /// buffer, a buffer too short to complete one engine frame made its id
    /// reappear, so the summary read it as working. Such a buffer proves nothing,
    /// so the last answer stands; and on a healthy engine it does not flicker.
    /// </summary>
    [Fact]
    public void A_buffer_that_completes_no_engine_frame_proves_nothing_either_way()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("deepfilternet3", "test");
        f.Settle();
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ProcessingEngineId);
        f.Pipeline.Process(new float[] { 0.1f, 0.1f });
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ProcessingEngineId);
        f.Dead["deepfilternet3"] = true;
        f.Pipeline.Process(Buffer());
        Assert.Equal("", f.Pipeline.NeuralSlot.ProcessingEngineId);
        f.Pipeline.Process(new float[] { 0.1f, 0.1f });
        Assert.Equal("", f.Pipeline.NeuralSlot.ProcessingEngineId);
        Assert.Contains(WaitingSummary(f, "deepfilternet3"), f.Noise.ActiveSummary());
    }

    /// <summary>
    /// Codex's control, kept: RNNoise, built with the slot, waits for its first
    /// buffer, then stays running while no audio arrives (no idle timeout calls a
    /// healthy engine "not running yet"), and PC audio off still wins.
    /// </summary>
    [Fact]
    public void RNNoise_waits_for_its_first_buffer_then_stays_running_while_idle()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        Assert.Contains(WaitingSummary(f, "rnnoise"), f.Noise.ActiveSummary());
        f.Pipeline.Process(Buffer());
        Assert.DoesNotContain(WaitingSummary(f, "rnnoise"), f.Noise.ActiveSummary());
        System.Threading.Thread.Sleep(50);
        Assert.DoesNotContain(WaitingSummary(f, "rnnoise"), f.Noise.ActiveSummary());
        f.Radio.PcAudioOn = false;
        Assert.Contains(Lexicon.Get("audio.noise_surface.active.waiting_pc_audio", VerbosityLevel.Chatty, ("engine", "RNNoise")), f.Noise.ActiveSummary());
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
            f.Noise.Adjust(f.Noise.Levers("nr4", false)[0], +1, "menu"); // #1019: NR4's own reduction is its first lever
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
