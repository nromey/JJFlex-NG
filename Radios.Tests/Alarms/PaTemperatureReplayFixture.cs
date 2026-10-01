using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// A real PA temperature series, sanitised to numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Provenance.</b> The bench FLEX-8600, the detailed capture begun
    /// 2026-08-22 at 20:35:46 local time on build 4.1.16.1378, archived as
    /// <c>trace-20260822-203546-clean_exit.zip</c> under the local AppData
    /// Traces tree. Read through 7-Zip without extracting on 2026-09-22 for
    /// Sprint 45 Track I. Forty-nine <c>PATempDataHandler</c> rows from trace
    /// tick 56451 through 159051: 102.600 seconds, 25.671875 to 33.78125 C,
    /// recorded inter-row gaps 2.110 to 2.579 seconds. The ticks are the
    /// trace's own millisecond counter; the values are the raw handler floats,
    /// which carry FlexLib's 1/64 C quantisation.
    /// </para>
    /// <para>
    /// <b>What it is and is not.</b> Real quantisation, real slow updates, a
    /// real heating and cooling profile ACROSS TRANSMIT TRANSITIONS: the
    /// original interval carries eighteen TransmitChange:True records,
    /// including multi-second transmissions, so this is not a receive-only
    /// control (it said it was until Astra's Track I review re-read the
    /// archive). It never reaches 60 C and its endpoint rise is about 8.08 C,
    /// so it exercises no genuine warning and no fan fault. Lowering a threshold to fire on it is
    /// legitimate RULE testing and is labelled as such wherever it is done.
    /// It is not a present-day SmartLink cadence measurement and not proof of
    /// sensor acquisition freshness. Only the numbers are here; the trace
    /// itself, and every other line in it, stays out of this public tree.
    /// </para>
    /// </remarks>
    internal static class PaTemperatureReplayFixture
    {
        /// <summary>The 8600's PATEMP as the 2026-09-21 bench capture described it: index 11, TX-:4, 0 to 120 C.</summary>
        internal static readonly MeterDescriptor Meter = new MeterDescriptor(
            11, "PATEMP", "PA Temperature", "TX-", 4, MeterUnits.DegreesC, 0, 120);

        /// <summary>(trace tick in ms, degrees C), in delivery order.</summary>
        internal static readonly IReadOnlyList<(long Ms, float DegreesC)> Rows = new (long, float)[]
        {
            (56451, 25.703125f), (58569, 25.671875f), (60684, 25.703125f), (62808, 25.734375f),
            (64922, 25.75f), (67035, 25.78125f), (69178, 25.875f), (71330, 27.1875f),
            (73444, 29f), (75557, 28.859375f), (77668, 28.296875f), (79779, 27.78125f),
            (81889, 27.4375f), (84048, 27.390625f), (86177, 28.4375f), (88292, 28.875f),
            (90431, 28.59375f), (92582, 29.484375f), (94717, 30.578125f), (96830, 30.890625f),
            (98940, 30.265625f), (101050, 29.46875f), (103629, 28.90625f), (105927, 28.640625f),
            (108038, 28.375f), (110152, 28.203125f), (112271, 28.078125f), (114389, 27.953125f),
            (116501, 27.859375f), (118617, 27.78125f), (120733, 27.703125f), (122870, 27.65625f),
            (125005, 28.296875f), (127117, 28.984375f), (129229, 28.765625f), (131341, 28.46875f),
            (133486, 28.34375f), (135622, 29.46875f), (137733, 30.046875f), (139846, 29.734375f),
            (141998, 29.4375f), (144129, 30.734375f), (146242, 31.15625f), (148385, 30.828125f),
            (150529, 31.53125f), (152644, 32.53125f), (154793, 32.15625f), (156941, 32.921875f),
            (159051, 33.78125f),
        };

        /// <summary>The series as replayed observations at recorded timing, never invented between rows.</summary>
        internal static IEnumerable<MeterObservation> Observations(int generation = 1)
        {
            long seq = 0;
            var epoch = new DateTime(2026, 8, 23, 1, 35, 46, DateTimeKind.Utc);
            foreach (var (ms, c) in Rows)
                yield return MeterObservation.Replayed(Meter, c, ++seq, ms, epoch.AddMilliseconds(ms), generation, null);
        }

        /// <summary>Largest and smallest gap between consecutive rows, in milliseconds.</summary>
        internal static (long MinGapMs, long MaxGapMs) Gaps()
        {
            long min = long.MaxValue, max = 0;
            for (int i = 1; i < Rows.Count; i++)
            {
                long g = Rows[i].Ms - Rows[i - 1].Ms;
                if (g < min) min = g;
                if (g > max) max = g;
            }
            return (min, max);
        }
    }
}
