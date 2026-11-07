using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The braille status line and the Audio Workshop's live meters show the
    /// computed SWR, never the radio's raw SWR meter, and each says so in words
    /// when there is no reading.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class SwrReadoutTests
    {
        [Theory]
        [InlineData(float.NaN)]
        [InlineData(-25f)]   // FlexBase.SWRNoReading, the radio's sentinel
        [InlineData(0.5f)]
        public void No_reading_is_words_on_every_surface(float swr)
        {
            Assert.False(SwrReadout.IsReading(swr));
            string words = Lexicon.Get("audio.tune.swr_no_reading");
            Assert.Equal(words, SwrReadout.BrailleCell(swr, wide: true));
            Assert.Equal(words, SwrReadout.MeterBoxText(swr));
            Assert.Equal(Lexicon.Get("connect.home.braille_swr_no_reading"),
                         SwrReadout.BrailleCell(swr, wide: false));
            Assert.DoesNotContain("-25", SwrReadout.BrailleCell(swr, wide: false));
            Assert.DoesNotContain("NaN", SwrReadout.MeterBoxText(swr));
        }

        [Fact]
        public void A_reading_keeps_the_shapes_the_displays_had()
        {
            // Positive control: a real reading still renders as a number.
            Assert.True(SwrReadout.IsReading(1.5f));
            Assert.Equal("SWR " + 1.5f.ToString("F1"), SwrReadout.BrailleCell(1.5f, wide: true));
            Assert.Equal("SW" + 1.5f.ToString("F1"), SwrReadout.BrailleCell(1.5f, wide: false));
            Assert.Equal("SWR: " + 1.5f.ToString("F1"), SwrReadout.MeterBoxText(1.5f));
            Assert.True(SwrReadout.IsReading(1.0f));
        }

        [Theory]
        [InlineData("JJFlexWpf/BrailleStatusEngine.cs")]
        [InlineData("JJFlexWpf/Dialogs/AudioWorkshopDialog.LiveMeters.cs")]
        public void The_display_readers_take_ComputedSWR_through_SwrReadout(string relative)
        {
            string src = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), relative));
            // Code, not comments: a comment may say what this used to read.
            string code = string.Join("\n", Array.FindAll(src.Split('\n'),
                l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotContain("SWRValue", code);
            Assert.Contains("SwrReadout.", code);
            Assert.Contains("ComputedSWR", code);
        }
    }
}
