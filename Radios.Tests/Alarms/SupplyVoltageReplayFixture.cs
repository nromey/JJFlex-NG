using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// A real supply-voltage series across a keying, sanitised to numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Provenance.</b> Noel's bench FLEX-8600, the detailed capture begun
    /// 2026-09-07 at 08:09:56 local time on build 4.1.16.1952, archived as
    /// <c>trace-20260907-080956-clean_exit.zip</c> under the local AppData
    /// Traces tree; read through 7-Zip without extracting on 2026-09-22 for
    /// Sprint 45 Track I. These are <c>VoltsDataHandler</c> rows — FlexLib's
    /// convenience path, which follows <c>+13.8A</c> only — from trace tick
    /// 370806 through 458495: twelve rows in receive at about 13.98 V, the
    /// client's <c>TransmitChange:True</c> at tick 395360, then thirty rows
    /// under a carrier of two to four watts at about 13.85 V. Cadence 2.11 to
    /// 2.15 seconds throughout. The archive holds 449 such rows in all;
    /// the window here is the one that carries a transition.
    /// </para>
    /// <para>
    /// <b>What it shows.</b> The supply sags by 0.13 to 0.14 V when the
    /// transmitter loads it — a normal, healthy drop, well inside the preset's
    /// 0.5 V line, and exactly what the drop alarm must stay quiet on. It says
    /// nothing about <c>+13.8B</c>, which this build did not trace, and nothing
    /// about a short dip between callbacks, which coalescing would erase.
    /// </para>
    /// </remarks>
    internal static class SupplyVoltageReplayFixture
    {
        /// <summary>The 8600's +13.8A as its own inventory describes it; the source index is the radio's, not Don's.</summary>
        internal static readonly MeterDescriptor Meter = new MeterDescriptor(
            2, "+13.8A", "+13.8V at PA", "RAD", 2, MeterUnits.Volts, 10.5, 15);

        /// <summary>The trace tick at which this client keyed.</summary>
        internal const long TransmitAtMs = 395360;

        /// <summary>(trace tick in ms, volts), in delivery order.</summary>
        internal static readonly IReadOnlyList<(long Ms, float Volts)> Rows = new (long, float)[]
        {
            (370806, 13.980469f), (372916, 13.984375f), (375031, 13.980469f), (377142, 13.984375f),
            (379254, 13.984375f), (381365, 13.984375f), (383478, 13.984375f), (385589, 13.984375f),
            (387702, 13.984375f), (389813, 13.984375f), (391926, 13.984375f), (394040, 13.984375f),
            // TransmitChange:True at 395360
            (396176, 13.855469f), (398323, 13.847656f), (400474, 13.839844f), (402621, 13.855469f),
            (404770, 13.855469f), (406925, 13.8515625f), (409077, 13.847656f), (411231, 13.8515625f),
            (413383, 13.8515625f), (415531, 13.8515625f), (417683, 13.8515625f), (419831, 13.8515625f),
            (421980, 13.8515625f), (424127, 13.84375f), (426276, 13.8515625f), (428423, 13.8515625f),
            (430572, 13.847656f), (432719, 13.8515625f), (434868, 13.847656f), (437015, 13.8515625f),
            (439164, 13.8515625f), (441310, 13.8515625f), (443457, 13.855469f), (445603, 13.855469f),
            (447753, 13.847656f), (449900, 13.847656f), (452048, 13.847656f), (454198, 13.8515625f),
            (456346, 13.8515625f), (458495, 13.8515625f),
        };

        internal static IEnumerable<MeterObservation> Observations(int generation = 1)
        {
            long seq = 0;
            var epoch = new DateTime(2026, 9, 7, 13, 9, 56, DateTimeKind.Utc);
            foreach (var (ms, v) in Rows)
                yield return MeterObservation.Replayed(Meter, v, ++seq, ms, epoch.AddMilliseconds(ms), generation, null);
        }
    }
}
