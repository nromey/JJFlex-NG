using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;

namespace HamBands
{
    /// <summary>
    /// Band and License class data.
    /// </summary>
    public static class Bands
    {
        /// <summary>
        /// HamBands.dll version
        /// </summary>
        public static Version Version
        {
            get
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                AssemblyName asmName = asm.GetName();
                return asmName.Version;
            }
        }

        /// <summary>
        /// internal band names
        /// </summary>
        /// <remarks>
        /// <para><b>Never rename, reorder or remove a member</b> (#1282).
        /// <c>Radios/BandMemory.cs</c> saves a band by its NAME, so a name it
        /// no longer knows makes the whole band memory fail to load, and the
        /// next save writes the empty memory over it. That is why <c>m560</c>
        /// now holds the 630 m band and <c>m2190</c> the 2200 m band, and why
        /// <c>m9</c> stays although 3.3 to 3.5 GHz is no longer an amateur band
        /// (it is <see cref="BandItem.Retired"/>). A new band is added at the
        /// end, before <c>NumBands</c>, with its <c>bandName</c> entry in the
        /// same place.</para>
        /// </remarks>
        public enum BandNames
        {
            m2190, m560, m160, m80, m60, m40, m30, m20, m17, m15, m12, m10,
            m6, m2, m125, m70, m35, m23, m13, m9, m6c, m3c, m125c,
            m6m, m4m, m25m, m2m, m1m,
            mAbove275g,
            NumBands
        }
        // The names shown in the Band Plans dialog, by enum value. Display
        // only: band memory stores the enum member's name, never these
        // (#1282: 2200m, 630m, 33cm, 5cm and 1.2cm corrected to Part 97's
        // names; "9cm" kept for the retired slot).
        private static string[] bandName = new string[] {
            "2200m", "630m", "160m", "80m", "60m", "40m", "30m",
            "20m", "17m", "15m", "12m", "10m", "6m", "2m", "1.25m",
            "70cm", "33cm", "23cm", "13cm", "9cm", "5cm", "3cm", "1.2cm",
            "6mm", "4mm", "2.5mm", "2mm", "1mm", "above 275GHz", ""
        };

        /// <summary>
        /// License classes
        /// </summary>
        public enum Licenses
        {
            none, novice, technition, general, advanced, extra
        }

        /// <summary>
        /// modes
        /// </summary>
        /// <remarks>
        /// Appended at the end only: the Band Plans dialog casts its list
        /// positions to this enum. <c>SSB</c> (#1310) marks a division where
        /// single-sideband voice is the only phone (J3E or R3E, 97.307(f)(10),
        /// Novice and Technician in 28.3 to 28.5 MHz); <c>Data</c> marks one
        /// where data is the only emission (219 to 220 MHz, 97.305(c)(4)(iv)).
        /// CW is allowed anywhere in a class's privileges (97.305(a)), so a
        /// division need not list it for CW to be judged lawful there.
        /// </remarks>
        public enum Modes
        {
            none, CW, MCW, PhoneCW, USB, Image, RTTYData, M60Data,
            SSB, Data
        }

        /// <summary>
        /// Band division class
        /// </summary>
        public class BandDivision
        {
            /// <summary>
            /// Low and High frequency for this division
            /// </summary>
            public ulong Low, High;
            /// <summary>
            /// Applicable licenses.
            /// </summary>
            public Licenses[] License;
            /// <summary>
            /// Applicable Modes
            /// </summary>
            public Modes[] Mode;
            internal BandDivision(ulong l, ulong h, Licenses[] li, Modes[] m)
            {
                Low = l;
                High = h;
                License = li;
                Mode = m;
            }
            internal BandDivision(BandDivision d)
            {
                Low = d.Low;
                High = d.High;
                License = d.License;
                Mode = d.Mode;
            }
        }

