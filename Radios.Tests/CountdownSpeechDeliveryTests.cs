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
    /// The spoken countdown through the REAL speech handoff (#835, Codex rounds
    /// 1 and 2): a real <see cref="SpeechArbiter"/>, a pump that holds what it
    /// has not handed to the reader, and a reader whose start and finish of
    /// each piece the test controls. The countdown's clock and the arbiter's
    /// clock advance together, and what the reader actually did is recorded
    /// apart from what was asked of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Round 1</b> found the numbers queued on the pips' clock with nothing
    /// listening for delivery, superseding one another, and left queued after a
    /// cancel. <b>Round 2</b> found three more: a pip sounding ahead of its own
    /// number under a busy reader; an expired estimate read as delivery on a
    /// reader with no completion channel (JAWS, SAPI); and a piece that never
    /// reached the reader, or whose Say threw, read as said. Round 1's bench
    /// could see none of the three: its arbiter clock never moved, every
    /// handoff was tracked, and it timed requests rather than delivery.
    /// </para>
    /// <para>
    /// <b>What it still cannot see:</b> NVDA, JAWS or SAPI themselves. On the
    /// estimate path in particular, the reader's own speech (a focus
    /// announcement) is outside the arbiter's knowledge; the test pins the
    /// stated rule, estimate plus margin, not a delivery the code cannot see.
    /// </para>
    /// </remarks>
    public sealed class CountdownSpeechDeliveryTests
    {
        // ── A pip never sounds ahead of its own number ──────────────────

        [Fact]
        public async Task With_a_busy_tracked_reader_each_pip_waits_for_its_own_number_to_start()
        {
            var bench = new Bench();
            bench.BusyWithOtherSpeechUntil(2000);
            var run = bench.Start(CountdownVoicing.SpokenAndBeeped, seconds: 3);

            Assert.True(await bench.RunUntil(run.Completion));

            Assert.Equal(new[] { "3", "2", "1" }, bench.Requested);
            Assert.Equal(3, bench.Tones.Count);
            foreach (var (number, toneAt) in new[] { "3", "2", "1" }.Zip(bench.Tones))
                Assert.True(toneAt >= bench.StartedAt(number),
                    $"the pip for {number} sounded at {toneAt} ms, before the reader began {number} at {bench.StartedAt(number)} ms");
            // The first number really was held up: its pip came at 2000 (within
            // one poll of the reader starting it), not at 0.
            Assert.InRange(bench.Tones[0], 2000, 2000 + CountdownRun.SpeechPollMs);
        }

        [Fact]
        public async Task A_number_the_reader_has_accepted_but_not_begun_holds_its_pip()
        {
            // Codex round 3: the reader takes the number at once (it has left
            // our pump) and then holds it for two seconds behind speech of its
            // own. The pip must wait for the first word, not for the hand-over.
            var bench = new Bench { ReaderFirstWordDelayMs = 2000 };
            var run = bench.Start(CountdownVoicing.SpokenAndBeeped, seconds: 3);

            Assert.True(await bench.RunUntil(run.Completion));
            Assert.InRange(bench.Tones[0], 2000, 2000 + CountdownRun.SpeechPollMs);
            foreach (var (number, toneAt) in new[] { "3", "2", "1" }.Zip(bench.Tones))
                Assert.True(toneAt >= bench.StartedAt(number),
                    $"the pip for {number} sounded at {toneAt} ms, before its first word at {bench.StartedAt(number)} ms");
        }

        [Fact]
        public void The_real_pump_says_a_piece_has_started_only_at_its_first_word_mark()
        {
            // The production signal, on the production pump: handed to the
            // reader is not started; the first word mark is.
            var channel = new FakeCompletionChannel();
            using var pump = new PacedSpeechDelivery((text, interrupt) => SpeechDelivery.Accepted,
                (ticket, text, outcome) => { }, _ => 5000);
            try
            {
                long ticket = pump.Enqueue(channel, "three");
                var call = channel.WaitForCall();
                Assert.Equal(ticket, call.Ticket);
                Assert.False(pump.HasReachedFirstWord(ticket), "accepted by the reader, no word yet, yet read as started");

                channel.Mark(call);
                Assert.True(pump.HasReachedFirstWord(ticket));

                long other = pump.Enqueue(channel, "two");
                Assert.False(pump.HasReachedFirstWord(other), "still queued, yet read as started");
            }
            finally { channel.ReleaseAll(); }
        }

        /// <summary>
        /// #855, ruled by Noel 2026-10-31: the pump reports progress, so the
        /// countdown hears NVDA's first word at once instead of at its next
        /// 25 ms poll. News comes on the first word and on the finish, and not
        /// on later words, which nothing waits for.
        /// </summary>
        [Fact]
        public void The_real_pump_reports_progress_at_the_first_word_and_at_the_finish_only()
        {
            var channel = new FakeCompletionChannel();
            using var pump = new PacedSpeechDelivery((text, interrupt) => SpeechDelivery.Accepted,
                (ticket, text, outcome) => { }, _ => 5000);
            try
            {
                var beforeAnything = pump.NextProgress;
                pump.Enqueue(channel, "three two");
                var call = channel.WaitForCall();
                Assert.False(beforeAnything.IsCompleted, "handed to the reader is not progress");

                channel.Mark(call);
                Assert.True(beforeAnything.Wait(2000), "the first word mark did not report progress");

                var afterFirstWord = pump.NextProgress;
                Assert.NotSame(beforeAnything, afterFirstWord);
                channel.Mark(call);
                Assert.False(afterFirstWord.Wait(150), "a later word reported progress");

                channel.Complete(call);
                Assert.True(afterFirstWord.Wait(2000), "the finish did not report progress");
            }
            finally { channel.ReleaseAll(); }
        }

        // ── The count, and the key, follow delivery ─────────────────────

        [Fact]
        public async Task With_the_reader_busy_the_next_number_and_the_key_wait_for_the_last_number_to_be_said()
        {
            var bench = new Bench();
            bench.BusyWithOtherSpeechUntil(2000);
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            Assert.True(await bench.RunUntil(run.Completion));

            Assert.Equal(new[] { "3", "2", "1" }, bench.Requested);
            Assert.True(bench.RequestedAt("2") >= bench.FinishedAt("3"));
            Assert.True(bench.RequestedAt("1") >= bench.FinishedAt("2"));
            Assert.True(bench.Clock.Now >= bench.FinishedAt("1"), "the key went down before the reader finished 1");
            Assert.Empty(bench.Withdrawn);
        }

        [Fact]
        public async Task The_key_waits_for_the_last_number_even_when_the_count_has_run_out()
        {
            var bench = new Bench { ReaderHolds = "1" };
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            await bench.Run(until: 5000, stopIf: run.Completion);
            Assert.False(run.Completion.IsCompleted, "the count ended, and would have keyed, before its 1 was said");

            bench.ReaderHolds = null;
            Assert.True(await bench.RunUntil(run.Completion));
            Assert.True(bench.Clock.Now >= bench.FinishedAt("1") + CountdownSettings.BeatMs,
                "a late 1 must still get its beat before the key");
        }

        // ── Words that are never said: nothing keys ─────────────────────

        [Fact]
        public async Task A_count_whose_words_are_never_said_does_not_key_and_takes_its_words_back()
        {
            var bench = new Bench();
            bench.BusyWithOtherSpeechUntil(long.MaxValue);
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            Assert.False(await bench.RunUntil(run.Completion, until: 30_000));
            Assert.True(run.SpeechStalled);
            Assert.Contains("3", bench.Withdrawn);
        }

        [Fact]
        public async Task A_tracked_reader_that_refuses_the_words_does_not_key()
        {
            var bench = new Bench { ReaderRefuses = true };
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            Assert.False(await bench.RunUntil(run.Completion, until: 30_000));
            Assert.True(run.SpeechStalled);
        }

        [Fact]
        public async Task Words_that_never_reach_the_reader_are_not_counted_as_said()
        {
            var bench = new Bench { Delivery = Bench.Mode.NotReached };
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            Assert.False(await bench.RunUntil(run.Completion, until: 30_000),
                "the count keyed although none of its words reached the reader");
            Assert.True(run.SpeechStalled);
            Assert.True(bench.Clock.Now < CountdownSettings.BeatMs, "a piece that can never be said need not be waited on");
        }

        [Fact]
        public async Task A_speech_layer_that_throws_is_not_counted_as_said()
        {
            var bench = new Bench { Delivery = Bench.Mode.Throws };
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            Assert.False(await bench.RunUntil(run.Completion, until: 30_000),
                "the count keyed although every Say threw");
            Assert.True(run.SpeechStalled);
        }

        [Fact]
        public async Task A_beeped_count_whose_lead_in_never_reaches_the_reader_counts_on_its_pips()
        {
            var bench = new Bench { Delivery = Bench.Mode.NotReached };
            var run = bench.Start(CountdownVoicing.Beeped, seconds: 3, leadIn: "Starting the microphone test in");

            Assert.True(await bench.RunUntil(run.Completion, until: 30_000));
            Assert.False(run.SpeechStalled);
            Assert.Equal(3, bench.Tones.Count);
        }

        [Fact]
        public async Task A_beeped_count_whose_lead_in_is_never_said_drops_the_lead_in_and_counts_on_its_pips()
        {
            var bench = new Bench();
            bench.BusyWithOtherSpeechUntil(long.MaxValue);
            var run = bench.Start(CountdownVoicing.Beeped, seconds: 3, leadIn: "Starting the microphone test in");

            Assert.True(await bench.RunUntil(run.Completion, until: 30_000));
            Assert.Contains("Starting the microphone test in", bench.Withdrawn);
            Assert.Equal(3, bench.Tones.Count);
        }

        // ── The estimate path (JAWS, SAPI): estimate plus margin ────────

        [Fact]
        public async Task On_a_reader_with_no_completion_signal_a_spoken_count_holds_each_number_for_its_estimate_and_margin()
        {
            var bench = new Bench { Delivery = Bench.Mode.Untracked };
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 3);

            Assert.True(await bench.RunUntil(run.Completion));

            int Held(string text)
            {
                int est = SpeechArbiter.EstimateSpokenMs(text);
                return est + CountdownSpeech.UntrackedMarginMs(est);
            }
            Assert.Equal(new[] { "3", "2", "1" }, bench.Requested);
            Assert.True(bench.RequestedAt("2") >= bench.RequestedAt("3") + Held("3"),
                "the next number came before the last one's estimate and margin had passed");
            Assert.True(bench.RequestedAt("1") >= bench.RequestedAt("2") + Held("2"));
            Assert.True(bench.Clock.Now >= bench.RequestedAt("1") + Held("1"),
                "the key went down before the last number's estimate and margin had passed");
        }

        // ── The policy: additive, and withdrawn on cancel ───────────────

        [Fact]
        public void The_numbers_do_not_supersede_one_another()
        {
            var bench = new Bench();
            bench.BusyWithOtherSpeechUntil(long.MaxValue);
            var speech = bench.Speech;
            speech.Say("5", true);
            speech.Say("4", true);
            speech.Say("3", true);

            Assert.Empty(bench.Withdrawn);
            Assert.Equal(new[] { "5", "4", "3" }, bench.QueuedTexts);
        }

        [Fact]
        public async Task Cancelling_the_count_withdraws_its_unsaid_words()
        {
            var bench = new Bench();
            bench.BusyWithOtherSpeechUntil(long.MaxValue);
            var run = bench.Start(CountdownVoicing.Spoken, seconds: 5, leadIn: "Starting the live check in");
            await bench.Run(until: 300, stopIf: run.Completion);

            run.Cancel();
            Assert.False(await bench.RunUntil(run.Completion));
            Assert.Contains("Starting the live check in", bench.Withdrawn);
            Assert.Empty(bench.QueuedTexts);
        }

        // ── The bench ──────────────────────────────────────────────────

        private sealed class Bench
        {
            public enum Mode { Tracked, Untracked, NotReached, Throws }

            private readonly object _sync = new();
            private long _nextTicket;
            private long _busyUntil = -1;
            private (long Ticket, string Text, long Since)? _inFlight;

            public Mode Delivery { get; set; } = Mode.Tracked;
            public string? ReaderHolds { get; set; }
            public bool ReaderRefuses { get; set; }
            public int ReaderMsPerPiece { get; set; } = 300;

            /// <summary>
            /// How long the reader holds a piece it has accepted before its first
            /// word: speech of its own ahead of ours, which the app cannot see
            /// (Codex round 3). The first word mark comes this long after it
            /// takes the piece.
            /// </summary>
            public int ReaderFirstWordDelayMs { get; set; }

            public FakeSpeechClock SpeechClock { get; } = new();
            public Clock Clock { get; }
            public SpeechArbiter Arbiter { get; }
            public CountdownSpeech Speech { get; }

            private readonly List<(long Ticket, string Text)> _queue = new();
            public List<string> Withdrawn { get; } = new();
            public List<long> Tones { get; } = new();
            private readonly List<(long At, string Text)> _requested = new();
            private readonly Dictionary<string, long> _started = new();
            private readonly Dictionary<string, long> _finished = new();

            public Bench()
            {
                Clock = new Clock(ms => SpeechClock.Advance(ms));
                Arbiter = new SpeechArbiter(
                    SpeechClock,
                    () => VerbosityLevel.Chatty,
                    (message, interrupt, intent, level, origin, salvaged) =>
                    {
                        switch (Delivery)
                        {
                            case Mode.NotReached: return SpeechHandoff.NotReached;
                            case Mode.Throws: throw new InvalidOperationException("the speech layer is broken");
                            case Mode.Untracked:
                                lock (_sync) _queue.Add((0, message));
                                return SpeechHandoff.Untracked;
                            default:
                                long ticket = Interlocked.Increment(ref _nextTicket);
                                lock (_sync) _queue.Add((ticket, message));
                                return SpeechHandoff.TrackedAs(ticket);
                        }
                    },
                    () => { },
                    (message, level, intent, origin) => { },
                    new SpeechRateModel(),
                    () => null,
                    (ticket, reason) =>
                    {
                        lock (_sync)
                        {
                            int i = _queue.FindIndex(q => q.Ticket == ticket && ticket != 0);
                            if (i < 0) return false;
                            Withdrawn.Add(_queue[i].Text);
                            _queue.RemoveAt(i);
                            return true;
                        }
                    });
                // The production signal: the reader has reported the first word.
                Speech = new CountdownSpeech(Arbiter, ticket =>
                {
                    lock (_sync)
                        return _inFlight is { } f && f.Ticket == ticket && Clock.Now >= f.Since + ReaderFirstWordDelayMs;
                });
            }

            public IEnumerable<string> QueuedTexts { get { lock (_sync) return _queue.Select(q => q.Text).ToList(); } }
            public IEnumerable<string> Requested { get { lock (_requested) return _requested.Select(r => r.Text).ToList(); } }
            public long RequestedAt(string text) { lock (_requested) return _requested.First(r => r.Text == text).At; }
            public long StartedAt(string text) { lock (_sync) return _started[text]; }
            public long FinishedAt(string text) { lock (_sync) return _finished[text]; }

            /// <summary>The reader is saying something of its own (or of ours, unfollowed) until then.</summary>
            public void BusyWithOtherSpeechUntil(long t) => _busyUntil = t;

            public CountdownRun Start(CountdownVoicing voicing, int seconds, string? leadIn = null)
            {
                var plan = CountdownPlan.For(new CountdownSettings { Seconds = seconds }, voicing,
                    leadIn, n => n.ToString());
                var run = CountdownRun.Start(plan, new Output(this), "test", Clock.NowMs, Clock.Delay);
                // No setup to wait for: these tests are about the words.
                _ = run.ReadyToKeyAsync();
                return run;
            }

            /// <summary>
            /// The reader, one step: finish what it is saying when its time is
            /// up, then take the next piece from the pump. Tracked pieces are
            /// answered through the arbiter; untracked ones are only recorded,
            /// since nobody tells the arbiter.
            /// </summary>
            private void ReaderStep()
            {
                long now = Clock.Now;
                (long Ticket, string Text, long Since)? done = null;
                lock (_sync)
                {
                    if (now < _busyUntil) return;
                    if (_inFlight is { } f && now >= f.Since + ReaderFirstWordDelayMs + ReaderMsPerPiece && f.Text != ReaderHolds)
                    {
                        _finished[f.Text] = now;
                        done = f;
                        _inFlight = null;
                    }
                }
                if (done is { } d && d.Ticket != 0)
                    Arbiter.OnOutcome(d.Ticket, d.Text, SpeechOutcome.Completed(1, 1, ReaderMsPerPiece));

                (long Ticket, string Text) next;
                lock (_sync)
                {
                    if (_inFlight != null || _queue.Count == 0) return;
                    next = _queue[0];
                    _queue.RemoveAt(0);
                    if (ReaderRefuses)
                    {
                        _inFlight = null;
                    }
                    else
                    {
                        _inFlight = (next.Ticket, next.Text, now);
                        _started[next.Text] = now + ReaderFirstWordDelayMs;
                    }
                }
                if (ReaderRefuses && next.Ticket != 0)
                    Arbiter.OnOutcome(next.Ticket, next.Text,
                        SpeechOutcome.Unknown(SpeechUnknownReason.Refused, "asleep", 0));
            }

            public async Task Run(long until, Task? stopIf = null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (Clock.Now < until && stopIf?.IsCompleted != true)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the clock stopped moving");
                    ReaderStep();
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
                    ReaderStep();
                    if (!Clock.Step()) await Task.Delay(1);
                }
                return await task;
            }

            private sealed class Output : ICountdownOutput
            {
                private readonly Bench _bench;
                public Output(Bench bench) => _bench = bench;
                public void Tone(CountdownBeat beat) { lock (_bench.Tones) _bench.Tones.Add(_bench.Clock.Now); }
                public ICountdownWords Say(string text, bool onlyWarning)
                {
                    lock (_bench._requested) _bench._requested.Add((_bench.Clock.Now, text));
                    return _bench.Speech.Say(text, onlyWarning);
                }
                public void WithdrawSpeech(string why) => _bench.Speech.Withdraw(why);
            }
        }

        /// <summary>
        /// A virtual clock whose delays complete only when stepped, and which
        /// moves the arbiter's clock by the same amount, so estimates expire in
        /// step with the count.
        /// </summary>
        private sealed class Clock
        {
            private readonly object _sync = new();
            private readonly Queue<(int Ms, TaskCompletionSource Done)> _pending = new();
            private readonly Action<int> _alsoAdvance;
            private long _now;

            public Clock(Action<int> alsoAdvance) => _alsoAdvance = alsoAdvance;

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
                    if (!next.Done.Task.IsCompleted)
                    {
                        _now += next.Ms;
                        _alsoAdvance(next.Ms);
                    }
                }
                next.Done.TrySetResult();
                return true;
            }
        }
    }
}
