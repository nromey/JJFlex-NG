using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Radios.Speech;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Before a door keys a transmit slice the operator is not on, where it will
/// transmit (#863; Noel, 2026-11-02: "Say it, then go", and for the other
/// doors "Yes, all of them"). Ruled again 2026-11-03: once per split, by
/// verbosity, with tri-tones and a three-way setting; Check your audio speaks
/// it whatever the setting. Repair round 1 adds Codex's four blockers: the
/// notice gates the key with or without a count (B1), what was announced is
/// what keys (B2), a cut sentence is not heard (B3), and Loopback is a door (B4).
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TransmitSliceNoticeTests
{
    public TransmitSliceNoticeTests() => Lexicon.Load(Lexicon.Partitions);

    private static string Mhz(ulong hz) => (hz / 1_000_000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Selected A on 14.175, transmit B on 14.100.</summary>
    private static readonly SliceRoles Apart =
        new SliceRoles("A", 14_175_000UL, hasTransmit: true, "B", 14_100_000UL, same: false);
    private static readonly SliceRoles Together =
        new SliceRoles("A", 14_100_000UL, hasTransmit: true, "A", 14_100_000UL, same: true);

    // ── The words, by verbosity (ruled by Noel 2026-11-04 06:50) ─────────

    [Fact]
    public void Terse_and_off_say_the_slice_and_frequency_only()
    {
        Assert.Equal("B 14.100", TransmitSliceNotice.Wording(Apart, VerbosityLevel.Terse, Mhz));
        Assert.Equal("B 14.100", TransmitSliceNotice.Wording(Apart, VerbosityLevel.Critical, Mhz));
    }

    [Fact]
    public void Chatty_and_diagnostic_say_transmitting_on_slice_every_time()
    {
        Assert.Equal("Transmitting on slice B, 14.100",
            TransmitSliceNotice.Wording(Apart, VerbosityLevel.Chatty, Mhz));
        Assert.Equal("Transmitting on slice B, 14.100",
            TransmitSliceNotice.Wording(Apart, VerbosityLevel.Diagnostic, Mhz));
    }

    [Fact]
    public void On_the_selected_slice_or_with_no_transmit_slice_it_says_nothing()
    {
        Assert.Null(TransmitSliceNotice.Wording(Together, VerbosityLevel.Chatty, Mhz));
        Assert.Null(TransmitSliceNotice.Wording(default, VerbosityLevel.Chatty, Mhz));
    }

    // ── Once per split ───────────────────────────────────────────────────

    [Fact]
    public void A_split_is_spoken_once_and_again_only_when_it_changes()
    {
        Assert.True(TransmitSliceNotice.SplitIsNew(Apart, null));             // the first key
        Assert.False(TransmitSliceNotice.SplitIsNew(Apart, Apart));           // the same split stands
        var moved = new SliceRoles("A", 14_175_000UL, true, "B", 14_105_000UL, false);
        Assert.True(TransmitSliceNotice.SplitIsNew(moved, Apart));            // the transmit frequency moved
        var otherTx = new SliceRoles("A", 14_175_000UL, true, "C", 14_100_000UL, false);
        Assert.True(TransmitSliceNotice.SplitIsNew(otherTx, Apart));          // a different transmit slice
        var otherSel = new SliceRoles("D", 7_150_000UL, true, "B", 14_100_000UL, false);
        Assert.True(TransmitSliceNotice.SplitIsNew(otherSel, Apart));         // a different selected slice
        Assert.False(TransmitSliceNotice.SplitIsNew(Together, null));         // no split, nothing to say
    }

    [Fact]
    public void The_setting_decides_except_for_check_your_audio()
    {
        Assert.True(TransmitSliceNotice.Speaks(TransmitElsewhereNotice.SpeakAndTone, false, Apart, null));
        Assert.False(TransmitSliceNotice.Speaks(TransmitElsewhereNotice.SpeakAndTone, false, Apart, Apart));
        Assert.False(TransmitSliceNotice.Speaks(TransmitElsewhereNotice.ToneOnly, false, Apart, null));
        Assert.False(TransmitSliceNotice.Speaks(TransmitElsewhereNotice.Off, false, Apart, null));
        // Check your audio: whatever the setting, and every time.
        Assert.True(TransmitSliceNotice.Speaks(TransmitElsewhereNotice.Off, true, Apart, Apart));
        Assert.False(TransmitSliceNotice.Speaks(TransmitElsewhereNotice.Off, true, Together, null));
    }

    [Fact]
    public void Tones_play_at_every_key_elsewhere_unless_the_notice_is_off()
    {
        Assert.True(TransmitSliceNotice.PlaysTones(TransmitElsewhereNotice.SpeakAndTone, true, Apart));
        Assert.True(TransmitSliceNotice.PlaysTones(TransmitElsewhereNotice.ToneOnly, true, Apart));
        Assert.False(TransmitSliceNotice.PlaysTones(TransmitElsewhereNotice.Off, true, Apart));
        Assert.False(TransmitSliceNotice.PlaysTones(TransmitElsewhereNotice.SpeakAndTone, true, Together));
    }

    /// <summary>
    /// Ruled by Noel 2026-11-04 06:51: unchecking "Play chirp tones on
    /// transmit and receive" silences the three-note tones too, key-down and
    /// key-up, for push to talk and for a send that keys itself. Both key-ups
    /// play only when their key-down did (the controller's _keyedElsewhere,
    /// the send's KeyedElsewhere), so silencing the decision silences both.
    /// </summary>
    [Fact]
    public void Chirps_off_silences_both_three_note_tones_and_chirps_on_still_plays_them()
    {
        foreach (var setting in new[] { TransmitElsewhereNotice.SpeakAndTone, TransmitElsewhereNotice.ToneOnly })
        {
            Assert.False(TransmitSliceNotice.PlaysTones(setting, chirps: false, Apart));
            Assert.True(TransmitSliceNotice.PlaysTones(setting, chirps: true, Apart));   // the control
        }
        // Every door that plays them asks with the same PTT chirp switch.
        string ptt = Source("JJFlexWpf/PttSafetyController.cs");
        Assert.Contains("PlaysTones(_getRigControl(), _config.ChirpEnabled)", ptt);
        Assert.Contains("public bool ChirpEnabled => _config.ChirpEnabled;", ptt);
        string sender = Source("JJFlexWpf/VoiceMessageSender.cs");
        Assert.Contains("PttControllerSource?.Invoke()?.ChirpEnabled ?? true;", sender);
        Assert.Contains("TransmitSliceNotice.PlaysTones(TransmitSliceNotice.Setting, chirps, notice.Announced)", sender);
        // And the stop tones play only after their start tone did.
        Assert.Contains("if (elsewhere) EarconPlayer.TxElsewhereStopTone();", ptt);
        Assert.Contains("info.KeyedElsewhere = true;\n            EarconPlayer.TxElsewhereStartTone();", sender.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Ruled by Noel 2026-11-04 06:59: a door that refuses because the place
    /// changed while it waited says what changed. The transmit slice: his
    /// sentence. Only the transmit frequency: the orchestrator's DRAFT
    /// extension. The control: a notice that could not be announced, and a
    /// change of the selected slice alone, keep the not-announced sentence.
    /// </summary>
    [Fact]
    public void Each_changed_place_refuses_with_its_own_sentence()
    {
        var otherSlice = new SliceRoles("A", 14_175_000UL, hasTransmit: true, "C", 21_300_000UL, same: false);
        var movedFreq = new SliceRoles("A", 14_175_000UL, hasTransmit: true, "B", 14_105_000UL, same: false);
        var noTransmit = new SliceRoles("A", 14_175_000UL, hasTransmit: false, "", 0UL, same: false);
        var otherSelected = new SliceRoles("D", 3_800_000UL, hasTransmit: true, "B", 14_100_000UL, same: false);

        Assert.Equal("The transmit slice changed, so nothing was transmitted.",
            TransmitSliceNotice.RefusalForChangedPlace(Apart, otherSlice));
        Assert.Equal("The transmit slice changed, so nothing was transmitted.",
            TransmitSliceNotice.RefusalForChangedPlace(Apart, noTransmit));
        Assert.Equal("The transmit frequency changed, so nothing was transmitted.",
            TransmitSliceNotice.RefusalForChangedPlace(Apart, movedFreq));
        Assert.Equal("The transmit slice could not be announced, so nothing was transmitted.",
            TransmitSliceNotice.RefusalForChangedPlace(Apart, otherSelected));
        // Repair round 3: the selected slice alone has its OWN key, with the
        // same text for now, so Noel's answer is a lexicon edit only.
        Assert.Equal(Lexicon.Get("audio.ptt.selected_slice_changed"),
            TransmitSliceNotice.RefusalForChangedPlace(Apart, otherSelected));
        string notice = Source("Radios/TransmitSliceNotice.cs");
        int refusal = notice.IndexOf("public static string RefusalForChangedPlace(", StringComparison.Ordinal);
        int end = notice.IndexOf("public static void Retire(", refusal, StringComparison.Ordinal);
        string body = notice.Substring(refusal, end - refusal);
        Assert.Contains("audio.ptt.selected_slice_changed", body);
        Assert.DoesNotContain("audio.ptt.transmit_slice_not_heard", body);
        Assert.Equal("The transmit slice could not be announced, so nothing was transmitted.",
            Lexicon.Get("audio.ptt.transmit_slice_not_heard"));   // the unspoken notice keeps its own
    }

    /// <summary>
    /// Repair round 3 (Codex's second re-check F1): a cancelled check takes
    /// back its own notice, stops waiting for it at once, and says no refusal
    /// of its own; ending a start that never keyed does the same.
    /// </summary>
    [Fact]
    public void A_cancelled_check_takes_back_its_own_notice_and_stops_waiting_for_it()
    {
        string check = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
        int cancel = check.IndexOf("private void CancelCheckStart()", StringComparison.Ordinal);
        int cancelEnd = check.IndexOf("\n    }", cancel, StringComparison.Ordinal);
        Assert.Contains("TransmitSliceNotice.Retire(_transmitSliceNotice,", check.Substring(cancel, cancelEnd - cancel));
        string countdown = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Countdown.cs");
        Assert.Contains("TransmitSliceNotice.HeardAsync(_transmitSliceNotice, () => !_closing && !_checkStarts.IsCancelled)", countdown);
        Assert.Contains("if (!_closing && !_checkStarts.IsCancelled) TransmitSliceNotHeard();", countdown);
        int end = countdown.IndexOf("private void EndStartCountdown()", StringComparison.Ordinal);
        int retire = countdown.IndexOf("TransmitSliceNotice.Retire(_transmitSliceNotice,", end, StringComparison.Ordinal);
        int clear = countdown.IndexOf("_transmitSliceNotice = null;", end, StringComparison.Ordinal);
        Assert.True(end > 0 && retire > end && clear > retire, "EndStartCountdown forgets its notice without taking it back");
    }

    [Fact]
    public void Every_door_that_refuses_a_changed_place_says_what_changed()
    {
        string sender = Source("JJFlexWpf/VoiceMessageSender.cs");
        Assert.Equal(2, Count(sender, "Dialogs.CheckYourAudioDialog.RefuseTransmit(changed);"));
        string check = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
        Assert.Contains("if (placeChanged) RefuseTransmit(placeRefusal);", check);
        // The not-heard doors keep the not-announced sentence.
        string countdown = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Countdown.cs");
        Assert.Contains("if (!_closing && !_checkStarts.IsCancelled) TransmitSliceNotHeard();", countdown);
    }

    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    // ── B2: what was announced is what keys ──────────────────────────────

    [Fact]
    public void A_changed_place_that_would_need_a_sentence_is_refused()
    {
        var moved = new SliceRoles("A", 14_175_000UL, true, "B", 14_105_000UL, false);
        // Announced B on 14.100; the transmit frequency moved during the wait.
        Assert.False(TransmitSliceNotice.StillTheAnnouncedPlace(Apart, moved, mandatory: false,
            TransmitElsewhereNotice.SpeakAndTone, lastSpoken: Apart));
        // Positive control: unchanged, it keys.
        Assert.True(TransmitSliceNotice.StillTheAnnouncedPlace(Apart, Apart, false,
            TransmitElsewhereNotice.SpeakAndTone, Apart));
    }

    [Fact]
    public void A_silent_start_that_became_a_split_during_the_wait_is_refused()
    {
        // Selected and transmit both A at the press: nothing said. The operator
        // selects B during the count: keying now would transmit elsewhere unannounced.
        var nowApart = new SliceRoles("B", 7_150_000UL, true, "A", 14_100_000UL, false);
        Assert.False(TransmitSliceNotice.StillTheAnnouncedPlace(Together, nowApart, mandatory: true,
            TransmitElsewhereNotice.Off, lastSpoken: null));
        Assert.False(TransmitSliceNotice.StillTheAnnouncedPlace(Together, nowApart, mandatory: false,
            TransmitElsewhereNotice.SpeakAndTone, lastSpoken: null));
    }

    [Fact]
    public void A_change_nobody_needed_telling_about_still_keys()
    {
        var nowApart = new SliceRoles("B", 7_150_000UL, true, "A", 14_100_000UL, false);
        // Tone only: the door would have said nothing anyway.
        Assert.True(TransmitSliceNotice.StillTheAnnouncedPlace(Together, nowApart, false,
            TransmitElsewhereNotice.ToneOnly, null));
        // The operator moved onto the transmit slice: nothing is owed.
        Assert.True(TransmitSliceNotice.StillTheAnnouncedPlace(Apart, Together, true,
            TransmitElsewhereNotice.SpeakAndTone, null));
        // A check's own listening slice became the selected one; the transmit place is unchanged.
        var withEars = new SliceRoles("C", 14_100_000UL, true, "A", 14_100_000UL, false, selectedIsDiagnostic: true);
        Assert.True(TransmitSliceNotice.StillTheAnnouncedPlace(Together, withEars, true,
            TransmitElsewhereNotice.SpeakAndTone, null));
    }

    // ── The wait ─────────────────────────────────────────────────────────

    private sealed class FakeWords : ICountdownWords
    {
        public bool Failed { get; set; }
        public bool Started { get; set; }
        public bool Finished { get; set; }
    }

    [Fact]
    public async Task Then_go_waits_for_the_reader_to_finish()
    {
        var words = new FakeWords();
        int polls = 0;
        Task Delay(int ms) { if (++polls == 3) words.Finished = true; return Task.CompletedTask; }

        Assert.True(await TransmitSliceNotice.HeardAsync(words, () => true, Delay));
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task Nothing_to_say_goes_at_once()
    {
        Assert.True(await TransmitSliceNotice.HeardAsync((ICountdownWords?)null, () => true, _ => throw new InvalidOperationException("waited")));
        Assert.True(await TransmitSliceNotice.HeardAsync((TransmitSliceNotice.Notice?)null, () => true, _ => throw new InvalidOperationException("waited")));
    }

    [Fact]
    public async Task Words_the_reader_never_got_key_nothing()
    {
        Assert.False(await TransmitSliceNotice.HeardAsync(new FakeWords { Failed = true }, () => true, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task A_cancelled_door_keys_nothing()
    {
        bool wanted = true;
        var words = new FakeWords();
        Task Delay(int ms) { wanted = false; return Task.CompletedTask; }
        Assert.False(await TransmitSliceNotice.HeardAsync(words, () => wanted, Delay));
    }

    [Fact]
    public async Task Words_that_never_finish_stop_at_the_ceiling()
    {
        int polls = 0;
        Assert.False(await TransmitSliceNotice.HeardAsync(new FakeWords(), () => true,
            _ => { polls++; return Task.CompletedTask; }, ceilingMs: 10 * TransmitSliceNotice.PollMs));
        Assert.Equal(10, polls);
    }

    // ── B3: heard means heard whole, through the real speech layer ───────

    internal sealed class Reader
    {
        public readonly FakeSpeechClock Clock = new();
        public readonly SpeechArbiter Arbiter;
        public readonly CountdownSpeech Speech;
        public long LastTicket;
        public string LastText = "";

        public Reader()
        {
            Arbiter = new SpeechArbiter(Clock, () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    LastTicket++;
                    LastText = message;
                    return SpeechHandoff.TrackedAs(LastTicket);
                },
                () => { }, (m, l, i, o) => { }, new SpeechRateModel(), () => null, (t, r) => false);
            Speech = new CountdownSpeech(Arbiter);
        }
    }

    [Fact]
    public void A_sentence_said_whole_is_heard()
    {
        var r = new Reader();
        ICountdownWords words = r.Speech.SayWhole("Transmitting on slice B, 14.100", SpeechSubject.TransmitSliceNotice);
        Assert.False(words.Finished);
        r.Arbiter.OnOutcome(r.LastTicket, r.LastText, SpeechOutcome.Completed(4, 4, 900));
        Assert.True(words.Finished);
        Assert.False(words.Failed);
    }

    [Theory]
    [InlineData(true)]    // cut by us (Control, or our own interrupt)
    [InlineData(false)]   // cut by something we cannot name (a focus change)
    public void A_sentence_cut_after_its_first_word_is_not_heard(bool byUs)
    {
        var r = new Reader();
        ICountdownWords words = r.Speech.SayWhole("Transmitting on slice B, 14.100", SpeechSubject.TransmitSliceNotice);
        r.Arbiter.OnOutcome(r.LastTicket, r.LastText, SpeechOutcome.Cancelled(1, 4, byUs, 200));
        Assert.False(words.Finished);
        Assert.True(words.Failed);
    }

    [Fact]
    public void The_countdowns_own_one_word_pieces_still_count_a_cut_as_heard()
    {
        // Positive control that the whole-only rule is the notice's alone: a
        // number of the count cut part-way was heard, as before.
        var r = new Reader();
        ICountdownWords number = r.Speech.Say("3", onlyWarning: true);
        r.Arbiter.OnOutcome(r.LastTicket, r.LastText, SpeechOutcome.Cancelled(1, 1, true, 100));
        Assert.True(number.Finished);
        Assert.False(number.Failed);
    }

    // ── The doors (source) ───────────────────────────────────────────────

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Check_your_audio_waits_for_the_notice_whether_or_not_it_counts()
    {
        // B1: the notice's own gate, after the count, in every branch.
        string countdown = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Countdown.cs");
        int lets = countdown.IndexOf("private async Task<bool> CountdownLetsKeyAsync()", StringComparison.Ordinal);
        Assert.True(lets > 0);
        int ready = countdown.IndexOf("run.ReadyToKeyAsync()", lets, StringComparison.Ordinal);
        int heard = countdown.IndexOf("TransmitSliceNotice.HeardAsync(_transmitSliceNotice", lets, StringComparison.Ordinal);
        int end = countdown.IndexOf("private bool StillTheAnnouncedPlace(out string? refusal)", lets, StringComparison.Ordinal);
        Assert.True(ready > lets && heard > ready && heard < end, "the notice must be waited for after the count, outside its branch");
        string between = countdown.Substring(ready, heard - ready);
        Assert.DoesNotContain("return go;", between);
        // Mandatory for the check; every method that keys, Loopback included (B4).
        Assert.Contains("TransmitSliceNotice.Prepare(_rig ?? RigSource?.Invoke(), \"Check your audio, \" + method, mandatory: true)", countdown);
        Assert.Contains("method != AudioCheckListenMethods.ComputerMic;", countdown);
        Assert.DoesNotContain("method != AudioCheckListenMethods.Loopback", countdown);
    }

    [Fact]
    public void Every_door_reads_the_place_again_at_the_key()
    {
        // B2, the doors: Check your audio inside its final admission, the box
        // send before it owns the carrier, the box-off send before it begins.
        string check = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
        int tryRun = check.IndexOf("bool admitted = start.TryRun(() =>", StringComparison.Ordinal);
        int place = check.IndexOf("_owner.StillTheAnnouncedPlace(out placeRefusal)", tryRun, StringComparison.Ordinal);
        int key = check.IndexOf("_ptt.KeyForCheck();", tryRun, StringComparison.Ordinal);
        Assert.True(tryRun > 0 && place > tryRun && key > place, "the check keys before reading the place again");

        string sender = Source("JJFlexWpf/VoiceMessageSender.cs");
        int core = sender.IndexOf("private static async Task<bool> BeginBoxCoreAsync(", StringComparison.Ordinal);
        int prep = sender.IndexOf("TransmitSliceNotice.Prepare(rig, \"the box send of \"", core, StringComparison.Ordinal);
        int wait = sender.IndexOf("TransmitSliceNotice.HeardAsync(sliceNotice,", core, StringComparison.Ordinal);
        int again = sender.IndexOf("TransmitSliceNotice.StillTheAnnouncedPlace(sliceNotice, out string? changed)", core, StringComparison.Ordinal);
        int own = sender.IndexOf("BoxSendOwnedRig.Own(", core, StringComparison.Ordinal);
        Assert.True(prep > core && wait > prep && again > wait && own > again, "box send: said, waited for, read again, then keyed");

        int after = sender.IndexOf("private static async Task BeginAfterNoticeAsync(", StringComparison.Ordinal);
        int afterWait = sender.IndexOf("TransmitSliceNotice.HeardAsync(notice,", after, StringComparison.Ordinal);
        int afterAgain = sender.IndexOf("TransmitSliceNotice.StillTheAnnouncedPlace(notice, out string? changed)", after, StringComparison.Ordinal);
        int afterBegin = sender.IndexOf("BeginAndLaunch(rig, info, info.Label, loaded);", after, StringComparison.Ordinal);
        Assert.True(after > 0 && afterWait > after && afterAgain > afterWait && afterBegin > afterAgain,
            "box-off send: said, waited for, read again, then keyed");

        // Push to talk in a dialog: go, then say (ruled), once per split.
        string main = Source("JJFlexWpf/MainWindow.xaml.cs");
        int door = main.IndexOf("private void PttEdgeFromOutsideHome(bool down, PttDoor door)", StringComparison.Ordinal);
        Assert.True(main.IndexOf("Radios.TransmitSliceNotice.SayAfterKey(RigControl", door, StringComparison.Ordinal) > door);
    }

    [Fact]
    public void The_controller_plays_the_three_note_tones_for_a_key_elsewhere()
    {
        string ptt = Source("JJFlexWpf/PttSafetyController.cs");
        Assert.Contains("_keyedElsewhere = Radios.TransmitSliceNotice.PlaysTones(_getRigControl(), _config.ChirpEnabled);", ptt);
        Assert.Contains("if (_keyedElsewhere) EarconPlayer.TxElsewhereStartTone();", ptt);
        Assert.Contains("if (elsewhere) EarconPlayer.TxElsewhereStopTone();", ptt);
        Assert.DoesNotContain("if (_config.ChirpEnabled) EarconPlayer.TxStartTone();\n                _updateStatusDisplay", ptt.Replace("\r\n", "\n"));
    }
}
