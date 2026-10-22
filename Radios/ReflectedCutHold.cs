#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using JJTrace;
using Radios.Speech;

namespace Radios
{
    /// <summary>
    /// The hold on keying for a short, fixed window after a reflected-power
    /// cut (#571): a press that would key the transmitter does not key it,
    /// and instead repeats why the transmission was stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> On 2026-10-21 at 20:43 the cut ended a 25 W
    /// transmission into an empty ANT2 and unlocked the transmitter. 149 ms
    /// later Noel's Shift+Space, meant as "unlock", read as "lock" and keyed
    /// 25 W back into the same fault for two more seconds, and the cut's
    /// explanation was cancelled at word 1 of 37. An operator who hears an
    /// alarm and reaches for the lock is very often pressing it as the cut
    /// lands; that is the ordinary case, not the odd one.
    /// </para>
    /// <para>
    /// <b>The rulings this carries, in Noel's words.</b> The hold lasts
    /// <see cref="HoldSeconds"/> seconds, fixed ("agreed with five seconds",
    /// 2026-10-21 21:14), never "until the alarm clears", so it cannot trap
    /// anyone off the air. It applies only while the transmit setup is
    /// unchanged since the cut: "it probably needs to test that nothing has
    /// changed with the transmitter. I don't want to get into a situation where
    /// I can't transmit because an alarm hasn't cleared" (20:47). So a change
    /// to the antenna, the power, the frequency or band, the tuner or the
    /// amplifier lifts the hold at once, and the next press keys as normal.
    /// Nothing re-keys inside the window: the lock key, push to talk, the
    /// system-wide keys, the Transmit button, a voice message, a transmit
    /// check, CW keying, and VOX ("that's fine", "if so, yes that's fine",
    /// 21:15 and 21:16). The tune carrier and an ATU tune are NOT held: "you
    /// should be able to tune right away to rectify the problem" (2026-10-22
    /// 05:42), and a tune is itself a change that lifts the hold. Only the
    /// reflected-power cut starts a hold, whoever owned the transmission — the
    /// controller's own or a transmit check's; the transmit timeout, the ALC
    /// release, the hard kill and a temperature over-run do not (05:14).
    /// </para>
    /// <para>
    /// <b>VOX is paused by switching the radio's own setting off and back
    /// on</b> ("switch off and on silently", 05:37), because VOX is decided
    /// inside the radio and the only lever the app has is the setting.
    /// Transmit inhibit was offered and rejected: it blocks every microphone
    /// and every client, and strands the radio if the app dies mid-window. The
    /// hold records WHICH setting it switched off — <c>SimpleVOXEnable</c> in
    /// the voice modes, <c>CWBreakIn</c> in CW — and puts back exactly that
    /// one, so a mode change inside the window cannot make the restore write
    /// the other (Astra's review, B5); and if the mode changes mid-hold, the
    /// new mode's setting is paused too if it is on. The switch is made as an
    /// APP-INITIATED write, so the operator-changed hook does not fire and the
    /// hold does not lift itself; and it is restored on every exit the app
    /// controls: expiry, an early lift, and the owner going away — including
    /// the normal disconnect, where the restore is written immediately rather
    /// than queued because the command loop is about to stop (B3). If the app
    /// dies mid-window VOX stays off, which is the accepted, safe-direction
    /// failure. The operator is told once, when VOX was on, at their verbosity
    /// and with their sound settings ("tell them/tone based on settings",
    /// 05:38); nothing is said when VOX comes back.
    /// </para>
    /// <para>
    /// <b>An app write's echo is not the operator's repair</b> (Astra's
    /// review, B2). When the application itself writes a snapshot field inside
    /// <c>FlexBase.AppInitiatedSettingChanges</c> — the Audio Check putting
    /// its saved power back after the cut ended its transmission is the
    /// ordinary case — the owner tells the hold through <see cref="AppWrote"/>,
    /// and the hold moves its baseline to the written value. The radio's later
    /// echo then matches the baseline and lifts nothing; a change the operator
    /// makes afterwards still differs and still lifts.
    /// </para>
    /// <para>
    /// <b>Shape.</b> Pure and clock-injected so the window, the signals, the
    /// VOX restore and the speech can all be driven from a test with no radio
    /// and no dispatcher. Every public method takes one lock, and every hold
    /// carries a generation: an end that belongs to an older hold cannot touch
    /// a newer one, whichever thread it arrives on — the controller's timer
    /// ticks on the dispatcher, a transmit check's refusal arrives on the
    /// Fixer's thread, and the kill switch's cut notice arrives on its watcher
    /// thread. The owner reads the transmit setup fresh from the rig and hands
    /// it in; this class only compares. It speaks through
    /// <see cref="ScreenReaderOutput"/> itself, so a test can assert on what
    /// is DELIVERED past the verbosity gate rather than on a call made before
    /// it.
    /// </para>
    /// </remarks>
    public sealed class ReflectedCutHold
    {
        /// <summary>The hold's length. RULED by Noel 2026-10-21 21:14: five seconds.</summary>
        public const int HoldSeconds = 5;

