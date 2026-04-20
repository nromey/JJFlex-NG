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

        public CwElement(CwElementType type, int durationMs)
        {
            Type = type;
            DurationMs = durationMs;
        }

        public static CwElement Mark(int ms) => new CwElement(CwElementType.Mark, ms);
        public static CwElement Gap(int ms)  => new CwElement(CwElementType.Gap, ms);
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
        /// <param name="ct">Cancels mid-sequence playback.</param>
        Task PlayElementsAsync(
            IReadOnlyList<CwElement> elements,
            int sidetoneHz,
            float volume,
            int riseFallMs,
            CancellationToken ct);

        /// <summary>Cancel any in-flight sequence immediately.</summary>
        void Cancel();
    }
}
