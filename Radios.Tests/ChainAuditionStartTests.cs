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
    /// start tone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Track Q3 (Noel 2026-11-19 20:26 and 20:29):</b> the order is the
    /// countdown if on, "Recording" said in full, the start tone unless the
    /// recorder beeps are off, then the take; the FILE begins once the tone has
    /// finished, so nothing is trimmed afterwards and Keep saves exactly what
    /// was measured and played. The fake recorder below writes its file through
    /// the real recorder's own cut (<c>MicRecorder.WriteAfterLead</c>), block
    /// by block as a device delivers.
    /// </para>
    /// </remarks>
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
                beforeOpening: () => { before = true; log.Add(("before", 0)); return true; },
                startSaid: () => { log.Add(("Recording said", 0)); return Task.FromResult(true); }));
            Assert.True(before);
            Assert.Equal(new[] { "Recording said", "before", "open", "marker" }, log.Select(e => e.What));
        }

        [Fact]
        public async Task A_room_the_window_could_not_make_quiet_opens_nothing()
        {
            // Repair 2: the window answers whether everything of its own has
            // stopped sounding; while anything still sounds, nothing is opened
            // and no marker sounds, whether or not the countdown ran.
            var log = new List<(string What, long At)>();
            var (take, rec) = Make(log, () => 0);
            Assert.False(await take.StartAfterAsync(() => Task.FromResult(true), () => true, () => Now,
                beforeOpening: () => { log.Add(("before", 0)); return false; }));
            Assert.Equal(0, rec.Opens);
            Assert.Equal(new[] { "before" }, log.Select(e => e.What));
            Assert.Equal(AuditionTakeState.Idle, take.State);
            Assert.Equal("", take.TakePath);
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

        // ── Track Q3: "Recording", then the tone, then the file ──────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Recording_is_said_in_full_after_the_count_and_only_then_does_the_microphone_open_and_the_tone_sound(bool beepsOn)
        {
            // The countdown on: the real count, then the real wait for "Recording"
            // (SaidInFull, the window's), with a reader the test holds back.
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var plan = SpokenPlan(seconds: 2);
            var count = CountdownRun.Start(plan, new SaidAtOnceOutput(), "test", clock.NowMs, clock.Delay);
            var word = new HeldWord();
            var (take, rec) = Make(log, clock.NowMs, markerSounds: () => beepsOn);

            var started = take.StartAfterAsync(
                async () => { bool go = await count.ReadyToKeyAsync(); lock (log) log.Add(("count ended", clock.Now)); return go; },
                () => true, () => Now,
                beforeOpening: () => { lock (log) log.Add(("before", clock.Now)); return true; },
                startSaid: async () => SaidInFull.LetsItOpen(await SaidInFull.SayAsync(word.Say, word.Withdraw,
                    "Recording", onlyWarning: !beepsOn, () => true, clock.NowMs, clock.Delay)));

            // The count ends and "Recording" is given to the reader, but not yet finished.
            await clock.StepUntil(() => word.Given && clock.Now >= plan.EndsAtMs + 500);
            Assert.False(started.IsCompleted);
            Assert.Equal(0, rec.Opens);
            Assert.Equal(new[] { "count ended" }, log.Select(e => e.What));

            // The reader finishes it: now the microphone opens, then the tone (beeps on only).
            long saidAt = clock.Now;
            word.Finish();
            Assert.True(await clock.RunUntil(started));
            Assert.Equal(beepsOn ? new[] { "count ended", "before", "open", "marker" } : new[] { "count ended", "before", "open" },
                log.Select(e => e.What));
            Assert.True(log.Single(e => e.What == "open").At >= saidAt, "the microphone opened before \"Recording\" had been said");
            Assert.Equal("Recording", word.Text);
            Assert.False(word.Withdrawn);
            // The file begins once the tone has finished, or at once with no tone.
            Assert.Equal(beepsOn ? Settle : TimeSpan.Zero, rec.FileStartsAfter);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task With_the_countdown_off_Recording_still_comes_first_and_the_tone_follows_only_with_beeps_on(bool beepsOn)
        {
            var log = new List<(string What, long At)>();
            var (take, rec) = Make(log, () => 0, markerSounds: () => beepsOn);
            var word = new HeldWord(finishAtOnce: true, log);
            Assert.True(await take.StartAfterAsync(() => Task.FromResult(true), () => true, () => Now,
                beforeOpening: () => { log.Add(("before", 0)); return true; },
                startSaid: async () => SaidInFull.LetsItOpen(await SaidInFull.SayAsync(word.Say, word.Withdraw,
                    "Recording", onlyWarning: !beepsOn, () => true))));
            Assert.Equal(beepsOn ? new[] { "say Recording", "before", "open", "marker" } : new[] { "say Recording", "before", "open" },
                log.Select(e => e.What));
            Assert.Equal(beepsOn ? Settle : TimeSpan.Zero, rec.FileStartsAfter);
        }

        [Fact]
        public async Task Recording_never_said_opens_nothing_when_it_is_the_only_warning_and_is_dropped_when_the_tone_follows()
        {
            // Beeps off: "Recording" is the only warning, so words the reader
            // never finishes open nothing (as a spoken count keys nothing).
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var (take, rec) = Make(log, clock.NowMs, markerSounds: () => false);
            var word = new HeldWord();
            var started = take.StartAfterAsync(() => Task.FromResult(true), () => true, () => Now,
                startSaid: async () => SaidInFull.LetsItOpen(await SaidInFull.SayAsync(word.Say, word.Withdraw,
                    "Recording", onlyWarning: true, () => true, clock.NowMs, clock.Delay)));
            Assert.False(await clock.RunUntil(started));
            Assert.True(clock.Now >= CountdownRun.SpeechCeilingMs, "positive control: it waited out the ceiling first");
            Assert.True(word.Withdrawn);
            Assert.Equal(0, rec.Opens);

            // Beeps on: the tone is the warning, so the unsaid words are dropped and the take opens.
            clock = new SteppedClock();
            log.Clear();
            (take, rec) = Make(log, clock.NowMs, markerSounds: () => true);
            word = new HeldWord();
            started = take.StartAfterAsync(() => Task.FromResult(true), () => true, () => Now,
                startSaid: async () => SaidInFull.LetsItOpen(await SaidInFull.SayAsync(word.Say, word.Withdraw,
                    "Recording", onlyWarning: false, () => true, clock.NowMs, clock.Delay)));
            Assert.True(await clock.RunUntil(started));
            Assert.True(word.Withdrawn);
            Assert.Equal(new[] { "open", "marker" }, log.Select(e => e.What));
        }

        [Fact]
        public async Task A_start_called_off_while_Recording_is_being_said_opens_nothing_and_takes_the_word_back()
        {
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var (take, rec) = Make(log, clock.NowMs);
            var word = new HeldWord();
            bool wanted = true;
            var started = take.StartAfterAsync(() => Task.FromResult(true), () => wanted, () => Now,
                startSaid: async () => SaidInFull.LetsItOpen(await SaidInFull.SayAsync(word.Say, word.Withdraw,
                    "Recording", onlyWarning: false, () => wanted, clock.NowMs, clock.Delay)));
            await clock.StepUntil(() => word.Given);
            wanted = false;   // Escape, Start or Ctrl+Enter, or the window closing
            long calledOffAt = clock.Now;
            Assert.False(await clock.RunUntil(started));
            // Taken back at once, not left queued until the speech ceiling.
            Assert.True(clock.Now - calledOffAt <= 2 * CountdownRun.SpeechPollMs,
                "the wait for \"Recording\" did not notice the start being called off");
            Assert.True(word.Withdrawn);
            Assert.Equal(0, rec.Opens);
            Assert.Empty(log);
            Assert.Equal(AuditionTakeState.Idle, take.State);
        }

        [Fact]
        public async Task Words_that_never_reach_the_reader_are_not_waited_on()
        {
            var clock = new SteppedClock();
            bool withdrawn = false;
            var outcome = await SaidInFull.SayAsync((t, w) => CountdownWords.Undelivered, _ => withdrawn = true,
                "Recording", onlyWarning: true, () => true, clock.NowMs, clock.Delay);
            Assert.Equal(SaidInFull.Outcome.NotSaid, outcome);
            Assert.True(withdrawn);
            Assert.Equal(0, clock.Now);
            outcome = await SaidInFull.SayAsync((t, w) => throw new InvalidOperationException("no reader"), _ => { },
                "Recording", onlyWarning: false, () => true, clock.NowMs, clock.Delay);
            Assert.Equal(SaidInFull.Outcome.Dropped, outcome);
        }

        [Fact]
        public void The_file_begins_after_the_tone_so_what_is_kept_is_what_is_measured_and_played_and_holds_no_tone()
        {
            // What the microphone hears from the moment it opens: the start tone
            // through the speakers for a full settle, then the operator.
            float[] speech = ChainAuditionAnalysisTests.Take(8);
            float[] heard = WithLoudStart(speech);

            var (take, rec) = Recorded(heard, beepsOn: true);
            float[] file = ChainAuditionTakeTests.ReadMono(take.TakePath)!.Value.Mono;

            // No tone in the file, and the file is the operator from the first sample.
            Assert.Equal(speech.Length, file.Length);
            Assert.True(Peak(file) < 0.5f, "the start tone the microphone heard is in the take's file");
            Assert.True(MaxDifference(file, speech) < 1e-3f, "the file does not begin where the tone ends");

            // What is measured and played is the file itself, nothing trimmed.
            var result = take.Result!;
            Assert.Equal(file.Length / (double)ChainAuditionAnalysisTests.Rate, result.Seconds, 6);
            float[] raw = result.Render(AuditionRender.Raw)!;
            Assert.Equal(file.Length, raw.Length);
            Assert.True(MaxDifference(raw, file.Select(v => v * result.PlaybackScale).ToArray()) < 1e-5f,
                "Play raw is not the take's file");
            Assert.Equal(AuditionSpeech.Heard, result.Speech);

            // Keep saves those same samples, byte for byte.
            string kept = Path.Combine(_folder, "kept", "take.wav");
            Assert.True(take.Keep(kept));
            Assert.Equal(File.ReadAllBytes(take.TakePath), File.ReadAllBytes(kept));
            Assert.Equal(file, ChainAuditionTakeTests.ReadMono(kept)!.Value.Mono);
            Assert.Equal(Settle, rec.FileStartsAfter);
        }

        [Fact]
        public void With_the_recorder_beeps_off_there_is_no_tone_and_the_file_starts_as_the_microphone_opens()
        {
            // Nothing sounds, so nothing is cut: every captured sample is the take.
            float[] heard = ChainAuditionAnalysisTests.Take(8);
            var (take, rec) = Recorded(heard, beepsOn: false);
            Assert.Equal(TimeSpan.Zero, rec.FileStartsAfter);
            Assert.Equal(0, rec.MarkersSounded);
            float[] file = ChainAuditionTakeTests.ReadMono(take.TakePath)!.Value.Mono;
            Assert.Equal(heard.Length, file.Length);
            Assert.Equal(file.Length, take.Result!.Render(AuditionRender.Raw)!.Length);

            // Positive control for the no-tone test above: had a tone been heard
            // here, it would be in the file, and the instrument sees it.
            var (loud, _) = Recorded(WithLoudStart(heard), beepsOn: false);
            Assert.True(Peak(ChainAuditionTakeTests.ReadMono(loud.TakePath)!.Value.Mono) > 0.75f);
        }

        [Fact]
        public void The_recorder_writes_nothing_of_the_capture_before_its_lead_and_everything_after_it()
        {
            // The real recorder's cut, block by block, stereo, the lead falling
            // inside a block: the file's first frame is exactly the lead's.
            const int rate = 48000, channels = 2, block = 441;
            long lead = MicRecorder.LeadFrames(Settle, rate);
            Assert.Equal(21600, lead);
            int frames = rate;   // one second captured
            string path = Path.Combine(_folder, "cut.wav");
            Directory.CreateDirectory(_folder);
            using (var w = new WavWriter(path, rate, channels))
            {
                long captured = 0;
                for (int at = 0; at < frames; at += block)
                {
                    int n = Math.Min(block, frames - at);
                    var buf = new float[n * channels];
                    for (int i = 0; i < n; i++)
                    {
                        // Each frame carries its own index, so the first one written says where the cut fell.
                        buf[i * channels] = (at + i) / 65536f;
                        buf[i * channels + 1] = -(at + i) / 65536f;
                    }
                    captured = MicRecorder.WriteAfterLead(w, buf, n * channels, n, captured, lead);
                }
                Assert.Equal(frames, captured);
                Assert.Equal(frames - lead, w.Frames);
                w.Finish();
            }
            byte[] bytes = File.ReadAllBytes(path);
            short first = BitConverter.ToInt16(bytes, 44);
            short firstRight = BitConverter.ToInt16(bytes, 46);
            Assert.Equal((short)(21600 / 65536f * 32767f), first);
            Assert.Equal((short)(-21600 / 65536f * 32767f), firstRight);

            // No lead keeps every frame; a lead past the capture keeps none.
            Assert.Equal(0, MicRecorder.LeadFrames(TimeSpan.Zero, rate));
            Assert.Equal(0, MicRecorder.LeadFrames(TimeSpan.FromMilliseconds(-5), rate));
            using (var w = new WavWriter(Path.Combine(_folder, "all.wav"), rate, 1))
            {
                MicRecorder.WriteAfterLead(w, new float[100], 100, 100, 0, 0);
                Assert.Equal(100, w.Frames);
                MicRecorder.WriteAfterLead(w, new float[100], 100, 100, 100, 1000);
                Assert.Equal(100, w.Frames);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>A take whose microphone hears <paramref name="heard"/> from the moment it opens, recorded and stopped.</summary>
        private (ChainAuditionTake Take, FakeRecorder Recorder) Recorded(float[] heard, bool beepsOn)
        {
            var log = new List<(string What, long At)>();
            var rec = new FakeRecorder(log, () => 0) { Samples = heard };
            string folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
            var take = new ChainAuditionTake(folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => rec.MarkersSounded++, markerSettle: Settle, markerSounds: () => beepsOn);
            rec.Saved = p => take.Saved(p, out _);
            Assert.True(take.Start(Now));
            take.StopRecording();
            Assert.Equal(AuditionTakeState.Ready, take.State);
            return (take, rec);
        }

        private static float MaxDifference(float[] a, float[] b)
        {
            Assert.Equal(a.Length, b.Length);
            float most = 0;
            for (int i = 0; i < a.Length; i++) most = Math.Max(most, Math.Abs(a[i] - b[i]));
            return most;
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

        private (ChainAuditionTake Take, FakeRecorder Recorder) Make(List<(string What, long At)> log, Func<long> now,
            Func<bool>? markerSounds = null)
        {
            var rec = new FakeRecorder(log, now);
            var take = new ChainAuditionTake(_folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => { lock (log) log.Add(("marker", now())); }, markerSettle: Settle, markerSounds: markerSounds);
            rec.Saved = p => take.Saved(p, out _);
            return (take, rec);
        }

        /// <summary>
        /// Logs when it opens; on stop, writes what the microphone heard
        /// (<see cref="Samples"/>, from the moment it opened) through the real
        /// recorder's cut, block by block as a device delivers, and reports the
        /// file, as the narrator does.
        /// </summary>
        private sealed class FakeRecorder : IAuditionRecorder
        {
            private readonly List<(string What, long At)> _log;
            private readonly Func<long> _now;
            public FakeRecorder(List<(string What, long At)> log, Func<long> now) { _log = log; _now = now; }
            public float[] Samples = ChainAuditionAnalysisTests.Take(8);
            public bool Running, OtherRecording, Refuse;
            public int Opens, MarkersSounded;
            public TimeSpan FileStartsAfter;
            public string Path = "";
            public Action<string>? Saved;

            public bool IsRunning => Running || OtherRecording;
            public bool IsRecordingTo(string path) => Running && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);

            public bool Start(string path, TimeSpan fileStartsAfter)
            {
                if (Refuse) { lock (_log) _log.Add(("refused", _now())); return false; }
                Opens++;
                Path = path;
                FileStartsAfter = fileStartsAfter;
                Running = true;
                lock (_log) _log.Add(("open", _now()));
                return true;
            }

            public void Stop()
            {
                if (!Running) return;
                Running = false;
                const int block = 441;   // not a divisor of the lead, so the cut falls inside a block
                long lead = MicRecorder.LeadFrames(FileStartsAfter, ChainAuditionAnalysisTests.Rate);
                using (var w = new WavWriter(Path, ChainAuditionAnalysisTests.Rate, 1))
                {
                    long captured = 0;
                    for (int at = 0; at < Samples.Length; at += block)
                    {
                        int n = Math.Min(block, Samples.Length - at);
                        var buf = new float[n];
                        Array.Copy(Samples, at, buf, 0, n);
                        captured = MicRecorder.WriteAfterLead(w, buf, n, n, captured, lead);
                    }
                    w.Finish();
                }
                Saved?.Invoke(Path);
            }
        }

        /// <summary>"Recording", given to a reader the test holds back until <see cref="Finish"/>, or says at once.</summary>
        private sealed class HeldWord
        {
            private readonly bool _finishAtOnce;
            private readonly List<(string What, long At)>? _log;
            private volatile bool _finished;
            public HeldWord(bool finishAtOnce = false, List<(string What, long At)>? log = null) { _finishAtOnce = finishAtOnce; _log = log; }
            public volatile bool Given, Withdrawn;
            public string Text = "";
            public void Finish() => _finished = true;

            public ICountdownWords Say(string text, bool onlyWarning)
            {
                Text = text;
                Given = true;
                _log?.Add(("say " + text, 0));
                return new Handle(this);
            }

            public void Withdraw(string why) => Withdrawn = true;

            private sealed class Handle : ICountdownWords
            {
                private readonly HeldWord _owner;
                public Handle(HeldWord owner) => _owner = owner;
                public bool Failed => _owner.Withdrawn;
                public bool Started => true;
                public bool Finished => _owner._finishAtOnce || _owner._finished;
            }
        }

        /// <summary>A reader that says every piece of the count at once.</summary>
        private sealed class SaidAtOnceOutput : ICountdownOutput
        {
            public void Tone(CountdownBeat beat) { }
            public void WithdrawSpeech(string why) { }
            public ICountdownWords Say(string text, bool onlyWarning) => CountdownWords.Immediate;
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
