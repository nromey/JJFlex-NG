using System;
using System.Collections.Generic;
using System.Globalization;

namespace Radios
{
    /// <summary>
    /// A heartbeat to keep running while a wait runs — the "still" line and the
    /// cadence it repeats at. Handed to <see cref="ProgressVoice"/> by whoever
    /// owns the connecting window; carried as data here so the decision about
    /// WHEN one is armed can be tested without a radio, a window or a voice.
    /// </summary>
    public sealed class ConnectWaitVoice
    {
        /// <summary>Short name of the wait. Traced, never spoken.</summary>
        public string What { get; init; } = "";

        /// <summary>The repeat line at Terse.</summary>
        public string StillTerse { get; init; } = "";

        /// <summary>The repeat line at Chatty.</summary>
        public string StillChatty { get; init; } = "";

        /// <summary>Gap before and between repeats.</summary>
        public int RepeatMs { get; init; } = ProgressVoice.DefaultRepeatMs;

        /// <summary>Hard ceiling on the heartbeat.</summary>
        public int MaxMs { get; init; } = ProgressVoice.DefaultMaxMs;
    }

    /// <summary>
    /// What the connecting window should do about one connection event: change
    /// its label, say something, sound a counting earcon, and arm or stop the
    /// heartbeat that covers the wait it is entering.
    /// </summary>
    public sealed class ConnectNarrationStep
    {
        /// <summary>The phase now entered, or 0 when the phase did not change.</summary>
        public int Phase { get; init; }

        /// <summary>New window text, or null to leave the label alone.</summary>
        public string StatusText { get; init; }

        /// <summary>Whether <see cref="StatusText"/> should also be spoken.</summary>
        public bool Speak { get; init; }

        /// <summary>
        /// A line to speak that is NOT the window text — used where the reason
        /// a connect died must be heard while this window still has focus.
        /// </summary>
        public string SpeakExtra { get; init; }

        /// <summary>Sound the counting earcon for <see cref="Phase"/>.</summary>
        public bool PlayPhaseTone { get; init; }

        /// <summary>Heartbeat to start, or null to leave any running one alone.</summary>
        public ConnectWaitVoice Arm { get; init; }

        /// <summary>Stop any heartbeat: the wait it covered is over.</summary>
        public bool StopVoice { get; init; }

        /// <summary>Nothing to do.</summary>
        public static readonly ConnectNarrationStep Nothing = new ConnectNarrationStep();

        /// <summary>True when this step asks for anything at all.</summary>
        public bool IsEmpty =>
            Phase == 0 && StatusText == null && SpeakExtra == null && Arm == null && !StopVoice;
    }

    /// <summary>
    /// Turns the connection profiler's event stream into what a listener hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists as a separate object.</b> Task #212, measured
    /// 2026-08-26: 12.5 seconds between a connect starting and the operator
    /// giving up on it, with NOTHING spoken in that window. He heard "slice
    /// acquired", then "setting up", and then silence, and killed it with
    /// Alt+F4 — correctly, because from where he sat a slow connect and a dead
    /// one sound identical. The connection had in fact failed; the phase at
    /// cancel was recorded as <c>station_name_wait</c>, eleven seconds in.
    /// </para>
    /// <para>
    /// The phase announcements existed. What did not exist was cover for the
    /// WAITS BETWEEN THEM — and those waits are where all the time goes: up to
    /// 20 seconds for the radio's antenna list, up to 45 for the station name.
    /// A phase line names a moment; only a heartbeat answers "is this still
    /// happening", which is the question an operator with no spinner, no greyed
    /// button and no progress bar is actually asking.
    /// </para>
    /// <para>
    /// <b>Fast connects stay silent, and that is deliberate.</b> A heartbeat is
    /// armed with no opening line — the phase announcement has already named the
    /// work — so the first thing it says arrives one repeat interval in. A LAN
    /// connect settles in about three seconds with sub-second phases, so every
    /// heartbeat it arms is stopped before it ever speaks. Nothing about the
    /// common case changes.
    /// </para>
    /// <para>
    /// <b>It is a model, not a window.</b> The connecting window is WinForms on
    /// its own message pump; a decision made in there can only be checked by
    /// connecting to a radio. Keeping the decision here means the whole spoken
    /// sequence is assertable in <c>Radios.Tests</c> with a fake clock and a
    /// list of event names.
    /// </para>
    /// </remarks>
    public sealed class ConnectNarrator
    {
        /// <summary>
        /// A phase line is only spoken when the PREVIOUS phase lasted at least
        /// this long. Fast LAN connects progress silently.
        /// </summary>
        public const int PhaseAnnounceThresholdMs = 500;

