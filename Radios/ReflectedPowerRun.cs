using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// What the reflected share has been doing lately, over the samples in
    /// the current bad streak.
    /// </summary>
    public enum ReflectedShape
    {
        /// <summary>Not enough judged samples in the streak to say.</summary>
        TooFew,

        /// <summary>The recent samples agree with each other: whatever is
        /// on the antenna port, it is holding still.</summary>
        Settled,

        /// <summary>The recent samples differ by more than a meter jitters:
        /// something is moving the match around.</summary>
        Changing,
    }

    /// <summary>
    /// The state one transmission accumulates for the reflected-power rule: how
    /// much forward power this transmission has actually managed to make, how
    /// many judgeable samples in a row have been bad, and — since #453's
    /// settling rule — what SHAPE those bad samples make.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the peak is measured rather than taken from the power setting
    /// (#453).</b> The floor below which a reflected share means nothing has to
    /// scale with the transmission, because an absolute one watt excludes
    /// almost nothing on a hundred-watt voice envelope — the envelope crosses
    /// one watt constantly on its way down between syllables.
    /// </para>
    /// <para>
    /// The obvious reference is the operator's SET power, and it is the wrong
    /// one. A Flex folds its power back when it sees a bad match: on 2026-08-22
    /// the bench 8600 made <b>101.2 W</b> into a properly connected dummy load
    /// and only <b>17.5 W</b> minutes earlier into an empty antenna port at the
    /// same setting. A floor derived from a hundred-watt SETTING would sit
    /// above everything a severely mismatched station can produce, and the
    /// alarm would go quiet in precisely the case it exists for. The share of a
    /// measured peak scales down with the foldback and keeps working.
    /// </para>
    /// <para>
    /// <b>Why the streak counts JUDGEABLE samples rather than ticks.</b> A
    /// voice envelope hands over two consecutive samples of anything for free,
    /// which is why the pre-existing "the warning fired on an earlier tick, the
    /// cut reads this one" rule was no defence here. But a plain
    /// consecutive-ticks rule fails the other way: with the floor set near the
    /// envelope peaks, most ticks are not judgeable at all, so a run of
    /// consecutive bad TICKS would almost never accumulate and the alarm would
    /// never fire. So a sample that cannot be judged — an incoherent pair, or
    /// forward power below the floor — is SKIPPED: it neither advances the
    /// streak nor resets it. Only a judgeable good sample resets it, because
    /// only a judgeable good sample is evidence that the antenna is fine.
    /// </para>
    /// <para>
    /// <b>Why the run now keeps the streak's shares and a clock (#453, the
    /// settling rule).</b> A tester's 6300 has no internal tuner. He drives a
    /// remote tuner by transmitting into it, so the flag the alarm stands down
    /// on during a tune cycle is never set on his station, and the alarm ended
    /// his transmissions while his tuner was still hunting — a second before it
    /// settled to 1.7. The alarm was not wrong about the number; it was wrong
    /// about the MOMENT. Noel's discriminator retires the argument rather than
    /// refining a threshold: <i>a bad antenna's reflected power is stable; a
    /// tuner searching produces reflected power that changes and trends down.</i>
    /// Judging that needs the last second or two of judged shares, which is
    /// what <see cref="Shape"/> reads, and it needs to know how long the streak
    /// has been going, which is why <see cref="Observe"/> takes the
    /// transmission's clock. Nothing here decides; the decision is
    /// <see cref="TransmitSafety.JudgeReflected"/>.
    /// </para>
    /// <para>
    /// One of these belongs to one transmission. <see cref="Reset"/> it
    /// wherever the once-per-transmission warning flag is reset, or a previous
    /// transmission's peak sets the floor for this one.
    /// </para>
    /// </remarks>
    public sealed class ReflectedPowerRun
    {
        /// <summary>
        /// How many of the current streak's judged shares are kept. At the
        /// kill switch's four-a-second cadence the two-second settle window
        /// holds eight or nine; this leaves room and is not a tuning knob.
        /// </summary>
        private const int StreakSharesKept = 16;

        /// <summary>The judged shares of the CURRENT bad streak, oldest first,
        /// each with the transmission clock it was taken at.</summary>
        private readonly List<(double At, float Share)> _streak = new List<(double, float)>();

        private float _streakFirstShare;
        private float _streakLastShare;

        /// <summary>
        /// The highest forward power seen this transmission, in watts.
        /// </summary>
        public float ForwardPeakWatts { get; private set; }

        /// <summary>
        /// Judgeable samples in a row whose reflected share was over the
        /// threshold.
        /// </summary>
        public int BadSamples { get; private set; }

        /// <summary>
        /// Judgeable samples seen this transmission, good and bad. Zero after a
        /// whole transmission means the rule never got to look at anything —
        /// which is a fact worth being able to state, since a guard that
        /// declined to judge looks exactly like a guard that saw nothing wrong.
        /// </summary>
        public int JudgedSamples { get; private set; }

        /// <summary>
        /// Samples this transmission that could not be judged because forward
        /// and reflected were not one sample. Persistently non-zero means the
        /// radio is not delivering the two meters together and the alarm is not
        /// working — trace it rather than leaving it silent.
        /// </summary>
        public int IncoherentSamples { get; private set; }

        /// <summary>
        /// The transmission clock at the first bad sample of the current
        /// streak, or NaN when there is no streak. The settling rule's outer
        /// bound counts from HERE, not from key-down: a remote tuner that
        /// re-hunts three minutes into a transmission — the antenna moved in
        /// the wind — gets the same patience as one that hunts at key-down.
        /// </summary>
        public double BadStreakStartSeconds { get; private set; } = double.NaN;

        /// <summary>
        /// Times <see cref="TransmitSafety.JudgeReflected"/> has deferred the
        /// alarm during the current streak — recorded by the caller through
        /// <see cref="NoteDeferred"/>, so the judgement itself stays pure.
        /// </summary>
        public int DeferredSamples { get; private set; }

        /// <summary>
        /// Sustained bad streaks this transmission that ended in a judged GOOD
        /// sample: the match went from bad to fine while the operator kept
        /// transmitting, which is what a tuner finding its match looks like.
        /// </summary>
        public int Recoveries { get; private set; }

        /// <summary>
        /// True only for the <see cref="Observe"/> call that ended a sustained
        /// bad streak with a good sample, so a caller can trace the recovery
        /// once, in the words of <see cref="LastRecovery"/>, without keeping a
        /// counter of its own. Cleared by the next observation.
        /// </summary>
        public bool JustRecovered { get; private set; }

        /// <summary>
        /// The most recent recovery, in words, for the trace. Empty until one
        /// has happened. This is the corroboration a tester's "my tuner said
        /// 1.7" never had: the streak's length, where the share started, where
        /// it was on its last bad sample, and what it settled to.
        /// </summary>
        public string LastRecovery { get; private set; } = "";

        /// <summary>
        /// The power below which a reflected share means nothing on THIS
        /// transmission.
        /// </summary>
        public float FloorWatts => TransmitSafety.ReflectedWarnFloorWatts(ForwardPeakWatts);

        /// <summary>
        /// Whether enough judgeable samples in a row have been bad to believe
        /// them.
        /// </summary>
        public bool Sustained => BadSamples >= TransmitSafety.ReflectedWarnSustainedSamples;

        /// <summary>
        /// How long the current bad streak has been going, in seconds of the
        /// transmission clock, or zero when there is no streak.
        /// </summary>
        public double BadStreakSeconds(double txSeconds)
        {
            if (BadSamples == 0 || double.IsNaN(BadStreakStartSeconds)) return 0;
            return Math.Max(0, txSeconds - BadStreakStartSeconds);
        }

        /// <summary>
        /// The streak's judged shares inside the settle window, oldest first.
        /// </summary>
        /// <remarks>
        /// The window is the last <see cref="TransmitSafety.ReflectedSettleWindowSeconds"/>
        /// of the streak, and never fewer than
        /// <see cref="TransmitSafety.ReflectedWarnSustainedSamples"/> samples —
        /// whichever holds MORE. Both halves are load-bearing. At the kill
        /// switch's four-a-second cadence, three samples cover under a second,
        /// and a tuner that steps its relays once a second would look settled
        /// between steps; the two seconds see across the step. On speech at
        /// one a second, most ticks are not judgeable at all and the last
        /// three judged samples may span six seconds; the sample minimum keeps
        /// the shape judgeable there. Only the CURRENT streak's samples are in
        /// it: the good sample that preceded a streak is not a change in the
        /// match, it is the moment before the fault, and letting it into the
        /// window would defer every abrupt fault by a window's length.
        /// </remarks>
        public IReadOnlyList<float> RecentShares
        {
            get
            {
                int start = WindowStart();
                var shares = new float[_streak.Count - start];
                for (int i = start; i < _streak.Count; i++) shares[i - start] = _streak[i].Share;
                return shares;
            }
        }

        /// <summary>
        /// The spread of the shares in the settle window — highest minus
        /// lowest — or NaN when the window is empty.
        /// </summary>
        public float RecentSpan
        {
            get
            {
                int start = WindowStart();
                if (start >= _streak.Count) return float.NaN;
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = start; i < _streak.Count; i++)
                {
                    float s = _streak[i].Share;
                    if (s < lo) lo = s;
                    if (s > hi) hi = s;
                }
                return hi - lo;
            }
        }

        /// <summary>
        /// What the current streak's recent shares are doing: holding still,
        /// moving, or too few to say.
        /// </summary>
        /// <remarks>
        /// Settled means every share in the window sits within
        /// <see cref="TransmitSafety.ReflectedSettleSpan"/> of every other. It
        /// says nothing about whether the level is GOOD — a streak is bad by
        /// definition — only whether it has stopped moving, which is the
        /// question the settling rule asks.
        /// </remarks>
        public ReflectedShape Shape
        {
            get
            {
                int count = _streak.Count - WindowStart();
                if (count < TransmitSafety.ReflectedWarnSustainedSamples) return ReflectedShape.TooFew;
                return RecentSpan <= TransmitSafety.ReflectedSettleSpan
                    ? ReflectedShape.Settled
                    : ReflectedShape.Changing;
            }
        }

        /// <summary>Start a fresh transmission.</summary>
        public void Reset()
        {
            ForwardPeakWatts = 0f;
            BadSamples = 0;
            JudgedSamples = 0;
            IncoherentSamples = 0;
            _streak.Clear();
            BadStreakStartSeconds = double.NaN;
            DeferredSamples = 0;
            Recoveries = 0;
            JustRecovered = false;
            LastRecovery = "";
        }

        /// <summary>
        /// Fold one reading in. Call once per meter poll, before asking
        /// <see cref="TransmitSafety.JudgeReflected"/> anything.
        /// </summary>
        /// <param name="reading">Forward and reflected as ONE reading.</param>
        /// <param name="txSeconds">
        /// The transmission's clock — seconds since key-down, or since the
        /// watch was armed. Only differences matter, so any monotonic clock
        /// the caller already keeps will do; pass the SAME clock to
        /// <see cref="TransmitSafety.JudgeReflected"/>.
        /// </param>
        /// <returns>
        /// True when this reading was judgeable — for a caller that wants to
        /// trace the difference between "watched and found nothing" and "never
        /// got to look".
        /// </returns>
        public bool Observe(in TransmitPowerReading reading, double txSeconds)
        {
            JustRecovered = false;

            if (!reading.IsCoherent)
            {
                IncoherentSamples++;
                return false;
            }

            // The peak grows from coherent readings only. An incoherent pair's
            // forward number may be perfectly good, but taking it here would
            // mean the floor is set by data the rule has already refused to
            // trust, and there is no need: the peak is reached many times a
            // second on speech.
            if (reading.ForwardWatts > ForwardPeakWatts)
                ForwardPeakWatts = reading.ForwardWatts;

            if (reading.ForwardWatts < FloorWatts) return false;

            float share = reading.ReflectedShare;
            if (float.IsNaN(share)) return false;

            JudgedSamples++;

            if (share > TransmitSafety.ReflectedWarnFraction)
            {
                if (BadSamples == 0)
                {
                    BadStreakStartSeconds = txSeconds;
                    _streakFirstShare = share;
                    DeferredSamples = 0;
                }
                BadSamples++;
                _streakLastShare = share;
                _streak.Add((txSeconds, share));
                if (_streak.Count > StreakSharesKept) _streak.RemoveAt(0);
                return true;
            }

            // A judged GOOD sample. If it ends a streak the rule had started to
            // believe, that is the fact worth recording — it is what the
            // operator's tuner finding its match looks like from here, and
            // without this line a tester's "my tuner said 1.7" could never be
            // corroborated from a bundle.
            if (Sustained)
            {
                Recoveries++;
                JustRecovered = true;
                LastRecovery =
                    "high for " + BadSamples + " judged samples over "
                    + BadStreakSeconds(txSeconds).ToString("F0") + " s, from "
                    + Percent(_streakFirstShare) + "% back to " + Percent(_streakLastShare)
                    + "% on the last bad sample, now " + Percent(share) + "%"
                    + (DeferredSamples > 0
                        ? ", alarm deferred " + DeferredSamples + " time"
                          + (DeferredSamples == 1 ? "" : "s")
                        : "");
            }

            BadSamples = 0;
            BadStreakStartSeconds = double.NaN;
            DeferredSamples = 0;
            _streak.Clear();
            return true;
        }

        /// <summary>
        /// Record that the alarm was deferred on this tick. Returns the count
        /// for the current streak, so a caller can trace the FIRST deferral
        /// and stay quiet about the rest.
        /// </summary>
        public int NoteDeferred() => ++DeferredSamples;

        private int WindowStart()
        {
            if (_streak.Count == 0) return 0;
            double newest = _streak[_streak.Count - 1].At;
            int start = _streak.Count;
            for (int i = _streak.Count - 1; i >= 0; i--)
            {
                int included = _streak.Count - i;
                bool inTime = newest - _streak[i].At <= TransmitSafety.ReflectedSettleWindowSeconds;
                if (inTime || included <= TransmitSafety.ReflectedWarnSustainedSamples) start = i;
                else break;
            }
            return start;
        }

        private static string Percent(float share) => Math.Round(share * 100f).ToString("F0");

        public override string ToString()
        {
            string s = "peak " + ForwardPeakWatts.ToString("F1") + " W, floor "
                + FloorWatts.ToString("F1") + " W, " + JudgedSamples + " judged, "
                + BadSamples + " bad in a row, " + IncoherentSamples + " not one sample";

            if (_streak.Count > 0)
            {
                var recent = RecentShares;
                var parts = new string[recent.Count];
                for (int i = 0; i < recent.Count; i++) parts[i] = Percent(recent[i]) + "%";
                s += ", recent shares " + string.Join(" ", parts) + " (" + Shape.ToString().ToLowerInvariant()
                    + ", spread " + Percent(RecentSpan) + ")";
                if (DeferredSamples > 0) s += ", alarm deferred " + DeferredSamples + "x";
            }
            if (Recoveries > 0) s += ", recovered " + Recoveries + "x";
            return s;
        }
    }
}
