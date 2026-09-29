using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JJFlex.TxFactAudit
{
    /// <summary>One correlated transmit-meter snapshot, as the app traced it.
    /// A null value is one the line said had not arrived: since H17 (#625)
    /// the writer prints <c>no-sample</c> instead of a placeholder number, and
    /// this reader keeps that as "no reading" rather than inventing one.</summary>
    public sealed record TxMeterLine(int Tick, double? ScMicDb, double? ScMicPeakDb,
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
    /// line carries <c>peak</c> — the maximum <c>_scMicMaxDb</c> has reached,
    /// tracked by the handler that sees every reading. So a once-a-second line
    /// is not a once-a-second measurement: transients inside the second are
    /// already in the peak. What is lost is the shape of the second, not its
    /// height, and height is what anything peak-sensitive wants.</para>
    ///
    /// <para><b>Two limits, designed around rather than papered over.</b>
    /// <c>traceTxMeters</c> returns unless the radio is transmitting or tuning,
    /// so there are NO lines while receiving — an absence of lines means no transmission
    /// was traced, and reporting that as "the meters are unreadable" would be
    /// its own fabricated fact. And the per-meter lines exist only when the
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
        /// </summary>
        private static readonly Regex TxMeters = new(
            @"^(?<tick>\d+)\s+\[[^\]]*\]\s+txMeters:\s+(?:state=\S+\s+)?"
            + @"SC_MIC=(?<sc>-?[\d.]+|no-sample)\s+\(peak\s+(?<peak>-?[\d.]+|no-sample)\)"
            + @"(?:\s+via\s+.*?)?\s+SWALC=(?<alc>-?[\d.]+|no-sample)"
            + @"\s+fwd=(?:(?<fwd>-?[\d.]+)\s+dBm|no-sample)",
            RegexOptions.Compiled);

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
            @"\]\s+(?<key>micData|micPeakData|compPeakData|SWRData|forwardPower|hwALCData):\s*(?<v>-?[\d.]+)",
            RegexOptions.Compiled);

        /// <summary>The coalesced form MeterTraceStream writes since 2026-08-21:
        /// one line per meter per second, min/max/last plus the sample count.
        /// The format is a contract with Radios/MeterTraceStream.cs — change
        /// either side only in step with the other.</summary>
        private static readonly Regex CoalescedValue = new(
            @"\]\s+(?<key>micData|micPeakData|compPeakData|SWRData|forwardPower|hwALCData):\s+min=(?<min>-?[\d.]+)\s+max=(?<max>-?[\d.]+)\s+last=(?<last>-?[\d.]+)\s+n=(?<n>\d+)",
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

            public bool AnyTransmission => TxLines.Count > 0;

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
                        int.Parse(m.Groups["tick"].Value, CultureInfo.InvariantCulture),
                        Value(m.Groups["sc"]), Value(m.Groups["peak"]),
                        Value(m.Groups["alc"]), Value(m.Groups["fwd"])));
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

        private static double Num(string s) =>
            double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>A <c>txMeters:</c> field: its number, or null when the line
        /// said <see cref="NoSample"/> (or, for fwd, carried no number).</summary>
        private static double? Value(Group g) =>
            !g.Success || g.Value == NoSample ? null : Num(g.Value);

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

            if (!r.AnyTransmission)
            {
                write("No transmit meter lines at all — and that is a statement about whether the radio");
                write("TRANSMITTED, not about whether its meters can be read. FlexBase.traceTxMeters");
                write("returns immediately unless the radio is transmitting or tuning, so a receiving session");
                write("traces none of these however healthy every meter is. Do not record the transmit");
                write("meter facts as unreadable on this evidence.");
                write("");
                write("The receive-side facts are settings and telemetry the radio holds continuously.");
                write("Read those with 'TxFactAudit audit', which asks the radio rather than the trace.");
            }
            else
            {
                TxMeterLine[] lines = r.TxLines.ToArray();
                int seconds = lines.Length;
                double spanMs = lines[^1].Tick - lines[0].Tick;

                write($"{seconds} transmit meter snapshots spanning {spanMs / 1000.0:0.#} seconds.");
                write("At most one line a second while transmitting and four while tuning, but each");
                write("carries the peak the app tracked between lines, so the transients inside each");
                write("second are already accounted for.");
                write("");

                write("SC_MIC — what the radio heard on transmit, from any source:");
                Band(lines.Select(l => l.ScMicDb), "dBFS", seconds, write);
                double[] peaks = lines.Where(l => l.ScMicPeakDb.HasValue).Select(l => l.ScMicPeakDb!.Value).ToArray();
                if (peaks.Length > 0) write($"  highest peak the app held: {peaks.Max():0.#} dBFS.");
                write("");

                write("SW ALC — transmit drive after the radio's own levelling:");
                Band(lines.Select(l => l.SwAlcDb), "dBFS", seconds, write);
                write("");

                write("Forward power, as traced in dBm and as the analyzer publishes it in watts:");
                Band(lines.Select(l => l.ForwardDbm), "dBm", seconds, write);
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
                    write("LINES WRITTEN BEFORE A METER HAD REPORTED, WHILE TRANSMITTING:");
                    if (scNoSample > 0) write($"  SC_MIC had not reported on {scNoSample} of {seconds} lines.");
                    if (alcNoSample > 0) write($"  SW ALC had not reported on {alcNoSample} of {seconds} lines.");
                    write("  The trace says so itself: those lines carry 'no-sample' rather than a number,");
                    write("  so they are not readings of silence and are left out of the figures above.");
                    write("");
                }

                int scSentinel = lines.Count(l => l.ScMicAtSentinel);
                int alcSentinel = lines.Count(l => l.SwAlcAtSentinel);
                if (scSentinel > 0 || alcSentinel > 0)
                {
                    write("SAMPLES SITTING ON THE IDLE SENTINEL, WHILE TRANSMITTING:");
                    if (scSentinel > 0) write($"  SC_MIC read -150 on {scSentinel} of {seconds} lines.");
                    if (alcSentinel > 0) write($"  SW ALC read -150 on {alcSentinel} of {seconds} lines.");
                    write("  This is the ambiguity the whole fact audit turns on. A meter that has never");
                    write("  reported and a meter reporting its floor produce the identical number, and");
                    write("  they are opposite diagnoses: one means nobody looked, the other means the");
                    write("  radio genuinely heard nothing. The trace cannot tell them apart either — only");
                    write("  the has-it-reported gate in TxChainFacts can, which is why it is there.");
                    write("");
                }
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
