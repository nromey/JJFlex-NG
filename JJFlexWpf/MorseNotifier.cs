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
        private static readonly byte[] ProsignAS = { Dit, Dah, Dit, Dit, Dit };       // &
        private static readonly byte[] ProsignBT = { Dah, Dit, Dit, Dit, Dah };       // =
        private static readonly byte[] ProsignSK = { Dit, Dit, Dit, Dah, Dit, Dah };  // $

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
        private CancellationTokenSource? _cts;

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

        /// <summary>True if a notification is currently playing.</summary>
        public bool IsPlaying => _cts != null && !_cts.IsCancellationRequested;

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
        public async Task PlayString(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(text)) return;

            Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            try
            {
                var elements = BuildStringElements(text);
                if (elements.Count == 0) return;
                await _output.PlayElementsAsync(
                    elements, SidetoneHz, Volume, RiseFallMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal cancel path */ }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>Cancel any currently-playing notification.</summary>
        public void Cancel()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _output.Cancel();
        }

        // --- Internal ---

        private async Task PlayCharacter(byte[] encodedChar, CancellationToken ct)
        {
            Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            try
            {
                var elements = BuildCharacterElements(encodedChar);
                if (elements.Count == 0) return;
                await _output.PlayElementsAsync(
                    elements, SidetoneHz, Volume, RiseFallMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal */ }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
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
        /// inter-character gaps, words separated by inter-word gaps.
        /// </summary>
        private List<CwElement> BuildStringElements(string text)
        {
            int interChar = InterCharMs;
            int interWord = InterWordMs;

            var elements = new List<CwElement>(text.Length * 6);
            bool firstChar = true;

            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToUpperInvariant(text[i]);

                if (c == ' ')
                {
                    if (!firstChar) elements.Add(CwElement.Gap(interWord));
                    firstChar = true;
                    continue;
                }

                byte[]? encoded = GetEncodingForChar(c);
                if (encoded == null) continue;

                if (!firstChar) elements.Add(CwElement.Gap(interChar));
                var charElements = BuildCharacterElements(encoded);
                elements.AddRange(charElements);
                firstChar = false;
            }
            return elements;
        }

        private static byte[]? GetEncodingForChar(char c)
        {
            if (c >= 'A' && c <= 'Z') return Letters[c - 'A'];
            if (c >= '0' && c <= '9') return Digits[c - '0'];
            // Prosign shortcuts
            if (c == '&') return ProsignAS;
            if (c == '=') return ProsignBT;
            if (c == '$') return ProsignSK;
            return null;
        }
    }
}