        /// <summary><see cref="HoldSeconds"/> in milliseconds.</summary>
        public const int HoldMs = HoldSeconds * 1000;

        /// <summary>
        /// How often the owner re-reads the transmit setup while holding. A
        /// change is also checked at the moment of a refused press, so this
        /// bounds only how late VOX comes back after a change the operator
        /// made without pressing anything.
        /// </summary>
        public const int SignalPollMs = 250;

        /// <summary>How a hold ended, for the trace and the owner.</summary>
        public enum EndReason
        {
            /// <summary>No hold has ended yet.</summary>
            None,
            /// <summary>The window ran out with the setup unchanged.</summary>
            Expired,
            /// <summary>The transmit setup changed since the cut.</summary>
            Lifted,
            /// <summary>The owner went away (radio closing, controller disposed), or a newer cut replaced this hold.</summary>
            Abandoned,
        }

        /// <summary>
        /// The transmit setup as read from the rig at one instant. Compared
        /// field by field against the one taken at the cut; any difference
        /// lifts the hold, except the mode, which re-pauses VOX instead. Each
        /// field says where it is read from and how fresh it is, because that
        /// is the question the ruling turns on.
        /// </summary>
        public readonly struct TransmitSetup
        {
            /// <summary>
            /// The TX slice's antenna port, live from the vendor library
            /// (<c>FlexBase.TXAntennaName</c> reads the slice's <c>TXAnt</c>).
            /// Changed from the app, the front panel or another client alike.
            /// </summary>
            public string TxAntenna { get; }

            /// <summary>
            /// The TX frequency AS REPORTED by the radio
            /// (<c>FlexBase.TXFrequencyAsReported</c>), not the optimistic
            /// cached request — a safety decision reads what the radio says.
            /// Zero when there is no valid TX slice. A band change moves it.
            /// </summary>
            public ulong TxFrequencyHz { get; }

            /// <summary>
            /// RF power, as the radio last reported it (<c>FlexBase.XmitPower</c>
            /// is written from the radio's <c>RFPower</c> status). Fresh on the
            /// radio's echo of any change, from any route.
            /// </summary>
            public int RfPower { get; }

            /// <summary>
            /// Tune power, as the radio last reported it (<c>FlexBase.TunePower</c>
            /// is written from the radio's <c>TunePower</c> status).
            /// </summary>
            public int TunePower { get; }

            /// <summary>
            /// The tuner is running: the radio's own ATU cycle
            /// (<c>FlexBase.ATUTuneInProgress</c>) or the tune carrier is up
            /// (<c>FlexBase.TxTune</c>), both live from the vendor library and
            /// both already read by the cut for its stand-down-while-tuning
            /// rule. A tune started from the app or the front panel reads the
            /// same.
            /// </summary>
            public bool Tuning { get; }

            /// <summary>
            /// Every external amplifier the radio reports, as "handle=state"
            /// pairs, from <c>AmplifierInventory</c>, which rebuilds on the
            /// vendor library's own amplifier events. Empty when there is none.
            /// Compared by <see cref="AmplifierSignatureChanged"/>, which
            /// ignores the one transition the cut itself causes.
            /// </summary>
            public string AmplifierSignature { get; }

            /// <summary>
            /// The TX slice's mode as the radio reports it
            /// (<c>FlexBase.TXModeAsReported</c>). NOT a change signal: a mode
            /// change clears no antenna fault. It decides which VOX setting is
            /// live, so a change re-pauses VOX for the new mode (B5).
            /// </summary>
            public string TxMode { get; }

            public TransmitSetup(string? txAntenna, ulong txFrequencyHz, int rfPower, int tunePower,
                                 bool tuning, string? amplifierSignature, string? txMode = null)
            {
                TxAntenna = txAntenna ?? "";
                TxFrequencyHz = txFrequencyHz;
                RfPower = rfPower;
                TunePower = tunePower;
                Tuning = tuning;
                AmplifierSignature = amplifierSignature ?? "";
                TxMode = txMode ?? "";
            }

            /// <summary>This setup with one field moved to a value the application wrote (<see cref="AppWrote"/>).</summary>
            public TransmitSetup WithRfPower(int v) => new TransmitSetup(TxAntenna, TxFrequencyHz, v, TunePower, Tuning, AmplifierSignature, TxMode);
            public TransmitSetup WithTunePower(int v) => new TransmitSetup(TxAntenna, TxFrequencyHz, RfPower, v, Tuning, AmplifierSignature, TxMode);
            public TransmitSetup WithTxAntenna(string v) => new TransmitSetup(v, TxFrequencyHz, RfPower, TunePower, Tuning, AmplifierSignature, TxMode);
            public TransmitSetup WithTxMode(string v) => new TransmitSetup(TxAntenna, TxFrequencyHz, RfPower, TunePower, Tuning, AmplifierSignature, v);

            public override string ToString()
                => "antenna " + (TxAntenna.Length == 0 ? "unknown" : TxAntenna)
                 + ", " + TxFrequencyHz + " Hz, power " + RfPower + ", tune power " + TunePower
                 + (TxMode.Length == 0 ? "" : ", " + TxMode)
                 + (Tuning ? ", tuning" : "")
                 + (AmplifierSignature.Length == 0 ? "" : ", amplifiers " + AmplifierSignature);
        }

