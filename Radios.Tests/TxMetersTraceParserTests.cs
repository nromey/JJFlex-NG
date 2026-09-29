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
            StartedAt = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Local),
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

            // The state is kept (H19): the pre-September line has none and is
            // Unknown, never guessed to be a transmission.
            Assert.Equal(new TxMeterLine(1000, TxMeterState.Unknown, -18.5, -12.0, -20.0, 47.0), r.TxLines[0]);
            Assert.Equal(new TxMeterLine(2000, TxMeterState.Transmit, -17.5, -11.0, -19.0, 46.5), r.TxLines[1]);
            Assert.Equal(new TxMeterLine(3000, TxMeterState.Tune, null, null, null, null), r.TxLines[2]);
            Assert.Equal(new TxMeterLine(3500, TxMeterState.Transmit, -16.0, -10.5, null, 45.0), r.TxLines[3]);
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
            Assert.False(r.AnyLines);
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
            int figures = report.FindIndex(l => l.Contains("transmit meter snapshot", StringComparison.Ordinal));
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

        // ---- H19 (#625): tune and transmit are kept apart in the REPORT ----
        //
        // Sol's review of H18 found that H17 taught the parser to accept
        // state= and then threw it away, so a tune-only trace was reported as
        // transmit meter snapshots and a tune's carrier power was pooled with a
        // voice transmission's. The tests below assert what the report SAYS,
        // section by section, not only what was parsed.

        /// <summary>A current-format line in any state, every field a number.</summary>
        private static string InState(long tick, string state, string sc, string peak, string alc, string fwd) =>
            tick.ToString(CultureInfo.InvariantCulture) + " [T12:Meters] txMeters: state=" + state
            + " SC_MIC=" + sc + " (peak " + peak + ") via [24] TX-:0 SWALC=" + alc + " fwd=" + fwd
            + " dBm refl=20.0 dBm fwdW=1.00 reflW=0.100 back=0.2% SWRraw=1.09 SWRcalc=1.09";

        /// <summary>Where each section of the per-state report begins. The
        /// per-meter section closes the last of them.</summary>
        private static readonly string[] SectionHeads =
        {
            "WHILE TRANSMITTING (state=tx)",
            "WHILE TUNING (state=tune)",
            "WHILE TUNING WITH TRANSMIT KEYED (state=tune+tx)",
            "WITH NO STATE ON THE LINE",
            "Per-meter lines",
        };

        /// <summary>The report's lines from one section heading up to the next.</summary>
        private static List<string> Section(List<string> report, string head)
        {
            int start = report.FindIndex(l => l.StartsWith(head, StringComparison.Ordinal));
            Assert.True(start >= 0, "the report has no section headed '" + head + "'");
            int end = report.FindIndex(start + 1, l => SectionHeads.Any(h => l.StartsWith(h, StringComparison.Ordinal)));
            return report.GetRange(start, (end < 0 ? report.Count : end) - start);
        }

        /// <summary>
        /// A trace with only tune lines is reported as a tune, and nothing in
        /// the report calls it a transmission: not the heading, not the count,
        /// and not the sub-headings for unreported meters and the idle
        /// sentinel, which used to say "WHILE TRANSMITTING" whatever the state.
        /// </summary>
        [Fact]
        public void A_tune_only_trace_is_reported_as_a_tune_and_never_as_a_transmission()
        {
            TraceMeters.Reading r = ReadLines(new[]
            {
                H17NothingYet(1000),
                InState(1250, "tune", "-150.0", "-70.0", "-90.0", "50.0"),
                InState(1500, "tune", "-80.0", "-70.0", "-90.0", "50.0"),
                InState(1750, "tune", "-80.0", "-70.0", "-90.0", "50.0"),
            });
            Assert.All(r.TxLines, l => Assert.Equal(TxMeterState.Tune, l.State));

            List<string> report = Describe(r);
            string all = string.Join(" ", report);
            Assert.Contains("4 txMeters lines read: 4 while tuning.", report);
            Assert.Contains("WHILE TUNING (state=tune): 4 tune meter snapshots, the first and last 0.8 seconds apart.", report);
            Assert.Contains("LINES WRITTEN BEFORE A METER HAD REPORTED, WHILE TUNING:", report);
            Assert.Contains("SAMPLES SITTING ON THE IDLE SENTINEL, WHILE TUNING:", report);
            Assert.Contains("  lowest 50 dBm, highest 50 dBm, last 50 dBm (from the 3 of 4 lines that carried one).", report);

            Assert.DoesNotContain("transmission", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transmit meter snapshot", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WHILE TRANSMITTING", all, StringComparison.Ordinal);
            Assert.DoesNotContain("WITH NO STATE", all, StringComparison.Ordinal);
            Assert.DoesNotContain("each kind is reported", all, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A transmit-only trace is reported as transmit, with no tune
        /// or no-state section beside it.</summary>
        [Fact]
        public void A_transmit_only_trace_is_reported_as_transmit_alone()
        {
            TraceMeters.Reading r = ReadLines(new[]
            {
                InState(1000, "tx", "-18.0", "-12.0", "-20.0", "40.0"),
                InState(2000, "tx", "-17.0", "-11.0", "-19.0", "40.0"),
            });
            Assert.All(r.TxLines, l => Assert.Equal(TxMeterState.Transmit, l.State));

            List<string> report = Describe(r);
            string all = string.Join(" ", report);
            Assert.Contains("2 txMeters lines read: 2 while transmitting.", report);
            List<string> tx = Section(report, "WHILE TRANSMITTING (state=tx)");
            Assert.Equal("WHILE TRANSMITTING (state=tx): 2 transmit meter snapshots, the first and last 1 seconds apart.", tx[0]);
            Assert.Contains("  lowest 40 dBm, highest 40 dBm, last 40 dBm.", tx);
            Assert.Contains("  highest peak the app held: -11 dBFS.", tx);

            Assert.DoesNotContain("WHILE TUNING", all, StringComparison.Ordinal);
            Assert.DoesNotContain("tune meter snapshot", all, StringComparison.Ordinal);
            Assert.DoesNotContain("WITH NO STATE", all, StringComparison.Ordinal);
        }

        /// <summary>
        /// The blocker itself. A trace with tune lines at 100 W, transmit lines
        /// at 10 W and a keyed tune at 31.6 W, interleaved: each state gets its
        /// own section with its own figures, and the tune's power and peak
        /// appear NOWHERE in the transmit section. Before H19 all five lines
        /// were pooled as "transmit meter snapshots", highest 50 dBm.
        /// <para>This is H19's mutation control: make the parser drop the
        /// state again and the transmit section disappears, so this goes
        /// red.</para>
        /// </summary>
        [Fact]
        public void A_mixed_trace_never_reports_the_tunes_power_as_the_transmissions()
        {
            TraceMeters.Reading r = ReadLines(new[]
            {
                InState(1000, "tune", "-80.0", "-70.0", "-90.0", "50.0"),
                InState(2000, "tx", "-18.0", "-12.0", "-20.0", "40.0"),
                InState(3000, "tx", "-17.0", "-12.0", "-19.0", "40.0"),
                InState(4000, "tune", "-80.0", "-70.0", "-90.0", "50.0"),
                InState(5000, "tune+tx", "-60.0", "-55.0", "-65.0", "45.0"),
            });

            List<string> report = Describe(r);
            Assert.Contains("5 txMeters lines read: 2 while transmitting, 2 while tuning and 1 while tuning with transmit keyed.", report);
            Assert.Contains("Each kind is reported on its own below, and no figure combines two of them.", report);

            List<string> tx = Section(report, "WHILE TRANSMITTING (state=tx)");
            Assert.Equal("WHILE TRANSMITTING (state=tx): 2 transmit meter snapshots, the first and last 1 seconds apart.", tx[0]);
            Assert.Contains("  lowest 40 dBm, highest 40 dBm, last 40 dBm.", tx);
            Assert.Contains("  in watts: lowest 10, highest 10.", tx);
            Assert.Contains("  highest peak the app held: -12 dBFS.", tx);
            // Nothing from the tune or the keyed tune, in any form.
            Assert.DoesNotContain(tx, l => l.Contains("50", StringComparison.Ordinal));
            Assert.DoesNotContain(tx, l => l.Contains("100", StringComparison.Ordinal));
            Assert.DoesNotContain(tx, l => l.Contains("45", StringComparison.Ordinal));
            Assert.DoesNotContain(tx, l => l.Contains("31.6", StringComparison.Ordinal));
            Assert.DoesNotContain(tx, l => l.Contains("-70", StringComparison.Ordinal));
            Assert.DoesNotContain(tx, l => l.Contains("-55", StringComparison.Ordinal));

            List<string> tune = Section(report, "WHILE TUNING (state=tune)");
            Assert.Equal("WHILE TUNING (state=tune): 2 tune meter snapshots, the first and last 3 seconds apart.", tune[0]);
            Assert.Contains("  lowest 50 dBm, highest 50 dBm, last 50 dBm.", tune);
            Assert.Contains("  in watts: lowest 100, highest 100.", tune);
            Assert.DoesNotContain(tune, l => l.Contains("40 dBm", StringComparison.Ordinal));
            Assert.DoesNotContain(tune, l => l.Contains("transmission", StringComparison.OrdinalIgnoreCase));

            List<string> keyed = Section(report, "WHILE TUNING WITH TRANSMIT KEYED (state=tune+tx)");
            Assert.Equal("WHILE TUNING WITH TRANSMIT KEYED (state=tune+tx): 1 meter snapshot.", keyed[0]);
            Assert.Contains("  lowest 45 dBm, highest 45 dBm, last 45 dBm.", keyed);
            Assert.Contains("  highest peak the app held: -55 dBFS.", keyed);

            Assert.DoesNotContain(report, l => l.StartsWith("WITH NO STATE", StringComparison.Ordinal));

            // Parsed as the lines said, in trace order.
            Assert.Equal(new[] { TxMeterState.Tune, TxMeterState.Transmit, TxMeterState.Transmit,
                                 TxMeterState.Tune, TxMeterState.TuneWhileKeyed },
                         r.TxLines.Select(l => l.State).ToArray());
        }

        /// <summary>
        /// Lines from before 2026-09-02 carry no state. They are reported as
        /// having none, and are NEVER guessed to be a transmission.
        /// </summary>
        [Fact]
        public void An_old_trace_without_state_is_reported_as_unknown_not_as_transmit()
        {
            TraceMeters.Reading r = ReadLines(new[]
            {
                PreSeptember(1000, "-18.5", "-12.0", "-20.0", "47.0"),
                PreSeptember(2000, "-17.5", "-11.0", "-19.0", "46.0"),
            });
            Assert.All(r.TxLines, l => Assert.Equal(TxMeterState.Unknown, l.State));

            List<string> report = Describe(r);
            string all = string.Join(" ", report);
            Assert.Contains("2 txMeters lines read: 2 with no state.", report);
            List<string> unknown = Section(report, "WITH NO STATE ON THE LINE");
            Assert.Equal("WITH NO STATE ON THE LINE: 2 meter snapshots, the first and last 1 seconds apart.", unknown[0]);
            Assert.Contains(unknown, l => l.Contains("this tool does not say which they were", StringComparison.Ordinal));
            Assert.Contains("  lowest 46 dBm, highest 47 dBm, last 46 dBm.", unknown);

            Assert.DoesNotContain("WHILE TRANSMITTING", all, StringComparison.Ordinal);
            Assert.DoesNotContain("WHILE TUNING", all, StringComparison.Ordinal);
            Assert.DoesNotContain("transmit meter snapshot", all, StringComparison.Ordinal);
        }

        /// <summary>
        /// Sol's H18 follow-up: when every line that looked like a reading
        /// could not be read, the report says the lines could not be READ, and
        /// does not go on to say none were FOUND, which contradicts the warning
        /// it has just given.
        /// </summary>
        [Fact]
        public void A_trace_whose_every_reading_is_unreadable_says_so_and_not_that_none_were_found()
        {
            TraceMeters.Reading r = ReadLines(new[]
            {
                "5000 [T12] txMeters: state=tx mic=-18.0 alc=-20.0 fwd=47.0 dBm",
                "6000 [T12] txMeters: state=tx mic=-17.0 alc=-19.0 fwd=46.0 dBm",
            });
            Assert.False(r.AnyLines);
            Assert.Equal(2, r.UnreadTxLines);

            string all = string.Join(" ", Describe(r));
            Assert.Contains("2 lines looked like txMeters readings, but this tool could not read them.", all, StringComparison.Ordinal);
            Assert.Contains("None of those lines could be read", all, StringComparison.Ordinal);
            Assert.Contains("do not read this as a trace without them", all, StringComparison.Ordinal);
            Assert.DoesNotContain("were found", all, StringComparison.Ordinal);
            Assert.DoesNotContain("not evidence about whether the radio transmitted", all, StringComparison.Ordinal);
        }

        /// <summary>
        /// A state this tool does not know is counted as a line it could not
        /// read, and is not filed under a state it is not. The writer writes
        /// three; a fourth means the format has moved on.
        /// </summary>
        [Fact]
        public void A_state_the_tool_does_not_know_is_reported_as_unread_not_filed_under_another()
        {
            string idle = InState(1000, "idle", "-18.0", "-12.0", "-20.0", "40.0");
            TraceMeters.Reading r = ReadLines(new[] { idle });
            Assert.Empty(r.TxLines);
            Assert.Equal(1, r.UnreadTxLines);
            Assert.Equal(idle, r.FirstUnreadTxLine);
        }
    }
}
