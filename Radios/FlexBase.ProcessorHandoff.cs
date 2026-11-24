#nullable enable
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    // The Flex side of the handoff between our transmit compressor and the
    // radio's speech processor (Sprint 49 Track CL, #1278). The rules live in
    // the radio-neutral ProcessorHandoff; this file only says how a Flex is
    // read and written, and gives the app the members it drives the policy
    // with. One call from the connect path (FlexBase.MainThread.cs, beside the
    // audio check's put-back) is all an existing file gained.
    public partial class FlexBase
    {
        /// <summary>The radio's speech processor as the handoff reads and writes it.</summary>
        private sealed class FlexSpeechProcessor : IRadioSpeechProcessor
        {
            private readonly FlexBase _rig;
            public FlexSpeechProcessor(FlexBase rig) { _rig = rig; }

            public string Serial => _rig.theRadio?.Serial ?? "";

            public bool Connected => _rig.IsConnected && !_rig.Disconnecting && _rig.theRadio != null;

            public bool? ProcessorOn => _rig.theRadio?.SpeechProcessorEnable;

            public int ProcessorLevel => (int)(_rig.theRadio?.SpeechProcessorLevel ?? 0);

            public bool Transmitting => _rig.TransmitKeyedOrInterlock;

            // Anything but "only us" for certain: an uncertain roster is said
            // as "shared", never as nobody (the roster's own rule for speech).
            public bool AnotherClientMayBeConnected => !_rig.OnlyStation;

            public bool RadioIsOperators => _rig.RadioIsMine;

            public bool WriteProcessorOn(bool on)
            {
                if (!Connected) return false;
                // The APPLICATION's change: inside this scope the setter does
                // not arm the disconnect save offer or the spoken receipt, so
                // it is never read as the operator's (FlexBase.MultiUser.cs).
                using (_rig.AppInitiatedSettingChanges())
                    _rig.ProcessorOn = on ? OffOnValues.on : OffOnValues.off;
                return true;
            }
        }

        private ProcessorHandoff? _processorHandoff;
        private IRadioSpeechProcessor? _speechProcessorForHandoff;

        /// <summary>For tests: a store in a folder of their own.</summary>
        internal ProcessorHandoffStore? ProcessorHandoffStoreOverride { get; set; }

        /// <summary>The radio's speech processor as the handoff sees it (tests read and write through the same object the policy does).</summary>
        internal IRadioSpeechProcessor SpeechProcessorForHandoff => _speechProcessorForHandoff ??= new FlexSpeechProcessor(this);

        /// <summary>
        /// Raised when the handoff did something the operator must hear
        /// (#1278: never silent). On the policy tick's thread; the app says it.
        /// </summary>
        public event Action<ProcessorHandoffNotice>? ProcessorHandoffNoticed;

        /// <summary>
        /// The handoff for this radio object. The app drives it: one
        /// <see cref="ProcessorHandoff.Tick"/> per policy tick with what is
        /// true on the computer's side, and <see cref="ProcessorHandoff.Leaving"/>
        /// before an orderly disconnect.
        /// </summary>
        public ProcessorHandoff ProcessorHandoff => _processorHandoff ??= new ProcessorHandoff(
            SpeechProcessorForHandoff,
            ProcessorHandoffStoreOverride ?? new ProcessorHandoffStore(RadioConfig.AppDataRoot),
            notice =>
            {
                try { ProcessorHandoffNoticed?.Invoke(notice); }
                catch (Exception ex) { Tracing.TraceLine("ProcessorHandoff: a listener threw: " + ex.Message, TraceLevel.Warning); }
            },
            line => Tracing.TraceLine(line, TraceLevel.Info));

        /// <summary>
        /// At connect, once the command loop runs: tell the handoff a
        /// connection has started, so its next tick applies the connect rule
        /// to a record an earlier session left (the processor put back, or
        /// kept off for a compressor that is on, and said either way).
        /// </summary>
        private void ProcessorHandoffAtConnect()
        {
            try { ProcessorHandoff.Connected(); }
            catch (Exception ex) { Tracing.TraceLine("ProcessorHandoffAtConnect: " + ex.Message, TraceLevel.Warning); }
        }

        /// <summary>Whether the computer's microphone is the radio's transmit source (not the radio's own microphone, line or accessory input).</summary>
        public bool PcMicrophoneIsTransmitSource => string.Equals(MicSource, "PC", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// How many times a stage of the transmit frame pipeline has thrown
        /// since the microphone stream opened (injection, conditioning or
        /// metering; the pipeline counts them together). The handoff reads a
        /// rise as "the chain faulted mid-over". Zero with no stream.
        /// </summary>
        public long TransmitChainFaults
        {
            get
            {
                try { return opusInputChannel?.PortAudioStream?.TransmitPipeline?.StageFailures ?? 0; }
                catch (Exception) { return 0; }
            }
        }
    }
}
