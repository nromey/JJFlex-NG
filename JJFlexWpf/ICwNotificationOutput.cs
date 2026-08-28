using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JJFlexWpf
{
    /// <summary>
    /// One CW element — either a keyed mark (dit/dah, a tone period) or an
    /// unkeyed gap (intra-character, inter-character, or inter-word silence).
    /// DurationMs carries the PARIS-computed length in milliseconds.
    /// </summary>
    public readonly struct CwElement
    {
        public CwElementType Type { get; }
        public int DurationMs { get; }

        /// <summary>
        /// True when this gap follows a COMPLETE character — the
        /// inter-character and inter-word gaps of a string, never the
        /// intra-character gap between the elements of one character.
        ///
        /// This is the grammar #182 keys on: "close, do not queue" means a
        /// superseded sequence yields at the next character boundary rather
        /// than mid-symbol, because a half-sent character is not silence, it
        /// is a DIFFERENT character (#88), and a fluent reader decodes
        /// garbage rather than noticing an interruption. Marks are never
        /// boundaries; a mark is by definition inside a character.
        /// </summary>
        public bool IsCharBoundary { get; }

        public CwElement(CwElementType type, int durationMs)
            : this(type, durationMs, isCharBoundary: false)
        {
        }

        public CwElement(CwElementType type, int durationMs, bool isCharBoundary)
        {
            Type = type;
            DurationMs = durationMs;
            IsCharBoundary = isCharBoundary && type == CwElementType.Gap;
        }

        public static CwElement Mark(int ms) => new CwElement(CwElementType.Mark, ms);
        public static CwElement Gap(int ms)  => new CwElement(CwElementType.Gap, ms);

        /// <summary>An inter-character or inter-word gap — a safe close point.</summary>
        public static CwElement BoundaryGap(int ms) =>
            new CwElement(CwElementType.Gap, ms, isCharBoundary: true);
    }

    public enum CwElementType
    {
        /// <summary>Keyed element — tone plays for DurationMs.</summary>
        Mark,
        /// <summary>Unkeyed element — silence for DurationMs.</summary>
        Gap
    }

    /// <summary>
    /// Abstraction for CW notification output. Separates the Morse *message*
    /// (element sequence) from the delivery *mechanism* (speaker tones,
    /// gamepad vibration, iPhone haptics, LED flashes, etc.).
    /// </summary>
    /// <remarks>
    /// The API is batch-oriented rather than tone-by-tone because correct CW
    /// keying requires precise inter-element timing that Task.Delay cannot
    /// deliver (Windows timer granularity is ~15 ms — enough jitter to
    /// corrupt dits at speeds above ~12 WPM). Audio implementations build
    /// one sample-provider that spans the whole sequence and submit it to
    /// the mixer in a single operation so the audio engine drives timing
    /// at sample-accurate resolution. Non-audio implementations (haptic,
    /// visual) get the same element timing and can render it on their own
    /// schedule.
    /// </remarks>
    public interface ICwNotificationOutput
    {
        /// <summary>
        /// Play a sequence of CW elements. Returns when the last element
        /// completes, or earlier if the CancellationToken fires.
        /// </summary>
        /// <param name="elements">
        /// Ordered sequence of marks and gaps. The caller is responsible for
        /// inserting intra-character gaps between marks of the same character;
        /// this method simply renders what it's given.
        /// </param>
        /// <param name="sidetoneHz">Tone frequency for mark elements (audio outputs).</param>
        /// <param name="volume">Amplitude 0.0–1.0 (audio outputs).</param>
        /// <param name="riseFallMs">Attack/release time in milliseconds for the envelope shape.</param>
        /// <param name="markVoice">
        /// Optional spectrum for mark elements (#145, audio outputs). Null — the
        /// default — is a single pure sine, which is what CW notifications have
        /// always sounded like. Only the voice's PARTIALS, brightness and
        /// inharmonicity are meaningful here: the keying envelope belongs to the
        /// renderer, because it is what stops the tone clicking, and a voice's
        /// gating would chop a 60 ms dit into fragments. Non-audio outputs
        /// (haptic, visual) ignore this exactly as they ignore sidetoneHz.
        /// </param>
        /// <param name="ct">Cancels mid-sequence playback.</param>
        /// <param name="protectedFromClose">
        /// True for the short, named exempt list of #182 — the session
        /// prosigns and the SK farewell. A protected sequence is never
        /// dropped from the pending queue and never soft-closed by
        /// <see cref="CloseForNewMessage"/>; it plays to completion. The
        /// operator's Ctrl interrupt (<see cref="Cancel"/>) still stops it —
        /// their silence command outranks etiquette.
        /// </param>
        Task PlayElementsAsync(
            IReadOnlyList<CwElement> elements,
            int sidetoneHz,
            float volume,
            int riseFallMs,
            MeterVoice? markVoice,
            CancellationToken ct,
            bool protectedFromClose = false);

        /// <summary>Cancel any in-flight sequence immediately.</summary>
        void Cancel();

        /// <summary>
        /// #182, Noel's ruling: notifications CLOSE, they do not queue. A new
        /// notification supersedes the pending one — drop every unprotected
        /// sequence still waiting in the queue, and ask the in-flight one (if
        /// unprotected) to yield at its next character boundary rather than
        /// mid-symbol. The caller then enqueues the new message normally, so
        /// arrowing across four slices sends the fourth, not four.
        /// </summary>
        /// <returns>
        /// True when anything was actually superseded — a pending sequence
        /// dropped or an in-flight one asked to yield. False when the channel
        /// was idle or held only protected sequences, so callers can avoid
        /// recording a close that closed nothing.
        /// </returns>
        bool CloseForNewMessage();
    }
}
