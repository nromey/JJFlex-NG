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
    /// Junk box batch 12 repair round 2 (#1127): a tune carrier's answers have
    /// an owner. On the 8600 bench a held Ctrl+T keyed, the first repeat turned
    /// the carrier off while "Tune on" still waited, and the arbiter rescued
    /// "Tune on" behind "Tune off": the last thing said, with nothing on the
    /// air. Twelve seconds later the next carrier's start rescued the previous
    /// carrier's "SWR not measured" into the new carrier.
    /// </summary>
    /// <remarks>
    /// The real arbiter on a virtual clock, fed what the window emits in each
    /// sequence: the press's sentence (interrupting, <see cref="SpeechSubject.TunePressSent"/>),
    /// "Tune on" (queued behind it, <see cref="SpeechSubject.TuneKeyed"/> and the
    /// owner's check), and at a stop "Tune off" (interrupting) then the SWR
    /// (queued, <see cref="SpeechSubject.SwrAfterTune"/>). That the window emits
    /// exactly these is pinned by source at the end, and driven through the
    /// window by JJFlexWpf.Tests' <c>TuneKeyCarrierTests</c>. What NVDA then says
    /// is the harness's. The lexicon load reads the language store under the
    /// settings root, so the class sits in the settings-root collection.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TuneCarrierAnswersTests
    {
        private sealed record SinkCall(string Message, bool Interrupt, bool Salvaged, double AtMs);

        private string Lead => Lexicon.Get("audio.tune.tune_carrier_sent");
        private string TuneOn => Lexicon.Get("audio.tune.tune_on");
        private string TuneOff => Lexicon.Get("audio.tune.tune_off");
        private string SwrNone => Lexicon.Get("audio.tune.swr_no_reading");

        public TuneCarrierAnswersTests() => Lexicon.Load(Lexicon.Partitions);

        /// <summary>How "Tune on" is spoken: as the repair speaks it, or as before it.</summary>
        public enum TuneOnAs { Owned, SubjectOnly, CheckOnly, AsBefore }

        /// <summary>One tune key's speech on the real arbiter.</summary>
        private sealed class Bench
        {
            public readonly FakeSpeechClock Clock = new();
            public readonly List<SinkCall> Calls = new();
            public readonly SpeechArbiter Arbiter;
            public readonly TuneCarrierAnswers Owner;
            private readonly TuneCarrierAnswersTests _t;
            private readonly TuneOnAs _as;

            public Bench(TuneCarrierAnswersTests t, TuneOnAs how, bool ownerRetires = true)
            {
                _t = t;
                _as = how;
                Arbiter = new SpeechArbiter(Clock, () => VerbosityLevel.Chatty,
                    (message, interrupt, intent, level, origin, salvaged) =>
                    {
                        Calls.Add(new SinkCall(message, interrupt, salvaged, Clock.ElapsedMs));
                        return true;
                    },
                    () => { },
                    (message, level, intent, origin) => { });
                Owner = new TuneCarrierAnswers(ownerRetires && how != TuneOnAs.AsBefore
                    ? (subject, why) => Arbiter.Supersede(subject, why, "TuneCarrierAnswers")
                    : (_, _) => { });
            }

            /// <summary>A press that starts a carrier: the owner first (in the toggle), then the press's sentence.</summary>
            public long Start()
            {
                long id = Owner.Started("a new tune carrier was asked for");
                Arbiter.Emit(_t.Lead, true, null, VerbosityLevel.Terse, "TuneKeyCarrierPress", SpeechSubject.TunePressSent);
                return id;
            }

            /// <summary>The radio's answer: "Tune on", queued behind the press's sentence.</summary>
            public void Keyed(long id)
            {
                Owner.Keyed(id);
                string? subject = _as is TuneOnAs.Owned or TuneOnAs.SubjectOnly ? SpeechSubject.TuneKeyed : null;
                Func<bool?>? check = _as is TuneOnAs.Owned or TuneOnAs.CheckOnly ? Owner.KeyedAnswerStillTrue(id) : null;
                Arbiter.Emit(_t.TuneOn, false, null, VerbosityLevel.Terse, "AnnounceTuneWhenTheRadioAnswers", subject,
                    stillValid: check);
            }

            /// <summary>A stop, by Ctrl+T or Ctrl+Shift+T: both end in the toggle's off feedback.</summary>
            public void Stop()
            {
                Owner.Ended("tune off");
                Arbiter.Emit(_t.TuneOff, true, null, VerbosityLevel.Terse, "TuneOffFeedback");
                Arbiter.Emit(_t.SwrNone, false, SpeechIntent.Queue, VerbosityLevel.Terse, "SpeakSwrAfterTune",
                    SpeechSubject.SwrAfterTune);
            }

            /// <summary>Everything handed to the reader at or after the last "Tune off".</summary>
            public List<SinkCall> AfterTuneOff()
            {
                int at = Calls.FindLastIndex(c => c.Message == _t.TuneOff);
                Assert.True(at >= 0, "no Tune off was spoken");
                return Calls.Skip(at + 1).ToList();
            }
        }

        /// <summary>Press, the radio keys at <paramref name="keyedMs"/>, a stop at <paramref name="stopMs"/>, then the settle window and more.</summary>
        private Bench StartKeyStop(TuneOnAs how, int keyedMs, int stopMs, bool ownerRetires = true)
        {
            var b = new Bench(this, how, ownerRetires);
            long id = b.Start();
            b.Clock.Advance(keyedMs);
            b.Keyed(id);
            b.Clock.Advance(stopMs - keyedMs);
            b.Stop();
            b.Clock.Advance(SpeechArbiter.SalvageSettleMs + 2000);
            return b;
        }

        // The bench's hold: keyed at 120 ms, the first repeat stopped it at 518 ms.
        // A quick press, press: the second press 250 ms after the first.
        // Ctrl+Shift+T stopping a Ctrl+T carrier before its answer is heard: 40 and 60 ms.
        public static IEnumerable<object[]> Stops => new[]
        {
            new object[] { "held key, the first repeat stops it", 120, 518 },
            new object[] { "press, press", 90, 250 },
            new object[] { "Ctrl+Shift+T before the answer is heard", 40, 60 },
        };

        /// <summary>
        /// The control: "Tune on" as it was spoken until this repair (no subject,
        /// no check, nothing retiring it). The arbiter rescues it behind "Tune
        /// off", so the instrument can see the defect the bench heard.
        /// </summary>
        [Theory]
        [MemberData(nameof(Stops))]
        public void As_before_the_repair_Tune_on_is_rescued_after_Tune_off(string sequence, int keyedMs, int stopMs)
        {
            var b = StartKeyStop(TuneOnAs.AsBefore, keyedMs, stopMs);
            Assert.True(b.AfterTuneOff().Any(c => c.Message == TuneOn && c.Salvaged),
                sequence + ": the control did not reproduce the bench, so this instrument cannot see the defect");
        }

        /// <summary>
        /// The repair: whatever ends the carrier, its "Tune on" is never said
        /// after "Tune off"; "Tune off" and then the SWR are the last things said.
        /// </summary>
        [Theory]
        [MemberData(nameof(Stops))]
        public void Tune_on_is_never_said_after_its_carrier_ended(string sequence, int keyedMs, int stopMs)
        {
            var b = StartKeyStop(TuneOnAs.Owned, keyedMs, stopMs);
            Assert.DoesNotContain(b.AfterTuneOff(), c => c.Message == TuneOn);
            Assert.Equal(SwrNone, b.Calls.Last(c => c.Message != Lead).Message);
            Assert.True(b.Calls.Count(c => c.Message == TuneOff) == 1, sequence);
        }

        /// <summary>
        /// Each defence alone holds: the owner's retirement at the stop (the
        /// subject, with no check), and the check before a replay (with the
        /// retirement switched off, for an end the owner hears of too late to
        /// retire anything).
        /// </summary>
        [Theory]
        [InlineData(TuneOnAs.SubjectOnly, true)]
        [InlineData(TuneOnAs.CheckOnly, false)]
        public void Either_the_retirement_or_the_check_alone_keeps_Tune_on_from_outliving_its_carrier(TuneOnAs how, bool ownerRetires)
        {
            var b = StartKeyStop(how, 120, 518, ownerRetires);
            Assert.DoesNotContain(b.AfterTuneOff(), c => c.Message == TuneOn);
        }

        /// <summary>
        /// The next carrier: the previous carrier's SWR, still unheard, is not
        /// rescued into it. The control (nothing retires) shows the arbiter would
        /// rescue it behind the new press's sentence, as the bench heard.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_previous_carriers_SWR_never_rides_into_the_next_carrier(bool ownerRetires)
        {
            var b = new Bench(this, TuneOnAs.Owned, ownerRetires);
            long first = b.Start();
            b.Clock.Advance(120);
            b.Keyed(first);
            b.Clock.Advance(3000);   // "Tune on" heard
            b.Stop();
            b.Clock.Advance(100);    // the SWR still waits behind "Tune off"
            int before = b.Calls.Count;
            long second = b.Start();
            b.Clock.Advance(120);
            b.Keyed(second);
            b.Clock.Advance(SpeechArbiter.SalvageSettleMs + 3000);

            var afterNext = b.Calls.Skip(before).ToList();
            bool rode = afterNext.Any(c => c.Message == SwrNone);
            if (ownerRetires) Assert.False(rode, "the previous carrier's SWR was said inside the next carrier");
            else Assert.True(rode, "the control did not reproduce the bench, so this instrument cannot see the defect");
            // The new carrier's own "Tune on" is said either way, once.
            Assert.Single(afterNext, c => c.Message == TuneOn);
        }

        /// <summary>
        /// The ownership itself: a stop or the radio's own drop ends the
        /// current carrier's "Tune on"; a later carrier ends an earlier one's; a
        /// fall that arrives before the new carrier has keyed is the previous
        /// carrier's, and leaves the new one's answer alone.
        /// </summary>
        [Fact]
        public void The_keyed_answer_belongs_to_one_carrier()
        {
            var retired = new List<(string Subject, string Why)>();
            var owner = new TuneCarrierAnswers((s, w) => retired.Add((s, w)));

            long one = owner.Started("first");
            Assert.Equal(TuneCarrierAnswers.StartRetires, retired.Select(r => r.Subject));
            Assert.False(owner.KeyedAnswerStillTrue(one)(), "not keyed yet");
            Assert.False(owner.RadioDropped("a fall before the carrier keyed"), "nothing of this carrier's to end");
            owner.Keyed(one);
            Assert.True(owner.KeyedAnswerStillTrue(one)());

            retired.Clear();
            owner.Ended("tune off");
            Assert.Equal(TuneCarrierAnswers.EndRetires, retired.Select(r => r.Subject));
            Assert.DoesNotContain(SpeechSubject.SwrAfterTune, retired.Select(r => r.Subject));
            Assert.False(owner.KeyedAnswerStillTrue(one)(), "ended");

            long two = owner.Started("second");
            Assert.False(owner.RadioDropped("the first carrier's fall, arriving late"));
            owner.Keyed(two);
            Assert.True(owner.KeyedAnswerStillTrue(two)(), "a late fall of the first carrier left the second's answer");
            Assert.False(owner.KeyedAnswerStillTrue(one)(), "superseded by the second");

            retired.Clear();
            Assert.True(owner.RadioDropped("the radio dropping tune"));
            Assert.Equal(TuneCarrierAnswers.EndRetires, retired.Select(r => r.Subject));
            Assert.False(owner.KeyedAnswerStillTrue(two)());
            Assert.False(owner.RadioDropped("the transmit edge after the fall"), "already ended, retired once");

            owner.Keyed(one);   // a stale report of an older carrier changes nothing
            Assert.False(owner.KeyedAnswerStillTrue(one)());
        }

        /// <summary>
        /// The window emits what the benches above feed the arbiter: "Tune on"
        /// with its carrier's subject and check; the owner told at every start
        /// and every end, before the end's own sentence.
        /// </summary>
        [Fact]
        public void The_window_speaks_Tune_on_owned_and_tells_the_owner_at_every_start_and_end()
        {
            string src = Source("JJFlexWpf", "MainWindow.xaml.cs");

            string announce = Body(src, "private void AnnounceTuneWhenTheRadioAnswers()", "private DispatcherTimer? _tuneAnswerWatch;");
            Assert.Contains("long carrier = TuneAnswers.Current;", announce, StringComparison.Ordinal);
            // The key is matched from its opening parenthesis, so the key guard does not read this pin as a use.
            Assert.Contains("TuneAnswers.Keyed(carrier);\n                Radios.ScreenReaderOutput.Speak(Radios.Lexicon", announce, StringComparison.Ordinal);
            Assert.Contains("(\"audio.tune.tune_on\"), VerbosityLevel.Terse, tuneOnInterrupts,\n"
                + "                    subject: Radios.Speech.SpeechSubject.TuneKeyed, stillValid: TuneAnswers.KeyedAnswerStillTrue(carrier));",
                announce, StringComparison.Ordinal);

            string toggle = Body(src, "public void ToggleTuneCarrier()", "private void TuneOffFeedback(");
            int write = toggle.IndexOf("RigControl.TxTune = newState;", StringComparison.Ordinal);
            int started = toggle.IndexOf("if (newState) TuneAnswers.Started(", StringComparison.Ordinal);
            int answer = toggle.IndexOf("AnnounceTuneWhenTheRadioAnswers();", StringComparison.Ordinal);
            Assert.True(write >= 0 && started > write && answer > started, "the toggle tells the owner after the write and before the answer");

            // Every end of a carrier tells the owner, on the line before its own end of the question.
            string off = Body(src, "private void TuneOffFeedback(", "public bool TurnTuneCarrierOff()");
            Assert.True(off.IndexOf("TuneAnswers.Ended(\"tune off\");", StringComparison.Ordinal) is >= 0 and var o
                && o < off.IndexOf("EndTuneAnswer(\"tune off\");", StringComparison.Ordinal)
                && o < off.IndexOf("audio.tune.tune_off", StringComparison.Ordinal), "the stop retires before Tune off is said");
            foreach (string teardown in new[] { "the connection fell", "the radio's events were unwired", "the radio powered off" })
            {
                int told = src.IndexOf("TuneAnswers.Ended(\"" + teardown + "\");", StringComparison.Ordinal);
                int ended = src.IndexOf("EndTuneAnswer(\"" + teardown + "\");", StringComparison.Ordinal);
                Assert.True(told >= 0 && ended > told && ended - told < 160, teardown + ": the owner is not told beside its end");
            }

            Assert.Contains("TuneAnswers.RadioDropped(\"the radio dropping tune\");", src, StringComparison.Ordinal);
            Assert.Contains("if (!transmit) TuneAnswers.RadioDropped(\"the radio going to receive\");", src, StringComparison.Ordinal);
            Assert.Contains("TuneAnswers.Started(\"an ATU tune was asked for\");", src, StringComparison.Ordinal);
        }

        private static string Body(string src, string start, string end)
        {
            int at = src.IndexOf(start, StringComparison.Ordinal);
            Assert.True(at >= 0, start + " is not where this test reads it.");
            at += start.Length;
            int to = src.IndexOf(end, at, StringComparison.Ordinal);
            Assert.True(to > at, end + " does not follow " + start + ".");
            return src.Substring(at, to - at);
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }
    }
}
