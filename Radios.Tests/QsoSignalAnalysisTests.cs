using System;
using System.Collections.Generic;
using Radios.SignalCapture;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The QSO signal analyzer's analysis engine (#271), fed synthetic traces
    /// with known shapes — which is the whole point of separating analysis
    /// from collection.
    /// </summary>
    /// <remarks>
    /// Every "could not be determined" shape has its own test, because the
    /// standing rule is that an absent measurement and a null result sound
    /// identical to a listener and need opposite responses: each must come
    /// back as a NAMED verdict, never as a silently missing field.
    /// </remarks>
    public sealed class QsoSignalAnalysisTests
    {
        // -------- trace builders --------

        /// <summary>A trace at 10 readings a second.</summary>
        private static List<QsoSignalSample> Trace(
            double seconds, Func<double, double> dbmAt, Func<double, bool> transmitAt = null)
        {
            var samples = new List<QsoSignalSample>();
            for (double t = 0; t <= seconds; t += 0.1)
                samples.Add(new QsoSignalSample(t, dbmAt(t), transmitAt?.Invoke(t) ?? false));
            return samples;
        }

        // -------- the shapes --------

        [Fact]
        public void SteadySignalReportsNoFadingAndNoTrend()
        {
            // -79 dBm with under a dB of ripple: S8 on the IARU HF scale.
            var samples = Trace(60, t => -79.0 + 0.4 * Math.Sin(2 * Math.PI * t / 3.0));
            var a = QsoSignalAnalysis.Analyze(samples, 60);

            Assert.True(a.HasStats);
            Assert.Equal(QsbVerdict.NoSignificantFading, a.Qsb);
            Assert.Equal(TrendVerdict.Steady, a.Trend);
            Assert.Equal(8, SMeterReading.FromDbm(a.PeakDbm, SMeterReading.Band.Hf));
            Assert.Equal(8, SMeterReading.FromDbm(a.TroughDbm, SMeterReading.Band.Hf));
            Assert.Equal(8, SMeterReading.FromDbm(a.MeanDbm, SMeterReading.Band.Hf));
            Assert.True(a.SwingSUnits < 1);
        }

        [Fact]
        public void DeepPeriodicFadeReportsPeriodDepthAndBounds()
        {
            // 12-second QSB, 24 dB peak to trough, for 90 seconds: the shape
            // Don's question describes. Peaks at -73, which is exactly S9 on
            // the IARU HF scale; troughs near -97.
            var samples = Trace(90, t => -85.0 + 12.0 * Math.Sin(2 * Math.PI * t / 12.0));
            var a = QsoSignalAnalysis.Analyze(samples, 90);

            Assert.True(a.HasStats);
            Assert.Equal(QsbVerdict.Periodic, a.Qsb);
            Assert.InRange(a.QsbPeriodSeconds, 10.5, 13.5);
            Assert.True(a.QsbCycleCount >= 4);
            Assert.True(a.DeepFading, "24 dB fades are deep by any measure");
            Assert.InRange(a.FadeDepthDb, 18.0, 26.0);
            Assert.Equal(9, SMeterReading.FromDbm(a.PeakDbm, SMeterReading.Band.Hf));
            Assert.Equal(5, SMeterReading.FromDbm(a.TroughDbm, SMeterReading.Band.Hf));
            Assert.Equal(TrendVerdict.Steady, a.Trend);
        }

        [Fact]
        public void RisingSignalReportsTheTrendAndNamesTheMissingRhythm()
        {
            // A clean 30 dB climb over a minute. It moved, so "no fading" would
            // be wrong; it never cycled, so a period would be a lie. The honest
            // verdict is TooFewCycles plus a rising trend.
            var samples = Trace(60, t => -100.0 + t * 0.5);
            var a = QsoSignalAnalysis.Analyze(samples, 60);

            Assert.Equal(TrendVerdict.Rising, a.Trend);
            Assert.True(a.TrendTotalDb > 20.0);
            Assert.Equal(QsbVerdict.TooFewCycles, a.Qsb);
        }

        [Fact]
        public void OneFadeIsNotARhythm()
        {
            // A single 20 dB dip and recovery inside 24 seconds: one trough,
            // zero complete cycles. Naming a period from that would be
            // authoritative-looking fiction.
            var samples = Trace(24, t =>
            {
                if (t < 8 || t > 16) return -80.0;
                double into = t < 12 ? (t - 8) / 4.0 : (16 - t) / 4.0;
                return -80.0 - 20.0 * into;
            });
            var a = QsoSignalAnalysis.Analyze(samples, 24);

            Assert.Equal(QsbVerdict.TooFewCycles, a.Qsb);
            Assert.True(a.FadeDepthDb > 10.0);
        }

        [Fact]
        public void TooShortACaptureNamesItselfRatherThanGuessing()
        {
            var samples = Trace(3, t => -80.0);
            var a = QsoSignalAnalysis.Analyze(samples, 3);

            Assert.False(a.HasStats);
            Assert.Equal(QsbVerdict.TooShortToAssess, a.Qsb);
            Assert.Equal(TrendVerdict.TooShortToAssess, a.Trend);
        }

        [Fact]
        public void NoReadingsAtAllIsItsOwnVerdictNotAQuietBand()
        {
            var a = QsoSignalAnalysis.Analyze(new List<QsoSignalSample>(), 45);

            Assert.Equal(0, a.SampleCount);
            Assert.False(a.HasStats);
            Assert.Equal(QsbVerdict.NothingMeasured, a.Qsb);
            Assert.Equal(TrendVerdict.NothingMeasured, a.Trend);
            Assert.Equal(45, a.CaptureSeconds, 3);
        }

        [Fact]
        public void ACaptureSpentEntirelyTransmittingMeasuresNothing()
        {
            var samples = Trace(30, t => -50.0, t => true);
            var a = QsoSignalAnalysis.Analyze(samples, 30);

            Assert.True(a.SampleCount > 0);
            Assert.Equal(0, a.AnalyzedCount);
            Assert.False(a.HasStats);
            Assert.Equal(QsbVerdict.NothingMeasured, a.Qsb);
            Assert.InRange(a.TransmitSeconds, 28.0, 30.5);
        }

        [Fact]
        public void OwnTransmissionsAreExcludedFromEveryStatistic()
        {
            // A steady S7 signal, except our own 20-second over pegs the meter
            // at -40. If the peak comes back over S9, the exclusion failed.
            var samples = Trace(60,
                t => (t >= 20 && t < 40) ? -40.0 : -80.0,
                t => t >= 20 && t < 40);
            var a = QsoSignalAnalysis.Analyze(samples, 60);

            Assert.True(a.HasStats);
            Assert.Equal(7, SMeterReading.FromDbm(a.PeakDbm, SMeterReading.Band.Hf));
            Assert.True(a.PeakDbm < -70.0, "the -40 dBm transmit readings must not be the peak");
            Assert.InRange(a.TransmitSeconds, 18.0, 22.0);
        }

        [Fact]
        public void TheBufferFilledFlagRidesThroughToTheResult()
        {
            var samples = Trace(30, t => -80.0);
            var a = QsoSignalAnalysis.Analyze(samples, 30, bufferFilled: true);
            Assert.True(a.BufferFilled);
        }

        [Fact]
        public void TheSteadyVerdictKnowsItsOwnHorizon()
        {
            // "Steady for 30 seconds" is only a verdict about rhythms faster
            // than half the span; the result carries that bound so the report
            // can name it.
            var samples = Trace(30, t => -79.0);
            var a = QsoSignalAnalysis.Analyze(samples, 30);
            Assert.Equal(a.AnalyzedSpanSeconds / 2.0, a.LongestObservablePeriodSeconds, 3);
        }
    }
}
