using System;
using System.Linq;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 38 Track E (#350). The measured half of the receive report: the
    /// rolling traffic window, the facts built from it, and the stage-4 rules
    /// that read them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receive report was nine facts and every one of them was a SETTING, so
    /// it could be entirely correct while no audio had ever reached the computer.
    /// FlexLib had counted the arriving bytes all along and nothing read them.
    /// </para>
    /// <para>
    /// <b>The tests that matter most are the ones asserting what this must NOT
    /// do.</b> A naive rate reading accuses working stations, and that would be
    /// worse than the gap it closes: zero is the CORRECT answer for an operator
    /// listening on the radio's own speaker, and "we have not looked yet" is a
    /// different answer again from "nothing arrived". Every one of those is a
    /// test below.
    /// </para>
    /// <para>
    /// No radio, no window and no thread. The window is pure and the rules are
    /// data, so every branch is reachable from here.
    /// </para>
    /// </remarks>
    public sealed class ReceiveTrafficTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 28, 7, 48, 3, DateTimeKind.Utc);

        // ── The window itself ────────────────────────────────────────────────

        [Fact]
        public void An_empty_window_reports_no_samples_rather_than_zero_traffic()
        {
            // The whole design rests on this. A window with nothing in it must
            // never be summarised as "we measured zero", because the fact source
            // turns that into an accusation.
            RxTrafficReading r = new RxTrafficWindow().Snapshot();

            Assert.False(r.HasSamples);
            Assert.Equal(0, r.SampleCount);
            Assert.Null(r.NewestAtUtc);
            Assert.Null(r.OldestAtUtc);
        }

        [Fact]
        public void The_window_reports_the_highest_and_the_most_recent_of_each_stream()
        {
            var w = new RxTrafficWindow();
            w.Add(10, 40, 4, T0);
            w.Add(42, 61, 5, T0.AddSeconds(1));
            w.Add(38, 55, 4, T0.AddSeconds(2));

            RxTrafficReading r = w.Snapshot();

            Assert.Equal(3, r.SampleCount);
            Assert.Equal(42, r.AudioPeakKbps);
            Assert.Equal(38, r.AudioLatestKbps);
            Assert.Equal(61, r.TotalPeakKbps);
            Assert.Equal(55, r.TotalLatestKbps);
            Assert.Equal(5, r.MeterPeakKbps);
            Assert.Equal(4, r.MeterLatestKbps);
            Assert.Equal(T0, r.OldestAtUtc);
            Assert.Equal(T0.AddSeconds(2), r.NewestAtUtc);
        }

        [Fact]
        public void The_window_counts_how_many_readings_actually_carried_audio()
        {
            // This is the number that answers Don's question directly: not "what
            // rate", but "did any audio arrive, and how often".
            var w = new RxTrafficWindow();
            w.Add(0, 12, 4, T0);
            w.Add(42, 61, 5, T0.AddSeconds(1));
            w.Add(0, 12, 4, T0.AddSeconds(2));
            w.Add(41, 60, 5, T0.AddSeconds(3));

            RxTrafficReading r = w.Snapshot();

            Assert.Equal(4, r.SampleCount);
            Assert.Equal(2, r.AudioReadingsWithTraffic);
        }

        [Fact]
        public void The_window_drops_the_oldest_reading_once_it_is_full()
        {
            var w = new RxTrafficWindow(3);
            w.Add(1, 1, 1, T0);
            w.Add(2, 2, 2, T0.AddSeconds(1));
            w.Add(3, 3, 3, T0.AddSeconds(2));
            w.Add(4, 4, 4, T0.AddSeconds(3));

            RxTrafficReading r = w.Snapshot();

            Assert.Equal(3, r.SampleCount);
            Assert.Equal(T0.AddSeconds(1), r.OldestAtUtc);
            Assert.Equal(4, r.AudioPeakKbps);
        }

        [Fact]
        public void Clearing_the_window_goes_back_to_having_looked_at_nothing()
        {
            // A reconnect must never be described with the previous radio's
            // numbers.
            var w = new RxTrafficWindow();
            w.Add(42, 61, 5, T0);
            w.Clear();

            Assert.False(w.Snapshot().HasSamples);
        }

        [Fact]
        public void The_window_describes_itself_in_readings_and_never_in_seconds()
        {
            // Deliberate. We sample on our timer and FlexLib publishes on its
            // own; the two are not phase-locked, so "ten readings about a second
            // apart" is true whatever the phase and "ten seconds of audio" is
            // not.
            var w = new RxTrafficWindow();
            w.Add(42, 61, 5, T0);
            w.Add(41, 60, 5, T0.AddSeconds(1));

            string text = w.Snapshot().DescribeWindow();

            Assert.Contains("2 readings", text);
            Assert.Contains("about a second apart", text);
            Assert.DoesNotContain("seconds of", text);
        }

        [Fact]
        public void A_window_that_has_never_been_fed_says_so_in_words()
        {
            Assert.Contains("no readings", new RxTrafficWindow().Snapshot().DescribeWindow());
        }

        // ── The facts ────────────────────────────────────────────────────────

        [Fact]
        public void With_no_radio_the_traffic_facts_are_absent_and_never_zero()
        {
            // A fabricated zero here would fire "no audio is arriving" at
            // somebody who has not even connected yet.
            DiagnosticFacts f = RxChainFacts.Collect(null);

            foreach (string name in new[] { "rx-audio-kbps", "rx-audio-readings",
                                            "rx-total-kbps", "rx-meter-kbps" })
            {
                DiagnosticFact fact = f.Find(name);
                Assert.NotNull(fact);
                Assert.Equal(FactState.Absent, fact.State);
                Assert.Null(fact.Number);
                Assert.False(string.IsNullOrWhiteSpace(fact.Why),
                             "\"" + name + "\" is absent without saying why");
            }
        }

        [Fact]
        public void The_traffic_facts_come_last_so_the_evidence_block_still_reads_as_a_walk()
        {
            DiagnosticFacts f = RxChainFacts.Collect(null);
            var names = f.All.Select(x => x.Name).ToList();

            Assert.True(names.IndexOf("pc-audio") < names.IndexOf("rx-audio-kbps"),
                        "the routing switch should be stated before what arrived across it");
            Assert.Equal(names.Count - 1, names.IndexOf("rx-meter-kbps"));
        }

        // ── The sentence an operator hears ───────────────────────────────────

        private static DiagnosticFacts Arriving(int peakKbps, int withAudio, int of,
                                                int totalKbps, int meterKbps, bool pcAudio)
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", pcAudio));
            f.Add(DiagnosticFact.Measure("rx-audio-kbps", "Audio arriving over the network from the radio",
                                         peakKbps, "kilobits per second", "the radio", T0));
            f.Add(DiagnosticFact.Measure("rx-audio-readings", "Readings in which audio was arriving",
                                         withAudio, "of " + of, "the radio", T0));
            f.Add(DiagnosticFact.Measure("rx-total-kbps", "All data arriving from the radio",
                                         totalKbps, "kilobits per second", "the radio", T0));
            f.Add(DiagnosticFact.Measure("rx-meter-kbps", "Meter readings arriving from the radio",
                                         meterKbps, "kilobits per second", "the radio", T0));
            return f;
        }

        [Fact]
        public void When_audio_is_arriving_the_sentence_says_so_with_the_number_and_the_window()
        {
            string s = RxChainFacts.ArrivalSentence(Arriving(42, 10, 10, 61, 5, pcAudio: true));

            Assert.Contains("Audio arriving from the radio", s);
            Assert.Contains("42", s);
            Assert.Contains("10 of 10 readings", s);
            Assert.Contains("about a second apart", s);
            Assert.Contains("61", s);
        }

        [Fact]
        public void A_correct_zero_on_the_radios_own_speaker_is_not_reported_as_a_fault()
        {
            // The commonest setup there is. The sentence has to carry its own
            // scope or it reads as an accusation.
            string s = RxChainFacts.ArrivalSentence(Arriving(0, 0, 10, 12, 4, pcAudio: false));

            Assert.Contains("none measured", s);
            Assert.Contains("none is expected", s);
            Assert.Contains("the sound stays at the radio", s);
        }

        [Fact]
        public void A_zero_while_computer_audio_is_on_says_audio_should_have_been_arriving()
        {
            string s = RxChainFacts.ArrivalSentence(Arriving(0, 0, 10, 12, 4, pcAudio: true));

            Assert.Contains("none measured", s);
            Assert.Contains("should be arriving here", s);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void When_nothing_has_been_measured_the_sentence_says_that_and_gives_the_reason(bool quiet)
        {
            var f = new DiagnosticFacts();
            const string label = "Audio arriving over the network from the radio";
            const string reason = "no traffic readings have been taken yet";
            f.Add(quiet
                ? DiagnosticFact.Silent("rx-audio-kbps", label, reason, "the radio")
                : DiagnosticFact.Absent("rx-audio-kbps", label, reason, "the radio"));

            string s = RxChainFacts.ArrivalSentence(f);

            Assert.Contains("not measured", s);
            Assert.Contains("no traffic readings have been taken yet", s);
            // And it must NOT claim a quantity it does not have.
            Assert.DoesNotContain("none measured", s);
        }

        [Fact]
        public void The_sentence_is_a_measurement_and_never_a_verdict()
        {
            // The Fixer's honest-subject rule: what WE observed, not whether the
            // radio is faulty. Nothing here may read as a diagnosis.
            foreach (string s in new[]
            {
                RxChainFacts.ArrivalSentence(Arriving(42, 10, 10, 61, 5, pcAudio: true)),
                RxChainFacts.ArrivalSentence(Arriving(0, 0, 10, 12, 4, pcAudio: true)),
                RxChainFacts.ArrivalSentence(Arriving(0, 0, 10, 12, 4, pcAudio: false)),
            })
            {
                Assert.DoesNotContain("broken", s, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("fault", s, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("wrong", s, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── The evidence line a Flex engineer reads ──────────────────────────

        [Fact]
        public void The_audio_reading_carries_when_it_was_taken_and_over_what()
        {
            // #217: this line has to survive a reader who distrusts our software
            // entirely, so it must say where the number came from and what
            // window it covers rather than asserting a bare figure.
            DiagnosticFact fact = DiagnosticFact.Measure(
                "rx-audio-kbps", "Audio arriving over the network from the radio",
                42, "kilobits per second",
                "the radio's network audio stream, highest of 10 readings about a second apart, "
                + "from 07:48:03 to 07:48:12",
                DateTime.UtcNow.AddSeconds(-2));

            string line = fact.EvidenceLine();

            Assert.Contains("42 kilobits per second", line);
            Assert.Contains("10 readings about a second apart", line);
            Assert.Contains("07:48:03 to 07:48:12", line);
            Assert.Contains("ago", line);
        }

        // ── The rules ────────────────────────────────────────────────────────

        private static DiagnosticRuleSet Rules()
        {
            RuleSetLoader.Forget();
            return RuleSetLoader.RxChain();
        }

        /// <summary>A connected radio with nothing wrong on the settings side,
        /// so only stage 4 can decide anything.</summary>
        private static DiagnosticFacts Silent(bool pcAudio, bool remote = true)
        {
            var f = new DiagnosticFacts();
            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", true));
            f.Add(DiagnosticFact.Flag("headphone-muted", "The headphone output is muted", false));
            f.Add(DiagnosticFact.Flag("lineout-muted", "The line out output is muted", false));
            f.Add(DiagnosticFact.Flag("front-speaker-muted", "The front speaker is muted", false));
            f.Add(DiagnosticFact.Measure("headphone-level", "Headphone level", 60));
            f.Add(DiagnosticFact.Measure("lineout-level", "Line out level", 60));
            f.Add(DiagnosticFact.Flag("pc-audio", "Radio audio through this computer", pcAudio));
            f.Add(DiagnosticFact.Flag("remote-radio", "Connected remotely", remote));
            return f;
        }

        private static DiagnosticFacts WithTraffic(DiagnosticFacts f, int audio, int withAudio,
                                                   int of, int total, int meter)
        {
            f.Add(DiagnosticFact.Measure("rx-audio-kbps", "Audio arriving over the network from the radio",
                                         audio, "kilobits per second", "the radio", T0));
            f.Add(DiagnosticFact.Measure("rx-audio-readings", "Readings in which audio was arriving",
                                         withAudio, "of " + of, "the radio", T0));
            f.Add(DiagnosticFact.Measure("rx-total-kbps", "All data arriving from the radio",
                                         total, "kilobits per second", "the radio", T0));
            f.Add(DiagnosticFact.Measure("rx-meter-kbps", "Meter readings arriving from the radio",
                                         meter, "kilobits per second", "the radio", T0));
            return f;
        }

        private static StageResult Stage4(ChainReport r) => r.Stages.Single(s => s.Stage.Number == 4);

        [Fact]
        public void The_shipped_rules_still_parse_with_the_measurement_stage_in_them()
        {
            DiagnosticRuleSet set = Rules();
            Assert.True(set.Problems.Count == 0,
                        "the shipped receive rule file did not parse cleanly:" + Environment.NewLine
                        + string.Join(Environment.NewLine, set.Problems));
            Assert.Contains(set.Stages, s => s.Number == 4);
            foreach (string id in new[] { "rx-nothing-arriving", "rx-meters-but-no-audio",
                                          "rx-data-but-no-audio" })
                Assert.True(set.Rules.Any(r => r.Id == id), "rule \"" + id + "\" is missing");
        }

        [Fact]
        public void Every_measurement_rule_is_gated_on_computer_audio_being_on()
        {
            // The gate is not optional. An ungated rule here accuses every
            // operator listening on the radio's own speaker.
            DiagnosticRuleSet set = Rules();
            foreach (DiagnosticRule rule in set.RulesFor(4))
            {
                Assert.True(rule.Needs.Any(c => string.Equals(c.FactName, "pc-audio",
                                                              StringComparison.OrdinalIgnoreCase)),
                            "rule \"" + rule.Id + "\" reads the traffic without checking pc-audio first");
            }
        }

        [Fact]
        public void Audio_that_is_arriving_leaves_the_measurement_stage_healthy()
        {
            ChainReport r = ChainAnalyzer.Run(Rules(),
                WithTraffic(Silent(pcAudio: true), audio: 42, withAudio: 10, of: 10,
                            total: 61, meter: 5));

            Assert.Equal(StageVerdict.Healthy, Stage4(r).Verdict);
            Assert.Equal(0, r.StagesBroken);
        }

        [Fact]
        public void Silence_on_the_radios_own_speaker_is_not_a_finding()
        {
            // PC audio off, no Opus traffic, and nothing wrong. If this ever
            // fires, the check has started accusing the commonest setup there is.
            ChainReport r = ChainAnalyzer.Run(Rules(),
                WithTraffic(Silent(pcAudio: false, remote: false), audio: 0, withAudio: 0, of: 10,
                            total: 12, meter: 4));

            Assert.Equal(0, r.StagesBroken);
            Assert.NotEqual(StageVerdict.Broken, Stage4(r).Verdict);
            Assert.Contains("none is expected", string.Join(" ", Stage4(r).Reasons));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Nothing_measured_yet_is_reported_as_unchecked_and_never_as_a_fault(bool quietRatherThanMissing)
        {
            // Seconds after a connect the window is empty. That must read as "we
            // have not looked", not as "no audio arrived".
            //
            // Both unreadable states are covered on purpose. The app produces
            // SILENT here — we are watching and have no reading yet — and would
            // produce ABSENT only if the sampler itself could not be read. They
            // say different things to a reader and neither may fire a rule.
            DiagnosticFacts f = Silent(pcAudio: true);
            const string why = "no traffic readings have been taken yet";
            foreach (string name in new[] { "rx-audio-kbps", "rx-audio-readings",
                                            "rx-total-kbps", "rx-meter-kbps" })
                f.Add(quietRatherThanMissing
                    ? DiagnosticFact.Silent(name, name, why, "the radio")
                    : DiagnosticFact.Absent(name, name, why, "the radio"));

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.Equal(0, r.StagesBroken);
            Assert.Equal(StageVerdict.NotObservable, Stage4(r).Verdict);
            Assert.Contains(why, string.Join(" ", Stage4(r).Reasons));
        }

        [Fact]
        public void No_traffic_of_any_kind_is_reported_as_the_link_rather_than_the_audio()
        {
            ChainReport r = ChainAnalyzer.Run(Rules(),
                WithTraffic(Silent(pcAudio: true), audio: 0, withAudio: 0, of: 10,
                            total: 0, meter: 0));

            StageResult s = Stage4(r);
            Assert.Equal(StageVerdict.Broken, s.Verdict);
            Assert.Equal("rx-nothing-arriving", s.Rule.Id);
            Assert.Contains("nothing at all", s.Message);
        }

        [Fact]
        public void Meters_arriving_without_audio_is_its_own_diagnosis()
        {
            ChainReport r = ChainAnalyzer.Run(Rules(),
                WithTraffic(Silent(pcAudio: true), audio: 0, withAudio: 0, of: 10,
                            total: 9, meter: 4));

            StageResult s = Stage4(r);
            Assert.Equal(StageVerdict.Broken, s.Verdict);
            Assert.Equal("rx-meters-but-no-audio", s.Rule.Id);
            // The rule quotes the operator's own numbers rather than a generic
            // sentence, which is what makes it worth reading.
            Assert.Contains("4 kilobits per second", s.Message);
            Assert.Contains("0 of 10", s.Message);
        }

        [Fact]
        public void Other_data_without_audio_or_meters_still_gets_an_answer()
        {
            ChainReport r = ChainAnalyzer.Run(Rules(),
                WithTraffic(Silent(pcAudio: true), audio: 0, withAudio: 0, of: 10,
                            total: 5, meter: 0));

            StageResult s = Stage4(r);
            Assert.Equal(StageVerdict.Broken, s.Verdict);
            Assert.Equal("rx-data-but-no-audio", s.Rule.Id);
            Assert.Contains("5 kilobits per second", s.Message);
        }

        [Fact]
        public void The_earlier_settings_stages_still_speak_before_the_measurement_does()
        {
            // A muted output is the answer even when no audio is arriving, and it
            // has to stay the answer: the chain fails at its earliest break.
            DiagnosticFacts f = Silent(pcAudio: true);
            f.Add(DiagnosticFact.Flag("headphone-muted", "The headphone output is muted", true));
            WithTraffic(f, audio: 0, withAudio: 0, of: 10, total: 9, meter: 4);

            ChainReport r = ChainAnalyzer.Run(Rules(), f);

            Assert.NotNull(r.FirstBroken);
            Assert.Equal(1, r.FirstBroken.Stage.Number);
        }

        [Fact]
        public void Every_fact_the_measurement_rules_name_is_one_the_fact_source_supplies()
        {
            // A rule naming a fact nobody collects reads as a check that could
            // not be made — silently, for ever. Collect with no radio still
            // declares every name it knows about, which is what makes this
            // comparable.
            DiagnosticFacts supplied = RxChainFacts.Collect(null);
            var known = supplied.All.Select(x => x.Name).ToList();

            foreach (DiagnosticRule rule in Rules().RulesFor(4))
                foreach (string name in rule.AllFactNames())
                    Assert.True(known.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)),
                                "rule \"" + rule.Id + "\" reads \"" + name
                                + "\", which nothing in RxChainFacts collects");
        }
    }
}
