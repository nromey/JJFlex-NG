#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// How long a reader takes to say a piece of text — the estimate every
    /// reader without a completion channel lives on (#557).
    ///
    /// <para><b>What replaced 80 ms per character, and why not just a
    /// smaller constant.</b> Measured against NVDA actually speaking, at the
    /// operator's own rate, on 2026-09-06: 10 characters took 551 ms, 77 took
    /// 2587, 164 took 8079 — 55, 34 and 49 ms per character. Not constant and
    /// not monotonic with length, because what costs time is not characters:
    /// a 10-character utterance is mostly fixed overhead; the 164-character
    /// one has five sentences (the reader pauses at each), four commas, and
    /// tokens like <c>FLEX-8600</c>, <c>PC</c> and <c>JJ</c> that expand when
    /// spoken far beyond their character count. A constant that fit one of
    /// those would be wrong on the other two, and the old constant was about
    /// 60% high on all three — in the direction that hurts, because an
    /// overestimate keeps an utterance in the ledger after the reader has
    /// finished it, and an entry still in the ledger is one the salvage
    /// believes went unheard.</para>
    ///
    /// <para><b>The model.</b> A fixed per-utterance overhead, plus a cost per
    /// spoken WORD-UNIT, plus a pause per sentence break and a smaller one
    /// per clause break. A token is one unit, plus its expansions: a digit
    /// run of n digits is about n−2 extra units ("8600" is "eighty-six
    /// hundred"), a spelled acronym (all capitals, no vowel: PC, JJ, TX, SWR)
    /// is one unit per letter, and a point or slash between digits is a
    /// spoken word of its own ("14.100.000" is seven units). The constants
    /// are the fit to the three measurements above — 200 ms, 170 ms per unit,
    /// 350 ms per sentence break, 150 ms per clause — and the tests pin the
    /// model within a stated band of each measurement so a retune is checked
    /// against data rather than taste.</para>
    ///
    /// <para><b>The constants are one operator's rate, and the model knows
    /// it.</b> <see cref="Scale"/> multiplies the whole estimate and is
    /// learned from real deliveries wherever a completion channel exists: a
    /// Completed outcome carries the actual duration, and the ratio of actual
    /// to modelled moves the scale by a fifth of the gap. It is persisted
    /// under the settings root so the sessions that lack the channel — an
    /// NVDA that hung the synchronous call, a different reader — inherit what
    /// the sessions that had it learned. Clamped so a wild sample cannot push
    /// the estimate somewhere absurd.</para>
    ///
    /// <para>The model returns its best guess. The ledger applies its own
    /// err-long margin on top (<c>SpeechArbiter.SalvageMarginPercent</c>),
    /// because "how long will this take" and "how long must I protect this"
    /// are different questions with different costs of being wrong.</para>
    /// </summary>
    public sealed class SpeechRateModel
    {
        // ── The fit (2026-09-06, NVDA 2026.2, the operator's rate) ──

        /// <summary>Per utterance: the reader's start-up, the trailing pause before "done".</summary>
        internal const int OverheadMs = 200;

        /// <summary>Per spoken word-unit.</summary>
        internal const int UnitMs = 170;

        /// <summary>Pause the reader inserts between sentences inside one utterance.</summary>
        internal const int SentenceBreakMs = 350;

        /// <summary>Pause at a comma, semicolon or colon between clauses.</summary>
        internal const int ClauseBreakMs = 150;

        // ── Calibration bounds ──

        internal const double MinScale = 0.5;
        internal const double MaxScale = 3.0;

        /// <summary>How far one observation moves the scale toward the observed ratio.</summary>
        internal const double LearningRate = 0.2;

        /// <summary>Observations shorter than this many word-units are overhead-dominated and teach nothing about rate.</summary>
        internal const int MinUnitsToLearnFrom = 4;

        /// <summary>The scale must move at least this much before a save is worth a disk write.</summary>
        internal const double SaveThreshold = 0.02;

        private readonly object _lock = new object();
        private readonly string? _persistPath;
        private double _scale = 1.0;
        private double _savedScale = 1.0;
        private int _samples;

        /// <summary>An unpersisted model at the reference rate. Tests, and the static estimate.</summary>
        public SpeechRateModel() { }

        private SpeechRateModel(string? persistPath) { _persistPath = persistPath; }

        /// <summary>
        /// A model that remembers its calibration in
        /// <paramref name="settingsRoot"/>. Never throws: an unreadable file
        /// is the reference rate, an unwritable one is a trace line.
        /// </summary>
        public static SpeechRateModel Persisted(string settingsRoot)
        {
            string path = string.IsNullOrEmpty(settingsRoot)
                ? string.Empty
                : Path.Combine(settingsRoot, FileName);
            var m = new SpeechRateModel(path.Length == 0 ? null : path);
            m.Load();
            return m;
        }

        /// <summary>Beside the other per-operator stores under the settings root.</summary>
        internal const string FileName = "speechRateV1.json";

        /// <summary>Multiplier on the reference rate. 1.0 is the fit; above 1 is a slower reader.</summary>
        public double Scale { get { lock (_lock) { return _scale; } } }

        /// <summary>How many Completed outcomes have taught this model.</summary>
        public int Samples { get { lock (_lock) { return _samples; } } }

        /// <summary>Best estimate of the milliseconds the reader will spend on <paramref name="text"/>, at the learned rate.</summary>
        public int Estimate(string text)
        {
            double scale;
            lock (_lock) { scale = _scale; }
            return (int)Math.Round(Uncalibrated(text) * scale);
        }

        /// <summary>The fit alone, at the reference rate. What the static ledger estimate and the tests use.</summary>
        public static int Uncalibrated(string text)
        {
            var s = Analyse(text);
            return OverheadMs + s.Units * UnitMs + s.SentenceBreaks * SentenceBreakMs + s.ClauseBreaks * ClauseBreakMs;
        }

        /// <summary>
        /// A Completed delivery taught us how long <paramref name="text"/>
        /// really took. Short utterances are ignored: 551 ms for two words is
        /// almost all overhead and says nothing about the rate.
        /// </summary>
        public void Observe(string text, int actualMs)
        {
            if (actualMs <= 0) return;
            var s = Analyse(text);
            if (s.Units < MinUnitsToLearnFrom) return;

            double modelled = Uncalibrated(text);
            if (modelled <= 0) return;
            double ratio = actualMs / modelled;

            bool save;
            double scale;
            lock (_lock)
            {
                _scale = Math.Clamp(_scale + (ratio - _scale) * LearningRate, MinScale, MaxScale);
                _samples++;
                scale = _scale;
                save = _persistPath != null && Math.Abs(_scale - _savedScale) >= SaveThreshold;
                if (save) _savedScale = _scale;
            }

            Tracing.TraceLine(
                $"SpeechRateModel: '{Clip(text)}' took {actualMs} ms against {modelled:F0} modelled "
                + $"(ratio {ratio:F2}); rate scale now {scale:F2} after {_samples} sample(s).",
                System.Diagnostics.TraceLevel.Verbose);

            if (save) Save(scale);
        }

        // ── Text analysis ──

        internal readonly struct Shape
        {
            public Shape(int units, int sentenceBreaks, int clauseBreaks)
            {
                Units = units; SentenceBreaks = sentenceBreaks; ClauseBreaks = clauseBreaks;
            }
            public int Units { get; }
            public int SentenceBreaks { get; }
            public int ClauseBreaks { get; }
        }

        /// <summary>Count spoken units and pauses. Pure; the tests read it directly.</summary>
        internal static Shape Analyse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Shape(0, 0, 0);
            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int units = 0, sentences = 0, clauses = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                bool last = i == tokens.Length - 1;

                // Trailing punctuation decides the pause AFTER this token,
                // and only when another token follows: the pause after the
                // final word is the overhead's business.
                int end = t.Length;
                while (end > 0 && IsPunctuation(t[end - 1])) end--;
                if (!last && end < t.Length)
                {
                    char p = t[end];   // the first trailing punctuation mark
                    if (p == '.' || p == '!' || p == '?' || p == ';') sentences++;
                    else if (p == ',' || p == ':') clauses++;
                }
                // Leading punctuation (an opening quote, a bracket) is silent.
                int start = 0;
                while (start < end && IsPunctuation(t[start])) start++;
                if (start >= end) { units++; continue; }   // a lone symbol still gets said, roughly

                units += UnitsIn(t.AsSpan(start, end - start));
            }
            return new Shape(units, sentences, clauses);
        }

        /// <summary>
        /// Word-units in one token with its punctuation stripped. Splits on
        /// internal separators (a hyphenated pair is two spoken words), then
        /// prices each part for the ways a reader expands it.
        /// </summary>
        private static int UnitsIn(ReadOnlySpan<char> token)
        {
            int units = 0;
            int partStart = 0;
            for (int i = 0; i <= token.Length; i++)
            {
                bool atEnd = i == token.Length;
                char c = atEnd ? '\0' : token[i];
                bool sep = !atEnd && (c == '-' || c == '/' || c == '.' || c == ':' || c == '_');
                if (!atEnd && !sep) continue;

                if (i > partStart) units += UnitsInPart(token.Slice(partStart, i - partStart));

                // A point, slash or colon BETWEEN two digits is spoken
                // ("fourteen point one hundred"); a hyphen only splits.
                if (sep && c != '-' && c != '_' && i > 0 && i + 1 < token.Length
                    && char.IsDigit(token[i - 1]) && char.IsDigit(token[i + 1]))
                    units++;
                partStart = i + 1;
            }
            return Math.Max(units, 1);
        }

        private static int UnitsInPart(ReadOnlySpan<char> part)
        {
            int units = 1;

            // Digit runs: "8600" is three spoken words, "100" is two, "15" one.
            int run = 0;
            for (int i = 0; i <= part.Length; i++)
            {
                if (i < part.Length && char.IsDigit(part[i])) { run++; continue; }
                if (run > 2) units += run - 2;
                run = 0;
            }

            // A spelled acronym: two or more capitals, no lowercase, no
            // vowel — PC, JJ, TX, SWR, RIT. A reader says each letter. FLEX
            // and USB have vowels and are (usually) said as words.
            if (part.Length >= 2 && part.Length <= 5)
            {
                bool allCaps = true, vowel = false;
                foreach (char c in part)
                {
                    if (!char.IsUpper(c)) { allCaps = false; break; }
                    if ("AEIOUY".IndexOf(c) >= 0) vowel = true;
                }
                if (allCaps && !vowel) units += part.Length - 1;
            }
            return units;
        }

        private static bool IsPunctuation(char c) =>
            c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?'
            || c == '"' || c == '\'' || c == '(' || c == ')' || c == '[' || c == ']'
            || c == '…' || c == '“' || c == '”' || c == '‘' || c == '’';

        private static string Clip(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

        // ── Persistence ──

        private sealed class Stored
        {
            public double Scale { get; set; } = 1.0;
            public int Samples { get; set; }
            public string? Updated { get; set; }
        }

        private void Load()
        {
            if (_persistPath == null) return;
            try
            {
                if (!File.Exists(_persistPath)) return;
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_persistPath));
                if (stored == null || double.IsNaN(stored.Scale) || double.IsInfinity(stored.Scale)) return;
                lock (_lock)
                {
                    _scale = Math.Clamp(stored.Scale, MinScale, MaxScale);
                    _savedScale = _scale;
                    _samples = Math.Max(0, stored.Samples);
                }
                Tracing.TraceLine(
                    $"SpeechRateModel: loaded rate scale {_scale:F2} from {_samples} earlier sample(s) ({_persistPath}).",
                    System.Diagnostics.TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"SpeechRateModel: could not read {_persistPath} ({ex.Message}); using the reference rate.",
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        private void Save(double scale)
        {
            if (_persistPath == null) return;
            try
            {
                string? dir = Path.GetDirectoryName(_persistPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var stored = new Stored
                {
                    Scale = scale,
                    Samples = _samples,
                    Updated = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
                File.WriteAllText(_persistPath, JsonSerializer.Serialize(stored));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"SpeechRateModel: could not write {_persistPath} ({ex.Message}); the calibration lives for this session only.",
                    System.Diagnostics.TraceLevel.Warning);
            }
        }
    }
}
