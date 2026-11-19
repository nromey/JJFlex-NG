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
            a.Emit(Landing, false, SpeechIntent.Queue, VerbosityLevel.Chatty, "landing", stillValid: stillValid);
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
            a.Emit(Landing, false, SpeechIntent.Queue, VerbosityLevel.Chatty, "landing", stillValid: () => !moved);
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
            a.Emit(Landing, false, SpeechIntent.Queue, VerbosityLevel.Chatty, "landing", stillValid: () => { asked++; return false; });
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

        [Fact]
        public void A_status_holds_while_the_radio_reports_what_it_said_or_has_not_yet_answered_what_was_asked()
        {
            string said30 = RadioStatusBuilder.StatedPart(At("10.125 megahertz", "30 meter"));
            string at20 = RadioStatusBuilder.StatedPart(At("14.100 megahertz", "20 meter"));
            // The walk: said 30 meters, the radio moved to 20.
            Assert.True(RadioStatusBuilder.StillDescribes(said30, said30, said30));
            Assert.False(RadioStatusBuilder.StillDescribes(said30, said30, at20));

            // A recall (#1131): the sentence names the asked-for 40 meter CW
            // memory while the radio still reports 20 meters USB. True before the
            // radio answers, true after it lands there, false if it went elsewhere.
            var asked = At("14.100 megahertz", "20 meter");
            RadioStatusBuilder.ApplyAskedFor(asked, 7_030_000, "CW");
            string saidRecall = RadioStatusBuilder.StatedPart(asked);
            Assert.True(RadioStatusBuilder.StillDescribes(saidRecall, at20, at20));
            Assert.True(RadioStatusBuilder.StillDescribes(saidRecall, at20, saidRecall));
            Assert.False(RadioStatusBuilder.StillDescribes(saidRecall, at20, said30));
        }

        [Fact]
        public void The_landing_carries_its_answer_queued_and_the_queue_road_passes_it_on()
        {
            // MainWindow cannot be built in a test: read the wiring from source.
            string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
            int landing = main.IndexOf("private void RunReturnToAppLanding(", StringComparison.Ordinal);
            Assert.True(landing >= 0);
            string land = main.Substring(landing, 7000);
            Assert.Contains("Radios.ScreenReaderOutput.Speak(status, Radios.Speech.SpeechIntent.Queue, Radios.VerbosityLevel.Chatty,", land);
            Assert.Contains("stillValid: Radios.RadioStatusBuilder.StillDescribes(rig, asked?.FrequencyHz, asked?.Mode)", land);
            string sro = Source("Radios", "ScreenReaderOutput.cs");
            Assert.Contains("_arbiter.Emit(message, interrupt: false, intent, level, origin, subject, additive, stillValid);", sro);
        }

        private static string Source(params string[] path)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
        }
    }
}
