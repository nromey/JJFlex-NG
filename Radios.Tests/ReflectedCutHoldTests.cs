#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #571: for five seconds after a reflected-power cut, a press that would
    /// key the transmitter is refused and repeats why; a change to the
    /// transmit setup lifts the hold at once; VOX is paused and put back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven with an injected clock and fake VOX delegates, so the window,
    /// every signal, every restore path and the words are exercised with no
    /// radio and no dispatcher. Speech goes through the REAL
    /// <see cref="ScreenReaderOutput"/> with render off and the transcript on,
    /// so "delivered at Off" is read from what reached the diverted backend,
    /// past the verbosity gate, not from a call made before it.
    /// </para>
    /// <para>
    /// What this cannot see: the controller's own doors (those are
    /// <c>JJFlexWpf.Tests.LockSafetyHoldTests</c>, driving the real
    /// controller), the rig's VOX write, and RF. The VOX write is pinned from
    /// source in <c>LockSafetySourcePinTests</c>.
    /// </para>
    /// </remarks>
    [Collection(SpeechOutputStaticsCollection.Name)]
    public sealed class ReflectedCutHoldTests : IDisposable
    {
        private readonly string _path;
        private readonly VerbosityLevel _verbosityBefore;
        private long _now = 1_000_000;

        // A plausible reading for the share rung: 17.8 W forward, 10.3 W back,
        // the 2026-10-21 20:43 cut exactly (58 percent).
        private static readonly TransmitPowerReading ShareCut =
            new TransmitPowerReading(17.8f, 10.3f, skewMilliseconds: 4f, ageMilliseconds: 20f, commandedWatts: 25);
        private static readonly TransmitPowerReading WattsCut =
            new TransmitPowerReading(100f, 13.2f, skewMilliseconds: 4f, ageMilliseconds: 20f, commandedWatts: 100);

        private static ReflectedCutHold.TransmitSetup Setup(
            string antenna = "ANT2", ulong hz = 14_250_000, int power = 25, int tunePower = 10,
            bool tuning = false, string amps = "")
            => new ReflectedCutHold.TransmitSetup(antenna, hz, power, tunePower, tuning, amps);

        private sealed class FakeVox
        {
            public bool On;
            public readonly List<bool> Writes = new();
            public int Tones;
            public bool IsOn() => On;
            public void Set(bool on) { Writes.Add(on); On = on; }
        }

        public ReflectedCutHoldTests()
        {
            Lexicon.Load(Lexicon.Partitions);
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

        private ReflectedCutHold Begin(FakeVox? vox = null, ReflectedCutHold.TransmitSetup? at = null,
                                       TransmitSafety.ReflectedCut rung = TransmitSafety.ReflectedCut.Share,
                                       string antenna = "ANT2", int tones = 0)
        {
            var hold = new ReflectedCutHold(() => _now);
            var reading = rung == TransmitSafety.ReflectedCut.Watts ? WattsCut : ShareCut;
            hold.Begin(rung, reading, antenna, at ?? Setup(), _now,
                voxIsOn: vox == null ? null : vox.IsOn,
                voxSet: vox == null ? null : vox.Set,
                voxPausedTone: vox == null ? null : () => vox.Tones++,
                refusalTone: () => _refusalTones++);
            return hold;
        }

        private int _refusalTones;

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
            var vox = new FakeVox { On = true };
            var hold = Begin(vox);
            Assert.Equal(new[] { false }, vox.Writes);

            _now += ReflectedCutHold.HoldMs - ReflectedCutHold.SignalPollMs;
            Assert.False(hold.Tick(Setup(), _now));
            Assert.True(vox.Writes.Count == 1, "VOX came back early");

            _now += ReflectedCutHold.SignalPollMs;
            Assert.True(hold.Tick(Setup(), _now));
            Assert.Equal(new[] { false, true }, vox.Writes);
            Assert.True(vox.On);
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
            // Tuning at the cut (the ATU hunting into the fault): the hold
            // begins with tuning true. The tune ending is the cut's own
            // consequence, not the operator's change, so it does not lift.
            var hold = Begin(at: Setup(tuning: true));
            _now += 300;
            Assert.False(hold.Tick(Setup(tuning: false), _now));
            Assert.True(hold.Holding);
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
        //  VOX
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public void VOX_on_at_the_cut_is_switched_off_told_once_and_comes_back_at_expiry_silently()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
            var vox = new FakeVox { On = true };
            var hold = Begin(vox);

            Assert.Equal(new[] { false }, vox.Writes);
            Assert.True(hold.VoxPaused);
            Assert.Equal(1, vox.Tones);
            string note = Lexicon.Get("audio.ptt.cut_hold_vox_paused", ("seconds", 5));
            Assert.Equal("VOX is paused for 5 seconds.", note);
            Assert.True(Delivered(note), "the VOX-paused note was not delivered at Terse");

            int spokenBefore = Speech().Length;
            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Equal(new[] { false, true }, vox.Writes);
            Assert.Equal(spokenBefore, Speech().Length);   // nothing said when VOX comes back
            Assert.Equal(1, vox.Tones);
        }

        [Fact]
        public void VOX_comes_back_on_an_early_lift()
        {
            var vox = new FakeVox { On = true };
            var hold = Begin(vox);
            _now += 700;
            Assert.True(hold.Tick(Setup(antenna: "ANT1"), _now));
            Assert.Equal(new[] { false, true }, vox.Writes);
            Assert.True(vox.On);
        }

        [Fact]
        public void VOX_comes_back_when_the_owner_goes_away()
        {
            var vox = new FakeVox { On = true };
            var hold = Begin(vox);
            _now += 700;
            hold.Abandon("the PTT controller is going away", _now);
            Assert.Equal(new[] { false, true }, vox.Writes);
            Assert.Equal(ReflectedCutHold.EndReason.Abandoned, hold.LastEnd);
            Assert.False(hold.Holding);
        }

        [Fact]
        public void VOX_off_at_the_cut_is_left_alone_and_nothing_is_said_about_it()
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            var vox = new FakeVox { On = false };
            var hold = Begin(vox);
            Assert.Empty(vox.Writes);
            Assert.False(hold.VoxPaused);
            Assert.Equal(0, vox.Tones);
            Assert.DoesNotContain(Speech(), e => e.GetProperty("text").GetString()!.StartsWith("VOX", StringComparison.Ordinal));

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Empty(vox.Writes);
        }

        [Fact]
        public void The_VOX_note_follows_the_operators_verbosity_and_is_gated_at_Off()
        {
            // "tell them/tone based on settings" (Noel, 2026-10-22 05:38): at
            // Off the words are gated and the tone is still asked for; the
            // tone's own switches decide whether it sounds (the delegate is
            // EarconPlayer.ToggleTone in production, which gates itself).
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off
            var vox = new FakeVox { On = true };
            Begin(vox);
            string note = Lexicon.Get("audio.ptt.cut_hold_vox_paused", ("seconds", 5));
            Assert.True(Gated(note), "the VOX note should be gated at Off; it is not a safety sentence");
            Assert.False(Delivered(note));
            Assert.Equal(1, vox.Tones);
        }

        [Fact]
        public void If_the_operator_changes_VOX_during_the_hold_the_hold_does_not_put_it_back()
        {
            var vox = new FakeVox { On = true };
            var hold = Begin(vox);
            _now += 500;
            // The operator turns VOX back on themselves (or off; either way it
            // is theirs now). The hook fires with "Vox"; the hold stands down.
            vox.Set(true); vox.Writes.Clear();
            hold.OperatorChanged("Vox", _now);
            Assert.True(hold.Holding, "a VOX change does not lift the hold");

            _now += ReflectedCutHold.HoldMs;
            hold.Tick(Setup(), _now);
            Assert.Empty(vox.Writes);   // no restore write
        }

        [Fact]
        public void A_second_cut_during_a_hold_restores_and_restarts()
        {
            var vox = new FakeVox { On = true };
            var hold = Begin(vox);
            _now += 1000;
            hold.Begin(TransmitSafety.ReflectedCut.Share, ShareCut, "ANT2", Setup(), _now,
                voxIsOn: vox.IsOn, voxSet: vox.Set);
            // The first hold's restore turned VOX on; the second paused it again.
            Assert.Equal(new[] { false, true, false }, vox.Writes);
            Assert.True(hold.Holding);
            _now += ReflectedCutHold.HoldMs - 1;
            Assert.True(hold.RefusePress("the lock key", _now, () => true), "the window restarted at the second cut");
        }

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
            // Read from the transcript: the arbiter records the subject it was
            // given. Same subject as the cut sentence, so the press's fuller
            // sentence covers an interrupted copy of the cut's rather than
            // queueing behind it (and both are withdrawn by the same answer).
            var hold = Begin();
            _now += 149;
            hold.RefusePress("the lock key", _now, () => true);
            var line = Speech().Last(e => e.GetProperty("text").GetString() == hold.LastRefusalSentence);
            Assert.Equal("Urgent", line.GetProperty("intent").GetString());
            Assert.Equal("Critical", line.GetProperty("level").GetString());
        }
    }
}
