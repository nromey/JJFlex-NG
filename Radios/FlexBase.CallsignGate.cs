using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// No callsign, no transmit (#335): the radio layer's half of the gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noel, 2026-08-28:</b> "If the operator does not give a call, they get
    /// no transmit ability, no ifs ands or buts." Ruled again 2026-11-08: no
    /// exception for a bench test into the dummy load, and the gate covers
    /// every keying JJ Flexible starts (push to talk, the lock, voice and CW
    /// sends, tune, the checks), each refused with a sentence naming the
    /// missing callsign and where to enter it. The radio's own microphone PTT
    /// and footswitch are left alone: nothing here writes a transmit inhibit
    /// into the radio.
    /// </para>
    /// <para>
    /// <b>Which callsign.</b> The OPERATOR's, entered under Radio, Operators,
    /// and explicitly NOT the radio's: <see cref="Callsign"/> is the radio's
    /// front-panel field, and under a borrowed or shared radio it is somebody
    /// else's. The radio layer cannot see the operator's record, so the
    /// application hands it a way to ask (<see cref="OpenParms.GetOperatorCallsign"/>),
    /// asked at the moment of each key and never cached, because the operator
    /// can change.
    /// </para>
    /// <para>
    /// <b>Where it is asked.</b> Twice, on purpose. The operator's doors ask
    /// first (the push to talk controller, the transmit toggle, tune, the
    /// sends, through <c>ZeroPowerDoors</c>), before anything is prepared, so
    /// a send does not start a file and a check does not count down. And the
    /// admission boundary asks again at the chokepoints every keying route ends
    /// at (FlexBase.CheckRestore.cs lists them): transmit going on, tune going
    /// on, the antenna tuner going on, both forms of SendCW, and a recording
    /// sent through VOX. That is what guarantees a route with no door of its
    /// own (CW typing, the Fix tool's stages) is refused too.
    /// </para>
    /// <para>
    /// <b>It only ever refuses a key.</b> Transmit off, tune off, the tuner
    /// off and every unkey pass untouched, and the question is never asked on
    /// the way down. Asking costs one delegate call, so an unkey is never
    /// delayed either.
    /// </para>
    /// <para>
    /// <b>When nothing was handed in</b> (a radio object built by a test, or
    /// by code that never sets <see cref="OpenParms.GetOperatorCallsign"/>),
    /// the gate is not installed and refuses nothing. The application's one
    /// place that builds <see cref="OpenParms"/> sets it, and
    /// <c>CallsignGateTests</c> pins that. A provider that throws reads as no
    /// callsign: refusing a key is the safe failure.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>
        /// True when the operator has no callsign: none entered, or only
        /// spaces. Any other text counts as a callsign; checking that it is a
        /// real, licensed one is the verification design's job, not this gate's.
        /// </summary>
        public bool OperatorCallsignMissing => CallsignMissing(Callouts?.GetOperatorCallsign);

        /// <summary>The decision, pure, so a test can hold it without a radio.</summary>
        internal static bool CallsignMissing(Func<string>? provider)
        {
            if (provider == null) return false;   // not installed: see the remarks
            try { return string.IsNullOrWhiteSpace(provider()); }
            catch (Exception ex)
            {
                Tracing.TraceLine("CallsignGate: the operator's callsign could not be read, so keying is refused: "
                    + ex.Message, TraceLevel.Warning);
                return true;
            }
        }

        /// <summary>
        /// The one sentence for this refusal, at every door and at the
        /// chokepoints. DRAFT wording, awaiting Noel.
        /// </summary>
        public static string NoCallsignRefusal => Lexicon.Get("settings.operators.no_callsign_transmit");

        /// <summary>
        /// Whether turning VOX on now would turn the radio's SIMPLE VOX on,
        /// rather than CW break-in (#335, night question J8). Read from the
        /// TRANSMIT slice, the slice <see cref="Vox"/>'s setter writes through:
        /// false with no transmit slice (the setter then writes nothing) and
        /// false when it is in CW, where the same control switches break-in.
        /// </summary>
        /// <remarks>
        /// <para>The one answer the setter's chokepoint and every door share,
        /// so a door can never let simple VOX through to a setter that refuses
        /// it silently, nor refuse break-in the setter would have allowed.
        /// The selected slice's mode (<see cref="Mode"/>) is NOT this answer:
        /// with a CW slice selected and an SSB transmit slice, or the reverse,
        /// it disagrees with what the setter writes.</para>
        /// <para>Break-in is not refused for a missing callsign tonight: that is
        /// night question J4, unruled.</para>
        /// </remarks>
        public bool VoxOnWouldTurnOnSimpleVox => TurnsOnSimpleVox(VFOToSlice(TXVFO));

        /// <summary>The decision, given the transmit slice: simple VOX unless
        /// there is none or it is in CW. Pure, so the setter and the property
        /// cannot tell the two apart differently.</summary>
        internal static bool TurnsOnSimpleVox(Flex.Smoothlake.FlexLib.Slice txSlice)
            => txSlice != null && txSlice.DemodMode != "CW";

        private long _lastNoCallsignSpokenMs;

        /// <summary>
        /// The admission boundary's refusal: true, traced and spoken (throttled,
        /// so a held key or a burst of queued sends says it once), when
        /// <paramref name="what"/> would start a transmission and the operator
        /// has no callsign. Asked only on the way ON.
        /// </summary>
        internal bool RefusedForNoCallsign(string what)
        {
            if (!OperatorCallsignMissing) return false;
            Tracing.TraceLine("Refused with no operator callsign: " + what, TraceLevel.Warning);
            long now = Environment.TickCount64;
            if (now - System.Threading.Interlocked.Read(ref _lastNoCallsignSpokenMs) > 1500)
            {
                System.Threading.Interlocked.Exchange(ref _lastNoCallsignSpokenMs, now);
                ScreenReaderOutput.Speak(NoCallsignRefusal, Speech.SpeechIntent.Interrupt, VerbosityLevel.Critical,
                    subject: Speech.SpeechSubject.TransmitKeyRefused);
            }
            return true;
        }
    }
}
