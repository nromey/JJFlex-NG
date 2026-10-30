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
