using System;
using System.Collections.Generic;
using System.Globalization;

namespace JJFlexWpf
{
    /// <summary>
    /// One <see cref="SoundTiming"/> trace line, read back (#855). Lives beside
    /// the writer so the two cannot drift: tools/soundtiming parses with this,
    /// and the tests round-trip a written line through it.
    /// </summary>
    public sealed class SoundTimingLine
    {
        private readonly Dictionary<string, string> _fields;

        private SoundTimingLine(Dictionary<string, string> fields, double traceMs)
        {
            _fields = fields;
            TraceMs = traceMs;
        }

        /// <summary>The trace prefix's milliseconds-since-start, or NaN when the line had none.</summary>
        public double TraceMs { get; }

        public int Version => Int("v");
        public string Sound => Get("sound") ?? "unnamed";
        public string? Detail => Get("detail")?.Replace(';', ' ');
        public string? Recipe => Get("recipe");
        public string Source => Get("source") ?? "lazy";
        public long QpcAsked => Long("qpcAsked");
        public long QpcMix => Long("qpcMix");
        public long QpcFrequency => Long("qpcFreq");
        public double SynthMs => Dbl("synth");
        public double AskedToHandoffMs => Dbl("asked2handoff");
        public double HandoffToMixMs => Dbl("handoff2mix");
        /// <summary>Audio already queued in the device at the first mix read, or NaN when the device did not say.</summary>
        public double QueuedMs => Dbl("queued");
        /// <summary>The app's estimate of asked-to-engine, or NaN.</summary>
        public double AskedToOutMs => Dbl("asked2out");
        public double MixReadMs => Dbl("mixRead");
        public double MixReadMaxMs => Dbl("mixReadMax");
        public int Reads => Int("reads");
        public int Inputs => Int("inputs");
        public long Samples => Long("samples");
        public string? Buffer => Get("buffer");

        /// <summary>A field by name, raw.</summary>
        public string? Get(string key) => _fields.TryGetValue(key, out var v) ? v : null;

        /// <summary>The ask, as a QPC instant in 100 ns units (the unit WASAPI stamps capture packets in).</summary>
        public long AskedHundredNs => QpcFrequency > 0 ? (long)(QpcAsked * (10_000_000.0 / QpcFrequency)) : 0;

        /// <summary>The first mix read, in 100 ns QPC units.</summary>
        public long MixHundredNs => QpcFrequency > 0 ? (long)(QpcMix * (10_000_000.0 / QpcFrequency)) : 0;

        /// <summary>
        /// Read a trace line. Accepts the whole line, prefix and all, or just the
        /// part from <see cref="SoundTiming.LinePrefix"/> on.
        /// </summary>
        public static bool TryParse(string? line, out SoundTimingLine? parsed)
        {
            parsed = null;
            if (string.IsNullOrEmpty(line)) return false;
            int at = line.IndexOf(SoundTiming.LinePrefix, StringComparison.Ordinal);
            if (at < 0) return false;

            double traceMs = double.NaN;
            if (at > 0)
            {
                int sp = line.IndexOf(' ');
                if (sp > 0 && double.TryParse(line.AsSpan(0, sp), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                    traceMs = t;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var rest = line.Substring(at + SoundTiming.LinePrefix.Length);
            foreach (var token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) continue;
                fields[token.Substring(0, eq)] = token.Substring(eq + 1);
            }
            if (!fields.ContainsKey("v") || !fields.ContainsKey("sound")) return false;
            parsed = new SoundTimingLine(fields, traceMs);
            return true;
        }

        private double Dbl(string key) =>
            Get(key) is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

        private long Long(string key) =>
            Get(key) is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;

        private int Int(string key) =>
            Get(key) is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }
}
