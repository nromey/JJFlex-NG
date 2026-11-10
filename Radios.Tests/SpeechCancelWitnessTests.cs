#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A delivery cancelled NOT by us names the nearest key, focus change, own
    /// focus return or lost foreground, instead of declaring the cause
    /// unknowable (#709 item 3). Planted events on a planted clock; no reader,
    /// no window.
    /// </summary>
    public sealed class SpeechCancelWitnessTests
    {
        private long _now = 100_000;
        private readonly List<string> _late = new();

        private SpeechCancelWitness Witness() => new(() => _now, _late.Add);

        [Fact]
        public void A_key_at_the_same_millisecond_is_named()
        {
            // The positive control the entry asks for: the 89,735 ms capture,
            // a LeftCtrl and a word-0 cancel in the same millisecond.
            var w = Witness();
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "LeftCtrl", 89_735);
            string a = w.Attribute(39, 89_735, "cancelled at word 0 of 10");
            Assert.Equal("nearest event: a key (LeftCtrl), 0 ms before the reader's answer", a);
        }

        [Fact]
        public void The_nearest_of_several_is_named_with_its_distance()
        {
            var w = Witness();
            w.NoteAt(SpeechCancelWitness.EventKind.FocusChange, "TextBox Callsign", 8_480);
            w.NoteAt(SpeechCancelWitness.EventKind.OwnFocusReturn, "Home, after a dialog closed", 8_526);
            string a = w.Attribute(5, 8_572, "x");
            Assert.Equal("nearest event: our own focus return (Home, after a dialog closed), 46 ms before the reader's answer", a);
        }

        [Fact]
        public void Nothing_near_keeps_the_honest_answer()
        {
            var w = Witness();
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "LeftCtrl", 80_013 - SpeechCancelWitness.WindowMs - 1);
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "Tab", 89_734); // 9.7 s later: the #709 capture
            string a = w.Attribute(36, 80_013, "cancelled at word 42 of 80");
            Assert.StartsWith("no key, focus change or loss of the foreground seen in the 150 ms before", a);
            Assert.Empty(_late);
        }

        [Fact]
        public void An_event_just_after_the_answer_is_traced_on_its_own_line()
        {
            // NVDA's hook cancels before our window sees the key.
            var w = Witness();
            w.Attribute(12, 5_000, "cancelled at word 0 of 9");
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "LeftCtrl", 5_004);
            Assert.Equal(new[] { "SpeechCancel: #12 (cancelled at word 0 of 9): a key (LeftCtrl), 4 ms after the reader's answer." }, _late);
            // Only once per delivery.
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "Space", 5_006);
            Assert.Single(_late);
        }

        [Fact]
        public void A_later_event_farther_than_the_one_named_adds_nothing()
        {
            var w = Witness();
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "LeftCtrl", 7_000);
            Assert.Contains("0 ms before", w.Attribute(1, 7_000, "x"));
            w.NoteAt(SpeechCancelWitness.EventKind.FocusChange, "Button OK", 7_020);
            Assert.Empty(_late);
        }

        [Fact]
        public void A_later_nearer_event_says_it_is_nearer()
        {
            var w = Witness();
            w.NoteAt(SpeechCancelWitness.EventKind.FocusChange, "ListBox", 7_000 - 100);
            w.Attribute(2, 7_000, "cancelled at word 1 of 4");
            w.NoteAt(SpeechCancelWitness.EventKind.ApplicationDeactivated, null, 7_003);
            Assert.Equal(new[] { "SpeechCancel: #2 (cancelled at word 1 of 4): another program taking the foreground, 3 ms after the reader's answer, nearer than the 100 ms event its line named." }, _late);
        }

        [Fact]
        public void An_event_past_the_window_after_the_answer_adds_nothing()
        {
            var w = Witness();
            w.Attribute(3, 1_000, "x");
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "A", 1_000 + SpeechCancelWitness.WindowMs + 1);
            Assert.Empty(_late);
        }

        [Fact]
        public void Notes_from_many_threads_do_not_corrupt_the_memory()
        {
            var w = Witness();
            System.Threading.Tasks.Parallel.For(0, 2000, i =>
                w.NoteAt(SpeechCancelWitness.EventKind.Key, "K" + i, 50_000 + (i % 50)));
            Assert.StartsWith("nearest event: a key", w.Attribute(9, 50_049, "x"));
        }

        // ---- the line itself ----

        [Fact]
        public void The_outcome_line_carries_the_attribution()
        {
            var o = SpeechOutcome.Cancelled(0, 10, byUs: false, 0)
                .WithAttribution("nearest event: a key (LeftCtrl), 0 ms before the reader's answer");
            Assert.Equal("cancelled at word 0 of 10 after 0 ms, NOT by us — nearest event: a key (LeftCtrl), 0 ms before the reader's answer",
                o.ToString());
            // WithDetail keeps it.
            Assert.Contains("LeftCtrl", o.WithDetail("d").ToString());
        }

        [Fact]
        public void Without_an_attribution_the_old_honest_line_stands()
        {
            Assert.Contains("NOT by us — cause unknown", SpeechOutcome.Cancelled(0, 10, byUs: false, 0).ToString());
            Assert.Equal("cancelled at word 2 of 5 after 300 ms, by us",
                SpeechOutcome.Cancelled(2, 5, byUs: true, 300).WithAttribution("ignored").ToString());
        }

        // ---- wiring, read from source ----

        [Fact]
        public void The_reader_answer_is_attributed_when_the_outcome_is_made()
        {
            string src = Source("Radios", "Speech", "NvdaCompletionChannel.cs");
            int answered = src.IndexOf("long answeredAtMs = SpeechCancelWitness.Now;", StringComparison.Ordinal);
            int speak = src.IndexOf("NvdaControllerClient.SpeakSsml(ssml", StringComparison.Ordinal);
            int classify = src.IndexOf("NvdaControllerClient.Classify(rc", StringComparison.Ordinal);
            int attribute = src.IndexOf("outcome = outcome.WithAttribution(SpeechCancelWitness.Shared.Attribute(", StringComparison.Ordinal);
            Assert.True(speak > 0 && answered > speak && classify > answered && attribute > classify,
                "the answer must be stamped right after the reader returns and attributed after it is classified");
        }

        [Fact]
        public void A_withdrawal_after_a_cancel_our_own_focus_return_was_nearest_to_does_not_blame_another_program()
        {
            // The delivery in flight is cancelled with CancelledByUs false, and
            // what its line names is OUR OWN focus return: the speech system
            // did not ask for the cancel, but this application's focus handling
            // was right beside it. The item withdrawn behind it may say the
            // first and must not claim the cause was outside this application.
            // Driven through the real pump, against the fake reader.
            var witness = Witness();
            witness.NoteAt(SpeechCancelWitness.EventKind.OwnFocusReturn, "Home, after a dialog closed", 20_000);
            string named = witness.Attribute(1, 20_000, "cancelled at word 1 of 6");
            Assert.Contains("our own focus return", named, StringComparison.Ordinal);

            var ch = new FakeCompletionChannel();
            var outcomes = new List<(long Ticket, SpeechOutcome Outcome)>();
            using var reported = new System.Threading.AutoResetEvent(false);
            using var pump = new PacedSpeechDelivery(
                (text, interrupt) => SpeechDelivery.Accepted,
                (ticket, text, outcome) => { lock (outcomes) outcomes.Add((ticket, outcome)); reported.Set(); },
                _ => 2000, 300, 300, 300);
            try
            {
                long a = pump.Enqueue(ch, "Connected to the radio, four slices.");
                long b = pump.Enqueue(ch, "PC audio on.");
                var call = ch.WaitForCall();
                call.Result = SpeechOutcome.Cancelled(1, 6, byUs: false, 40).WithAttribution(named);
                call.Done.Set();

                SpeechOutcome inFlight = WaitFor(outcomes, reported, a);
                SpeechOutcome withdrawn = WaitFor(outcomes, reported, b);

                Assert.Contains("our own focus return", inFlight.ToString(), StringComparison.Ordinal);
                string line = withdrawn.ToString();
                Assert.Equal(0, withdrawn.MarksReached);
                Assert.False(withdrawn.CancelledByUs);
                Assert.Contains($"withdrawn unspoken: delivery #{a} ahead of it was cancelled, and the speech system did not ask for that cancel", line, StringComparison.Ordinal);
                Assert.DoesNotContain("outside this application", line, StringComparison.Ordinal);
                Assert.Equal(0, ch.CancelCalls);
            }
            finally { ch.ReleaseAll(); }
        }

        [Fact]
        public void A_key_noted_after_the_answer_but_before_it_was_registered_is_still_traced()
        {
            // The answer is stamped, a key lands a millisecond later, and only
            // then does the delivery thread register the answer. Nothing was
            // waiting when the key was noted, so registering must look at it.
            var w = Witness();
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "LeftCtrl", 1_001);
            string a = w.Attribute(7, 1_000, "cancelled at word 0 of 9");
            Assert.StartsWith("no key, focus change or loss of the foreground seen in the 150 ms before", a);
            Assert.Equal(new[] { "SpeechCancel: #7 (cancelled at word 0 of 9): a key (LeftCtrl), 1 ms after the reader's answer." }, _late);
            // It settled the delivery, as a key noted after registration does: once only.
            w.NoteAt(SpeechCancelWitness.EventKind.Key, "Space", 1_002);
            Assert.Single(_late);
        }

        private static SpeechOutcome WaitFor(List<(long Ticket, SpeechOutcome Outcome)> outcomes,
            System.Threading.AutoResetEvent reported, long ticket)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                lock (outcomes)
                    foreach (var o in outcomes)
                        if (o.Ticket == ticket) return o.Outcome;
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) throw new TimeoutException($"no outcome for #{ticket}");
                reported.WaitOne(remaining);
            }
        }

        [Fact]
        public void The_witness_is_fed_from_the_shell_a_class_handler_and_the_focus_return()
        {
            string bridge = Source("BridgeForm.vb");
            Assert.Contains("JJFlexWpf.SpeechCancelWitnessFeed.Install()", bridge, StringComparison.Ordinal);
            Assert.Contains("SpeechCancelWitness.EventKind.Key, keyData.ToString())", bridge, StringComparison.Ordinal);
            Assert.Contains("SpeechCancelWitness.EventKind.ApplicationDeactivated", bridge, StringComparison.Ordinal);

            string feed = Source("JJFlexWpf", "SpeechCancelWitnessFeed.cs");
            Assert.Contains("RegisterClassHandler(typeof(UIElement), Keyboard.PreviewKeyDownEvent", feed, StringComparison.Ordinal);
            Assert.Contains("RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent", feed, StringComparison.Ordinal);

            string home = Source("JJFlexWpf", "MainWindow.xaml.cs");
            int note = home.IndexOf("SpeechCancelWitness.EventKind.OwnFocusReturn", StringComparison.Ordinal);
            int move = home.IndexOf("Radios.WindowActivation.EnsureForeground(ShellHandle());", note < 0 ? 0 : note, StringComparison.Ordinal);
            Assert.True(note > 0 && move > note, "the own focus return is noted just before Home takes the foreground back");
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }
    }
}
