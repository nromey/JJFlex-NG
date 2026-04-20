using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JJFlexWpf
{
    /// <summary>
    /// Plays Morse code notifications for app status events.
    /// </summary>
    /// <remarks>
    /// Builds a <see cref="CwElement"/> list from PARIS-standard timing and
    /// dispatches the whole sequence to an <see cref="ICwNotificationOutput"/>
    /// in one batch. The output implementation drives element timing (sample-
    /// accurate for audio; whatever is appropriate for haptics/visual).
    ///
    /// Supported prosigns: AS (wait/connecting), BT (break/connected),
    /// SK (end/closing). Arbitrary strings (mode names, letters, digits)
    /// via <see cref="PlayString"/>.
    ///
    /// Farnsworth timing and per-operator weighting are intentionally left
    /// out of the current notifier — they are learning-tool concerns, and
    /// prosign notifications are short enough that operators don't benefit
    /// from them. See <c>docs/planning/design/cw-keying-design.md</c> for
    /// how they would plug in on top of the same element builder.
    /// </remarks>
    public class MorseNotifier
    {
        // Element markers used when walking an encoded character.
        private const byte Dit = 2;
        private const byte Dah = 3;

        // PARIS standard: dit length in ms = 1200 / WPM.
        private const int ParisDividend = 1200;

        // Prosigns (single "characters" — no intra-character gap between
        // these elements, because a prosign is one run-together character).
        private static readonly byte[] ProsignAS = { Dit, Dah, Dit, Dit, Dit };       // & — wait / standing by
        private static readonly byte[] ProsignBT = { Dah, Dit, Dit, Dit, Dah };       // = — break / ready
        private static readonly byte[] ProsignSK = { Dit, Dit, Dit, Dah, Dit, Dah };  // $ — end of contact
        private static readonly byte[] ProsignAR = { Dit, Dah, Dit, Dah, Dit };       // + — end of message
        private static readonly byte[] ProsignKN = { Dah, Dit, Dah, Dah, Dit };       // (  — invitation to named station

        // Prosign lookup by symbolic name (Sprint 26 Phase 6: bracket syntax
        // "<AS>" / "<SK>" etc. in PlayUtterance strings). Names are uppercase
        // lookup keys; users can type them in any case but the parser
        // uppercases.
        private static readonly Dictionary<string, byte[]> ProsignsByName = new(StringComparer.OrdinalIgnoreCase)
        {
            { "AS", ProsignAS },
            { "BT", ProsignBT },
            { "SK", ProsignSK },
            { "AR", ProsignAR },
            { "KN", ProsignKN },
        };

        private static readonly byte[][] Letters =
        {
            new byte[] { Dit, Dah },             // A
            new byte[] { Dah, Dit, Dit, Dit },   // B
            new byte[] { Dah, Dit, Dah, Dit },   // C
            new byte[] { Dah, Dit, Dit },         // D
            new byte[] { Dit },                   // E
            new byte[] { Dit, Dit, Dah, Dit },   // F
            new byte[] { Dah, Dah, Dit },         // G
            new byte[] { Dit, Dit, Dit, Dit },   // H
            new byte[] { Dit, Dit },              // I
            new byte[] { Dit, Dah, Dah, Dah },   // J
            new byte[] { Dah, Dit, Dah },         // K
            new byte[] { Dit, Dah, Dit, Dit },   // L
            new byte[] { Dah, Dah },              // M
            new byte[] { Dah, Dit },              // N
            new byte[] { Dah, Dah, Dah },         // O
            new byte[] { Dit, Dah, Dah, Dit },   // P
            new byte[] { Dah, Dah, Dit, Dah },   // Q
            new byte[] { Dit, Dah, Dit },         // R
            new byte[] { Dit, Dit, Dit },         // S
            new byte[] { Dah },                   // T
            new byte[] { Dit, Dit, Dah },         // U
            new byte[] { Dit, Dit, Dit, Dah },   // V
            new byte[] { Dit, Dah, Dah },         // W
            new byte[] { Dah, Dit, Dit, Dah },   // X
            new byte[] { Dah, Dit, Dah, Dah },   // Y
            new byte[] { Dah, Dah, Dit, Dit },   // Z
        };

        // Digits 0–9 (index 0 = '0').
        private static readonly byte[][] Digits =
        {
            new byte[] { Dah, Dah, Dah, Dah, Dah }, // 0
            new byte[] { Dit, Dah, Dah, Dah, Dah }, // 1
            new byte[] { Dit, Dit, Dah, Dah, Dah }, // 2
            new byte[] { Dit, Dit, Dit, Dah, Dah }, // 3
            new byte[] { Dit, Dit, Dit, Dit, Dah }, // 4
            new byte[] { Dit, Dit, Dit, Dit, Dit }, // 5
            new byte[] { Dah, Dit, Dit, Dit, Dit }, // 6
            new byte[] { Dah, Dah, Dit, Dit, Dit }, // 7
            new byte[] { Dah, Dah, Dah, Dit, Dit }, // 8
            new byte[] { Dah, Dah, Dah, Dah, Dit }, // 9
        };

        private ICwNotificationOutput _output;

        /// <summary>Sidetone frequency in Hz (default 700 — traditional CW sidetone).</summary>
        public int SidetoneHz { get; set; } = 700;

        /// <summary>Speed in words per minute (default 20). Clamped at read-time to ≥5 WPM.</summary>
        public int SpeedWpm { get; set; } = 20;

        /// <summary>Volume 0.0–1.0 (default 0.25).</summary>
        public float Volume { get; set; } = 0.25f;

        /// <summary>
        /// Attack and release time in milliseconds for each mark's raised-cosine
        /// envelope. 5 ms matches the ARRL minimum-click recommendation for
        /// speeds up to 30 WPM. Longer (10 ms) is cleaner spectrally; shorter
        /// sounds crisper but can click. Exposed as a tunable for future
        /// operator-preference work.
        /// </summary>
        public int RiseFallMs { get; set; } = 5;

        public MorseNotifier(ICwNotificationOutput output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>Replace the output implementation (e.g. audio → haptic).</summary>
        public void SetOutput(ICwNotificationOutput output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        // --- PARIS timing (milliseconds) ---

        private int DitMs => ParisDividend / Math.Max(SpeedWpm, 5);
        private int DahMs => DitMs * 3;
        private int IntraCharMs => DitMs;     // gap between elements within a character
        private int InterCharMs => DitMs * 3; // gap between characters of a string
        private int InterWordMs => DitMs * 7; // gap between words

        // --- Public API ---

        /// <summary>Play the AS prosign (wait / connection in progress).</summary>
        public Task PlayAS(CancellationToken ct = default) =>
            PlayCharacter(ProsignAS, ct);

        /// <summary>Play the BT prosign (break / connected).</summary>
        public Task PlayBT(CancellationToken ct = default) =>
            PlayCharacter(ProsignBT, ct);

        /// <summary>Play the SK prosign (end of contact / app closing).</summary>
        public Task PlaySK(CancellationToken ct = default) =>
            PlayCharacter(ProsignSK, ct);

        /// <summary>Play a string as Morse code (mode names, digits, etc.).</summary>
        /// <remarks>
        /// Enqueues the string's element sequence on the output's FIFO queue
        /// and returns a Task that resolves when it finishes playing. Does
        /// NOT cancel any in-flight sequence — concurrent Play* calls are
        /// serialized in arrival order.
        ///
        /// <para>
        /// Sprint 26 Phase 6: the parser now understands prosign bracket syntax.
        /// <c>"73 &lt;SK&gt;"</c> renders as "73" + the joined SK prosign with a
        /// standard PARIS inter-word gap between them — one continuous waveform,
        /// no inter-utterance boundary artifact (the BUG-061 pattern that
        /// resulted from calling <c>PlayString("73")</c> then <c>PlaySK()</c>
        /// as two separate queue items). Supported bracket names:
        /// <c>&lt;AS&gt;</c>, <c>&lt;BT&gt;</c>, <c>&lt;SK&gt;</c>,
        /// <c>&lt;AR&gt;</c>, <c>&lt;KN&gt;</c>.
        /// </para>
        /// </remarks>
        public async Task PlayString(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                var elements = BuildStringElements(text);
                if (elements.Count == 0) return;
                await _output.PlayElementsAsync(
                    elements, SidetoneHz, Volume, RiseFallMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal cancel path */ }
        }

        /// <summary>
        /// Single-utterance sign-off — equivalent to calling
        /// <see cref="PlayString"/> with <c>"&lt;text&gt; &lt;SK&gt;"</c> if
        /// <paramref name="text"/> doesn't already contain a bracket. Guarantees
        /// one continuous waveform with standard PARIS word spacing before the
        /// SK prosign, fixing BUG-061's inter-utterance artifact.
        /// </summary>
        public Task PlaySignoff(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(text)) return PlayString("<SK>", ct);
            if (text.Contains('<')) return PlayString(text, ct);
            return PlayString($"{text} <SK>", ct);
        }

        /// <summary>
        /// Shutdown-style interrupt. Tells the output to drop its in-flight
        /// sequence and flush any queued items. New Play* calls after this
        /// will still enqueue normally.
        /// </summary>
        public void Cancel()
        {
            _output.Cancel();
        }

        // --- Internal ---

        private async Task PlayCharacter(byte[] encodedChar, CancellationToken ct)
        {
            try
            {
                var elements = BuildCharacterElements(encodedChar);
                if (elements.Count == 0) return;
                await _output.PlayElementsAsync(
                    elements, SidetoneHz, Volume, RiseFallMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal */ }
        }

        /// <summary>
        /// Build the element list for a single character (or prosign).
        /// Marks separated by intra-character gaps. No leading or trailing gap.
        /// </summary>
        private List<CwElement> BuildCharacterElements(byte[] encodedChar)
        {
            int dit = DitMs;
            int dah = DahMs;
            int intra = IntraCharMs;

            var elements = new List<CwElement>(encodedChar.Length * 2);
            for (int i = 0; i < encodedChar.Length; i++)
            {
                byte el = encodedChar[i];
                if (el == Dit) elements.Add(CwElement.Mark(dit));
                else if (el == Dah) elements.Add(CwElement.Mark(dah));
                else continue;
                if (i < encodedChar.Length - 1)
                    elements.Add(CwElement.Gap(intra));
            }
            return elements;
        }

        /// <summary>
        /// Build the element list for a string — characters separated by
        /// inter-character gaps, words separated by inter-word gaps. Supports
        /// prosign bracket syntax <c>&lt;AS&gt;</c>, <c>&lt;BT&gt;</c>,
        /// <c>&lt;SK&gt;</c>, <c>&lt;AR&gt;</c>, <c>&lt;KN&gt;</c>.
        /// </summary>
        private List<CwElement> BuildStringElements(string text)
        {
            int interChar = InterCharMs;
            int interWord = InterWordMs;

            var elements = new List<CwElement>(text.Length * 6);
            bool firstChar = true;
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (c == ' ')
                {
                    if (!firstChar) elements.Add(CwElement.Gap(interWord));
                    firstChar = true;
                    i++;
                    continue;
                }

                // Bracket prosign: <NAME> scans until the closing '>' and
                // resolves via ProsignsByName. Unknown names are skipped
                // silently (dropping the whole <...> token) so operators
                // typing something odd don't produce mangled audio.
                if (c == '<')
                {
                    int closeIdx = text.IndexOf('>', i + 1);
                    if (closeIdx > i + 1)
                    {
                        string name = text.Substring(i + 1, closeIdx - i - 1);
                        if (ProsignsByName.TryGetValue(name, out byte[]? prosign))
                        {
                            if (!firstChar) elements.Add(CwElement.Gap(interChar));
                            elements.AddRange(BuildCharacterElements(prosign));
                            firstChar = false;
                        }
                        // else: unknown bracket name, drop silently
                        i = closeIdx + 1;
                        continue;
                    }
                    // no closing '>' — treat '<' as a regular un-encodable char (dropped)
                    i++;
                    continue;
                }

                char uc = char.ToUpperInvariant(c);
                byte[]? encoded = GetEncodingForChar(uc);
                if (encoded == null)
                {
                    i++;
                    continue;
                }

                if (!firstChar) elements.Add(CwElement.Gap(interChar));
                var charElements = BuildCharacterElements(encoded);
                elements.AddRange(charElements);
                firstChar = false;
                i++;
            }
            return elements;
        }

        private static byte[]? GetEncodingForChar(char c)
        {
            if (c >= 'A' && c <= 'Z') return Letters[c - 'A'];
            if (c >= '0' && c <= '9') return Digits[c - '0'];
            // Legacy prosign shortcuts (kept for backward compat with any
            // callers passing '&', '=', '$'). Prefer <AS>/<BT>/<SK> syntax
            // in new code.
            if (c == '&') return ProsignAS;
            if (c == '=') return ProsignBT;
            if (c == '$') return ProsignSK;
            if (c == '+') return ProsignAR;
            return null;
        }
    }
}