        /// <summary>
        /// How much longer than its own declared budget a wait is assumed to be
        /// able to run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Because a budget in this code is not a duration.</b> The
        /// station-name wait declares 45,000 ms, and implements it as 1,800
        /// turns of a loop that sleeps 25 ms each — so the advertised budget is
        /// a COUNT OF SLEEPS, and every turn also costs whatever the work in it
        /// costs. A field trace of the 2026-08-26 incident measured the same
        /// wait running 55.7 seconds of wall clock: 24% over, and the overshoot
        /// grows with anything that slows a turn down.
        /// </para>
        /// <para>
        /// A heartbeat that stops at the declared figure would therefore fall
        /// silent with ten seconds of the wait still to run — recreating the
        /// exact silence this exists to remove, at the latest and worst possible
        /// moment, when the operator has already been waiting the longest. Half
        /// again covers the measured overshoot twice over and still cannot run
        /// away: the connecting window's own escalation prompt arrives at 60
        /// seconds and its auto-cancel at five minutes.
        /// </para>
        /// </remarks>
        public const double WaitCeilingMargin = 1.5;

        private readonly string _radioName;
        private readonly Func<long> _nowMs;
        private int _phase = 1;
        private long _phaseStartMs;
        private int _startCount;

        public ConnectNarrator(string radioName, Func<long> nowMs = null)
        {
            _radioName = string.IsNullOrWhiteSpace(radioName)
                ? Lexicon.Get("connect.connecting.default_radio_name")
                : radioName;
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            _phaseStartMs = _nowMs();
        }

        /// <summary>The phase last entered. 1 until the radio answers.</summary>
        public int Phase => _phase;

        /// <summary>
        /// The heartbeat to arm the instant the window appears, before any
        /// event has arrived.
        /// </summary>
        /// <remarks>
        /// The connect leg runs before <c>start_begin</c> is ever recorded —
        /// over SmartLink that is a session, a sign-in, a hole punch and a TLS
        /// handshake, and it is entirely outside the phase ladder. It was the
        /// first silence of the evening and nothing covered it.
        /// </remarks>
        public ConnectWaitVoice OpeningVoice() => Reaching();

        /// <summary>
        /// What to do about one connection event. Unknown events return
        /// <see cref="ConnectNarrationStep.Nothing"/>.
        /// </summary>
        /// <param name="data">
        /// The structured payload the profiler recorded with the event, when it
        /// has one. Used for the wait budgets, which the connect layer already
        /// publishes and nothing had ever read.
        /// </param>
        public ConnectNarrationStep OnEvent(string eventName,
                                            IReadOnlyDictionary<string, object> data = null)
        {
            switch (eventName)
            {
                // Start() has begun. The FIRST one changes nothing — the
                // opening heartbeat is already covering this stretch and
                // re-arming would push its first line a whole interval further
                // away. A LATER one is a retry after an aborted attempt, where
                // the ladder genuinely starts again.
                case "start_begin":
                    _startCount++;
                    if (_startCount <= 1) return ConnectNarrationStep.Nothing;
                    _phase = 1;
                    _phaseStartMs = _nowMs();
                    return new ConnectNarrationStep { Arm = Reaching() };

                // The radio has reported its free-slice count. What follows is
                // the antenna-list round trip — under 200 ms on a LAN, five to
                // fifteen seconds over SmartLink, twenty before it gives up.
                case "start_slices_available":
                    return EnterPhase(
                        2,
                        Lexicon.Get("connect.connecting.phase_slice_wait", ("radioName", _radioName)),
                        Waiting());

                // Setup data has landed. The station-name wait starts within
                // microseconds of this, and arms its own heartbeat.
                case "start_antenna_available":
                    return EnterPhase(3, Lexicon.Get("connect.connecting.phase_setup"), null);

                // THE SILENCE FROM THE INCIDENT — measured in the field at 55.7
                // seconds without a word. The event carries its own budgets and
                // has done since it was written; nothing had ever read them.
                case "start_station_name_wait_begin":
                    return new ConnectNarrationStep { Arm = SettingUp(CeilingFor(data, "maxWaitMs")) };

                // The radio answered. Say nothing — whoever asked for the
                // connect is about to announce it by name.
                case "station_name_set":
                    return new ConnectNarrationStep { StopVoice = true };

                // The client registration dropped and the attempt is being
                // wound up for a fresh one. This used to change the window
                // label and nothing else, which for a screen-reader operator is
                // the same as saying nothing: a WinForms label is not read
                // because it changed.
                case "start_early_abort":
                case "start_grace_abort":
                    return new ConnectNarrationStep
                    {
                        StatusText = Lexicon.Get("connect.connecting.retrying"),
                        Speak = true,
                        StopVoice = true
                    };

                // The operator asked to stop. The cancel path speaks its own
                // Critical line; this only stops the heartbeat talking over it.
                case "start_cancelled":
                case "start_cancelled_in_station_wait":
                    return new ConnectNarrationStep
                    {
                        StatusText = Lexicon.Get("connect.connecting.cancelling"),
                        StopVoice = true
                    };

                // The two ways setup dies on its own. SAY THE REASON HERE,
                // while this window still holds focus and the speech queue is
                // stable. The verdict that follows — "Connection failed" and
                // its advice — is spoken by the caller after this window has
                // been asked to close, and a screen reader flushes its queue on
                // a window change, so that sentence may never be heard at all.
                // A reason that lands and a verdict that might is a far better
                // trade than the silence this replaces.
                case "station_name_timeout":
                    return new ConnectNarrationStep
                    {
                        SpeakExtra = Lexicon.Get("connect.connecting.setup_never_finished"),
                        StopVoice = true
                    };

                case "start_connection_lost":
                    return new ConnectNarrationStep
                    {
                        SpeakExtra = Lexicon.Get("connect.connecting.dropped_during_setup"),
                        StopVoice = true
                    };

                default:
                    return ConnectNarrationStep.Nothing;
            }
        }

