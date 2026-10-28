#nullable enable
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// A box send's rig, owned by the transmit kill switch for the send's life
    /// (#835, fix round 1): the carrier the box keys is under the
    /// reflected-power CUT, with its existing thresholds and the hold after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the kill switch and not the PTT controller.</b> The box first
    /// asked the controller to WATCH its carrier
    /// (<c>PttSafetyController.BeginExternalTransmitWatch</c>). That leaves the
    /// controller Idle, and the controller judges its cut only for a
    /// transmission it owns, so the warning ran and the cut never could
    /// (Codex's review, blocker 1). <see cref="TransmitKillSwitch"/> is the
    /// existing owner for "a transmit the operator is not holding a key for":
    /// it judges both rungs through <see cref="TransmitSafety.JudgeReflectedCut"/>,
    /// the same function the controller calls, on a thread of its own; its cut
    /// drops the carrier, disarms every source, and starts the same hold the
    /// controller's cut starts (<see cref="TransmitKillSwitch.ReflectedCutHappened"/>).
    /// Nothing about the thresholds or the hold is restated here.
    /// </para>
    /// <para>
    /// <b>Armed at construction, before the carrier goes up</b>, as
    /// <see cref="TransmitKillSwitch.Arm(FlexBase, string)"/> asks, and keyed
    /// through <see cref="TransmitKillSwitch.RaiseCarrier"/>, so the switch
    /// knows the carrier is ours and refuses to raise it once a stop has been
    /// asked for or keying is held after a cut. Escape is NOT watched from
    /// anywhere for this arm: a box send's Escape is the main window's.
    /// </para>
    /// <para>
    /// Dispose after the send has settled. Disposing twice is harmless.
    /// </para>
    /// </remarks>
    public sealed class BoxSendOwnedRig : IVoiceMessageRig, IDisposable
    {
        private readonly FlexBase _rig;
        private readonly IVoiceMessageRig _source;
        private IDisposable? _arm;

        private BoxSendOwnedRig(FlexBase rig, IVoiceMessageRig source, IDisposable arm)
        {
            _rig = rig;
            _source = source;
            _arm = arm;
        }

        /// <summary>
        /// Arm the kill switch for this send and wrap its source. Call
        /// immediately before <see cref="VoiceMessageSend.BeginKeyed"/>.
        /// </summary>
        /// <param name="rig">The radio the send keys.</param>
        /// <param name="source">The send's own rig: the file player, or the slice's playback.</param>
        /// <param name="what">What is transmitting, in the operator's words, for the trace.</param>
        public static BoxSendOwnedRig Own(FlexBase rig, IVoiceMessageRig source, string what)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new BoxSendOwnedRig(rig, source, TransmitKillSwitch.Arm(rig, what, escapeStops: false));
        }

        /// <summary>
        /// <see cref="Own(FlexBase, IVoiceMessageRig, string)"/>, unless a kill
        /// has happened since the press (fix round 1, Codex's follow-up 4).
        /// Null means a kill came between <paramref name="disarmGenerationAtPress"/>
        /// (<see cref="TransmitKillSwitch.DisarmGeneration"/>, read at the press)
        /// and now: the caller must not key, and nothing is left armed.
        /// </summary>
        /// <remarks>
        /// The order closes the window. The switch is armed FIRST, then the
        /// generation is read. A kill before that read moved the generation and
        /// is caught here; a kill after it finds the switch armed, and
        /// <see cref="TransmitKillSwitch.Request"/> latches the stop that makes
        /// <see cref="TransmitKillSwitch.RaiseCarrier"/> refuse the key.
        /// </remarks>
        public static BoxSendOwnedRig? Own(FlexBase rig, IVoiceMessageRig source, string what,
            long disarmGenerationAtPress)
        {
            var owned = Own(rig, source, what);
            if (TransmitKillSwitch.DisarmGeneration == disarmGenerationAtPress) return owned;
            owned.Dispose();
            Tracing.TraceLine("BoxSendOwnedRig: a kill came while " + what + " was getting ready, so it does not key",
                TraceLevel.Warning);
            return null;
        }

        /// <summary>
        /// True when this send asked to key and the kill switch refused: a stop
        /// had already been asked for, or keying is held after a cut. Nothing
        /// was written to the radio.
        /// </summary>
        public bool KeyRefused { get; private set; }

        /// <summary>
        /// Keying goes through the kill switch; unkeying is never conditional
        /// (<see cref="TransmitKillSwitch.DropCarrier"/>).
        /// </summary>
        public bool Transmit
        {
            get => _source.Transmit;
            set
            {
                if (value)
                {
                    if (!TransmitKillSwitch.RaiseCarrier(_rig, TransmitKillSwitch.Carrier.Mox))
                    {
                        KeyRefused = true;
                        Tracing.TraceLine("BoxSendOwnedRig: the kill switch refused the key; nothing was transmitted",
                            TraceLevel.Warning);
                    }
                }
                else TransmitKillSwitch.DropCarrier(_rig, TransmitKillSwitch.Carrier.Mox);
            }
        }

        public bool FilePlaying => _source.FilePlaying;
        public bool FileReachedEnd => _source.FileReachedEnd;
        public double FileSeconds => _source.FileSeconds;
        public void FileStart() => _source.FileStart();
        public void FileStartForVox() => _source.FileStartForVox();
        public void FileStop() => _source.FileStop();
        public bool VoxOn => _source.VoxOn;

        /// <summary>Disarm. The send must have settled: the switch stops watching this carrier.</summary>
        public void Dispose()
        {
            var arm = _arm;
            _arm = null;
            arm?.Dispose();
        }
    }
}
