using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Power Genius XL's interlock reason survives FlexLib (#1225).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> When the PG-XL holds off transmit, the radio reports
    /// <c>reason=AMP:PG-XL</c> on the interlock topic. Stock FlexLib had no
    /// value for it, and its status parser deliberately let it through as
    /// <c>None</c>, so a refused tune could only say "the radio will not
    /// transmit here", never why.
    /// </para>
    /// <para>
    /// <b>The fix.</b> A minimal JJFlex patch (MIGRATION.md item 31): an
    /// appended <c>InterlockReason.PGXL</c>, and the reason parser maps the
    /// PG-XL reason to it. The tune answer then names the amplifier.
    /// </para>
    /// <para>
    /// <b>What these cannot see.</b> A real PG-XL holding the interlock: the
    /// reason string is the research's reading of the radio's wiki and of
    /// FlexLib's own special case for it, not a captured line.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class PgxlInterlockReasonTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static Radio NewRadio() => (Radio)Activator.CreateInstance(
            typeof(Radio), Private, binder: null, args: new object[] { false }, culture: null)!;

        private static void Interlock(Radio radio, string status) =>
            typeof(Radio).GetMethod("ParseInterlockStatus", Private)!.Invoke(radio, new object[] { status });

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

        [Fact]
        public void The_PG_XL_reason_is_named_not_erased()
        {
            var radio = NewRadio();
            Interlock(radio, "state=NOT_READY reason=AMP:PG-XL");
            Assert.Equal(InterlockReason.PGXL, radio.InterlockReason);
        }

        [Fact]
        public void The_PG_XL_reason_replaces_an_earlier_one()
        {
            var radio = NewRadio();
            Interlock(radio, "reason=OUT_OF_BAND");
            Assert.Equal(InterlockReason.OUT_OF_BAND, radio.InterlockReason);

            Interlock(radio, "reason=AMP:PG-XL");
            Assert.Equal(InterlockReason.PGXL, radio.InterlockReason);
        }

        [Fact]
        public void Positive_control_the_tuners_reason_and_an_empty_reason_parse_as_before()
        {
            var radio = NewRadio();
            Interlock(radio, "reason=AMP:TG");
            Assert.Equal(InterlockReason.TGXL, radio.InterlockReason);

            Interlock(radio, "reason=");
            Assert.Equal(InterlockReason.None, radio.InterlockReason);
        }

        [Fact]
        public void The_new_value_is_appended_so_no_existing_value_moves()
        {
            Assert.Equal((int)InterlockReason.TGXL + 1, (int)InterlockReason.PGXL);
            Assert.Equal(10, (int)InterlockReason.TGXL);
        }

        [Fact]
        public void A_tune_the_PG_XL_holds_off_names_the_amplifier_not_the_tuner()
        {
            var reading = new TuneReading(Transmitting: false, Busy: false, TxAllowed: false, NotReady: true,
                                          Reason: InterlockReason.PGXL, Source: PTTSource.None, AnotherClient: false);
            string said = TuneAnswer.NotKeyedSentence(reading);

            Assert.Equal(Lexicon.Get("audio.tune.refused_power_amplifier"), said);
            Assert.NotEqual(Lexicon.Get("audio.tune.refused_amplifier"), said);
            Assert.NotEqual(Lexicon.Get("audio.tune.refused"), said);
            Assert.NotEqual("audio.tune.refused_power_amplifier", said);
        }

        [Fact]
        public void The_patch_is_marked_and_listed()
        {
            string radio = Src("FlexLib_API", "FlexLib", "Radio.cs");
            Assert.True(Regex.Matches(radio, @"JJFlex patch \(MIGRATION\.md item 31\)").Count >= 2,
                "the enum value and the reason parser each carry the JJFlex patch comment naming MIGRATION.md item 31");
            Assert.Contains("reason = InterlockReason.PGXL;", radio, StringComparison.Ordinal);

            string migration = Src("MIGRATION.md");
            Assert.Contains("31. **The Power Genius XL's interlock reason is kept", migration, StringComparison.Ordinal);
        }
    }
}
