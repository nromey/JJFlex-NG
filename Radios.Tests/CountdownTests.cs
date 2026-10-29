using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one countdown (#835, fix round 2026-10-28): its settings, the shape
    /// of the count, how it is voiced, and the gate that keys only when both
    /// the count has ended and the check's setup is confirmed.
    /// </summary>
    /// <remarks>
    /// Noel's spec, 2026-10-28 21:32 to 21:40: length 2 to 10 seconds, default
    /// 5, one pip a second, the last beat the long tone an octave up; the pip's
    /// pitch held to 200 to 2000 Hz; the pip's length, the long tone a fixed
    /// multiple; sounds off speaks the count, sounds on and Chatty speaks and
    /// beeps it; the setup runs during the count, and the last beat waits for
    /// a slow setup rather than keying early.
    /// </remarks>
    public sealed class CountdownTests
    {
        // ── Settings: round trip and clamp ─────────────────────────────────

        [Fact]
        public void The_settings_survive_a_save_and_load_and_are_clamped_on_the_way_in()
        {
            var saved = new CountdownSettings
            {
                Enabled = false, Seconds = 7, PipHz = 650, PipMs = 120, LeadIn = false,
            };
            var back = RoundTrip(saved);
            Assert.False(back.Enabled);
            Assert.Equal(7, back.Seconds);
            Assert.Equal(650, back.PipHz);
            Assert.Equal(120, back.PipMs);
            Assert.False(back.LeadIn);

            // A hand-edited file: every number out of range, both ways.
            var wild = RoundTrip(new CountdownSettings { Seconds = 40, PipHz = 9, PipMs = 5000 });
            var original = CountdownSettings.Current;
            try
            {
                CountdownSettings.Current = wild;
                Assert.Equal(CountdownSettings.MaxSeconds, CountdownSettings.Current.Seconds);
                Assert.Equal(CountdownSettings.MinPipHz, CountdownSettings.Current.PipHz);
                Assert.Equal(CountdownSettings.MaxPipMs, CountdownSettings.Current.PipMs);

                CountdownSettings.Current = new CountdownSettings { Seconds = 0, PipHz = 99999, PipMs = 1 };
                Assert.Equal(CountdownSettings.MinSeconds, CountdownSettings.Current.Seconds);
                Assert.Equal(CountdownSettings.MaxPipHz, CountdownSettings.Current.PipHz);
                Assert.Equal(CountdownSettings.MinPipMs, CountdownSettings.Current.PipMs);

                // Stored as a copy: changing the object afterwards changes nothing live.
                var mine = new CountdownSettings { Seconds = 3 };
                CountdownSettings.Current = mine;
                mine.Seconds = 9;
                Assert.Equal(3, CountdownSettings.Current.Seconds);
            }
            finally { CountdownSettings.Current = original; }
        }

        [Fact]
        public void The_defaults_are_the_ruled_ones()
        {
            var d = new CountdownSettings();
            Assert.True(d.Enabled);
            Assert.Equal(5, d.Seconds);
            Assert.Equal(2, CountdownSettings.MinSeconds);
            Assert.Equal(10, CountdownSettings.MaxSeconds);
            Assert.Equal(200, CountdownSettings.MinPipHz);
            Assert.Equal(2000, CountdownSettings.MaxPipHz);
            Assert.Equal(1000, CountdownSettings.BeatMs);
            // Every preset is a legal pitch, and the default is one of them.
            Assert.All(CountdownSettings.PipHzPresets, hz =>
                Assert.InRange(hz, CountdownSettings.MinPipHz, CountdownSettings.MaxPipHz));
            Assert.Contains(CountdownSettings.DefaultPipHz, CountdownSettings.PipHzPresets);
        }

        [Fact]
        public void The_long_tone_always_fits_inside_its_beat_and_the_pips_never_abut()
        {
            // #396: the beat must be longer than the tone or the count has no
            // rhythm. For EVERY legal pip, not only the default.
            Assert.True(CountdownSettings.MaxPipMs < CountdownSettings.BeatMs);
            Assert.True(CountdownSettings.MaxPipMs * CountdownSettings.LandingMultiple <= CountdownSettings.BeatMs,
                "the longest long tone would still be sounding when the key goes down");
        }

        // ── The shape of the count ─────────────────────────────────────────

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(10)]
        public void There_is_one_beat_a_second_for_each_second_and_the_last_is_the_long_tone(int seconds)
        {
            var plan = CountdownPlan.For(new CountdownSettings { Seconds = seconds, LeadIn = false },
                CountdownVoicing.SpokenAndBeeped, numberText: n => n.ToString());

            Assert.Equal(seconds, plan.Beats.Count);
            for (int i = 0; i < seconds; i++)
            {
                Assert.Equal(i * CountdownSettings.BeatMs, plan.Beats[i].AtMs);
                Assert.Equal(seconds - i, plan.Beats[i].Number);
                Assert.Equal(i == seconds - 1, plan.Beats[i].IsLanding);
            }
            Assert.Equal(seconds * CountdownSettings.BeatMs, plan.EndsAtMs);
        }

        [Theory]
        [InlineData(200, 50)]
        [InlineData(300, 300)]
        [InlineData(1234, 175)]
        [InlineData(2000, 500)]
        public void The_long_tone_is_the_octave_above_the_pip_and_a_fixed_multiple_of_its_length(int hz, int ms)
        {
            var plan = CountdownPlan.For(new CountdownSettings { Seconds = 4, PipHz = hz, PipMs = ms },
                CountdownVoicing.Beeped);

            foreach (var pip in plan.Beats.Where(b => !b.IsLanding))
            {
                Assert.Equal(hz, pip.Hz);
                Assert.Equal(ms, pip.Ms);
            }
            var landing = plan.Beats.Single(b => b.IsLanding);
            Assert.Equal(hz * 2, landing.Hz);
            Assert.Equal(ms * CountdownSettings.LandingMultiple, landing.Ms);
        }

        // ── Spoken and beeped ──────────────────────────────────────────────

        [Fact]
        public void Which_voicing_follows_the_sound_switch_and_the_verbosity()
        {
            Assert.Equal(CountdownVoicing.Spoken, CountdownPlan.VoicingFor(false, VerbosityLevel.Chatty));
            Assert.Equal(CountdownVoicing.Spoken, CountdownPlan.VoicingFor(false, VerbosityLevel.Terse));
            Assert.Equal(CountdownVoicing.SpokenAndBeeped, CountdownPlan.VoicingFor(true, VerbosityLevel.Chatty));
            Assert.Equal(CountdownVoicing.SpokenAndBeeped, CountdownPlan.VoicingFor(true, VerbosityLevel.Diagnostic));
            Assert.Equal(CountdownVoicing.Beeped, CountdownPlan.VoicingFor(true, VerbosityLevel.Terse));
            Assert.Equal(CountdownVoicing.Beeped, CountdownPlan.VoicingFor(true, VerbosityLevel.Critical));
        }

        [Fact]
        public void Spoken_and_beeped_each_number_falls_on_its_own_pip()
        {
            var plan = CountdownPlan.For(new CountdownSettings { Seconds = 5, LeadIn = false },
                CountdownVoicing.SpokenAndBeeped, numberText: n => n.ToString());

            // One schedule carries both: every beat that sounds also speaks,
            // and what it speaks is its own number.
            Assert.All(plan.Beats, b =>
            {
                Assert.True(b.Beeped);
                Assert.Equal(b.Number.ToString(), b.Spoken);
            });
            Assert.Equal(new[] { "5", "4", "3", "2", "1" }, plan.Beats.Select(b => b.Spoken));
        }

        [Fact]
        public void Spoken_only_has_no_tones_and_beeped_only_has_no_numbers()
        {
            var spoken = CountdownPlan.For(new CountdownSettings(), CountdownVoicing.Spoken, numberText: n => n.ToString());
            Assert.All(spoken.Beats, b => { Assert.False(b.Beeped); Assert.NotNull(b.Spoken); });

            var beeped = CountdownPlan.For(new CountdownSettings(), CountdownVoicing.Beeped, numberText: n => n.ToString());
            Assert.All(beeped.Beats, b => { Assert.True(b.Beeped); Assert.Null(b.Spoken); });

            // Same beats, same times, whichever way it is voiced.
            Assert.Equal(spoken.Beats.Select(b => b.AtMs), beeped.Beats.Select(b => b.AtMs));
        }

        [Fact]
        public void The_lead_in_takes_a_beat_of_its_own_so_the_numbers_stay_on_their_pips()
        {
            var with = CountdownPlan.For(new CountdownSettings { Seconds = 5 }, CountdownVoicing.SpokenAndBeeped,
                leadIn: "Starting the microphone test in", numberText: n => n.ToString());
            var without = CountdownPlan.For(new CountdownSettings { Seconds = 5 }, CountdownVoicing.SpokenAndBeeped,
                leadIn: null, numberText: n => n.ToString());

            Assert.Equal("Starting the microphone test in", with.LeadIn);
            Assert.Null(without.LeadIn);
            Assert.Equal(CountdownSettings.BeatMs, with.Beats[0].AtMs);
            Assert.Equal(0, without.Beats[0].AtMs);
            Assert.Equal(without.EndsAtMs + CountdownSettings.BeatMs, with.EndsAtMs);
        }

        [Fact]
        public void Check_your_audio_names_the_method_and_honours_the_switches()
        {
            var on = new CountdownSettings { LeadIn = true };
            var plan = CheckCountdown.PlanFor(AudioCheckListenMethods.MicTest, on, true, VerbosityLevel.Chatty)!;
            Assert.Equal("Starting the microphone test in", plan.LeadIn);
            Assert.Equal(CountdownVoicing.SpokenAndBeeped, plan.Voicing);
            Assert.Equal("5", plan.Beats[0].Spoken);

            Assert.Null(CheckCountdown.PlanFor(AudioCheckListenMethods.Monitor,
                new CountdownSettings { Enabled = false }, true, VerbosityLevel.Chatty));
            Assert.Null(CheckCountdown.PlanFor(AudioCheckListenMethods.Monitor,
                new CountdownSettings { LeadIn = false }, false, VerbosityLevel.Chatty)!.LeadIn);

            // Every method the window offers has a short name of its own.
            foreach (var m in AudioCheckMethods.DisplayOrder)
                Assert.NotEqual("audio.countdown.method.other", CheckCountdown.ShortNameKey(m));

            // Play sample plays even when the countdown is off.
            Assert.NotNull(CheckCountdown.SamplePlan(new CountdownSettings { Enabled = false }, true, VerbosityLevel.Terse));
        }

        // ── The gate: key only when both the count and the setup are done ──

        [Fact]
        public async Task A_setup_faster_than_the_count_keys_when_the_count_ends()
        {
            var clock = new SteppedClock();
            var output = new RecordingOutput(clock);
            var plan = Plan(seconds: 5);
            var run = CountdownRun.Start(plan, output, "test", clock.NowMs, clock.Delay);

            // The setup is confirmed at once, before the first second is out.
            var ready = run.ReadyToKeyAsync();
            Assert.True(await clock.RunUntil(ready));
            Assert.Equal(plan.EndsAtMs, clock.Now);
            Assert.Equal(0, run.SetupWaitMs);
            Assert.Equal(5, output.Tones.Count);
            Assert.Equal(plan.Beats.Last().AtMs, output.Tones.Last().At);
        }

        [Fact]
        public async Task A_setup_slower_than_the_count_holds_the_long_tone_and_keys_late_never_early()
        {
            var clock = new SteppedClock();
            var output = new RecordingOutput(clock);
            var plan = Plan(seconds: 5);
            var run = CountdownRun.Start(plan, output, "test", clock.NowMs, clock.Delay);

            // Run the clock as far as it will go with no setup confirmed.
            await clock.Drain();
            int landingAt = plan.Beats.Last().AtMs;
            Assert.Equal(landingAt, clock.Now);
            Assert.True(run.IsRunning);
            Assert.Equal(4, output.Tones.Count);
            Assert.DoesNotContain(output.Tones, t => t.Landing);

            // The setup takes two and a half seconds more than the count.
            clock.Advance(2500);
            long confirmedAt = clock.Now;
            var ready = run.ReadyToKeyAsync();
            Assert.True(await clock.RunUntil(ready));

            var landing = output.Tones.Single(t => t.Landing);
            Assert.True(landing.At >= confirmedAt, "the long tone sounded before the setup was confirmed");
            Assert.Equal(confirmedAt, landing.At);
            Assert.Equal(confirmedAt + CountdownSettings.BeatMs, clock.Now);
            Assert.True(clock.Now > plan.EndsAtMs, "a slow setup must make the key LATE");
            Assert.Equal(2500, run.SetupWaitMs);
        }

        [Fact]
        public async Task A_cancelled_count_never_lets_the_check_key_and_never_sounds_its_long_tone()
        {
            var clock = new SteppedClock();
            var output = new RecordingOutput(clock);
            var plan = Plan(seconds: 5);
            var run = CountdownRun.Start(plan, output, "test", clock.NowMs, clock.Delay);

            clock.Step();
            clock.Step();
            run.Cancel();
            var ready = run.ReadyToKeyAsync();
            Assert.False(await clock.RunUntil(ready));
            Assert.DoesNotContain(output.Tones, t => t.Landing);
            Assert.False(run.IsRunning);
            Assert.True(output.Withdrawn > 0, "numbers queued before the cancel would still be spoken");
        }

        [Fact]
        public async Task The_spoken_count_is_said_on_the_same_beats_as_the_tones()
        {
            var clock = new SteppedClock();
            var output = new RecordingOutput(clock);
            var plan = CountdownPlan.For(new CountdownSettings { Seconds = 3 }, CountdownVoicing.SpokenAndBeeped,
                leadIn: "Starting the live check in", numberText: n => n.ToString());
            var run = CountdownRun.Start(plan, output, "test", clock.NowMs, clock.Delay);
            Assert.True(await clock.RunUntil(run.ReadyToKeyAsync()));

            Assert.Equal(new[] { (0L, "Starting the live check in"), (1000L, "3"), (2000L, "2"), (3000L, "1") },
                output.Said);
            Assert.Equal(new[] { 1000L, 2000L, 3000L }, output.Tones.Select(t => t.At));
            Assert.Equal(4000, clock.Now);
        }

        // ── The defaults live in two places; they must agree ───────────────

        [Fact]
        public void The_default_pitch_length_and_beat_agree_with_the_earcon_constants()
        {
            string player = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "EarconPlayer.cs"));
            Assert.Equal(CountdownSettings.DefaultPipHz, ReadIntConstant(player, "CountdownCountHz"));
            Assert.Equal(CountdownSettings.DefaultPipMs, ReadIntConstant(player, "CountdownStepMs"));
            Assert.Equal(CountdownSettings.BeatMs, ReadIntConstant(player, "CountdownIntervalMs"));
        }

        [Fact]
        public void The_fix_tools_fallbacks_are_not_earlier_than_the_rule_at_the_default_length()
        {
            // The Fix tool keys on its last counting pip: (counts - 1) beats in,
            // where counts is one fewer than the seconds. Its fallbacks are what
            // a host that reports nothing gets, and must not be EARLY at the
            // default, which is the length nearly everyone will have.
            var d = new CountdownSettings();
            int keyUp = (CountdownPlan.FixerCounts(d) - 1) * CountdownSettings.BeatMs;
            int duration = CountdownPlan.FixerCounts(d) * CountdownSettings.BeatMs + d.LandingMs;
            Assert.Equal(Radios.ChainChecks.FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs, keyUp);
            Assert.True(Radios.ChainChecks.FixerTransmitAudioBoundary.DefaultCountdownDurationMs >= duration);
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static CountdownPlan Plan(int seconds) =>
            CountdownPlan.For(new CountdownSettings { Seconds = seconds }, CountdownVoicing.SpokenAndBeeped,
                leadIn: null, numberText: n => n.ToString());

        private static CountdownSettings RoundTrip(CountdownSettings s)
        {
            var xs = new XmlSerializer(typeof(CountdownSettings));
            using var w = new StringWriter();
            xs.Serialize(w, s);
            using var r = new StringReader(w.ToString());
            return (CountdownSettings)xs.Deserialize(r)!;
        }

        private static int ReadIntConstant(string source, string name)
        {
            var m = new Regex(@"const\s+int\s+" + Regex.Escape(name) + @"\s*=\s*(-?\d+)").Match(source);
            Assert.True(m.Success, "No 'const int " + name + " = ...' in EarconPlayer.cs.");
            return int.Parse(m.Groups[1].Value);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }

        /// <summary>A virtual clock whose delays complete only when the test steps them.</summary>
        private sealed class SteppedClock
        {
            private readonly object _sync = new();
            private readonly Queue<(int Ms, TaskCompletionSource Done)> _pending = new();
            private long _now;

            public long Now { get { lock (_sync) return _now; } }
            public long NowMs() => Now;
            public void Advance(long ms) { lock (_sync) _now += ms; }

            public Task Delay(int ms, CancellationToken token)
            {
                var done = new TaskCompletionSource();
                token.Register(() => done.TrySetCanceled(token));
                lock (_sync) _pending.Enqueue((ms, done));
                return done.Task;
            }

            /// <summary>Complete the oldest pending delay. False when nothing is waiting.</summary>
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

            /// <summary>Step until nothing has been pending for a while: the run is waiting on something else.</summary>
            public async Task Drain()
            {
                int idle = 0;
                while (idle < 20)
                {
                    if (Step()) idle = 0;
                    else { idle++; await Task.Delay(5); }
                }
            }

            public async Task<bool> RunUntil(Task<bool> task)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!task.IsCompleted)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the countdown never finished");
                    if (!Step()) await Task.Delay(1);
                }
                return await task;
            }
        }

        private sealed class RecordingOutput : ICountdownOutput
        {
            private readonly SteppedClock _clock;
            public RecordingOutput(SteppedClock clock) => _clock = clock;
            public List<(long At, int Number, bool Landing)> Tones { get; } = new();
            public List<(long, string)> Said { get; } = new();
            public int Withdrawn { get; private set; }

            /// <summary>This output's reader says everything the moment it is given.</summary>
            public bool SpeechOwed => false;

            public void WithdrawSpeech(string why) => Withdrawn++;

            public void Tone(CountdownBeat beat)
            {
                lock (Tones) Tones.Add((_clock.Now, beat.Number, beat.IsLanding));
            }

            public void Say(string text)
            {
                lock (Said) Said.Add((_clock.Now, text));
            }
        }
    }
}