        /// <summary>The owner's two levers on the radio's autonomous keying, by setting.</summary>
        public sealed class VoxLevers
        {
            /// <summary>Which setting the TX slice's mode keys on, if it is on now; None otherwise.</summary>
            public Func<FlexBase.VoxSetting> CurrentIfOn { get; }

            /// <summary>
            /// Write one setting as the application's act: (which, on, immediate).
            /// Returns true when a write was ISSUED to a present radio.
            /// </summary>
            public Func<FlexBase.VoxSetting, bool, bool, bool> Write { get; }

            /// <summary>The tone beside the VOX-paused note, under the operator's sound switches. Null plays nothing.</summary>
            public Action? PausedTone { get; }

            public VoxLevers(Func<FlexBase.VoxSetting> currentIfOn, Func<FlexBase.VoxSetting, bool, bool, bool> write, Action? pausedTone = null)
            {
                CurrentIfOn = currentIfOn ?? throw new ArgumentNullException(nameof(currentIfOn));
                Write = write ?? throw new ArgumentNullException(nameof(write));
                PausedTone = pausedTone;
            }
        }

        private readonly object _gate = new object();
        private readonly Func<long> _clock;

        private int _generation;
        private bool _holding;
        private long _beganMs;
        private TransmitSetup _atCut;
        private string _reason = "";
        private string _antennaCheck = "";

        private VoxLevers? _vox;
        private Action? _refusalTone;
        private readonly List<FlexBase.VoxSetting> _pausedSettings = new List<FlexBase.VoxSetting>();
        private bool _voxRestoreCancelled;
        private bool _voxNoteGiven;