        /// <summary>
        /// Band class
        /// </summary>
        public class BandItem
        {
            /// <summary>
            /// Band enumeration
            /// </summary>
            public BandNames Band;
            /// <summary>
            /// entry's ID
            /// </summary>
            public int ID { get { return (int)Band; } }
            /// <summary>
            /// Band name
            /// </summary>
            public string Name { get { return bandName[(int)Band]; } }
            /// <summary>
            /// Low and High frequencies
            /// </summary>
            public ulong Low, High;
            /// <summary>
            /// Array of band divisions
            /// </summary>
            public BandDivision[] Divisions;
            /// <summary>
            /// True for a band that is no longer an amateur allocation but keeps
            /// its slot (#1282): <see cref="Query(ulong)"/> never finds it and the
            /// Band Plans dialog does not list it, while band memory can still
            /// read its name. Only 9 cm (3.3 to 3.5 GHz) today.
            /// </summary>
            public bool Retired { get; private set; }
            internal BandItem(BandNames b, ulong l, ulong h, BandDivision[] d)
            {
                Band = b;
                Low = l;
                High = h;
                Divisions = d;
            }
            internal BandItem(BandNames b, ulong l, ulong h, BandDivision[] d, bool retired)
                : this(b, l, h, d)
            {
                Retired = retired;
            }
            public BandItem BaseCopy()
            {
                return new BandItem(Band, Low, High, null, Retired);
            }
            public BandItem Copy()
            {
                BandItem rv = BaseCopy();
                if (Divisions != null)
                {
                    rv.Divisions = new BandDivision[Divisions.Length];
                    for (int i = 0; i < rv.Divisions.Length; i++)
                    {
                        rv.Divisions[i] = new BandDivision(Divisions[i]);
                    }
                }
                else rv.Divisions = null;
                return rv;
            }
        }

        // 97.301(a): Technician, General, Advanced and Extra share every band
        // from 6 m up; a Novice has only the two 97.301(e) pieces above 30 MHz.
        private static readonly Licenses[] TechnicianAndUp =
            { Licenses.technition, Licenses.general, Licenses.advanced, Licenses.extra };
        private static readonly Licenses[] GeneralAndUp =
            { Licenses.general, Licenses.advanced, Licenses.extra };
        private static readonly Licenses[] NoviceAndUp =
            { Licenses.novice, Licenses.technition, Licenses.general, Licenses.advanced, Licenses.extra };
        private static readonly Licenses[] NoviceOnly = { Licenses.novice };
        // Every emission (97.305(c)(4) and (5) and up, and (c)(1) and (2) for
        // 2200 m and 630 m): phone first, because the sub-band notice and the
        // Band Plans dialog name a division by its first mode.
        private static readonly Modes[] AllEmissions =
            { Modes.PhoneCW, Modes.RTTYData, Modes.Image, Modes.MCW };
        // 2200 m and 630 m (97.305(c)(1) and (2)): phone, RTTY, data and image,
        // and CW everywhere under 97.305(a). No MCW: MCW is not unmodulated CW,
        // and neither paragraph grants it (repair round 1 of batch 31; MCW stays
        // on VHF and up, where 97.305(c)(4) and up grant it).
        private static readonly Modes[] LowFrequencyEmissions =
            { Modes.PhoneCW, Modes.RTTYData, Modes.Image };
        // 60 m (97.303(h)(3), 97.305(c)(3)(iii), 97.307(f)(14)): phone, RTTY,
        // data and CW, never wider than 2.8 kHz. The lockout judges the class's
        // privileges and the allocation's edges only, so phone is marked as
        // phone (repair round 1 of batch 31): which phone goes on a channel,
        // the dial per mode, and the 2.8 kHz width in the segment are the 60 m
        // rule's ("Enforce 60 meter transmit rules"). Modes.USB is not kept:
        // the Band Plans "USB" filter finds phone divisions (Bands.Allows).
        private static readonly Modes[] SixtyMeterEmissions =
            { Modes.PhoneCW, Modes.RTTYData, Modes.M60Data };

        /// <summary>
        /// One 60 m channel's division: its centre plus and minus 1.4 kHz, the
        /// 2.8 kHz channel 97.303(h)(3) describes. The centres are
        /// <c>Radios.SixtyMeterChannels.USChannels</c>'s (this project cannot
        /// see that one; <c>SixtyMeterLockoutTests</c> holds the two equal).
        /// </summary>
        private static BandDivision SixtyMeterChannel(ulong centreHz) =>
            new BandDivision(centreHz - 1400, centreHz + 1400, GeneralAndUp, SixtyMeterEmissions);

