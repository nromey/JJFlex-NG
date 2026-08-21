using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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

            // #171 silent verification channel: with render off, Prism is never
            // loaded - no native DLL, no screen reader hookup, nothing that can
            // sound or steal the operator's reader. The DivertedScreenReader
            // reports HasSpeech=true so every policy layer above the backend
            // runs exactly as production; only the hand-off goes nowhere.
            if (!OutputChannelRecorder.RenderEnabled)
            {
                _backend = new Speech.DivertedScreenReader();
                _available = true;
                _screenReaderName = null;
                _initialized = true;
                Tracing.TraceLine(
                    "ScreenReaderOutput: render disabled - Prism not loaded, speech diverted to transcript",
                    TraceLevel.Info);
                if (OutputChannelRecorder.RecordEnabled)
                {
                    OutputChannelRecorder.RecordSpeechBackend(
                        _backend.BackendName, null, "diverted", true, false);
                }
                return;
            }

            try
            {
                // Backend selection lives in the factory. Everything above this
                // line - the verbosity gate, SuppressSpeech, the last-message
                // history - is backend-neutral policy and stays here.
                _backend = Speech.ScreenReaderFactory.Create();
                _available = _backend.HasSpeech;
                _screenReaderName = _backend.DetectedReader;

                // #167: the channel is no longer fixed at startup. A screen
                // reader that starts (or restarts) later displaces a lower
                // tier, and everything up here that cached backend state has
                // to hear about it - along with the transcript and the
                // operator themself.
                if (_backend is Speech.PrismScreenReader prism)
                    prism.ChannelChanged += OnChannelChanged;

                // The transcript gets the backend and TIER because they are
                // materially different outcomes to assert on: "spoke via NVDA"
                // and "spoke via the fallback synthesiser" sound similar right
                // up until two voices collide - and backend "none" with render
                // on is the app-cannot-talk deployment failure.
                if (OutputChannelRecorder.RecordEnabled)
                {
                    OutputChannelRecorder.RecordSpeechBackend(
                        _backend.BackendName, _screenReaderName, Tier.ToString(),
                        _available, _backend.HasBraille);
                }

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
        /// When true, Speak() calls are not sounded. Since the #171 recorded
        /// channel landed, "suppressed" means DIVERTED, not dropped: when a
        /// transcript is open the message is still recorded (with
        /// <c>suppressed: true</c>) so a test can see it fired. Used during
        /// menu transitions to prevent NVDA stutter from focus change events.
        /// Note this is distinct from FlexBase.SuppressSpeech, an instance
        /// flag that guards call sites BEFORE Speak() - those messages
        /// genuinely never fire (background connection tests) and never reach
        /// the transcript.
        /// </summary>
        public static bool SuppressSpeech { get; set; }

        // ── Intent-based speech ───────────────────────────────────────────
        //
        // The bool overloads below remain and now MAP onto these, so the whole
        // application keeps working while call sites migrate deliberately.
        // Mapping is deliberately asymmetric and documented at the overload.

        /// <summary>Quiet period after the LAST change before a Latest utterance speaks.</summary>
        ///
        /// A debounce, not a throttle - the timer restarts on every new value,
        /// so a sweep speaks once when the operator stops rather than at a
        /// fixed cadence while they are still moving.
        ///
        /// The first attempt got this wrong and it was audible: a fixed 120 ms
        /// window that did NOT restart fired every 120 ms during a hold, each
        /// utterance cutting off the last after about one phoneme. Reported
        /// 2026-08-18 as "r r r r r RF gain 5". The comment justifying it
        /// claimed restarting would "defer the announcement forever" - which
        /// cannot happen, because a sweep ends when a finger comes off a key.
        private const int CoalesceMs = 300;

        /// <summary>
        /// How long after an utterance a key is still considered "sweeping",
        /// so the next value coalesces instead of speaking immediately.
        ///
        /// Must comfortably exceed the Windows key-repeat INITIAL delay, which
        /// defaults to around half a second. That delay is the whole reason a
        /// plain debounce clicks: press and hold, and the gap before the repeat
        /// burst arrives is longer than any debounce short enough to feel
        /// responsive - so the first press speaks, the burst speaks again, and
        /// the second cuts off the first.
        private const int SweepWindowMs = 1200;

        /// <summary>
        /// Minimum spacing between two utterances WE emit for the same key.
        ///
        /// Every Latest emission interrupts, which is right when it supersedes
        /// a stale value and wrong when the thing it interrupts is us. A value
        /// announcement takes roughly a second to speak, so a settle coming due
        /// shortly after the lead cut the lead off mid-word - heard as clicks
        /// and ticks while sweeping.
        ///
        /// Measured from the trace on 2026-08-18: "TX Power 87" at 26.978 s,
        /// "TX Power 86" at 28.256 s. 1.28 seconds apart, against an utterance
        /// about 1.2 seconds long, landing exactly on the tail of the first.
        ///
        /// Tuning the sweep window cannot fix this - the window and the
        /// utterance are the same order of magnitude, so any setting trades
        /// clicks for lag. A floor on the gap fixes it directly: a settle that
        /// comes due too soon WAITS rather than cutting in, and speaks a moment
        /// later with the same information.
        ///
        /// Deliberately a fixed estimate rather than asking the backend whether
        /// it is still speaking: is-speaking is a per-backend feature bit, so a
        /// design that polls it works under one screen reader and silently
        /// stalls under another.
        private const int MinGapMs = 1200;

        // A "speak anyway after N ms" ceiling used to live here, so a long hold
        // got periodic feedback rather than silence. It was REMOVED on
        // 2026-08-18 because it did not work by ear: a value announcement takes
        // longer to speak than the ceiling allowed, so each periodic utterance
        // was cut off by the next one, producing the clicks and ticks the
        // operator reported while sweeping.
        //
        // The choice is between silence during a hold and speech that is
        // audibly chopped. Silence is better: the operator is holding a key
        // deliberately and knows the value is moving, whereas a click carries
        // no information at all and sounds like a fault.
        //
        // If periodic feedback is wanted later it must be SHORT enough to
        // finish - the bare number rather than the whole phrase - not the full
        // announcement fired more often.

        private sealed class PendingUtterance
        {
            public string Message = string.Empty;
            public VerbosityLevel Level;

            /// <summary>See the repeatWhileHeld parameter on Speak.</summary>
            public bool RepeatWhileHeld;
            /// <summary>Call site of the newest value, for the transcript.</summary>
            public string? Origin;
            public System.Threading.Timer? Timer;
        }

        private static readonly Dictionary<string, PendingUtterance> _pending =
            new Dictionary<string, PendingUtterance>(StringComparer.Ordinal);

        /// <summary>Per key: what was last spoken, and when.</summary>
        private static readonly Dictionary<string, (string Message, DateTime At)> _lastByKey =
            new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);

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
        /// <param name="repeatWhileHeld">
        /// Latest only. Normally an identical repeated value is dropped, since
        /// saying the same thing twice tells the operator nothing. Set this
        /// when the repetition IS the information - holding a key against the
        /// end of a range, where "still at minimum" is how you learn you can
        /// stop pressing. Repeats are still spaced by the minimum gap, so they
        /// cannot chop each other.
        /// </param>
        public static void Speak(
            string message,
            Speech.SpeechIntent intent,
            VerbosityLevel level = VerbosityLevel.Terse,
            string? coalesceKey = null,
            bool repeatWhileHeld = false,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            [CallerMemberName] string callerMember = "")
        {
            if (string.IsNullOrEmpty(message)) return;
            string origin = FormatOrigin(callerFile, callerLine, callerMember);
            if ((int)level > (int)CurrentVerbosity)
            {
                // Recorded rather than vanishing: "fired but the verbosity
                // filter dropped it" and "never fired" sound identical (like
                // nothing) and need different fixes.
                RecordGated(message, level, intent, origin);
                return;
            }

            switch (intent)
            {
                case Speech.SpeechIntent.Urgent:
                    // Cut what is speaking AND drop what is queued, so nothing
                    // stale can play on top of a transmit warning.
                    DiscardPending();
                    try { _backend?.Silence(); } catch { }
                    Emit(message, interrupt: true, intent, level, origin);
                    return;

                case Speech.SpeechIntent.Latest:
                    if (string.IsNullOrEmpty(coalesceKey))
                    {
                        Tracing.TraceLine(
                            $"ScreenReaderOutput: Latest without a coalesce key - "
                            + $"treating as Interrupt: '{message}'",
                            TraceLevel.Warning);
                        Emit(message, interrupt: true, intent, level, origin);
                        return;
                    }
                    Coalesce(coalesceKey!, message, level, repeatWhileHeld, origin);
                    return;

                case Speech.SpeechIntent.Queue:
                    Emit(message, interrupt: false, intent, level, origin);
                    return;

                default:
                    Emit(message, interrupt: true, intent, level, origin);
                    return;
            }
        }

        /// <summary>
        /// Lead, then settle.
        ///
        /// The FIRST value for a key speaks immediately, so a single deliberate
        /// press is instant. Anything arriving while that key is still sweeping
        /// is coalesced and spoken once the operator stops.
        ///
        /// **Why not a plain debounce.** Windows key repeat waits about half a
        /// second before the burst begins - longer than any debounce short
        /// enough to feel responsive. So a plain debounce speaks on the first
        /// press, speaks again after the burst, and the second cuts off the
        /// first. That was heard as clicks and ticks while sweeping a value on
        /// 2026-08-18. The tuning code had already solved it this way, by hand,
        /// and worked; this brings the same shape into the shared mechanism
        /// instead of leaving it as a sixth private copy.
        ///
        /// Coalescing has to happen before emission: once text reaches a screen
        /// reader we cannot take it back.
        /// </summary>
        private static void Coalesce(
            string key, string message, VerbosityLevel level, bool repeatWhileHeld = false,
            string? origin = null)
        {
            lock (_pendingLock)
            {
                if (_pending.TryGetValue(key, out var existing))
                {
                    existing.Message = message;
                    existing.Level = level;
                    existing.RepeatWhileHeld = repeatWhileHeld;
                    existing.Origin = origin;

                    // A repeating entry must NOT have its timer pushed out by
                    // each new keypress: the operator is holding the key, so
                    // restarting the wait would defer the announcement for
                    // exactly as long as they need to hear it.
                    if (repeatWhileHeld) return;

                    try
                    {
                        existing.Timer?.Change(CoalesceMs, System.Threading.Timeout.Infinite);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Raced with its own flush; the next value starts a
                        // fresh entry, so there is nothing to repair.
                    }
                    return;
                }

                // Not sweeping: speak now. This is the single deliberate press,
                // and making it wait is the difference between a control that
                // answers and one that feels sticky.
                bool sweeping =
                    _lastByKey.TryGetValue(key, out var last)
                    && (DateTime.UtcNow - last.At).TotalMilliseconds < SweepWindowMs;

                if (!sweeping && RemainingGapMs(key) == 0)
                {
                    _lastByKey[key] = (message, DateTime.UtcNow);
                    Emit(message, interrupt: true, Speech.SpeechIntent.Latest, level, origin);
                    return;
                }

                // Either mid-sweep, or too soon after our own last utterance to
                // lead without clipping it. Both cases coalesce.

                var entry = new PendingUtterance
                {
                    Message = message,
                    Level = level,
                    RepeatWhileHeld = repeatWhileHeld,
                    Origin = origin,
                };
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

                // Nothing new to say. Skipping matters: on a two- or three-step
                // sweep the settle would otherwise arrive while the lead
                // utterance is still speaking and cut it off to repeat a value
                // the operator has already heard.
                if (!entry.RepeatWhileHeld
                    && _lastByKey.TryGetValue(key, out var last)
                    && string.Equals(last.Message, entry.Message, StringComparison.Ordinal))
                {
                    entry.Timer?.Dispose();
                    return;
                }

                // Too soon after our own last utterance for this key: speaking
                // now would cut it off mid-word. Put the entry back and wait
                // out the remainder - the information is unchanged, only its
                // timing moves.
                int wait = RemainingGapMs(key);
                if (wait > 0)
                {
                    _pending[key] = entry;
                    try
                    {
                        entry.Timer?.Change(wait, System.Threading.Timeout.Infinite);
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        _pending.Remove(key);
                        // Fall through and speak; a disposed timer cannot be
                        // rescheduled, and losing the value entirely is worse
                        // than a clipped one.
                    }
                }

                _lastByKey[key] = (entry.Message, DateTime.UtcNow);
            }

            entry.Timer?.Dispose();
            if ((int)entry.Level <= (int)CurrentVerbosity)
            {
                Emit(entry.Message, interrupt: true, Speech.SpeechIntent.Latest, entry.Level, entry.Origin);
            }
            else
            {
                // The verbosity setting moved while this value was pending.
                RecordGated(entry.Message, entry.Level, Speech.SpeechIntent.Latest, entry.Origin);
            }
        }

        /// <summary>
        /// True when speaking for this key right now would cut off our own
        /// previous utterance; the caller should wait out the remainder.
        /// Returns the milliseconds still to wait, or 0 when clear.
        /// </summary>
        private static int RemainingGapMs(string key)
        {
            if (!_lastByKey.TryGetValue(key, out var last)) return 0;
            var elapsed = (DateTime.UtcNow - last.At).TotalMilliseconds;
            var remaining = MinGapMs - elapsed;
            return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
        }

        /// <summary>Drop everything waiting to be spoken. Used by Urgent.</summary>
        private static void DiscardPending()
        {
            lock (_pendingLock)
            {
                foreach (var entry in _pending.Values) entry.Timer?.Dispose();
                _pending.Clear();

                // Forget what was last spoken as well, so the next value after
                // an urgent warning always speaks rather than being suppressed
                // as a duplicate of something the flush just discarded.
                _lastByKey.Clear();
            }
        }

        /// <summary>
        /// The single point where text actually reaches the backend. Every
        /// intent funnels through here, so suppression, the last-message
        /// history and tracing cannot be bypassed by adding a new intent.
        /// </summary>
        private static void Emit(string message, bool interrupt,
            Speech.SpeechIntent? intent = null, VerbosityLevel? level = null, string? origin = null)
        {
            bool suppressed = SuppressSpeech;
            bool rendered = false;

            if (!suppressed)
            {
                try
                {
                    if (!_initialized) Initialize();
                    if (_available)
                    {
                        _backend?.Speak(message, interrupt);
                        Remember(message);
                        // rendered means "actually sounded": with render off the
                        // diverted backend accepted the text and discarded it,
                        // and the transcript must not claim otherwise.
                        rendered = OutputChannelRecorder.RenderEnabled;
                        Tracing.TraceLine(
                            $"ScreenReaderOutput: Spoke '{message}' (interrupt={interrupt})",
                            TraceLevel.Verbose);
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"ScreenReaderOutput: Error speaking - {ex.Message}", TraceLevel.Warning);
                }
            }

            // #171: every utterance is recorded AFTER the render attempt so
            // the rendered flag is truthful. A suppressed message is diverted,
            // not dropped - the transcript shows it fired.
            if (OutputChannelRecorder.RecordEnabled)
            {
                OutputChannelRecorder.RecordSpeech(message, level?.ToString(), intent?.ToString(),
                    interrupt, gated: false, suppressed, rendered, origin);
            }
        }

        /// <summary>Record a verbosity-gated utterance - fired but filtered.</summary>
        private static void RecordGated(string message, VerbosityLevel level,
            Speech.SpeechIntent? intent, string? origin)
        {
            if (!OutputChannelRecorder.RecordEnabled) return;
            OutputChannelRecorder.RecordSpeech(message, level.ToString(), intent?.ToString(),
                interrupt: false, gated: true, suppressed: SuppressSpeech, rendered: false, origin);
        }

        // Compact call-site tag for transcript events, e.g. "FlexBase.cs:1878 Start".
        // Caller-info attributes make this free at runtime - no stack walk.
        private static string FormatOrigin(string callerFile, int callerLine, string callerMember)
        {
            string name;
            try { name = Path.GetFileName(callerFile); }
            catch { name = callerFile; }
            if (string.IsNullOrEmpty(name)) return callerMember;
            return $"{name}:{callerLine} {callerMember}";
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
        public static void Speak(string message, bool interrupt = false,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            [CallerMemberName] string callerMember = "")
        {
            if (string.IsNullOrEmpty(message)) return;
            // This body used to be a verbatim copy of Emit's; delegating keeps
            // behaviour identical and gives the #171 transcript one funnel.
            Emit(message, interrupt, null, null, FormatOrigin(callerFile, callerLine, callerMember));
        }

        /// <summary>
        /// Speak a message through the active screen reader, filtered by verbosity level.
        /// Messages are only spoken if their level is at or below CurrentVerbosity.
        /// Critical messages are always spoken (even at "Off"/Critical setting).
        /// </summary>
        /// <param name="message">The message to speak</param>
        /// <param name="level">Verbosity level — Critical always spoken, Terse at Terse+, Chatty at Chatty only</param>
        /// <param name="interrupt">If true, interrupts any current speech</param>
        public static void Speak(string message, VerbosityLevel level, bool interrupt = false,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            [CallerMemberName] string callerMember = "")
        {
            if (string.IsNullOrEmpty(message)) return;
            string origin = FormatOrigin(callerFile, callerLine, callerMember);
            if ((int)level > (int)CurrentVerbosity)
            {
                RecordGated(message, level, null, origin);
                return;
            }
            Emit(message, interrupt, null, level, origin);
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
        public static void Output(string message, bool interrupt = false,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            [CallerMemberName] string callerMember = "")
        {
            if (string.IsNullOrEmpty(message)) return;

            bool rendered = false;
            try
            {
                if (!_initialized)
                {
                    Initialize();
                }

                if (_available)
                {
                    _backend?.Output(message, interrupt);
                    rendered = OutputChannelRecorder.RenderEnabled;
                    Tracing.TraceLine($"ScreenReaderOutput: Output '{message}'", TraceLevel.Verbose);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: Error outputting - {ex.Message}", TraceLevel.Warning);
            }

            if (OutputChannelRecorder.RecordEnabled)
            {
                OutputChannelRecorder.RecordBrailleOutput(message, interrupt, rendered,
                    FormatOrigin(callerFile, callerLine, callerMember));
            }
        }

        /// <summary>
        /// Speak a message and wait approximately long enough for it to be spoken.
        /// Use this for important messages that shouldn't be cut off.
        /// </summary>
        /// <param name="message">The message to speak</param>
        public static void SpeakAndWait(string message,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            [CallerMemberName] string callerMember = "")
        {
            // Caller info passed through explicitly, or the transcript would
            // stamp every SpeakAndWait utterance as originating here.
            Speak(message, false, callerFile, callerLine, callerMember);

            // With render off there is nothing to wait for - the settle window
            // is exactly the per-press cost the silent channel exists to kill.
            if (!OutputChannelRecorder.RenderEnabled) return;

            // Estimate how long the message takes to speak
            int delayMs = Math.Max(MinDelayMs, Math.Min(MaxDelayMs, message.Length * MsPerCharacter));
            System.Threading.Thread.Sleep(delayMs);
        }

        /// <summary>
        /// Speak a message and wait asynchronously. Use in async methods.
        /// </summary>
        /// <param name="message">The message to speak</param>
        public static async Task SpeakAndWaitAsync(string message,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            [CallerMemberName] string callerMember = "")
        {
            Speak(message, false, callerFile, callerLine, callerMember);

            // Same rule as SpeakAndWait: no render, no settle window.
            if (!OutputChannelRecorder.RenderEnabled) return;

            // Estimate how long the message takes to speak
            int delayMs = Math.Max(MinDelayMs, Math.Min(MaxDelayMs, message.Length * MsPerCharacter));
            await Task.Delay(delayMs);
        }

        /// <summary>
        /// Stop any current speech.
        /// </summary>
        public static void Silence(
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            [CallerMemberName] string callerMember = "")
        {
            try
            {
                if (_available)
                {
                    _backend?.Silence();
                }
            }
            catch { /* ignore */ }

            // Recorded because an explicit silence is a cutoff: a transcript
            // reader chasing "it stopped mid-sentence" needs this line.
            if (OutputChannelRecorder.RecordEnabled)
            {
                OutputChannelRecorder.RecordSilence(FormatOrigin(callerFile, callerLine, callerMember));
            }
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

        // ══ Recent speech history (#70, Sprint 32 Track H) ══
        //
        // Repeat-last-message could only ever repeat ONE message, and by the
        // time an operator reaches for it the thing they wanted has usually
        // been overwritten: connect alone emits a short series, and any key
        // press between hearing something and asking for it again destroys it.
        // A single-slot memory is exactly one press too shallow to be useful.
        //
        // The coalescer's _lastByKey looks like history and is not: it is
        // per-key dedup state, it holds only the newest value per key, and
        // DiscardPending clears the whole thing on any urgent flush — which is
        // to say it is emptied precisely when something worth re-reading has
        // just happened.
        //
        // So this is a proper ring: the last ten distinct utterances, newest
        // first, recorded at the single point where text actually reaches the
        // backend so no new intent can bypass it.

        /// <summary>How many past utterances are kept.</summary>
        private const int HistoryDepth = 10;

        /// <summary>
        /// How long the operator has to press again before the walk restarts at
        /// the newest message. Long enough to hear a short utterance and decide
        /// to keep going, short enough that coming back later starts from the
        /// present rather than from wherever they stopped.
        /// </summary>
        private const int HistoryWalkResetMs = 6000;

        private static readonly List<string> _history = new List<string>(HistoryDepth);
        private static readonly object _historyLock = new object();
        private static int _historyCursor = -1;
        private static DateTime _lastWalkAt = DateTime.MinValue;

        /// <summary>
        /// True while a replay is being emitted, so the replay does not enter
        /// the history it is reading from. Without this, pressing repeat twice
        /// would fill the ring with copies of one message.
        /// </summary>
        [ThreadStatic] private static bool _replaying;

        private static void Remember(string message)
        {
            _lastMessage = message;
            if (_replaying) return;
            if (string.IsNullOrEmpty(message)) return;

            lock (_historyLock)
            {
                // A value spoken twice running tells the operator nothing the
                // second time and would push something useful off the end.
                if (_history.Count > 0 && string.Equals(_history[0], message, StringComparison.Ordinal))
                    return;

                _history.Insert(0, message);
                if (_history.Count > HistoryDepth) _history.RemoveAt(_history.Count - 1);

                // Anything newly spoken makes a walk in progress stale.
                _historyCursor = -1;
            }
        }

        /// <summary>
        /// The recent utterances, newest first. A snapshot — safe to enumerate.
        /// </summary>
        public static IReadOnlyList<string> RecentMessages
        {
            get { lock (_historyLock) { return _history.ToArray(); } }
        }

        /// <summary>
        /// Speak the next message back through the history.
        ///
        /// The first press after a pause says the most recent thing; pressing
        /// again promptly steps further back, and running off the oldest wraps
        /// to the newest. Wrapping rather than stopping is deliberate: a silent
        /// dead end is indistinguishable from a broken key, and announcing the
        /// end would need wording for a state the operator can already feel
        /// when the newest message comes round again.
        /// </summary>
        /// <returns>False when there is nothing recorded yet.</returns>
        public static bool RepeatRecent()
        {
            string message;
            lock (_historyLock)
            {
                if (_history.Count == 0) return false;

                bool stale = (DateTime.UtcNow - _lastWalkAt).TotalMilliseconds > HistoryWalkResetMs;
                if (stale || _historyCursor < 0) _historyCursor = 0;
                else _historyCursor = (_historyCursor + 1) % _history.Count;

                _lastWalkAt = DateTime.UtcNow;
                message = _history[_historyCursor];
            }

            // Critical, and past the verbosity gate on purpose: the operator
            // asked for this one by pressing a key, so the setting that governs
            // how much the application volunteers has no bearing on it.
            _replaying = true;
            try { Emit(message, interrupt: true); }
            finally { _replaying = false; }
            return true;
        }

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
            // State refresh, transcript event and any announcement all happen
            // in OnChannelChanged, which the upgrade raises on success — one
            // path for every way the channel can move (#167).
            return prism.TryUpgradeToUia();
        }

        /// <summary>
        /// The backend moved to a different channel after startup — the UIA
        /// upgrade once the main window exists, or a controller reader (NVDA,
        /// JAWS) arriving late or coming back from a restart (#167). May be
        /// called from a worker thread.
        /// </summary>
        private static void OnChannelChanged(Speech.SpeechTier tier, string? reader)
        {
            _available = _backend?.HasSpeech == true;
            _screenReaderName = _backend?.DetectedReader;

            // The transcript gets a second speech-backend event: the tier
            // moving from Synthesiser to ScreenReader IS the assertion the
            // recovery test makes, and it needs no ears.
            if (OutputChannelRecorder.RecordEnabled)
            {
                OutputChannelRecorder.RecordSpeechBackend(
                    _backend?.BackendName ?? "none", _screenReaderName, tier.ToString(),
                    _available, _backend?.HasBraille == true);
            }

            Tracing.TraceLine(
                $"Speech channel changed: tier={tier}, reader={_screenReaderName ?? "none detected"}, "
                + $"speech={_available}, braille={_backend?.HasBraille == true}",
                TraceLevel.Info);

            // Announce ONLY the climb onto the operator's own reader, once and
            // quietly (queued, Terse). Until this moment they were hearing a
            // strange voice with no explanation; this line is spoken by the
            // reader they configured, in their own voice, which is itself most
            // of the message. The UIA upgrade stays silent - it happens at
            // startup before anything worth interrupting, and "your reader now
            // speaks our text" is not something an operator needs narrated.
            if (tier == Speech.SpeechTier.ScreenReader)
            {
                string name = string.IsNullOrEmpty(_screenReaderName) ? "your screen reader" : _screenReaderName;
                Speak($"Speech now through {name}", Speech.SpeechIntent.Queue, VerbosityLevel.Terse);
            }
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
        /// <remarks>
        /// Sprint 32 Track H left this in place but stopped calling it. The
        /// slice vocabulary it served was replaced (see
        /// <see cref="PlayCwText"/>), and removing a delegate that a second
        /// track might be wiring is exactly the invisible cross-track
        /// dependency this sprint is trying not to repeat. It should be retired
        /// after the merge train, not during it.
        /// </remarks>
        public static Func<string, Task>? PlayCwMode { get; set; }

        /// <summary>
        /// Play arbitrary text in CW — letters, digits and the stroke.
        ///
        /// Added Sprint 32 Track H (#58) for the slice vocabulary Noel
        /// specified: a census of "&lt;used&gt;/&lt;total&gt;" when the slice set
        /// changes, and "SL &lt;letter&gt; &lt;mode&gt;" when the operator moves
        /// to another slice or changes a mode. Both are text, not modes, which
        /// is why <see cref="PlayCwMode"/> could not carry them honestly.
        /// </summary>
        public static Func<string, Task>? PlayCwText { get; set; }

        /// <summary>
        /// The connected radio's CW sidetone pitch in hertz, or null when there
        /// is no radio to have one (#146).
        ///
        /// Pushed from FlexBase on the radio's CWPitch property change and on
        /// every connect and disconnect, and consumed by MainWindow, which
        /// hands it to the notifier. Same inversion as the Play delegates: the
        /// radio layer sits below JJFlexWpf and announces the fact rather than
        /// knowing who wants it.
        ///
        /// Null is a NORMAL value, not an error. The notifier falls back to the
        /// operator's configured tone and says nothing about it.
        /// </summary>
        public static Action<int?>? RadioCwPitchChanged { get; set; }

        /// <summary>
        /// Drop whatever CW is currently keying and flush anything queued
        /// behind it. Wired to <c>MorseNotifier.Cancel</c> by MainWindow, for
        /// the same reason as the Play delegates: FlexBase and this class sit
        /// below JJFlexWpf and cannot see it.
        ///
        /// It reaches the CW output ONLY. A continuous earcon — ATU progress is
        /// the live example — is a separate input on the alert mixer and is not
        /// touched. That was worth checking rather than assuming, because the
        /// two share an audio channel and Noel confirmed on 2026-08-20 that
        /// CW-over-ATU-tone is a combination that really happens.
        /// </summary>
        public static Action? CancelCw { get; set; }

        // ══ Recent CW history (#153, Sprint 33 Track F) ══
        //
        // The speech repeat above walks the last ten things SPOKEN. This walks
        // the last ten things SENT AS CW, and they are deliberately separate
        // rings rather than one: an operator running with speech off and CW
        // notifications on has a CW history and no speech history, and merging
        // them would mean pressing repeat to hear a message that was never
        // rendered in the mode they are listening in.
        //
        // WHAT GOES IN: text messages only — the slice census ("3/4") and the
        // slice vocabulary ("SL A USB"). Noel's ruling, 2026-08-20.
        //
        // WHAT STAYS OUT: the AS / BT / SK prosigns. They are punctuation —
        // wait, connected, closing — and re-sending "closing" out of context
        // tells an operator nothing they can act on, while filling the ring
        // with entries that push real information off the end.

        /// <summary>How many past CW messages are kept.</summary>
        private const int CwHistoryDepth = 10;

        /// <summary>
        /// How long after CW playback FINISHES a second press still means "step
        /// further back" rather than "start again at the newest".
        ///
        /// Note the word FINISHES, and note that this is the same 6000 ms the
        /// speech walk uses while meaning something quite different by it. The
        /// speech window is measured from the press, which is fine because
        /// speech is fast. Measured the same way, CW would be broken for
        /// exactly the operators most likely to want this: at 20 WPM "SL A USB"
        /// runs about 4.4 seconds, and at the 10 WPM floor the app allows it
        /// runs about 8.9 — past the window before the message has even
        /// finished playing. The operator would press twice and hear the newest
        /// message again, and the walk would look broken.
        ///
        /// Deriving the window from SpeedWpm would also have worked and was
        /// rejected: it computes an answer the audio path can simply be asked
        /// for. The Play delegate's Task already resolves when the sequence has
        /// drained through the device, so the end of playback is an OBSERVED
        /// instant, and observed beats computed every time — the same argument
        /// EarconCwOutput.WaitForDrain makes at length about why a computed
        /// duration cut the exit farewell short.
        /// </summary>
        private const int CwHistoryWalkResetMs = 6000;

        private static readonly List<string> _cwHistory = new List<string>(CwHistoryDepth);
        private static readonly object _cwHistoryLock = new object();
        private static int _cwHistoryCursor = -1;
        private static DateTime _cwWalkEndedAt = DateTime.MinValue;
        private static bool _cwWalkPlaying;

        /// <summary>
        /// Which replay is current. A press that interrupts an in-flight replay
        /// leaves the old one's continuation still to run, and without this it
        /// would land after the new replay started and clear the "still
        /// playing" flag underneath it — turning the very next press into a
        /// restart. Cheap to fix, invisible to test for.
        /// </summary>
        private static long _cwWalkGeneration;

        /// <summary>
        /// Send text as CW and remember it.
        ///
        /// This is the single point where CW text reaches the notifier, which is
        /// why the history is recorded here rather than at the callers: a future
        /// caller gets into the walk by using this method, and cannot forget to.
        /// Prosign playback deliberately does not come through here.
        /// </summary>
        /// <returns>A Task that resolves when the CW has finished playing, or
        /// immediately when nothing was sent.</returns>
        public static Task SendCwText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
            var play = PlayCwText;
            if (play == null) return Task.CompletedTask;

            RememberCw(text);
            return play(text);
        }

        private static void RememberCw(string message)
        {
            lock (_cwHistoryLock)
            {
                // The census fires on every slice-set change and often repeats
                // the same fraction. Sending it again is right — it is a status
                // ping — but storing it again would push something useful off
                // the end of a ten-deep ring to say nothing new.
                if (_cwHistory.Count > 0 &&
                    string.Equals(_cwHistory[0], message, StringComparison.Ordinal))
                    return;

                _cwHistory.Insert(0, message);
                if (_cwHistory.Count > CwHistoryDepth) _cwHistory.RemoveAt(_cwHistory.Count - 1);

                // Anything newly sent makes a walk in progress stale.
                _cwHistoryCursor = -1;
            }
        }

        /// <summary>The recent CW messages, newest first. A snapshot.</summary>
        public static IReadOnlyList<string> RecentCwMessages
        {
            get { lock (_cwHistoryLock) { return _cwHistory.ToArray(); } }
        }

        /// <summary>
        /// Re-send the next message back through the CW history.
        ///
        /// First press after a pause sends the most recent; pressing again
        /// before the reset window expires steps further back; running off the
        /// oldest wraps to the newest — the same shape as the speech walk, so
        /// an operator who knows one knows the other.
        ///
        /// The in-flight sequence is cancelled first, exactly as the speech
        /// repeat emits with interrupt. Without it, walking back would queue
        /// rather than interrupt, and a second press would mean waiting out the
        /// first message before hearing the second — which at 10 WPM is most of
        /// ten seconds and reads as the key having done nothing.
        /// </summary>
        /// <returns>False when nothing has been sent as CW yet.</returns>
        public static bool RepeatRecentCw()
        {
            if (PlayCwText == null) return false;

            string message;
            lock (_cwHistoryLock)
            {
                if (_cwHistory.Count == 0) return false;

                // A replay that is still playing is not stale by definition —
                // no time has elapsed since it ended, because it has not.
                bool stale = !_cwWalkPlaying &&
                    (DateTime.UtcNow - _cwWalkEndedAt).TotalMilliseconds > CwHistoryWalkResetMs;

                if (stale || _cwHistoryCursor < 0) _cwHistoryCursor = 0;
                else _cwHistoryCursor = (_cwHistoryCursor + 1) % _cwHistory.Count;

                message = _cwHistory[_cwHistoryCursor];
                _cwWalkPlaying = true;
            }

            try { CancelCw?.Invoke(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: CW cancel before repeat failed - {ex.Message}",
                    TraceLevel.Warning);
            }

            _ = ReplayCw(message);
            return true;
        }

        /// <summary>
        /// Play one history entry and stamp when it finished.
        ///
        /// It calls the delegate directly rather than <see cref="SendCwText"/>,
        /// which is what keeps a replay out of the history it is reading from —
        /// no re-entrancy flag needed, because the recording lives in the other
        /// method rather than in the play path.
        /// </summary>
        private static async Task ReplayCw(string message)
        {
            long gen = System.Threading.Interlocked.Increment(ref _cwWalkGeneration);
            try
            {
                var play = PlayCwText;
                if (play != null) await play(message).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* interrupted by the next press */ }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ScreenReaderOutput: CW repeat playback failed - {ex.Message}",
                    TraceLevel.Warning);
            }
            finally
            {
                // Only the newest replay owns the clock. An older one finishing
                // late must not tell the walk that the current message ended.
                // Under the same lock the walk reads them, so a press landing
                // at this instant sees one consistent pair rather than a
                // half-updated one.
                lock (_cwHistoryLock)
                {
                    if (System.Threading.Interlocked.Read(ref _cwWalkGeneration) == gen)
                    {
                        _cwWalkEndedAt = DateTime.UtcNow;
                        _cwWalkPlaying = false;
                    }
                }
            }
        }

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

        // ── Warning alarm (Sprint 31, #111) ──

        /// <summary>
        /// Play the warning alarm — the long harmonic 800 Hz tone that precedes
        /// a spoken warning the operator did not ask for.
        ///
        /// An Action rather than a direct call because Radios sits below
        /// JJFlexWpf in the project graph and cannot see EarconPlayer. Assigned
        /// once at startup in MainWindow, next to the MultiFlex client earcons
        /// that use the same inversion. Null-safe: unassigned means silence,
        /// never a crash, which matters because the caller is on a connect path.
        /// </summary>
        public static Action? PlayWarningAlarmEarcon { get; set; }
    }
}
