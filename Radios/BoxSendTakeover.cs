#nullable enable
using System;

namespace Radios
{
    /// <summary>Who took the transmitter from a box send (#835).</summary>
    public enum BoxTakeover
    {
        /// <summary>Nobody: the send still has it.</summary>
        None,
        /// <summary>Push to talk or the lock, through the PTT controller.</summary>
        Controller,
        /// <summary>A hardware PTT line: the microphone's, ACC or the rear RCA.</summary>
        Hardware,
    }

    /// <summary>
    /// The host's one answer to "has the operator taken the transmitter from
    /// this box send?" (#835 fix round 1, Codex's blocker 2; Noel 2026-10-27
    /// 15:26, "ptt takes over" — any push to talk). One instance per send,
    /// asked on every tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The controller first</b>: push to talk on the keyboard and the lock
    /// key through <c>PttSafetyController</c>, which then owns the unkey.
    /// </para>
    /// <para>
    /// <b>Then a hardware line</b>, from the radio's own interlock source
    /// (<see cref="FlexBase.PttSourceIsHardware"/>: MIC, ACC or RCA). The send
    /// keys in software, so the radio reports SW for it. The discriminator is
    /// order: a hardware source counts only after a SOFTWARE source has been
    /// seen while transmitting in this same send. So the send's own key can
    /// never be mistaken for the operator, and neither can a hardware source
    /// left standing from an earlier transmission the radio has not yet
    /// replaced. If the radio never reports SW for our key, hardware takeover
    /// is never detected, which fails safe: the recording carries on and the
    /// send unkeys its own software key at the end, and a hardware line held
    /// through that unkey keeps the radio keyed by itself.
    /// </para>
    /// <para>
    /// Not proved anywhere but the bench: what the radio reports as the
    /// source when a hardware line is pressed under a software key.
    /// </para>
    /// </remarks>
    public sealed class BoxSendTakeover
    {
        private readonly Func<bool> _controllerKeyed;
        private readonly Func<bool> _transmitting;
        private readonly Func<bool> _hardwareSource;
        private bool _sawSoftwareKey;

        /// <summary>Raw readings, for a host that is not a <see cref="FlexBase"/>.</summary>
        public BoxSendTakeover(Func<bool> controllerKeyed, Func<bool> transmitting, Func<bool> hardwareSource)
        {
            _controllerKeyed = controllerKeyed ?? throw new ArgumentNullException(nameof(controllerKeyed));
            _transmitting = transmitting ?? throw new ArgumentNullException(nameof(transmitting));
            _hardwareSource = hardwareSource ?? throw new ArgumentNullException(nameof(hardwareSource));
        }

        /// <summary>The production predicate: the controller, and the rig's own transmit and PTT source.</summary>
        public static BoxSendTakeover For(FlexBase rig, Func<bool> controllerKeyed)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            return new BoxSendTakeover(controllerKeyed, () => rig.Transmit, () => rig.PttSourceIsHardware);
        }

        /// <summary>Who has taken the transmitter, if anyone. Unreadable reads as nobody.</summary>
        public BoxTakeover Check()
        {
            try { if (_controllerKeyed()) return BoxTakeover.Controller; }
            catch { /* an unreadable controller is not a takeover */ }

            bool transmitting, hardware;
            try { transmitting = _transmitting(); hardware = _hardwareSource(); }
            catch { return BoxTakeover.None; }
            if (!transmitting) return BoxTakeover.None;
            if (!hardware)
            {
                _sawSoftwareKey = true;
                return BoxTakeover.None;
            }
            return _sawSoftwareKey ? BoxTakeover.Hardware : BoxTakeover.None;
        }
    }
}
