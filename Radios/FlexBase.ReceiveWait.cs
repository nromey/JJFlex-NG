#nullable enable
using System;
using System.Threading;
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
        public bool WhenReallyReceiving(Action<bool> then) => WhenRadioReportsReceive((same, _) => then(same), null);

        /// <summary>
        /// The same wait, given up quietly once <paramref name="stillWanted"/>
        /// says no (a newer debt replaced the one it was for): then
        /// <paramref name="then"/> never runs, and nothing is left waiting
        /// behind a carrier that may hold for a long time.
        /// </summary>
        public bool WhenReallyReceiving(Action<bool> then, Func<bool> stillWanted) =>
            WhenRadioReportsReceive((same, _) => then(same), stillWanted);

        /// <summary>
        /// The same wait, handing <paramref name="then"/> a way to act only if
        /// the radio is STILL receiving, for this transmission, on this
        /// connection (repair rounds 5 and 6, Codex's #863 N2 and S1). The
        /// function runs its action, and returns true, only when that holds,
        /// with the check and the action as one step against every new key
        /// this process asks for and every report or connection change the
        /// transport publishes (<see cref="FallingCueGate"/>).
        /// </summary>
        public bool WhenReallyReceiving(Action<bool, Func<Action, bool>> then, Func<bool> stillWanted) =>
            WhenRadioReportsReceive(then, stillWanted);

        /// <summary>For tests: runs on the payment's thread just before its final check takes the transport's dispatch gate.</summary>
        internal Action? BeforeFallingCueFinalCheckForTests;

        /// <summary>
        /// The interlock reports as they stood when this rig's latest
        /// transmission was asked for, or its cached Transmit last rose
        /// (repair round 5, #863 N1). Zero before any.
        /// </summary>
        private long _txEpisodeMark;

        /// <summary>A transmission begins: a report from before now says nothing about its end.</summary>
        private void NoteTransmitEpisodeBegins() => Volatile.Write(ref _txEpisodeMark, InterlockReports.Sequence);

        /// <summary>For tests: the fallback interval, in place of <see cref="ReceiveReportFallbackMs"/>; zero uses it.</summary>
        internal int ReceiveReportFallbackMsOverride;

        private bool WhenRadioReportsReceive(Action<bool, Func<Action, bool>> then, Func<bool>? stillWanted)
        {
            Radio? radio = theRadio;
            CommandConnection? connection = radio?.Connected == true ? radio.CurrentCommandConnection : null;
            // The episode this wait is about (repair round 5, Codex's #863 N1):
            // the reports read before this release, and before this
            // transmission began, cannot confirm its end. A READY delivered
            // before the key was pressed is not the radio saying this key's
            // carrier is down.
            long releaseMark = InterlockReports.Sequence;
            long episodeMark = Volatile.Read(ref _txEpisodeMark);
            bool SameConnection() => radio != null && connection != null && ReferenceEquals(theRadio, radio)
                && radio.Connected && ReferenceEquals(connection, radio.CurrentCommandConnection)
                && connection.State == CommandConnectionState.Connected;
            bool ReportsReceive() => !_Transmit && RadioInterlockConfirmsReceive
                && InterlockReports.ConfirmsReceiveFor(connection, releaseMark, episodeMark);
            // Repair round 6 (Codex's #863 S1 variant): a newer transmission this
            // process asked for since the debt was made (another send, a check,
            // a tune) voids it. Its own end owes its own cue; the first
            // carrier's receive report says nothing about the newer one.
            long transmissions = FallingCueGate.Transmissions;
            bool Superseded() => FallingCueGate.Transmissions != transmissions;
            bool StillReceiving() => SameConnection() && !Superseded() && ReportsReceive();
            // The final check and the caller's action as ONE step (repair round
            // 6, S1): under the transport's dispatch gate, which the vendor's
            // parser, our interlock record and the connection's own changes
            // are written under, and under the gate every new key is counted
            // under. Lock order: dispatch gate, cue gate, then the caller's.
            bool SubmitIfStillReceiving(Action act)
            {
                BeforeFallingCueFinalCheckForTests?.Invoke();
                object? dispatch = InterlockReports.ConnectionSyncOf(radio);
                if (dispatch == null) return false;
                lock (dispatch) return FallingCueGate.RunIf(StillReceiving, act);
            }
            int fallback = ReceiveReportFallbackMsOverride > 0 ? ReceiveReportFallbackMsOverride : ReceiveReportFallbackMs;

            if (!SameConnection()) { then(false, SubmitIfStillReceiving); return false; }
            if (ReportsReceive()) { then(true, SubmitIfStillReceiving); return false; }

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
                    while ((same = SameConnection()) && !ReportsReceive() && !Superseded())
                    {
                        if (stillWanted != null && !stillWanted()) return;
                        await wake.WaitAsync(fallback).ConfigureAwait(false);
                    }
                }
                finally
                {
                    radio.PropertyChanged -= onRadio;
                    InterlockReports.Reported -= onReport;
                }
                then(same, SubmitIfStillReceiving);
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