        private ConnectNarrationStep EnterPhase(int phase, string text, ConnectWaitVoice arm)
        {
            // A phase never runs backwards. The label still catches up, because
            // a repeat of an event we have already passed still describes the
            // truth better than whatever is on screen.
            if (phase <= _phase)
                return new ConnectNarrationStep { StatusText = text, Arm = arm };

            long lasted = _nowMs() - _phaseStartMs;
            _phase = phase;
            _phaseStartMs = _nowMs();

            bool loud = lasted >= PhaseAnnounceThresholdMs;
            return new ConnectNarrationStep
            {
                Phase = phase,
                StatusText = text,
                Speak = loud,
                PlayPhaseTone = loud,
                Arm = arm
            };
        }

        private ConnectWaitVoice Reaching() => new ConnectWaitVoice
        {
            What = "connect: reaching the radio",
            StillTerse = Lexicon.Get("connect.connecting.still_connecting_terse"),
            StillChatty = Lexicon.Get("connect.connecting.still_connecting_chatty", ("radioName", _radioName))
        };

        private ConnectWaitVoice Waiting() => new ConnectWaitVoice
        {
            What = "connect: waiting for the radio's setup data",
            StillTerse = Lexicon.Get("connect.connecting.still_waiting_terse"),
            StillChatty = Lexicon.Get("connect.connecting.still_waiting_chatty", ("radioName", _radioName))
        };

        private ConnectWaitVoice SettingUp(int maxMs = ProgressVoice.DefaultMaxMs) => new ConnectWaitVoice
        {
            What = "connect: waiting for the station name",
            StillTerse = Lexicon.Get("connect.connecting.still_setting_up_terse"),
            StillChatty = Lexicon.Get("connect.connecting.still_setting_up_chatty", ("radioName", _radioName)),
            MaxMs = maxMs
        };

        /// <summary>
        /// The ceiling for a wait that published its own budget, or the default
        /// when it did not.
        /// </summary>
        /// <remarks>
        /// Never SHORTER than the default. A wait declaring a small budget is
        /// still a wait, and shrinking the cover below what every other wait
        /// gets would be a strange way to reward a component for being honest
        /// about its timing.
        /// </remarks>
        private static int CeilingFor(IReadOnlyDictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out object raw) || raw == null)
                return ProgressVoice.DefaultMaxMs;

            int budget;
            try
            {
                budget = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return ProgressVoice.DefaultMaxMs;
            }

            if (budget <= 0) return ProgressVoice.DefaultMaxMs;
            return Math.Max(ProgressVoice.DefaultMaxMs, (int)(budget * WaitCeilingMargin));
        }
    }
}
