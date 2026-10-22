#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #571: for five seconds after a reflected-power cut, a press that would
    /// key the transmitter is refused and repeats why; a change to the
    /// transmit setup lifts the hold at once; VOX is paused by setting and put
    /// back on every exit the app controls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven with an injected clock and fake VOX levers, so the window, every
    /// signal, every restore path and the words are exercised with no radio
    /// and no dispatcher. Speech goes through the REAL
    /// <see cref="ScreenReaderOutput"/> with render off and the transcript on,
    /// so "delivered at Off" is read from what reached the diverted backend,
    /// past the verbosity gate, not from a call made before it.
    /// </para>
    /// <para>
    /// What this cannot see: the controller's own doors (those are
    /// <c>JJFlexWpf.Tests.LockSafetyHoldTests</c>, driving the real
    /// controller), the rig's VOX write, and RF. The rig side is in
    /// <c>CutHoldRigDoorsTests</c> and pinned from source in
    /// <c>LockSafetySourcePinTests</c>.
    /// </para>
    /// <para>
    /// Expected sentences are LITERALS here, on purpose: this class is in the
    /// speech-statics collection, so it may not touch the lexicon statics
    /// itself (the isolation test refuses a class that names them without the
    /// RadioConfig collection); the production class under test loads what it
    /// needs. A wording change therefore fails here by design, which is where
    /// Noel's review of the draft prose will land.
    /// </para>
    /// </remarks>
    [Collection(SpeechOutputStaticsCollection.Name)]
    public sealed class ReflectedCutHoldTests : IDisposable
    {
        private readonly string _path;
        private readonly VerbosityLevel _verbosityBefore;
        private long _now = 1_000_000;
        private int _refusalTones;

        // A plausible reading for the share rung: 17.8 W forward, 10.3 W back,
        // the 2026-10-21 20:43 cut exactly (58 percent).
        private static readonly TransmitPowerReading ShareCut =
            new TransmitPowerReading(17.8f, 10.3f, skewMilliseconds: 4f, ageMilliseconds: 20f, commandedWatts: 25);
        private static readonly TransmitPowerReading WattsCut =
            new TransmitPowerReading(100f, 13.2f, skewMilliseconds: 4f, ageMilliseconds: 20f, commandedWatts: 100);

        private static ReflectedCutHold.TransmitSetup Setup(
            string antenna = "ANT2", ulong hz = 14_250_000, int power = 25, int tunePower = 10,
            bool tuning = false, string amps = "", string mode = "USB")
            => new ReflectedCutHold.TransmitSetup(antenna, hz, power, tunePower, tuning, amps, mode);

        /// <summary>A radio with two independent VOX-class settings, keyed by the mode the test says it is in.</summary>
        private sealed class FakeRadio
        {
            public bool SimpleVox;
            public bool CwBreakIn;
            public string Mode = "USB";
            public bool Present = true;
            public bool LoopRunning = true;
            public readonly List<(FlexBase.VoxSetting Which, bool On, bool Immediate)> Writes = new();
            public int Tones;

            public FlexBase.VoxSetting CurrentIfOn()
            {
                if (Mode == "CW") return CwBreakIn ? FlexBase.VoxSetting.CwBreakIn : FlexBase.VoxSetting.None;
                return SimpleVox ? FlexBase.VoxSetting.SimpleVox : FlexBase.VoxSetting.None;
            }

            public bool Write(FlexBase.VoxSetting which, bool on, bool immediate)
            {
                if (!Present) return false;
                if (!immediate && !LoopRunning) return false;
                Writes.Add((which, on, immediate));
                if (which == FlexBase.VoxSetting.SimpleVox) SimpleVox = on; else CwBreakIn = on;
                return true;
            }

            public ReflectedCutHold.VoxLevers Levers() => new ReflectedCutHold.VoxLevers(CurrentIfOn, Write, () => Tones++);
        }

        public ReflectedCutHoldTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-cut-hold-tests");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
            _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.ResetTransientSpeechStateForTest();
            AssertTheDeskIsSafe();
        }

        public void Dispose()
        {
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
            try { File.Delete(_path); } catch { }
        }

        private static void AssertTheDeskIsSafe()
        {
            Assert.False(OutputChannelRecorder.RenderEnabled, "Refusing: render is on, so speech could reach the screen reader");
            string backend = ScreenReaderOutput.BackendName;
            Assert.True(backend == "diverted" || backend == "none",
                "Refusing: the live speech backend is '" + backend + "', which could reach the screen reader");
        }

        private JsonElement[] Speech()
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("event").GetString() == "speech")
                .ToArray();
        }

        /// <summary>Delivered: the utterance passed the verbosity gate, unsuppressed.</summary>
        private bool Delivered(string text) => Speech().Any(e =>
            e.GetProperty("text").GetString() == text
            && !e.GetProperty("gated").GetBoolean()
            && !e.GetProperty("suppressed").GetBoolean());

        private bool Gated(string text) => Speech().Any(e =>
            e.GetProperty("text").GetString() == text && e.GetProperty("gated").GetBoolean());

        private ReflectedCutHold Begin(FakeRadio? radio = null, ReflectedCutHold.TransmitSetup? at = null,
                                       TransmitSafety.ReflectedCut rung = TransmitSafety.ReflectedCut.Share,
                                       string antenna = "ANT2")
        {
            var hold = new ReflectedCutHold(() => _now);
            var reading = rung == TransmitSafety.ReflectedCut.Watts ? WattsCut : ShareCut;
            hold.Begin(rung, reading, antenna, at ?? Setup(), _now, radio?.Levers(), refusalTone: () => _refusalTones++);
            return hold;
        }

        private const string VoxNote = "VOX is paused for 5 seconds.";

        // ──────────────────────────────────────────────────────────────────
        //  The window
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Five_seconds_fixed_as_ruled()
        {
            Assert.Equal(5, ReflectedCutHold.HoldSeconds);
            Assert.Equal(5000, ReflectedCutHold.HoldMs);
        }

        [Fact]
        public void A_press_inside_the_window_is_refused_and_a_press_after_it_is_not()
        {
            var hold = Begin();
            Assert.True(hold.Holding);

            _now += 149;   // the 2026-10-21 press, 149 ms after the cut
            Assert.True(hold.RefusePress("the lock key", _now, () => true));
            Assert.Equal(1, hold.RefusedPresses);
            Assert.Equal(1, _refusalTones);
            Assert.True(hold.Holding);

            _now += ReflectedCutHold.HoldMs;   // well past the window
            Assert.False(hold.RefusePress("the lock key", _now, () => true));
            Assert.False(hold.Holding);
            Assert.Equal(ReflectedCutHold.EndReason.Expired, hold.LastEnd);
            Assert.Equal(1, hold.RefusedPresses);   // the second press was not a refusal
        }

        [Fact]
        public void The_window_expires_at_exactly_five_seconds_not_before()
        {
            var hold = Begin();
            _now += ReflectedCutHold.HoldMs - 1;
            Assert.True(hold.RefusePress("push to talk", _now, () => true), "4,999 ms in is still inside the window");
            _now += 1;
            Assert.False(hold.RefusePress("push to talk", _now, () => true), "5,000 ms in is outside it");
            Assert.Equal(ReflectedCutHold.EndReason.Expired, hold.LastEnd);
        }

        [Fact]
        public void The_tick_notices_expiry_with_no_press_at_all()
        {
            // VOX must come back on time even if nobody presses anything.
            var radio = new FakeRadio { SimpleVox = true };
            var hold = Begin(radio);
            Assert.Equal(new[] { (FlexBase.VoxSetting.SimpleVox, false, false) }, radio.Writes);

            _now += ReflectedCutHold.HoldMs - ReflectedCutHold.SignalPollMs;
            Assert.False(hold.Tick(Setup(), _now));
            Assert.Single(radio.Writes);   // VOX did not come back early

            _now += ReflectedCutHold.SignalPollMs;
            Assert.True(hold.Tick(Setup(), _now));
            Assert.Equal(new[] { (FlexBase.VoxSetting.SimpleVox, false, false), (FlexBase.VoxSetting.SimpleVox, true, false) }, radio.Writes);
            Assert.True(radio.SimpleVox);
            Assert.False(hold.VoxPaused);
        }

        [Fact]
        public void Nothing_is_refused_when_no_hold_is_in_force()
        {
            var hold = new ReflectedCutHold(() => _now);
            Assert.False(hold.RefusePress("the lock key", _now, () => true));
            Assert.False(hold.Tick(Setup(), _now));
            Assert.Empty(Speech());
        }

        // ──────────────────────────────────────────────────────────────────
        //  The change signals, each lifting the hold at once
        // ──────────────────────────────────────────────────────────────────

        public static TheoryData<string, ReflectedCutHold.TransmitSetup> Changes => new()
        {
            { "the transmit antenna changed", Setup(antenna: "ANT1") },
            { "the transmit frequency changed", Setup(hz: 14_250_500) },
            { "the transmit frequency changed", Setup(hz: 7_150_000) },          // a band change moves it
            { "the transmit frequency changed", Setup(hz: 0) },                  // the TX slice went away
            { "the RF power changed", Setup(power: 5) },
            { "the tune power changed", Setup(tunePower: 5) },
            { "a tune started", Setup(tuning: true) },
            { "the amplifier changed", Setup(amps: "0x1=Standby") },             // an amplifier appeared
        };

        [Theory]
        [MemberData(nameof(Changes))]
        public void A_changed_transmit_setup_lifts_the_hold_and_the_next_press_keys(string why, ReflectedCutHold.TransmitSetup now)
        {
            var hold = Begin();
            _now += 300;
            Assert.True(hold.Tick(now, _now));
            Assert.False(hold.Holding);
            Assert.Equal(ReflectedCutHold.EndReason.Lifted, hold.LastEnd);
            Assert.StartsWith(why, hold.LastEndDetail);

            Assert.False(hold.RefusePress("the lock key", _now, () => true), "a press after the change must key as normal");
            Assert.Equal(0, hold.RefusedPresses);
        }

        [Fact]
        public void An_unchanged_setup_does_not_lift_it()
        {
            var hold = Begin();
            _now += 300;
            Assert.False(hold.Tick(Setup(), _now));
            Assert.True(hold.Holding);
            Assert.Null(ReflectedCutHold.WhatChanged(Setup(), Setup()));
        }

        [Fact]
        public void A_tune_that_was_already_running_at_the_cut_is_not_a_change_but_its_ending_is_not_either()
        {
            var hold = Begin(at: Setup(tuning: true));
            _now += 300;
            Assert.False(hold.Tick(Setup(tuning: false), _now));
            Assert.True(hold.Holding);
        }

        [Fact]
        public void A_mode_change_is_not_a_change_that_lifts()
        {
            // A mode change clears no antenna fault (Noel's list: antenna,
            // power, band or frequency, tuner, amplifier). It re-pauses VOX
            // instead; see the VOX section.
            var hold = Begin();
            _now += 300;
            Assert.False(hold.Tick(Setup(mode: "CW"), _now));
            Assert.True(hold.Holding);
            Assert.Null(ReflectedCutHold.WhatChanged(Setup(mode: "USB"), Setup(mode: "CW")));
        }

        [Theory]
        [InlineData("TXAntennaName", true)]
        [InlineData("TXAntenna", true)]
        [InlineData("XmitPower", true)]
        [InlineData("TunePower", true)]
        [InlineData("Vox", false)]            // VOX cannot clear an antenna fault
        [InlineData("MicGain", false)]
        [InlineData("TXFilterLow", false)]
        [InlineData("NoiseReduction", false)]
        public void The_operators_own_setting_change_lifts_the_hold_only_for_what_could_clear_the_fault(string what, bool lifts)
        {
            var hold = Begin();
            _now += 300;
            hold.OperatorChanged(what, _now);
            Assert.Equal(!lifts, hold.Holding);
            if (lifts)
            {
                Assert.Equal(ReflectedCutHold.EndReason.Lifted, hold.LastEnd);
                Assert.StartsWith("you changed", hold.LastEndDetail);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  An app write's echo is not the operator's repair (Astra B2)
        // ──────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("XmitPower", "25", 25, 10, "ANT2")]        // the Audio Check putting its saved power back
        [InlineData("TunePower", "40", 25, 40, "ANT2")]
        [InlineData("TXAntennaName", "ANT1", 25, 10, "ANT1")]
        public void The_apps_own_write_moves_the_baseline_so_its_echo_does_not_lift(string what, string value, int echoPower, int echoTunePower, string echoAntenna)
        {
            // Codex's B2 case: power reduced to 5 W for an Audio Check, the cut
            // lands at 5 W, the check restores 25 W as an app write, the radio
            // echoes 25 W. Before this, the echo differed from the 5 W baseline
            // and lifted the hold although the operator repaired nothing.
            var hold = Begin(at: Setup(power: 5));
            _now += 200;
            Assert.True(hold.RefusePress("the lock key", _now, () => true));

            hold.AppWrote(what, value);
            _now += 300;
            // The echo of the app's write, and no other change, except that
            // the power baseline was 5 and the restore case echoes 25.
            var echo = Setup(antenna: echoAntenna, power: what == "XmitPower" ? echoPower : 5, tunePower: echoTunePower);
            Assert.False(hold.Tick(echo, _now), "the app write's echo lifted the hold");
            Assert.True(hold.Holding);
            Assert.True(hold.RefusePress("the lock key", _now, () => true), "the press after the echo keyed");
        }

        [Fact]
        public void After_the_apps_write_the_operators_own_later_change_still_lifts()
        {
            // The positive control for the re-baseline: it moves the baseline
            // to the written value and nothing more. A different value from
            // the operator afterwards still counts.
            var hold = Begin(at: Setup(power: 5));
            hold.AppWrote("XmitPower", "25");
            _now += 300;
            Assert.False(hold.Tick(Setup(power: 25), _now));
            _now += 300;
            Assert.True(hold.Tick(Setup(power: 50), _now));
            Assert.StartsWith("the RF power changed from 25 to 50", hold.LastEndDetail);
        }

        [Fact]
        public void An_app_write_of_a_field_the_hold_does_not_snapshot_changes_nothing()
        {
            var hold = Begin();
            hold.AppWrote("MicGain", "40");
            hold.AppWrote("XmitPower", "not a number");
            _now += 300;
            Assert.False(hold.Tick(Setup(), _now));
            Assert.True(hold.Tick(Setup(power: 24), _now));
        }

        // ──────────────────────────────────────────────────────────────────
        //  The amplifier: what counts and what the cut itself causes
        // ──────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("0x1=TransmitA", "0x1=Idle", false)]        // the amplifier following the unkey: NOT a change
        [InlineData("0x1=TransmitB", "0x1=Idle", false)]
        [InlineData("0x1=Idle", "0x1=Standby", true)]            // the operator put it in standby
        [InlineData("0x1=TransmitA", "0x1=Standby", true)]
        [InlineData("0x1=Idle", "0x1=Fault", true)]
        [InlineData("0x1=Standby", "0x1=Idle", true)]            // brought out of standby
        [InlineData("0x1=Idle", "", true)]                       // the amplifier went away
        [InlineData("", "0x1=Idle", true)]                       // one appeared
        [InlineData("0x1=Idle", "0x2=Idle", true)]               // a different box
        [InlineData("0x1=Idle;0x2=TransmitA", "0x1=Idle;0x2=Idle", false)]
        [InlineData("", "", false)]
        public void The_amplifier_signature_ignores_only_the_transition_the_cut_causes(string atCut, string now, bool changed)
        {
            Assert.Equal(changed, ReflectedCutHold.AmplifierSignatureChanged(atCut, now));
        }

        // ──────────────────────────────────────────────────────────────────
        //  VOX, by setting (Astra B5), restored on every exit (B3)
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void VOX_on_at_the_cut_is_switched_off_told_once_and_comes_back_at_expiry_silently()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
            var radio = new FakeRadio { SimpleVox = true };
            var hold = Begin(radio);

            Assert.Equal(new[] { (FlexBase.VoxSetting.SimpleVox, false, false) }, radio.Writes);
            Assert.True(hold.VoxPaused);
            Assert.Equal(new[] { FlexBase.VoxSetting.SimpleVox }, hold.PausedSettings);
            Assert.Equal(1, radio.Tones);
            Assert.True(Delivered(VoxNote), "the VOX-paused note was not delivered at Terse");

            int spokenBefore = Speech().Length;
            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Equal(new[] { (FlexBase.VoxSetting.SimpleVox, false, false), (FlexBase.VoxSetting.SimpleVox, true, false) }, radio.Writes);
            Assert.Equal(spokenBefore, Speech().Length);   // nothing said when VOX comes back
            Assert.Equal(1, radio.Tones);
        }

        [Fact]
        public void In_CW_it_is_break_in_that_is_paused_and_restored_not_SimpleVOX()
        {
            var radio = new FakeRadio { Mode = "CW", CwBreakIn = true, SimpleVox = true };
            var hold = Begin(radio, at: Setup(mode: "CW"));
            Assert.Equal(new[] { (FlexBase.VoxSetting.CwBreakIn, false, false) }, radio.Writes);
            Assert.True(radio.SimpleVox, "SimpleVOX is the other mode's setting and must be left alone");
            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(mode: "CW"), _now);
            Assert.Equal((FlexBase.VoxSetting.CwBreakIn, true, false), radio.Writes.Last());
            Assert.True(radio.CwBreakIn);
        }

        [Fact]
        public void The_restore_writes_the_setting_that_was_switched_off_even_after_a_mode_change()
        {
            // Astra B5: the cut lands in USB and pauses SimpleVOX; the operator
            // moves to CW inside the window. A restore through rig.Vox would
            // have written CWBreakIn=true and left SimpleVOX off. The radio's
            // break-in is OFF here, so the mode change pauses nothing new.
            var radio = new FakeRadio { Mode = "USB", SimpleVox = true, CwBreakIn = false };
            var hold = Begin(radio);
            radio.Mode = "CW";
            _now += 300;
            Assert.False(hold.Tick(Setup(mode: "CW"), _now));
            Assert.Equal(new[] { FlexBase.VoxSetting.SimpleVox }, hold.PausedSettings);

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(mode: "CW"), _now);
            Assert.Equal((FlexBase.VoxSetting.SimpleVox, true, false), radio.Writes.Last());
            Assert.True(radio.SimpleVox);
            Assert.False(radio.CwBreakIn, "break-in was off before and must not have been switched on");
        }

        [Fact]
        public void A_mode_change_inside_the_window_pauses_the_new_modes_setting_too_and_both_come_back()
        {
            // Astra B5, the other half: break-in IS on in CW, so after the
            // mode change the radio could key on CW; the hold pauses it too,
            // and the note is given once, not twice.
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
            var radio = new FakeRadio { Mode = "USB", SimpleVox = true, CwBreakIn = true };
            var hold = Begin(radio);
            radio.Mode = "CW";
            _now += 300;
            Assert.False(hold.Tick(Setup(mode: "CW"), _now));
            Assert.Equal(new[] { FlexBase.VoxSetting.SimpleVox, FlexBase.VoxSetting.CwBreakIn }, hold.PausedSettings);
            Assert.False(radio.CwBreakIn);
            Assert.Equal(1, radio.Tones);
            Assert.Single(Speech(), e => e.GetProperty("text").GetString() == VoxNote);

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(mode: "CW"), _now);
            Assert.True(radio.SimpleVox);
            Assert.True(radio.CwBreakIn);
            Assert.Empty(hold.PausedSettings);
        }

        [Fact]
        public void VOX_comes_back_on_an_early_lift()
        {
            var radio = new FakeRadio { SimpleVox = true };
            var hold = Begin(radio);
            _now += 700;
            Assert.True(hold.Tick(Setup(antenna: "ANT1"), _now));
            Assert.Equal((FlexBase.VoxSetting.SimpleVox, true, false), radio.Writes.Last());
            Assert.True(radio.SimpleVox);
        }

        [Fact]
        public void When_the_owner_goes_away_the_restore_is_written_immediately_not_queued()
        {
            // Astra B3: CloseTheRadio disposes the rig right after
            // UnwireRadioEvents, and FlexBase.Dispose stops the command loop,
            // so a queued restore might never run. The teardown path asks for
            // an immediate write and the lever is told so.
            var radio = new FakeRadio { SimpleVox = true };
            var hold = Begin(radio);
            _now += 700;
            hold.Abandon("radio events unwired", _now, immediate: true);
            Assert.Equal((FlexBase.VoxSetting.SimpleVox, true, true), radio.Writes.Last());
            Assert.True(radio.SimpleVox);
            Assert.Equal(ReflectedCutHold.EndReason.Abandoned, hold.LastEnd);
            Assert.False(hold.Holding);
        }

        [Fact]
        public void A_restore_the_rig_could_not_issue_is_not_reported_as_a_restore()
        {
            // The lever says no write was issued (no radio, or the loop has
            // stopped and the write was not immediate); the hold ends, the
            // setting is reported as still off, and nothing claims otherwise.
            var radio = new FakeRadio { SimpleVox = true };
            var hold = Begin(radio);
            radio.LoopRunning = false;
            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Single(radio.Writes);           // the pause; the fake records nothing for a write it refused
            Assert.False(radio.SimpleVox);         // so VOX really is still off
            Assert.False(hold.VoxPaused);          // and the hold does not pretend it still owns it
            Assert.False(hold.Holding);
        }

        [Fact]
        public void VOX_off_at_the_cut_is_left_alone_and_nothing_is_said_about_it()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            var radio = new FakeRadio { SimpleVox = false };
            var hold = Begin(radio);
            Assert.Empty(radio.Writes);
            Assert.False(hold.VoxPaused);
            Assert.Equal(0, radio.Tones);
            Assert.DoesNotContain(Speech(), e => e.GetProperty("text").GetString()!.StartsWith("VOX", StringComparison.Ordinal));

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Empty(radio.Writes);
        }

        [Fact]
        public void The_VOX_note_follows_the_operators_verbosity_and_is_gated_at_Off()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off
            var radio = new FakeRadio { SimpleVox = true };
            Begin(radio);
            Assert.True(Gated(VoxNote), "the VOX note should be gated at Off; it is not a safety sentence");
            Assert.False(Delivered(VoxNote));
            Assert.Equal(1, radio.Tones);
        }

        // ──────────────────────────────────────────────────────────────────
        //  Repair round 2: Opus N4, N5, N6, N7
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void On_the_kill_switchs_path_the_VOX_note_waits_for_the_cut_sentence_and_is_given_once_after_it()
        {
            // N4: the kill switch speaks its Urgent cut sentence AFTER the hold
            // begins, and Urgent flushes the queue; a note given at Begin would
            // be cut off. With voxNoteNow false the note is pending until the
            // owner says the sentence is out.
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
            var radio = new FakeRadio { SimpleVox = true };
            var hold = new ReflectedCutHold(() => _now);
            hold.Begin(new ReflectedCutNotice(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2"), Setup(), _now,
                       radio.Levers(), voxNoteNow: false);
            Assert.True(hold.VoxPaused, "VOX is paused at once; only the words wait");
            Assert.Equal(1, radio.Tones == 0 ? 1 : 0);   // no tone yet either
            Assert.DoesNotContain(Speech(), e => e.GetProperty("text").GetString()!.StartsWith("VOX", StringComparison.Ordinal));

            _now += 1600;   // ConfirmDown's wait, then the sentence
            hold.SpeakPendingVoxNote();
            Assert.True(Delivered("VOX is paused for 4 seconds."), "the note was not given after the sentence");
            Assert.Equal(1, radio.Tones);

            hold.SpeakPendingVoxNote();   // a second call gives nothing more
            Assert.Single(Speech(), e => e.GetProperty("text").GetString()!.StartsWith("VOX", StringComparison.Ordinal));
        }

        [Fact]
        public void A_pending_VOX_note_is_dropped_if_the_hold_ends_before_the_sentence_is_out()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
            var radio = new FakeRadio { SimpleVox = true };
            var hold = new ReflectedCutHold(() => _now);
            hold.Begin(new ReflectedCutNotice(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2"), Setup(), _now,
                       radio.Levers(), voxNoteNow: false);
            _now += 300;
            hold.Tick(Setup(antenna: "ANT1"), _now);   // lifted; VOX restored
            hold.SpeakPendingVoxNote();
            Assert.DoesNotContain(Speech(), e => e.GetProperty("text").GetString()!.StartsWith("VOX", StringComparison.Ordinal));
            Assert.True(radio.SimpleVox);
        }

        [Fact]
        public void The_controllers_own_path_gives_the_note_at_once_as_before()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
            var radio = new FakeRadio { SimpleVox = true };
            Begin(radio);   // the rung overload: the cut sentence has already been spoken by GoIdle
            Assert.True(Delivered(VoxNote));
        }

        [Fact]
        public void The_VOX_note_says_the_seconds_the_hold_actually_has_left()
        {
            // N5: break-in off at the cut in CW, then USB with VOX on four
            // seconds in. The note used to say five seconds with one left.
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
            var radio = new FakeRadio { Mode = "CW", CwBreakIn = false, SimpleVox = true };
            var hold = Begin(radio, at: Setup(mode: "CW"));
            Assert.False(hold.VoxPaused);
            _now += 4000;
            radio.Mode = "USB";
            hold.NoteMode("USB", _now);
            Assert.True(hold.VoxPaused);
            Assert.True(Delivered("VOX is paused for one more second."), "the note did not say the remaining second");
            Assert.DoesNotContain(Speech(), e => e.GetProperty("text").GetString() == VoxNote);

            // And three seconds in, it says two.
            var radio2 = new FakeRadio { Mode = "CW", CwBreakIn = false, SimpleVox = true };
            _now += 10_000;
            var hold2 = Begin(radio2, at: Setup(mode: "CW"));
            _now += 3000;
            radio2.Mode = "USB";
            hold2.NoteMode("USB", _now);
            Assert.True(Delivered("VOX is paused for 2 seconds."));
        }

        [Fact]
        public void The_mode_event_re_pauses_at_once_without_waiting_for_a_poll()
        {
            // N6: for up to a poll interval after a mode change the new mode's
            // keying was live. The rig's mode event reaches the hold directly.
            var radio = new FakeRadio { Mode = "USB", SimpleVox = true, CwBreakIn = true };
            var hold = Begin(radio);
            radio.Mode = "CW";
            hold.NoteMode("CW", _now + 5);          // the event, 5 ms after the change
            Assert.False(radio.CwBreakIn, "break-in was still live after the mode event");
            Assert.Equal(new[] { FlexBase.VoxSetting.SimpleVox, FlexBase.VoxSetting.CwBreakIn }, hold.PausedSettings);
            Assert.True(hold.Holding, "a mode change is not a change that lifts");

            hold.NoteMode("CW", _now + 10);         // the same mode again: nothing new
            Assert.Equal(2, hold.PausedSettings.Count);
            _now += 300;
            Assert.False(hold.Tick(Setup(mode: "CW"), _now));   // the poll finds nothing left to do
            Assert.Equal(2, radio.Writes.Count);
        }

        [Fact]
        public void One_operator_VOX_change_cancels_only_the_restore_of_the_setting_they_touched()
        {
            // N7: USB with VOX on at the cut, then CW with break-in on, then the
            // operator toggles VOX in CW (break-in). Break-in is theirs now;
            // SimpleVOX is still the hold's to put back.
            var radio = new FakeRadio { Mode = "USB", SimpleVox = true, CwBreakIn = true };
            var hold = Begin(radio);
            radio.Mode = "CW";
            hold.NoteMode("CW", _now);
            Assert.Equal(2, hold.PausedSettings.Count);
            radio.CwBreakIn = true;   // the operator's own hand, in CW
            hold.OperatorChanged("Vox", _now);

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(mode: "CW"), _now);
            Assert.True(radio.SimpleVox, "SimpleVOX was paused by the hold and untouched by the operator; it must come back");
            Assert.True(radio.CwBreakIn);
            Assert.DoesNotContain(radio.Writes, w => w.Which == FlexBase.VoxSetting.CwBreakIn && w.On);   // no restore write for the one they touched
            Assert.Contains(radio.Writes, w => w.Which == FlexBase.VoxSetting.SimpleVox && w.On);
        }

        [Fact]
        public void If_the_operator_changes_VOX_during_the_hold_the_hold_does_not_put_it_back()
        {
            var radio = new FakeRadio { SimpleVox = true };
            var hold = Begin(radio);
            _now += 500;
            radio.SimpleVox = true; radio.Writes.Clear();   // the operator's own hand
            hold.OperatorChanged("Vox", _now);
            Assert.True(hold.Holding, "a VOX change does not lift the hold");

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Empty(radio.Writes);   // no restore write
        }

        [Fact]
        public void A_second_cut_during_a_hold_restores_and_restarts()
        {
            var radio = new FakeRadio { SimpleVox = true };
            var hold = Begin(radio);
            _now += 1000;
            hold.Begin(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2", Setup(), _now, radio.Levers());
            // The first hold's restore turned VOX on; the second paused it again.
            Assert.Equal(new[] { false, true, false }, radio.Writes.Select(w => w.On).ToArray());
            Assert.True(hold.Holding);
            _now += ReflectedCutHold.HoldMs - 1;
            Assert.True(hold.RefusePress("the lock key", _now, () => true), "the window restarted at the second cut");
        }

        // ──────────────────────────────────────────────────────────────────
        //  Generations: an old end cannot touch a new hold
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Each_hold_has_its_own_generation_and_Ended_names_the_one_that_ended()
        {
            var hold = new ReflectedCutHold(() => _now);
            var ended = new List<(int Gen, ReflectedCutHold.EndReason Reason)>();
            hold.Ended += (g, r, d) => ended.Add((g, r));

            int first = hold.Begin(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2", Setup(), _now);
            _now += 100;
            int second = hold.Begin(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2", Setup(), _now);
            Assert.Equal(first + 1, second);
            Assert.Equal(second, hold.Generation);
            Assert.True(hold.Holding, "the second hold must survive the first one's end");
            Assert.Equal(new[] { (first, ReflectedCutHold.EndReason.Abandoned) }, ended);

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Equal((second, ReflectedCutHold.EndReason.Expired), ended.Last());
        }

        [Fact]
        public void Concurrent_ticks_presses_and_restarts_never_corrupt_the_hold()
        {
            // The Fixer's thread refuses while the dispatcher ticks and a new
            // cut begins (the race Astra named). Under one lock the invariants
            // hold: never an exception, and after the dust settles the hold's
            // state is one of the two legal ones for the last Begin.
            var hold = new ReflectedCutHold(() => Interlocked.Read(ref _sharedNow));
            var radio = new FakeRadio { SimpleVox = true };
            _sharedNow = 1_000_000;
            hold.Begin(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2", Setup(), _sharedNow, radio.Levers());

            var tasks = new List<Task>();
            for (int t = 0; t < 4; t++)
            {
                int which = t;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 500; i++)
                    {
                        switch ((which + i) % 4)
                        {
                            case 0: hold.Tick(Setup(), Interlocked.Read(ref _sharedNow)); break;
                            case 1: hold.RefusePress("push to talk", Interlocked.Read(ref _sharedNow), () => true); break;
                            case 2: hold.Begin(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2", Setup(), Interlocked.Read(ref _sharedNow), radio.Levers()); break;
                            case 3: hold.OperatorChanged("MicGain", Interlocked.Read(ref _sharedNow)); break;
                        }
                        Interlocked.Add(ref _sharedNow, 1);
                    }
                }));
            }
            Task.WaitAll(tasks.ToArray());

            Assert.True(hold.Holding);   // the last Begin was within 2,000 ms of this clock, so it still holds
            Assert.True(hold.VoxPaused);
            // Every write is paired: each pause (false) is followed by exactly one restore (true), except the open one.
            int offs = radio.Writes.Count(w => !w.On), ons = radio.Writes.Count(w => w.On);
            Assert.Equal(offs - 1, ons);
        }

        private long _sharedNow;

        // ──────────────────────────────────────────────────────────────────
        //  The words, assembled, and their delivery
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_refusal_is_delivered_at_Off_with_the_cut_sentences_reason_in_percent()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off
            var hold = Begin();
            _now += 149;
            Assert.True(hold.RefusePress("the lock key", _now, () => true));

            const string expected = "Not keyed. 58 percent of your power was coming back on ANT2 a moment ago, "
                + "so keying is held for 5 more seconds. Check ANT2, or change your antenna, power, frequency or tuner to key now.";
            Assert.Equal(expected, hold.LastRefusalSentence);
            Assert.True(Delivered(expected), "the refusal did not reach the screen reader at Off");
        }

        [Fact]
        public void The_watts_rung_speaks_watts_and_never_percent()
        {
            var hold = Begin(rung: TransmitSafety.ReflectedCut.Watts, antenna: "ANT1");
            _now += 2600;
            Assert.True(hold.RefusePress("push to talk", _now, () => true));
            Assert.Equal("Not keyed. 13 watts of your power were coming back on ANT1 a moment ago, "
                + "so keying is held for 3 more seconds. Check ANT1, or change your antenna, power, frequency or tuner to key now.",
                hold.LastRefusalSentence);
            Assert.DoesNotContain("percent", hold.LastRefusalSentence);
        }

        [Fact]
        public void A_transmit_checks_cut_that_carried_no_reading_says_too_much_rather_than_inventing_a_number()
        {
            // Astra B1: a Request(Source.ReflectedPower) from a caller without
            // the watcher's reading still starts a hold, and the hold says so.
            var hold = new ReflectedCutHold(() => _now);
            hold.Begin(ReflectedCutNotice.WithoutReading("ANT2"), Setup(), _now);
            _now += 100;
            Assert.True(hold.RefusePress("the lock key", _now, () => true));
            Assert.Equal("Not keyed. Too much of your power was coming back on ANT2 a moment ago, "
                + "so keying is held for 5 more seconds. Check ANT2, or change your antenna, power, frequency or tuner to key now.",
                hold.LastRefusalSentence);

            // And one that did carry the reading speaks the rung's own words.
            var hold2 = new ReflectedCutHold(() => _now);
            hold2.Begin(new ReflectedCutNotice(TransmitSafety.ReflectedCut.Watts, WattsCut, "ANT1"), Setup(), _now);
            hold2.RefusePress("the lock key", _now, () => true);
            Assert.StartsWith("Not keyed. 13 watts of your power were coming back on ANT1", hold2.LastRefusalSentence);
        }

        [Fact]
        public void With_no_antenna_name_the_sentence_says_the_antenna()
        {
            var hold = Begin(antenna: "");
            _now += 100;
            hold.RefusePress("the lock key", _now, () => true);
            Assert.Equal("Not keyed. 58 percent of your power was coming back on the antenna a moment ago, "
                + "so keying is held for 5 more seconds. Check the antenna, or change your antenna, power, frequency or tuner to key now.",
                hold.LastRefusalSentence);
        }

        [Fact]
        public void The_last_second_is_singular()
        {
            var hold = Begin();
            _now += 4500;
            hold.RefusePress("the lock key", _now, () => true);
            Assert.Contains("held for one more second.", hold.LastRefusalSentence);
            Assert.DoesNotContain("1 more seconds", hold.LastRefusalSentence);
        }

        [Fact]
        public void The_remaining_seconds_count_down_across_presses()
        {
            var hold = Begin();
            _now += 100;  hold.RefusePress("the lock key", _now, () => true);
            Assert.Contains("5 more seconds", hold.LastRefusalSentence);
            _now += 1000; hold.RefusePress("the lock key", _now, () => true);
            Assert.Contains("4 more seconds", hold.LastRefusalSentence);
            _now += 2500; hold.RefusePress("the lock key", _now, () => true);
            Assert.Contains("2 more seconds", hold.LastRefusalSentence);
            Assert.Equal(3, hold.RefusedPresses);
        }

        [Fact]
        public void The_refusal_carries_the_cuts_subject_and_the_validity_answer()
        {
            var hold = Begin();
            _now += 149;
            hold.RefusePress("the lock key", _now, () => true);
            var line = Speech().Last(e => e.GetProperty("text").GetString() == hold.LastRefusalSentence);
            Assert.Equal("Urgent", line.GetProperty("intent").GetString());
            Assert.Equal("Critical", line.GetProperty("level").GetString());
        }
    }
}
