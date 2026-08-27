#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Radios.Fixer.Evidence;

namespace Radios.SignalCapture
{
    /// <summary>
    /// The capture as one continuous document, in both of its forms — the
    /// detail tier of #271's two-tier output. The spoken headline is
    /// <see cref="QsoSignalHeadline"/>; this page is where the numbers live,
    /// readable at leisure and copyable into a log or an email.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything that could not be determined is named (#271, standing
    /// rule).</b> An absent measurement and a null result sound identical to a
    /// listener and need opposite responses from them — "too short to show a
    /// fade cycle" sends the operator back with a longer capture; "no
    /// significant fading" is an answer. No field is ever silently omitted.
    /// </para>
    /// <para>
    /// <b>Findings are stated as observations (#217).</b> Every number here is
    /// one a reader could take themselves from the radio's own meter stream,
    /// and the "How these numbers were taken" section states the method
    /// precisely enough to check.
    /// </para>
    /// </remarks>
    public static class QsoSignalCaptureReport
    {
        /// <summary>Render both report forms and the list-row peak into the
        /// record. Call once, at the moment the capture stops.</summary>
        public static void Bake(QsoSignalCaptureRecord record, QsoSignalAnalysisResult analysis)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            List<EvidenceSection> sections = Build(record, analysis);
            record.ReportText = EvidenceReportDocument.PlainText(sections);
            record.ReportHtml = EvidenceReportDocument.HtmlFragment(sections, headingLevel: 2);
            record.PeakDisplay = analysis.HasStats
                ? SMeterReading.Display(SMeterReading.FromDbm(analysis.PeakDbm))
                : "";
        }

