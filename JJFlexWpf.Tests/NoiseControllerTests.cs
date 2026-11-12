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
        Assert.Equal(SpeechSubject.NoiseUnavailable, f.Spoken.Said[^1].Subject);
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
            Assert.DoesNotContain(refusal, f.Spoken.SaidAfter);   // the answer to a press still speaks at once

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
    /// swap first, under the restore's subject; neither cuts the other.
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

        var said = OneAction(f, "entering CW", () => Mode(f, "CW"));

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

        var said = OneAction(f, "leaving CW", () => Mode(f, "USB"));

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
            OneAction(f, "first time in CW", () => Mode(f, "CW"));
            OneAction(f, "back to voice", () => Mode(f, "USB"));
            OneAction(f, "CW again", () => Mode(f, "CW"));
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
            OneAction(f, "digital", () => Mode(f, "DIGU"));
            OneAction(f, "voice from digital", () => Mode(f, "USB"));
            OneAction(f, "CW from voice", () => Mode(f, "CW"));
            OneAction(f, "the neural slot off", () => f.Noise.SetSlotOn(DenoiseFamily.Neural, false, "test"));
            OneAction(f, "the neural slot on in CW", () => f.Noise.SetSlotOn(DenoiseFamily.Neural, true, "test"));
            OneAction(f, "PC audio off, then RNNoise", () => { f.Radio.PcAudioOn = false; f.Noise.Toggle("rnnoise", "test"); });
            OneAction(f, "PC audio off, then NNR", () => f.Noise.Toggle("nnr", "test"));
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
