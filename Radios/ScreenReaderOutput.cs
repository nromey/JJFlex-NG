using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    // ────────────────────────────────────────────────────────────────
    //  VerbosityLevel — tags each Speak() call with its priority.
    //  Also used as the user's current setting (Critical = Off).
    //  Filtering: message spoken when (int)messageLevel <= (int)CurrentVerbosity.
    // ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Speech verbosity level. Tags each Speak() call with its importance.
    /// Also used as the user's verbosity setting (Critical means "off — critical only").
    /// Sprint 24 Phase 6.
    /// </summary>
    public enum VerbosityLevel
    {
        /// <summary>Always spoken: errors, safety warnings, connection status.</summary>
        Critical = 0,
        /// <summary>Spoken at Terse+Chatty: toggle confirmations, value changes, band/mode.</summary>
        Terse = 1,
        /// <summary>Spoken only at Chatty (default): hints, supplementary context.</summary>
        Chatty = 2,
    }

    /// <summary>
    /// Helper class for screen reader output using Tolk.
    /// Provides a simple interface to speak messages through NVDA, JAWS, or SAPI.
    /// </summary>
    public static class ScreenReaderOutput
    {
        private static bool _initialized;
        private static bool _available;
        private static string _screenReaderName;
        private static string _lastMessage;

        // ── Verbosity engine (Sprint 24 Phase 6) ──

        /// <summary>
        /// Current verbosity setting. Default Chatty = all messages spoken (zero regression).
        /// Critical = off (only safety/error messages). Terse = feature toggles and values.
        /// </summary>
        public static VerbosityLevel CurrentVerbosity { get; set; } = VerbosityLevel.Chatty;

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
                // Enable SAPI fallback for users without a screen reader
                Tolk.TrySAPI(true);

                // Load Tolk and detect screen reader
                Tolk.Load();

                if (Tolk.IsLoaded())
                {
                    _screenReaderName = Tolk.DetectScreenReader();
                    _available = Tolk.HasSpeech();

                    if (_available)
                    {
                        Tracing.TraceLine($"ScreenReaderOutput: Initialized with {_screenReaderName ?? "SAPI"}", TraceLevel.Info);
                    }
                    else
                    {
                        Tracing.TraceLine("ScreenReaderOutput: Loaded but no speech capability", TraceLevel.Warning);
                    }
                }
                else
                {
                    _available = false;
                    Tracing.TraceLine("ScreenReaderOutput: Tolk failed to load", TraceLevel.Warning);
                }
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
        /// <param name="interrupt">If true, interrupts any current speech</param>
        /// <summary>
        /// When true, Speak() calls are silently dropped. Used during menu transitions
        /// to prevent NVDA stutter from focus change events.
        /// </summary>
        public static bool SuppressSpeech { get; set; }

        public static void Speak(string message, bool interrupt = false)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (SuppressSpeech) return;

            try
            {
                if (!_initialized)
                {
                    Initialize();
                }

                if (_available)
                {
                    Tolk.Speak(message, interrupt);
                    _lastMessage = message;
                    Tracing.TraceLine($"ScreenReaderOutput: Spoke '{message}'", TraceLevel.Verbose);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: Error speaking - {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Speak a message through the active screen reader, filtered by verbosity level.
        /// Messages are only spoken if their level is at or below CurrentVerbosity.
        /// Critical messages are always spoken (even at "Off"/Critical setting).
        /// </summary>
        /// <param name="message">The message to speak</param>
        /// <param name="level">Verbosity level — Critical always spoken, Terse at Terse+, Chatty at Chatty only</param>
        /// <param name="interrupt">If true, interrupts any current speech</param>
        public static void Speak(string message, VerbosityLevel level, bool interrupt = false)
        {
            if ((int)level > (int)CurrentVerbosity) return;
            Speak(message, interrupt);
        }

        /// <summary>
        /// Cycle verbosity: Chatty → Terse → Off → Chatty.
        /// Returns a spoken announcement of the new level.
        /// </summary>
        public static VerbosityLevel CycleVerbosity()
        {
            CurrentVerbosity = CurrentVerbosity switch
            {
                VerbosityLevel.Chatty => VerbosityLevel.Terse,
                VerbosityLevel.Terse => VerbosityLevel.Critical,
                _ => VerbosityLevel.Chatty,
            };

            // Always announce the new level (this is Critical-level — user needs to know)
            string label = CurrentVerbosity switch
            {
                VerbosityLevel.Critical => "Speech off",
                VerbosityLevel.Terse => "Speech terse",
                _ => "Speech chatty",
            };
            Speak(label, true);
            return CurrentVerbosity;
        }

        /// <summary>
        /// Output a message through both speech and braille (if available).
        /// </summary>
        /// <param name="message">The message to output</param>
        /// <param name="interrupt">If true, interrupts any current speech</param>
        public static void Output(string message, bool interrupt = false)
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
                    Tolk.Output(message, interrupt);
                    Tracing.TraceLine($"ScreenReaderOutput: Output '{message}'", TraceLevel.Verbose);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: Error outputting - {ex.Message}", TraceLevel.Warning);
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
        /// Stop any current speech.
        /// </summary>
        public static void Silence()
        {
            try
            {
                if (_available)
                {
                    Tolk.Silence();
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Clean up resources. Call at app shutdown.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                if (_initialized)
                {
                    Tolk.Unload();
                }
            }
            catch { /* ignore */ }

            _initialized = false;
            _available = false;
            _screenReaderName = null;
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

        /// <summary>
        /// Gets the name of the detected screen reader, or null if using SAPI fallback.
        /// </summary>
        public static string ScreenReaderName
        {
            get
            {
                if (!_initialized) Initialize();
                return _screenReaderName;
            }
        }

        /// <summary>
        /// Gets the last message that was spoken, for repeat-last-message functionality.
        /// </summary>
        public static string LastMessage => _lastMessage;

        /// <summary>
        /// Gets whether braille output is available.
        /// </summary>
        public static bool HasBraille
        {
            get
            {
                if (!_initialized) Initialize();
                return _available && Tolk.HasBraille();
            }
        }

        // ── CW Morse notifications (Sprint 25 Phase 15) ──
        // Delegates wired by MainWindow to MorseNotifier. FlexBase calls these
        // without taking a dependency on JJFlexWpf.

        /// <summary>Play the AS prosign (wait / connection in progress).</summary>
        public static Func<Task>? PlayCwAS { get; set; }

        /// <summary>Play the BT prosign (break / connected).</summary>
        public static Func<Task>? PlayCwBT { get; set; }

        /// <summary>Play the SK prosign (end of contact / app closing).</summary>
        public static Func<Task>? PlayCwSK { get; set; }

        /// <summary>Play a mode name in CW (e.g., "USB", "CW").</summary>
        public static Func<string, Task>? PlayCwMode { get; set; }

        /// <summary>Whether CW notifications are currently enabled.</summary>
        public static bool CwNotificationsEnabled { get; set; }

        /// <summary>Whether CW mode announcements are enabled (when speech is off).</summary>
        public static bool CwModeAnnounceEnabled { get; set; }
    }
}
