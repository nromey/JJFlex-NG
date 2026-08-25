using System;
using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The order of the block an operator pastes into an email to FlexRadio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a test about SEQUENCE, which is unusual and is the point. The
    /// block was reordered on 2026-08-25 on Noel's ruling, and when the change
    /// was made the whole suite still passed — because nothing was guarding
    /// the order. A property that matters and is unguarded is one edit away
    /// from silently reverting, and this one cannot be caught by reading: both
    /// orders produce a perfectly sensible-looking document.
    /// </para>
    /// <para>
    /// <b>Why the order carries the meaning.</b> Noel: "They won't wanna touch
    /// JJ Flexible with a ten foot pole — that's us that does that. But they
    /// will care if we say we tried it in SmartSDR, here are the goods you can
    /// use to tell where fault happens." Leading with our verdict makes the
    /// document a third-party bug report, and the predictable reply is "that is
    /// third-party software, reproduce it in SmartSDR". Leading with the
    /// operator's own reproduction, then the radio's identity, then the
    /// readings, makes it a question about the radio with evidence attached.
    /// </para>
    /// <para>
    /// The litmus test for any line: would it still be useful to a reader who
    /// distrusts our software completely? See task #217.
    /// </para>
    /// </remarks>
    public class EvidenceOrderTests
    {
        private const string MinimalRules = @"
ruleset: Transmit chain check
version: 1
stage: 0 the connection to your radio
about: Everything else depends on this.
rule: no-radio
in-stage: 0
broken-when: radio-connected is no
verdict: No radio is connected, so none of your transmit chain can be checked.
fix: Connect to your radio and run this check again.
";

        private static ChainReport Report(bool connected)
        {
            DiagnosticRuleSet rules = DiagnosticRuleSet.Parse(MinimalRules, "test");
            var facts = new DiagnosticFacts();
            facts.Add(DiagnosticFact.Flag("radio-connected", "Radio connected", connected, "the radio"));
            return ChainAnalyzer.Run(rules, facts);
        }

        private static readonly string[] Station =
        {
            "Model: FLEX-6300",
            "Serial number: 1234-5678-6300-0001",
            "Firmware version: 3.8.24",
        };

        private static readonly string[] Build = { "JJ Flexible 4.1.16.1403" };

        private static int At(string text, string heading)
        {
            int i = text.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(i >= 0, "the evidence block is missing the section: " + heading);
            return i;
        }

        [Fact]
        public void The_operators_reproduction_claim_comes_first_of_all()
        {
            // THE test. This sentence is what reframes the document from a
            // third-party complaint into a question about the radio, so it
            // cannot sit below our own analysis.
            string t = Report(connected: false)
                .EvidenceText(Station, Build, "Also happens in SmartSDR, same radio, same session.");

            Assert.True(At(t, "Reproduced outside JJ Flexible") < At(t, "Radio"),
                "the reproduction claim must precede the radio identity");
            Assert.True(At(t, "Reproduced outside JJ Flexible")
                      < At(t, "What JJ Flexible made of the above"),
                "the reproduction claim must precede our interpretation");
        }

        [Fact]
        public void Our_interpretation_comes_LAST_and_says_it_is_ours()
        {
            // Not buried — Noel was explicit that our findings are welcome:
            // "tell them what we find, but not force them to the conclusion."
            // Last and labelled, so an engineer can discard this section
            // without discarding the measurements above it.
            string t = Report(connected: false).EvidenceText(Station, Build);

            int ours = At(t, "What JJ Flexible made of the above");
            Assert.True(ours > At(t, "Radio"));
            Assert.True(ours > At(t, "Readings, in signal-path order"));
            Assert.True(ours > At(t, "Stage by stage"));
            Assert.Contains("our interpretation, not a measurement", t);
            Assert.Contains("stand on their own", t);
        }

        [Fact]
        public void The_radio_and_its_readings_precede_anything_we_concluded()
        {
            // Everything a reader could have taken themselves comes before
            // anything only we assert. That is the litmus test made structural.
            string t = Report(connected: false).EvidenceText(Station, Build);

            Assert.True(At(t, "Radio") < At(t, "What JJ Flexible made of the above"));
            Assert.True(At(t, "Readings, in signal-path order")
                      < At(t, "What JJ Flexible made of the above"));
        }

        [Fact]
        public void With_no_reproduction_claim_the_section_is_omitted_not_hedged()
        {
            // We can never write this sentence ourselves and must never imply
            // it. An empty or hedged version in our voice would poison the one
            // section a vendor is most likely to act on.
            string t = Report(connected: false).EvidenceText(Station, Build);

            Assert.DoesNotContain("Reproduced outside JJ Flexible", t);
        }

        [Fact]
        public void The_radio_identity_is_present_because_it_is_what_support_asks_for_first()
        {
            string t = Report(connected: false).EvidenceText(Station, Build);

            Assert.Contains("FLEX-6300", t);
            Assert.Contains("Serial number", t);
            Assert.Contains("Firmware version", t);
        }

        [Fact]
        public void The_block_is_titled_as_measurements_rather_than_a_verdict()
        {
            // It said "evidence" until 2026-08-25, which reads as a case being
            // made. "Measurements" is what it actually contains and what a
            // vendor is willing to receive.
            string t = Report(connected: false).EvidenceText(Station, Build);

            Assert.Contains("— measurements", t);
        }

        [Fact]
        public void Nothing_falls_over_with_no_station_or_build_lines()
        {
            // Both are optional and both can fail to be read on a disconnected
            // radio. A half-filled evidence block is still worth sending.
            string t = Report(connected: true).EvidenceText();

            Assert.Contains("What JJ Flexible made of the above", t);
            Assert.DoesNotContain("Radio\n", t);
        }
    }
}
