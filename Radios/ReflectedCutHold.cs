#nullable enable
using System;
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
    /// check, and VOX ("that's fine", "if so, yes that's fine", 21:15 and
    /// 21:16). The tune carrier and an ATU tune are NOT held: "you should be
    /// able to tune right away to rectify the problem" (2026-10-22 05:42), and
    /// a tune is itself a change that lifts the hold. Only the reflected-power
    /// cut starts a hold; the transmit timeout, the ALC release, the hard kill
    /// and a temperature over-run do not (05:14).
    /// </para>
    /// <para>
    /// <b>VOX is paused by switching the radio's VOX off and back on</b>
    /// ("switch off and on silently", 05:37), because VOX is decided inside the
    /// radio and the only lever the app has is the radio's setting. Transmit
    /// inhibit was offered and rejected: it blocks every microphone and every
    /// client, and strands the radio if the app dies mid-window. The switch is
    /// made as an APP-INITIATED write, so the operator-changed hook does not
    /// fire and the hold does not lift itself; it is never saved into a
    /// profile; and it is restored on every exit the app controls: expiry, an
    /// early lift, and the owner going away. If the app dies mid-window VOX
    /// stays off, which is the accepted, safe-direction failure. The operator
    /// is told once, when VOX was on, at their verbosity and with their sound
    /// settings ("tell them/tone based on settings", 05:38); nothing is said
    /// when VOX comes back.
    /// </para>
    /// <para>
    /// <b>Shape.</b> Pure and clock-injected so the window, the signals, the
    /// VOX restore and the speech can all be driven from a test with no radio
    /// and no dispatcher. The owner (the PTT safety controller) reads the
    /// transmit setup fresh from the rig and hands it in; this class only
    /// compares. It speaks through <see cref="ScreenReaderOutput"/> itself,
    /// so a test can assert on what is DELIVERED past the verbosity gate
    /// rather than on a call that was made before it.
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
            /// <summary>The owner went away (radio off, controller disposed).</summary>
            Abandoned,
        }

        /// <summary>
        /// The transmit setup as read from the rig at one instant. Compared
        /// field by field against the one taken at the cut; any difference
        /// lifts the hold. Each field says where it is read from and how
        /// fresh it is, because that is the question the ruling turns on.
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

            public TransmitSetup(string? txAntenna, ulong txFrequencyHz, int rfPower, int tunePower,
                                 bool tuning, string? amplifierSignature)
            {
                TxAntenna = txAntenna ?? "";
                TxFrequencyHz = txFrequencyHz;
                RfPower = rfPower;
                TunePower = tunePower;
                Tuning = tuning;
                AmplifierSignature = amplifierSignature ?? "";
            }

            public override string ToString()
                => "antenna " + (TxAntenna.Length == 0 ? "unknown" : TxAntenna)
                 + ", " + TxFrequencyHz + " Hz, power " + RfPower + ", tune power " + TunePower
                 + (Tuning ? ", tuning" : "")
                 + (AmplifierSignature.Length == 0 ? "" : ", amplifiers " + AmplifierSignature);
        }

        private readonly Func<long> _clock;

        private bool _holding;
        private long _beganMs;
        private TransmitSetup _atCut;
        private string _reason = "";
        private string _antennaCheck = "";

        private Func<bool>? _voxIsOn;
        private Action<bool>? _voxSet;
        private Action? _refusalTone;
        private bool _voxPaused;
        private bool _voxRestoreCancelled;

        /// <summary>Clock in milliseconds; <c>Environment.TickCount64</c> in production.</summary>
        public ReflectedCutHold(Func<long> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>True while a hold is in force. Expiry is noticed lazily by <see cref="Tick"/> and by a refused press.</summary>
        public bool Holding => _holding;

        /// <summary>How many presses this hold refused, across all doors.</summary>
        public int RefusedPresses { get; private set; }

        /// <summary>How the last hold ended, and why, for the report and the trace.</summary>
        public EndReason LastEnd { get; private set; } = EndReason.None;

        /// <summary>The words of the last end: which signal lifted it, or that it expired.</summary>
        public string LastEndDetail { get; private set; } = "";

        /// <summary>The sentence the last refused press spoke, or empty.</summary>
        public string LastRefusalSentence { get; private set; } = "";

        /// <summary>True while this hold has the radio's VOX switched off on the operator's behalf.</summary>
        public bool VoxPaused => _voxPaused;

        /// <summary>Raised once when a hold ends, by whichever path ended it. VOX has already been restored when this fires.</summary>
        public event Action<EndReason, string>? Ended;

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
        /// <param name="voxIsOn">Reads the radio's VOX. Null when the owner cannot reach it; VOX is then left alone.</param>
        /// <param name="voxSet">Writes the radio's VOX AS AN APP-INITIATED CHANGE. The owner wraps the write so the operator-changed hook does not fire.</param>
        /// <param name="voxPausedTone">The tone beside the VOX-paused note, under the operator's sound switches. Null plays nothing.</param>
        /// <param name="refusalTone">The tone a refused press plays — the cut's own alarm tone. Null plays nothing.</param>
        public void Begin(TransmitSafety.ReflectedCut rung, in TransmitPowerReading reading, string? antennaName,
                          in TransmitSetup atCut, long nowMs,
                          Func<bool>? voxIsOn = null, Action<bool>? voxSet = null,
                          Action? voxPausedTone = null, Action? refusalTone = null)
        {
            if (_holding) EndHold(EndReason.Abandoned, "a second cut began before the first hold ended", nowMs);

            _holding = true;
            _beganMs = nowMs;
            _atCut = atCut;
            _reason = ReasonFor(rung, reading, antennaName);
            _antennaCheck = string.IsNullOrWhiteSpace(antennaName)
                ? Lexicon.Get("audio.ptt.cut_hold_the_antenna")
                : antennaName!;
            _voxIsOn = voxIsOn;
            _voxSet = voxSet;
            _refusalTone = refusalTone;
            _voxPaused = false;
            _voxRestoreCancelled = false;
            RefusedPresses = 0;
            LastRefusalSentence = "";

            Tracing.TraceLine("CutHold: keying is held for " + HoldSeconds + " s after the reflected-power cut, "
                + "or until the transmit setup changes; at the cut: " + atCut, TraceLevel.Warning);

            PauseVoxIfOn(voxPausedTone);
        }

        private void PauseVoxIfOn(Action? voxPausedTone)
        {
            if (_voxIsOn == null || _voxSet == null) return;
            bool on;
            try { on = _voxIsOn(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("CutHold: could not read VOX, so it is left alone — " + ex.Message, TraceLevel.Warning);
                return;
            }
            if (!on) return;

            try { _voxSet(false); }
            catch (Exception ex)
            {
                Tracing.TraceLine("CutHold: COULD NOT switch VOX off — " + ex.Message, TraceLevel.Error);
                return;
            }
            _voxPaused = true;
            Tracing.TraceLine("CutHold: VOX was on, switched off for the hold (app-initiated, not saved)", TraceLevel.Info);

            // The one note the operator gets about VOX, at their own verbosity
            // and under their own sound switches (Noel, 2026-10-22 05:38), after
            // the cut sentence, which is Urgent and goes first. Nothing is said
            // when VOX comes back.
            try { voxPausedTone?.Invoke(); } catch { }
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
            if (!_holding) return false;
            if (nowMs - _beganMs >= HoldMs)
            {
                EndHold(EndReason.Expired, "the window ran out", nowMs);
                return false;
            }

            RefusedPresses++;
            int remaining = Math.Max(1, (int)Math.Ceiling((HoldMs - (nowMs - _beganMs)) / 1000.0));
            string sentence = RefusalSentence(_reason, _antennaCheck, remaining);
            LastRefusalSentence = sentence;

            Tracing.TraceLine("CutHold: REFUSED to key from " + door + " — " + remaining + " s of the hold left; "
                + "press " + RefusedPresses + " of this hold", TraceLevel.Warning);

            try { _refusalTone?.Invoke(); } catch { }
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
        /// the cut, and lift the hold on any difference. Also notices expiry.
        /// Safe to call when nothing is held.
        /// </summary>
        /// <returns>True when this call ended the hold.</returns>
        public bool Tick(in TransmitSetup now, long nowMs)
        {
            if (!_holding) return false;
            if (nowMs - _beganMs >= HoldMs)
            {
                EndHold(EndReason.Expired, "the window ran out", nowMs);
                return true;
            }

            string? changed = WhatChanged(_atCut, now);
            if (changed == null) return false;
            EndHold(EndReason.Lifted, changed, nowMs);
            return true;
        }

        /// <summary>
        /// The first field of <paramref name="now"/> that differs from
        /// <paramref name="atCut"/>, in the operator's words, or null when the
        /// setup is unchanged. Public so the comparison can be tested on its
        /// own and so the report can name each signal.
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

        private static string Name(string antenna) => antenna.Length == 0 ? "unknown" : antenna;

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
        /// names. No amplifier has ever been on the bench (the inventory's own
        /// remarks say so), so this is read from the vendor library's model,
        /// not from a measurement.
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
            if (!_holding) return;
            switch (what)
            {
                case "TXAntennaName":
                case "TXAntenna":
                    EndHold(EndReason.Lifted, "you changed the transmit antenna", nowMs);
                    break;
                case "XmitPower":
                    EndHold(EndReason.Lifted, "you changed the RF power", nowMs);
                    break;
                case "TunePower":
                    EndHold(EndReason.Lifted, "you changed the tune power", nowMs);
                    break;
                case "Vox":
                    if (_voxPaused)
                    {
                        _voxRestoreCancelled = true;
                        Tracing.TraceLine("CutHold: you changed VOX during the hold, so the hold will not put it back itself",
                                          TraceLevel.Info);
                    }
                    break;
            }
        }

        /// <summary>
        /// The owner is going away — radio powered off, controller disposed.
        /// Restores VOX if this hold had it off and the rig can still be
        /// written, and ends the hold.
        /// </summary>
        public void Abandon(string why, long nowMs)
        {
            if (!_holding) return;
            EndHold(EndReason.Abandoned, why, nowMs);
        }

        private void EndHold(EndReason reason, string detail, long nowMs)
        {
            _holding = false;
            LastEnd = reason;
            LastEndDetail = detail;
            long heldMs = nowMs - _beganMs;

            RestoreVox();

            Tracing.TraceLine("CutHold: hold ended after " + heldMs + " ms — " + reason + ": " + detail
                + (RefusedPresses == 0 ? "" : "; " + RefusedPresses + " press(es) refused"), TraceLevel.Info);

            _voxIsOn = null;
            _voxSet = null;
            _refusalTone = null;

            try { Ended?.Invoke(reason, detail); } catch { }
        }

        private void RestoreVox()
        {
            if (!_voxPaused) return;
            _voxPaused = false;
            if (_voxRestoreCancelled)
            {
                Tracing.TraceLine("CutHold: VOX left as you set it during the hold", TraceLevel.Info);
                return;
            }
            if (_voxSet == null) return;
            try
            {
                _voxSet(true);
                Tracing.TraceLine("CutHold: VOX switched back on (app-initiated, nothing said)", TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CutHold: COULD NOT switch VOX back on — " + ex.Message
                    + ". VOX stays off, the safe direction; the operator can turn it on by hand.", TraceLevel.Error);
            }
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
                default:
                    return Lexicon.Get("audio.ptt.cut_hold_reason_share",
                        ("percent", (int)Math.Round(reading.ReflectedShare * 100f)), ("antenna", antenna));
            }
        }

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
        /// cut — the direction Noel's worry points.
        /// </summary>
        public static TransmitSetup Snapshot(FlexBase rig)
        {
            string antenna = "";
            ulong freq = 0;
            int power = 0;
            int tunePower = 0;
            bool tuning = false;
            string amps = "";
            try { antenna = rig.TXAntennaName ?? ""; } catch { }
            try { freq = rig.TXFrequencyAsReported; } catch { }
            try { power = rig.XmitPower; } catch { }
            try { tunePower = rig.TunePower; } catch { }
            try { tuning = rig.ATUTuneInProgress || rig.TxTune; } catch { }
            try { amps = AmplifierSignature(rig.Amplifiers); } catch { }
            return new TransmitSetup(antenna, freq, power, tunePower, tuning, amps);
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
    }
}
