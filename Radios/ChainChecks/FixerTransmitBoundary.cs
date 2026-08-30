using System;
using System.Diagnostics;
using Radios.Fixer;
using JJTrace;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The transmit boundary: the only code in the Fixer Tool that both
    /// consults <see cref="FixerTransmitGate"/> and calls something that keys a
    /// radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This supplies <c>TransmitStageSet.Hosts.ProbeTransmitter</c>. The split
    /// is deliberate and belongs to the engine side: <b>the host measures, the
    /// engine interprets.</b> So nothing here decides what a reading means —
    /// <c>TransmitStages.Transmitter</c> owns every word the operator hears
    /// about the result, and this file owns only whether the measurement is
    /// allowed to be taken at all.
    /// </para>
    /// <para>
    /// <b>It lives in Radios.ChainChecks and not in Radios.Fixer, and that is
    /// load-bearing.</b> A reflection test walks every type in Radios.Fixer and
    /// fails if <c>FlexBase</c> appears in any signature, so that namespace is
    /// structurally incapable of touching a radio. This file must name
    /// <c>FlexBase</c> to do its job, so it belongs on the other side of that
    /// line — next to <see cref="TxTuneProbeRunner"/>, which is the only other
    /// thing here that keys anything.
    /// </para>
    /// <para>
    /// <b>A gate nothing consults is not a gate.</b> This exists so the guard
    /// sits on the actual path rather than beside it — the failure this whole
    /// tool was built to expose, turned on the tool itself.
    /// </para>
    /// <para>
    /// A refusal comes back as <c>TxTuneProbe.Result.NotRun</c> carrying the
    /// gate's own words, rather than as a second kind of thing the engine would
    /// have to learn about. One vocabulary, one path.
    /// </para>
    /// </remarks>
    public static class FixerTransmitBoundary
    {
        /// <summary>
        /// Reads the live radio. Returns null when there is no radio — never a
        /// stand-in, because a stand-in would let a stage report a measurement
        /// nothing made.
        /// </summary>
        public delegate FlexBase RadioSource();

        /// <summary>
        /// Build the host's transmitter probe: ask the gate, and only if it
        /// agrees, key the rig with TUNE and measure.
        /// </summary>
        /// <remarks>
        /// Returns null when anything it needs is missing. Null is the engine's
        /// "the host wired nothing" signal and records the stage as unable to
        /// run — which for a transmitting stage is exactly what keeps a
        /// half-wired host from keying anything.
        /// </remarks>
        /// <param name="gate">Holds every fact that decides whether RF may go out.</param>
        /// <param name="radio">Where the live radio comes from.</param>
        /// <param name="stageId">
        /// The stage this probe belongs to, so the gate can enforce its
        /// once-per-stage rule against the right thing.
        /// </param>
        /// <param name="speakNow">
        /// Told when the radio has confirmed keying (#255). The host owns the
        /// words — for a tune carrier they must say nothing is wanted of the
        /// operator, because stage 4's "speak into your microphone" is wrong
        /// here. This stage blocks the UI thread while it transmits, so
        /// nothing else can say anything for it.
        /// </param>
        /// <param name="speakDone">
        /// Told after the carrier is down — only if <paramref name="speakNow"/>
        /// was told, so nobody hears "finished" for a transmit that never
        /// began.
        /// </param>
        /// <param name="countdown">
        /// Starts the transmit countdown tones (#261), UNKEYED, with the
        /// key-up issued when the count reaches
        /// the moment the host publishes as the key-up. The
        /// operator does nothing in this stage — the count is the warning
        /// that RF is imminent, the same ruling that gave stage 3 its count.
        /// It never sounds for a transmit the gate refused.
        /// </param>
        /// <param name="stopRequested">
        /// Polled during the countdown. True ends the stage before anything
        /// keys, recorded honestly as stopped-before-keying.
        /// </param>
        /// <param name="countdownKeyUpAtMs">
        /// When to issue the key-up, in milliseconds from the start of the
        /// count. PUBLISHED BY THE HOST, from the sound it is actually going to
        /// play — never copied into this assembly, which cannot see it. The
        /// number was a hand-copied constant until 2026-08-29 and had drifted to
        /// half its true value, so this stage raised RF during the second dit of
        /// its own warning. See
        /// <see cref="FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs"/>.
        /// </param>
        public static Func<TxTuneProbe.Result> ProbeTransmitter(
            FixerTransmitGate gate, RadioSource radio, string stageId,
            Action speakNow = null, Action speakDone = null,
            Action countdown = null, Func<bool> stopRequested = null,
            int countdownKeyUpAtMs = FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs)
        {
            if (gate == null || radio == null || string.IsNullOrWhiteSpace(stageId))
                return null;

            return () =>
            {
                FlexBase rig = Safely(radio);

                // Facts the gate is not allowed to take on trust, read here
                // from the radio itself.
                bool reachable = rig != null;
                bool keyed = ReadKeyed(rig);

                // The run id comes from the gate rather than from a caller.
                // Whether the REQUEST named the right run was settled at the
                // message pump, before any stage started; by the time we are
                // here the only open questions are about the station.
                FixerTransmitGate.Decision d = gate.Request(
                    gate.RunId, stageId, stageTransmits: true,
                    radioReachable: reachable, rigIsKeyed: keyed,
                    // The power THIS transmit would use: the tune probe keys
                    // the radio's tune carrier, so tune power is the number
                    // the low-power ceiling judges (#180).
                    transmitPowerWatts: ReadTransmitPowerWatts(rig, tuneCarrier: true));

                if (!d.Allowed)
                {
                    Tracing.TraceLine("FixerTransmitStages: transmit refused (" + d.Why
                        + ") — " + d.Explanation, TraceLevel.Warning);
                    return TxTuneProbe.Result.NotRun(SkipFor(d.Why), d.Explanation);
                }

                // The countdown, UNKEYED, after the grant and before the key
                // (#255, #261). Stage 2 used to key in total silence while
                // blocking the UI thread — the speak pair had been built for
                // stage 4 and the countdown for stages 3 and 4, and this
                // stage got neither. Shared pacing, so the timing an operator
                // learns on one keying stage holds on all of them.
                if (!FixerTransmitAudioBoundary.CountUnkeyedThenReadyToKey(
                        countdown, stopRequested, countdownKeyUpAtMs))
                {
                    return TxTuneProbe.Result.NotRun(
                        TxTuneProbe.SkipReason.Cancelled,
                        FixerTransmitAudioBoundary.StoppedDuringCountdownText);
                }

                bool cuedTransmitting = false;
                return TxTuneProbeRunner.Run(
                    rig,
                    // Re-derived from the gate's own record rather than passed
                    // as a bare true because we got this far. If the two ever
                    // disagree, the runner refuses as well and nothing keys.
                    loadDeclared: gate.LoadDeclaration.Length > 0,
                    cancel: default,
                    antennaPort: null,
                    onKeyConfirmed: () =>
                    {
                        gate.NoteKeyed(stageId);
                        // Spoken only on the radio's confirmation, never on
                        // the setter's return — the same rule as stage 4's
                        // "go": a radio that never keys never gets a cue.
                        cuedTransmitting = true;
                        FixerTransmitAudioBoundary.Witness(speakNow, "speakNow");
                    },
                    onUnkeyed: () =>
                    {
                        gate.NoteUnkeyed();
                        // Only if the start cue went out: "finished" for a
                        // transmit that never began would be an invention.
                        if (cuedTransmitting)
                            FixerTransmitAudioBoundary.Witness(speakDone, "speakDone");
                    });
            };
        }

        /// <summary>
        /// Which of the probe's own skip reasons a gate refusal is.
        /// </summary>
        /// <remarks>
        /// Three of the gate's refusals are facts about the STATION and already
        /// have a name in the probe's vocabulary. The rest are facts about the
        /// SOFTWARE, and they map to <c>RefusedByHost</c> rather than being
        /// squeezed into a station reason — telling an operator their radio was
        /// unreachable when really our repeat guard fired would send them
        /// hunting a fault that is not there.
        /// </remarks>
        public static TxTuneProbe.SkipReason SkipFor(FixerTransmitGate.Refusal why)
        {
            switch (why)
            {
                case FixerTransmitGate.Refusal.NoRadio:
                    return TxTuneProbe.SkipReason.RadioNotReachable;
                case FixerTransmitGate.Refusal.AlreadyInFlight:
                    return TxTuneProbe.SkipReason.AlreadyTransmitting;
                case FixerTransmitGate.Refusal.LoadNotDeclared:
                    return TxTuneProbe.SkipReason.LoadNotDeclared;
                case FixerTransmitGate.Refusal.RunAborted:
                    return TxTuneProbe.SkipReason.Cancelled;
                default:
                    return TxTuneProbe.SkipReason.RefusedByHost;
            }
        }

        // -------- plumbing --------
        //
        // Internal rather than private since 2026-08-25: the transmit-audio
        // boundary (FixerTransmitAudioBoundary) faces the same two questions —
        // "is there a radio" and "is it keyed" — and two answers to a safety
        // question is one more than is safe.

        internal static FlexBase Safely(RadioSource radio)
        {
            // The Fixer Tool only opens when something is already wrong, so the
            // thing it asks for the radio is exactly the thing likely to throw.
            // Falling over here would take the diagnosis away at the moment it
            // was wanted.
            try { return radio(); } catch { return null; }
        }

        internal static bool ReadKeyed(FlexBase rig)
        {
            if (rig == null) return false;

            // A radio that cannot be asked is treated as KEYED, deliberately.
            // The two ways to be wrong are not equal: refusing a transmit that
            // would have been fine costs a retry, and stacking one on top of a
            // transmit already running does not.
            try { return rig.Transmit || rig.TxTune; } catch { return true; }
        }

        /// <summary>
        /// The power the next transmit would use, for the gate's low-power
        /// ceiling (#180): tune power for a tune carrier, transmit power for
        /// everything else. Returns -1 when it cannot be read — NEVER 0,
        /// because a failure that reads as "zero watts" would sail under the
        /// ceiling, and fail-open is the one direction this reader must not
        /// fail in.
        /// </summary>
        internal static int ReadTransmitPowerWatts(FlexBase rig, bool tuneCarrier)
        {
            if (rig == null) return -1;
            try { return tuneCarrier ? rig.TunePower : rig.XmitPower; }
            catch { return -1; }
        }
    }
}
