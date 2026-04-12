using System;
using System.Threading;
using System.Threading.Tasks;

namespace JJFlexWpf
{
    /// <summary>
    /// Plays Morse code notifications for app status events.
    /// Uses PARIS-standard timing at configurable speed and sidetone frequency.
    /// Prosigns: AS (wait/connecting), BT (break/connected), SK (end/closing).
    /// </summary>
    public class MorseNotifier
    {
        // Element types from JJPortaudio Morse.cs code table
        private const byte Ignore = 0;
        private const byte Space = 1;
        private const byte Dit = 2;
        private const byte Dah = 3;

        // PARIS standard: dit length = 1200 / WPM
        private const int ParisDividend = 1200;

        // Prosign element sequences (no intra-character spacing — they're single characters)
        private static readonly byte[] ProsignAS = { Dit, Dah, Dit, Dit, Dit };       // &
        private static readonly byte[] ProsignBT = { Dah, Dit, Dit, Dit, Dah };       // =
        private static readonly byte[] ProsignSK = { Dit, Dit, Dit, Dah, Dit, Dah };  // $

        // Morse code table — letters A-Z only (index 0 = A)
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

        private ICwNotificationOutput _output;
        private CancellationTokenSource? _cts;

        /// <summary>Sidetone frequency in Hz (default 700).</summary>
        public int SidetoneHz { get; set; } = 700;

        /// <summary>Speed in words per minute (default 20).</summary>
        public int SpeedWpm { get; set; } = 20;

        /// <summary>Volume 0.0-1.0 (default 0.25).</summary>
        public float Volume { get; set; } = 0.25f;

        /// <summary>True if a notification is currently playing.</summary>
        public bool IsPlaying => _cts != null && !_cts.IsCancellationRequested;

        public MorseNotifier(ICwNotificationOutput output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>Update the output provider (e.g., when adding haptic output).</summary>
        public void SetOutput(ICwNotificationOutput output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        // --- Timing ---

        private int DitMs => ParisDividend / Math.Max(SpeedWpm, 5);
        private int DahMs => DitMs * 3;
        private int IntraCharMs => DitMs;       // gap between elements within a character
        private int InterCharMs => DitMs * 3;   // gap between characters
        private int InterWordMs => DitMs * 7;   // gap between words

        // --- Public API ---

        /// <summary>Play the AS prosign (wait / connection in progress).</summary>
        public Task PlayAS(CancellationToken ct = default) => PlayElements(ProsignAS, ct);

        /// <summary>Play the BT prosign (break / connected).</summary>
        public Task PlayBT(CancellationToken ct = default) => PlayElements(ProsignBT, ct);

        /// <summary>Play the SK prosign (end of contact / app closing).</summary>
        public Task PlaySK(CancellationToken ct = default) => PlayElements(ProsignSK, ct);

        /// <summary>Play a string as Morse code (e.g., mode name "USB", "CW", "AM").</summary>
        public async Task PlayString(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(text)) return;
            Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            try
            {
                for (int i = 0; i < text.Length; i++)
                {
                    token.ThrowIfCancellationRequested();

                    char c = char.ToUpperInvariant(text[i]);
                    if (c == ' ')
                    {
                        await _output.DelayAsync(InterWordMs);
                        continue;
                    }

                    byte[]? elements = GetElementsForChar(c);
                    if (elements == null) continue;

                    await PlayElementsInternal(elements, token);

                    // Inter-character gap (unless last character)
                    if (i < text.Length - 1)
                        await _output.DelayAsync(InterCharMs);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>Cancel any currently playing notification.</summary>
        public void Cancel()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        // --- Internal ---

        private async Task PlayElements(byte[] elements, CancellationToken ct)
        {
            Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            try
            {
                await PlayElementsInternal(elements, token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task PlayElementsInternal(byte[] elements, CancellationToken token)
        {
            for (int i = 0; i < elements.Length; i++)
            {
                token.ThrowIfCancellationRequested();

                byte el = elements[i];
                if (el == Dit)
                {
                    _output.PlayTone(SidetoneHz, DitMs, Volume);
                    await _output.DelayAsync(DitMs);
                }
                else if (el == Dah)
                {
                    _output.PlayTone(SidetoneHz, DahMs, Volume);
                    await _output.DelayAsync(DahMs);
                }

                // Intra-character gap (unless last element)
                if (i < elements.Length - 1 && el != Space && el != Ignore)
                    await _output.DelayAsync(IntraCharMs);
            }
        }

        private static byte[]? GetElementsForChar(char c)
        {
            if (c >= 'A' && c <= 'Z')
                return Letters[c - 'A'];
            // Prosign shortcuts
            if (c == '&') return ProsignAS;
            if (c == '=') return ProsignBT;
            if (c == '$') return ProsignSK;
            return null;
        }
    }
}
