#nullable enable
using System.Text;

namespace Radios.SignalCapture
{
    /// <summary>
    /// The spoken tier of #271's two-tier output: strongest, weakest, anything
    /// notable — and nothing else. The detail lives on the report page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every shape of capture gets a complete sentence, including the shapes
    /// where nothing could be measured — "no meter readings arrived" and "too
    /// short to measure" are answers, and their absence would be
    /// indistinguishable from a quiet band. S-unit wording reuses the same
    /// lexicon keys as the Ctrl+S readout, so the analyzer and the live meter
    /// can never speak the same value two different ways.
    /// </para>
    /// </remarks>
    public static class QsoSignalHeadline
    {
        /// <summary>
        /// The sentence spoken when the operator stops a capture.
        /// </summary>
        /// <param name="band">
        /// Which S-unit calibration to speak in — the capture's own, from its
        /// receive frequency. Required rather than defaulted: the headline and
        /// the report must never name different S-units for one capture, and a
        /// default here would let them (#296).
        /// </param>
        public static string Compose(QsoSignalAnalysisResult a, string captureId, bool saved,
                                     SMeterReading.Band band)
        {
            var sb = new StringBuilder();
            sb.Append(Lexicon.Get("audio.qso.stopped",
                ("duration", SpokenDuration.English(a.CaptureSeconds))));

            Append(sb, Body(a, band));

            Append(sb, saved
                ? Lexicon.Get("audio.qso.saved", ("id", captureId))
                : Lexicon.Get("audio.qso.save_failed"));
            return sb.ToString();
        }

        private static string Body(QsoSignalAnalysisResult a, SMeterReading.Band band)
        {
            if (a.SampleCount == 0)
                return Lexicon.Get("audio.qso.nothing");
            if (a.AnalyzedCount == 0)
                return Lexicon.Get("audio.qso.all_transmit");
            if (!a.HasStats)
                return Lexicon.Get("audio.qso.too_short",
                    ("duration", SpokenDuration.English(a.AnalyzedSpanSeconds)));

            var sb = new StringBuilder();
            if (a.SwingSUnits < 1)
            {
                sb.Append(Lexicon.Get("audio.qso.steady", ("mean", SpokenS(a.MeanDbm, band))));
            }
            else
            {
                sb.Append(Lexicon.Get("audio.qso.range",
                    ("peak", SpokenS(a.PeakDbm, band)),
                    ("trough", SpokenS(a.TroughDbm, band)),
                    ("mean", SpokenS(a.MeanDbm, band))));
            }

            switch (a.Qsb)
            {
                case QsbVerdict.Periodic:
                    Append(sb, Lexicon.Get(
                        a.DeepFading ? "audio.qso.qsb_deep" : "audio.qso.qsb",
                        ("period", Period(a.QsbPeriodSeconds))));
                    break;
                case QsbVerdict.Irregular:
                    Append(sb, Lexicon.Get("audio.qso.qsb_irregular"));
                    break;
                case QsbVerdict.TooFewCycles:
                    Append(sb, Lexicon.Get("audio.qso.qsb_few"));
                    break;
                    // NoSignificantFading and TooShortToAssess stay out of the
                    // headline — the page names both; the headline is for what
                    // is notable.
            }

            if (a.Trend == TrendVerdict.Rising) Append(sb, Lexicon.Get("audio.qso.rising"));
            else if (a.Trend == TrendVerdict.Falling) Append(sb, Lexicon.Get("audio.qso.falling"));

            return sb.ToString();
        }

        /// <summary>A dBm value in the same spoken form as the Ctrl+S readout.</summary>
        private static string SpokenS(double dbm, SMeterReading.Band band)
        {
            int smeter = SMeterReading.FromDbm(dbm, band);
            return SMeterReading.IsOverS9(smeter)
                ? Lexicon.Get("audio.smeter.over_s9", ("over", SMeterReading.ExcessOverS9(smeter)))
                : Lexicon.Get("audio.smeter.s_units", ("smeter", smeter));
        }

        private static string Period(double seconds)
            => seconds < 10.0
                ? System.Math.Round(seconds, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                : System.Math.Round(seconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        private static void Append(StringBuilder sb, string sentence)
        {
            if (string.IsNullOrWhiteSpace(sentence)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }
    }
}