        /// <summary>Clock in milliseconds; <c>Environment.TickCount64</c> in production.</summary>
        public ReflectedCutHold(Func<long> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>True while a hold is in force. Expiry is noticed lazily by <see cref="Tick"/> and by a refused press.</summary>
        public bool Holding { get { lock (_gate) return _holding; } }

        /// <summary>
        /// Which hold this is: incremented at every <see cref="Begin"/>. An
        /// owner that wired itself to one hold compares this before tearing
        /// anything down on that hold's end.
        /// </summary>
        public int Generation { get { lock (_gate) return _generation; } }

        /// <summary>How many presses this hold refused, across all doors.</summary>
        public int RefusedPresses { get; private set; }

        /// <summary>How the last hold ended, and why, for the report and the trace.</summary>
        public EndReason LastEnd { get; private set; } = EndReason.None;

        /// <summary>The words of the last end: which signal lifted it, or that it expired.</summary>
        public string LastEndDetail { get; private set; } = "";

        /// <summary>The sentence the last refused press spoke, or empty.</summary>
        public string LastRefusalSentence { get; private set; } = "";

        /// <summary>True while this hold has at least one of the radio's VOX settings switched off on the operator's behalf.</summary>
        public bool VoxPaused { get { lock (_gate) return _pausedSettings.Count > 0; } }

        /// <summary>The settings this hold has switched off, in the order it switched them.</summary>
        public IReadOnlyList<FlexBase.VoxSetting> PausedSettings { get { lock (_gate) return _pausedSettings.ToArray(); } }

        /// <summary>
        /// Raised once when a hold ends, by whichever path ended it, with that
        /// hold's generation. VOX has already been restored (or the restore
        /// attempted) when this fires. Raised outside the lock.
        /// </summary>
        public event Action<int, EndReason, string>? Ended;

        // ──────────────────────────────────────────────────────────────────
        //  Beginning
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// A reflected-power cut has just ended a transmission: start the hold.
        /// </summary>
        /// <param name="rung">Which rung cut, so the reason speaks in that rung's unit (#237).</param>
        /// <param name="reading">The reading the cut was judged on.</param>
        /// <param name="antennaName">The TX antenna the cut named, or empty.</param>
        /// <param name="atCut">The transmit setup read at this instant; later reads are compared to it.</param>
        /// <param name="nowMs">The clock now.</param>
        /// <param name="vox">The owner's levers on the radio's VOX settings. Null leaves VOX alone.</param>
        /// <param name="refusalTone">The tone a refused press plays — the cut's own alarm tone. Null plays nothing.</param>
        /// <returns>This hold's generation.</returns>
        public int Begin(TransmitSafety.ReflectedCut rung, in TransmitPowerReading reading, string? antennaName,
                         in TransmitSetup atCut, long nowMs, VoxLevers? vox = null, Action? refusalTone = null)
            => Begin(ReasonFor(rung, reading, antennaName), antennaName, atCut, nowMs, vox, refusalTone);

        /// <summary>The same, from what a transmit check's cut knew (<see cref="ReflectedCutNotice"/>).</summary>
        public int Begin(in ReflectedCutNotice notice, in TransmitSetup atCut, long nowMs, VoxLevers? vox = null, Action? refusalTone = null)
            => Begin(ReasonFor(notice), notice.Antenna, atCut, nowMs, vox, refusalTone);

        private int Begin(string reason, string? antennaName, in TransmitSetup atCut, long nowMs, VoxLevers? vox, Action? refusalTone)
        {
            (int gen, EndReason reason0, string detail0)? ended = null;
            int generation;
            lock (_gate)
            {
                if (_holding)
                    ended = EndHoldLocked(EndReason.Abandoned, "a second cut began before the first hold ended", nowMs, immediate: false);

                _generation++;
                generation = _generation;
                _holding = true;
                _beganMs = nowMs;
                _atCut = atCut;
                _reason = reason;
                _antennaCheck = string.IsNullOrWhiteSpace(antennaName)
                    ? Lexicon.Get("audio.ptt.cut_hold_the_antenna")
                    : antennaName!;
                _vox = vox;
                _refusalTone = refusalTone;
                _pausedSettings.Clear();
                _voxRestoreCancelled = false;
                _voxNoteGiven = false;
                RefusedPresses = 0;
                LastRefusalSentence = "";

                Tracing.TraceLine("CutHold: hold " + generation + " — keying is held for " + HoldSeconds
                    + " s after the reflected-power cut, or until the transmit setup changes; at the cut: " + atCut,
                    TraceLevel.Warning);

                PauseVoxIfOnLocked(firstTime: true);
            }
            RaiseEnded(ended);
            return generation;
        }

        /// <summary>
        /// Switch off the setting the current mode keys on, if it is on and
        /// this hold has not already switched it off. Called at the start and
        /// again when the mode changes inside the window.
        /// </summary>
        private void PauseVoxIfOnLocked(bool firstTime)
        {
            if (_vox == null) return;
            FlexBase.VoxSetting which;
            try { which = _vox.CurrentIfOn(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("CutHold: could not read VOX, so it is left alone — " + ex.Message, TraceLevel.Warning);
                return;
            }
            if (which == FlexBase.VoxSetting.None) return;
            if (_pausedSettings.Contains(which)) return;

            bool issued;
            try { issued = _vox.Write(which, false, false); }
            catch (Exception ex)
            {
                Tracing.TraceLine("CutHold: COULD NOT switch " + which + " off — " + ex.Message, TraceLevel.Error);
                return;
            }
            if (!issued)
            {
                Tracing.TraceLine("CutHold: the " + which + " off write was not issued, so nothing is paused and nothing will be restored",
                                  TraceLevel.Warning);
                return;
            }
            _pausedSettings.Add(which);
            Tracing.TraceLine("CutHold: " + which + " was on, switched off for the hold (app-initiated"
                + (firstTime ? "" : ", after a mode change") + ")", TraceLevel.Info);

            // The one note the operator gets about VOX, at their own verbosity
            // and under their own sound switches (Noel, 2026-10-22 05:38), after
            // the cut sentence, which is Urgent and goes first. Once per hold,
            // however many settings end up paused; nothing is said when they
            // come back.
            if (_voxNoteGiven) return;
            _voxNoteGiven = true;
            try { _vox.PausedTone?.Invoke(); } catch { }
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.ptt.cut_hold_vox_paused", ("seconds", HoldSeconds)),
                SpeechIntent.Queue, VerbosityLevel.Terse,
                subject: SpeechSubject.VoxPausedAfterCut);
        }

        // ──────────────────────────────────────────────────────────────────
        //  Refusing
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// A press that would key the transmitter has arrived at one of the
        /// doors. If the hold is in force, speak why the transmission was
        /// stopped — at the cut's own delivery, Urgent and Critical, so it is
        /// heard at every verbosity, Off included — and return true: the
        /// caller must not key. False when nothing is held, and nothing is
        /// said.
        /// </summary>
        /// <param name="door">Which door, for the trace.</param>
        /// <param name="nowMs">The clock now; expiry is noticed here too.</param>
        /// <param name="stillValid">
        /// The owner's answer to "is the operator still off the air", the same
        /// one the cut sentence carries, so an unheard copy is withdrawn rather
        /// than replayed into a later legitimate transmission (blocker 6).
        /// </param>
        public bool RefusePress(string door, long nowMs, Func<bool>? stillValid)
        {
            (int, EndReason, string)? ended = null;
            string sentence;
            Action? tone;
            lock (_gate)
            {
                if (!_holding) return false;
                if (nowMs - _beganMs >= HoldMs)
                {
                    ended = EndHoldLocked(EndReason.Expired, "the window ran out", nowMs, immediate: false);
                    RaiseEnded(ended);
                    return false;
                }

                RefusedPresses++;
                int remaining = Math.Max(1, (int)Math.Ceiling((HoldMs - (nowMs - _beganMs)) / 1000.0));
                sentence = RefusalSentence(_reason, _antennaCheck, remaining);
                LastRefusalSentence = sentence;
                tone = _refusalTone;

                Tracing.TraceLine("CutHold: REFUSED to key from " + door + " — " + remaining + " s of the hold left; "
                    + "press " + RefusedPresses + " of this hold", TraceLevel.Warning);
            }

            try { tone?.Invoke(); } catch { }
            // Same subject as the cut sentence, on purpose: this IS the cut's
            // explanation, repeated. A copy of the cut sentence that the press
            // interrupted is covered by this fuller one rather than queued
            // behind it, and both are withdrawn by the same answer when the
            // operator does key again.
            ScreenReaderOutput.Speak(sentence, SpeechIntent.Urgent, VerbosityLevel.Critical,
                subject: SpeechSubject.ReflectedPowerCut, stillValid: stillValid);
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        //  The signals
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The owner has re-read the transmit setup: compare it to the one at
        /// the cut, and lift the hold on any difference that could clear the
        /// fault. A changed MODE does not lift; it re-pauses VOX for the new
        /// mode. Also notices expiry. Safe to call when nothing is held.
        /// </summary>
        /// <returns>True when this call ended the hold.</returns>
        public bool Tick(in TransmitSetup now, long nowMs)
        {
            (int, EndReason, string)? ended;
            lock (_gate)
            {
                if (!_holding) return false;
                if (nowMs - _beganMs >= HoldMs)
                {
                    ended = EndHoldLocked(EndReason.Expired, "the window ran out", nowMs, immediate: false);
                }
                else
                {
                    string? changed = WhatChanged(_atCut, now);
                    if (changed == null)
                    {
                        if (!string.Equals(_atCut.TxMode, now.TxMode, StringComparison.Ordinal))
                        {
                            Tracing.TraceLine("CutHold: the mode changed from " + Name(_atCut.TxMode) + " to "
                                + Name(now.TxMode) + " inside the hold — not a change that clears the fault; "
                                + "pausing the new mode's VOX setting if it is on", TraceLevel.Info);
                            _atCut = _atCut.WithTxMode(now.TxMode);
                            PauseVoxIfOnLocked(firstTime: false);
                        }
                        return false;
                    }
                    ended = EndHoldLocked(EndReason.Lifted, changed, nowMs, immediate: false);
                }
            }
            RaiseEnded(ended);
            return true;
        }

        /// <summary>
        /// The first field of <paramref name="now"/> that differs from
        /// <paramref name="atCut"/> in a way that could clear the fault, in the
        /// operator's words, or null when the setup is unchanged. The mode is
        /// deliberately not compared here. Public so the comparison can be
        /// tested on its own and so the report can name each signal.
        /// </summary>
        public static string? WhatChanged(in TransmitSetup atCut, in TransmitSetup now)
        {
            if (!string.Equals(atCut.TxAntenna, now.TxAntenna, StringComparison.Ordinal))
                return "the transmit antenna changed from " + Name(atCut.TxAntenna) + " to " + Name(now.TxAntenna);
            if (atCut.TxFrequencyHz != now.TxFrequencyHz)
                return "the transmit frequency changed from " + atCut.TxFrequencyHz + " to " + now.TxFrequencyHz + " Hz";
            if (atCut.RfPower != now.RfPower)
                return "the RF power changed from " + atCut.RfPower + " to " + now.RfPower;
            if (atCut.TunePower != now.TunePower)
                return "the tune power changed from " + atCut.TunePower + " to " + now.TunePower;
            if (now.Tuning && !atCut.Tuning)
                return "a tune started";
            if (AmplifierSignatureChanged(atCut.AmplifierSignature, now.AmplifierSignature))
                return "the amplifier changed from [" + atCut.AmplifierSignature + "] to [" + now.AmplifierSignature + "]";
            return null;
        }

        private static string Name(string value) => value.Length == 0 ? "unknown" : value;

        /// <summary>
        /// Whether the amplifier picture has changed in a way the OPERATOR
        /// could have made. The cut itself unkeys the radio, and an amplifier
        /// that follows keying then reports transmitting-to-operate on its
        /// own; counting that would lift every hold the instant it began. So a
        /// transmitting state becoming operate is ignored, and anything else —
        /// an amplifier appearing or disappearing, going to standby or fault,
        /// or leaving standby — counts.
        /// </summary>
        /// <remarks>
        /// Signature pairs are "handle=state", separated by ';', in the
        /// inventory's own order. States spelled as <c>AmplifierState</c>
        /// names. The amplifier's antenna mapping and the tuner's bypass state
        /// are NOT in the signature; see the track report. No amplifier has
        /// ever been on the bench (the inventory's own remarks say so), so this
        /// is read from the vendor library's model, not from a measurement.
        /// </remarks>
        public static bool AmplifierSignatureChanged(string atCut, string now)
        {
            if (string.Equals(atCut, now, StringComparison.Ordinal)) return false;
            var before = Parse(atCut);
            var after = Parse(now);
            if (before.Length != after.Length) return true;
            for (int i = 0; i < before.Length; i++)
            {
                if (!string.Equals(before[i].handle, after[i].handle, StringComparison.Ordinal)) return true;
                if (string.Equals(before[i].state, after[i].state, StringComparison.Ordinal)) continue;
                bool wasTransmitting = before[i].state == "TransmitA" || before[i].state == "TransmitB";
                bool nowOperate = after[i].state == "Idle";
                if (wasTransmitting && nowOperate) continue;   // the cut's own unkey, echoed by the amplifier
                return true;
            }
            return false;
        }

        private static (string handle, string state)[] Parse(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return Array.Empty<(string, string)>();
            string[] pairs = signature.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var result = new (string, string)[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                result[i] = eq < 0 ? (pairs[i], "") : (pairs[i].Substring(0, eq), pairs[i].Substring(eq + 1));
            }
            return result;
        }

        /// <summary>
        /// The operator changed a radio setting through the app
        /// (<c>FlexBase.NoteOperatorChangedRadioSetting</c>, which already
        /// tells the operator's writes from the app's own). Lifts the hold for
        /// the settings that could clear an antenna fault: the antenna, the RF
        /// power and the tune power. A VOX change does not lift it — VOX
        /// cannot clear a fault — but it does mean the operator has taken VOX
        /// back into their own hands, so the hold's restore stands down rather
        /// than overriding them.
        /// </summary>
        public void OperatorChanged(string what, long nowMs)
        {
            (int, EndReason, string)? ended = null;
            lock (_gate)
            {
                if (!_holding) return;
                switch (what)
                {
                    case "TXAntennaName":
                    case "TXAntenna":
                        ended = EndHoldLocked(EndReason.Lifted, "you changed the transmit antenna", nowMs, immediate: false);
                        break;
                    case "XmitPower":
                        ended = EndHoldLocked(EndReason.Lifted, "you changed the RF power", nowMs, immediate: false);
                        break;
                    case "TunePower":
                        ended = EndHoldLocked(EndReason.Lifted, "you changed the tune power", nowMs, immediate: false);
                        break;
                    case "Vox":
                        if (_pausedSettings.Count > 0)
                        {
                            _voxRestoreCancelled = true;
                            Tracing.TraceLine("CutHold: you changed VOX during the hold, so the hold will not put it back itself",
                                              TraceLevel.Info);
                        }
                        break;
                }
            }
            RaiseEnded(ended);
        }

        /// <summary>
        /// The APPLICATION wrote a snapshot field on the operator's behalf,
        /// inside <c>FlexBase.AppInitiatedSettingChanges</c> (B2): move the
        /// baseline to the written value, so the radio's echo of that write
        /// is not read as the operator's repair. The Audio Check putting its
        /// saved power back after the cut ended its transmission is the
        /// ordinary case. <paramref name="value"/> is the setter's value as a
        /// string; a field this class does not snapshot is ignored.
        /// </summary>
        public void AppWrote(string what, string value)
        {
            lock (_gate)
            {
                if (!_holding) return;
                switch (what)
                {
                    case "XmitPower":
                        if (int.TryParse(value, out int p)) { _atCut = _atCut.WithRfPower(p); Trace(what, value); }
                        break;
                    case "TunePower":
                        if (int.TryParse(value, out int t)) { _atCut = _atCut.WithTunePower(t); Trace(what, value); }
                        break;
                    case "TXAntennaName":
                        _atCut = _atCut.WithTxAntenna(value ?? ""); Trace(what, value);
                        break;
                }
            }

            static void Trace(string what, string value) =>
                Tracing.TraceLine("CutHold: the app wrote " + what + "=" + value + " during the hold — baseline moved, "
                    + "so its echo is not read as your change", TraceLevel.Info);
        }

        /// <summary>
        /// The owner is going away — radio closing, powered off, controller
        /// disposed. Restores any VOX setting this hold had off, IMMEDIATELY
        /// when asked (the command loop may be stopping), and ends the hold.
        /// </summary>
        /// <param name="why">For the trace.</param>
        /// <param name="nowMs">The clock now.</param>
        /// <param name="immediate">
        /// True writes the restore on this thread rather than through the
        /// command queue — the normal disconnect's order, where
        /// <c>MainWindow.UnwireRadioEvents</c> runs before the rig is disposed
        /// and a queued write might never run (B3).
        /// </param>
        public void Abandon(string why, long nowMs, bool immediate = false)
        {
            (int, EndReason, string)? ended;
            lock (_gate)
            {
                if (!_holding) return;
                ended = EndHoldLocked(EndReason.Abandoned, why, nowMs, immediate);
            }
            RaiseEnded(ended);
        }

        private (int, EndReason, string) EndHoldLocked(EndReason reason, string detail, long nowMs, bool immediate)
        {
            _holding = false;
            LastEnd = reason;
            LastEndDetail = detail;
            long heldMs = nowMs - _beganMs;
            int generation = _generation;

            RestoreVoxLocked(immediate);

            Tracing.TraceLine("CutHold: hold " + generation + " ended after " + heldMs + " ms — " + reason + ": " + detail
                + (RefusedPresses == 0 ? "" : "; " + RefusedPresses + " press(es) refused"), TraceLevel.Info);

            _vox = null;
            _refusalTone = null;
            return (generation, reason, detail);
        }

        private void RestoreVoxLocked(bool immediate)
        {
            if (_pausedSettings.Count == 0) return;
            var toRestore = _pausedSettings.ToArray();
            _pausedSettings.Clear();
            if (_voxRestoreCancelled)
            {
                Tracing.TraceLine("CutHold: VOX left as you set it during the hold (" + string.Join(", ", toRestore) + " not restored)",
                                  TraceLevel.Info);
                return;
            }
            var vox = _vox;
            if (vox == null) return;
            foreach (var which in toRestore)
            {
                try
                {
                    bool issued = vox.Write(which, true, immediate);
                    Tracing.TraceLine(issued
                        ? "CutHold: " + which + " switched back on — write " + (immediate ? "issued immediately" : "queued") + " (app-initiated, nothing said)"
                        : "CutHold: " + which + " restore was NOT issued (no radio, or the command loop is not running); it stays off, the safe direction, and the operator can turn it on by hand",
                        issued ? TraceLevel.Info : TraceLevel.Error);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("CutHold: COULD NOT switch " + which + " back on — " + ex.Message
                        + ". It stays off, the safe direction; the operator can turn it on by hand.", TraceLevel.Error);
                }
            }
        }

        private void RaiseEnded((int gen, EndReason reason, string detail)? ended)
        {
            if (ended == null) return;
            try { Ended?.Invoke(ended.Value.gen, ended.Value.reason, ended.Value.detail); } catch { }
        }

        // ──────────────────────────────────────────────────────────────────
        //  Words
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The reason clause, in the rung's own unit (#237): the share rung
        /// speaks percent and the watts rung speaks watts, never the other's.
        /// Names the antenna when known, "the antenna" when not.
        /// </summary>
        // DRAFT PROSE (#571): awaiting Noel
        public static string ReasonFor(TransmitSafety.ReflectedCut rung, in TransmitPowerReading reading, string? antennaName)
        {
            string antenna = string.IsNullOrWhiteSpace(antennaName)
                ? Lexicon.Get("audio.ptt.cut_hold_the_antenna")
                : antennaName!;
            switch (rung)
            {
                case TransmitSafety.ReflectedCut.Watts:
                    return Lexicon.Get("audio.ptt.cut_hold_reason_watts",
                        ("watts", (int)Math.Round(reading.ReflectedWatts)), ("antenna", antenna));
                case TransmitSafety.ReflectedCut.Share:
                    return Lexicon.Get("audio.ptt.cut_hold_reason_share",
                        ("percent", (int)Math.Round(reading.ReflectedShare * 100f)), ("antenna", antenna));
                default:
                    return Lexicon.Get("audio.ptt.cut_hold_reason_unknown", ("antenna", antenna));
            }
        }

        /// <summary>The reason for a transmit check's cut: the rung's words when it carried a reading, "too much" when it did not.</summary>
        public static string ReasonFor(in ReflectedCutNotice notice)
            => notice.HasReading
                ? ReasonFor(notice.Rung, notice.Reading, notice.Antenna)
                : ReasonFor(TransmitSafety.ReflectedCut.None, TransmitPowerReading.None, notice.Antenna);

        /// <summary>
        /// The sentence a refused press hears: not keyed, the reason, how long
        /// the hold has left, what to check, and that a change keys at once.
        /// </summary>
        // DRAFT PROSE (#571): awaiting Noel
        public static string RefusalSentence(string reason, string antennaCheck, int remainingSeconds)
        {
            return remainingSeconds == 1
                ? Lexicon.Get("audio.ptt.cut_hold_refused_last_second", ("reason", reason), ("antenna", antennaCheck))
                : Lexicon.Get("audio.ptt.cut_hold_refused", ("reason", reason), ("antenna", antennaCheck),
                              ("seconds", remainingSeconds));
        }

        // ──────────────────────────────────────────────────────────────────
        //  Reading the rig
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The transmit setup as the rig reports it now. Every read is wrapped,
        /// because a rig mid-teardown throws from its getters and a hold must
        /// not take the controller down with it; an unreadable field reads as
        /// its default, which compares as "changed" against a real value at the
        /// cut — the direction Noel's worry points (fail open, disclosed in the
        /// report).
        /// </summary>
        public static TransmitSetup Snapshot(FlexBase rig)
        {
            string antenna = "";
            ulong freq = 0;
            int power = 0;
            int tunePower = 0;
            bool tuning = false;
            string amps = "";
            string mode = "";
            try { antenna = rig.TXAntennaName ?? ""; } catch { }
            try { freq = rig.TXFrequencyAsReported; } catch { }
            try { power = rig.XmitPower; } catch { }
            try { tunePower = rig.TunePower; } catch { }
            try { tuning = rig.ATUTuneInProgress || rig.TxTune; } catch { }
            try { amps = AmplifierSignature(rig.Amplifiers); } catch { }
            try { mode = rig.TXModeAsReported ?? ""; } catch { }
            return new TransmitSetup(antenna, freq, power, tunePower, tuning, amps, mode);
        }

        /// <summary>"handle=state;handle=state", in the inventory's order.</summary>
        public static string AmplifierSignature(AmplifierInventory? inventory)
        {
            if (inventory == null) return "";
            var sb = new StringBuilder();
            foreach (AmplifierInfo amp in inventory.Amplifiers)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(amp.Handle).Append('=').Append(amp.State);
            }
            return sb.ToString();
        }

        /// <summary>The levers a real rig offers, wired for the controller.</summary>
        public static VoxLevers LeversFor(FlexBase rig, Action? pausedTone)
            => new VoxLevers(rig.CurrentVoxSettingIfOn, rig.WriteVoxSetting, pausedTone);
    }
}
