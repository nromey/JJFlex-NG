using System;

namespace JJFlexWpf
{
    public static partial class EarconPlayer
    {
        // ── The alert channel's output buffer (#855, step 4) ───────────────
        //
        // RULED by Noel 2026-10-30 01:44, "Yes", to a smaller-buffer setting
        // with the default chosen by measurement. The measurement: the
        // countdown pip reached the speaker 110 to 177 ms after its spoken
        // number with the channel at 100 ms by 2 buffers, and an experiment
        // build at 30 ms by 3 brought JAWS to 29 ms and NVDA to 106 ms. Most of
        // the lag was this queue. So the default is 30 by 3.
        //
        // WHY A FLOOR AND A MINIMUM TOTAL. WinMM keeps the device fed from a
        // thread that refills one buffer each time the device finishes one. If
        // the whole queue is shorter than the longest that thread can be kept
        // waiting, the device runs dry and the sound breaks up. Nothing under
        // 20 ms per buffer, or under 60 ms in all, has been measured on this
        // machine, let alone a tester's busy one, so neither can be set; a
        // hand-edited file below them is raised to them. The ceiling is twice
        // the old setting's per-buffer length and four buffers, past which a
        // choice is only adding delay.
        //
        // ONLY THE ALERT CHANNEL. The meter channel's tones are continuous and
        // are not timed against speech; it keeps 100 by 2.

        /// <summary>The shortest buffer that can be set, in milliseconds.</summary>
        internal const int AlertBufferMinMs = 20;

        /// <summary>The longest buffer that can be set, in milliseconds.</summary>
        internal const int AlertBufferMaxMs = 200;

        internal const int AlertBufferMinCount = 2;
        internal const int AlertBufferMaxCount = 4;

        /// <summary>The least audio that can be queued in all, buffers times length.</summary>
        internal const int AlertBufferMinTotalMs = 60;

        /// <summary>The measured default (see above).</summary>
        public const int DefaultAlertBufferMs = 30;
        public const int DefaultAlertBufferCount = 3;

        private static int _alertBufferMs = DefaultAlertBufferMs;
        private static int _alertBufferCount = DefaultAlertBufferCount;

        /// <summary>
        /// A buffer length and count brought inside what the device can be
        /// trusted with: each clamped to its range, then buffers added until the
        /// queue holds at least <see cref="AlertBufferMinTotalMs"/>.
        /// </summary>
        internal static (int ms, int count) ClampAlertBuffer(int ms, int count)
        {
            ms = Math.Clamp(ms, AlertBufferMinMs, AlertBufferMaxMs);
            count = Math.Clamp(count, AlertBufferMinCount, AlertBufferMaxCount);
            while (ms * count < AlertBufferMinTotalMs && count < AlertBufferMaxCount) count++;
            return (ms, count);
        }

        /// <summary>The alert channel's buffer length and count as set.</summary>
        public static (int ms, int count) AlertBuffer => (_alertBufferMs, _alertBufferCount);

        /// <summary>The meter channel's, which is not a setting.</summary>
        internal static (int ms, int count) MeterBuffer => (100, 2);

        /// <summary>
        /// Set the alert channel's buffer, clamped. Applies at once when the
        /// channel is open (it reopens its device); otherwise it is used when
        /// the channel opens.
        /// </summary>
        public static void SetAlertBuffer(int ms, int count)
        {
            (ms, count) = ClampAlertBuffer(ms, count);
            _alertBufferMs = ms;
            _alertBufferCount = count;
            _alertChannel?.Rebuffer(ms, count);
        }
    }
}
