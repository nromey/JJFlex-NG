using System.Threading.Tasks;

namespace JJFlexWpf
{
    /// <summary>
    /// Abstraction for CW notification output — separates the Morse message from
    /// the delivery mechanism. Speaker output uses EarconPlayer tones; future
    /// implementations can drive gamepad vibration or iPhone haptics.
    /// </summary>
    public interface ICwNotificationOutput
    {
        /// <summary>Play a tone at the given frequency and duration.</summary>
        void PlayTone(int frequencyHz, int durationMs, float volume);

        /// <summary>Async delay for element/character/word spacing.</summary>
        Task DelayAsync(int ms);
    }
}