        /// <summary>
        /// Main band array.
        /// </summary>
        /// <remarks>
        /// <para><b>US privileges, ITU Region 2</b>, from 47 CFR 97.301 and
        /// 97.305 as current on 2026-11-20 (the Part 97 check of 2026-11-24).
        /// Ruled by Noel 2026-11-24 for 2 m and up (#1282); the 2200 m and
        /// 630 m corrections and the 60 m narrowing are the orchestrator's
        /// decisions from that check.</para>
        /// <para><b>Position equals the enum's value</b> for every entry (the
        /// retired 9 cm keeps its slot), so nothing that reads this by
        /// position can drift from <see cref="BandNames"/>.</para>
        /// </remarks>
        public static BandItem[] TheBands = new BandItem[] {
            // 2200 m, 135.7 to 137.8 kHz, General and up (97.301(b) to (d); 97.305(c)(1)).
            new BandItem(BandNames.m2190, 135700, 137800,
                new BandDivision[] { new BandDivision(135700, 137800, GeneralAndUp, LowFrequencyEmissions) }),
            // 630 m, 472 to 479 kHz, General and up (97.301(b) to (d); 97.305(c)(2)).
            // The member is still named m560 for band memory's sake; 501 to 504 kHz,
            // which it used to hold, is not an amateur allocation.
            new BandItem(BandNames.m560, 472000, 479000,
                new BandDivision[] { new BandDivision(472000, 479000, GeneralAndUp, LowFrequencyEmissions) }),
            new BandItem(BandNames.m160, 1800000, 2000000,
                new BandDivision[] {
                    new BandDivision(1800000, 2000000,
                        new Licenses[] {Licenses.general,Licenses.advanced,Licenses.extra},
                        new Modes[] {Modes.CW, Modes.PhoneCW, Modes.RTTYData})}),
            new BandItem(BandNames.m80, 3500000, 4000000,
                new BandDivision[] {
                    new BandDivision(3525000, 3600000,
                        new Licenses[] {Licenses.novice, Licenses.technition},
                        new Modes[] {Modes.CW}),
                    new BandDivision(3525000, 3600000,
                        new Licenses[] {Licenses.general, Licenses.advanced},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(3800000, 4000000,
                        new Licenses[] {Licenses.general},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(3700000, 4000000,
                        new Licenses[] {Licenses.advanced},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(3500000, 3600000,
                        new Licenses[] {Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(3600000, 4000000,
                        new Licenses[] {Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                }),
            // 60 m: the four channels and the segment, 5351.5 to 5366.5 kHz
            // (97.303(h)(3)). The lockout judges only edges; the exact dial for
            // a mode on a channel is the 60 m rule's (Enforce transmit rules).
            new BandItem(BandNames.m60, 5300000, 5500000,
                new BandDivision[] {
                    SixtyMeterChannel(5332000),
                    SixtyMeterChannel(5348000),
                    new BandDivision(5351500, 5366500, GeneralAndUp, SixtyMeterEmissions),
                    SixtyMeterChannel(5373000),
                    SixtyMeterChannel(5405000),
                }),
            new BandItem(BandNames.m40, 7000000, 7300000,
                new BandDivision[] {
                    new BandDivision(7025000, 7125000,
                        new Licenses[] {Licenses.novice, Licenses.technition},
                        new Modes[] {Modes.CW}),
                    new BandDivision(7025000, 7125000,
                        new Licenses[] {Licenses.general, Licenses.advanced},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(7175000, 7300000,
                        new Licenses[] {Licenses.general},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(7125000, 7300000,
                        new Licenses[] {Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(7000000, 7125000,
                        new Licenses[] {Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                }),
            new BandItem(BandNames.m30, 10100000, 10150000,
                new BandDivision[] {
                    new BandDivision(10100000, 10150000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                }),
            new BandItem(BandNames.m20, 14000000, 14350000,
                new BandDivision[] {
                    new BandDivision(14025000, 14150000,
                        new Licenses[] {Licenses.general, Licenses.advanced},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(14225000, 14350000,
                        new Licenses[] {Licenses.general},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(14175000, 14350000,
                        new Licenses[] {Licenses.advanced},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(14000000, 14150000,
                        new Licenses[] {Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(14150000, 14350000,
                        new Licenses[] {Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                }),
            new BandItem(BandNames.m17, 18068000, 18168000,
                new BandDivision[] {
                    new BandDivision(18068000, 18110000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(18110000, 18168000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                }),
            new BandItem(BandNames.m15, 21000000, 21450000,
                new BandDivision[] {
                    new BandDivision(21025000, 21200000,
                        new Licenses[] {Licenses.novice, Licenses.technition},
                        new Modes[] {Modes.CW}),
                    new BandDivision(21025000, 21200000,
                        new Licenses[] {Licenses.general, Licenses.advanced},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(21275000, 21450000,
                        new Licenses[] {Licenses.general},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(21225000, 21450000,
                        new Licenses[] {Licenses.advanced},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                    new BandDivision(21000000, 21200000,
                        new Licenses[] {Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(21200000, 21450000,
                        new Licenses[] {Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                }),
            new BandItem(BandNames.m12, 24890000, 24990000,
                new BandDivision[] {
                    new BandDivision(24890000, 24930000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(24930000, 24990000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                }),
            new BandItem(BandNames.m10, 28000000, 29700000,
                new BandDivision[] {
                    new BandDivision(28000000, 28300000,
                        new Licenses[] {Licenses.novice, Licenses.technition},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    new BandDivision(28000000, 28300000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.CW, Modes.RTTYData}),
                    // CW and single-sideband voice only (97.307(f)(10)).
                    new BandDivision(28300000, 28500000,
                        new Licenses[] {Licenses.novice, Licenses.technition},
                        new Modes[] {Modes.SSB}),
                    new BandDivision(28300000, 29700000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.Image}),
                }),
            new BandItem(BandNames.m6, 50000000, 54000000,
                new BandDivision[] {
                    new BandDivision(50000000, 50100000,
                        new Licenses[] {Licenses.technition, Licenses.general,
                            Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.CW}),
                    new BandDivision(50100000, 54000000,
                        new Licenses[] {Licenses.technition, Licenses.general,
                            Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.PhoneCW, Modes.RTTYData, Modes.Image, Modes.MCW}),
                }),
            // 2 m: CW only below 144.1 MHz (97.305(c)(4)(iii)).
            new BandItem(BandNames.m2, 144000000, 148000000,
                new BandDivision[] {
                    new BandDivision(144000000, 144100000, TechnicianAndUp, new Modes[] {Modes.CW}),
                    new BandDivision(144100000, 148000000, TechnicianAndUp, AllEmissions),
                }),
            // 1.25 m: 219 to 220 MHz data only (97.303(l), 97.305(c)(4)(iv)), not
            // for a Novice (97.301(e)); 222 to 225 MHz for every class.
            new BandItem(BandNames.m125, 219000000, 225000000,
                new BandDivision[] {
                    new BandDivision(219000000, 220000000, TechnicianAndUp, new Modes[] {Modes.Data}),
                    new BandDivision(222000000, 225000000, NoviceAndUp, AllEmissions),
                }),
            new BandItem(BandNames.m70, 420000000, 450000000,
                new BandDivision[] { new BandDivision(420000000, 450000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m35, 902000000, 928000000,
                new BandDivision[] { new BandDivision(902000000, 928000000, TechnicianAndUp, AllEmissions) }),
            // 23 cm: a Novice has 1270 to 1295 MHz only (97.301(e)).
            new BandItem(BandNames.m23, 1240000000, 1300000000,
                new BandDivision[] {
                    new BandDivision(1240000000, 1300000000, TechnicianAndUp, AllEmissions),
                    new BandDivision(1270000000, 1295000000, NoviceOnly, AllEmissions),
                }),
            // 13 cm is two pieces (97.301(a)).
            new BandItem(BandNames.m13, 2300000000, 2450000000,
                new BandDivision[] {
                    new BandDivision(2300000000, 2310000000, TechnicianAndUp, AllEmissions),
                    new BandDivision(2390000000, 2450000000, TechnicianAndUp, AllEmissions),
                }),
            // 9 cm (3.3 to 3.5 GHz) is no longer in 97.301: retired, slot kept.
            new BandItem(BandNames.m9, 3300000000, 3500000000, null, true),
            new BandItem(BandNames.m6c, 5650000000, 5925000000,
                new BandDivision[] { new BandDivision(5650000000, 5925000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m3c, 10000000000, 10500000000,
                new BandDivision[] { new BandDivision(10000000000, 10500000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m125c, 24000000000, 24250000000, // 1.2 cm
                new BandDivision[] { new BandDivision(24000000000, 24250000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m6m, 47000000000, 47200000000,
                new BandDivision[] { new BandDivision(47000000000, 47200000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m4m, 76000000000, 81000000000,
                new BandDivision[] { new BandDivision(76000000000, 81000000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m25m, 122250000000, 123000000000, // 2.5mm
                new BandDivision[] { new BandDivision(122250000000, 123000000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m2m, 134000000000, 141000000000,
                new BandDivision[] { new BandDivision(134000000000, 141000000000, TechnicianAndUp, AllEmissions) }),
            new BandItem(BandNames.m1m, 241000000000, 250000000000,
                new BandDivision[] { new BandDivision(241000000000, 250000000000, TechnicianAndUp, AllEmissions) }),
            // Everything above 275 GHz (97.301(a)); 3 THz stands for "and up".
            new BandItem(BandNames.mAbove275g, 275000000000, 3000000000000,
                new BandDivision[] { new BandDivision(275000000000, 3000000000000, TechnicianAndUp, AllEmissions) })
        };

        // ---------------- ADIF ----------------

        // ADIF 3.1.8's Band enumeration (adif.org.uk/318, read 2026-11-27),
        // its own names and edges in hertz, both edges inclusive. Not ours:
        // ADIF's bands are not Part 97's (it has 8 m, 5 m and 4 m, 60 m from
        // 5.06 MHz, 9 cm and 560 m, and nothing for 219 to 220 MHz or 275 to
        // 300 GHz), which is why this is a function of frequency and never a
        // BandItem member: m125 alone spans two ADIF answers.
        private static readonly (string Name, ulong Low, ulong High)[] AdifBands =
        {
            ("2190m", 135_700, 137_800),
            ("630m", 472_000, 479_000),
            ("560m", 501_000, 504_000),
            ("160m", 1_800_000, 2_000_000),
            ("80m", 3_500_000, 4_000_000),
            ("60m", 5_060_000, 5_450_000),
            ("40m", 7_000_000, 7_300_000),
            ("30m", 10_100_000, 10_150_000),
            ("20m", 14_000_000, 14_350_000),
            ("17m", 18_068_000, 18_168_000),
            ("15m", 21_000_000, 21_450_000),
            ("12m", 24_890_000, 24_990_000),
            ("10m", 28_000_000, 29_700_000),
            ("8m", 40_000_000, 45_000_000),
            ("6m", 50_000_000, 54_000_000),
            ("5m", 54_000_001, 69_900_000),
            ("4m", 70_000_000, 71_000_000),
            ("2m", 144_000_000, 148_000_000),
            ("1.25m", 222_000_000, 225_000_000),
            ("70cm", 420_000_000, 450_000_000),
            ("33cm", 902_000_000, 928_000_000),
            ("23cm", 1_240_000_000, 1_300_000_000),
            ("13cm", 2_300_000_000, 2_450_000_000),
            ("9cm", 3_300_000_000, 3_500_000_000),
            ("6cm", 5_650_000_000, 5_925_000_000),
            ("3cm", 10_000_000_000, 10_500_000_000),
            ("1.25cm", 24_000_000_000, 24_250_000_000),
            ("6mm", 47_000_000_000, 47_200_000_000),
            ("4mm", 75_500_000_000, 81_000_000_000),
            ("2.5mm", 119_980_000_000, 123_000_000_000),
            ("2mm", 134_000_000_000, 149_000_000_000),
            ("1mm", 241_000_000_000, 250_000_000_000),
            ("submm", 300_000_000_000, 7_500_000_000_000),
        };

        /// <summary>
        /// The ADIF BAND value for a frequency, in hertz: ADIF 3.1.8's own name
        /// for the band it falls in, or "" where ADIF defines no band (ADIF allows
        /// FREQ without BAND). Pure. For the logger and the ADIF export only
        /// (repair round 1 of junk box batch 31): <see cref="BandItem.Name"/> is
        /// the display label and is not an ADIF value ("2200m", "33cm" and "5cm"
        /// are Part 97's names; ADIF's are 2190m, 33cm and 6cm).
        /// </summary>
        public static string AdifBand(ulong hz)
        {
            foreach (var b in AdifBands)
                if (hz >= b.Low && hz <= b.High) return b.Name;
            return "";
        }

        // A BAND value the logger has written that is not an ADIF 3.1.8 name,
        // and the one ADIF band its whole range lies in. Built from the band
        // table's history (repair round 2 of junk box batch 31), which in this
        // repository begins at e68dabc5 (2024-03-03): that is a recorded limit,
        // not a search that stopped. On main the only such label is "35cm"
        // (902 to 928 MHz, ADIF's 33cm). "2200m", "5cm" and "1.2cm" were written
        // only by this unmerged branch, so no operator's log holds them; they
        // map one to one anyway. "above 275GHz" (275 GHz and up) spans a range
        // with no ADIF band and ADIF's submm, so it gets no alias.
        private static readonly (string Label, string Adif)[] BandLabelAliases =
        {
            ("35cm", "33cm"),
            ("2200m", "2190m"),
            ("5cm", "6cm"),
            ("1.2cm", "1.25cm"),
        };

        /// <summary>
        /// One logged contact's band, for comparing contacts and for the ADIF
        /// export: the logged BAND when there is one (through the alias list
        /// above, case ignored, else exactly as logged), and when the logged BAND
        /// is empty, ADIF's band (<see cref="AdifBand"/>) for the transmit
        /// frequency, or for the receive frequency only when the transmit one is
        /// missing (0). Pure. Nothing stored is rewritten (repair round 2 of junk
        /// box batch 31: an old 35cm entry and a new 33cm one, or an old entry
        /// the parent logged with no BAND at 475 kHz and a new 630m one, now
        /// compare equal).
        /// </summary>
        /// <param name="loggedBand">The record's BAND, as stored; null reads as empty.</param>
        /// <param name="txHz">The record's TXFREQ in hertz, 0 when missing, unparseable or zero.</param>
        /// <param name="rxHz">The record's FREQ (the receive frequency in this log) in hertz, 0 likewise.</param>
        /// <remarks>
        /// A nonzero transmit frequency that gives no ADIF band leaves the key
        /// empty and never falls back to the receive frequency: that would hand a
        /// cross-band contact its receive band, the defect this exists to end.
        /// </remarks>
        public static string ContactBandKey(string loggedBand, ulong txHz, ulong rxHz)
        {
            string logged = loggedBand ?? "";
            string trimmed = logged.Trim();
            if (trimmed.Length > 0)
            {
                foreach (var alias in BandLabelAliases)
                    if (string.Equals(trimmed, alias.Label, StringComparison.OrdinalIgnoreCase)) return alias.Adif;
                return logged;
            }
            if (txHz != 0) return AdifBand(txHz);
            if (rxHz != 0) return AdifBand(rxHz);
            return "";
        }

        /// <summary>
        /// Return the band object
        /// </summary>
        /// <param name="frequency">in HZ</param>
        /// <returns>the associated BandItem object</returns>
        public static BandItem Query(ulong frequency)
        {
            BandItem rv = null;
            foreach (BandItem item in TheBands)
            {
                if (item.Retired) continue;   // #1282: no longer an amateur band
                if ((item.Low <= frequency) && (item.High >= frequency))
                {
                    rv = item.Copy();
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Return the band object.
        /// </summary>
        /// <param name="band">enumeration</param>
        /// <returns>The associated BandItem object</returns>
        public static BandItem Query(BandNames band)
        {
            BandItem rv = null;
            foreach (BandItem b in TheBands)
            {
                if (band == b.Band)
                {
                    rv = b.Copy();
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Return the band object.
        /// </summary>
        /// <param name="band">enumeration</param>
        /// <param name="license">Licenses enumeration</param>
        /// <returns>The associated BandItem object</returns>
        public static BandItem Query(BandNames band, Licenses license)
        {
            BandItem rv = Query(band);
            // If no divisions, return the object anyway.
            if ((rv != null) && (rv.Divisions != null))
            {
                Collection<BandDivision> c = new Collection<BandDivision>();
                foreach (BandDivision b in rv.Divisions)
                {
                    if (b.License != null)
                    {
                        foreach (Licenses l in b.License)
                        {
                            if (l == license)
                            {
                                c.Add(b);
                                break;
                            }
                        }
                    }
                    else c.Add(b);
                }
                if (c.Count > 0)
                {
                    rv.Divisions = new BandDivision[c.Count];
                    c.CopyTo(rv.Divisions, 0);
                }
                else
                {
                    rv.Divisions = null;
                }
            }
            return rv;
        }

        /// <summary>
        /// Return the band object.
        /// </summary>
        /// <param name="band">enumeration</param>
        /// <param name="mode">Modes enumeration</param>
        /// <returns>The associated BandItem object</returns>
        public static BandItem Query(BandNames band, Modes mode)
        {
            BandItem rv = Query(band);
            // If no divisions, return the object anyway.
            if ((rv != null) && (rv.Divisions != null))
            {
                Collection<BandDivision> c = new Collection<BandDivision>();
                foreach (BandDivision b in rv.Divisions)
                {
                    if (DivisionAllows(b, mode)) c.Add(b);
                }
                if (c.Count > 0)
                {
                    rv.Divisions = new BandDivision[c.Count];
                    c.CopyTo(rv.Divisions, 0);
                }
                else
                {
                    rv.Divisions = null;
                }
            }
            return rv;
        }

        /// <summary>
        /// Return the band object.
        /// </summary>
        /// <param name="band">enumeration</param>
        /// <param name="license">Licenses enumeration</param>
        /// <param name="mode">Modes enumeration</param>
        /// <returns>The associated BandItem object</returns>
        public static BandItem Query(BandNames band, Licenses license, Modes mode)
        {
            BandItem rv = Query(band, license);
            // If no divisions, return the object anyway.
            if ((rv != null) && (rv.Divisions != null))
            {
                Collection<BandDivision> c = new Collection<BandDivision>();
                foreach (BandDivision b in rv.Divisions)
                {
                    if (DivisionAllows(b, mode)) c.Add(b);
                }
                if (c.Count > 0)
                {
                    rv.Divisions = new BandDivision[c.Count];
                    c.CopyTo(rv.Divisions, 0);
                }
                else
                {
                    rv.Divisions = null;
                }
            }
            return rv;
        }
    
        // ---------------- the Band Plans browser's mode filter ----------------

        /// <summary>
        /// Whether a division marked <paramref name="divisionMark"/> is a match
        /// for the Band Plans browser's mode filter <paramref name="filter"/>
        /// (repair round 1 of junk box batch 31; both <see cref="Query(BandNames, Modes)"/>
        /// and <see cref="Query(BandNames, Licenses, Modes)"/> ask it, through
        /// <see cref="DivisionAllows"/>). Pure.
        /// </summary>
        /// <remarks>
        /// <para><b>The browser's meaning, deliberately coarser than the transmit
        /// judge's</b> (<c>Radios.TransmitBandLockout.Permits</c>, which is not
        /// changed by this and must not be made to agree with it: agreement would
        /// loosen what the lockout admits, for example AM in a Technician's 28.3 to
        /// 28.5 MHz single-sideband segment). The mapping is the orchestrator's
        /// decision:</para>
        /// <list type="bullet">
        /// <item>none and CW: every division (CW is allowed throughout a class's
        /// privileges, 97.305(a)); see <see cref="DivisionAllows"/>.</item>
        /// <item>MCW: MCW divisions only.</item>
        /// <item>Phone and CW, USB, and SSB: the phone divisions only (PhoneCW, USB,
        /// SSB). Not CW everywhere: Band Plans preselects Phone and CW for every
        /// slice that is not CW, and a phone operator must not see the CW-only
        /// segments as matches.</item>
        /// <item>Image: Image.</item>
        /// <item>RTTY and Data: RTTYData and M60Data.</item>
        /// <item>Data: RTTYData, M60Data and Data.</item>
        /// <item>Data on 60 meters: M60Data.</item>
        /// </list>
        /// </remarks>
        public static bool Allows(Modes divisionMark, Modes filter)
        {
            switch (filter)
            {
                case Modes.none:
                case Modes.CW:
                    return true;
                case Modes.MCW:
                    return divisionMark == Modes.MCW;
                case Modes.PhoneCW:
                case Modes.USB:
                case Modes.SSB:
                    return divisionMark is Modes.PhoneCW or Modes.USB or Modes.SSB;
                case Modes.Image:
                    return divisionMark == Modes.Image;
                case Modes.RTTYData:
                    return divisionMark is Modes.RTTYData or Modes.M60Data;
                case Modes.Data:
                    return divisionMark is Modes.RTTYData or Modes.M60Data or Modes.Data;
                case Modes.M60Data:
                    return divisionMark == Modes.M60Data;
                default:
                    return divisionMark == filter;
            }
        }

        /// <summary>
        /// Whether a division is a match for the browser's mode filter: every
        /// division for none and CW, and a division that lists no modes for any
        /// filter; otherwise any of its marks <see cref="Allows"/> the filter.
        /// </summary>
        private static bool DivisionAllows(BandDivision d, Modes filter)
        {
            if (filter == Modes.none || filter == Modes.CW) return true;
            if (d.Mode == null || d.Mode.Length == 0) return true;
            foreach (var m in d.Mode)
                if (Allows(m, filter)) return true;
            return false;
        }
    }
}
