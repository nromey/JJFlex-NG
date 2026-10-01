using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JJFlex.TxFactAudit
{
    /// <summary>
    /// What the radio was doing when a <c>txMeters:</c> line was written, as
    /// the line itself says in its <c>state=</c> field.
    /// <para><b>Why the reader keeps it (#625, H19).</b> The writer puts the
    /// state on the line precisely so a tune's carrier cannot be read as a
    /// transmission's power, and H17 taught this reader to accept the field
    /// and then threw it away: every line became "transmit", and a tune's
    /// forward power was pooled with a voice transmission's. Found by Sol's
    /// review of H18.</para>
    /// </summary>
    public enum TxMeterState
    {
        /// <summary>The line carries no <c>state=</c> field: it was written
        /// before 2026-09-02, when the field did not exist. Never guessed to
        /// be a transmission — the reader does not know, and says so.</summary>
        Unknown,

        /// <summary><c>state=tx</c>: transmitting, not tuning.</summary>
        Transmit,

        /// <summary><c>state=tune</c>: tuning, with the transmitter not keyed.</summary>
        Tune,

        /// <summary><c>state=tune+tx</c>: a tune during which the transmitter
        /// was also keyed, such as an ATU sweep that raised Mox.</summary>
        TuneWhileKeyed,
    }

    /// <summary>One correlated transmit-meter snapshot, as the app traced it.
    /// A null value is one the line said had not arrived: since H17 (#625)
    /// the writer prints <c>no-sample</c> instead of a placeholder number, and
    /// this reader keeps that as "no reading" rather than inventing one.
    /// <see cref="State"/> is what the line said the radio was doing; see
    /// <see cref="TxMeterState"/>.</summary>
    public sealed record TxMeterLine(long Tick, TxMeterState State, double? ScMicDb, double? ScMicPeakDb,
                                     double? SwAlcDb, double? ForwardDbm)
    {
        public double? ForwardWatts =>
            ForwardDbm is double dbm ? Math.Pow(10.0, dbm / 10.0) / 1000.0 : null;

        /// <summary>True when a value is sitting exactly on the initialiser the
        /// app uses for "nothing has reported". Worth naming, because it is the
        /// value that used to reach an operator dressed as a measurement. Only
        /// traces from before H17 can carry it: the writer now prints
        /// <c>no-sample</c> instead, which <see cref="ScMicNoSample"/> counts.</summary>
        public bool ScMicAtSentinel => ScMicDb <= -149.5;

        public bool SwAlcAtSentinel => SwAlcDb <= -149.5;

        /// <summary>The line said SC_MIC had not reported (H17 and later).</summary>
        public bool ScMicNoSample => ScMicDb is null;

        /// <summary>The line said SW ALC had not reported (H17 and later).</summary>
        public bool SwAlcNoSample => SwAlcDb is null;
    }

    /// <summary>
    /// Reads the radio's meter readings out of the APPLICATION'S OWN TRACE.
    ///
    /// <para><b>Why this rather than a UDP stream of our own.</b> The question
    /// is whether JJ Flexible's facts are honest, and a second client with its
    /// own subscription is not the same experiment: the meter list is a
    /// property of the moment, not of the model — eleven meters with no station
    /// client, thirty-five with one — so a stream the application never saw
    /// cannot testify about what the application knew. This reads what actually
    /// reached it.</para>
    ///
    /// <para><b>The trace rate is not the measurement rate.</b>
    /// <c>FlexBase.traceTxMeters</c> throttles to one line a second (four while
    /// tuning, since Sprint 44 Track E), but each
    /// line carries <c>peak</c>, the highest SC_MIC reading so far in the
    /// current transmission or tune, tracked by the handler that sees every
    /// reading (the writer's own since H18; before that, the election's peak
    /// since the last push-to-talk key-down). So a once-a-second line
    /// is not a once-a-second measurement: transients inside the second are
    /// already in the peak. What is lost is the shape of the second, not its
    /// height, and height is what anything peak-sensitive wants.</para>
    ///
    /// <para><b>Two limits, designed around rather than papered over.</b>
    /// <c>traceTxMeters</c> returns unless the radio is transmitting or tuning,
    /// so there are NO lines while receiving. An absence of lines is only
    /// that: no line was found. It is not evidence that the radio did not
    /// transmit (no reading may have arrived to drive one, or the recording
    /// may not cover a transmission), and reporting it as "the meters are
    /// unreadable" would be its own fabricated fact. And the per-meter lines exist only when the
    /// operator has turned on "Record the meter stream" on Settings →
    /// Diagnostics (task #170) — no trace level brings them back, because
    /// their raw form was measured at half of a 52 MB capture and made
    /// opt-in. When the switch is on they arrive COALESCED, one line per
    /// meter per second: <c>micData: min=-120 max=-118.4 last=-119 n=34</c>.
    /// Traces from before 2026-08-21 carry the raw per-packet form
    /// (<c>micData:-120</c>) at Verbose; this reader parses both.</para>
    /// </summary>
    public static class TraceMeters
    {
        /// <summary>What the app writes in a <c>txMeters:</c> field in place
        /// of a number the radio has not reported. A contract with
        /// <c>Radios/CaptureMeterSet.NoSample</c>: this tool does not reference
        /// the Radios assembly, so the word is repeated here, and a change on
        /// either side must be made on both.</summary>
        public const string NoSample = "no-sample";

        /// <summary>
        /// The correlated <c>txMeters:</c> line, in every shape it has had.
        /// <para><b>It did not match for four weeks, and nothing said so.</b>
        /// This pattern expected <c>txMeters: SC_MIC=</c> right after the
        /// head and <c>SWALC=</c> right after the peak. Sprint 44 Track E put
        /// <c>state=</c> first and Track B put <c>via &lt;copy&gt;</c> after the
        /// peak, both on 2026-09-02, and from then on every trace read as "no
        /// transmit meter lines at all" — which this tool reports as a
        /// statement that the radio did not TRANSMIT. Found by H17 while
        /// listing the parsers of the line before changing it (#625). Both
        /// additions are optional here, so traces from before and after parse,
        /// and each value may be <see cref="NoSample"/>.</para>
        /// <para><b>The state is captured, and only the three the writer
        /// writes are accepted</b> (H19). A <c>state=</c> this tool does not
        /// know fails this pattern and is counted by
        /// <see cref="LooksLikeTxMeterReading"/> as a line it could not read,
        /// so a new state is reported rather than filed under one it is not.
        /// <c>tune+tx</c> is tried before <c>tune</c>, which is a prefix of it.</para>
        /// </summary>
        private static readonly Regex TxMeters = new(
            @"^(?<tick>\d+)\s+\[[^\]]*\]\s+txMeters:\s+(?:state=(?<state>tune\+tx|tune|tx)\s+)?"
            + @"SC_MIC=(?<sc>" + Number + @"|no-sample)\s+\(peak\s+(?<peak>" + Number + @"|no-sample)\)"
            + @"(?:\s+via\s+.*?)?\s+SWALC=(?<alc>" + Number + @"|no-sample)"
            + @"\s+fwd=(?:(?<fwd>" + Number + @")\s+dBm|no-sample)",
            RegexOptions.Compiled);

        /// <summary>
        /// A number as ANY culture's .NET formatting writes it, because traces
        /// already in the field were written that way (#625, H18, Sol's
        /// blocker 2). Until H18 the app formatted <c>txMeters:</c> numbers in
        /// the machine's own culture, so a German machine wrote <c>-18,0</c>,
        /// a Finnish or Swedish one <c>U+2212 18,0</c> (a real minus sign),
        /// and an Arabic one a direction mark, a minus and the Arabic decimal
        /// separator. This reader took digits and periods only, dropped every
        /// such line without a word, and then reported a session with no
        /// transmission. The writer is invariant now; the reader stays
        /// tolerant for the traces that already exist. <see cref="Num"/>
        /// normalises what this matches. The set is every shape the installed
        /// cultures produce, and a test walks all of them.
        /// </summary>
        private const string Number =
            @"[\u061C\u200E\u200F]*[-\u2212]?[\u061C\u200E\u200F]*\d+(?:[.,\u066B]\d+)?";

        /// <summary>A line the writer meant as a <c>txMeters:</c> reading: the
        /// head straight after the thread tag, then <c>state=</c> or
        /// <c>SC_MIC=</c>. Not a census or an election line, which carry no
        /// <c>=</c> after the meter name, and not the line's own introduction,
        /// which quotes the head mid-sentence. Used only to COUNT readings the
        /// pattern above could not read, so a format change is reported rather
        /// than read as silence.</summary>
        private static readonly Regex LooksLikeTxMeterReading = new(
            @"\]\s+txMeters:\s+(?:state=|SC_MIC=)", RegexOptions.Compiled);

        /// <summary>The per-meter lines that only exist while meter-stream
        /// recording is on (raw at Verbose in pre-2026-08-21 traces). Named
        /// here so a run that has them can say so and a run that does not can
        /// say which switch it would need.</summary>
        private static readonly (string Key, string Meter)[] VerboseMeters =
        {
            ("micData:", "MIC — the analog codec path"),
            ("micPeakData:", "MICPEAK — also the codec path"),
            ("compPeakData:", "COMPPEAK"),
            ("SWRData:", "SWR"),
            ("forwardPower:", "FWDPWR on its own"),
            ("hwALCData:", "HWALC — the amplifier jack"),
        };

        private static readonly Regex VerboseValue = new(
            @"\]\s+(?<key>micData|micPeakData|compPeakData|SWRData|forwardPower|hwALCData):\s*(?<v>" + Number + ")",
            RegexOptions.Compiled);

        /// <summary>The coalesced form MeterTraceStream writes since 2026-08-21:
        /// one line per meter per second, min/max/last plus the sample count.
        /// The format is a contract with Radios/MeterTraceStream.cs — change
        /// either side only in step with the other.</summary>
        private static readonly Regex CoalescedValue = new(
            @"\]\s+(?<key>micData|micPeakData|compPeakData|SWRData|forwardPower|hwALCData):\s+min=(?<min>" + Number
            + @")\s+max=(?<max>" + Number + @")\s+last=(?<last>" + Number + @")\s+n=(?<n>\d+)",
            RegexOptions.Compiled);

        /// <summary>What one trace had to say about the transmit meters.</summary>
        public sealed class Reading
        {
            public long LinesRead { get; internal set; }
            public List<TxMeterLine> TxLines { get; } = new();
            public Dictionary<string, List<double>> VerboseSamples { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Underlying readings per meter, which is NOT the sample
            /// list's count once coalesced lines are involved: one coalesced
            /// line contributes min, max and last to the sample list but
            /// carries <c>n</c> raw readings. This holds the honest total.</summary>
            public Dictionary<string, long> ReadingCounts { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>True when any <c>txMeters:</c> line was read, in any
            /// state. Deliberately NOT called "any transmission", which is what
            /// it was called until H19: a tune-only trace made that true.</summary>
            public bool AnyLines => TxLines.Count > 0;

            /// <summary>The lines read in one state, in trace order.</summary>
            public TxMeterLine[] LinesIn(TxMeterState state) =>
                TxLines.Where(l => l.State == state).ToArray();

            /// <summary>Lines that looked like <c>txMeters:</c> readings and
            /// that this reader could not read. Non-zero means the format has
            /// moved on from this tool, which is exactly how it spent four
            /// weeks reporting no transmissions (H17).</summary>
            public long UnreadTxLines { get; internal set; }

            /// <summary>The first such line, so the report can show it.</summary>
            public string? FirstUnreadTxLine { get; internal set; }

            internal void AddSample(string key, double value, long readings)
            {
                if (!VerboseSamples.TryGetValue(key, out List<double>? samples))
                {
                    samples = new List<double>();
                    VerboseSamples[key] = samples;
                }
                samples.Add(value);
                ReadingCounts.TryGetValue(key, out long total);
                // Min/max/last from one coalesced line are three views of the
                // same n readings — count them once, on the call that says so.
                if (readings > 0) ReadingCounts[key] = total + readings;
            }
        }

        /// <summary>
        /// Reads a trace, sharing the file so the live log can be read while the
        /// app still holds it open for writing.
        /// </summary>
        public static Reading Read(string path)
        {
            var result = new Reading();

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                result.LinesRead++;

                Match m = TxMeters.Match(line);
                if (m.Success)
                {
                    result.TxLines.Add(new TxMeterLine(
                        long.Parse(m.Groups["tick"].Value, CultureInfo.InvariantCulture),
                        StateOf(m.Groups["state"]),
                        Value(m.Groups["sc"]), Value(m.Groups["peak"]),
                        Value(m.Groups["alc"]), Value(m.Groups["fwd"])));
                    continue;
                }
                if (LooksLikeTxMeterReading.IsMatch(line))
                {
                    result.UnreadTxLines++;
                    result.FirstUnreadTxLine ??= line;
                    continue;
                }

                // Coalesced first (the format the app writes now); the raw
                // per-packet form still parses because archived traces are
                // full of it and this tool reads archives.
                Match c = CoalescedValue.Match(line);
                if (c.Success)
                {
                    string key = c.Groups["key"].Value;
                    long n = long.TryParse(c.Groups["n"].Value, out long parsed) ? parsed : 1;
                    result.AddSample(key, Num(c.Groups["min"].Value), n);
                    result.AddSample(key, Num(c.Groups["max"].Value), 0);
                    result.AddSample(key, Num(c.Groups["last"].Value), 0);
                    continue;
                }

                Match v = VerboseValue.Match(line);
                if (v.Success)
                {
                    result.AddSample(v.Groups["key"].Value, Num(v.Groups["v"].Value), 1);
                }
            }

            return result;
        }

        /// <summary>A number matched by <see cref="Number"/>, whatever culture
        /// wrote it: direction marks dropped, a real minus sign made a hyphen,
        /// and a comma or the Arabic decimal separator made a period. Safe
        /// because every number on these lines is written without digit
        /// grouping, so a comma can only be a decimal separator.</summary>
        private static double Num(string s)
        {
            string plain = s.Replace("\u061C", "", StringComparison.Ordinal)
                .Replace("\u200E", "", StringComparison.Ordinal)
                .Replace("\u200F", "", StringComparison.Ordinal)
                .Replace('\u2212', '-')
                .Replace(',', '.')
                .Replace('\u066B', '.');
            return double.Parse(plain, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>A <c>txMeters:</c> field: its number, or null when the line
        /// said <see cref="NoSample"/> (or, for fwd, carried no number).</summary>
        private static double? Value(Group g) =>
            !g.Success || g.Value == NoSample ? null : Num(g.Value);

        /// <summary>A line's <c>state=</c> field. No field means a line from
        /// before the field existed, which is <see cref="TxMeterState.Unknown"/>,
        /// never a guess. The pattern admits only the three values below.</summary>
        private static TxMeterState StateOf(Group g) => !g.Success ? TxMeterState.Unknown : g.Value switch
        {
            "tx" => TxMeterState.Transmit,
            "tune" => TxMeterState.Tune,
            "tune+tx" => TxMeterState.TuneWhileKeyed,
            _ => throw new InvalidOperationException("The txMeters pattern admitted a state it has no name for: " + g.Value),
        };

        /// <summary>
        /// The report. Prose and bullets, and it never prints a number for
        /// something that did not arrive.
        /// </summary>
        public static void Describe(TraceSessionFile session, Reading r, Action<string> write)
        {
            write("Reading " + session.Describe() + ".");
            write("");
            write($"{r.LinesRead} lines of trace.");
            write("");

            if (r.UnreadTxLines > 0)
            {
                // First, before any figure: a reader that cannot read the line
                // must not let the reader of this report think the line was
                // absent. H17 found four weeks of exactly that.
                write($"{r.UnreadTxLines} lines looked like txMeters readings, but this tool could not read them.");
                write("The line's format has probably changed since the tool was last updated. Fix the");
                write("tool before drawing any conclusion from this trace. The first of those lines was:");
                write("  " + r.FirstUnreadTxLine);
                write("");
            }

            if (!r.AnyLines && r.UnreadTxLines > 0)
            {
                // Every line that looked like a reading failed to parse (H19,
                // Sol's H18 follow-up). The lines are there, so saying none
                // were FOUND would be the silent format failure again, in the
                // report's own words.
                write("None of those lines could be read, so this report has no txMeters figures from this");
                write("trace. The lines are there, so do not read this as a trace without them. Fix the tool");
                write("and read the trace again.");
                write("");
                write("The receive-side facts are settings and telemetry the radio holds continuously.");
                write("Read those with 'TxFactAudit audit', which asks the radio rather than the trace.");
            }
            else if (!r.AnyLines)
            {
                // Only what the tool can establish (H18, Sol's blocker 2). An
                // empty result used to be reported as a statement that the
                // radio did not transmit, which a trace cannot show.
                write("No txMeters lines were found in this trace. That is all this tool can establish from");
                write("it, and it is not evidence about whether the radio transmitted. The app writes a");
                write("txMeters line only while the radio is transmitting or tuning and a meter reading arrives");
                write("to drive it, so a trace can lack them because the radio did not transmit, because no");
                write("reading arrived, or because the recording did not cover a transmission. Nor is it");
                write("evidence that the transmit meters cannot be read, so do not record the transmit meter");
                write("facts as unreadable on this evidence.");
                write("");
                write("The receive-side facts are settings and telemetry the radio holds continuously.");
                write("Read those with 'TxFactAudit audit', which asks the radio rather than the trace.");
            }
            else
            {
                DescribeByState(r, write);
            }

            write("Per-meter lines, which exist only while meter-stream recording is on:");
            if (r.VerboseSamples.Count == 0)
            {
                write("  none. SWR, the codec MIC meter and HWALC reach the trace only when the operator");
                write("  has turned on 'Record the meter stream' on Settings > Diagnostics — since task");
                write("  #170 no trace level brings them back, and a Detailed capture alone is NOT enough");
                write("  any more. That is a gap in the RECORDING, not a finding about those meters —");
                write("  turn the switch on before the bench run to get them. (Traces from before");
                write($"  2026-08-21 carried them at Verbose instead; this session booted at level {session.Level}.)");
            }
            else
            {
                foreach ((string key, string meter) in VerboseMeters)
                {
                    string name = key.TrimEnd(':');
                    if (r.VerboseSamples.TryGetValue(name, out List<double>? samples) && samples.Count > 0)
                    {
                        // The reading count and the sample-list length diverge
                        // on coalesced traces: each line is three samples but n
                        // readings. Report readings — that is what happened at
                        // the radio.
                        long readings = r.ReadingCounts.TryGetValue(name, out long n) ? n : samples.Count;
                        write($"  {meter}: {readings} readings, "
                              + $"lowest {samples.Min():0.##}, highest {samples.Max():0.##}, "
                              + $"last {samples[^1]:0.##}.");
                    }
                    else
                    {
                        write($"  {meter}: no readings in this trace.");
                    }
                }
            }
        }

        /// <summary>How the report names one state: its section heading, what
        /// one of its lines is called, what the lines mean, and the qualifier
        /// its sub-headings carry. Every word a section says about transmit or
        /// tune comes from here, so a section cannot describe a state it does
        /// not cover (H19).</summary>
        private sealed record StateWords(
            TxMeterState State, string Heading, string Snapshot, string Tally,
            string[] Meaning, string ScMicLabel, string Qualifier);

        /// <summary>The sections, in the order the report gives them.</summary>
        private static readonly StateWords[] StateSections =
        {
            new(TxMeterState.Transmit,
                "WHILE TRANSMITTING (state=tx)", "transmit meter snapshot", "while transmitting",
                new[]
                {
                    "  At most one line a second while transmitting. Each carries the highest SC_MIC reading so",
                    "  far in its transmission, so the transients between lines are already in the peak.",
                },
                "SC_MIC — what the radio heard on transmit, from any source:",
                "WHILE TRANSMITTING"),
            new(TxMeterState.Tune,
                "WHILE TUNING (state=tune)", "tune meter snapshot", "while tuning",
                new[]
                {
                    "  These readings were taken during a tune, not during keyed transmit. Nobody talks during",
                    "  a tune, so SC_MIC and SW ALC here say nothing about the microphone. At most four lines a",
                    "  second while tuning, and each carries the highest SC_MIC reading so far in its tune.",
                },
                "SC_MIC — the transmit audio level during the tune, from any source:",
                "WHILE TUNING"),
            new(TxMeterState.TuneWhileKeyed,
                "WHILE TUNING WITH TRANSMIT KEYED (state=tune+tx)", "meter snapshot", "while tuning with transmit keyed",
                new[]
                {
                    "  A tune during which transmit was also keyed, such as an ATU sweep. These are kept apart",
                    "  from plain tuning and plain transmit, because they are neither. At most four lines a",
                    "  second, and each carries the highest SC_MIC reading so far in its tune.",
                },
                "SC_MIC — the transmit audio level during the tune, from any source:",
                "WHILE TUNING WITH TRANSMIT KEYED"),
            new(TxMeterState.Unknown,
                "WITH NO STATE ON THE LINE", "meter snapshot", "with no state",
                new[]
                {
                    "  Lines began saying whether they were written while tuning or while transmitting on",
                    "  2026-09-02, and these do not, so this tool does not say which they were. Their figures",
                    "  are kept apart from every line that does say.",
                },
                "SC_MIC — the transmit audio level the radio measured, from any source:",
                "ON LINES WITH NO STATE"),
        };

        /// <summary>
        /// The figures, one section per state, and NOTHING POOLED ACROSS
        /// STATES (#625, H19, Sol's H18 blocker). A tune's carrier and a voice
        /// transmission's power are different measurements; one "highest
        /// forward power" over both lets the tune's reading stand as the
        /// transmission's, which is the misreading the writer's <c>state=</c>
        /// field exists to prevent.
        /// </summary>
        private static void DescribeByState(Reading r, Action<string> write)
        {
            var present = StateSections
                .Select(w => (Words: w, Lines: r.LinesIn(w.State)))
                .Where(s => s.Lines.Length > 0)
                .ToArray();

            string[] parts = present.Select(s => $"{s.Lines.Length} {s.Words.Tally}").ToArray();
            string tally = parts.Length == 1 ? parts[0]
                : string.Join(", ", parts[..^1]) + " and " + parts[^1];
            write($"{r.TxLines.Count} txMeters lines read: {tally}.");
            if (present.Length > 1)
            {
                write("Each kind is reported on its own below, and no figure combines two of them.");
            }
            write("");

            bool noSampleExplained = false;
            bool sentinelExplained = false;
            foreach ((StateWords words, TxMeterLine[] lines) in present)
            {
                int count = lines.Length;
                if (count == 1)
                {
                    write($"{words.Heading}: 1 {words.Snapshot}.");
                }
                else
                {
                    double spanMs = lines[^1].Tick - lines[0].Tick;
                    write($"{words.Heading}: {count} {words.Snapshot}s, the first and last {spanMs / 1000.0:0.#} seconds apart.");
                }
                foreach (string line in words.Meaning) write(line);
                write("");

                write(words.ScMicLabel);
                Band(lines.Select(l => l.ScMicDb), "dBFS", count, write);
                double[] peaks = lines.Where(l => l.ScMicPeakDb.HasValue).Select(l => l.ScMicPeakDb!.Value).ToArray();
                if (peaks.Length > 0) write($"  highest peak the app held: {peaks.Max():0.#} dBFS.");
                write("");

                write("SW ALC — transmit drive after the radio's own levelling:");
                Band(lines.Select(l => l.SwAlcDb), "dBFS", count, write);
                write("");

                write("Forward power, as traced in dBm and as the analyzer publishes it in watts:");
                Band(lines.Select(l => l.ForwardDbm), "dBm", count, write);
                double[] watts = lines.Where(l => l.ForwardWatts.HasValue).Select(l => l.ForwardWatts!.Value).ToArray();
                if (watts.Length > 0)
                {
                    write($"  in watts: lowest {watts.Min():0.###}, highest {watts.Max():0.###}.");
                }
                write("");

                // H17 traces say outright that a meter had not reported; older
                // ones printed the -150 initialiser, counted below.
                int scNoSample = lines.Count(l => l.ScMicNoSample);
                int alcNoSample = lines.Count(l => l.SwAlcNoSample);
                if (scNoSample > 0 || alcNoSample > 0)
                {
                    write($"LINES WRITTEN BEFORE A METER HAD REPORTED, {words.Qualifier}:");
                    if (scNoSample > 0) write($"  SC_MIC had not reported on {scNoSample} of {count} lines.");
                    if (alcNoSample > 0) write($"  SW ALC had not reported on {alcNoSample} of {count} lines.");
                    if (!noSampleExplained)
                    {
                        write("  The trace says so itself: those lines carry 'no-sample' rather than a number,");
                        write("  so they are not readings of silence and are left out of the figures above.");
                        noSampleExplained = true;
                    }
                    else
                    {
                        write("  As before, those lines carry 'no-sample' and are left out of the figures.");
                    }
                    write("");
                }

                int scSentinel = lines.Count(l => l.ScMicAtSentinel);
                int alcSentinel = lines.Count(l => l.SwAlcAtSentinel);
                if (scSentinel > 0 || alcSentinel > 0)
                {
                    write($"SAMPLES SITTING ON THE IDLE SENTINEL, {words.Qualifier}:");
                    if (scSentinel > 0) write($"  SC_MIC read -150 on {scSentinel} of {count} lines.");
                    if (alcSentinel > 0) write($"  SW ALC read -150 on {alcSentinel} of {count} lines.");
                    if (!sentinelExplained)
                    {
                        write("  This is the ambiguity the whole fact audit turns on. A meter that has never");
                        write("  reported and a meter reporting its floor produce the identical number, and");
                        write("  they are opposite diagnoses: one means nobody looked, the other means the");
                        write("  radio genuinely heard nothing. The trace cannot tell them apart either — only");
                        write("  the has-it-reported gate in TxChainFacts can, which is why it is there.");
                        sentinelExplained = true;
                    }
                    else
                    {
                        write("  The same ambiguity as above: the trace cannot say which of the two it is.");
                    }
                    write("");
                }
            }
        }

        /// <summary>Lowest, highest and last of the values that ARRIVED. A line
        /// that said <see cref="NoSample"/> contributes nothing, and a meter
        /// that never reported on any line says so instead of printing a
        /// number.</summary>
        private static void Band(IEnumerable<double?> values, string units, int lines, Action<string> write)
        {
            double[] v = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            if (v.Length == 0)
            {
                write($"  no reading arrived on any of the {lines} lines.");
                return;
            }
            string counted = v.Length == lines ? "" : $" (from the {v.Length} of {lines} lines that carried one)";
            write(string.Create(CultureInfo.InvariantCulture,
                $"  lowest {v.Min():0.#} {units}, highest {v.Max():0.#} {units}, last {v[^1]:0.#} {units}{counted}."));
        }
    }
}
