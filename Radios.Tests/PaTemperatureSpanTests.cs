#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// PA temperature during every keyed check stage (#236, ruled 2026-08-26):
    /// read at key-down and just before the carrier drops, reported in the
    /// stage's evidence and traced, and judged by nothing. A missing reading
    /// is "not reported" and changes no verdict, answer or finding.
    /// </summary>
    public sealed class PaTemperatureSpanTests
    {
        private static TxTuneProbe.Reading Got(double c) => TxTuneProbe.Reading.Got("PATEMP", c, "degC");
        private static TxTuneProbe.Reading Missing() => TxTuneProbe.Reading.Missing("PATEMP");

        private static PaTemperatureSpan Span(TxTuneProbe.Reading down, TxTuneProbe.Reading up)
        {
            var s = PaTemperatureSpan.Begin("a test stage", () => down);
            s.End(() => up);
            return s;
        }

        // ── the span itself ──

        [Fact]
        public void Both_readings_and_the_change_are_reported()
        {
            string line = Span(Got(41.5), Got(42.25)).EvidenceLine();
            Assert.Contains("at key-down 41.5 degC, at key-up 42.25 degC, a change of +0.75 degC.", line);
            Assert.Contains("nothing is judged on it", line);
        }

        [Fact]
        public void A_missing_reading_is_not_reported_and_has_no_change()
        {
            string line = Span(Missing(), Got(42)).EvidenceLine();
            Assert.Contains("at key-down not reported, at key-up 42 degC.", line);
            Assert.DoesNotContain("change", line);
        }

        [Fact]
        public void A_reader_that_throws_is_not_reported_and_never_escapes()
        {
            var s = PaTemperatureSpan.Begin("a test stage", () => throw new InvalidOperationException("meter gone"));
            s.End(() => throw new InvalidOperationException("meter gone"));
            Assert.False(s.AtKeyDown.Reported);
            Assert.False(s.AtKeyUp.Reported);
            Assert.True(s.Ended);
        }

        [Fact]
        public void Only_the_first_key_up_reading_counts()
        {
            var s = Span(Got(40), Got(41));
            s.End(() => Got(90));
            Assert.Equal(41, s.AtKeyUp.Value);
        }

        [Fact]
        public void No_span_writes_no_line()
        {
            Assert.Equal("", PaTemperatureSpan.EvidenceLines(null));
        }

        // ── the tune check ──

        [Fact]
        public void The_tune_check_samples_the_PA_temperature()
        {
            Assert.Contains(PaTemperatureSpan.Meter, TxTuneProbe.Watched);
        }

        public static IEnumerable<object[]> LoadCases() => new[]
        {
            new object[] { double.NaN, double.NaN },   // no forward power meter
            new object[] { 0.1, 0.0 },                 // no power
            new object[] { 1.0, 0.0 },                 // under the floor
            new object[] { 10.0, 0.01 },               // a good load
            new object[] { 10.0, 4.0 },                // a suspect load
        };

        [Theory]
        [MemberData(nameof(LoadCases))]
        public void A_PA_temperature_present_or_missing_never_changes_the_tune_verdict(double fwd, double rev)
        {
            List<TxTuneProbe.Reading> Meters(TxTuneProbe.Reading pa)
            {
                var m = new List<TxTuneProbe.Reading>();
                m.Add(double.IsNaN(fwd) ? TxTuneProbe.Reading.Missing("FWDPWR") : TxTuneProbe.Reading.Got("FWDPWR", fwd, "W"));
                m.Add(double.IsNaN(rev) ? TxTuneProbe.Reading.Missing("REFPWR") : TxTuneProbe.Reading.Got("REFPWR", rev, "W"));
                m.Add(TxTuneProbe.Reading.Missing("SWR"));
                m.Add(TxTuneProbe.Reading.Missing("ALC"));
                m.Add(pa);
                return m;
            }
            double swr = double.NaN;
            var without = TxTuneProbe.Assess(Meters(Missing()), swr);
            Assert.Equal(without, TxTuneProbe.Assess(Meters(Got(35)), swr));
            Assert.Equal(without, TxTuneProbe.Assess(Meters(Got(95)), swr));
        }

        [Fact]
        public void A_PA_temperature_alone_is_not_a_transmit_reading()
        {
            // The radio reports its PA temperature keyed or not, so a radio
            // that never transmitted must still read as "nothing measured".
            var onlyPa = TxTuneProbe.Watched.Select(n => n == "PATEMP" ? Got(40) : TxTuneProbe.Reading.Missing(n)).ToList();
            Assert.False(TxTuneProbeRunner.HasAnyReading(onlyPa));

            // Control: a forward power reading is one.
            var withFwd = TxTuneProbe.Watched.Select(n =>
                n == "PATEMP" ? Got(40) : n == "FWDPWR" ? TxTuneProbe.Reading.Got("FWDPWR", 10, "W") : TxTuneProbe.Reading.Missing(n)).ToList();
            Assert.True(TxTuneProbeRunner.HasAnyReading(withFwd));
        }

        [Fact]
        public void The_tune_evidence_carries_the_span()
        {
            var meters = TxTuneProbe.Watched.Select(n => TxTuneProbe.Reading.Missing(n)).ToList();
            var r = TxTuneProbe.Result.Ran(TxTuneProbe.Verdict.MakesPower, DateTime.UtcNow, meters, 10,
                double.NaN, false, "14.100", "USB", "ANT1", Span(Got(40), Got(41)));
            Assert.Contains("PA temperature (PATEMP", TxTuneProbe.EvidenceSection(r));
        }

        // ── the two audio stages ──

        private static InjectedTransmitFacts Injected(PaTemperatureSpan? pa) => new InjectedTransmitFacts
        {
            Keyed = true,
            Probes = new[]
            {
                new TxProbeSet.ProbeResult(TxProbeSet.Probe.SingleTone, TxProbeSet.Outcome.Unavailable, "test"),
            },
            Conditions = "Frequency 14.100",
            PaTemperature = pa,
        };

        private static SpokenTransmitFacts Spoken(PaTemperatureSpan? pa) => new SpokenTransmitFacts
        {
            Attempted = true,
            Keyed = true,
            ReachedRadio = true,
            Conditions = "Frequency 14.100",
            PaTemperature = pa,
        };

        private static string Shape(FixerOutcome o)
            => o.Answer + "|" + o.NothingMeasured + "|" + string.Join(",", o.Findings.Select(f => f.Id + ":" + f.Owner + ":" + f.Critical));

        [Fact]
        public void A_PA_temperature_present_or_missing_never_changes_an_audio_stage_outcome()
        {
            string injected = Shape(TransmitStages.Injected(Injected(null)));
            Assert.Equal(injected, Shape(TransmitStages.Injected(Injected(Span(Missing(), Missing())))));
            Assert.Equal(injected, Shape(TransmitStages.Injected(Injected(Span(Got(40), Got(95))))));

            string spoken = Shape(TransmitStages.Spoken(Spoken(null), micBaseline: null));
            Assert.Equal(spoken, Shape(TransmitStages.Spoken(Spoken(Span(Missing(), Missing())), micBaseline: null)));
            Assert.Equal(spoken, Shape(TransmitStages.Spoken(Spoken(Span(Got(40), Got(95))), micBaseline: null)));
        }

        [Fact]
        public void The_audio_stage_evidence_carries_the_span()
        {
            Assert.Contains("at key-down 40 degC, at key-up 41 degC",
                TransmitStages.Injected(Injected(Span(Got(40), Got(41)))).Evidence);
            Assert.Contains("at key-down 40 degC, at key-up 41 degC",
                TransmitStages.Spoken(Spoken(Span(Got(40), Got(41))), micBaseline: null).Evidence);
            Assert.DoesNotContain("PA temperature", TransmitStages.Injected(Injected(null)).Evidence);
        }

        // ── every keyed stage takes both readings, the second before the unkey ──

        [Theory]
        [InlineData("TxTuneProbeRunner.cs", "paTemperature = PaTemperatureSpan.Begin(\"the transmitter check\"",
                    "paTemperature?.End(", "Unkey(rig);")]
        [InlineData("FixerTransmitAudioBoundary.cs", "facts.PaTemperature = PaTemperatureSpan.Begin(\"the injected transmit check\"",
                    "facts.PaTemperature?.End(", "UnkeyMox(rig);")]
        [InlineData("FixerTransmitAudioBoundary.cs", "facts.PaTemperature = PaTemperatureSpan.Begin(\"the spoken transmit check\"",
                    "facts.PaTemperature?.End(", "UnkeyMox(rig);")]
        public void Every_keyed_stage_reads_at_key_down_and_again_before_the_carrier_drops(
            string file, string begin, string end, string unkey)
        {
            string src = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "ChainChecks", file));
            int b = src.IndexOf(begin, StringComparison.Ordinal);
            Assert.True(b >= 0, "no key-down reading: " + begin);
            int e = src.IndexOf(end, b, StringComparison.Ordinal);
            Assert.True(e > b, "no key-up reading after " + begin);
            int u = src.IndexOf(unkey, e, StringComparison.Ordinal);
            Assert.True(u > e && src.Substring(e, u - e).Split('\n').Length <= 3,
                "the key-up reading must come immediately before the unkey");
        }
    }
}
