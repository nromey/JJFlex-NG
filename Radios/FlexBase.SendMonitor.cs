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

        /// <summary>
        /// A send's monitor hold, let go only when the radio next reports
        /// receive (#835, fix round 2026-10-30; Noel, 2026-10-29 23:57: after a
        /// push-to-talk takeover, <i>"I'd wait until unkey, a change could be
        /// kind of weird"</i>). The operator who took the send over keeps
        /// hearing the monitor exactly as the send left it until they let go;
        /// then the hold ends, and a monitor the send turned on goes back off
        /// by <see cref="SendMonitorOwner"/>'s usual rules. The same wait the
        /// box send's VOX pause makes after a takeover
        /// (<c>ReleaseVoxWhenReceive</c>), or the connection going away. Not
        /// transmitting now, it is let go now.
        /// </summary>
        /// <remarks>
        /// The wait is <see cref="WhenRadioReallyReceives"/> (Codex round 1030,
        /// B5): cached Transmit false AND the radio's interlock out of transmit,
        /// pinned to the radio and command connection it began on. After a
        /// hardware takeover the send drops its software key, so cached Transmit
        /// alone read receive while the hand mic still keyed the radio. A lost or
        /// replaced connection ends it as a disconnect, and the monitor owner's
        /// lost-connection rule records the put-back.
        /// </remarks>
        public void ReleaseMonitorHoldAtUnkey(IDisposable? hold, string who)
        {
            if (hold == null) return;
            bool waited = false;
            WhenRadioReallyReceives(sameConnection =>
            {
                if (waited)
                    Tracing.TraceLine(sameConnection
                        ? "SendMonitor: the operator unkeyed after taking over " + who
                            + " (Transmit False, interlock " + theRadio?.InterlockState + "); the send's hold on the transmit monitor ends"
                        : "SendMonitor: the connection " + who + " was taken over on went away before the unkey; the send's hold on the transmit monitor ends",
                        TraceLevel.Info);
                hold.Dispose();
            }, beforeWaiting: () =>
            {
                waited = true;
                Tracing.TraceLine("SendMonitor: " + who + " was taken over; the transmit monitor stays as it is until the operator unkeys"
                    + " (Transmit " + _Transmit + ", interlock " + theRadio?.InterlockState + ")",
                    TraceLevel.Info);
            });
        }

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

        /// <summary>
        /// The operator chose the monitor in this app (the <see cref="Monitor"/>
        /// setter: Home's checkbox, the menu, the Workshop): any put-back owed to
        /// this radio from an earlier disconnect is an older intent and goes
        /// (#835, Codex round 3). Never throws; the store traces its own failures.
        /// </summary>
        private void SupersedeOwedMonitorPutBack()
        {
            string? serial = theRadio?.Serial;
            if (string.IsNullOrEmpty(serial)) return;
            SendMonitorPutBackStore store = MonitorPutBackStore;
            if (!store.Contains(serial)) return;
            store.Remove(serial);
            Tracing.TraceLine("SendMonitor: the operator set the monitor, so the put-back owed from an earlier disconnect is dropped",
                TraceLevel.Info);
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
            // Revalidated here, when the queue runs it: an operator choice made
            // since it was scheduled has already cleared the record (Codex round 3).
            if (!store.Contains(serial))
            {
                Tracing.TraceLine("SendMonitor: the owed put-back was superseded by the operator's own monitor choice; not sent",
                    TraceLevel.Info);
                return;
            }
            // The radio does not echo this raw write, so FlexLib's cache would
            // still say the monitor is on; write the acknowledged value into it
            // the way the take's restores do (round 1029 merge, #835).
            var monitorOff = new Listenback.SavedRadioSetting("monitor", "0", "transmit set mon=0", "transmit", "sb_monitor");
            string cachedBefore = Listenback.FlexLibRestoreCache.Read(radio, monitorOff);
            string? refused = SendRadioCommandWithReply(radio, "transmit set mon=0", reply =>
            {
                Tracing.TraceLine("SendMonitor: monitor put-back for an earlier disconnect " + reply, TraceLevel.Info);
                if (reply.Acknowledged)
                {
                    store.Remove(serial);
                    Tracing.TraceLine("SendMonitor: cache " + Listenback.FlexLibRestoreCache.Accept(radio, monitorOff, cachedBefore), TraceLevel.Info);
                }
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
