using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JJTrace;
using CrossSpeak;

namespace Radios
{
    /// <summary>
    /// Helper class for screen reader output using CrossSpeak/Tolk.
    /// Provides a simple interface to speak messages through NVDA, JAWS, Narrator, or SAPI.
    /// </summary>
    public static class ScreenReaderOutput
    {
        private static bool _initialized;
        private static bool _available;

        // Timing for speech delays - kept short so user isn't stuck waiting if they silence (Ctrl)
        // We only wait long enough for critical messages to be heard, not to complete fully
        // Average speaking rate ~150 words/min, but we use shorter delays for responsiveness
        private const int MsPerCharacter = 50;  // Shorter than actual speech - responsive over complete
        private const int MinDelayMs = 300;     // Brief pause to let speech start
        private const int MaxDelayMs = 2500;    // Cap so user isn't stuck waiting

        /// <summary>
        /// Initialize the screen reader connection. Call once at app startup.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            try
            {
                CrossSpeakManager.Instance.Initialize();
                _available = true;
                Tracing.TraceLine("ScreenReaderOutput: Initialized successfully", TraceLevel.Info);
            }
            catch (Exception ex)
            {
                _available = false;
                Tracing.TraceLine($"ScreenReaderOutput: Failed to initialize - {ex.Message}", TraceLevel.Warning);
            }

            _initialized = true;
        }

        /// <summary>
        /// Speak a message through the active screen reader.
        /// </summary>
        /// <param name="message">The message to speak</param>
        /// <param name="interrupt">If true, interrupts any current speech (not fully supported)</param>
        public static void Speak(string message, bool interrupt = false)
        {
            if (string.IsNullOrEmpty(message)) return;

            try
            {
                if (!_initialized)
                {
                    Initialize();
                }

                if (_available)
                {
                    CrossSpeakManager.Instance.Speak(message);
                    Tracing.TraceLine($"ScreenReaderOutput: Spoke '{message}'", TraceLevel.Verbose);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: Error speaking - {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Speak a message and wait approximately long enough for it to be spoken.
        /// Use this for important messages that shouldn't be cut off.
        /// </summary>
        /// <param name="message">The message to speak</param>
        public static void SpeakAndWait(string message)
        {
            Speak(message);

            // Estimate how long the message takes to speak
            int delayMs = Math.Max(MinDelayMs, Math.Min(MaxDelayMs, message.Length * MsPerCharacter));
            System.Threading.Thread.Sleep(delayMs);
        }

        /// <summary>
        /// Speak a message and wait asynchronously. Use in async methods.
        /// </summary>
        /// <param name="message">The message to speak</param>
        public static async Task SpeakAndWaitAsync(string message)
        {
            Speak(message);

            // Estimate how long the message takes to speak
            int delayMs = Math.Max(MinDelayMs, Math.Min(MaxDelayMs, message.Length * MsPerCharacter));
            await Task.Delay(delayMs);
        }

        /// <summary>
        /// Clean up resources. Call at app shutdown.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                if (_available)
                {
                    CrossSpeakManager.Instance.Close();
                }
            }
            catch { /* ignore */ }

            _initialized = false;
            _available = false;
        }

        /// <summary>
        /// Gets whether screen reader output is available.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_initialized) Initialize();
                return _available;
            }
        }
    }
}
