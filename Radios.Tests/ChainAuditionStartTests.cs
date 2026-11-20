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
    /// the real recorder's own file start (<c>CaptureFileStart</c>), block by
    /// block as a device delivers.
    /// </para>
    /// <para>
    /// <b>Repair 1 (Codex's check of 83932446):</b> "finished" is the tone's
    /// real end as its marker reports it, not a fixed 450 ms (C1), and
    /// "Recording" not heard in full opens nothing, every time (C2).
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
        public void A_marker_that_throws_never_costs_the_take_and_the_file_starts_at_once()
        {
            // Nothing sounded, so nothing is held back: the file begins with the first frame.
            var rec = new FakeRecorder(new List<(string, long)>(), () => 0);
            var take = new ChainAuditionTake(_folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => throw new InvalidOperationException("no sound"));
            rec.Saved = p => take.Saved(p, out _);
            Assert.True(take.Start(Now));
            Assert.Equal(AuditionTakeState.Recording, take.State);
            Assert.True(rec.Hold);
            Assert.Equal(1, rec.BeginCalls);
            Assert.Null(rec.BeginAt);
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
            // The file waits for the tone's end, or begins at once with no tone.
            Assert.Equal(beepsOn, rec.Hold);
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
            Assert.Equal(beepsOn, rec.Hold);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Recording_never_said_opens_nothing_whether_or_not_the_tone_follows(bool beepsOn)
        {
            // Repair 1 (Codex's check of 83932446, C2): "Recording" is said in
            // full first, every time. Track Q3 let a beeped start go ahead
            // without it; now neither does.
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var (take, rec) = Make(log, clock.NowMs, markerSounds: () => beepsOn);
            var word = new HeldWord();
            var started = take.StartAfterAsync(() => Task.FromResult(true), () => true, () => Now,
                startSaid: async () => SaidInFull.LetsItOpen(await SaidInFull.SayAsync(word.Say, word.Withdraw,
                    "Recording", onlyWarning: !beepsOn, () => true, clock.NowMs, clock.Delay)));
            Assert.False(await clock.RunUntil(started));
            Assert.True(clock.Now >= CountdownRun.SpeechCeilingMs, "positive control: it waited out the ceiling first");
            Assert.True(word.Withdrawn);
            Assert.Equal(0, rec.Opens);
            Assert.Empty(log);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Recording_cut_part_way_opens_nothing_at_once(bool beepsOn)
        {
            // A word the reader reports cut fails its handle (the real arbiter's
            // case is RecordingHeardInFullTests): no wait for the ceiling, no take.
            var clock = new SteppedClock();
            var log = new List<(string What, long At)>();
            var (take, rec) = Make(log, clock.NowMs, markerSounds: () => beepsOn);
            var word = new HeldWord();
            var started = take.StartAfterAsync(() => Task.FromResult(true), () => true, () => Now,
                startSaid: async () => SaidInFull.LetsItOpen(await SaidInFull.SayAsync(word.Say, word.Withdraw,
                    "Recording", onlyWarning: !beepsOn, () => true, clock.NowMs, clock.Delay)));
            await clock.StepUntil(() => word.Given);
            long cutAt = clock.Now;
            word.Cut();
            Assert.False(await clock.RunUntil(started));
            Assert.True(clock.Now - cutAt <= 2 * CountdownRun.SpeechPollMs, "a cut word was waited on");
            Assert.Equal(0, rec.Opens);
            Assert.Empty(log);
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
        public async Task Words_that_never_reach_the_reader_are_not_waited_on_and_open_nothing()
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
            Assert.Equal(SaidInFull.Outcome.NotSaid, outcome);
            Assert.False(SaidInFull.LetsItOpen(outcome));
        }

        // ── Repair 1: the file begins where the tone really ends ─────────

        [Theory]
        [InlineData(90)]
        [InlineData(800)]
        public void The_file_begins_after_the_tones_real_end_so_what_is_kept_is_what_is_measured_and_played_and_holds_no_tone(int outputLagMs)
        {
            // Codex's check of 83932446, C1, at the take: the tone reaches the
            // speaker after the output's lag, and the marker reports when it is
            // audibly over. Track Q3 assumed a loud tone lasting exactly 450 ms
            // (WithLoudStart); here the tone is 160 ms and ends where the lag
            // puts it, and the operator starts 150 ms after it, faster than a
            // person answers a tone. (The real rendered beep, through the real
            // output position, is JJFlexWpf.Tests' AuditionToneEndTests.)
            const double handedOverMs = 25, toneMs = 160, reactionMs = 150;
            double toneEndMs = handedOverMs + outputLagMs + toneMs;
            float[] speech = ChainAuditionAnalysisTests.Take(8);
            float[] heard = Heard(handedOverMs + outputLagMs, toneMs, reactionMs, speech);

            var (take, rec) = Recorded(heard, markerEnd: Ticks(toneEndMs));
            float[] file = ChainAuditionTakeTests.ReadMono(take.TakePath)!.Value.Mono;

            // The file begins the margin after the tone's end, to the frame.
            long expectedFirst = (long)Math.Ceiling((toneEndMs + ChainAuditionTake.ToneMarginMs) * Rate / 1000.0);
            Assert.InRange(rec.BoundaryFrame, expectedFirst - 1, expectedFirst + 1);
            Assert.Equal(heard.Length - rec.BoundaryFrame, file.Length);

            // No tone in it, and every sample of the operator's speech.
            Assert.True(Peak(file) < 0.5f, "the start tone the microphone heard is in the take's file");
            Assert.True(file.Length >= speech.Length, "the operator's first words were thrown away");
            Assert.True(MaxDifference(file.Skip(file.Length - speech.Length).ToArray(), speech) < 1e-3f,
                "the operator's speech is not whole in the file");
            Assert.True(file.Take(file.Length - speech.Length).All(v => Math.Abs(v) < 1e-3f),
                "something other than silence lies between the tone's end and the speech");

            // What is measured and played is the file itself, nothing trimmed.
            var result = take.Result!;
            Assert.Equal(file.Length / (double)Rate, result.Seconds, 6);
            float[] raw = result.Render(AuditionRender.Raw)!;
            Assert.Equal(file.Length, raw.Length);
            Assert.True(MaxDifference(raw, file.Select(v => v * result.PlaybackScale).ToArray()) < 1e-5f,
                "Play raw is not the take's file");
            Assert.Equal(AuditionSpeech.Heard, result.Speech);

            // Keep saves those same samples, byte for byte.
            string kept = Path.Combine(_folder, "kept-" + outputLagMs, "take.wav");
            Assert.True(take.Keep(kept));
            Assert.Equal(File.ReadAllBytes(take.TakePath), File.ReadAllBytes(kept));
            Assert.Equal(file, ChainAuditionTakeTests.ReadMono(kept)!.Value.Mono);
            Assert.True(rec.Hold);
        }

        [Fact]
        public void With_the_recorder_beeps_off_there_is_no_tone_and_the_file_starts_as_the_microphone_opens()
        {
            // Nothing sounds, so nothing is held: every captured sample is the take.
            float[] heard = ChainAuditionAnalysisTests.Take(8);
            var (take, rec) = Recorded(heard, markerEnd: null, beepsOn: false);
            Assert.False(rec.Hold);
            Assert.Equal(0, rec.MarkersSounded);
            Assert.Equal(0, rec.BeginCalls);
            float[] file = ChainAuditionTakeTests.ReadMono(take.TakePath)!.Value.Mono;
            Assert.Equal(heard.Length, file.Length);
            Assert.Equal(file.Length, take.Result!.Render(AuditionRender.Raw)!.Length);

            // Positive control for the no-tone tests above: had a tone been heard
            // here, it would be in the file, and the instrument sees it.
            var (loud, _) = Recorded(Heard(25, 160, 150, heard), markerEnd: null, beepsOn: false);
            Assert.True(Peak(ChainAuditionTakeTests.ReadMono(loud.TakePath)!.Value.Mono) > 0.75f);
        }

        [Fact]
        public void A_marker_that_reports_no_tone_sounded_begins_the_file_with_the_first_frame()
        {
            // The beeps were on when asked, but nothing sounded (every sound off):
            // nothing is held back, so nothing of the operator is lost.
            float[] heard = ChainAuditionAnalysisTests.Take(8);
            var (take, rec) = Recorded(heard, markerEnd: null);
            Assert.True(rec.Hold);
            Assert.Equal(1, rec.BeginCalls);
            Assert.Null(rec.BeginAt);
            Assert.Equal(heard.Length, ChainAuditionTakeTests.ReadMono(take.TakePath)!.Value.Mono.Length);
        }

        [Fact]
        public async Task The_file_waits_for_the_marker_to_say_when_its_tone_ended()
        {
            // The output reports the tone's end only once it has taken the tone:
            // until then the recorder is told nothing, and holds what it captures.
            var log = new List<(string What, long At)>();
            var rec = new FakeRecorder(log, () => 0);
            var end = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var take = new ChainAuditionTake(_folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => end.Task);
            Assert.True(take.Start(Now));
            Assert.True(rec.Hold);
            Assert.Equal(0, rec.BeginCalls);
            end.SetResult(Ticks(400));
            await rec.Begun.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, rec.BeginCalls);
            Assert.Equal(ChainAuditionTake.FileStartAfterTone(Ticks(400)), rec.BeginAt);
            Assert.Equal(ChainAuditionTake.ToneMarginMs * Freq / 1000, rec.BeginAt!.Value - Ticks(400));
        }

        [Fact]
        public async Task A_marker_whose_report_faults_leaves_the_file_starting_well_after_any_tone()
        {
            var rec = new FakeRecorder(new List<(string, long)>(), () => 0);
            var end = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var take = new ChainAuditionTake(_folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => end.Task);
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.True(take.Start(Now));
            long after = System.Diagnostics.Stopwatch.GetTimestamp();
            end.SetException(new InvalidOperationException("no position"));
            await rec.Begun.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long unknown = Ticks(ChainAuditionTake.UnknownToneEndMs);
            Assert.InRange(rec.BeginAt!.Value, before + unknown, after + unknown);
        }

        // ── Repair 1: the recorder's own file start (CaptureFileStart) ───

        [Fact]
        public void The_file_begins_at_the_frame_captured_at_the_moment_it_is_given_however_late_the_blocks_arrive()
        {
            // Stereo, MicProbe's 1024-frame blocks, a reported input latency of
            // 30 ms, a first block that arrives 40 ms late (the backlog when the
            // recorder's sink is installed after the device started), and the
            // rest jittered. Frame 0 was captured at origin; each frame carries
            // its own index, so the first one written says where the file began.
            const int rate = 48000, channels = 2, block = 1024;
            const double latency = 0.030;
            long origin = Ticks(5000);
            int[] jitterMs = { 40, 0, 3, 1, 6, 2, 0, 4 };
            long moment = origin + Ticks(337.5);
            const long expected = 16200;   // 337.5 ms at 48 kHz
            int frames = rate;   // one second captured
            string path = Path.Combine(_folder, "cut.wav");
            Directory.CreateDirectory(_folder);
            var start = new CaptureFileStart(rate, hold: true, latency, Freq);
            using (var w = new WavWriter(path, rate, channels))
            {
                int k = 0;
                for (int at = 0; at < frames; at += block, k++)
                {
                    int n = Math.Min(block, frames - at);
                    var buf = new float[n * channels];
                    for (int i = 0; i < n; i++)
                    {
                        buf[i * channels] = (at + i) / 65536f;
                        buf[i * channels + 1] = -(at + i) / 65536f;
                    }
                    long arrival = origin + Ticks((at + n) * 1000.0 / rate + latency * 1000 + jitterMs[k % jitterMs.Length]);
                    // The moment is learnt part-way through, as the output reports it.
                    if (k == 3) start.BeginAt(moment);
                    // Repair 2: a block on time reports no backlog; a late one does not say.
                    int jitter = jitterMs[k % jitterMs.Length];
                    // Repair 3: an on-time block's answer comes at once after its stamp.
                    start.Block(w, buf, n * channels, n, new CaptureBlockTiming(arrival, jitter == 0 ? 0 : -1, streamStartedAt: origin - Ticks(5),
                        backlogAnsweredAt: jitter == 0 ? arrival : null));
                }
                Assert.False(start.Finish(w));
                Assert.True(start.FileBegan);
                Assert.Equal(frames, start.CapturedFrames);
                Assert.InRange(start.BoundaryFrame, expected, expected + 1);
                Assert.Equal(frames - start.BoundaryFrame, w.Frames);
                w.Finish();
            }
            byte[] bytes = File.ReadAllBytes(path);
            short first = BitConverter.ToInt16(bytes, 44);
            short firstRight = BitConverter.ToInt16(bytes, 46);
            long b = start.BoundaryFrame;
            Assert.Equal((short)(b / 65536f * 32767f), first);
            Assert.Equal((short)(-b / 65536f * 32767f), firstRight);
        }

        [Fact]
        public void Without_a_hold_every_frame_is_the_files_and_a_hold_released_with_no_moment_keeps_every_frame()
        {
            Directory.CreateDirectory(_folder);
            using (var w = new WavWriter(Path.Combine(_folder, "all.wav"), 1000, 1))
            {
                var open = new CaptureFileStart(1000, hold: false, 0, Freq);
                Assert.True(open.FileBegan);
                open.Block(w, new float[100], 100, 100, OnTime(Ticks(100)));
                Assert.Equal(100, w.Frames);
                Assert.Equal(0, open.BoundaryFrame);
            }
            using (var w = new WavWriter(Path.Combine(_folder, "held.wav"), 1000, 1))
            {
                var held = new CaptureFileStart(1000, hold: true, 0, Freq);
                held.Block(w, new float[100], 100, 100, OnTime(Ticks(100)));
                Assert.Equal(0, w.Frames);
                Assert.False(held.FileBegan);
                held.BeginAt(null);       // no tone sounded
                held.BeginAt(Ticks(50));  // only the first call counts
                held.Block(w, new float[100], 100, 100, OnTime(Ticks(200)));
                Assert.Equal(200, w.Frames);
                Assert.Equal(0, held.BoundaryFrame);
            }
        }

        [Fact]
        public void A_recording_stopped_before_its_moment_was_captured_keeps_nothing_and_says_so()
        {
            Directory.CreateDirectory(_folder);
            using var w = new WavWriter(Path.Combine(_folder, "early.wav"), 1000, 1);
            var start = new CaptureFileStart(1000, hold: true, 0, Freq);
            start.Block(w, new float[100], 100, 100, OnTime(Ticks(100)));
            start.BeginAt(Ticks(450));
            start.Block(w, new float[100], 100, 100, OnTime(Ticks(200)));
            Assert.False(start.Finish(w));
            Assert.False(start.FileBegan);
            Assert.Equal(0, w.Frames);
            Assert.Equal(200, start.CapturedFrames);
        }

        [Fact]
        public void A_moment_learnt_after_the_last_block_is_still_applied_when_the_recording_stops()
        {
            Directory.CreateDirectory(_folder);
            using var w = new WavWriter(Path.Combine(_folder, "late.wav"), 1000, 1);
            var start = new CaptureFileStart(1000, hold: true, 0, Freq);
            for (int k = 1; k <= 5; k++) start.Block(w, new float[100], 100, 100, OnTime(Ticks(k * 100)));
            start.BeginAt(Ticks(320));
            Assert.True(start.Finish(w));
            Assert.InRange(start.BoundaryFrame, 320, 321);
            Assert.Equal(500 - start.BoundaryFrame, w.Frames);
        }

        [Fact]
        public void A_hold_never_told_its_moment_keeps_only_the_last_ten_seconds()
        {
            Directory.CreateDirectory(_folder);
            using var w = new WavWriter(Path.Combine(_folder, "cap.wav"), 1000, 1);
            var start = new CaptureFileStart(1000, hold: true, 0, Freq);
            for (int k = 1; k <= 12; k++) start.Block(w, new float[1000], 1000, 1000, OnTime(Ticks(k * 1000)));
            Assert.Equal(2000, start.DroppedFrames);
            start.BeginAt(null);
            Assert.True(start.Finish(w));
            Assert.Equal((long)(CaptureFileStart.MaxHeldSeconds * 1000), w.Frames);
        }

        // ── Repair 2: the capture's timing must be established (C1b) ────

        /// <summary>
        /// A take at 1 kHz for arithmetic that reads: frame 0 captured at
        /// <c>origin</c>, 100-frame blocks, no input latency, the moment at
        /// 337 ms, so the true file start is frame 337. The reader is
        /// <paramref name="lagMs"/> behind while <paramref name="behind"/> says
        /// so; <paramref name="backlog"/> says what each block reports.
        /// </summary>
        private static CaptureFileStart Run(int lagMs, Func<int, bool> behind, Func<int, int, long> backlog,
            long? streamStartedAt, int totalFrames, out long written, Action<CaptureFileStart, int>? afterBlock = null)
        {
            const int rate = 1000, block = 100;
            long origin = Ticks(10_000);
            var start = new CaptureFileStart(rate, hold: true, 0, Freq);
            start.BeginAt(origin + Ticks(337));
            string path = Path.Combine(Path.GetTempPath(), "jjflex-q3r2-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                using var w = new WavWriter(path, rate, 1);
                for (int at = 0, k = 0; at < totalFrames; at += block, k++)
                {
                    int lag = behind(at) ? lagMs : 0;
                    long arrival = origin + Ticks(at + block + lag);
                    // Repair 3: a reported backlog's answer comes at once after the stamp.
                    long reported = backlog(at, lag);
                    start.Block(w, new float[block], block, block,
                        new CaptureBlockTiming(arrival, reported, streamStartedAt: streamStartedAt.HasValue ? origin + streamStartedAt.Value : null,
                            backlogAnsweredAt: reported >= 0 ? arrival : null));
                    afterBlock?.Invoke(start, at + block);
                }
                start.Finish(w);
                written = w.Frames;
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
            return start;
        }

        [Fact]
        public void A_reader_behind_through_the_lead_waits_for_a_block_that_establishes_the_timing()
        {
            // Behind by 120 ms for the first half second, saying nothing about
            // it; caught up after. Repair 1 placed the file by the loose bound,
            // 120 ms early. Now nothing is written until a caught-up block
            // establishes the timing, and the file begins at the true frame.
            bool begunBeforeEstablished = false;
            var start = Run(120, at => at < 500, (at, lag) => lag == 0 ? 0 : -1, streamStartedAt: Ticks(-10), totalFrames: 2000,
                out long written, (s, through) => { if (s.FileBegan && !s.TimingEstablished) begunBeforeEstablished = true; });
            Assert.False(begunBeforeEstablished);
            Assert.True(start.TimingEstablished);
            Assert.Equal(337, start.BoundaryFrame);
            Assert.Equal(2000 - 337, written);
        }

        [Fact]
        public void A_backlog_the_device_reports_comes_off_its_blocks_bound()
        {
            // Behind by 120 ms throughout, and the device says so on every
            // block. No block arrives caught up, so the timing never counts as
            // established; the file begins by the stream's start. The known
            // backlog also comes off each bound: with it ignored, the precise
            // reading would sit 120 ms late (positive control below).
            var start = Run(120, at => true, (at, lag) => lag, streamStartedAt: Ticks(-10), totalFrames: 3000, out _);
            Assert.False(start.TimingEstablished);
            Assert.Equal(347, start.BoundaryFrame);   // 10 ms after the truth, by the stream's start
            // The backlog taken off: the precise reading is exact.
            Assert.Equal(337, start.FrameAt(Ticks(10_000) + Ticks(337)));
        }

        [Fact]
        public void A_small_reported_backlog_establishes_the_timing_and_is_taken_off_its_bound()
        {
            // Every block 20 ms late with its 20 frames reported waiting behind
            // it: smaller than the block, so each establishes the timing, and
            // taking the backlog off makes the reading exact. Ignoring it would
            // start the file 20 ms early.
            var start = Run(20, at => true, (at, lag) => lag, streamStartedAt: Ticks(-10), totalFrames: 2000, out _);
            Assert.True(start.TimingEstablished);
            Assert.Equal(337, start.BoundaryFrame);
        }

        [Fact]
        public void Never_established_the_file_begins_by_the_streams_start_later_never_earlier_and_only_after_the_wait()
        {
            // Behind by 120 ms throughout and never saying so: every upper bound
            // is 120 ms late. The file begins by the lower bound (the stream
            // started 10 ms before frame 0), so 10 ms LATE rather than 120 ms
            // early, and only once a further second has been captured for a
            // block that never came.
            long firstWrittenAt = -1;
            var start = Run(120, at => true, (at, lag) => -1, streamStartedAt: Ticks(-10), totalFrames: 3000,
                out long written, (s, through) => { if (firstWrittenAt < 0 && s.FileBegan) firstWrittenAt = through; });
            Assert.False(start.TimingEstablished);
            Assert.Equal(347, start.BoundaryFrame);
            Assert.Equal(3000 - 347, written);
            Assert.True(firstWrittenAt >= 347 + CaptureFileStart.EstablishWithinSeconds * 1000,
                "the file began before the wait for an establishing block was over: " + firstWrittenAt);
            // The precise reading it refused: 120 ms early.
            Assert.Equal(217, start.FrameAt(Ticks(10_000) + Ticks(337)));
        }

        [Fact]
        public void Never_established_a_recording_stopped_within_the_wait_still_begins_by_the_streams_start()
        {
            var start = Run(120, at => true, (at, lag) => -1, streamStartedAt: Ticks(-10), totalFrames: 600, out long written);
            Assert.True(start.FileBegan);
            Assert.Equal(347, start.BoundaryFrame);
            Assert.Equal(600 - 347, written);
        }

        [Fact]
        public void Never_established_and_with_no_lower_bound_the_file_does_not_begin()
        {
            // Nothing but upper bounds, none established: there is no reading
            // that keeps the file from starting early, so it does not start.
            var start = Run(120, at => true, (at, lag) => -1, streamStartedAt: null, totalFrames: 3000, out long written);
            Assert.False(start.FileBegan);
            Assert.Equal(0, written);
        }

        [Fact]
        public void A_block_after_lost_input_does_not_establish_the_timing()
        {
            Directory.CreateDirectory(_folder);
            using var w = new WavWriter(Path.Combine(_folder, "overflow.wav"), 1000, 1);
            var start = new CaptureFileStart(1000, hold: true, 0, Freq);
            start.Block(w, new float[100], 100, 100, new CaptureBlockTiming(Ticks(100), 0, overflowed: true, streamStartedAt: 0, backlogAnsweredAt: Ticks(100)));
            Assert.False(start.TimingEstablished);
            start.Block(w, new float[100], 100, 100, OnTime(Ticks(200)));
            Assert.True(start.TimingEstablished);
        }

        // -- Repair 3 (Codex's re-check of b6543ac1, C1b) --------------------

        /// <summary>
        /// A backlog is paired with the clock reading taken BEFORE it was asked,
        /// and only when its answer came within the block's own duration. One
        /// true capture (frame 0 at 10 s, 100-frame blocks at 1 kHz, no input
        /// latency, the moment at 337 ms) and an honest device: every backlog is
        /// what was waiting when it was asked. One 500 ms thread pause on the
        /// first block, at each place it can fall; afterwards the reader catches
        /// up, reading back to back. Nowhere may the file begin before frame 337.
        /// </summary>
        [Theory]
        [InlineData("none")]
        [InlineData("before the stamp")]
        [InlineData("between the stamp and the answer")]
        [InlineData("after the answer, before its reading")]
        [InlineData("repair 2's order, no answer time")]
        public void A_pause_anywhere_around_the_backlog_question_cannot_start_the_file_early(string where)
        {
            const int rate = 1000, block = 100;
            long origin = Ticks(10_000), pause = Ticks(500);
            var start = new CaptureFileStart(rate, hold: true, 0, Freq);
            start.BeginAt(origin + Ticks(337));
            Directory.CreateDirectory(_folder);
            using var w = new WavWriter(Path.Combine(_folder, "pause-" + Guid.NewGuid().ToString("N") + ".wav"), rate, 1);
            long previous = 0;
            for (int at = 0; at < 3000; at += block)
            {
                long readReturned = Math.Max(origin + Ticks(at + block), previous + Ticks(0.1));
                long stamp = readReturned, asked = readReturned, answered = readReturned;
                bool answerKnown = true;
                if (at == 0)
                {
                    switch (where)
                    {
                        case "before the stamp": stamp = asked = answered = readReturned + pause; break;
                        case "between the stamp and the answer": asked = answered = readReturned + pause; break;
                        case "after the answer, before its reading": answered = readReturned + pause; break;
                        // Repair 2 asked first and stamped after; the pause sat between them, unseen.
                        case "repair 2's order, no answer time": stamp = readReturned + pause; answerKnown = false; break;
                    }
                }
                else if (where == "repair 2's order, no answer time") answerKnown = false;
                long backlog = Math.Max(0, (asked - origin) * rate / Freq - (at + block));
                start.Block(w, new float[block], block, block,
                    new CaptureBlockTiming(stamp, backlog, streamStartedAt: origin - Ticks(10),
                        backlogAnsweredAt: answerKnown ? answered : null));
                previous = Math.Max(stamp, answered);
            }
            start.Finish(w);
            Assert.True(start.FileBegan);
            Assert.True(start.BoundaryFrame >= 337, where + ": the file began at " + start.BoundaryFrame + ", before frame 337");
            if (where.StartsWith("repair 2"))
            {
                // With no answer time nothing establishes; the stream's start places it, 10 ms late.
                Assert.False(start.TimingEstablished);
                Assert.Equal(347, start.BoundaryFrame);
            }
            else
            {
                // Positive control: once caught up, a block establishes the timing, and the file begins exactly.
                Assert.True(start.TimingEstablished, where);
                Assert.Equal(337, start.BoundaryFrame);
            }
        }

        /// <summary>
        /// The two cases repair 3 refuses to establish from, by themselves: a
        /// backlog answered later than its block's duration after the stamp,
        /// and a backlog with no answer time. Either way the block's bound
        /// still counts (a true bound), and the stream's start places the file.
        /// </summary>
        [Fact]
        public void A_backlog_answered_late_or_at_no_known_time_does_not_establish_the_timing()
        {
            Directory.CreateDirectory(_folder);
            using var w = new WavWriter(Path.Combine(_folder, "late-answer.wav"), 1000, 1);
            var late = new CaptureFileStart(1000, hold: true, 0, Freq);
            late.Block(w, new float[100], 100, 100, new CaptureBlockTiming(Ticks(100), 0, streamStartedAt: 0, backlogAnsweredAt: Ticks(100) + Ticks(101)));
            Assert.False(late.TimingEstablished);
            late.Block(w, new float[100], 100, 100, new CaptureBlockTiming(Ticks(200), 0, backlogAnsweredAt: Ticks(200) - 1));
            Assert.False(late.TimingEstablished);   // an answer before its stamp is no pairing either
            late.Block(w, new float[100], 100, 100, new CaptureBlockTiming(Ticks(300), 0));
            Assert.False(late.TimingEstablished);
            // Positive control: answered within the block's 100 ms, it establishes.
            late.Block(w, new float[100], 100, 100, new CaptureBlockTiming(Ticks(400), 0, backlogAnsweredAt: Ticks(400) + Ticks(100)));
            Assert.True(late.TimingEstablished);
        }

        /// <summary>
        /// A backlog answered too late is not taken off its block's bound: that
        /// count could hold frames that arrived long after the stamp, and the
        /// block's delivered frames alone are a true bound.
        /// </summary>
        [Fact]
        public void A_backlog_answered_too_late_is_not_taken_off_its_blocks_bound()
        {
            Directory.CreateDirectory(_folder);
            using var w = new WavWriter(Path.Combine(_folder, "late-backlog.wav"), 1000, 1);
            var start = new CaptureFileStart(1000, hold: true, 0, Freq);
            // On time, but the answer came 500 ms later and counts the 500 frames that arrived meanwhile.
            start.Block(w, new float[100], 100, 100, new CaptureBlockTiming(Ticks(100), 500, backlogAnsweredAt: Ticks(600)));
            Assert.Equal(337, start.FrameAt(Ticks(337)));
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>A block that arrived with no backlog behind it, as the device reports.</summary>
        private static CaptureBlockTiming OnTime(long arrival) => new CaptureBlockTiming(arrival, 0, backlogAnsweredAt: arrival);

        private const int Rate = ChainAuditionAnalysisTests.Rate;
        private static readonly long Freq = System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Milliseconds as Stopwatch ticks, on the fake capture's clock (frame 0 captured at tick 0).</summary>
        private static long Ticks(double ms) => (long)Math.Round(ms * Freq / 1000.0);

        /// <summary>
        /// A take whose microphone hears <paramref name="heard"/> from the moment
        /// it opens, recorded and stopped. The marker reports its tone audibly
        /// over at <paramref name="markerEnd"/> on the capture's clock, or that
        /// no tone sounded (null).
        /// </summary>
        private (ChainAuditionTake Take, FakeRecorder Recorder) Recorded(float[] heard, long? markerEnd, bool beepsOn = true)
        {
            var log = new List<(string What, long At)>();
            var rec = new FakeRecorder(log, () => 0) { Samples = heard };
            string folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
            var take = new ChainAuditionTake(folder, rec, ChainNotConnectedYet.Instance, ChainAuditionTakeTests.ReadMono,
                startMarker: () => { rec.MarkersSounded++; return Task.FromResult(markerEnd); }, markerSounds: () => beepsOn);
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

        /// <summary>
        /// What the microphone hears: silence, a loud tone from
        /// <paramref name="toneStartMs"/> for <paramref name="toneMs"/>, silence
        /// for <paramref name="gapMs"/> (the operator answering the tone), then the take.
        /// </summary>
        private static float[] Heard(double toneStartMs, double toneMs, double gapMs, float[] take)
        {
            int toneFrom = (int)Math.Round(toneStartMs * Rate / 1000), toneTo = (int)Math.Round((toneStartMs + toneMs) * Rate / 1000);
            int speechFrom = (int)Math.Round((toneStartMs + toneMs + gapMs) * Rate / 1000);
            var x = new float[speechFrom + take.Length];
            for (int i = toneFrom; i < toneTo; i++) x[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 880 * i / Rate);
            Array.Copy(take, 0, x, speechFrom, take.Length);
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
                startMarker: () => { lock (log) log.Add(("marker", now())); return Task.FromResult<long?>(Ticks(300)); },
                markerSounds: markerSounds);
            rec.Saved = p => take.Saved(p, out _);
            return (take, rec);
        }

        /// <summary>
        /// Logs when it opens; on stop, writes what the microphone heard
        /// (<see cref="Samples"/>, from the moment it opened) through the real
        /// recorder's file start (<see cref="CaptureFileStart"/>), block by
        /// block as a device delivers, frame 0 captured at tick 0, and reports
        /// the file, as the narrator does.
        /// </summary>
        private sealed class FakeRecorder : IAuditionRecorder
        {
            private readonly List<(string What, long At)> _log;
            private readonly Func<long> _now;
            public FakeRecorder(List<(string What, long At)> log, Func<long> now) { _log = log; _now = now; }
            public float[] Samples = ChainAuditionAnalysisTests.Take(8);
            public bool Running, OtherRecording, Refuse, Hold;
            public int Opens, MarkersSounded, BeginCalls;
            public long? BeginAt;
            public long BoundaryFrame = -1;
            public readonly TaskCompletionSource Begun = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Path = "";
            public Action<string>? Saved;

            public bool IsRunning => Running || OtherRecording;
            public bool IsRecordingTo(string path) => Running && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);

            public bool Start(string path, bool holdFileStart)
            {
                if (Refuse) { lock (_log) _log.Add(("refused", _now())); return false; }
                Opens++;
                Path = path;
                Hold = holdFileStart;
                Running = true;
                lock (_log) _log.Add(("open", _now()));
                return true;
            }

            public void BeginFileAt(string path, long? stopwatchTimestamp)
            {
                if (!IsRecordingTo(path)) return;
                BeginCalls++;
                BeginAt = stopwatchTimestamp;
                Begun.TrySetResult();
            }

            public void Stop()
            {
                if (!Running) return;
                Running = false;
                const int block = 441;   // the moment falls inside a block
                var start = new CaptureFileStart(Rate, Hold, 0, Freq);
                if (BeginCalls > 0) start.BeginAt(BeginAt);
                using (var w = new WavWriter(Path, Rate, 1))
                {
                    for (int at = 0; at < Samples.Length; at += block)
                    {
                        int n = Math.Min(block, Samples.Length - at);
                        var buf = new float[n];
                        Array.Copy(Samples, at, buf, 0, n);
                        start.Block(w, buf, n, n, OnTime(Ticks((at + n) * 1000.0 / Rate)));
                    }
                    start.Finish(w);
                    BoundaryFrame = start.BoundaryFrame;
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
            private volatile bool _cut;
            /// <summary>The reader reports the word cut part-way: its handle fails.</summary>
            public void Cut() => _cut = true;

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
                public bool Failed => _owner.Withdrawn || _owner._cut;
                public bool Started => true;
                public bool Finished => !_owner._cut && (_owner._finishAtOnce || _owner._finished);
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
