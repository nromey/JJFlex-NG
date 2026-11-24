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

            public bool WriteProcessorOn(bool on, Func<bool> stillWanted, Action<ProcessorWriteStep> report)
            {
                if (!Connected) return false;
                var queue = _rig.q;
                if (queue == null) return false;
                // Asked, not written (repair 1, B3). The radio's command queue
                // runs this later, on its own thread, and by then the radio may
                // be transmitting, the link may be going down, or the handoff
                // may have changed its mind: so the write asks all three again
                // at the moment it is made, and is not made otherwise.
                queue.Enqueue((FunctionDel)(() => WriteIfStillRight(on, stillWanted, report)), "ProcessorHandoff");
                return true;
            }

            public bool WriteProcessorOnNow(bool on) => WriteIfStillRight(on, null, null);

            /// <summary>The command FlexLib's own setter sends for the processor's switch, read from <c>Radio.SpeechProcessorEnable</c>.</summary>
            internal static string Command(bool on) => "transmit set speech_processor_enable=" + (on ? "1" : "0");

            /// <summary>
            /// The write itself, on whichever thread has reached it: checked
            /// again here, then made as the APPLICATION's change.
            /// </summary>
            /// <remarks>
            /// <para><b>Why not FlexLib's setter alone</b> (repair 2, B3a; read
            /// from FlexLib's source, <c>Radio.cs</c>). The setter
            /// <c>Radio.SpeechProcessorEnable</c> assigns its cached field,
            /// calls <c>SendCommand</c> with no reply handler, and raises
            /// PropertyChanged, all on the calling thread; and it does nothing
            /// at all when the cache already holds the value. So reading the
            /// property back, at any later time, reads the app's own write;
            /// <c>SendCommand</c> silently sends nothing when the command link
            /// is down; and an asking again never reaches the wire.</para>
            /// <para><b>What confirms.</b> <c>Radio.SendReplyCommand</c> is
            /// public: it returns 0 and registers nothing when the command
            /// link is down, and otherwise <c>Radio.ParseReply</c> hands the
            /// radio's own <c>R&lt;sequence&gt;|&lt;hex code&gt;|</c> to the
            /// handler, code 0 for success. That answer is the one thing here
            /// the local setter cannot produce. Whether this radio also sends
            /// a status for the property back to the client that wrote it has
            /// not been measured, so nothing here depends on it either
            /// way.</para>
            /// <para><b>The order.</b> The command goes FIRST, with its reply
            /// handler: if it could not be sent, nothing else is touched and
            /// FlexLib's cache keeps saying what the radio last said. Then the
            /// write is reported as sent. Then FlexLib's setter runs, inside
            /// the application's scope, so its cache and its property event
            /// follow what was sent (anything that later sets the processor
            /// through FlexLib is compared against that cache, and a cache
            /// left stale would swallow an operator's change). When the cache
            /// differed, the setter sends the same command a second time; the
            /// radio is told the same thing twice, and only the first is
            /// listened to for an answer.</para>
            /// </remarks>
            private bool WriteIfStillRight(bool on, Func<bool>? stillWanted, Action<ProcessorWriteStep>? report)
            {
                var radio = _rig.theRadio;
                string why = !Connected || radio == null ? "the radio is no longer connected"
                    : Transmitting ? "the radio is transmitting"
                    : stillWanted != null && !stillWanted() ? "the handoff no longer wants it"
                    : "";
                if (why.Length == 0)
                {
                    int sequence = radio!.SendReplyCommand((seq, code, text) => Answered(radio, on, code, text, stillWanted, report), Command(on));
                    if (sequence == 0) why = "the command link is down";
                }
                if (why.Length > 0)
                {
                    Tracing.TraceLine("ProcessorHandoff: the radio's speech processor was NOT written " + (on ? "on" : "off") + ": " + why, TraceLevel.Info);
                    report?.Invoke(ProcessorWriteStep.NotMade);
                    return false;
                }
                // Reported as sent BEFORE FlexLib's cache can show it: the policy
                // reads the reading first and this news after, so a reading it
                // takes with no such news cannot be this write.
                report?.Invoke(ProcessorWriteStep.Sent);
                SetReading(radio!, on);
                Tracing.TraceLine("ProcessorHandoff: the radio's speech processor was sent " + (on ? "on" : "off") + "; the radio's answer is awaited", TraceLevel.Info);
                return true;
            }

            /// <summary>
            /// FlexLib's cache and property event, set to what was sent. The
            /// APPLICATION's change: it goes to the radio's own property, never
            /// through the operator-facing setter (whose first acts are to note
            /// an operator's change and to tell this handoff it was taken
            /// over), and inside the application's scope, so nothing that asks
            /// "whose write is this" during it reads it as the operator's
            /// (FlexBase.MultiUser.cs).
            /// </summary>
            private void SetReading(Flex.Smoothlake.FlexLib.Radio radio, bool on)
            {
                using (_rig.AppInitiatedSettingChanges())
                    radio.SpeechProcessorEnable = on;
            }

            /// <summary>The radio's own answer to the command, on FlexLib's reader thread.</summary>
            private void Answered(Flex.Smoothlake.FlexLib.Radio radio, bool on, uint code, string text,
                Func<bool>? stillWanted, Action<ProcessorWriteStep>? report)
            {
                try
                {
                    if (code != 0)
                    {
                        Tracing.TraceLine("ProcessorHandoff: the radio REFUSED the speech processor " + (on ? "on" : "off") + ": code " + code.ToString("X8") + " " + text, TraceLevel.Warning);
                        report?.Invoke(ProcessorWriteStep.Refused);
                        return;
                    }
                    // The radio took it, as of this point in what it sends. FlexLib reads the radio's
                    // lines in order on this one thread, so a report it parsed before this answer was
                    // sent before the radio reached our command: if such a report has put the old
                    // value back in the cache, the cache is behind the radio, and is set right. Only
                    // while the handoff still wants this write (somebody taking the processor over
                    // withdraws it), and never for the leave, which is not waited for.
                    if (stillWanted != null && stillWanted() && ReferenceEquals(_rig.theRadio, radio) && radio.SpeechProcessorEnable != on)
                    {
                        Tracing.TraceLine("ProcessorHandoff: a report from before the radio took the command had put the old value back in the reading; it is set to " + (on ? "on" : "off") + " again", TraceLevel.Info);
                        SetReading(radio, on);
                    }
                    Tracing.TraceLine("ProcessorHandoff: the radio answered that it took the speech processor " + (on ? "on" : "off"), TraceLevel.Info);
                    report?.Invoke(ProcessorWriteStep.Accepted);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("ProcessorHandoff: the radio's answer could not be handled: " + ex.Message, TraceLevel.Warning);
                }
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
        /// The processor is being set through this rig's own setter
        /// (<see cref="ProcessorOn"/>: the operator's key, a menu, a field, a
        /// preset): tell the handoff, BEFORE the write is queued, that it is
        /// somebody else's from here (repair 2, B3b). Nothing to tell when no
        /// handoff has been made for this rig.
        /// </summary>
        private void ProcessorSetThroughTheRigsSetter()
        {
            try { _processorHandoff?.TakenOver(); }
            catch (Exception ex) { Tracing.TraceLine("ProcessorSetThroughTheRigsSetter: " + ex.Message, TraceLevel.Warning); }
        }

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
