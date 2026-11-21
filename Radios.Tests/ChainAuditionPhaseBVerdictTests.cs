using System;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The audition's seam as phase B grew it (#262, Sprint 49 Track Q phase B,
    /// items 3, 5, 6 and 7): the threshold's source, the live gate's figure
    /// (Noel's S3), the transmit-mode clause, nothing on (Noel's S4), a reason
    /// filled with values, and the cancel and progress the processor is handed.
    /// Every existing verdict sentence stays in its place with its text: the new
    /// clauses come after the threshold sentence. Pure: synthetic samples and a
    /// fake chain, no device, no radio.
    /// </summary>
    public sealed class ChainAuditionPhaseBVerdictTests
    {
        private const int Rate = ChainAuditionAnalysisTests.Rate;

        private sealed class Chain : IChainAuditionProcessor
        {
            public bool Gate = true;
            public float? Threshold = -42f;
            public AuditionThresholdSource? Source = AuditionThresholdSource.FromThisTake;
            public float? Floor = -50f;
            public AuditionLiveState? Live;
            public ChainProcessing? Instead;
            public CancellationToken SeenCancel;
            public Action<double>? SeenProgress;

            public ChainProcessing Process(float[] raw, int rate, CancellationToken cancel, Action<double>? progress)
            {
                SeenCancel = cancel;
                SeenProgress = progress;
                progress?.Invoke(0.5);
                if (Instead != null) return Instead;
                var o = new float[raw.Length];
                for (int i = 0; i < o.Length; i++) o[i] = raw[i] * ChainAuditionAnalysisTests.GoodGate(i);
                return ChainProcessing.Rendered(o, 0, Gate, Threshold, Source, Floor, Live);
            }
        }

        private static AuditionLiveState Live(bool auto = true, float db = -38f, bool measured = true, string mode = "USB", bool aside = false) =>
            new(new AuditionLiveGate(auto, db, measured), mode, aside);

        private static string Verdict(Chain chain) =>
            ChainAudition.Verdict(ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), Rate, chain));

        private static int At(string verdict, string key, params (string, object?)[] args)
        {
            int i = verdict.IndexOf(Lexicon.Get(key, args), StringComparison.Ordinal);
            Assert.True(i >= 0, key + " is not in: " + verdict);
            return i;
        }

        [Theory]
        [InlineData(AuditionThresholdSource.FromThisTake, "audio.audition.verdict.threshold_from_take")]
        [InlineData(AuditionThresholdSource.LiveFigure, "audio.audition.verdict.threshold_from_live")]
        [InlineData(AuditionThresholdSource.Default, "audio.audition.verdict.threshold_default")]
        [InlineData(AuditionThresholdSource.SetByHand, "audio.audition.verdict.threshold_by_hand")]
        public void The_verdict_says_where_the_threshold_came_from_after_the_threshold_sentence(AuditionThresholdSource source, string key)
        {
            var chain = new Chain { Source = source, Floor = source == AuditionThresholdSource.FromThisTake ? -50f : null };
            string v = Verdict(chain);
            int threshold = At(v, "audio.audition.verdict.threshold", ("threshold", "-42.0"));
            int said = source == AuditionThresholdSource.FromThisTake
                ? At(v, key, ("floor", "-50.0")) : At(v, key);
            Assert.True(said > threshold, "the source must come after the threshold sentence");
            // The existing sentences keep their places: the change before the threshold.
            Assert.True(v.IndexOf("WORDING PLACEHOLDER: the chain changed", StringComparison.Ordinal) is >= 0 and var change && change < threshold);
            // And only the one source is said.
            foreach (var other in new[] { "audio.audition.verdict.threshold_from_take", "audio.audition.verdict.threshold_from_live",
                         "audio.audition.verdict.threshold_default", "audio.audition.verdict.threshold_by_hand" })
                if (other != key) Assert.DoesNotContain(Lexicon.Get(other, ("floor", "-50.0")), v);
        }

        [Fact]
        public void With_the_gate_off_neither_the_source_nor_the_live_figure_is_said()
        {
            var chain = new Chain { Gate = false, Threshold = null, Live = Live() };
            var result = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), Rate, chain);
            Assert.Null(result.ThresholdSource);
            string v = ChainAudition.Verdict(result);
            At(v, "audio.audition.verdict.gate_off");
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.threshold_from_take", ("floor", "-50.0")), v);
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.live_threshold", ("threshold", "-38.0")), v);
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.live_not_measured"), v);
        }

        [Fact]
        public void The_live_gate_rule_says_the_figure_whenever_there_is_one_and_nothing_it_cannot_stand_behind()
        {
            var from = AuditionThresholdSource.FromThisTake;
            // No radio, gate off, by hand: nothing.
            Assert.Equal(AuditionLiveClause.None, ChainAudition.LiveGateClause(true, from, -42f, null));
            Assert.Equal(AuditionLiveClause.None, ChainAudition.LiveGateClause(false, null, null, Live()));
            Assert.Equal(AuditionLiveClause.None, ChainAudition.LiveGateClause(true, AuditionThresholdSource.SetByHand, -42f, Live(auto: false)));
            // Automatic and not measured: says so, whatever the take used.
            Assert.Equal(AuditionLiveClause.NotMeasuredYet, ChainAudition.LiveGateClause(true, from, -42f, Live(measured: false)));
            Assert.Equal(AuditionLiveClause.NotMeasuredYet, ChainAudition.LiveGateClause(true, AuditionThresholdSource.LiveFigure, -60f, Live(measured: false)));
            // The take already used the live figure: not said twice.
            Assert.Equal(AuditionLiveClause.None, ChainAudition.LiveGateClause(true, AuditionThresholdSource.LiveFigure, -38f, Live()));
            // Noel's S3: beside it whenever there is one, the same figure included.
            Assert.Equal(AuditionLiveClause.Figure, ChainAudition.LiveGateClause(true, from, -42f, Live()));
            Assert.Equal(AuditionLiveClause.Figure, ChainAudition.LiveGateClause(true, from, -38f, Live(db: -38f)));
            Assert.Equal(AuditionLiveClause.Figure, ChainAudition.LiveGateClause(true, AuditionThresholdSource.Default, -60f, Live()));
        }

        [Fact]
        public void The_verdict_says_the_live_figure_or_that_the_live_gate_has_not_measured_an_over_after_the_source()
        {
            string v = Verdict(new Chain { Live = Live(db: -37.5f) });
            int source = At(v, "audio.audition.verdict.threshold_from_take", ("floor", "-50.0"));
            Assert.True(At(v, "audio.audition.verdict.live_threshold", ("threshold", "-37.5")) > source);

            v = Verdict(new Chain { Live = Live(measured: false) });
            Assert.True(At(v, "audio.audition.verdict.live_not_measured") > At(v, "audio.audition.verdict.threshold_from_take", ("floor", "-50.0")));

            // No radio: nothing about a live gate.
            v = Verdict(new Chain { Live = null });
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.live_not_measured"), v);
            Assert.DoesNotContain("-37.5", v);
        }

        [Fact]
        public void The_mode_clause_is_said_only_when_the_live_chain_stands_aside_in_the_transmit_mode()
        {
            string v = Verdict(new Chain { Live = Live(mode: "CW", aside: true) });
            int mode = At(v, "audio.audition.verdict.mode_stands_aside", ("mode", "CW"));
            Assert.True(mode > At(v, "audio.audition.verdict.live_threshold", ("threshold", "-38.0")));
            // Gate off too: the voice chain still rendered, and it is still not used in CW.
            At(Verdict(new Chain { Gate = false, Threshold = null, Live = Live(mode: "CW", aside: true) }),
                "audio.audition.verdict.mode_stands_aside", ("mode", "CW"));
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.mode_stands_aside", ("mode", "USB")), Verdict(new Chain { Live = Live() }));
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.mode_stands_aside", ("mode", "")), Verdict(new Chain()));
        }

        [Fact]
        public void Nothing_on_offers_only_the_raw_take_and_says_nothing_in_the_chain_is_on()
        {
            var result = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), Rate,
                new Chain { Instead = ChainProcessing.NothingIsOn(Live()) });
            Assert.True(result.NothingOn);
            Assert.False(result.ChainAvailable);
            Assert.Equal(new[] { AuditionRender.Raw }, result.Playable.ToArray());
            string v = ChainAudition.Verdict(result);
            Assert.True(At(v, ChainProcessing.NothingOnKey) > At(v, "audio.audition.verdict.heard",
                ("speech", Fig(result.RawSpeechLufs)), ("floor", Fig(result.RawFloorLufs)), ("ratio", Fig(result.RawSpeechToNoiseLu))));
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.gate_off"), v);
            Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.live_threshold", ("threshold", "-38.0")), v);
            Assert.Contains("nothing in the chain is on", ChainAudition.TraceLine(result));
        }

        private static string Fig(float? v) => v!.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        [Fact]
        public void A_reason_with_values_is_said_filled()
        {
            var result = ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), Rate,
                new Chain { Instead = ChainProcessing.Unavailable("audio.audition.verdict.mode_stands_aside", ("mode", "RTTY")) });
            Assert.Contains(Lexicon.Get("audio.audition.verdict.mode_stands_aside", ("mode", "RTTY")), ChainAudition.Verdict(result));
            Assert.DoesNotContain("{mode}", ChainAudition.Verdict(result));
        }

        [Fact]
        public void The_processor_is_handed_the_cancel_and_the_progress_and_a_cancel_after_it_returns_still_throws()
        {
            var chain = new Chain();
            using var cts = new CancellationTokenSource();
            double seen = -1;
            ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), Rate, chain, cts.Token, p => seen = p);
            Assert.Equal(cts.Token, chain.SeenCancel);
            Assert.Equal(0.5, seen);

            var late = new Chain();
            using var cancelled = new CancellationTokenSource();
            late.Instead = null;
            var canceller = new CancelsWhileProcessing(cancelled);
            Assert.Throws<OperationCanceledException>(() =>
                ChainAudition.Analyse(ChainAuditionAnalysisTests.Take(8), Rate, canceller, cancelled.Token, null));
        }

        /// <summary>A processor that does not look at the token, while someone cancels: the analysis must not go on to report.</summary>
        private sealed class CancelsWhileProcessing : IChainAuditionProcessor
        {
            private readonly CancellationTokenSource _cts;
            public CancelsWhileProcessing(CancellationTokenSource cts) => _cts = cts;
            public ChainProcessing Process(float[] raw, int rate, CancellationToken cancel, Action<double>? progress)
            {
                _cts.Cancel();
                return ChainProcessing.Rendered(raw, 0, false, null);
            }
        }
    }
}
