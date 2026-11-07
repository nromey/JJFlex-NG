#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.SignalCapture
{
    /// <summary>
    /// One S-meter reading inside a capture window.
    /// </summary>
    /// <remarks>
    /// The value is the un-truncated dBm from the meter stream — NOT the
    /// integer S-unit. Peak, trough and QSB depth need the sub-dB resolution
    /// the scalar <c>FlexBase.SMeter</c> path throws away; conversion to
    /// S-units happens once, at the edge, through
    /// <see cref="SMeterReading.FromDbm"/>.
    /// </remarks>
    public readonly struct QsoSignalSample
    {
        public QsoSignalSample(double offsetSeconds, double dbm, bool localTransmit)
        {
            OffsetSeconds = offsetSeconds;
            Dbm = dbm;
            LocalTransmit = localTransmit;
        }

        /// <summary>Seconds since the capture started.</summary>
        public double OffsetSeconds { get; }

        /// <summary>The reading, in dBm.</summary>
        public double Dbm { get; }

        /// <summary>
        /// True when this station was transmitting (or running the tune
        /// carrier) at the moment of the reading. Flagged rather than dropped,
        /// so the record stays a complete account of the window; the analysis
        /// excludes flagged samples from every statistic.
        /// </summary>
        public bool LocalTransmit { get; }
    }

    /// <summary>What could be said about fading, and — just as important — what could not.</summary>
    public enum QsbVerdict
    {
        /// <summary>Nothing was measured at all — no usable readings arrived.</summary>
        NothingMeasured = 0,

        /// <summary>The capture is too short for fading to be assessed.</summary>
        TooShortToAssess = 1,

        /// <summary>The signal stayed within one S-unit — no fading worth the name.</summary>
        NoSignificantFading = 2,

        /// <summary>The signal moved, but too few complete fade cycles fit in
        /// the capture to measure a rhythm.</summary>
        TooFewCycles = 3,

        /// <summary>Fades with a measurable, reasonably regular period.</summary>
        Periodic = 4,

        /// <summary>Fades, but at spacings too uneven to call a period.</summary>
        Irregular = 5,
    }

    /// <summary>Which way the signal was heading over the whole capture.</summary>
    public enum TrendVerdict
    {
        NothingMeasured = 0,
        TooShortToAssess = 1,
        Steady = 2,
        Rising = 3,
        Falling = 4,
    }

    /// <summary>
    /// Everything the analysis could determine about one capture — and
    /// explicit verdicts for everything it could not.
    /// </summary>
    /// <remarks>
    /// An absent measurement and a null result sound identical to a listener
    /// and need opposite responses from them, so nothing here is ever silently
    /// omitted: every "could not be determined" is a named enum value that the
    /// report and the spoken headline turn into words.
    /// </remarks>
    public sealed class QsoSignalAnalysisResult
    {
        /// <summary>The operator's whole window, start to stop, in seconds.</summary>
        public double CaptureSeconds { get; internal set; }

        /// <summary>Every reading that arrived, including ones taken while transmitting.</summary>
        public int SampleCount { get; internal set; }

        /// <summary>Readings the statistics actually used (not transmitting).</summary>
        public int AnalyzedCount { get; internal set; }

        /// <summary>Approximate seconds of the window spent transmitting, excluded from every statistic.</summary>
        public double TransmitSeconds { get; internal set; }

        /// <summary>First to last analyzed reading, in seconds.</summary>
        public double AnalyzedSpanSeconds { get; internal set; }

        /// <summary>True when peak, trough and mean are meaningful.</summary>
        public bool HasStats { get; internal set; }

        /// <summary>Highest single reading, dBm.</summary>
        public double PeakDbm { get; internal set; }

        /// <summary>Lowest smoothed reading, dBm — a pause between words is not a fade.</summary>
        public double TroughDbm { get; internal set; }

        /// <summary>Arithmetic mean of the raw readings, dBm.</summary>
        public double MeanDbm { get; internal set; }

        /// <summary>Peak minus trough, decibels.</summary>
        public double SwingDb => HasStats ? PeakDbm - TroughDbm : 0.0;

        /// <summary>The swing in whole S-units (6 dB each), rounded to nearest.</summary>
        public int SwingSUnits => HasStats ? (int)Math.Round(SwingDb / 6.0) : 0;

        public QsbVerdict Qsb { get; internal set; } = QsbVerdict.NothingMeasured;

        /// <summary>Mean trough-to-trough spacing in seconds. Valid for
        /// <see cref="QsbVerdict.Periodic"/> and <see cref="QsbVerdict.Irregular"/>.</summary>
        public double QsbPeriodSeconds { get; internal set; }

        /// <summary>Complete fade cycles observed (trough-to-trough intervals).</summary>
        public int QsbCycleCount { get; internal set; }

        /// <summary>Mean depth of the detected fades, decibels peak-to-trough.</summary>
        public double FadeDepthDb { get; internal set; }

        /// <summary>True when the fades average three S-units (18 dB) or more.</summary>
        public bool DeepFading => FadeDepthDb >= QsoSignalAnalysis.DeepFadeDb;

        /// <summary>
        /// The slowest fade rhythm this capture could possibly have shown —
        /// half the analyzed span. A steady verdict is only a verdict about
        /// rhythms faster than this, and the report says so.
        /// </summary>
        public double LongestObservablePeriodSeconds => AnalyzedSpanSeconds / 2.0;

        public TrendVerdict Trend { get; internal set; } = TrendVerdict.NothingMeasured;

        /// <summary>Signed regression change over the analyzed span, decibels.
        /// Positive is rising.</summary>
        public double TrendTotalDb { get; internal set; }

        /// <summary>True when the capture buffer reached its cap and later
        /// readings were not kept. Named in the report — a report that quietly
        /// covered less than its window would look authoritative and not be.</summary>
        public bool BufferFilled { get; internal set; }

        // -------- coverage (#452) --------

        /// <summary>The capture's own usual spacing between readings: the
        /// median gap between consecutive receive readings, in seconds. Zero
        /// when there were too few readings to tell. Never assumed: the meter
        /// stream's rate is whatever this capture shows it to be.</summary>
        public double TypicalSpacingSeconds { get; internal set; }

        /// <summary>Gaps between receive readings longer than
        /// <see cref="QsoSignalAnalysis.GapSpacingMultiple"/> times the typical
        /// spacing: readings that never arrived, most often a network stall.</summary>
        public int GapCount { get; internal set; }

        /// <summary>Seconds of the window with no readings beyond the usual
        /// spacing, summed over every gap.</summary>
        public double MissingSeconds { get; internal set; }

        /// <summary>The longest single gap, seconds.</summary>
        public double LongestGapSeconds { get; internal set; }

        /// <summary>The fraction of the window that readings covered, 0 to 1.
        /// One when there were no gaps.</summary>
        public double Coverage { get; internal set; } = 1.0;

        /// <summary>True when at least one gap was found. A clean capture says
        /// nothing about coverage at all.</summary>
        public bool CoverageDegraded => GapCount > 0;

        /// <summary>Fade bottoms that fell on or beside a gap and were not
        /// counted as fades: a hole in the readings is not the signal.</summary>
        public int FadesSetAsideAtGaps { get; internal set; }

        /// <summary>True when readings beside a gap were left out of the trough
        /// and swing. False when there was no gap, or when every reading sat
        /// beside one, in which case all of them were used rather than none,
        /// and the report says so.</summary>
        public bool GapExclusionApplied { get; internal set; }
    }

    /// <summary>
    /// The analysis: a pure function from a time series of S-meter readings to
    /// a <see cref="QsoSignalAnalysisResult"/>. No radio, no clock, no I/O —
    /// which is what makes every shape of capture testable with a synthetic
    /// trace.
    /// </summary>
    public static class QsoSignalAnalysis
    {
        /// <summary>Total width of the smoothing window, seconds. Two seconds:
        /// wide enough that syllables and key clicks average out, narrow enough
        /// that a four-second QSB cycle survives.</summary>
        public const double SmoothingWindowSeconds = 2.0;

        /// <summary>Below this many usable readings, no statistics.</summary>
        public const int MinSamplesForStats = 10;

        /// <summary>Below this analyzed span, no statistics.</summary>
        public const double MinSecondsForStats = 5.0;

        /// <summary>Below this analyzed span, fading is not assessed at all —
        /// the smoothing window would be a large fraction of the capture.</summary>
        public const double MinSecondsForQsb = 8.0;

        /// <summary>Below this analyzed span, no trend — a trend read off a few
        /// seconds is a syllable, not a trend.</summary>
        public const double MinSecondsForTrend = 20.0;

        /// <summary>A smoothed swing below one S-unit (6 dB) is "steady".</summary>
        public const double SteadyBelowDb = 6.0;

        /// <summary>A fade excursion must reverse by at least this much to
        /// count as a cycle — one S-unit, or 40 percent of the smoothed swing,
        /// whichever is larger.</summary>
        public const double FadeHysteresisFloorDb = 6.0;

        /// <summary>Fades averaging this deep (three S-units) are "deep".</summary>
        public const double DeepFadeDb = 18.0;

        /// <summary>Regression change below one S-unit over the whole span is "steady".</summary>
        public const double TrendThresholdDb = 6.0;

        /// <summary>Trough spacings whose coefficient of variation exceeds this
        /// are reported as irregular rather than given a false period.</summary>
        public const double IrregularCv = 0.6;

        /// <summary>
        /// A gap between receive readings longer than this many times the
        /// capture's own typical spacing is a gap in the data (#452). Ten
        /// times: at the bench's 15 to 20 readings a second that is about half
        /// a second, well clear of ordinary jitter, and far shorter than any
        /// fade. Relative on purpose, so no sample rate is assumed.
        /// </summary>
        public const double GapSpacingMultiple = 10.0;

        /// <summary>
        /// Analyze one capture. <paramref name="samples"/> must be in time
        /// order (the buffer appends, so it is).
        /// </summary>
        public static QsoSignalAnalysisResult Analyze(
            IReadOnlyList<QsoSignalSample> samples,
            double captureSeconds,
            bool bufferFilled = false)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));

            var r = new QsoSignalAnalysisResult
            {
                CaptureSeconds = Math.Max(0.0, captureSeconds),
                SampleCount = samples.Count,
                BufferFilled = bufferFilled,
            };

            // Split out the readings taken while transmitting, and total the
            // time they cover. Each inter-sample gap is attributed to the state
            // at its left endpoint — approximate, and named as such in the report.
            var analyzed = new List<QsoSignalSample>(samples.Count);
            double txSeconds = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (i > 0 && samples[i - 1].LocalTransmit)
                    txSeconds += Math.Max(0.0, samples[i].OffsetSeconds - samples[i - 1].OffsetSeconds);
                if (!samples[i].LocalTransmit)
                    analyzed.Add(samples[i]);
            }
            r.TransmitSeconds = txSeconds;
            r.AnalyzedCount = analyzed.Count;

            if (analyzed.Count == 0)
            {
                r.Qsb = QsbVerdict.NothingMeasured;
                r.Trend = TrendVerdict.NothingMeasured;
                return r;
            }

            r.AnalyzedSpanSeconds =
                analyzed[analyzed.Count - 1].OffsetSeconds - analyzed[0].OffsetSeconds;

            List<(double Start, double End)> gaps = FindGaps(r, samples);

            // Raw statistics.
            double peak = double.MinValue, sum = 0.0;
            foreach (QsoSignalSample s in analyzed)
            {
                if (s.Dbm > peak) peak = s.Dbm;
                sum += s.Dbm;
            }

            bool enough = analyzed.Count >= MinSamplesForStats
                          && r.AnalyzedSpanSeconds >= MinSecondsForStats;

            double[] smoothed = Smooth(analyzed);

            // The trough and the swing come from smoothed readings clear of any
            // gap: beside a hole, the two-second average is built from one side
            // only, and a low there is the hole, not the signal (#452). If
            // every reading sits beside a gap, use them all rather than nothing.
            bool[] besideGap = BesideGaps(analyzed, gaps);
            double smoothMin = double.MaxValue, smoothMax = double.MinValue;
            bool anyClear = Array.IndexOf(besideGap, false) >= 0;
            r.GapExclusionApplied = gaps.Count > 0 && anyClear;
            for (int i = 0; i < smoothed.Length; i++)
            {
                if (anyClear && besideGap[i]) continue;
                double v = smoothed[i];
                if (v < smoothMin) smoothMin = v;
                if (v > smoothMax) smoothMax = v;
            }

            if (enough)
            {
                r.HasStats = true;
                r.PeakDbm = peak;
                r.TroughDbm = smoothMin;
                r.MeanDbm = sum / analyzed.Count;
            }
            else
            {
                // Not enough for statistics — but the verdicts below must still
                // be named, so fall through with the too-short verdicts set.
                r.Qsb = QsbVerdict.TooShortToAssess;
                r.Trend = TrendVerdict.TooShortToAssess;
                return r;
            }

            AssessQsb(r, analyzed, smoothed, smoothMax - smoothMin, gaps);
            AssessTrend(r, analyzed, smoothed);
            return r;
        }

        // -------- coverage (#452) --------

        /// <summary>
        /// Find the holes in the readings: gaps between consecutive receive
        /// readings longer than <see cref="GapSpacingMultiple"/> times the
        /// capture's own median spacing. Fills the coverage fields of
        /// <paramref name="r"/> and returns the gaps.
        /// </summary>
        /// <remarks>
        /// A gap with a transmit reading at either end is not counted: that
        /// time is the operator's own over, already attributed to transmit.
        /// </remarks>
        private static List<(double Start, double End)> FindGaps(
            QsoSignalAnalysisResult r, IReadOnlyList<QsoSignalSample> samples)
        {
            var gaps = new List<(double Start, double End)>();
            var spacings = new List<double>();
            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i].LocalTransmit || samples[i - 1].LocalTransmit) continue;
                double d = samples[i].OffsetSeconds - samples[i - 1].OffsetSeconds;
                if (d > 0.0) spacings.Add(d);
            }
            if (spacings.Count < 2) return gaps;

            spacings.Sort();
            int mid = spacings.Count / 2;
            double typical = spacings.Count % 2 == 1
                ? spacings[mid]
                : (spacings[mid - 1] + spacings[mid]) / 2.0;
            r.TypicalSpacingSeconds = typical;
            if (typical <= 0.0) return gaps;

            double limit = typical * GapSpacingMultiple;
            double missing = 0.0, longest = 0.0;
            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i].LocalTransmit || samples[i - 1].LocalTransmit) continue;
                double start = samples[i - 1].OffsetSeconds, end = samples[i].OffsetSeconds;
                double d = end - start;
                if (d <= limit) continue;
                gaps.Add((start, end));
                missing += d - typical;
                if (d > longest) longest = d;
            }

            // The capture's EDGES, judged by the same cadence (repair round
            // one, Codex's blocker). Readings between start and the first
            // reading, and between the last reading and stop, were never
            // observed either: a capture started while the stream was stalled,
            // or stopped before it recovered, used to report full coverage and
            // "Steady" over time nothing was measured in. An edge whose reading
            // was taken while transmitting is the operator's own over, as above.
            if (samples.Count > 0)
            {
                QsoSignalSample first = samples[0], last = samples[samples.Count - 1];
                double head = first.OffsetSeconds;
                if (!first.LocalTransmit && head > limit)
                {
                    gaps.Insert(0, (0.0, head));
                    missing += head - typical;
                    if (head > longest) longest = head;
                }
                double tail = r.CaptureSeconds - last.OffsetSeconds;
                if (!last.LocalTransmit && tail > limit)
                {
                    gaps.Add((last.OffsetSeconds, r.CaptureSeconds));
                    missing += tail - typical;
                    if (tail > longest) longest = tail;
                }
            }

            r.GapCount = gaps.Count;
            r.MissingSeconds = missing;
            r.LongestGapSeconds = longest;
            double window = r.CaptureSeconds > 0.0 ? r.CaptureSeconds : r.AnalyzedSpanSeconds;
            r.Coverage = window > 0.0 ? Math.Clamp(1.0 - missing / window, 0.0, 1.0) : 1.0;
            return gaps;
        }

        /// <summary>
        /// Whether a moment falls in or beside a gap: inside it, or within one
        /// smoothing window of either edge. Half a window is where the smoothed
        /// value is built from one side of the hole only; the other half is the
        /// reach of the readings at the hole's very edge, which arrive as the
        /// stream stalls and recovers and are the least trustworthy of all.
        /// </summary>
        private static bool NearGap(double t, List<(double Start, double End)> gaps)
        {
            double margin = SmoothingWindowSeconds;
            foreach ((double start, double end) in gaps)
                if (t >= start - margin && t <= end + margin) return true;
            return false;
        }

        private static bool[] BesideGaps(List<QsoSignalSample> analyzed, List<(double Start, double End)> gaps)
        {
            var near = new bool[analyzed.Count];
            if (gaps.Count == 0) return near;
            for (int i = 0; i < analyzed.Count; i++)
                near[i] = NearGap(analyzed[i].OffsetSeconds, gaps);
            return near;
        }

        private static bool GapBetween(double a, double b, List<(double Start, double End)> gaps)
        {
            foreach ((double start, double end) in gaps)
                if (start >= a && end <= b) return true;
            return false;
        }

        // -------- fading --------

        private static void AssessQsb(
            QsoSignalAnalysisResult r,
            List<QsoSignalSample> analyzed,
            double[] smoothed,
            double smoothedSwing,
            List<(double Start, double End)> gaps)
        {
            if (r.AnalyzedSpanSeconds < MinSecondsForQsb)
            {
                r.Qsb = QsbVerdict.TooShortToAssess;
                return;
            }

            if (smoothedSwing < SteadyBelowDb)
            {
                r.Qsb = QsbVerdict.NoSignificantFading;
                return;
            }

            double hysteresis = Math.Max(FadeHysteresisFloorDb, smoothedSwing * 0.4);
            List<(double Time, double Value, bool IsTrough)> extremes =
                ZigZag(analyzed, smoothed, hysteresis);

            // A fade bottom on or beside a gap in the readings is not counted
            // as a fade at all (#452): one phantom trough corrupts the rhythm
            // for the whole capture, so it is set aside rather than reported
            // with a caveat. The peak-to-trough moves either side of it go too.
            int setAside = 0;
            var counted = new bool[extremes.Count];
            for (int i = 0; i < extremes.Count; i++)
            {
                counted[i] = !(gaps.Count > 0 && NearGap(extremes[i].Time, gaps));
                if (!counted[i] && extremes[i].IsTrough) setAside++;
            }
            r.FadesSetAsideAtGaps = setAside;

            // Depth: mean move between adjacent committed extremes.
            double depthSum = 0.0;
            int depthMoves = 0;
            for (int i = 1; i < extremes.Count; i++)
            {
                if (!counted[i] || !counted[i - 1]) continue;
                depthSum += Math.Abs(extremes[i].Value - extremes[i - 1].Value);
                depthMoves++;
            }
            r.FadeDepthDb = depthMoves > 0 ? depthSum / depthMoves : smoothedSwing;

            // Trough-to-trough spacings, only between consecutive counted fade
            // bottoms with no gap between them: across a hole, a real fade may
            // have gone unseen, which would double the spacing.
            var spacings = new List<double>();
            double? previousTrough = null;
            for (int i = 0; i < extremes.Count; i++)
            {
                if (!extremes[i].IsTrough) continue;
                if (!counted[i]) { previousTrough = null; continue; }
                double t = extremes[i].Time;
                if (previousTrough is double p && !GapBetween(p, t, gaps))
                    spacings.Add(t - p);
                previousTrough = t;
            }

            int intervals = spacings.Count;
            if (intervals < 2)
            {
                r.QsbCycleCount = intervals;
                r.Qsb = QsbVerdict.TooFewCycles;
                return;
            }

            double meanInterval = 0.0;
            foreach (double s in spacings) meanInterval += s;
            meanInterval /= intervals;

            double variance = 0.0;
            foreach (double s in spacings)
            {
                double d = s - meanInterval;
                variance += d * d;
            }
            double cv = meanInterval > 0.0 ? Math.Sqrt(variance / intervals) / meanInterval : 0.0;

            r.QsbCycleCount = intervals;
            r.QsbPeriodSeconds = meanInterval;
            r.Qsb = cv > IrregularCv ? QsbVerdict.Irregular : QsbVerdict.Periodic;
        }

        /// <summary>
        /// Classic zigzag extreme detection with hysteresis: an extreme is
        /// committed only once the smoothed series has reversed away from it by
        /// at least <paramref name="hysteresis"/> dB, so ripple inside a fade
        /// never counts as a cycle.
        /// </summary>
        private static List<(double Time, double Value, bool IsTrough)> ZigZag(
            List<QsoSignalSample> analyzed, double[] smoothed, double hysteresis)
        {
            var extremes = new List<(double, double, bool)>();
            int direction = 0; // 0 unknown, +1 rising toward a peak, -1 falling toward a trough
            double candHi = smoothed[0], candHiTime = analyzed[0].OffsetSeconds;
            double candLo = smoothed[0], candLoTime = analyzed[0].OffsetSeconds;

            for (int i = 1; i < smoothed.Length; i++)
            {
                double v = smoothed[i];
                double t = analyzed[i].OffsetSeconds;

                if (direction == 0)
                {
                    if (v > candHi) { candHi = v; candHiTime = t; }
                    if (v < candLo) { candLo = v; candLoTime = t; }
                    if (candHi - candLo >= hysteresis)
                    {
                        // Whichever extreme came first is the series' opening
                        // extreme; we are now moving away from it.
                        if (candHiTime < candLoTime)
                        {
                            extremes.Add((candHiTime, candHi, false));
                            direction = -1;
                        }
                        else
                        {
                            extremes.Add((candLoTime, candLo, true));
                            direction = 1;
                        }
                    }
                }
                else if (direction > 0)
                {
                    if (v > candHi) { candHi = v; candHiTime = t; }
                    else if (candHi - v >= hysteresis)
                    {
                        extremes.Add((candHiTime, candHi, false));
                        direction = -1;
                        candLo = v; candLoTime = t;
                    }
                }
                else
                {
                    if (v < candLo) { candLo = v; candLoTime = t; }
                    else if (v - candLo >= hysteresis)
                    {
                        extremes.Add((candLoTime, candLo, true));
                        direction = 1;
                        candHi = v; candHiTime = t;
                    }
                }
            }
            return extremes;
        }

        // -------- trend --------

        private static void AssessTrend(
            QsoSignalAnalysisResult r, List<QsoSignalSample> analyzed, double[] smoothed)
        {
            if (r.AnalyzedSpanSeconds < MinSecondsForTrend)
            {
                r.Trend = TrendVerdict.TooShortToAssess;
                return;
            }

            // Least-squares line through the smoothed series.
            int n = smoothed.Length;
            double meanT = 0.0, meanV = 0.0;
            for (int i = 0; i < n; i++)
            {
                meanT += analyzed[i].OffsetSeconds;
                meanV += smoothed[i];
            }
            meanT /= n; meanV /= n;

            double num = 0.0, den = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dt = analyzed[i].OffsetSeconds - meanT;
                num += dt * (smoothed[i] - meanV);
                den += dt * dt;
            }
            if (den <= 0.0)
            {
                r.Trend = TrendVerdict.Steady;
                return;
            }

            double slope = num / den; // dB per second
            r.TrendTotalDb = slope * r.AnalyzedSpanSeconds;

            if (r.TrendTotalDb >= TrendThresholdDb) r.Trend = TrendVerdict.Rising;
            else if (r.TrendTotalDb <= -TrendThresholdDb) r.Trend = TrendVerdict.Falling;
            else r.Trend = TrendVerdict.Steady;
        }

        // -------- smoothing --------

        /// <summary>
        /// Centered moving average over a <see cref="SmoothingWindowSeconds"/>
        /// time window. Two-pointer, O(n), tolerant of the meter stream's
        /// uneven spacing.
        /// </summary>
        private static double[] Smooth(List<QsoSignalSample> analyzed)
        {
            int n = analyzed.Count;
            var result = new double[n];
            double half = SmoothingWindowSeconds / 2.0;
            int lo = 0, hi = 0;
            double windowSum = 0.0;

            for (int i = 0; i < n; i++)
            {
                double center = analyzed[i].OffsetSeconds;
                while (hi < n && analyzed[hi].OffsetSeconds <= center + half)
                {
                    windowSum += analyzed[hi].Dbm;
                    hi++;
                }
                while (lo < hi && analyzed[lo].OffsetSeconds < center - half)
                {
                    windowSum -= analyzed[lo].Dbm;
                    lo++;
                }
                result[i] = windowSum / (hi - lo);
            }
            return result;
        }
    }
}
