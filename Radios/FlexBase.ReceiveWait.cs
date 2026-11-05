#nullable enable
using System;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// FlexBase, waiting for the radio to be really receiving before something
    /// held for a transmission is let go (#835, Codex round 1030, B5 and its
    /// sibling).
    /// </summary>
    public partial class FlexBase
    {
        /// <summary>
        /// Run <paramref name="then"/> once the radio is really receiving, or
        /// once the connection this call began on is gone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>"Receive" is the radio's word, not only our cache.</b> Our cached
        /// <see cref="Transmit"/> falls the moment we drop our own key, which a
        /// hardware takeover does while the hand mic still keys the radio, and
        /// FlexLib's Mox cache flips the moment we ask for an unkey. So both
        /// must say receive: cached Transmit false AND the radio's interlock
        /// out of transmit (<see cref="RadioInterlockReportsTransmit"/>), the
        /// same pair the check's restores wait for.
        /// </para>
        /// <para>
        /// <b>Pinned to the radio and command connection it began on</b>
        /// (<c>feedback_deferred_state_carries_its_session</c>). Another radio,
        /// a new connection or a lost one ends the wait the way a disconnect
        /// always has: <paramref name="then"/> runs with
        /// <c>connectionHeld</c> false, and the caller's own lost-connection
        /// rules apply.
        /// </para>
        /// <para>
        /// <b>No time limit,</b> deliberately: the ruling is "only when the radio
        /// is really receiving". An interlock that never reports READY on a
        /// live connection keeps the wait going; whether that wants a limit is
        /// Noel's question, not this method's.
        /// </para>
        /// </remarks>
        /// <param name="then">
        /// Runs exactly once. Its argument is true when the radio reported
        /// receive on the same connection, false when the connection went.
        /// </param>
        /// <param name="beforeWaiting">
        /// Runs before the wait starts, only if there is one to wait for, so a
        /// trace line saying "waiting" always comes before the one saying it
        /// ended.
        /// </param>
        /// <returns>
        /// False when nothing had to be waited for: <paramref name="then"/> has
        /// already run, on this thread. True when it runs later, on the pool.
        /// </returns>
        /// <summary>
        /// <see cref="WhenRadioReallyReceives"/> for the push-to-talk controller:
        /// the falling tri-tone of a carrier this computer did not key for
        /// itself plays on the radio's real receive, not on our cache's
        /// (#863, repair round 3, Codex's second re-check B1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Positive, since repair round 4</b> (ruled by Noel 2026-11-04
        /// 22:24: every falling transmit cue waits for the radio's own receive
        /// report). "Really receiving" here is our cached Transmit false AND a
        /// recognised receive report on the connection the wait began on,
        /// still connected (<see cref="RadioInterlockConfirmsReceive"/>), not
        /// merely "the interlock does not say transmit": a tone means really
        /// off the air, so a state we do not recognise is not receive.
        /// </para>
        /// <para>
        /// <b>Woken by the reports, not polled</b>, so a tone follows the
        /// radio's word with no delay of our own: FlexLib's interlock and Mox
        /// notifications and our own record of the report each wake it. A
        /// slow fallback check catches a connection that ends quietly.
        /// </para>
        /// </remarks>
        public bool WhenReallyReceiving(Action<bool> then) => WhenRadioReportsReceive(then, null);

        /// <summary>
        /// The same wait, given up quietly once <paramref name="stillWanted"/>
        /// says no (a newer debt replaced the one it was for): then
        /// <paramref name="then"/> never runs, and nothing is left waiting
        /// behind a carrier that may hold for a long time.
        /// </summary>
        public bool WhenReallyReceiving(Action<bool> then, Func<bool> stillWanted) => WhenRadioReportsReceive(then, stillWanted);

        private bool WhenRadioReportsReceive(Action<bool> then, Func<bool>? stillWanted)
        {
            Radio? radio = theRadio;
            CommandConnection? connection = radio?.Connected == true ? radio.CurrentCommandConnection : null;
            bool SameConnection() => radio != null && connection != null && ReferenceEquals(theRadio, radio)
                && radio.Connected && ReferenceEquals(connection, radio.CurrentCommandConnection)
                && connection.State == CommandConnectionState.Connected;
            bool ReportsReceive() => !_Transmit && RadioInterlockConfirmsReceive;

            if (!SameConnection()) { then(false); return false; }
            if (ReportsReceive()) { then(true); return false; }

            var wake = new System.Threading.SemaphoreSlim(0);
            System.ComponentModel.PropertyChangedEventHandler onRadio = (_, e) =>
            {
                if (e.PropertyName is "InterlockState" or "Mox") { try { wake.Release(); } catch { } }
            };
            Action<CommandConnection> onReport = c =>
            {
                if (ReferenceEquals(c, connection)) { try { wake.Release(); } catch { } }
            };
            radio!.PropertyChanged += onRadio;
            InterlockReports.Reported += onReport;
            _ = Task.Run(async () =>
            {
                bool same;
                try
                {
                    while ((same = SameConnection()) && !ReportsReceive())
                    {
                        if (stillWanted != null && !stillWanted()) return;
                        await wake.WaitAsync(ReceiveReportFallbackMs).ConfigureAwait(false);
                    }
                }
                finally
                {
                    radio.PropertyChanged -= onRadio;
                    InterlockReports.Reported -= onReport;
                }
                then(same);
            });
            return true;
        }

        /// <summary>
        /// How often a wait for the receive report looks again with nothing to
        /// wake it: only for a connection that ends without a word. Every
        /// report wakes it at once, so this never delays a tone that has its
        /// report.
        /// </summary>
        internal const int ReceiveReportFallbackMs = 250;

        private bool WhenRadioReallyReceives(Action<bool> then, Action? beforeWaiting = null)
        {
            Radio? radio = theRadio;
            CommandConnection? connection = radio?.Connected == true ? radio.CurrentCommandConnection : null;
            bool SameConnection() => radio != null && ReferenceEquals(theRadio, radio) && radio.Connected
                && ReferenceEquals(connection, radio.CurrentCommandConnection);
            // Read only after SameConnection(), so theRadio is the pinned radio.
            bool StillTransmitting() => _Transmit || RadioInterlockReportsTransmit;

            bool held = SameConnection();
            if (!held || !StillTransmitting())
            {
                then(held);
                return false;
            }
            beforeWaiting?.Invoke();
            _ = Task.Run(async () =>
            {
                bool same;
                while ((same = SameConnection()) && StillTransmitting())
                    await Task.Delay(20).ConfigureAwait(false);
                then(same);
            });
            return true;
        }
    }
}
