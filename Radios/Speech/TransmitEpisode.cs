using System;

namespace Radios.Speech
{
    /// <summary>
    /// The owner-side identity of one transmission, and the two questions a
    /// protected safety sentence asks of it before it is replayed.
    ///
    /// <para><b>The episode follows RF, not the state label (Astra's Track
    /// IJK2 review, blocker 1).</b> The first build advanced a counter beside
    /// every <c>SetTx(true)</c> in <c>PttSafetyController</c>, which is every
    /// place a transmission can START — and also one place a transmission is
    /// merely KEPT ON: a held PTT becoming a transmit lock calls
    /// <c>SetTx(true)</c> again with no <c>SetTx(false)</c> between, and RF
    /// never stops. The counter said "new transmission", so a reflected-power
    /// warning cut short during the hold, still true during the lock, was
    /// withdrawn as history while power was still coming back into the
    /// finals. A source test required the increment before every
    /// <c>SetTx(true)</c>, so it pinned the defect.</para>
    ///
    /// <para>So the rule lives here, as a function of RF alone: the episode
    /// advances when RF goes from OFF to ON, and nowhere else. An owner that
    /// says "on" while RF is already on is continuing the same transmission,
    /// whatever its state machine calls the new state. RF here is what the
    /// owner commands or watches — its own key, or an external transmit it
    /// has been asked to watch — so an external probe's transmission is a
    /// transmission too, not an Idle that cannot possibly be on the air.</para>
    ///
    /// <para>Read from the speech layer's thread through the closures below,
    /// so both fields are volatile. One instance per owner.</para>
    /// </summary>
    public sealed class TransmitEpisode
    {
        private volatile int _episode;
        private volatile bool _rfOn;

        /// <summary>The current episode. Zero until RF has been on once.</summary>
        public int Current => _episode;

        /// <summary>Whether RF is on, by the owner's last account.</summary>
        public bool RfOn => _rfOn;

        /// <summary>
        /// The owner's account of RF right now. Advances the episode only on
        /// the OFF-to-ON edge. Saying "on" twice is one transmission; saying
        /// "off" ends it without starting another; "off" twice is harmless.
        /// </summary>
        public void RfIs(bool on)
        {
            if (on && !_rfOn) _episode++;
            _rfOn = on;
        }

        /// <summary>
        /// For a sentence about THIS transmission while it runs — "power is
        /// coming back", "about to end": true while RF is still on and no
        /// transmission has started since. Captures the episode now.
        /// </summary>
        public Func<bool> StillThisTransmission()
        {
            int captured = _episode;
            return () => _rfOn && _episode == captured;
        }

        /// <summary>
        /// For a sentence about this transmission's END — "you are no longer
        /// on the air": true until a new transmission starts, whether or not
        /// RF is on at the moment of asking. Captures the episode now, so it
        /// is called after the owner has said "off".
        /// </summary>
        public Func<bool> NoTransmissionSince()
        {
            int captured = _episode;
            return () => _episode == captured;
        }
    }
}
