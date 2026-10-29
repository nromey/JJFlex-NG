#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the transmit monitor a send turns on and puts back (#835,
    /// rulings of 2026-10-28 16:36 and 16:47). The decisions are
    /// <see cref="SendMonitorOwner"/>'s; this is its port onto the radio.
    /// </summary>
    public partial class FlexBase
    {
        private SendMonitorOwner? _sendMonitor;
        private long _monitorOperatorWrites;

        private SendMonitorOwner SendMonitor
        {
            get
            {
                SendMonitorOwner? owner = _sendMonitor;
                if (owner != null) return owner;
                System.Threading.Interlocked.CompareExchange(ref _sendMonitor,
                    new SendMonitorOwner(new SendMonitorPort(this)), null);
                return _sendMonitor!;
            }
        }

        /// <summary>
        /// Hold the radio's transmit monitor on for a send, silently, until the
        /// returned lease is disposed; if the send turned it on, it goes back
        /// off then, unless the operator touched it meanwhile. Never touches
        /// the monitor's level. <paramref name="who"/> is for the trace.
        /// </summary>
        public IDisposable HoldMonitorForSend(string who) => SendMonitor.Hold(who);

        /// <summary>True while the monitor is on because a send turned it on. For tests and the trace.</summary>
        internal bool MonitorOnForASend => _sendMonitor?.OnForASend == true;

        /// <summary>
        /// Where a put-back owed across a disconnect is recorded (#835, Codex
        /// rounds 1 and 2). On disk, per radio serial, under the settings root,
        /// because the ordinary close and reopen disposes this FlexBase and
        /// makes a new one. Tests point it at a folder of their own.
        /// </summary>
        internal SendMonitorPutBackStore MonitorPutBackStore =>
            MonitorPutBackStoreOverride ?? new SendMonitorPutBackStore(RadioConfig.AppDataRoot);

        /// <summary>For tests: a store in a folder of their own.</summary>
        internal SendMonitorPutBackStore? MonitorPutBackStoreOverride { get; set; }

        /// <summary>
        /// What a send holds on the radio, ended on the disconnect path while
        /// the link is still up (#835, Codex round 1, findings 3 and 4): the PTT
        /// source watch is revoked, and a monitor a send turned on is put back
        /// off now, by a command the radio acknowledges, rather than by the
        /// send's release later, which would find the connection gone. If the
        /// radio does not acknowledge within a second, the put-back is recorded
        /// for the next connect instead. Called from <see cref="Disconnect"/>.
        /// </summary>
        internal void RevokeSendHoldsForDisconnect(string why)
        {
            RevokePttWatch(why);
            Radio? radio = theRadio;
            SendMonitorOwner? owner = _sendMonitor;
            if (radio == null || owner == null || !owner.TakeBackForDisconnect()) return;
            bool acknowledged = false;
            // Not disposed: a reply arriving after the wait has given up may
            // still set it, harmlessly.
            var answered = new ManualResetEventSlim();
            string refusal = SendRadioCommandWithReply(radio, "transmit set mon=0", reply =>
            {
                acknowledged = reply.Acknowledged;
                answered.Set();
            });
            if (refusal == null && answered.Wait(MonitorPutBackWaitMs) && acknowledged)
            {
                Tracing.TraceLine("SendMonitor: transmit monitor back off before the disconnect, acknowledged",
                    TraceLevel.Info);
                return;
            }
            Tracing.TraceLine("SendMonitor: the monitor's put-back before the disconnect was "
                + (refusal ?? (acknowledged ? "late" : "not acknowledged")) + "; noted for the next connect",
                TraceLevel.Warning);
            MonitorPutBackStore.Add(radio.Serial);
        }

        /// <summary>How long the disconnect waits for the radio to acknowledge the monitor's put-back.</summary>
        private const int MonitorPutBackWaitMs = 1000;

        /// <summary>
        /// The command loop has started on a new connection, so the station is
        /// established and its profile loaded: if a send left this radio's
        /// monitor on at an earlier disconnect, put it back off now (#835, Codex
        /// round 2). Called from the main thread right after
        /// <c>q.MainLoop = true</c>, never earlier: work queued before the loop
        /// runs is dropped, which is how round 1's version lost it.
        /// </summary>
        private void ApplyPendingMonitorPutBack()
        {
            Radio? radio = theRadio;
            string? serial = radio?.Serial;
            if (radio == null || string.IsNullOrEmpty(serial)) return;
            SendMonitorPutBackStore store = MonitorPutBackStore;
            if (!store.Contains(serial)) return;
            Tracing.TraceLine("SendMonitor: a send left this radio's monitor on at an earlier disconnect; putting it back off",
                TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                if (ReferenceEquals(theRadio, radio)) PutBackMonitorNow(radio);
            }), "SendMonitorPutBack");
        }

        /// <summary>
        /// Send the owed put-back, <c>transmit set mon=0</c>, by the radio's own
        /// command (FlexLib's setter skips a value its cache believes unchanged),
        /// and clear the record only when the radio acknowledges it. A refusal
        /// or no answer leaves it for the next connect.
        /// </summary>
        internal void PutBackMonitorNow(Radio radio)
        {
            string? serial = radio?.Serial;
            if (radio == null || string.IsNullOrEmpty(serial)) return;
            SendMonitorPutBackStore store = MonitorPutBackStore;
            string? refused = SendRadioCommandWithReply(radio, "transmit set mon=0", reply =>
            {
                Tracing.TraceLine("SendMonitor: monitor put-back for an earlier disconnect " + reply, TraceLevel.Info);
                if (reply.Acknowledged) store.Remove(serial);
            });
            if (refused != null)
                Tracing.TraceLine("SendMonitor: monitor put-back for an earlier disconnect not sent: " + refused
                    + "; kept for the next connect", TraceLevel.Warning);
        }

        private sealed class SendMonitorPort : SendMonitorOwner.IPort
        {
            private readonly FlexBase _rig;
            private Radio? _watched;
            private string? _watchedSerial;

            internal SendMonitorPort(FlexBase rig) { _rig = rig; }

            public bool? MonitorOn
            {
                get
                {
                    Radio? radio = _rig.theRadio;
                    return radio == null || !_rig.IsConnected ? null : radio.TXMonitor;
                }
            }

            public long OperatorWrites => System.Threading.Interlocked.Read(ref _rig._monitorOperatorWrites);

            public object? Connection
            {
                get
                {
                    Radio? radio = _rig.theRadio;
                    if (radio == null || !_rig.IsConnected) return null;
                    try { return (object?)radio.CurrentCommandConnection ?? radio; }
                    catch { return radio; }
                }
            }

            public void WriteMonitor(bool on)
            {
                Radio? radio = _rig.theRadio;
                if (radio == null) return;
                // The same queue and the same FlexLib property the Monitor
                // setter writes, but not through that setter: this write is
                // the send's, so it neither counts as the operator's nor says
                // anything.
                _rig.q.Enqueue((FunctionDel)(() =>
                {
                    if (ReferenceEquals(_rig.theRadio, radio)) radio.TXMonitor = on;
                }), "SendMonitor");
            }

            public void Watch(bool watching)
            {
                Radio? old = _watched;
                _watched = null;
                if (old != null) old.PropertyChanged -= OnRadioProperty;
                if (!watching) return;
                Radio? radio = _rig.theRadio;
                if (radio == null) return;
                _watched = radio;
                _watchedSerial = radio.Serial;
                radio.PropertyChanged += OnRadioProperty;
            }

            public void PutBackLost()
            {
                if (_watchedSerial != null) _rig.MonitorPutBackStore.Add(_watchedSerial);
            }

            private void OnRadioProperty(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(Radio.TXMonitor)) return;
                _rig._sendMonitor?.Observe();
            }
        }
    }
}
