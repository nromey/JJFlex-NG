using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.SignalCapture;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #452: a network stall during a signal capture is a hole in the
    /// readings, not a fade. The analysis finds the hole from the capture's
    /// own spacing, says how much of the window was measured, and never counts
    /// a fade that falls on one.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class QsoSignalCoverageTests
    {
        /// <summary>A trace at <paramref name="perSecond"/> readings a second,
        /// with nothing between <paramref name="gapStart"/> and
        /// <paramref name="gapEnd"/> for each gap.</summary>
        private static List<QsoSignalSample> Trace(
            double seconds, double perSecond, Func<double, double> dbmAt,
            params (double Start, double End)[] gaps)
        {
            var samples = new List<QsoSignalSample>();
            int n = (int)Math.Round(seconds * perSecond);
            for (int i = 0; i <= n; i++)
            {
                double t = i / perSecond;
                if (gaps.Any(g => t > g.Start && t < g.End)) continue;
                samples.Add(new QsoSignalSample(t, dbmAt(t), false));
            }
            return samples;
        }

        /// <summary>
        /// The stall's edges: the readings either side of each hole sag, as
        /// the stream degrades into and out of the stall. With no gap logic
        /// this is three troughs ten seconds apart on an otherwise steady S9
        /// signal: "fading about every 10 seconds", about the operator's Wi-Fi.
        /// </summary>
        private static Func<double, double> SteadyWithSaggingEdges((double Start, double End)[] gaps)
            => t => gaps.Any(g => (t <= g.Start && t > g.Start - 1.0) || (t >= g.End && t < g.End + 1.0))
                ? -110.0
                : -73.0;

        private static readonly (double, double)[] ThreeStalls =
            { (14.0, 16.5), (24.0, 26.5), (34.0, 36.5) };

        [Fact]
        public void A_clean_capture_finds_no_gaps_and_says_nothing_about_coverage()
        {
            var a = QsoSignalAnalysis.Analyze(Trace(60, 20, _ => -73.0), 60);

            Assert.Equal(0, a.GapCount);
            Assert.False(a.CoverageDegraded);
            Assert.Equal(1.0, a.Coverage);
            Assert.InRange(a.TypicalSpacingSeconds, 0.049, 0.051);

            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);
            Assert.DoesNotContain("missing", headline);
            Assert.DoesNotContain("percent", headline);
        }

        [Fact]
        public void Spliced_stalls_are_found_and_are_not_called_fades()
        {
            // THE POSITIVE CONTROL the entry asks for. First, prove the trace
            // really does read as fading when its holes are ignored: the same
            // readings, with the gaps closed up so nothing marks them.
            List<QsoSignalSample> withHoles = Trace(60, 20, SteadyWithSaggingEdges(ThreeStalls), ThreeStalls);
            var closedUp = new List<QsoSignalSample>();
            for (int i = 0; i < withHoles.Count; i++)
                closedUp.Add(new QsoSignalSample(i / 20.0, withHoles[i].Dbm, false));
            var blind = QsoSignalAnalysis.Analyze(closedUp, closedUp.Count / 20.0);
            Assert.Equal(0, blind.GapCount);
            Assert.True(blind.Qsb == QsbVerdict.Periodic || blind.Qsb == QsbVerdict.TooFewCycles
                        || blind.Qsb == QsbVerdict.Irregular,
                "the sagging edges must look like fades when nothing marks the holes, or this test proves nothing");

            // Now with the holes where they really are.
            var a = QsoSignalAnalysis.Analyze(withHoles, 60);
            Assert.Equal(3, a.GapCount);
            Assert.True(a.CoverageDegraded);
            Assert.InRange(a.LongestGapSeconds, 2.5, 2.6);
            Assert.InRange(a.MissingSeconds, 7.3, 7.6);
            Assert.InRange(a.Coverage, 0.875, 0.88);
            Assert.Equal(QsbVerdict.NoSignificantFading, a.Qsb);
            Assert.True(a.SwingSUnits < 1, "the trough must not be a stall's edge");
        }

        [Fact]
        public void No_sample_rate_is_assumed()
        {
            foreach (double rate in new[] { 8.0, 15.0, 40.0 })
            {
                var a = QsoSignalAnalysis.Analyze(Trace(60, rate, SteadyWithSaggingEdges(ThreeStalls), ThreeStalls), 60);
                Assert.Equal(3, a.GapCount);
                Assert.InRange(a.TypicalSpacingSeconds, 0.99 / rate, 1.01 / rate);
                Assert.Equal(QsbVerdict.NoSignificantFading, a.Qsb);
            }
        }

        [Fact]
        public void A_gap_in_real_fading_keeps_the_rhythm_and_the_worst_gap()
        {
            // 12-second QSB, as in the analysis tests, with one 3-second stall
            // spliced across a fade bottom (troughs fall at 9, 21, 33 ...).
            Func<double, double> qsb = t => -85.0 + 12.0 * Math.Sin(2 * Math.PI * t / 12.0);
            var clean = QsoSignalAnalysis.Analyze(Trace(120, 20, qsb), 120);
            var a = QsoSignalAnalysis.Analyze(Trace(120, 20, qsb, (31.5, 34.5)), 120);

            Assert.Equal(QsbVerdict.Periodic, clean.Qsb);
            Assert.Equal(QsbVerdict.Periodic, a.Qsb);
            Assert.InRange(a.QsbPeriodSeconds, clean.QsbPeriodSeconds - 0.5, clean.QsbPeriodSeconds + 0.5);
            Assert.Equal(1, a.GapCount);
            Assert.InRange(a.LongestGapSeconds, 3.0, 3.1);
            Assert.True(a.QsbCycleCount < clean.QsbCycleCount,
                "no fade spacing may be measured across the gap");
        }

        [Fact]
        public void Gaps_during_your_own_transmission_are_not_coverage_gaps()
        {
            var samples = Trace(60, 20, _ => -73.0, (20.0, 25.0))
                .Select(s => new QsoSignalSample(s.OffsetSeconds, s.Dbm,
                    s.OffsetSeconds >= 19.0 && s.OffsetSeconds <= 20.0))
                .ToList();
            var a = QsoSignalAnalysis.Analyze(samples, 60);
            Assert.Equal(0, a.GapCount);
        }

        [Fact]
        public void The_headline_names_degraded_coverage_before_the_numbers()
        {
            var a = QsoSignalAnalysis.Analyze(Trace(60, 20, SteadyWithSaggingEdges(ThreeStalls), ThreeStalls), 60);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

            string coverage = Lexicon.Get("audio.qso.coverage_gaps",
                ("coverage", QsoSignalHeadline.CoveragePercent(a.Coverage)),
                ("missing", SpokenDuration.English(a.MissingSeconds)),
                ("longest", SpokenDuration.English(a.LongestGapSeconds)));
            Assert.Contains(coverage, headline);
            int at = headline.IndexOf(coverage, StringComparison.Ordinal);
            int steady = headline.IndexOf("Steady at", StringComparison.Ordinal);
            Assert.True(steady > at, "the qualifier must be heard before the figure it qualifies");
        }

        [Fact]
        public void The_report_page_names_the_gaps_before_the_numbers_and_explains_the_rule()
        {
            var a = QsoSignalAnalysis.Analyze(Trace(60, 20, SteadyWithSaggingEdges(ThreeStalls), ThreeStalls), 60);
            var record = new QsoSignalCaptureRecord { CaptureId = "AAA-222", CaptureSeconds = 60 };
            string text = Radios.Fixer.Evidence.EvidenceReportDocument.PlainText(
                QsoSignalCaptureReport.Build(record, a));

            int gaps = text.IndexOf("Readings were missing at 3 points", StringComparison.Ordinal);
            int peak = text.IndexOf("Peaked at", StringComparison.Ordinal);
            Assert.True(gaps >= 0, text);
            Assert.True(peak > gaps);
            Assert.Contains("Only " + QsoSignalHeadline.CoveragePercent(a.Coverage) + " percent of the window was measured.", text);
            Assert.Equal("87", QsoSignalHeadline.CoveragePercent(a.Coverage)); // 7.35 of 60 seconds missing
            Assert.Contains("A gap is any stretch between readings longer than 10 times", text);
        }

        [Fact]
        public void Coverage_never_reads_as_one_hundred_percent_when_there_was_a_gap()
        {
            Assert.Equal("99", QsoSignalHeadline.CoveragePercent(0.9999));
            Assert.Equal("87", QsoSignalHeadline.CoveragePercent(0.879));
        }
        // ── Repair round one: the capture's edges count too ──

        private static string Headline(QsoSignalAnalysisResult a) =>
            QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

        [Fact]
        public void Stopping_before_the_stream_recovers_is_not_full_coverage()
        {
            // Codex's reproduction: 60 seconds, readings only to second 40.
            var a = QsoSignalAnalysis.Analyze(Trace(40, 20, _ => -73.0), 60);
            Assert.Equal(1, a.GapCount);
            Assert.InRange(a.MissingSeconds, 19.9, 20.0);
            Assert.InRange(a.LongestGapSeconds, 19.99, 20.01);
            Assert.InRange(a.Coverage, 0.66, 0.67);
            Assert.True(a.CoverageDegraded);
            Assert.Contains("only 66 percent of the capture was measured", Headline(a), StringComparison.Ordinal);
        }

        [Fact]
        public void Starting_during_a_stall_is_not_full_coverage()
        {
            var samples = Trace(60, 20, _ => -73.0).Where(s => s.OffsetSeconds >= 20.0).ToList();
            var a = QsoSignalAnalysis.Analyze(samples, 60);
            Assert.Equal(1, a.GapCount);
            Assert.InRange(a.Coverage, 0.66, 0.67);
            Assert.Contains("percent of the capture was measured", Headline(a), StringComparison.Ordinal);
        }

        [Fact]
        public void An_interior_hole_and_a_missing_tail_are_both_counted()
        {
            // Codex: with a hole from 20 to 25 as well, it reported 91 percent
            // where about 35 of 60 seconds were observed.
            var a = QsoSignalAnalysis.Analyze(Trace(40, 20, _ => -73.0, (20.0, 25.0)), 60);
            Assert.Equal(2, a.GapCount);
            Assert.InRange(a.MissingSeconds, 24.8, 25.0);
            Assert.InRange(a.Coverage, 0.58, 0.59);
            Assert.Contains("only 58 percent of the capture was measured", Headline(a), StringComparison.Ordinal);
        }

        [Fact]
        public void A_tail_shorter_than_the_gap_threshold_is_ordinary_spacing()
        {
            // Positive control for the edge rule's threshold: a stop a quarter
            // second after the last reading is cadence, not a stall.
            var a = QsoSignalAnalysis.Analyze(Trace(60, 20, _ => -73.0), 60.25);
            Assert.Equal(0, a.GapCount);
            Assert.False(a.CoverageDegraded);
        }

        [Fact]
        public void The_method_paragraph_says_what_was_done_when_every_reading_sits_beside_a_gap()
        {
            var record = new QsoSignalCaptureRecord { CaptureId = "AAA-222", CaptureSeconds = 10 };
            var all = QsoSignalAnalysis.Analyze(Trace(2, 20, _ => -73.0), 10);
            Assert.True(all.CoverageDegraded);
            Assert.False(all.GapExclusionApplied);
            string text = Radios.Fixer.Evidence.EvidenceReportDocument.PlainText(QsoSignalCaptureReport.Build(record, all));
            Assert.Contains("so the trough and the swing use all of them", text, StringComparison.Ordinal);
            Assert.DoesNotContain("leave out the smoothed readings", text, StringComparison.Ordinal);

            // Control: an ordinary degraded capture does leave them out, and says so.
            var some = QsoSignalAnalysis.Analyze(Trace(40, 20, _ => -73.0), 60);
            Assert.True(some.GapExclusionApplied);
            string other = Radios.Fixer.Evidence.EvidenceReportDocument.PlainText(
                QsoSignalCaptureReport.Build(new QsoSignalCaptureRecord { CaptureId = "AAA-223", CaptureSeconds = 60 }, some));
            Assert.Contains("leave out the smoothed readings", other, StringComparison.Ordinal);
        }
        [Fact]
        public void A_real_session_stopped_during_a_stall_reports_the_tail_as_missing()
        {
            // Codex's reachability control, through the public session and
            // its injected clock: readings every 50 ms to second 40, none from
            // 20 to 25, then Stop at second 60.
            DateTime t0 = new DateTime(2026, 11, 7, 1, 0, 0, DateTimeKind.Utc);
            DateTime now = t0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            for (int i = 0; i <= 800; i++)
            {
                double t = i * 0.05;
                if (t >= 20.0 && t <= 25.0) continue;
                now = t0.AddSeconds(t);
                session.Add(-73.0, transmitting: false);
            }
            now = t0.AddSeconds(60);
            QsoSignalAnalysisResult a = session.Stop("stopped by you").Analysis;
            Assert.Equal(2, a.GapCount);
            Assert.InRange(a.Coverage, 0.57, 0.60);
        }
        // ── Repair round two: receive time leaves the over out ──

        private static string ReportText(QsoSignalAnalysisResult a, double seconds) =>
            Radios.Fixer.Evidence.EvidenceReportDocument.PlainText(QsoSignalCaptureReport.Build(
                new QsoSignalCaptureRecord { CaptureId = "AAA-224", CaptureSeconds = seconds }, a));

        [Fact]
        public void A_capture_with_an_over_inside_calls_only_the_receive_part_receive_time()
        {
            // The bench's shape: 19 seconds, about 5 of them transmitting, the
            // S-meter arriving through the over at its usual cadence (and
            // flagged as transmit, so left out of every statistic).
            var samples = Trace(19, 8, _ => -100.0)
                .Select(s => new QsoSignalSample(s.OffsetSeconds, s.Dbm, s.OffsetSeconds >= 7.0 && s.OffsetSeconds < 12.0))
                .ToList();
            var a = QsoSignalAnalysis.Analyze(samples, 19);
            Assert.InRange(a.TransmitSeconds, 4.9, 5.1);
            Assert.InRange(a.AnalyzedSpanSeconds, 18.9, 19.1);
            Assert.InRange(a.ReceiveSeconds, 13.9, 14.1);

            string text = ReportText(a, 19);
            Assert.Contains("across 14 seconds of receive time", text, StringComparison.Ordinal);
            Assert.Contains("this capture had 14 seconds of it.", text, StringComparison.Ordinal);   // the trend rule
            Assert.DoesNotContain("19 seconds of receive time", text, StringComparison.Ordinal);
        }

        [Fact]
        public void With_no_over_receive_time_is_the_whole_span()
        {
            // Control: the same capture without the over keeps all 19 seconds.
            var a = QsoSignalAnalysis.Analyze(Trace(19, 8, _ => -100.0), 19);
            Assert.Equal(0.0, a.TransmitSeconds);
            Assert.InRange(a.ReceiveSeconds, 18.9, 19.1);
            Assert.Contains("across 19 seconds of receive time", ReportText(a, 19), StringComparison.Ordinal);
        }

        [Fact]
        public void The_minimum_lengths_are_judged_on_receive_time()
        {
            // 22 seconds in all with a 5-second over: over the 20-second trend
            // minimum by span, under it by receive time, so the trend is not
            // assessed. The control with no over is assessed.
            var withOver = Trace(22, 8, t => -100.0 + t)
                .Select(s => new QsoSignalSample(s.OffsetSeconds, s.Dbm, s.OffsetSeconds >= 8.0 && s.OffsetSeconds < 13.0))
                .ToList();
            Assert.Equal(TrendVerdict.TooShortToAssess, QsoSignalAnalysis.Analyze(withOver, 22).Trend);
            Assert.NotEqual(TrendVerdict.TooShortToAssess, QsoSignalAnalysis.Analyze(Trace(22, 8, t => -100.0 + t), 22).Trend);
        }
    }
}
