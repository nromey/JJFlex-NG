#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// FlexBase, whether the radio has told us its powers on this connection
    /// (#859), so a value can be saved to be put back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two things read as a report and neither is one.</b>
    /// </para>
    /// <para>
    /// FlexLib raises the same <c>RFPower</c> and <c>TunePower</c> changes from
    /// its local setter, before any reply, as from a status line. It does so
    /// synchronously on the caller's thread and nothing in the event tells them
    /// apart, so a write of ours marks itself (<see cref="OurPowerWrite"/>) and
    /// anything not so marked came from a status line. No vendor change. The
    /// writes in <c>XmitPower</c> and <c>TunePower</c> are the only ones in the
    /// tree, and a test holds that. The app's own transmit profile selections
    /// are marked the same way.
    /// </para>
    /// <para>
    /// And the first status lines are placeholders. Measured on fifteen real
    /// connects, a 6300 over SmartLink and an 8600: the radio sends
    /// <c>rfpower=0 tunepower=0</c> several times from about 100 ms after the
    /// connect, then the real pair, and within 16 ms of that, never before it,
    /// its transmit profile selection. A radio really at zero reports the same
    /// zeros, and only the selection tells them apart. So a zero counts once the
    /// selection has come; anything else counts as soon as it arrives, because a
    /// placeholder is always zero.
    /// </para>
    /// <para>
    /// Tune power rides the same status line as RF power: the two trace within
    /// a millisecond on every connect read. It has its own fact anyway, because
    /// a dummy load saves both.
    /// </para>
    /// <para>
    /// A radio at zero with no transmit profile selected would stay unreported.
    /// That refuses a check, which is the safe side, and it has not been seen.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        [ThreadStatic] private static int _ourPowerWrites;

        private volatile bool _rfPowerHeard, _rfPowerSeenNonZero;
        private volatile bool _tunePowerHeard, _tunePowerSeenNonZero;
        private volatile bool _transmitProfileHeard;

        private sealed class PowerWriteMark : IDisposable
        {
            public PowerWriteMark() => _ourPowerWrites++;
            public void Dispose() => _ourPowerWrites--;
        }

        /// <summary>Around FlexLib's optimistic power setter, whose change event is ours and not the radio's.</summary>
        private static IDisposable OurPowerWrite() => new PowerWriteMark();

        private void NoteRfPowerEvent(int value)
        {
            if (_ourPowerWrites > 0) return;
            _rfPowerHeard = true;
            if (value != 0) _rfPowerSeenNonZero = true;
        }

        private void NoteTunePowerEvent(int value)
        {
            if (_ourPowerWrites > 0) return;
            _tunePowerHeard = true;
            if (value != 0) _tunePowerSeenNonZero = true;
        }

        private void NoteTransmitProfileEvent(string? selection)
        {
            if (_ourPowerWrites == 0 && !string.IsNullOrEmpty(selection)) _transmitProfileHeard = true;
        }

        private void ForgetPowerReports()
        {
            _rfPowerHeard = _rfPowerSeenNonZero = false;
            _tunePowerHeard = _tunePowerSeenNonZero = false;
            _transmitProfileHeard = false;
        }

        /// <summary>
        /// The radio has told us its transmit power on this connection, so
        /// <see cref="XmitPower"/> is a reading. Nothing may save the value as a
        /// prior, or act on it being zero, until this is true.
        /// </summary>
        public bool TransmitPowerReported => _rfPowerHeard && (_rfPowerSeenNonZero || _transmitProfileHeard);

        /// <summary>The same for <see cref="TunePower"/>.</summary>
        public bool TunePowerReported => _tunePowerHeard && (_tunePowerSeenNonZero || _transmitProfileHeard);

        /// <summary>Everything a check can save to put back is a reading.</summary>
        public bool CheckPowersReported => TransmitPowerReported && TunePowerReported;
    }
}
