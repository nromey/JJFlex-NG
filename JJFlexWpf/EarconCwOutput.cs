using System.Threading.Tasks;

namespace JJFlexWpf
{
    /// <summary>
    /// Speaker-based CW notification output. Plays Morse elements as audio tones
    /// through the EarconPlayer alert channel.
    /// </summary>
    public class EarconCwOutput : ICwNotificationOutput
    {
        public void PlayTone(int frequencyHz, int durationMs, float volume)
        {
            EarconPlayer.PlayTone(frequencyHz, durationMs, volume);
        }

        public Task DelayAsync(int ms)
        {
            return Task.Delay(ms);
        }
    }
}
