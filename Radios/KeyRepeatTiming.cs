using System;
using System.Runtime.InteropServices;

namespace Radios
{
    /// <summary>
    /// The operator's own keyboard auto-repeat settings, in milliseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The gap between a held key's first event and its
    /// second is not a property of a screen reader, a machine, or a measurement
    /// session — it is a Windows setting, it is on the Keyboard control panel,
    /// and it is different on different machines. A screen reader that
    /// synthesises key events for a held key rides that same setting, which is
    /// why the first synthetic pair of a press was measured "at the Windows
    /// repeat delay". Anything that has to bridge that gap should ASK, not
    /// assume: a constant tuned on one box is wrong on the next one, and wrong
    /// in the direction that chops a transmission (#216).
    /// </para>
    /// <para>
    /// Both readings are cheap and both are conversions the caller can test
    /// without a keyboard — <see cref="DelayMsFromSetting"/> and
    /// <see cref="RepeatPeriodMsFromSetting"/> are pure.
    /// </para>
    /// </remarks>
    public static class KeyRepeatTiming
    {
        private const uint SPI_GETKEYBOARDDELAY = 0x0016;
        private const uint SPI_GETKEYBOARDSPEED = 0x000A;

        /// <summary>
        /// Windows' own default repeat delay setting (index 1 = 500 ms), used
        /// when the setting cannot be read. It is also the setting the
        /// 2026-08-24 key-probe measurements were taken at, which is why the
        /// first synthetic pair landed near 512 ms there.
        /// </summary>
        public const int DefaultDelayMs = 500;

        /// <summary>Fallback repeat period when the speed setting cannot be read.</summary>
        public const int DefaultRepeatPeriodMs = 92;

        [DllImport("user32.dll", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfoW(
            uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);

        /// <summary>
        /// SPI_GETKEYBOARDDELAY returns 0-3. The documented mapping is roughly
        /// 250 ms per step starting at 250: 250, 500, 750, 1000.
        /// Pure, so the mapping can be tested without touching the machine.
        /// </summary>
        public static int DelayMsFromSetting(int setting)
        {
            if (setting < 0) setting = 0;
            if (setting > 3) setting = 3;
            return 250 + (setting * 250);
        }

        /// <summary>
        /// SPI_GETKEYBOARDSPEED returns 0-31, slowest to fastest: about 2.5
        /// repeats a second at 0 and about 30 at 31. Returned here as the
        /// PERIOD between repeats, which is the quantity a hold watchdog wants.
        /// Pure.
        /// </summary>
        public static int RepeatPeriodMsFromSetting(int setting)
        {
            if (setting < 0) setting = 0;
            if (setting > 31) setting = 31;
            // 400 ms at the slow end, 33 ms at the fast end, linear in the
            // setting — the interpolation Windows itself documents.
            return 400 - (setting * (400 - 33) / 31);
        }

        /// <summary>
        /// How long Windows waits before a held key starts repeating on THIS
        /// machine. Falls back to <see cref="DefaultDelayMs"/> rather than
        /// throwing — a hold watchdog must still work on a machine that will
        /// not answer.
        /// </summary>
        public static int DelayMs()
        {
            try
            {
                if (SystemParametersInfoW(SPI_GETKEYBOARDDELAY, 0, out uint v, 0))
                    return DelayMsFromSetting((int)v);
            }
            catch { /* fall through */ }
            return DefaultDelayMs;
        }

        /// <summary>
        /// The interval between repeats once a held key has started repeating,
        /// on THIS machine. Falls back to
        /// <see cref="DefaultRepeatPeriodMs"/>.
        /// </summary>
        public static int RepeatPeriodMs()
        {
            try
            {
                if (SystemParametersInfoW(SPI_GETKEYBOARDSPEED, 0, out uint v, 0))
                    return RepeatPeriodMsFromSetting((int)v);
            }
            catch { /* fall through */ }
            return DefaultRepeatPeriodMs;
        }
    }
}
