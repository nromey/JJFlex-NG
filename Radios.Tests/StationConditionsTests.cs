using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one reader for the conditions a transmit measurement was taken under
    /// (#399), and the parser the frequency hand-off validates with.
    /// </summary>
    /// <remarks>
    /// Frequency, mode and antenna used to be read by two byte-identical
    /// triples, one in the tune probe and one in the differential capture. Two
    /// homes for one idea is how a run into a real antenna could record the
    /// frequency in one stage's evidence and nowhere else — and a document
    /// describing an on-air transmission that cannot say where it transmitted is
    /// a defect in its own right.
    /// </remarks>
    public class StationConditionsTests
    {
        [Theory]
        // Megahertz with a decimal point — what an operator types fastest.
        [InlineData("14.250", 14_250_000UL)]
        [InlineData("14.25", 14_250_000UL)]
        // Our own printed form, read back exactly.
        [InlineData("14.250000", 14_250_000UL)]
        [InlineData("7.074000", 7_074_000UL)]
        // The app's dotted grouping: megahertz, kilohertz, hertz.
        [InlineData("14.250.000", 14_250_000UL)]
        [InlineData("3.573.100", 3_573_100UL)]
        [InlineData("14.25.0", 14_250_000UL)]
        // Bare digits are kilohertz. "14250" is 20 metres, not 14.25 kHz.
        [InlineData("14250", 14_250_000UL)]
        [InlineData("50313", 50_313_000UL)]
        // Whitespace is the operator's, not a refusal.
        [InlineData("  14.250  ", 14_250_000UL)]
        public void A_frequency_an_operator_would_type_is_read(string typed, ulong hz)
        {
            Assert.True(StationConditions.TryParse(typed, out ulong got));
            Assert.Equal(hz, got);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("twenty metres")]
        [InlineData("14,250")]        // a comma is not our separator
        [InlineData("14.")]
        [InlineData(".250")]
        [InlineData("14.250.000.1")]  // four groups is not a frequency
        [InlineData("14.2500000")]    // more than six decimal places of MHz
        [InlineData("0")]
        [InlineData("14.250.0000")]   // a hertz group wider than three digits
        public void Anything_that_is_not_a_frequency_is_refused_rather_than_guessed(string typed)
        {
            // REFUSED, not approximated. This value goes to a transmitter: a
            // parser that took its best guess at "14,250" would move the
            // operator somewhere they did not ask to go, and they would find out
            // by keying there.
            Assert.False(StationConditions.TryParse(typed, out ulong got));
            Assert.Equal(0UL, got);
        }

        [Fact]
        public void The_printed_form_parses_back_to_the_number_it_came_from()
        {
            // The hand-off pre-fills its box with our own printed form, so the
            // round trip is not academic — it is the ordinary case, every time
            // the operator opens the box and presses OK without editing.
            foreach (ulong hz in new[] { 1_800_000UL, 7_074_000UL, 14_250_000UL,
                                         50_313_000UL, 144_200_000UL })
            {
                string printed = StationConditions.Format(hz).Replace(" MHz", "");
                Assert.True(StationConditions.TryParse(printed, out ulong back), printed);
                Assert.Equal(hz, back);
            }
        }

        [Fact]
        public void A_missing_radio_says_so_rather_than_reading_as_zero_megahertz()
        {
            // "0.000000 MHz" in a document about where a radio transmitted is a
            // plausible-looking lie, and it is what a bare TXFrequency read
            // produces on a session where the radio has not reported a transmit
            // slice.
            Assert.Equal(StationConditions.NotReported, StationConditions.Frequency(null));
            Assert.Equal(StationConditions.NotReported, StationConditions.Mode(null));
            Assert.Equal(StationConditions.NotReported, StationConditions.Antenna(null));
            Assert.Equal(0UL, StationConditions.FrequencyHz(null));
        }

        [Fact]
        public void The_evidence_line_names_all_three_even_when_none_is_known()
        {
            // A stage that keyed a transmitter must record where. An omitted
            // line reads as an oversight; a line saying "not reported" is a
            // fact about the radio.
            string line = StationConditions.Line(null);
            Assert.Contains("Frequency: not reported", line);
            Assert.Contains("Mode: not reported", line);
            Assert.Contains("Transmit antenna: not reported", line);
        }

        [Fact]
        public void The_run_sentence_omits_what_it_does_not_know_rather_than_saying_so_twice()
        {
            Assert.Equal(" on 14.250000 MHz in USB",
                StationConditions.OnInPhrase("14.250000 MHz", "USB"));
            Assert.Equal(" on 14.250000 MHz",
                StationConditions.OnInPhrase("14.250000 MHz", StationConditions.NotReported));
            // The evidence block is where "not reported" belongs. A sentence
            // telling an operator what pressing Run will do reads worse for
            // carrying it, so the clause simply drops.
            Assert.Equal("", StationConditions.OnInPhrase(StationConditions.NotReported,
                                                          StationConditions.CouldNotBeRead));
            Assert.Equal("", StationConditions.OnInPhrase("", ""));
        }

        // ------------------------------------------------------------------
        //  The shared refusal and confirmation vocabulary (#399, #411)
        // ------------------------------------------------------------------

        [Fact]
        public void A_rig_that_cannot_be_asked_reads_as_keyed()
        {
            // FAIL CLOSED. The frequency and mode hand-offs both gate on this
            // read, and the cost of wrongly allowing a change under a live
            // carrier is RF on the air. No rig at all is the plainest case.
            Assert.True(StationConditions.KeyedFailClosed(null));

            // And the POSITIVE CONTROL for the catch path: a constructed but
            // never-connected FlexBase throws from its TxTune read (theRadio
            // is null until a connection exists), so this proves the guard
            // turns a throw into "keyed" rather than into "go ahead".
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            Assert.True(StationConditions.KeyedFailClosed(rig));
        }

        [Fact]
        public void The_keyed_refusal_names_the_thing_it_refused_to_change()
        {
            // Refused out loud, not silently ignored — a button that does
            // nothing reads as a broken button. One sentence shape for both
            // hand-offs, so the operator hears one rule, not two dialects.
            Assert.Equal(
                "The radio is transmitting. Wait until it stops, then change "
                + "the frequency.",
                StationConditions.RefusedWhileKeyed("frequency"));
            Assert.Equal(
                "The radio is transmitting. Wait until it stops, then change "
                + "the mode.",
                StationConditions.RefusedWhileKeyed("mode"));
        }

        [Fact]
        public void The_confirmation_sentences_report_the_radio_never_the_request()
        {
            // #164: the radio acks transmit writes it does not apply, so every
            // sentence here is built from what the radio REPORTS. There is no
            // sentence in this vocabulary for "we sent the command" — that is
            // the sentence the frequency hand-off shipped with once, and it
            // told an operator they had moved when they had not.
            Assert.Equal("The radio now reports 14.250000 MHz.",
                StationConditions.NowReports("14.250000 MHz"));
            Assert.Equal("The radio now reports USB.",
                StationConditions.NowReports("USB"));

            Assert.Equal(
                "The radio has not confirmed that change. It still reports LSB.",
                StationConditions.NotConfirmedStillReports("LSB"));

            Assert.Equal(
                "The radio has not confirmed that change, and is not reporting "
                + "a transmit mode at all.",
                StationConditions.NotConfirmedNothingReported("mode"));
            Assert.Equal(
                "The radio has not confirmed that change, and is not reporting "
                + "a transmit frequency at all.",
                StationConditions.NotConfirmedNothingReported("frequency"));
        }
    }
}
