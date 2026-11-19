using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JJPortaudio;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The audition starts the way every Check your audio method starts (Sprint
    /// 49 Track Q2; Noel 2026-11-19 12:22: <i>"start recording after speech is
    /// done, start it with a beep or count it down like we've done in all the
    /// other stuff"</i>): the microphone opens only once the countdown has been
    /// said in full, and the take is marked by the computer microphone check's
    /// start tone, whose settle is left out of what is measured and played.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The countdown here is the REAL <see cref="CountdownRun"/>, the gate the
    /// window hands the take, on a virtual clock and with a reader the test
    /// holds back, so "the last number has not been said yet" is a state the
    /// test can stand in. The recorder is a fake that logs when it opens and
    /// writes a synthetic take on stop; no microphone is opened and no sound is
    /// played: the start marker is a logging action.
    /// </para>
    /// <para>
    /// <b>What these cannot see:</b> the real narrator's silence (source-pinned
    /// in <c>CheckYourAudioAuditionWiringTests</c>), what NVDA actually says, and
    /// whether the screen reader is still talking about something else when
    /// capture opens. Those are the harness walk's.
    /// </para>
    /// </remarks>
    public sealed class ChainAuditionStartTests : IDisposable
    {
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(450);
        private static readonly DateTime Now = new(2026, 11, 19, 12, 30, 0);
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-start-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ── Nothing opens before the count has been said ─────────────────

        [Fact]
        public async Task Capture_opens_only_after_the_countdowns_last_word_has_been_said_and_is_then_marked()
        {
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var output = new HeldLastNumberOutput();
            var plan = SpokenPlan(seconds: 3);
            var run = CountdownRun.Start(plan, output, "test", clock.NowMs, clock.Delay);
            var (take, rec) = Make(log, clock.NowMs);

            var started = take.StartAfterAsync(run.ReadyToKeyAsync, () => true, () => Now);

            // The count runs past its end with its last number still being said,
            // well short of the point it would give up on its words.
            await clock.StepUntil(() => clock.Now >= plan.EndsAtMs + 2000);
            Assert.True(output.LastNumberGiven, "positive control: the count reached its last number");
            Assert.False(started.IsCompleted);
            Assert.Equal(0, rec.Opens);
            Assert.Empty(log);

            // The reader finishes the last number: now, and only now, it opens.
            long saidAt = clock.Now;
            output.Release();
            Assert.True(await clock.RunUntil(started));
            Assert.Equal(new[] { "open", "marker" }, log.Select(e => e.What));
            Assert.True(log[0].At >= saidAt, "the microphone opened before the countdown's last word had been said");
            Assert.Equal(AuditionTakeState.Recording, take.State);
        }

        [Fact]
        public async Task A_count_whose_words_are_never_said_opens_nothing()
        {
            // With sounds off the words are the only warning, so a count that
            // cannot deliver them stops (CountdownRun.SpeechStalled): no take.
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var run = CountdownRun.Start(SpokenPlan(seconds: 2), new NeverFinishedOutput(), "test", clock.NowMs, clock.Delay);
            var (take, rec) = Make(log, clock.NowMs);

            var started = take.StartAfterAsync(run.ReadyToKeyAsync, () => true, () => Now);
            Assert.False(await clock.RunUntil(started));
            Assert.True(run.SpeechStalled, "positive control: the count stopped because its words were not said");
            Assert.Equal(0, rec.Opens);
            Assert.Empty(log);
            Assert.Equal(AuditionTakeState.Idle, take.State);
        }

        [Fact]
        public async Task A_count_called_off_or_a_start_no_longer_wanted_opens_nothing()
        {
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var run = CountdownRun.Start(SpokenPlan(seconds: 3), new HeldLastNumberOutput(), "test", clock.NowMs, clock.Delay);
            var (take, rec) = Make(log, clock.NowMs);
            var started = take.StartAfterAsync(run.ReadyToKeyAsync, () => true, () => Now);
            clock.Step();
            run.Cancel();   // Escape, Start or Ctrl+Enter during the count
            Assert.False(await clock.RunUntil(started));

            // The count ended, but the window is closing.
            Assert.False(await take.StartAfterAsync(() => Task.FromResult(true), () => false, () => Now));
            Assert.Equal(0, rec.Opens);
            Assert.Empty(log);
        }

        [Fact]
        public async Task With_the_countdown_off_capture_opens_at_once_and_is_still_marked()
        {
            // The window's gate is true at once when the operator has turned the
            // countdown off; the start tone is then the only marker, and it still sounds.
            var log = new List<(string What, long At)>();
            var (take, _) = Make(log, () => 0);
            bool before = false;
            Assert.True(await take.StartAfterAsync(() => Task.FromResult(true), () => true, () => Now,
                beforeOpening: () => { before = true; log.Add(("before", 0)); }));
            Assert.True(before);
            Assert.Equal(new[] { "before", "open", "marker" }, log.Select(e => e.What));
        }

        // ── The marker ───────────────────────────────────────────────────

        [Fact]
        public void The_start_marker_sounds_once_when_capture_opens_and_never_for_a_capture_that_did_not()
        {
            var log = new List<(string What, long At)>();
            var (take, rec) = Make(log, () => 0);
            Assert.True(take.Start(Now));
            Assert.Equal(new[] { "open", "marker" }, log.Select(e => e.What));

            take.StopRecording();
            log.Clear();
            rec.Refuse = true;
            Assert.False(take.Start(Now));
            Assert.Equal(new[] { "refused" }, log.Select(e => e.What));

            log.Clear();
            rec.Refuse = false;
            rec.OtherRecording = true;
            Assert.False(take.Start(Now));
            Assert.Empty(log);
        }

        [Fact]
        public void A_marker_that_throws_never_costs_the_take()
        {
            var rec = new FakeRecorder(new List<(string, long)>(), () => 0);
            var take = new ChainAuditionTake(_folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => throw new InvalidOperationException("no sound"), markerSettle: Settle);
            rec.Saved = p => take.Saved(p, out _);
            Assert.True(take.Start(Now));
            Assert.Equal(AuditionTakeState.Recording, take.State);
        }

        // ── The marker's settle is not the operator ──────────────────────

        [Fact]
        public void The_marker_and_its_settle_are_left_out_of_what_is_measured_and_played()
        {
            float[] speech = ChainAuditionAnalysisTests.Take(8);
            float[] recorded = WithLoudStart(speech);

            var marked = Recorded(recorded, marker: true);
            Assert.Equal(speech.Length, marked.Render(AuditionRender.Raw)!.Length);
            Assert.Equal(8.0, marked.Seconds, 3);
            Assert.True(Peak(marked.Render(AuditionRender.Raw)!) < 0.5f,
                "the start tone the microphone heard is still in what is played");
            Assert.Equal(AuditionSpeech.Heard, marked.Speech);

            // Positive control: without a marker nothing is left out, and the loud start is there.
            var unmarked = Recorded(recorded, marker: false);
            Assert.Equal(recorded.Length, unmarked.Render(AuditionRender.Raw)!.Length);
            Assert.True(Peak(unmarked.Render(AuditionRender.Raw)!) > 0.75f);
        }

        [Fact]
        public void A_take_no_longer_than_the_settle_reads_as_too_short_and_not_as_unreadable()
        {
            var result = Recorded(ChainAuditionAnalysisTests.Take(0.3), marker: true);
            Assert.Equal(AuditionSpeech.TooShort, result.Speech);
        }

        [Fact]
        public void The_settle_left_out_is_exactly_its_length_in_samples()
        {
            var x = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
            var kept = ChainAuditionTake.AfterMarker(x, 48000, Settle, marked: true);
            Assert.Equal(48000 - 21600, kept.Length);
            Assert.Equal(21600f, kept[0]);
            Assert.Same(x, ChainAuditionTake.AfterMarker(x, 48000, Settle, marked: false));
            Assert.Same(x, ChainAuditionTake.AfterMarker(x, 48000, TimeSpan.Zero, marked: true));
            Assert.Single(ChainAuditionTake.AfterMarker(new float[100], 48000, Settle, marked: true));
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private ChainAuditionResult Recorded(float[] samples, bool marker)
        {
            var log = new List<(string What, long At)>();
            var rec = new FakeRecorder(log, () => 0) { Samples = samples };
            string folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
            var take = new ChainAuditionTake(folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: marker ? () => log.Add(("marker", 0)) : null, markerSettle: Settle);
            rec.Saved = p => take.Saved(p, out _);
            Assert.True(take.Start(Now));
            take.StopRecording();
            Assert.Equal(AuditionTakeState.Ready, take.State);
            return take.Result!;
        }

        /// <summary>A full settle's worth of loud tone, as a start tone heard through speakers, then the take.</summary>
        private static float[] WithLoudStart(float[] take)
        {
            int n = (int)(Settle.TotalSeconds * ChainAuditionAnalysisTests.Rate);
            var x = new float[n + take.Length];
            for (int i = 0; i < n; i++) x[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 880 * i / ChainAuditionAnalysisTests.Rate);
            Array.Copy(take, 0, x, n, take.Length);
            return x;
        }

        private static float Peak(float[] x) => x.Length == 0 ? 0 : x.Max(v => Math.Abs(v));

        private static CountdownPlan SpokenPlan(int seconds) =>
            CountdownPlan.For(new CountdownSettings { Seconds = seconds }, CountdownVoicing.Spoken,
                leadIn: "Starting the audition in", numberText: n => n.ToString());

        private (ChainAuditionTake Take, FakeRecorder Recorder) Make(List<(string What, long At)> log, Func<long> now)
        {
            var rec = new FakeRecorder(log, now);
            var take = new ChainAuditionTake(_folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => { lock (log) log.Add(("marker", now())); }, markerSettle: Settle);
            rec.Saved = p => take.Saved(p, out _);
            return (take, rec);
        }

        /// <summary>Logs when it opens; writes <see cref="Samples"/> on stop and reports the file, as the narrator does.</summary>
        private sealed class FakeRecorder : IAuditionRecorder
        {
            private readonly List<(string What, long At)> _log;
            private readonly Func<long> _now;
            public FakeRecorder(List<(string What, long At)> log, Func<long> now) { _log = log; _now = now; }
            public float[] Samples = ChainAuditionAnalysisTests.Take(8);
            public bool Running, OtherRecording, Refuse;
            public int Opens;
            public string Path = "";
            public Action<string>? Saved;

            public bool IsRunning => Running || OtherRecording;
            public bool IsRecordingTo(string path) => Running && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);

            public bool Start(string path)
            {
                if (Refuse) { lock (_log) _log.Add(("refused", _now())); return false; }
                Opens++;
                Path = path;
                Running = true;
                lock (_log) _log.Add(("open", _now()));
                return true;
            }

            public void Stop()
            {
                if (!Running) return;
                Running = false;
                using (var w = new WavWriter(Path, ChainAuditionAnalysisTests.Rate, 1))
                {
                    w.Write(Samples, Samples.Length);
                    w.Finish();
                }
                Saved?.Invoke(Path);
            }
        }

        /// <summary>A reader that says everything at once except the last number, which it starts but holds unfinished until released.</summary>
        private sealed class HeldLastNumberOutput : ICountdownOutput
        {
            private volatile bool _released;
            public volatile bool LastNumberGiven;
            public void Release() => _released = true;
            public void Tone(CountdownBeat beat) { }
            public void WithdrawSpeech(string why) { }
            public ICountdownWords Say(string text, bool onlyWarning)
            {
                if (text != "1") return CountdownWords.Immediate;
                LastNumberGiven = true;
                return new Held(this);
            }

            private sealed class Held : ICountdownWords
            {
                private readonly HeldLastNumberOutput _owner;
                public Held(HeldLastNumberOutput owner) => _owner = owner;
                public bool Failed => false;
                public bool Started => true;
                public bool Finished => _owner._released;
            }
        }

        /// <summary>A reader that starts every piece and never finishes one.</summary>
        private sealed class NeverFinishedOutput : ICountdownOutput
        {
            public void Tone(CountdownBeat beat) { }
            public void WithdrawSpeech(string why) { }
            public ICountdownWords Say(string text, bool onlyWarning) => new Stuck();
            private sealed class Stuck : ICountdownWords
            {
                public bool Failed => false;
                public bool Started => true;
                public bool Finished => false;
            }
        }

        /// <summary>A virtual clock whose delays complete only when the test steps them (as <c>CountdownTests</c>' own).</summary>
        private sealed class SteppedClock
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

            /// <summary>Step until <paramref name="reached"/> holds.</summary>
            public async Task StepUntil(Func<bool> reached)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!reached())
                {
                    Assert.True(DateTime.UtcNow < deadline, "the clock never got there");
                    if (!Step()) await Task.Delay(1);
                }
            }

            public async Task<bool> RunUntil(Task<bool> task)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!task.IsCompleted)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the start never finished");
                    if (!Step()) await Task.Delay(1);
                }
                return await task;
            }
        }
    }
}
