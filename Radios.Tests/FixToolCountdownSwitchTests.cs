using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The countdown switch covers the Fix tool too (#835, fix round 1030 P2,
    /// ruled by Noel 2026-10-29 23:59: "Each check counts down unless that
    /// function is turned off, then no countdown would count."). With it off,
    /// no count plays anywhere and a Fix tool stage keys without one, the way
    /// Check your audio keys as soon as its setup is confirmed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Safety, both ways.</b> Off means no count and no wait, never a count
    /// whose key-up figure was worked out for a different length. On, or a
    /// switch that cannot be read, keeps the ruled count and its key-up: the
    /// only side a broken switch may fail to is the later one.
    /// </para>
    /// <para>
    /// What these cannot see: the sound, and a radio keying. The keyed paths
    /// need a rig; the pacing, the switch and the host wiring are what is held
    /// here. The bench hears the rest.
    /// </para>
    /// </remarks>
    public class FixToolCountdownSwitchTests
    {
        private const string Run = "TX-P2SW";

        private static FixerTransmitGate ReadyGate()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("50 ohm dummy load on ANT1", FixerLoadKind.DummyLoad);
            return g;
        }

        [Fact]
        public void With_the_countdown_off_nothing_plays_and_the_stage_may_key_at_once()
        {
            bool counted = false;
            var w = Stopwatch.StartNew();
            bool ready = FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                () => counted = true, stopRequested: null, keyUpAtMs: 3000, countsDown: false);
            w.Stop();

            Assert.True(ready);
            Assert.False(counted, "a countdown played with the countdown switched off");
            Assert.True(w.ElapsedMilliseconds < 1000,
                "the stage waited out a count that does not exist: " + w.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void With_the_countdown_off_a_stop_already_asked_still_keys_nothing()
        {
            Assert.False(FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                countdown: null, stopRequested: () => true, keyUpAtMs: 3000, countsDown: false));
            Assert.False(FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                countdown: null, stopRequested: () => throw new InvalidOperationException("broken"),
                keyUpAtMs: 3000, countsDown: false));
        }

        [Fact]
        public void With_the_countdown_on_the_count_still_plays_and_paces_the_key_up()
        {
            // The positive control: the same call with the switch on is the
            // ruled count, unchanged.
            bool counted = false;
            var w = Stopwatch.StartNew();
            bool ready = FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                () => counted = true, stopRequested: null, keyUpAtMs: 250, countsDown: true);
            w.Stop();
            Assert.True(ready);
            Assert.True(counted);
            Assert.True(w.ElapsedMilliseconds >= 220, "the key-up was not paced: " + w.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void A_switch_that_is_missing_or_cannot_be_read_counts_down()
        {
            Assert.True(FixerTransmitAudioBoundary.CountdownIsOn(null));
            Assert.True(FixerTransmitAudioBoundary.CountdownIsOn(() => throw new InvalidOperationException("broken")));
            Assert.True(FixerTransmitAudioBoundary.CountdownIsOn(() => true));
            Assert.False(FixerTransmitAudioBoundary.CountdownIsOn(() => false));
        }

        [Fact]
        public void The_audio_stages_with_the_countdown_off_play_nothing_and_leave_nothing_to_wait_out()
        {
            bool counted = false;
            var b = FixerTransmitAudioBoundary.Create(ReadyGate(), () => null,
                countdown: () => counted = true, countdownKeyUpAtMs: 3000, countdownDurationMs: 3600,
                countdownOn: () => false);

            var w = Stopwatch.StartNew();
            Assert.True(Invoke<bool>(b, "CountdownThenReadyToKey"));
            Assert.True(Invoke<bool>(b, "WaitOutTheCountdown"));
            w.Stop();

            Assert.False(counted, "the audio stages played a count with the countdown off");
            Assert.True(w.ElapsedMilliseconds < 1000, "the stage waited for a count that never played: " + w.ElapsedMilliseconds + " ms");
            Assert.False(Field<bool>(b, "_lastCountedDown"), "the injected stage would not say it is transmitting");
        }

        [Fact]
        public void The_audio_stages_with_the_countdown_on_count_and_wait_as_ruled()
        {
            bool counted = false;
            var b = FixerTransmitAudioBoundary.Create(ReadyGate(), () => null,
                countdown: () => counted = true, countdownKeyUpAtMs: 200, countdownDurationMs: 300,
                countdownOn: () => true);

            var w = Stopwatch.StartNew();
            Assert.True(Invoke<bool>(b, "CountdownThenReadyToKey"));
            long keyUpAt = w.ElapsedMilliseconds;
            Assert.True(Invoke<bool>(b, "WaitOutTheCountdown"));
            w.Stop();

            Assert.True(counted);
            Assert.True(keyUpAt >= 170, "keyed before the published key-up: " + keyUpAt + " ms");
            // The wait-out is the sound's length plus the settle, from the count's start.
            Assert.True(w.ElapsedMilliseconds >= 300 + FixerTransmitAudioBoundary.DefaultCountdownSettleMs - 30,
                "the sound was not waited out: " + w.ElapsedMilliseconds + " ms");
            Assert.True(Field<bool>(b, "_lastCountedDown"));
        }

        [Fact]
        public void The_tune_carrier_stage_with_the_countdown_off_still_sounds_nothing_for_a_refused_transmit()
        {
            bool counted = false, spoke = false;
            var gate = new FixerTransmitGate();
            gate.BeginRun(Run);            // no load declared: refused
            var r = FixerTransmitBoundary.ProbeTransmitter(gate, () => null, TransmitStageSet.TransmitterCheck,
                speakNow: () => spoke = true, countdown: () => counted = true, countdownOn: () => false)();
            Assert.Equal(TxTuneProbe.Verdict.NotRun, r.Verdict);
            Assert.False(counted);
            Assert.False(spoke);
            Assert.Equal(0, gate.TransmitCount);
        }

        [Fact]
        public void The_host_wires_the_one_switch_to_every_Fix_tool_count()
        {
            string dialog = Read("JJFlexWpf", "Dialogs", "FixerDialog.cs");
            // The tune carrier and the two audio stages, and the microphone check.
            Assert.Equal(2, Count(dialog, "countdownOn: () => FixerCountdown.On"));
            Assert.Contains("CountdownOn = () => FixerCountdown.On", dialog, StringComparison.Ordinal);
            Assert.Contains("injectedKeyedWithoutCount:", dialog, StringComparison.Ordinal);
            Assert.Contains("SpeechSubject.FixToolInjectedKeyed", dialog, StringComparison.Ordinal);

            // The tones themselves ask it, so no caller can sound a count that is off.
            string seam = Read("JJFlexWpf", "Dialogs", "FixerCountdown.cs");
            Assert.Equal(2, Count(seam, "if (!On) { Off(\"Countdown"));
            Assert.Contains("Radios.CountdownSettings.Current.Enabled", seam, StringComparison.Ordinal);

            // And the microphone check does not wait out a count that never played.
            string mic = Read("JJFlexWpf", "Dialogs", "FixerHostWiring.cs");
            Assert.Contains("if (!faulted && !CountsDown(cues))", mic, StringComparison.Ordinal);
        }

        private static T Invoke<T>(object target, string method) =>
            (T)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null)!;

        private static T Field<T>(object target, string field) =>
            (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

        private static int Count(string text, string what)
        {
            int n = 0, from = 0;
            while ((from = text.IndexOf(what, from, StringComparison.Ordinal)) >= 0) { n++; from += what.Length; }
            return n;
        }

        private static string Read(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("repo root not found");
            return File.ReadAllText(Path.Combine(dir.FullName, Path.Combine(parts)));
        }
    }
}
