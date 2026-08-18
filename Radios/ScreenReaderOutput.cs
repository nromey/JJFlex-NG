using System;
using System.Collections.Generic;
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
        /// <summary>
        /// Spoken only at Diagnostic: plumbing an operator does not normally
        /// want narrated - which account a session used, which discovery path
        /// answered, what a background task is doing.
        ///
        /// **Deliberately outside the Ctrl+Shift+V cycle.** That key rotates
        /// Chatty, Terse and Off, and nobody should land here by pressing it
        /// one time too many. It is opt-in from Settings, for a tester chasing
        /// something specific.
        ///
        /// Detail that belongs here is detail that would otherwise be deleted:
        /// the choice is not "say it or lose it" but "say it to whoever asked".
        /// </summary>
        Diagnostic = 3,
    }

    /// <summary>
    /// Helper class for screen reader output. The backend (Prism) is brought up
    /// by ScreenReaderFactory; policy above it - verbosity, suppression,
    /// last-message history - is backend-neutral.
    /// Provides a simple interface to speak messages through NVDA, JAWS, or SAPI.
    /// </summary>
    public static class ScreenReaderOutput
    {
        private static bool _initialized;
        private static bool _available;
        private static string _screenReaderName;
        private static string _lastMessage;

        /// <summary>
        /// The live speech backend, chosen by
        /// <see cref="Speech.ScreenReaderFactory"/>. Null until
        /// <see cref="Initialize"/> runs; every call below null-guards rather
        /// than assuming, because speech is attempted from radio events that
        /// can arrive before or after startup completes.
        /// </summary>
        private static Speech.IScreenReader _backend;

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
                // Backend selection lives in the factory. Everything above this
                // line - the verbosity gate, SuppressSpeech, the last-message
                // history - is backend-neutral policy and stays here.
                _backend = Speech.ScreenReaderFactory.Create();
                _available = _backend.HasSpeech;
                _screenReaderName = _backend.DetectedReader;

                Tracing.TraceLine(
                    $"ScreenReaderOutput: {_backend.BackendName} backend, reader "
                    + $"{_screenReaderName ?? "none detected"}, speech={_available}, "
                    + $"braille={_backend.HasBraille}",
                    _available ? TraceLevel.Info : TraceLevel.Warning);
            }
            catch (Exception ex)
            {
                // The factory is written not to throw, so reaching here means
                // something unexpected. Stay silent rather than crash - the app
                // is still usable by a sighted helper, and a crash at startup is
                // not.
                _available = false;
                Tracing.TraceLine($"ScreenReaderOutput: Failed to initialize - {ex.Message}", TraceLevel.Error);
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

        // ── Intent-based speech ───────────────────────────────────────────
        //
        // The bool overloads below remain and now MAP onto these, so the whole
        // application keeps working while call sites migrate deliberately.
        // Mapping is deliberately asymmetric and documented at the overload.

        /// <summary>How long a Latest utterance waits to be superseded.</summary>
        ///
        /// Long enough to swallow a key-repeat burst (Windows repeats at
        /// roughly 30 a second, so ~33 ms apart), short enough that a single
        /// deliberate press does not feel laggy. Riding a control emits one
        /// utterance per settle, not one per step.
        private const int CoalesceMs = 120;

        private sealed class PendingUtterance
        {
            public string Message = string.Empty;
            public VerbosityLevel Level;
            public System.Threading.Timer? Timer;
        }

        private static readonly Dictionary<string, PendingUtterance> _pending =
            new Dictionary<string, PendingUtterance>(StringComparer.Ordinal);
        private static readonly object _pendingLock = new object();

        /// <summary>
        /// Speak with an explicit intent. This is the form new code should use.
        /// </summary>
        /// <param name="message">What to say.</param>
        /// <param name="intent">What KIND of utterance this is.</param>
        /// <param name="level">Verbosity gate; Critical is always spoken.</param>
        /// <param name="coalesceKey">
        /// Required for <see cref="Speech.SpeechIntent.Latest"/>: utterances
        /// sharing a key replace one another while pending. Ignored otherwise.
        /// A Latest call with no key cannot coalesce against anything, so it
        /// degrades to Interrupt rather than silently pretending to work.
        /// </param>
        public static void Speak(
            string message,
            Speech.SpeechIntent intent,
            VerbosityLevel level = VerbosityLevel.Terse,
            string? coalesceKey = null)
        {
            if (string.IsNullOrEmpty(message)) return;
            if ((int)level > (int)CurrentVerbosity) return;

            switch (intent)
            {
                case Speech.SpeechIntent.Urgent:
                    // Cut what is speaking AND drop what is queued, so nothing
                    // stale can play on top of a transmit warning.
                    DiscardPending();
                    try { _backend?.Silence(); } catch { }
                    Emit(message, interrupt: true);
                    return;

                case Speech.SpeechIntent.Latest:
                    if (string.IsNullOrEmpty(coalesceKey))
                    {
                        Tracing.TraceLine(
                            $"ScreenReaderOutput: Latest without a coalesce key - "
                            + $"treating as Interrupt: '{message}'",
                            TraceLevel.Warning);
                        Emit(message, interrupt: true);
                        return;
                    }
                    Coalesce(coalesceKey!, message, level);
                    return;

                case Speech.SpeechIntent.Queue:
                    Emit(message, interrupt: false);
                    return;

                default:
                    Emit(message, interrupt: true);
                    return;
            }
        }

        /// <summary>
        /// Hold this utterance briefly. A newer one with the same key REPLACES
        /// it rather than queueing behind it, so sweeping a control recites the
        /// value it settled on instead of every value it passed through.
        ///
        /// Coalescing has to happen here, before emission: once text reaches a
        /// screen reader we cannot take it back.
        /// </summary>
        private static void Coalesce(string key, string message, VerbosityLevel level)
        {
            lock (_pendingLock)
            {
                if (_pending.TryGetValue(key, out var existing))
                {
                    // Same key already waiting - overwrite the words and let the
                    // running timer carry on. Restarting it on every step would
                    // let a continuous sweep defer the announcement forever.
                    existing.Message = message;
                    existing.Level = level;
                    return;
                }

                var entry = new PendingUtterance { Message = message, Level = level };
                _pending[key] = entry;
                entry.Timer = new System.Threading.Timer(
                    _ => FlushCoalesced(key), null, CoalesceMs, System.Threading.Timeout.Infinite);
            }
        }

        private static void FlushCoalesced(string key)
        {
            PendingUtterance? entry;
            lock (_pendingLock)
            {
                if (!_pending.TryGetValue(key, out entry)) return;
                _pending.Remove(key);
            }

            entry.Timer?.Dispose();
            if ((int)entry.Level <= (int)CurrentVerbosity)
            {
                Emit(entry.Message, interrupt: true);
            }
        }

        /// <summary>Drop everything waiting to be spoken. Used by Urgent.</summary>
        private static void DiscardPending()
        {
            lock (_pendingLock)
            {
                foreach (var entry in _pending.Values) entry.Timer?.Dispose();
                _pending.Clear();
            }
        }

        /// <summary>
        /// The single point where text actually reaches the backend. Every
        /// intent funnels through here, so suppression, the last-message
        /// history and tracing cannot be bypassed by adding a new intent.
        /// </summary>
        private static void Emit(string message, bool interrupt)
        {
            if (SuppressSpeech) return;

            try
            {
                if (!_initialized) Initialize();
                if (!_available) return;

                _backend?.Speak(message, interrupt);
                _lastMessage = message;
                Tracing.TraceLine(
                    $"ScreenReaderOutput: Spoke '{message}' (interrupt={interrupt})",
                    TraceLevel.Verbose);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: Error speaking - {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Legacy bool form. Retained so 664 call sites keep working while they
        /// migrate to <see cref="Speech.SpeechIntent"/> deliberately, a cluster
        /// at a time, rather than in one unreviewable sweep.
        ///
        /// **Note the mapping is asymmetric on purpose.** true becomes
        /// Interrupt, which is what it always meant. false becomes Queue rather
        /// than "some third thing", because letting the screen reader queue is
        /// exactly what not-interrupting has always done. So this overload
        /// changes NO behaviour; it only renames it.
        /// </summary>
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
                    _backend?.Speak(message, interrupt);
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
            // Three-way on purpose. Diagnostic is NOT in this rotation - see
            // the enum. A tester turns it on in Settings and means it; nobody
            // should arrive there by pressing a hotkey once too often and then
            // wonder why the application started narrating its plumbing.
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
        /// The greeting, spoken once when the application starts.
        ///
        /// Distinct from the arrival announcement at Home, which says where you
        /// landed and in which tuning mode. This one only says the application
        /// is up - a greeting delivered AFTER you have chosen a radio and
        /// connected is describing a moment that passed thirty seconds ago.
        ///
        /// Queued, not interrupting: it is the first utterance of the startup
        /// series, and the connect dialog announcing itself is the second.
        /// Under the old bool this was an interrupt guarded by a 2-second
        /// sleep, which is what a queue looks like when you do not have one.
        /// </summary>
        public static void SpeakGreeting()
        {
            string msg;
            switch (CurrentVerbosity)
            {
                case VerbosityLevel.Chatty:
                    // Chatty is where the version belongs: discoverable without
                    // being recited to everyone at every launch, and the single
                    // most useful thing to have already heard when something
                    // later goes wrong and needs reporting.
                    var version = DiagnosticSnapshot.QuickFileVersion;
                    msg = string.IsNullOrEmpty(version)
                        ? "Welcome to JJ Flexible Radio Access"
                        : "Welcome to JJ Flexible Radio Access, version " + version;
                    break;

                case VerbosityLevel.Terse:
                    // You just launched it. You know what it is.
                    msg = "Welcome";
                    break;

                default:
                    // Critical means speech off for everything but the things
                    // that matter. A greeting is not one of them.
                    return;
            }

            Speak(msg, Speech.SpeechIntent.Queue, VerbosityLevel.Critical);
        }

        /// <summary>
        /// Verbosity-aware "no radio connected" announcement, used when a Radio-scope
        /// keystroke fires without a connected radio. Ensures every Radio-scope key
        /// produces audible feedback in the disconnected state instead of going silent.
        ///
        /// Spoken at Critical level — connection status is the enum's stated example
        /// of an always-spoken message, so even users at "Off" hear a brief form.
        /// Interrupts any current speech so the answer lands immediately.
        ///
        /// When <paramref name="actionLabel"/> is provided (a verb-led short phrase
        /// like "change band"), the announcement names what the user just tried to
        /// do — "Unable to change band, JJ Flexible Home no radio connected" — so
        /// the failure isn't ambiguous when the same key would normally do many
        /// different things depending on radio state.
        /// </summary>
        public static void SpeakNoRadioConnected(string? actionLabel = null)
        {
            string msg;
            if (string.IsNullOrWhiteSpace(actionLabel))
            {
                msg = CurrentVerbosity switch
                {
                    VerbosityLevel.Chatty => "JJ Flexible Home, no radio connected",
                    VerbosityLevel.Terse  => "Home, no radio",
                    _                     => "No radio connected",
                };
            }
            else
            {
                msg = CurrentVerbosity switch
                {
                    VerbosityLevel.Chatty => $"Unable to {actionLabel}, JJ Flexible Home no radio connected",
                    VerbosityLevel.Terse  => $"{actionLabel}, no radio",
                    _                     => $"{actionLabel}, no radio",
                };
            }
            Speak(msg, VerbosityLevel.Critical, true);
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
                    _backend?.Output(message, interrupt);
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
                    _backend?.Silence();
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
                    _backend?.Dispose();
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
                return _available && _backend?.HasBraille == true;
            }
        }

        /// <summary>
        /// Which speech library is actually driving output — "Prism", or "none"
        /// when nothing came up. Reported on the About page and in every crash
        /// report and debug bundle, because on 2026-08-17 a completely
        /// non-functional Prism integration was indistinguishable from a working
        /// one: the fallback caught it, speech carried on, and nothing anywhere
        /// said which library was talking.
        /// </summary>
        public static string BackendName
        {
            get
            {
                if (!_initialized) Initialize();
                return _backend?.BackendName ?? "none";
            }
        }

        /// <summary>
        /// Re-state which speech backend is live, for the trace file.
        ///
        /// Initialize() runs from ApplicationEvents before GetConfigInfo turns
        /// boot tracing on, so its own trace line lands before there is a file
        /// to land in — which meant the single most load-bearing fact about
        /// accessibility (which library is driving the user's ears) appeared in
        /// no trace anyone could send us. Found 2026-08-17 while trying to
        /// confirm Prism had actually loaded and finding nothing at all.
        ///
        /// Called from GetConfigInfo immediately after Tracing.On. Safe to call
        /// more than once; it only reports.
        /// </summary>
        /// <summary>
        /// The kind of channel speech is running on. See
        /// <see cref="Speech.SpeechTier"/> - the tiers are not interchangeable.
        /// </summary>
        public static Speech.SpeechTier Tier =>
            (_backend as Speech.PrismScreenReader)?.Tier ?? Speech.SpeechTier.None;

        /// <summary>
        /// Ask the backend to re-evaluate now that the application owns a
        /// visible window.
        ///
        /// This exists because of an ordering constraint we cannot remove:
        /// speech comes up during startup, before anything is drawn, but the
        /// UI Automation channel REQUIRES a visible top-level window at the
        /// moment it initialises. So the only chance to reach a Narrator user
        /// arrives after the main window is shown.
        ///
        /// A no-op unless we settled for a raw synthesiser - a controller-based
        /// reader is already the better channel and is never displaced.
        /// Returns true when the channel actually changed.
        /// </summary>
        public static bool TryUpgradeChannel()
        {
            if (_backend is not Speech.PrismScreenReader prism) return false;
            if (!prism.TryUpgradeToUia()) return false;

            _available = _backend.HasSpeech;
            _screenReaderName = _backend.DetectedReader;
            return true;
        }

        public static void TraceBackend()
        {
            try
            {
                if (!_initialized) Initialize();
                Tracing.TraceLine(
                    $"Speech: backend={_backend?.BackendName ?? "none"}, "
                    + $"reader={_screenReaderName ?? "none detected"}, "
                    + $"speech={_available}, braille={_backend?.HasBraille == true}",
                    TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Speech: could not report backend - {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Push a line to a connected braille display WITHOUT speaking it.
        ///
        /// Used by the radio status line and the panadapter readout, both of
        /// which update far too often to be spoken — braille is a surface that
        /// gets overwritten, so a display can carry a live value that speech
        /// never could.
        ///
        /// Two callers used to reach past this class and drive the backend
        /// directly, which would have left them on the old one after everything
        /// else moved. Deliberately NOT verbosity-gated:
        /// braille is a passive surface the operator reads when they choose,
        /// not an interruption, so the speech verbosity setting has no bearing.
        /// </summary>
        public static void Braille(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                if (!_initialized) Initialize();
                _backend?.Braille(message);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: Error writing braille - {ex.Message}", TraceLevel.Warning);
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

        /// <summary>
        /// Whether connection progress speech and counting earcons fire while
        /// the connecting modal is up. Default true. Critical-level events
        /// (errors, "cancelled", "timed out") always speak regardless.
        /// Mirrors CwNotificationsEnabled — loaded from AudioOutputConfig at
        /// MainWindow construction so the connecting modal sees the flag the
        /// first time it's shown (auto-connect on startup).
        /// </summary>
        public static bool SpeakConnectionProgressEnabled { get; set; } = true;

        /// <summary>
        /// Set true after PlayCwSK has been invoked once for the current session
        /// (typically by FlexBase.Disconnect on user disconnect). The shutdown
        /// handler in ApplicationEvents checks this and skips its own SK so the
        /// 73 prosign doesn't fire twice on app exit while connected.
        /// Cleared by app startup (default false).
        /// </summary>
        public static bool SkAlreadyPlayedThisSession { get; set; } = false;

        // ── MultiFlex client notifications (Sprint 25 Phase 19) ──

        /// <summary>Play ascending chirp for client connected.</summary>
        public static Action? PlayClientConnectedEarcon { get; set; }

        /// <summary>Play descending chirp for client disconnected.</summary>
        public static Action? PlayClientDisconnectedEarcon { get; set; }
    }
}
