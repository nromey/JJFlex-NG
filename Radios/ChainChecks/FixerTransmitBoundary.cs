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
        public static Func<TxTuneProbe.Result> ProbeTransmitter(
            FixerTransmitGate gate, RadioSource radio, string stageId)
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
                    radioReachable: reachable, rigIsKeyed: keyed);

                if (!d.Allowed)
                {
                    Tracing.TraceLine("FixerTransmitStages: transmit refused (" + d.Why
                        + ") — " + d.Explanation, TraceLevel.Warning);
                    return TxTuneProbe.Result.NotRun(SkipFor(d.Why), d.Explanation);
                }

                return TxTuneProbeRunner.Run(
                    rig,
                    // Re-derived from the gate's own record rather than passed
                    // as a bare true because we got this far. If the two ever
                    // disagree, the runner refuses as well and nothing keys.
                    loadDeclared: gate.LoadDeclaration.Length > 0,
                    cancel: default,
                    antennaPort: null,
                    onKeyConfirmed: () => gate.NoteKeyed(stageId),
                    onUnkeyed: gate.NoteUnkeyed);
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

        private static FlexBase Safely(RadioSource radio)
        {
            // The Fixer Tool only opens when something is already wrong, so the
            // thing it asks for the radio is exactly the thing likely to throw.
            // Falling over here would take the diagnosis away at the moment it
            // was wanted.
            try { return radio(); } catch { return null; }
        }

        private static bool ReadKeyed(FlexBase rig)
        {
            if (rig == null) return false;

            // A radio that cannot be asked is treated as KEYED, deliberately.
            // The two ways to be wrong are not equal: refusing a transmit that
            // would have been fine costs a retry, and stacking one on top of a
            // transmit already running does not.
            try { return rig.Transmit || rig.TxTune; } catch { return true; }
        }
    }
}
