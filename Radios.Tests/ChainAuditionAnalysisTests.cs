using System;
using System.Linq;
using JJPortaudio;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The audition's analysis (#262), pure and windowless: one take processed
    /// twice, level-matched on speech loudness, rendered raw, cleaned and
    /// removed, with the speech-to-noise change and the gate's failures named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every take here is synthetic: speech-like bursts (a 140 Hz voice with
    /// three harmonics and a 4 Hz syllable swell) at about -20 dBFS, 600 ms on and
    /// 900 ms off, over white noise at about -50 dBFS. The chains are fakes that
    /// know the burst schedule, so each one does exactly one thing: a good gate,
    /// a gate that clips each word's first 60 ms, one that chatters inside words,
    /// one that never opens, one that never closes, a level change, a delay.
    /// No microphone is opened and nothing is played.
    /// </para>
    /// <para>
    /// <b>What these cannot see:</b> a real voice in a real room. The thresholds
    /// that separate the failures are Claude's judgement, shown here to separate
    /// these fakes; whether they separate a real gate on a real speaker is the
    /// bench's question once phase B renders through the real chain.
    /// </para>
    /// </remarks>
    public sealed class ChainAuditionAnalysisTests
    {
        internal const int Rate = 48000;
        private const double FirstBurstS = 0.5, OnS = 0.6, OffS = 0.9;
        private static readonly float Closed = (float)Math.Pow(10, -25 / 20.0);   // the gate's default depth

        // ── The take ─────────────────────────────────────────────────────

        /// <summary>Milliseconds into the burst sample <paramref name="i"/> falls in, or -1 between bursts.</summary>
        internal static double MsIntoBurst(int i)
        {
            double t = (double)i / Rate - FirstBurstS;
            if (t < 0) return -1;
            double into = t % (OnS + OffS);
            return into < OnS ? into * 1000 : -1;
        }

        /// <summary>Milliseconds since the last burst ended, or -1 inside or before the first.</summary>
        private static double MsSinceBurst(int i)
        {
            double t = (double)i / Rate - FirstBurstS;
            if (t < 0) return -1;
            double into = t % (OnS + OffS);
            return into < OnS ? -1 : (into - OnS) * 1000;
        }

        internal static float[] Take(double seconds, bool speech = true, int seed = 262)
        {
            int n = (int)(seconds * Rate);
            var rng = new Random(seed);
            var x = new float[n];
            double noiseAmp = Math.Pow(10, -50 / 20.0) * Math.Sqrt(3);
            // Harmonic sum's RMS is about 0.844 of its amplitude; aim near -20 dBFS.
            double speechAmp = Math.Pow(10, -20 / 20.0) / 0.844;
            for (int i = 0; i < n; i++)
            {
                double s = (rng.NextDouble() * 2 - 1) * noiseAmp;
                if (speech && MsIntoBurst(i) >= 0)
                {
                    double t = (double)i / Rate;
                    double voice = 0;
                    for (int k = 1; k <= 4; k++) voice += Math.Sin(2 * Math.PI * 140 * k * t) / k;
                    s += speechAmp * (0.75 + 0.25 * Math.Sin(2 * Math.PI * 4 * t)) * voice;
                }
                x[i] = (float)s;
            }
            return x;
        }

        // ── The fake chains ──────────────────────────────────────────────

        internal sealed class FakeChain : IChainAuditionProcessor
        {
            private readonly Func<int, float> _gain;
            private readonly int _latency;
            private readonly bool _gate;
            private readonly float? _threshold;
            public bool Zeroed;

            public FakeChain(Func<int, float> gain, int latency = 0, bool gate = true, float? threshold = -42f, bool zeroInput = false)
            {
                _gain = gain; _latency = latency; _gate = gate; _threshold = threshold; Zeroed = zeroInput;
            }

            public ChainProcessing Process(float[] rawMono, int sampleRate)
            {
                var output = new float[rawMono.Length];
                for (int i = _latency; i < output.Length; i++)
                    output[i] = rawMono[i - _latency] * _gain(i - _latency);
                if (Zeroed) Array.Clear(rawMono);   // a processor that scribbles on what it was given
                return ChainProcessing.Rendered(output, _latency, _gate, _threshold);
            }
        }

        /// <summary>Open through each burst and 50 ms after it, closed by 25 dB otherwise.</summary>
        internal static float GoodGate(int i)
        {
            if (MsIntoBurst(i) >= 0) return 1f;
            double since = MsSinceBurst(i);
            return since >= 0 && since < 50 ? 1f : Closed;
        }

        private static float ClippingGate(int i) => MsIntoBurst(i) is >= 0 and < 60 ? Closed : GoodGate(i);

        private static float ChatteringGate(int i)
        {
            double into = MsIntoBurst(i);
            if (into < 0) return GoodGate(i);
            return ((int)(into / 30)) % 2 == 1 ? Closed : 1f;
        }

        private static ChainAuditionResult Run(Func<int, float> gain, bool gate = true, int latency = 0, double seconds = 8) =>
            ChainAudition.Analyse(Take(seconds), Rate, new FakeChain(gain, latency, gate));

        private static double RmsDb(float[] x)
        {
            double sum = 0;
            foreach (float s in x) sum += (double)s * s;
            return 10 * Math.Log10(sum / Math.Max(1, x.Length) + 1e-20);
        }

        // ── What was heard ───────────────────────────────────────────────

        [Fact]
        public void Speech_over_room_noise_is_heard_and_the_room_alone_is_not()
        {
            var speech = ChainAudition.Analyse(Take(8), Rate, ChainNotConnectedYet.Instance);
            Assert.Equal(AuditionSpeech.Heard, speech.Speech);
            Assert.True(speech.RawSpeechToNoiseLu > 20, "speech-to-noise " + speech.RawSpeechToNoiseLu);

            var room = ChainAudition.Analyse(Take(8, speech: false), Rate, ChainNotConnectedYet.Instance);
            Assert.Equal(AuditionSpeech.NoSpeech, room.Speech);
            Assert.True(room.RawSpeechToNoiseLu < ChainAudition.MinSpeechToNoiseLu, "room alone " + room.RawSpeechToNoiseLu);
        }

        [Fact]
        public void A_take_too_short_for_a_noise_floor_says_so()
        {
            var r = ChainAudition.Analyse(Take(2), Rate, new FakeChain(GoodGate));
            Assert.Equal(AuditionSpeech.TooShort, r.Speech);
            Assert.Equal(new[] { AuditionRender.Raw }, r.Playable);
        }

        [Fact]
        public void With_no_speech_only_the_raw_take_is_offered_and_nothing_is_matched()
        {
            // Matching on speech loudness with no speech would match the noise,
            // undoing exactly what is being auditioned.
            var r = ChainAudition.Analyse(Take(8, speech: false), Rate, new FakeChain(GoodGate));
            Assert.Equal(AuditionSpeech.NoSpeech, r.Speech);
            Assert.Equal(new[] { AuditionRender.Raw }, r.Playable);
            Assert.Equal(0f, r.SpeechToNoiseChangeLu);
            Assert.Equal(AuditionGateFault.None, r.Faults);
        }

        [Fact]
        public void Phase_A_offers_the_raw_take_alone_and_says_the_chain_is_not_connected()
        {
            var r = ChainAudition.Analyse(Take(8), Rate, ChainNotConnectedYet.Instance);
            Assert.False(r.ChainAvailable);
            Assert.Equal(ChainNotConnectedYet.ReasonKey, r.ChainUnavailableKey);
            Assert.Equal(new[] { AuditionRender.Raw }, r.Playable);
            Assert.Null(r.Render(AuditionRender.Cleaned));
            Assert.Null(r.Render(AuditionRender.Removed));
        }

        // ── One take, processed twice ────────────────────────────────────

        [Fact]
        public void The_processor_gets_a_copy_and_the_raw_render_is_the_take_itself()
        {
            var take = Take(8);
            var original = (float[])take.Clone();
            var chain = new FakeChain(GoodGate, zeroInput: true);
            var r = ChainAudition.Analyse(take, Rate, chain);
            Assert.Equal(original, take);   // never changed
            var raw = r.Render(AuditionRender.Raw)!;
            Assert.Equal(take.Length, raw.Length);
            for (int i = 0; i < raw.Length; i += 997) Assert.Equal(take[i] * r.PlaybackScale, raw[i], 6);
        }

        [Fact]
        public void The_cleaned_take_is_matched_to_the_raw_one_on_speech_loudness()
        {
            // A chain that halves everything, gate off: after matching, the
            // speech must be as loud as the raw take's or the comparison flatters.
            var r = Run(_ => 0.5f, gate: false);
            Assert.True(r.LevelMatched);
            Assert.InRange(r.MatchGainDb, 5.8f, 6.3f);
            var raw = ChainAudition.Measure(r.Render(AuditionRender.Raw)!, r.Render(AuditionRender.Raw)!.Length, Rate);
            var cleaned = ChainAudition.Measure(r.Render(AuditionRender.Cleaned)!, r.Render(AuditionRender.Cleaned)!.Length, Rate);
            Assert.InRange(cleaned.SpeechLufs - raw.SpeechLufs, -0.3f, 0.3f);
            // A level change is not a noise change.
            Assert.InRange(r.SpeechToNoiseChangeLu, -0.5f, 0.5f);
            Assert.Equal(AuditionGateFault.None, r.Faults);
        }

        [Fact]
        public void The_output_is_lined_up_by_the_chains_latency_before_anything_is_compared()
        {
            // A pass-through 10 ms late: lined up, nothing was removed.
            var r = Run(_ => 1f, gate: false, latency: 480);
            Assert.True(RmsDb(r.Render(AuditionRender.Removed)!) < -100, "removed " + RmsDb(r.Render(AuditionRender.Removed)!));
            Assert.Equal(Take(8).Length - 480, r.Render(AuditionRender.Cleaned)!.Length);

            // Positive control: the same output NOT lined up leaves the voice in
            // what was "removed", which is what a missing alignment would play.
            var unaligned = ChainAudition.Analyse(Take(8), Rate, new LyingLatency(480));
            Assert.True(RmsDb(unaligned.Render(AuditionRender.Removed)!) > -40);
        }

        /// <summary>Delays by <c>delay</c> but reports no latency.</summary>
        private sealed class LyingLatency : IChainAuditionProcessor
        {
            private readonly int _delay;
            public LyingLatency(int delay) => _delay = delay;
            public ChainProcessing Process(float[] raw, int rate)
            {
                var o = new float[raw.Length];
                Array.Copy(raw, 0, o, _delay, raw.Length - _delay);
                return ChainProcessing.Rendered(o, 0, false, null);
            }
        }

        [Fact]
        public void The_three_renders_share_one_scale_and_none_clips()
        {
            // A chain that halves everything needs +6 dB to match; on a hot take
            // that would clip, so all three come down together instead.
            var hot = Take(8).Select(s => s * 9f).ToArray();
            var r = ChainAudition.Analyse(hot, Rate, new FakeChain(_ => 0.5f, gate: false));
            Assert.True(r.PlaybackScale < 1f);
            foreach (var render in r.Playable)
                Assert.True(r.Render(render)!.Max(Math.Abs) <= 0.9f, render + " clips");
            var raw = ChainAudition.Measure(r.Render(AuditionRender.Raw)!, r.Render(AuditionRender.Raw)!.Length, Rate);
            var cleaned = ChainAudition.Measure(r.Render(AuditionRender.Cleaned)!, r.Render(AuditionRender.Cleaned)!.Length, Rate);
            Assert.InRange(cleaned.SpeechLufs - raw.SpeechLufs, -0.3f, 0.3f);
        }

        [Fact]
        public void The_renders_are_offered_cleaned_first_then_raw_then_removed()
        {
            // Ruled 2026-11-19 07:03: "play the clean audio since it's going to be transmitted".
            Assert.Equal(new[] { AuditionRender.Cleaned, AuditionRender.Raw, AuditionRender.Removed }, ChainAudition.PlayOrder);
            Assert.Equal(ChainAudition.PlayOrder, Run(GoodGate).Playable);
        }

        // ── The figure of merit ──────────────────────────────────────────

        [Fact]
        public void A_good_gate_raises_speech_to_noise_by_about_its_depth_and_names_no_fault()
        {
            var r = Run(GoodGate);
            Assert.True(r.ChainAvailable);
            Assert.Equal(AuditionSpeech.Heard, r.Speech);
            Assert.Equal(AuditionGateFault.None, r.Faults);
            Assert.InRange(r.SpeechToNoiseChangeLu, 15f, 30f);
            Assert.InRange(r.MatchGainDb, -1f, 1f);
            Assert.Equal(-42f, r.ThresholdDb);
        }

        // ── The failures #262 says to name ───────────────────────────────

        [Fact]
        public void A_gate_that_never_opens_is_named_and_not_also_called_never_closing()
        {
            var r = Run(_ => Closed);
            Assert.Equal(AuditionGateFault.NeverOpens, r.Faults);
        }

        [Fact]
        public void A_gate_that_never_closes_is_named()
        {
            var r = Run(_ => 1f, gate: true);
            Assert.Equal(AuditionGateFault.NeverCloses, r.Faults);
        }

        [Fact]
        public void A_gate_that_chatters_inside_words_is_named()
        {
            var r = Run(ChatteringGate);
            Assert.True((r.Faults & AuditionGateFault.Chatters) != 0, "faults " + r.Faults);
            Assert.True((r.Faults & AuditionGateFault.NeverCloses) == 0);
        }

        [Fact]
        public void A_gate_that_clips_each_first_syllable_is_named_and_nothing_else_is()
        {
            var r = Run(ClippingGate);
            Assert.Equal(AuditionGateFault.ClipsFirstSyllable, r.Faults);
        }

        [Fact]
        public void A_gate_that_silences_everything_is_never_opening_and_is_not_matched()
        {
            var r = Run(_ => 0f);
            Assert.True((r.Faults & AuditionGateFault.NeverOpens) != 0);
            Assert.False(r.LevelMatched);
            Assert.Equal(0f, r.MatchGainDb);
        }

        [Fact]
        public void With_the_gate_off_no_gate_failure_is_named()
        {
            // The same output that reads as "never closes" with the gate on is
            // simply a chain that did nothing when the gate is off.
            var r = Run(_ => 1f, gate: false);
            Assert.Equal(AuditionGateFault.None, r.Faults);
        }
    }

    /// <summary>The audition's verdict, in the lexicon's words (placeholders until Codex writes them).</summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ChainAuditionVerdictTests
    {
        private static string L(string key) => Lexicon.Get(key);

        [Fact]
        public void Every_verdict_key_is_in_the_lexicon()
        {
            foreach (string key in new[]
            {
                "audio.audition.verdict.too_short", "audio.audition.verdict.no_speech", "audio.audition.verdict.heard",
                ChainNotConnectedYet.ReasonKey, "audio.audition.verdict.change", "audio.audition.verdict.gate_off",
                "audio.audition.verdict.threshold", "audio.audition.verdict.threshold_unknown",
                "audio.audition.verdict.not_level_matched",
            }.Concat(ChainAudition.FaultKeys.Select(f => f.Key)))
                Assert.NotEqual(key, L(key));
        }

        [Fact]
        public void No_speech_says_so_and_reports_no_figure()
        {
            var r = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8, speech: false), ChainAuditionAnalysisTests.Rate,
                ChainNotConnectedYet.Instance);
            string v = ChainAudition.Verdict(r);
            Assert.StartsWith(L("audio.audition.verdict.no_speech"), v);
            Assert.DoesNotContain("LUFS", v.Replace(L("audio.audition.verdict.no_speech"), ""));
            Assert.Contains(L(ChainNotConnectedYet.ReasonKey), v);
        }

        [Fact]
        public void Phase_A_reports_what_was_heard_and_that_the_chain_is_not_connected()
        {
            var r = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), ChainAuditionAnalysisTests.Rate,
                ChainNotConnectedYet.Instance);
            string v = ChainAudition.Verdict(r);
            string heard = Lexicon.Get("audio.audition.verdict.heard", ("speech", r.RawSpeechLufs.ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture)), ("floor", r.RawFloorLufs.ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture)), ("ratio", r.RawSpeechToNoiseLu.ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture)));
            // What was heard, then the chain's sentence, and nothing about a change it did not make.
            Assert.Equal(heard + " " + L(ChainNotConnectedYet.ReasonKey), v);
        }

        [Fact]
        public void The_change_is_reported_with_the_threshold_that_produced_it()
        {
            var r = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), ChainAuditionAnalysisTests.Rate,
                new ChainAuditionAnalysisTests.FakeChain(ChainAuditionAnalysisTests.GoodGate, threshold: -42f));
            string v = ChainAudition.Verdict(r);
            Assert.Contains(Lexicon.Get("audio.audition.verdict.threshold", ("threshold", "-42.0")), v);
            foreach (var (_, key) in ChainAudition.FaultKeys) Assert.DoesNotContain(L(key), v);
        }

        [Fact]
        public void Each_failure_found_is_said()
        {
            var r = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), ChainAuditionAnalysisTests.Rate,
                new ChainAuditionAnalysisTests.FakeChain(_ => 1f, gate: true));
            Assert.Contains(L("audio.audition.fault.never_closes"), ChainAudition.Verdict(r));
        }

        [Fact]
        public void The_trace_line_carries_figures_and_never_a_path()
        {
            var r = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), ChainAuditionAnalysisTests.Rate,
                ChainNotConnectedYet.Instance);
            string line = ChainAudition.TraceLine(r);
            Assert.Contains("speech Heard", line);
            Assert.DoesNotContain("\\", line);
            Assert.DoesNotContain(".wav", line);
        }
    }
}
