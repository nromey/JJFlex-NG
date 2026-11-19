#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  Sprint 49 Track N2: a salvaged STATUS sentence is dropped if the radio
    //  state it describes no longer holds.
    //
    //  The integration walk at f3beb60b: closing a window queued the landing
    //  "Listening on 10.125 megahertz, USB, 30 meter band, slice A"; a band
    //  change 2.5 s later cut it before its first word; the arbiter held it as a
    //  salvage behind "20 meter band, 14.100.000" and then said it, after the
    //  radio was on 14.100. Protected safety sentences could already carry their
    //  producer's answer to "is this still true"; an ordinary queued sentence now
    //  can too, asked at every replay and never before the first hand-over.
    // ────────────────────────────────────────────────────────────────
    public class StatusSalvageValidityTests
    {
        private sealed record SinkCall(string Message, bool Salvaged, long Ticket);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;

        private const int Settle = SpeechArbiter.SalvageSettleMs;
        private const string Landing = "Listening on 10.125 megahertz, USB, 30 meter band, slice A";
        private const string BandChange = "20 meter band, 14.100.000";

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                long t = ++_nextTicket;
                _calls.Add(new SinkCall(message, salvaged, t));
                return SpeechHandoff.TrackedAs(t);
            },
            () => { },
            (message, level, intent, origin) => { });

        private IEnumerable<string> Salvaged() => _calls.Where(c => c.Salvaged).Select(c => c.Message);

        /// <summary>The walk's sequence: the landing queued, a band change interrupting it, the settle.</summary>
        private IEnumerable<string> RunTheWalk(Func<bool>? stillValid, Action? beforeTheBandChange = null)
        {
            var a = NewArbiter();
            if (stillValid == null) a.Emit(Landing, false, SpeechIntent.Queue, VerbosityLevel.Chatty, "landing");
            else a.EmitQueuedWhileTrue(Landing, SpeechIntent.Queue, VerbosityLevel.Chatty, "landing", null, false, stillValid);
            beforeTheBandChange?.Invoke();
            a.Emit(BandChange, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "band");
            _clock.Advance(Settle);
            return Salvaged().ToList();
        }

        [Fact]
        public void A_landing_the_radio_has_moved_away_from_is_dropped_rather_than_salvaged()
        {
            bool moved = false;
            var salvaged = RunTheWalk(() => !moved, () => moved = true);
            Assert.DoesNotContain(Landing, salvaged);
        }

        [Fact]
        public void A_landing_still_true_is_salvaged_as_before()
        {
            // The positive control: the same sequence, the radio where the sentence says.
            Assert.Contains(Landing, RunTheWalk(() => true));
        }

        [Fact]
        public void A_sentence_that_declares_nothing_is_salvaged_under_the_existing_rules()
        {
            Assert.Contains(Landing, RunTheWalk(null));
        }

        [Fact]
        public void A_producer_that_cannot_answer_does_not_get_its_status_replayed()
        {
            Assert.DoesNotContain(Landing, RunTheWalk(() => throw new InvalidOperationException("the radio is gone")));
        }

        [Fact]
        public void The_question_is_asked_at_the_release_not_only_at_the_interrupt()
        {
            // True when the band change cuts it, false by the time the hold
            // releases it: the release asks again, and drops it.
            bool moved = false;
            var a = NewArbiter();
            a.EmitQueuedWhileTrue(Landing, SpeechIntent.Queue, VerbosityLevel.Chatty, "landing", null, false, () => !moved);
            a.Emit(BandChange, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "band");
            moved = true;
            _clock.Advance(Settle);
            Assert.DoesNotContain(Landing, Salvaged());
        }

        [Fact]
        public void The_first_hand_over_is_never_asked()
        {
            // A sentence is true when built; only a replay asks.
            int asked = 0;
            var a = NewArbiter();
            a.EmitQueuedWhileTrue(Landing, SpeechIntent.Queue, VerbosityLevel.Chatty, "landing", null, false, () => { asked++; return false; });
            Assert.Contains(_calls, c => c.Message == Landing && !c.Salvaged);
            Assert.Equal(0, asked);
        }

        // ── The status sentence's own answer ──

        private static RadioStatusSnapshot At(string freq, string band, string mode = "USB") => new()
        {
            IsConnected = true, HasActiveSlice = true, Mode = mode,
            FrequencySpoken = freq, BandSpoken = band, SliceLetter = "A", SignalDisplay = "S5",
        };

        [Fact]
        public void The_stated_part_is_frequency_mode_band_and_slice_and_never_the_signal()
        {
            var a = At("10.125 megahertz", "30 meter");
            var b = At("10.125 megahertz", "30 meter");
            b.SignalDisplay = "S9";
            Assert.Equal(RadioStatusBuilder.StatedPart(a), RadioStatusBuilder.StatedPart(b));
            Assert.NotEqual(RadioStatusBuilder.StatedPart(a), RadioStatusBuilder.StatedPart(At("14.100 megahertz", "20 meter")));
            Assert.NotEqual(RadioStatusBuilder.StatedPart(a), RadioStatusBuilder.StatedPart(At("10.125 megahertz", "30 meter", "CW")));
            var keyed = At("10.125 megahertz", "30 meter");
            keyed.IsTransmitting = true;
            Assert.NotEqual(RadioStatusBuilder.StatedPart(a), RadioStatusBuilder.StatedPart(keyed));
        }

        /// <summary>A radio read through a test: each read returns <see cref="Now"/>'s copy and is counted.</summary>
        private sealed class Reading
        {
            public RadioStatusSnapshot Now;
            public int Reads;
            public Reading(RadioStatusSnapshot now) => Now = now;
            public RadioStatusSnapshot Read() { Reads++; return Now.Copy(); }
        }

        /// <summary>A snapshot from hertz, as BuildDetailedStatus would make it.</summary>
        private static RadioStatusSnapshot AtHz(ulong hz, string mode, string slice = "A")
        {
            var s = At(RadioStatusBuilder.FrequencyToSpoken(hz), "", mode);
            s.SliceLetter = slice;
            s.SliceCount = 1;
            RadioStatusBuilder.ApplyAskedFor(s, hz, null);
            return s;
        }

        [Fact]
        public void The_text_and_its_answer_come_from_one_reading()
        {
            // Codex's F1, made deterministic: the radio reads 10.125 when the
            // status is captured and 14.100 at every read after. One capture
            // must not certify 14.100 for a sentence saying 10.125.
            int calls = 0;
            RadioStatusSnapshot Read() => ++calls == 1 ? AtHz(10_125_000, "USB") : AtHz(14_100_000, "USB");
            var status = RadioStatusBuilder.CaptureSpokenStatus(Read, null);
            Assert.Equal(1, calls);
            Assert.Contains("10.125 megahertz", status.Text);
            Assert.False(status.StillValid());
        }

        [Fact]
        public void A_status_holds_while_the_radio_reports_what_it_said()
        {
            // The walk: said 30 meters, the radio moved to 20. And the positive
            // control: unchanged, read again at every question.
            var radio = new Reading(AtHz(10_125_000, "USB"));
            var status = RadioStatusBuilder.CaptureSpokenStatus(radio.Read, null);
            Assert.Equal("Listening on 10.125 megahertz, USB, 30 meter band, slice A", status.Text);
            Assert.True(status.StillValid());
            radio.Now.SignalDisplay = "S9";
            Assert.True(status.StillValid());
            Assert.Equal(3, radio.Reads);
            radio.Now = AtHz(14_100_000, "USB");
            Assert.False(status.StillValid());
            // A status naming nothing it was not already reported has no recall to wait for.
            radio.Now = AtHz(10_125_000, "USB");
            var same = RadioStatusBuilder.CaptureSpokenStatus(radio.Read, new StatusAsk(10_125_000, "USB", () => throw new InvalidOperationException("never asked")));
            radio.Now = AtHz(14_100_000, "USB");
            Assert.False(same.StillValid());
        }

        [Fact]
        public void A_recall_status_allows_each_of_its_two_answers_in_either_order_while_pending()
        {
            // #1131 with Codex's F2: 14.100 USB, a recall of 7.030 CW. The radio
            // answers frequency and mode as separate reports.
            foreach (bool frequencyFirst in new[] { true, false })
            {
                var radio = new Reading(AtHz(14_100_000, "USB"));
                var status = RadioStatusBuilder.CaptureSpokenStatus(radio.Read, new StatusAsk(7_030_000, "CW", () => true));
                Assert.Equal("Listening on 7.030 megahertz, CW, 40 meter band, slice A", status.Text);
                Assert.True(status.StillValid(), "before the radio answers");
                radio.Now = frequencyFirst ? AtHz(7_030_000, "USB") : AtHz(14_100_000, "CW");
                Assert.True(status.StillValid(), "half answered, frequency first: " + frequencyFirst);
                radio.Now = AtHz(7_030_000, "CW");
                Assert.True(status.StillValid(), "fully answered");
            }
        }

        [Fact]
        public void A_recall_status_refuses_what_the_recall_did_not_ask_for()
        {
            var ask = new StatusAsk(7_030_000, "CW", () => true);
            RadioStatusSnapshot[] unrelated =
            {
                AtHz(10_125_000, "USB"),   // a frequency neither asked nor before
                AtHz(7_030_000, "LSB"),    // a mode neither asked nor before
                AtHz(14_100_000, "USB", "B"), // another slice
            };
            foreach (var now in unrelated)
            {
                var radio = new Reading(AtHz(14_100_000, "USB"));
                var status = RadioStatusBuilder.CaptureSpokenStatus(radio.Read, ask);
                radio.Now = now;
                Assert.False(status.StillValid(), RadioStatusBuilder.StatedPart(now));
            }
            var keyed = new Reading(AtHz(14_100_000, "USB"));
            var whileListening = RadioStatusBuilder.CaptureSpokenStatus(keyed.Read, ask);
            keyed.Now.IsTransmitting = true;
            Assert.False(whileListening.StillValid(), "transmitting");
        }

        [Fact]
        public void A_recall_part_that_has_arrived_never_goes_back()
        {
            // Frequency seen at the asked 7.030, then 14.100 again: that is a
            // new change, not the recall's way there.
            var radio = new Reading(AtHz(14_100_000, "USB"));
            var status = RadioStatusBuilder.CaptureSpokenStatus(radio.Read, new StatusAsk(7_030_000, "CW", () => true));
            radio.Now = AtHz(7_030_000, "USB");
            Assert.True(status.StillValid());
            radio.Now = AtHz(14_100_000, "USB");
            Assert.False(status.StillValid());

            // Fully answered once, then back: only the sentence holds from then on.
            var again = new Reading(AtHz(14_100_000, "USB"));
            var full = RadioStatusBuilder.CaptureSpokenStatus(again.Read, new StatusAsk(7_030_000, "CW", () => true));
            again.Now = AtHz(7_030_000, "CW");
            Assert.True(full.StillValid());
            again.Now = AtHz(7_030_000, "USB");
            Assert.False(full.StillValid());
        }

        [Fact]
        public void A_recall_no_longer_pending_allows_only_the_sentence()
        {
            // Superseded by a later mode choice, or past its lifetime.
            bool pending = true;
            var radio = new Reading(AtHz(14_100_000, "USB"));
            var status = RadioStatusBuilder.CaptureSpokenStatus(radio.Read, new StatusAsk(7_030_000, "CW", () => pending));
            Assert.True(status.StillValid());
            pending = false;
            Assert.False(status.StillValid(), "the old state, for a recall no longer pending");
            pending = true;
            Assert.False(status.StillValid(), "once retired, it stays retired");
            radio.Now = AtHz(7_030_000, "CW");
            Assert.True(status.StillValid(), "the sentence itself still holds");
        }

        [Fact]
        public void The_no_slice_wording_comes_from_the_same_reading()
        {
            var connecting = new RadioStatusSnapshot { IsConnected = true, RadioModel = "FLEX-6300", SliceCount = 0 };
            Assert.Equal("Connected to FLEX-6300", RadioStatusBuilder.SpokenText(connecting));
            connecting.SliceCount = 2;
            Assert.Equal("Connected to FLEX-6300, no active slice", RadioStatusBuilder.SpokenText(connecting));
            Assert.Equal("No radio connected", RadioStatusBuilder.SpokenText(new RadioStatusSnapshot()));
        }

        [Fact]
        public void The_landing_carries_its_answer_queued_and_the_queue_road_passes_it_on()
        {
            // MainWindow cannot be built in a test: read the wiring from source.
            string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
            int landing = main.IndexOf("private void RunReturnToAppLanding(", StringComparison.Ordinal);
            Assert.True(landing >= 0);
            string land = main.Substring(landing, 7000);
            Assert.Contains("var status = Radios.RadioStatusBuilder.CaptureSpokenStatus(rig, asked);", land);
            Assert.Contains("Radios.ScreenReaderOutput.Speak(status.Text, Radios.Speech.SpeechIntent.Queue, Radios.VerbosityLevel.Chatty,", land);
            Assert.Contains("stillValid: status.StillValid);", land);
            string sro = Source("Radios", "ScreenReaderOutput.cs");
            Assert.Contains("_arbiter.EmitQueuedWhileTrue(message, intent, level, origin, subject, additive, stillValid);", sro);
            // Emit keeps its signature: tests reach it by reflection with a fixed argument count.
            var emit = typeof(SpeechArbiter).GetMethods().Where(m => m.Name == "Emit").ToList();
            Assert.Single(emit);
            Assert.Equal(7, emit[0].GetParameters().Length);
        }

        private static string Source(params string[] path)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
        }
    }
}
