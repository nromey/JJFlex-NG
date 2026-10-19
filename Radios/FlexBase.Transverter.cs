#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, transverter (XVTR) power.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region Transverter (XVTR) Power — QB Track I

        // Transverter definitions reported by the radio ("sub xvtr all" is part
        // of the FlexLib connect sequence, so XvtrAdded/XvtrRemoved fire without
        // any extra subscription). Radio._xvtrs is private with no public list
        // accessor, so we mirror it here via the public events.
        private readonly List<Xvtr> myXvtrs = new List<Xvtr>();

        private void xvtrAdded(Xvtr xvtr)
        {
            lock (myXvtrs)
            {
                if (!myXvtrs.Contains(xvtr)) myXvtrs.Add(xvtr);
            }
            Tracing.TraceLine($"xvtrAdded: index={xvtr.Index} name={xvtr.Name} rf={xvtr.RFFreq}", TraceLevel.Info);
        }

        private void xvtrRemoved(Xvtr xvtr)
        {
            lock (myXvtrs)
            {
                myXvtrs.Remove(xvtr);
            }
            Tracing.TraceLine($"xvtrRemoved: index={xvtr.Index} name={xvtr.Name}", TraceLevel.Info);
        }

        /// <summary>
        /// True when the transmit slice's TX antenna is the transverter port.
        /// Power surfaces switch from watts to dBm drive in this state — mixer
        /// overdrive is the classic transverter killer, and the radio's own
        /// design puts fine drive control (hundredths of a dB) only here.
        /// </summary>
        public bool TXAntennaIsTransverter =>
            string.Equals(TXAntennaName, "XVTR", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The transverter definition covering the transmit slice's frequency
        /// (the selected slice's when nothing transmits — see
        /// <see cref="SliceForTransmitSetting()"/>), or null. It is the transmit
        /// slice's band that the drive in dBm goes to, so it must be the
        /// transmit slice's frequency that names the band.
        /// Selection: among valid XVTRs whose RF start frequency is at or
        /// below the slice frequency, the highest start wins (an XVTR band has a
        /// start but no reported width). Falls back to the only defined XVTR
        /// when exactly one exists.
        /// </summary>
        public Xvtr ActiveXvtr
        {
            get
            {
                double freqMHz = SliceForTransmitSetting()?.Freq ?? 0.0;
                lock (myXvtrs)
                {
                    if (myXvtrs.Count == 0) return null;
                    Xvtr best = null;
                    foreach (var x in myXvtrs)
                    {
                        if (!x.Valid) continue;
                        if (x.RFFreq > freqMHz + 0.000001) continue;
                        if (best == null || x.RFFreq > best.RFFreq) best = x;
                    }
                    if (best != null) return best;
                    // No band match — a single defined XVTR is unambiguous.
                    return (myXvtrs.Count == 1) ? myXvtrs[0] : null;
                }
            }
        }

        /// <summary>True when the TX antenna is the XVTR port AND we can resolve which transverter it drives.</summary>
        public bool XvtrPowerAvailable => TXAntennaIsTransverter && ActiveXvtr != null;

        /// <summary>Name of the active transverter definition ("2M", "70CM"...), empty if none.</summary>
        public string ActiveXvtrName => ActiveXvtr?.Name ?? string.Empty;

        // Drive is carried in hundredths of a dBm (centi-dBm) so integer-based
        // UI controls can adjust it; Xvtr.MaxPower itself is a double in dBm.
        public const int XvtrDriveMinCentiDbm = -1000;         // -10.00 dBm, FlexLib floor
        public const int XvtrDriveIncrementCentiDbm = 10;      // 0.10 dB per step

        /// <summary>
        /// Upper drive limit in centi-dBm, mirroring the FlexLib Xvtr.MaxPower
        /// clamp: IF below 80 MHz allows +10 dBm on 6400/6600 (else +15);
        /// IF at or above 80 MHz allows +8 dBm. The vendor setter clamps
        /// again, so this is a UI bound, not the enforcement point.
        /// </summary>
        public int XvtrDriveMaxCentiDbm
        {
            get
            {
                var x = ActiveXvtr;
                if (x == null) return 1000;
                string model = theRadio?.Model ?? string.Empty;
                double limit;
                if (x.IFFreq < 80.0)
                {
                    limit = (model == "FLEX-6400M" || model == "FLEX-6400"
                          || model == "FLEX-6600M" || model == "FLEX-6600") ? 10.0 : 15.0;
                }
                else
                {
                    limit = 8.0;
                }
                return (int)Math.Round(limit * 100.0);
            }
        }

        /// <summary>
        /// Transverter drive power in centi-dBm (e.g. 550 = 5.50 dBm). Reads and
        /// writes the active transverter's MaxPower. No-op when no transverter
        /// is resolvable.
        /// </summary>
        public int XvtrDrivePowerCentiDbm
        {
            get
            {
                var x = ActiveXvtr;
                return (x == null) ? 0 : (int)Math.Round(x.MaxPower * 100.0);
            }
            set
            {
                var x = ActiveXvtr;
                if (x == null) return;
                double dbm = value / 100.0;
                q.Enqueue((FunctionDel)(() => { x.MaxPower = dbm; }), "XvtrDrive");
            }
        }

        #endregion
    }
}
