#nullable enable
using System;
using System.IO;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  The speaking-rate model (#557).
    //
    //  Three measurements exist, taken 2026-09-06 against NVDA 2026.2 at the
    //  operator's own rate with a quiet keyboard. They are pinned here as a
    //  BAND, not a point: the model must land within +5% and +25% of each,
    //  erring long. The old 80 ms/char constant was 45%, 138% and 62% over
    //  the same three, and a retune that drifts back toward that fails
    //  here rather than in a capture reading weeks later.
    // ────────────────────────────────────────────────────────────────
    public class SpeechRateModelTests
    {
        private const string Short = "Probe one.";                                                  // 551 ms
        private const string Medium = "This is the third and final probe utterance, and nothing should interrupt it."; // 2587 ms
        private const string Batch = "Connected to FLEX-8600, SmartLink, 4 slices. This radio had no mic profile, so I loaded Default. PC audio off. Recording is on. JJ Flexible Home, Modern tuning mode"; // 8079 ms

        [Theory]
        [InlineData(Short, 551)]
        [InlineData(Medium, 2587)]
        [InlineData(Batch, 8079)]
        public void LedgerEstimate_IsWithinTheBandOfTheMeasurement(string text, int actualMs)
        {
            int est = SpeechArbiter.EstimateSpokenMs(text);
            double over = (est - actualMs) / (double)actualMs;
            // The ledger's floor lifts the shortest one; that is the floor's
            // job (the reader's own traffic delays ours), not the model's.
            if (est == SpeechArbiter.SalvageMinMs) return;
            Assert.True(over >= 0.05 && over <= 0.25,
                $"'{text}': estimate {est} ms against {actualMs} measured is {over:P0} off; the band is +5% to +25%");
        }

        [Theory]
        [InlineData(Short, 551)]
        [InlineData(Medium, 2587)]
        [InlineData(Batch, 8079)]
        public void Model_IsFarCloserThanTheOldConstant(string text, int actualMs)
        {
            // Both models before the ledger's floor and cap, so the shortest
            // case compares the fits and not the floor they both land on.
            int oldModel = text.Length * SpeechArbiter.SalvageMsPerCharacter;
            int newModel = SpeechRateModel.Uncalibrated(text) * (100 + SpeechArbiter.SalvageMarginPercent) / 100;
            Assert.True(Math.Abs(newModel - actualMs) < Math.Abs(oldModel - actualMs),
                $"'{text}': new {newModel}, old {oldModel}, actual {actualMs}");
        }

        [Fact]
        public void Model_IsAffine_NotProportional()
        {
            // One word costs far more than a tenth of ten: the overhead is
            // real and the old model had none. No punctuation, so no pauses
            // muddy the comparison.
            int one = SpeechRateModel.Uncalibrated("Yes");
            int ten = SpeechRateModel.Uncalibrated("Yes Yes Yes Yes Yes Yes Yes Yes Yes Yes");
            Assert.True(one > SpeechRateModel.OverheadMs);
            Assert.True(ten < one * 10);
            Assert.Equal(SpeechRateModel.OverheadMs + 10 * SpeechRateModel.UnitMs, ten);
        }

        [Fact]
        public void Analyse_PricesExpansions_NotCharacters()
        {
            var flex = SpeechRateModel.Analyse("FLEX-8600");
            var word = SpeechRateModel.Analyse("Connected");
            Assert.True(flex.Units > word.Units, "FLEX-8600 expands to several spoken words; Connected is one");
            Assert.Equal(4, flex.Units);   // flex + eighty-six hundred

            Assert.Equal(2, SpeechRateModel.Analyse("PC").Units);       // spelled
            Assert.Equal(2, SpeechRateModel.Analyse("JJ").Units);       // spelled
            Assert.Equal(3, SpeechRateModel.Analyse("SWR").Units);      // spelled
            Assert.Equal(1, SpeechRateModel.Analyse("FLEX").Units);     // has a vowel: a word
            Assert.Equal(1, SpeechRateModel.Analyse("I").Units);        // one capital is a word
            Assert.Equal(1, SpeechRateModel.Analyse("15").Units);       // fifteen
            Assert.Equal(2, SpeechRateModel.Analyse("100").Units);      // one hundred
            Assert.Equal(7, SpeechRateModel.Analyse("14.100.000").Units); // fourteen point one hundred point zero zero zero
        }

        [Fact]
        public void Analyse_CountsPausesBetweenSentencesAndClauses()
        {
            var s = SpeechRateModel.Analyse(Batch);
            Assert.Equal(4, s.SentenceBreaks);   // slices. Default. off. on.  — the final "mode" has none
            Assert.Equal(4, s.ClauseBreaks);     // FLEX-8600, SmartLink, profile, Home,
            // 28 tokens, plus FLEX-8600 (+3: flex, eighty-six hundred), PC (+1) and JJ (+1).
            Assert.Equal(33, s.Units);

            // A pause after the LAST word is the overhead's business.
            Assert.Equal(0, SpeechRateModel.Analyse("Done.").SentenceBreaks);
            Assert.Equal(1, SpeechRateModel.Analyse("Done. Next").SentenceBreaks);
        }

        [Fact]
        public void Observe_MovesTheScaleTowardTheObservedRatio_AndClamps()
        {
            var m = new SpeechRateModel();
            Assert.Equal(1.0, m.Scale);

            // A reader twice as slow as the reference.
            int modelled = SpeechRateModel.Uncalibrated(Medium);
            for (int i = 0; i < 40; i++) m.Observe(Medium, modelled * 2);
            Assert.InRange(m.Scale, 1.9, 2.0);
            Assert.Equal(40, m.Samples);

            // Absurd samples cannot push it past the clamp.
            for (int i = 0; i < 40; i++) m.Observe(Medium, modelled * 50);
            Assert.Equal(SpeechRateModel.MaxScale, m.Scale);

            // The estimate follows the scale.
            Assert.Equal((int)Math.Round(modelled * SpeechRateModel.MaxScale), m.Estimate(Medium));
        }

        [Fact]
        public void Observe_IgnoresShortUtterances_WhichAreAllOverhead()
        {
            var m = new SpeechRateModel();
            m.Observe(Short, 5000);   // two words in five seconds says nothing about rate
            Assert.Equal(1.0, m.Scale);
            Assert.Equal(0, m.Samples);
            m.Observe("Something", 0);
            Assert.Equal(0, m.Samples);
        }

        [Fact]
        public void Persisted_RoundTripsThroughTheSettingsRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "jjflex-rate-" + Guid.NewGuid().ToString("N"));
            try
            {
                var m = SpeechRateModel.Persisted(root);
                int modelled = SpeechRateModel.Uncalibrated(Medium);
                for (int i = 0; i < 10; i++) m.Observe(Medium, (int)(modelled * 1.5));
                Assert.True(File.Exists(Path.Combine(root, SpeechRateModel.FileName)), "the calibration should have been written");

                var again = SpeechRateModel.Persisted(root);
                Assert.Equal(m.Scale, again.Scale, 3);
                Assert.True(again.Samples > 0);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
            }
        }

        [Fact]
        public void Persisted_WithNoRoot_IsTheReferenceRate_AndNeverThrows()
        {
            var m = SpeechRateModel.Persisted(string.Empty);
            Assert.Equal(1.0, m.Scale);
            int modelled = SpeechRateModel.Uncalibrated(Medium);
            for (int i = 0; i < 5; i++) m.Observe(Medium, modelled * 2);   // no path to write; must not throw
            Assert.True(m.Scale > 1.0);
        }

        [Fact]
        public void Persisted_IgnoresACorruptFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "jjflex-rate-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, SpeechRateModel.FileName), "{ not json");
                var m = SpeechRateModel.Persisted(root);
                Assert.Equal(1.0, m.Scale);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
            }
        }

        [Fact]
        public void TheLegacyConstant_IsStillEighty_ForTheBriefingYardstick()
        {
            // ConnectBriefing's settle trace and its tests quote this number
            // as a rough yardstick. It is NOT the ledger's rate any more; the
            // doc comment on it says so, and this pins the number the tests
            // in ConnectBriefingTests still compute with.
            Assert.Equal(80, SpeechArbiter.SalvageMsPerCharacter);
        }
    }
}
