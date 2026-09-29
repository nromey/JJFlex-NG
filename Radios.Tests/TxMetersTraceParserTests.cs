using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JJFlex.TxFactAudit;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// TxFactAudit's reader of the <c>txMeters:</c> line
    /// (<c>tools/TxFactAudit/TraceMeters.cs</c>), which until H18 had no
    /// committed test at all (#625).
    /// <para><b>Why it needs one.</b> The line changed shape on 2026-09-02 and
    /// this reader stopped matching it; for four weeks every trace read as a
    /// session with no transmission, and nothing said so (found by H17). It
    /// also read digits and periods only, so every line written on a machine
    /// whose culture uses a decimal comma was dropped the same silent way
    /// (Sol's H17 review, blocker 2). These tests hold every shape the line
    /// has had, a line from every culture .NET installs, the writer's own
    /// output, and what the report may conclude from finding nothing.</para>
    /// </summary>
    public sealed class TxMetersTraceParserTests
    {
        private readonly ITestOutputHelper _out;

        public TxMetersTraceParserTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private const string NoSample = "no-sample";

        /// <summary>Before 2026-09-02: no state, no copy label.</summary>
        private static string PreSeptember(long tick, string sc, string peak, string alc, string fwd) =>
            tick.ToString(CultureInfo.InvariantCulture) + " [T12] txMeters: SC_MIC=" + sc + " (peak " + peak + ")"
            + " SWALC=" + alc + " fwd=" + fwd + " dBm refl=20.0 dBm fwdW=50.12 reflW=0.100 back=0.2% SWRraw=1.09 SWRcalc=1.09";

        /// <summary>Sprint 44: state first (Track E) and the believed copy
        /// after the peak (Track B). A label with spaces and brackets, as the
        /// real one has.</summary>
        private static string Sprint44(long tick, string sc, string peak, string alc, string fwd) =>
            tick.ToString(CultureInfo.InvariantCulture) + " [T12:Meters] txMeters: state=tx SC_MIC=" + sc + " (peak " + peak + ")"
            + " via [24] TX-:0 SWALC=" + alc + " fwd=" + fwd + " dBm refl=20.0 dBm fwdW=50.12 reflW=0.100 back=0.2% SWRraw=1.09 SWRcalc=1.09";

        /// <summary>H17: a value that had not arrived says so.</summary>
        private static string H17NothingYet(long tick) =>
            tick.ToString(CultureInfo.InvariantCulture) + " [T12] txMeters: state=tune SC_MIC=no-sample (peak no-sample)"
            + " via no copy has reported SWALC=no-sample fwd=no-sample refl=no-sample fwdW=no-sample"
            + " reflW=no-sample back=no-sample SWRraw=no-sample SWRcalc=no-sample";

        private string WriteTrace(IEnumerable<string> lines)
        {
            string path = Path.Combine(Path.GetTempPath(), "jjflex-txmeters-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return path;
        }

        private TraceMeters.Reading ReadLines(IEnumerable<string> lines)
        {
            string path = WriteTrace(lines);
            try { return TraceMeters.Read(path); }
            finally { try { File.Delete(path); } catch { /* temp file */ } }
        }

        private static TraceSessionFile Session() => new TraceSessionFile
        {
            Path = @"C:\traces\JJFlexRadioTrace.txt",
            Instance = 1,
            AssemblyPath = @"C:\dev\jjflex\bin\x64\Debug\net10.0-windows\win-x64\jjflexible.dll",
            Version = "4.1.16.0",
            StartedAt = new DateTime(2026, 9, 29, 8, 0, 0),
            Level = "Info",
            IsLiveName = true,
        };

        private List<string> Describe(TraceMeters.Reading r)
        {
            var report = new List<string>();
            TraceMeters.Describe(Session(), r, report.Add);
            foreach (string l in report) _out.WriteLine(l);
            return report;
        }

        /// <summary>
        /// Every shape the line has had, in one trace, beside the lines that
        /// share its head and are not readings (census, election, its own
        /// introduction). Exactly the readings are read, with their values,
        /// and <c>no-sample</c> becomes "no reading".
        /// </summary>
        [Fact]
        public void Every_shape_the_line_has_had_is_read()
        {
            var lines = new[]
            {
                PreSeptember(1000, "-18.5", "-12.0", "-20.0", "47.0"),
                Sprint44(2000, "-17.5", "-11.0", "-19.0", "46.5"),
                H17NothingYet(3000),
                "3500 [T12] txMeters: state=tx SC_MIC=-16.0 (peak -10.5) via [24] TX-:0 SWALC=no-sample fwd=45.0 dBm refl=no-sample"
                    + " fwdW=31.62 reflW=no-sample back=no-sample SWRraw=no-sample SWRcalc=no-sample",
                "3600 [T12] txMeters: SC_MIC 3 copies ([17] TX-:8, [21] TX-:8, [43] TX-:9)",
                "3700 [T12] txMeters: SC_MIC elected [43] TX-:9 — the only copy the radio publishes. SC_MIC: 1 copy",
                "3800 [T12] About this file: lines that begin 'txMeters: state=' are JJ Flexible's transmit meter snapshots.",
            };
            TraceMeters.Reading r = ReadLines(lines);

            Assert.Equal(lines.Length, r.LinesRead);
            Assert.Equal(4, r.TxLines.Count);
            Assert.Equal(0, r.UnreadTxLines);

            Assert.Equal(new TxMeterLine(1000, -18.5, -12.0, -20.0, 47.0), r.TxLines[0]);
            Assert.Equal(new TxMeterLine(2000, -17.5, -11.0, -19.0, 46.5), r.TxLines[1]);
            Assert.Equal(new TxMeterLine(3000, null, null, null, null), r.TxLines[2]);
            Assert.Equal(new TxMeterLine(3500, -16.0, -10.5, null, 45.0), r.TxLines[3]);
        }

        /// <summary>
        /// A line written under ANY installed culture reads as the same numbers
        /// as the invariant one (Sol's blocker 2). Until H18 the writer used the
        /// machine's culture, so traces already in the field carry decimal
        /// commas, the real minus sign U+2212, direction marks and the Arabic
        /// decimal separator. One line per culture, both old shapes; every one
        /// must be read, and read right.
        /// <para>Positive control first: the set really contains those shapes,
        /// so a pass is not a walk over cultures that all write periods.</para>
        /// </summary>
        [Fact]
        public void A_trace_written_under_any_culture_is_read_not_dropped()
        {
            CultureInfo[] cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
            string Fmt(double v, string f, CultureInfo c) => ((float)v).ToString(f, c);

            Assert.Contains(cultures, c => Fmt(-18.5, "F1", c) == "-18,5");                       // de-DE and friends
            Assert.Contains(cultures, c => Fmt(-18.5, "F1", c).Contains('\u2212', StringComparison.Ordinal)); // fi, sv
            Assert.Contains(cultures, c => Fmt(-18.5, "F1", c).Contains('\u066B', StringComparison.Ordinal)); // ar
            Assert.Contains(cultures, c => Fmt(-18.5, "F1", c).Contains('\u200E', StringComparison.Ordinal)); // he, fa

            var lines = new List<string>();
            long tick = 1000;
            foreach (CultureInfo c in cultures)
            {
                lines.Add(PreSeptember(tick++, Fmt(-18.5, "F1", c), Fmt(-12.5, "F1", c), Fmt(-20.5, "F1", c), Fmt(47.5, "F1", c)));
                lines.Add(Sprint44(tick++, Fmt(-18.5, "F1", c), Fmt(-12.5, "F1", c), Fmt(-20.5, "F1", c), Fmt(47.5, "F1", c)));
            }
            TraceMeters.Reading r = ReadLines(lines);

            _out.WriteLine(cultures.Length + " cultures, " + lines.Count + " lines, " + r.TxLines.Count + " read.");
            Assert.Equal(0, r.UnreadTxLines);
            Assert.Equal(lines.Count, r.TxLines.Count);
            foreach (TxMeterLine l in r.TxLines)
            {
                Assert.Equal(-18.5, l.ScMicDb);
                Assert.Equal(-12.5, l.ScMicPeakDb);
                Assert.Equal(-20.5, l.SwAlcDb);
                Assert.Equal(47.5, l.ForwardDbm);
            }
        }

        /// <summary>
        /// The writer and the reader agree, whatever the machine's culture: the
        /// REAL writer, run under German and Finnish culture, writes numbers the
        /// invariant way, and the reader reads its lines back exactly. This is
        /// the contract <c>TraceMeters</c> names, held from both ends.
        /// </summary>
        [Theory]
        [InlineData("de-DE")]
        [InlineData("fi-FI")]
        [InlineData("ar-SA")]
        public void The_writers_own_lines_are_invariant_and_read_back(string cultureName)
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            var written = new List<string>();
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                // Premise: this culture really does write the number differently.
                Assert.NotEqual((-18.5f).ToString("F1", CultureInfo.InvariantCulture), (-18.5f).ToString("F1"));

                using var rig = new TxMetersEvidenceTests.Rig();
                rig.ForceTransmitField(true);
                rig.Call("forwardPowerData", 47.5f);
                rig.Call("reflectedPowerData", 20.5f);
                rig.Call("sWRData", 1.09f);
                written.Add(rig.WriteOne());
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }

            string line = written.Single();
            _out.WriteLine(line);
            Assert.Contains(" fwd=47.5 dBm", line, StringComparison.Ordinal);
            Assert.Contains(" refl=20.5 dBm", line, StringComparison.Ordinal);
            Assert.Contains(" SWRraw=1.09", line, StringComparison.Ordinal);
            Assert.DoesNotContain(",", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2212", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\u066B", line, StringComparison.Ordinal);

            TraceMeters.Reading r = ReadLines(written);
            TxMeterLine read = Assert.Single(r.TxLines);
            Assert.Equal(47.5, read.ForwardDbm);
            Assert.Null(read.ScMicDb);   // the radio sent no SC_MIC, and the line said so
        }

        /// <summary>
        /// Finding no <c>txMeters:</c> lines is reported as exactly that, and
        /// NOT as a finding that the radio did not transmit (Sol's blocker 2,
        /// and his follow-up: a trace can lack the line because no reading
        /// arrived to drive it, or because the recording did not cover a
        /// transmission). The old sentence called an empty result "a statement
        /// about whether the radio TRANSMITTED".
        /// </summary>
        [Fact]
        public void Finding_no_lines_is_reported_as_no_lines_found_and_nothing_more()
        {
            TraceMeters.Reading r = ReadLines(new[]
            {
                "100 [T1] Boot Tracing on instance 1",
                "200 [T12] txMeters: SC_MIC 1 copy ([24] TX-:0)",   // a census line: not a reading
            });
            Assert.False(r.AnyTransmission);
            Assert.Equal(0, r.UnreadTxLines);

            // Joined with spaces: the report wraps its sentences across lines.
            string report = string.Join(" ", Describe(r));
            Assert.Contains("No txMeters lines were found in this trace.", report, StringComparison.Ordinal);
            Assert.Contains("not evidence about whether the radio transmitted", report, StringComparison.Ordinal);
            Assert.Contains("because no reading arrived", report, StringComparison.Ordinal);
            Assert.DoesNotContain("No transmit meter lines at all", report, StringComparison.Ordinal);
            Assert.DoesNotContain("statement about whether the radio", report, StringComparison.Ordinal);
            Assert.DoesNotContain("TRANSMITTED", report, StringComparison.Ordinal);
            Assert.DoesNotContain("could not read", report, StringComparison.Ordinal);
        }

        /// <summary>
        /// A line that is plainly meant as a reading but that the reader
        /// cannot read is COUNTED and shown first, so the next change of
        /// format is reported instead of read as silence, which is what the
        /// 2026-09-02 change did for four weeks.
        /// </summary>
        [Fact]
        public void A_reading_the_parser_cannot_read_is_reported_not_read_as_silence()
        {
            const string Future = "5000 [T12] txMeters: state=tx mic=-18.0 alc=-20.0 fwd=47.0 dBm";
            TraceMeters.Reading r = ReadLines(new[] { Future, Sprint44(6000, "-17.5", "-11.0", "-19.0", "46.5") });
            Assert.Equal(1, r.UnreadTxLines);
            Assert.Equal(Future, r.FirstUnreadTxLine);
            Assert.Single(r.TxLines);

            List<string> report = Describe(r);
            int warn = report.FindIndex(l => l.Contains("could not read", StringComparison.Ordinal));
            int figures = report.FindIndex(l => l.Contains("transmit meter snapshots", StringComparison.Ordinal));
            Assert.True(warn >= 0, "the unread line is not reported");
            Assert.True(figures > warn, "the warning must come before the figures");
            Assert.Contains(report, l => l.Contains(Future, StringComparison.Ordinal));
        }

        /// <summary>A session longer than 24.8 days has a tick past
        /// <c>int.MaxValue</c>; reading it must not throw.</summary>
        [Fact]
        public void A_tick_past_the_32_bit_range_is_read()
        {
            long tick = (long)int.MaxValue + 12345;
            TraceMeters.Reading r = ReadLines(new[] { Sprint44(tick, "-17.5", "-11.0", "-19.0", "46.5") });
            Assert.Equal(tick, Assert.Single(r.TxLines).Tick);
        }
    }
}
