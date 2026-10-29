#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The spoken countdown through the REAL speech handoff (#835, Codex round
    /// 1, blockers 1 and 2): a real <see cref="SpeechArbiter"/>, a pump that
    /// holds what it has not handed to the reader, and a reader that answers
    /// only when the test says so, so "busy" and "slow" are facts rather than
    /// sleeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What Codex found.</b> The numbers were queued on the pips' clock with
    /// nothing listening for delivery, so a busy reader delayed them while the
    /// count, and the key, ran on time; with sounds off that keys the radio
    /// while its only warning is still owed. They shared a subject without
    /// being additive, so a new number superseded, and the pump withdrew, an
    /// unsaid earlier one. And a cancelled count's words stayed queued, since
    /// the still-valid test the old output passed is honoured only for Urgent
    /// speech. The earlier tests could not see any of it: their output recorded
    /// each Say the instant it was called.
    /// </para>
    /// <para>
    /// <b>What it still cannot see:</b> NVDA itself. The reader here is the
    /// arbiter's completion contract, played by the test.
    /// </para>
    /// </remarks>
    public sealed class CountdownSpeechDeliveryTests
    {
        private const string Subject = SpeechSubject.CheckCountdown;

        // ── Blocker 1: the count follows delivery, and the key waits for it ──

        [Fact]
        public async Task With_the_reader_busy_the_next_number_and_the_key_wait_for_the_last_number_to_be_said()
        {
            var bench = new Bench();
            bench.Arbiter.Emit("A long sentence the reader is still saying.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "test", "something-else");
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            // Two seconds of clock with the reader stuck on the earlier sentence.
            await bench.AdvanceTo(2000);
            Assert.Equal(new[] { "3" }, bench.Said);
            Assert.False(run.Completion.IsCompleted, "the count ran on while its first number was unsaid");

            // The reader catches up; from here it says each piece at once.
            bench.ReaderKeepsUp = true;
            long caughtUpAt = bench.Clock.Now;
            Assert.True(await bench.RunUntil(run.Completion));

            Assert.Equal(new[] { "3", "2", "1" }, bench.Said);
            // Each number followed the delivery of the one before it, a beat on.
            Assert.True(bench.SaidAt("2") >= caughtUpAt);
            Assert.True(bench.SaidAt("1") >= bench.SaidAt("2") + CountdownSettings.BeatMs);
            // And the key may go down only after "1" was said, a beat on.
            Assert.True(bench.Clock.Now >= bench.SaidAt("1") + CountdownSettings.BeatMs);
            Assert.Empty(bench.Withdrawn);
        }

        [Fact]
        public async Task The_key_waits_for_the_last_number_even_when_the_count_has_run_out()
        {
            var bench = new Bench { ReaderKeepsUp = true, ReaderHolds = "1" };
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            // The count's own end is 3000 ms; run the clock well past it with "1" unsaid.
            await bench.AdvanceTo(5000, keepUp: true, stopIf: run.Completion);
            Assert.Equal(new[] { "3", "2", "1" }, bench.Said);
            Assert.False(run.Completion.IsCompleted, "the count ended, and would have keyed, before its 1 was said");

            bench.ReaderHolds = null;
            long saidAt = bench.Clock.Now;
            Assert.True(await bench.RunUntil(run.Completion));
            Assert.True(bench.Clock.Now >= saidAt + CountdownSettings.BeatMs,
                "a late 1 must still get its beat before the key");
        }

        [Fact]
        public async Task A_count_whose_words_are_never_said_does_not_key_and_takes_its_words_back()
        {
            var bench = new Bench();
            bench.Arbiter.Emit("A sentence the reader never finishes.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "test", "something-else");
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            Assert.False(await bench.RunUntil(run.Completion, until: 30_000),
                "the count finished, and would have keyed, with its words never said");
            Assert.Contains("3", bench.Withdrawn);
            Assert.False(new CountdownSpeech(bench.Arbiter).Owed);
        }

        [Fact]
        public void The_numbers_do_not_supersede_one_another()
        {
            var bench = new Bench();
            bench.Arbiter.Emit("Busy.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "test", "something-else");
            var speech = new CountdownSpeech(bench.Arbiter);
            speech.Say("5");
            speech.Say("4");
            speech.Say("3");

            // All three are still owed and still in the pump, in order.
            Assert.Empty(bench.Withdrawn);
            Assert.Equal(new[] { "Busy.", "5", "4", "3" }, bench.Unsent.Select(u => u.Text));
            Assert.True(speech.Owed);
        }

        // ── Blocker 2: cancelling takes the words back ──

        [Fact]
        public async Task Cancelling_the_count_withdraws_its_unsaid_words()
        {
            var bench = new Bench();
            bench.Arbiter.Emit("Busy.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "test", "something-else");
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 5, leadIn: "Starting the live check in");
            await bench.AdvanceTo(300);

            run.Cancel();
            Assert.False(await bench.RunUntil(run.Completion));
            Assert.Contains("Starting the live check in", bench.Withdrawn);
            Assert.DoesNotContain(bench.Unsent, u => bench.Said.Contains(u.Text));
            Assert.False(new CountdownSpeech(bench.Arbiter).Owed);
        }

        [Fact]
        public async Task A_beeped_count_whose_lead_in_is_never_said_drops_the_lead_in_and_counts_on_its_pips()
        {
            var bench = new Bench();
            bench.Arbiter.Emit("Busy.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "test", "something-else");
            var run = bench.Start(CountdownVoicing.Beeped, seconds: 3, leadIn: "Starting the microphone test in");

            Assert.True(await bench.RunUntil(run.Completion, until: 30_000));
            Assert.Contains("Starting the microphone test in", bench.Withdrawn);
            Assert.Equal(3, bench.Tones.Count);
        }

        // ── The bench ──────────────────────────────────────────────────

        private sealed class Bench
        {
            private long _nextTicket;
            private readonly object _sync = new();

            public FakeSpeechClock SpeechClock { get; } = new();
            public Clock Clock { get; } = new();
            public SpeechArbiter Arbiter { get; }
            public List<(long Ticket, string Text, string? Subject)> Unsent { get; } = new();
            public List<string> Withdrawn { get; } = new();
            public List<(long At, string Text)> SaidLog { get; } = new();
            public List<long> Tones { get; } = new();
            public bool ReaderKeepsUp { get; set; }

            /// <summary>A piece the reader will not finish while this is set.</summary>
            public string? ReaderHolds { get; set; }

            private readonly Dictionary<long, (string Text, string? Subject)> _sent = new();

            public Bench()
            {
                Arbiter = new SpeechArbiter(
                    SpeechClock,
                    () => VerbosityLevel.Chatty,
                    (message, interrupt, intent, level, origin, salvaged) =>
                    {
                        long ticket = ++_nextTicket;
                        lock (_sync)
                        {
                            Unsent.Add((ticket, message, null));
                            _sent[ticket] = (message, null);
                        }
                        return SpeechHandoff.TrackedAs(ticket);
                    },
                    () => { },
                    (message, level, intent, origin) => { },
                    new SpeechRateModel(),
                    () => null,
                    (ticket, reason) =>
                    {
                        lock (_sync)
                        {
                            int i = Unsent.FindIndex(u => u.Ticket == ticket);
                            if (i < 0) return false;
                            Withdrawn.Add(Unsent[i].Text);
                            Unsent.RemoveAt(i);
                            return true;
                        }
                    });
            }

            public IEnumerable<string> Said => SaidLog.Select(s => s.Text);
            public long SaidAt(string text) => SaidLog.First(s => s.Text == text).At;

            public CountdownRun Start(CountdownVoicing voicing, int seconds, string? leadIn = null)
            {
                var plan = CountdownPlan.For(new CountdownSettings { Seconds = seconds }, voicing,
                    leadIn, n => n.ToString());
                var run = CountdownRun.Start(plan, new Output(this, new CountdownSpeech(Arbiter)), "test",
                    Clock.NowMs, Clock.Delay);
                // No setup to wait for: these tests are about the words.
                _ = run.ReadyToKeyAsync();
                return run;
            }

            /// <summary>
            /// The reader says, and finishes, whatever is first in the pump.
            /// False when there is nothing, or the first is the held piece.
            /// </summary>
            public bool ReaderFinishesNext()
            {
                (long Ticket, string Text, string? Subject) next;
                lock (_sync)
                {
                    if (Unsent.Count == 0) return false;
                    if (ReaderHolds != null && Unsent[0].Text == ReaderHolds) return false;
                    next = Unsent[0];
                    Unsent.RemoveAt(0);
                }
                Arbiter.OnOutcome(next.Ticket, next.Text, SpeechOutcome.Completed(1, 1, 200));
                return true;
            }

            public async Task AdvanceTo(long t, bool keepUp = false, Task? stopIf = null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (Clock.Now < t && stopIf?.IsCompleted != true)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the clock stopped moving");
                    if (keepUp) while (ReaderFinishesNext()) { }
                    if (!Clock.Step()) await Task.Delay(1);
                }
            }

            public async Task<bool> RunUntil(Task<bool> task, long until = 60_000)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!task.IsCompleted)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the countdown never finished");
                    Assert.True(Clock.Now <= until, "the countdown ran past " + until + " ms of its own clock");
                    if (ReaderKeepsUp) while (ReaderFinishesNext()) { }
                    if (!Clock.Step()) await Task.Delay(1);
                }
                return await task;
            }

            private sealed class Output : ICountdownOutput
            {
                private readonly Bench _bench;
                private readonly CountdownSpeech _speech;
                public Output(Bench bench, CountdownSpeech speech) { _bench = bench; _speech = speech; }
                public void Tone(CountdownBeat beat) { lock (_bench.Tones) _bench.Tones.Add(_bench.Clock.Now); }
                public void Say(string text)
                {
                    lock (_bench.SaidLog) _bench.SaidLog.Add((_bench.Clock.Now, text));
                    _speech.Say(text);
                }
                public bool SpeechOwed => _speech.Owed;
                public void WithdrawSpeech(string why) => _speech.Withdraw(why);
            }
        }

        /// <summary>A virtual clock whose delays complete only when stepped.</summary>
        private sealed class Clock
        {
            private readonly object _sync = new();
            private readonly Queue<(int Ms, TaskCompletionSource Done)> _pending = new();
            private long _now;

            public long Now { get { lock (_sync) return _now; } }
            public long NowMs() => Now;

            public Task Delay(int ms, CancellationToken token)
            {
                var done = new TaskCompletionSource();
                token.Register(() => done.TrySetCanceled(token));
                lock (_sync) _pending.Enqueue((ms, done));
                return done.Task;
            }

            public bool Step()
            {
                (int Ms, TaskCompletionSource Done) next;
                lock (_sync)
                {
                    if (_pending.Count == 0) return false;
                    next = _pending.Dequeue();
                    if (!next.Done.Task.IsCompleted) _now += next.Ms;
                }
                next.Done.TrySetResult();
                return true;
            }
        }
    }
}
