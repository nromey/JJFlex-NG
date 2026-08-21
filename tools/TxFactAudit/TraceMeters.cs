using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JJFlex.TxFactAudit
{
    /// <summary>One correlated transmit-meter snapshot, as the app traced it.</summary>
    public sealed record TxMeterLine(int Tick, double ScMicDb, double ScMicPeakDb,
                                     double SwAlcDb, double ForwardDbm)
    {
        public double ForwardWatts => Math.Pow(10.0, ForwardDbm / 10.0) / 1000.0;

        /// <summary>True when a value is sitting exactly on the initialiser the
        /// app uses for "nothing has reported". Worth naming, because it is the
        /// value that used to reach an operator dressed as a measurement.</summary>
        public bool ScMicAtSentinel => ScMicDb <= -149.5;

        public bool SwAlcAtSentinel => SwAlcDb <= -149.5;
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
    /// <c>FlexBase.traceTxMeters</c> throttles to one line a second, but each
    /// line carries <c>peak</c> — the maximum <c>_scMicMaxDb</c> has reached,
    /// tracked by the handler that sees every reading. So a once-a-second line
    /// is not a once-a-second measurement: transients inside the second are
    /// already in the peak. What is lost is the shape of the second, not its
    /// height, and height is what anything peak-sensitive wants.</para>
    ///
    /// <para><b>Two limits, designed around rather than papered over.</b>
    /// <c>traceTxMeters</c> opens with <c>if (!Transmit) return;</c>, so there
    /// are NO lines while receiving — an absence of lines means no transmission
    /// was traced, and reporting that as "the meters are unreadable" would be
    /// its own fabricated fact. And at the default Info level only this
    /// correlated line exists; SWR, the codec MIC meter and HWALC are traced at
    /// Verbose, which is what the Detailed diagnostic capture turns on.</para>
    /// </summary>
    public static class TraceMeters
    {
        private static readonly Regex TxMeters = new(
            @"^(?<tick>\d+)\s+\[[^\]]*\]\s+txMeters:\s+SC_MIC=(?<sc>-?[\d.]+)\s+\(peak\s+(?<peak>-?[\d.]+)\)\s+SWALC=(?<alc>-?[\d.]+)\s+fwd=(?<fwd>-?[\d.]+)\s+dBm",
            RegexOptions.Compiled);

        /// <summary>The per-meter lines that only exist at Verbose. Named here
        /// so a run that has them can say so and a run that does not can say
        /// which level it would need.</summary>
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

        /// <summary>What one trace had to say about the transmit meters.</summary>
        public sealed class Reading
        {
            public long LinesRead { get; internal set; }
            public List<TxMeterLine> TxLines { get; } = new();
            public Dictionary<string, List<double>> VerboseSamples { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public bool AnyTransmission => TxLines.Count > 0;
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
                        Num(m.Groups["sc"].Value), Num(m.Groups["peak"].Value),
                        Num(m.Groups["alc"].Value), Num(m.Groups["fwd"].Value)));
                    continue;
                }

                Match v = VerboseValue.Match(line);
                if (v.Success)
                {
                    string key = v.Groups["key"].Value;
                    if (!result.VerboseSamples.TryGetValue(key, out List<double>? samples))
                    {
                        samples = new List<double>();
                        result.VerboseSamples[key] = samples;
                    }
                    samples.Add(Num(v.Groups["v"].Value));
                }
            }

            return result;
        }

        private static double Num(string s) =>
            double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

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
                write("returns immediately when the radio is not transmitting, so a receiving session");
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
                write("One line a second by design, but each carries the peak the app tracked between");
                write("lines, so the transients inside each second are already accounted for.");
                write("");

                write("SC_MIC — what the radio heard on transmit, from any source:");
                Band(lines.Select(l => l.ScMicDb), "dBFS", write);
                write($"  highest peak the app held: {lines.Max(l => l.ScMicPeakDb):0.#} dBFS.");
                write("");

                write("SW ALC — transmit drive after the radio's own levelling:");
                Band(lines.Select(l => l.SwAlcDb), "dBFS", write);
                write("");

                write("Forward power, as traced in dBm and as the analyzer publishes it in watts:");
                Band(lines.Select(l => l.ForwardDbm), "dBm", write);
                write($"  in watts: lowest {lines.Min(l => l.ForwardWatts):0.###}, "
                      + $"highest {lines.Max(l => l.ForwardWatts):0.###}.");
                write("");

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

            write("Per-meter lines, which exist only at Verbose:");
            if (r.VerboseSamples.Count == 0)
            {
                write($"  none. This session booted at level {session.Level}.");
                write("  SWR, the codec MIC meter and HWALC are traced at Verbose and are simply not in");
                write("  this file. That is a gap in the CAPTURE, not a finding about those meters —");
                write("  start a Detailed diagnostic capture before transmitting to get them.");
            }
            else
            {
                foreach ((string key, string meter) in VerboseMeters)
                {
                    string name = key.TrimEnd(':');
                    if (r.VerboseSamples.TryGetValue(name, out List<double>? samples) && samples.Count > 0)
                    {
                        write($"  {meter}: {samples.Count} readings, "
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

        private static void Band(IEnumerable<double> values, string units, Action<string> write)
        {
            double[] v = values.ToArray();
            write(string.Create(CultureInfo.InvariantCulture,
                $"  lowest {v.Min():0.#} {units}, highest {v.Max():0.#} {units}, last {v[^1]:0.#} {units}."));
        }
    }
}