        /// <summary>The one content model both forms render. Public so tests
        /// can assert the words without parsing HTML.</summary>
        public static List<EvidenceSection> Build(
            QsoSignalCaptureRecord record, QsoSignalAnalysisResult analysis)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            var sections = new List<EvidenceSection>
            {
                Header(record),
                SignalSection(analysis),
                TransmitSection(analysis),
                ContextSection(record, analysis),
                MethodSection(analysis),
            };
            return sections;
        }

        // -------- sections --------

        private static EvidenceSection Header(QsoSignalCaptureRecord record)
        {
            var s = new EvidenceSection();
            s.Para("JJ Flexible QSO signal capture report");
            s.Para("Capture ID: " + record.CaptureId);
            if (!string.IsNullOrWhiteSpace(record.Label))
                s.Para("Name: " + record.Label);
            s.Para("Capture started " + Stamp(record.StartedUtc) + ".");
            s.Para("It ran " + SpokenDuration.English(record.CaptureSeconds)
                 + " and was " + (string.IsNullOrWhiteSpace(record.EndReason)
                     ? "stopped" : record.EndReason)
                 + ". This report was written at that moment; a capture is never "
                 + "added to after it ends.");
            return s;
        }

        private static EvidenceSection SignalSection(QsoSignalAnalysisResult a)
        {
            var s = new EvidenceSection { Title = "What the signal did" };

            if (a.SampleCount == 0)
            {
                s.Para("No meter readings arrived during this capture, so nothing was "
                     + "measured. That is a gap in the data, not a quiet band — a quiet "
                     + "band still produces readings, down near the noise floor.");
                return s;
            }

            if (a.AnalyzedCount == 0)
            {
                s.Para("Every reading in this capture was taken while you were "
                     + "transmitting, so nothing could be measured about anyone else's "
                     + "signal. Nothing was determined.");
                return s;
            }

            if (!a.HasStats)
            {
                s.Para("Only " + Count(a.AnalyzedCount) + " usable "
                     + (a.AnalyzedCount == 1 ? "reading" : "readings") + " over "
                     + SpokenDuration.English(a.AnalyzedSpanSeconds)
                     + " of receive time — too little to characterize a signal. Nothing "
                     + "under " + WholeSeconds(QsoSignalAnalysis.MinSecondsForStats)
                     + " seconds of receive time can be. Peak, trough, average, fading "
                     + "and trend were all left undetermined rather than guessed at.");
                return s;
            }

            s.Bullet("Peaked at " + Display(a.PeakDbm) + " — the highest single reading.");
            s.Bullet("Fell to " + Display(a.TroughDbm) + " — the lowest two-second average.");
            s.Bullet("Averaged " + Display(a.MeanDbm) + " across "
                   + SpokenDuration.English(a.AnalyzedSpanSeconds) + " of receive time.");
            s.Bullet(a.SwingSUnits < 1
                ? "Total swing under one S-unit (" + WholeDb(a.SwingDb) + " dB)."
                : "Total swing about " + SUnits(a.SwingSUnits)
                  + " (" + WholeDb(a.SwingDb) + " dB).");

            s.Para(QsbSentence(a));
            s.Para(TrendSentence(a));
            return s;
        }

        private static string QsbSentence(QsoSignalAnalysisResult a)
        {
            switch (a.Qsb)
            {
                case QsbVerdict.TooShortToAssess:
                    return "Fading was not assessed: that needs at least "
                         + WholeSeconds(QsoSignalAnalysis.MinSecondsForQsb)
                         + " seconds of receive time, and this capture had "
                         + SpokenDuration.English(a.AnalyzedSpanSeconds) + ".";

                case QsbVerdict.NoSignificantFading:
                    return "No significant fading: the smoothed signal stayed within one "
                         + "S-unit the whole time. One honest limit: fades spaced more "
                         + "than about " + WholeSeconds(a.LongestObservablePeriodSeconds)
                         + " seconds apart would not have shown in a capture this long.";

                case QsbVerdict.TooFewCycles:
                    return "The signal moved more than one S-unit, but fewer than two "
                         + "complete fade cycles fit in this capture, so no fading rhythm "
                         + "could be measured — a longer capture would answer this. The "
                         + "movement seen averaged about " + WholeDb(a.FadeDepthDb)
                         + " dB deep.";

                case QsbVerdict.Periodic:
                    return (a.DeepFading ? "Deep, regular fading" : "Regular fading")
                         + ": about every " + Seconds(a.QsbPeriodSeconds) + " seconds, "
                         + Count(a.QsbCycleCount) + " complete "
                         + (a.QsbCycleCount == 1 ? "cycle" : "cycles")
                         + " seen, averaging " + WholeDb(a.FadeDepthDb) + " dB deep — about "
                         + SUnits((int)Math.Max(1, Math.Round(a.FadeDepthDb / 6.0))) + ".";

                case QsbVerdict.Irregular:
                    return (a.DeepFading ? "Deep fading at an irregular rhythm" :
                            "Fading at an irregular rhythm") + ": "
                         + Count(a.QsbCycleCount) + " complete cycles averaging "
                         + Seconds(a.QsbPeriodSeconds) + " seconds apart, but too unevenly "
                         + "spaced to call a period. Fades averaged "
                         + WholeDb(a.FadeDepthDb) + " dB deep.";

                default:
                    return "Fading was not assessed: nothing was measured.";
            }
        }

        private static string TrendSentence(QsoSignalAnalysisResult a)
        {
            switch (a.Trend)
            {
                case TrendVerdict.TooShortToAssess:
                    return "The overall trend was not assessed: that needs at least "
                         + WholeSeconds(QsoSignalAnalysis.MinSecondsForTrend)
                         + " seconds of receive time, and this capture had "
                         + SpokenDuration.English(a.AnalyzedSpanSeconds) + ".";

                case TrendVerdict.Steady:
                    return "No overall trend: fades aside, the fitted change across the "
                         + "capture was under one S-unit.";

                case TrendVerdict.Rising:
                    return "Coming up: fades aside, about " + WholeDb(a.TrendTotalDb)
                         + " dB stronger at the end of the capture than at the start.";

                case TrendVerdict.Falling:
                    return "Going down: fades aside, about " + WholeDb(-a.TrendTotalDb)
                         + " dB weaker at the end of the capture than at the start.";

                default:
                    return "The overall trend was not assessed: nothing was measured.";
            }
        }

        private static EvidenceSection TransmitSection(QsoSignalAnalysisResult a)
        {
            var s = new EvidenceSection { Title = "Your own transmissions" };
            if (a.TransmitSeconds < 0.5)
            {
                s.Para("You did not transmit during this capture.");
            }
            else
            {
                s.Para("You transmitted for about "
                     + SpokenDuration.English(a.TransmitSeconds)
                     + " of this capture. Readings taken while you were transmitting "
                     + "were left out of every measurement above — during your own "
                     + "over, the S-meter is not describing the other station.");
            }
            return s;
        }

        private static EvidenceSection ContextSection(
            QsoSignalCaptureRecord r, QsoSignalAnalysisResult a)
        {
            var s = new EvidenceSection { Title = "Where this was measured" };

            s.Bullet(Observation("Frequency", r.FrequencyText));
            s.Bullet(Observation("Mode", r.ModeText));
            s.Bullet(Observation("Slice", r.SliceLetter));
            s.Bullet(Observation("Radio", r.RadioModelText));

            if (r.FrequencyChanged)
                s.Para("The receive frequency changed during this capture, so these "
                     + "readings may mix more than one signal. Treat the numbers above "
                     + "as describing the capture window, not a single station.");
            if (r.ModeChanged)
                s.Para("The mode changed during this capture.");
            if (r.SliceChanged)
                s.Para("The active slice changed during this capture. The capture "
                     + "follows whichever slice is active — the same signal your "
                     + "S-meter reads — so readings from more than one slice are mixed "
                     + "here.");

            if (a.BufferFilled)
                s.Para("The capture buffer filled at " + Count(a.SampleCount)
                     + " readings; later readings were not kept. The measurements "
                     + "describe the window up to that point only.");
            return s;
        }

        private static string Observation(string name, string value)
            => string.IsNullOrWhiteSpace(value)
                ? name + ": could not be read when the capture started."
                : name + ": " + value;

        private static EvidenceSection MethodSection(QsoSignalAnalysisResult a)
        {
            var s = new EvidenceSection { Title = "How these numbers were taken" };
            s.Para(Count(a.SampleCount) + " readings over "
                 + SpokenDuration.English(a.CaptureSeconds)
                 + ", from the radio's own S-meter stream for the receive slice, "
                 + "recorded in dBm at the stream's full rate.");
            s.Para("S-units follow this application's meter calibration: S0 at minus "
                 + "124 dBm, 6 dB per S-unit, S9 at minus 70 dBm, and readings above "
                 + "S9 given as dB over S9 — the same arithmetic as the live S-meter "
                 + "readout, so a reading here matches what pressing Control S at that "
                 + "moment would have spoken.");
            s.Para("The peak is the highest single reading. The trough and all fading "
                 + "measurements use a two-second moving average, so a pause between "
                 + "words does not count as a fade.");
            s.Para("A fade cycle is counted only when the smoothed signal reverses by "
                 + "at least one S-unit, or forty percent of its total swing, "
                 + "whichever is larger; the fading rhythm is the average spacing "
                 + "between fade bottoms.");
            s.Para("The trend is a least-squares line through the smoothed readings.");
            return s;
        }

        // -------- rendering helpers --------

        private static string Stamp(DateTime utc)
            => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        private static string Display(double dbm)
            => SMeterReading.Display(SMeterReading.FromDbm(dbm));

        private static string SUnits(int n)
            => n == 1 ? "1 S-unit" : n.ToString(CultureInfo.InvariantCulture) + " S-units";

        private static string WholeDb(double db)
            => Math.Round(db).ToString("0", CultureInfo.InvariantCulture);

        private static string WholeSeconds(double seconds)
            => Math.Round(seconds).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>Fade spacing: one decimal under ten seconds ("2.5"),
        /// whole above ("12").</summary>
        private static string Seconds(double seconds)
            => seconds < 10.0
                ? Math.Round(seconds, 1).ToString("0.#", CultureInfo.InvariantCulture)
                : Math.Round(seconds).ToString("0", CultureInfo.InvariantCulture);

        private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }
}
