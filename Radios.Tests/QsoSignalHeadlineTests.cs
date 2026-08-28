using System;
using System.Collections.Generic;
using Radios;
using Radios.SignalCapture;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The spoken headline (#271's first tier): exact sentences, because every
    /// word here is read aloud to the operator and reviewed as prose.
    /// </summary>
    /// <remarks>
    /// Joined to the RadioConfig statics collection because the headline is
    /// assembled from <see cref="Lexicon"/>, and LexiconTests in that
    /// collection calls <c>Lexicon.Forget()</c> — without this, that class can
    /// empty the store part-way through a test here.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class QsoSignalHeadlineTests
    {
        private static List<QsoSignalSample> Trace(
            double seconds, Func<double, double> dbmAt)
        {
            var samples = new List<QsoSignalSample>();
            for (double t = 0; t <= seconds; t += 0.1)
                samples.Add(new QsoSignalSample(t, dbmAt(t), false));
            return samples;
        }

        [Fact]
        public void ASteadySignalGetsTheWholeSentence()
        {
            var a = QsoSignalAnalysis.Analyze(Trace(60, _ => -79.0), 60);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

            Assert.Equal(
                "Capture stopped after 1 minute. Steady at S 8. "
                + "Saved as capture AAA-222, under Tools, Signal captures.",
                headline);
        }

        [Fact]
        public void DeepQsbLeadsWithTheRangeAndNamesTheRhythm()
        {
            var a = QsoSignalAnalysis.Analyze(
                Trace(90, t => -85.0 + 12.0 * Math.Sin(2 * Math.PI * t / 12.0)), 90);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

            Assert.StartsWith("Capture stopped after 1 minute 30 seconds. "
                + "Peaked S 9, fell to S 5, averaged S 7.", headline);
            Assert.Contains("Deep fades about every 12 seconds.", headline);
            Assert.EndsWith("Saved as capture AAA-222, under Tools, Signal captures.",
                headline);
        }

        [Fact]
        public void NoReadingsIsSpokenAsAGapNeverAsSilence()
        {
            var a = QsoSignalAnalysis.Analyze(new List<QsoSignalSample>(), 45);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

            Assert.Equal(
                "Capture stopped after 45 seconds. No meter readings arrived. "
                + "Nothing was measured. "
                + "Saved as capture AAA-222, under Tools, Signal captures.",
                headline);
        }

        [Fact]
        public void ATooShortCaptureSaysSoInsteadOfGuessing()
        {
            var a = QsoSignalAnalysis.Analyze(Trace(3, _ => -80.0), 3);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

            Assert.Contains("Too short to measure, so nothing was determined.", headline);
        }

        [Fact]
        public void AFailedSaveIsNamedNeverImplied()
        {
            var a = QsoSignalAnalysis.Analyze(Trace(60, _ => -79.0), 60);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: false, SMeterReading.Band.Hf);

            Assert.EndsWith("The capture could not be saved to disk.", headline);
            Assert.DoesNotContain("Saved as capture", headline);
        }

        [Fact]
        public void ARisingSignalCarriesTheTrendAndTheMissingRhythm()
        {
            var a = QsoSignalAnalysis.Analyze(Trace(60, t => -100.0 + t * 0.5), 60);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

            Assert.Contains("It moved, but too few fade cycles fit to measure a rhythm.",
                headline);
            Assert.Contains("Rising overall.", headline);
        }

        [Fact]
        public void AnOverS9PeakSpeaksInDbOverNine()
        {
            // Peaks at -50 dBm: 23 dB over S9 on the IARU HF scale. The
            // over-S9 wording must be the same one Ctrl+S uses, decibels AS
            // IS — never multiplied.
            var a = QsoSignalAnalysis.Analyze(
                Trace(60, t => -65.0 + 15.0 * Math.Sin(2 * Math.PI * t / 15.0)), 60);
            string headline = QsoSignalHeadline.Compose(a, "AAA-222", saved: true, SMeterReading.Band.Hf);

            Assert.Contains("Peaked S 9 plus 23 dB", headline);
        }
    }
}
