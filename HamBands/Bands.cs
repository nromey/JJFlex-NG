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
                new BandDivision[] { new BandDivision(135700, 137800, GeneralAndUp, AllEmissions) }),
            // 630 m, 472 to 479 kHz, General and up (97.301(b) to (d); 97.305(c)(2)).
            // The member is still named m560 for band memory's sake; 501 to 504 kHz,
            // which it used to hold, is not an amateur allocation.
            new BandItem(BandNames.m560, 472000, 479000,
                new BandDivision[] { new BandDivision(472000, 479000, GeneralAndUp, AllEmissions) }),
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
                    if (b.Mode != null)
                    {
                        foreach (Modes m in b.Mode)
                        {
                            if (m == mode)
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
                    if (b.Mode != null)
                    {
                        foreach (Modes m in b.Mode)
                        {
                            if (m == mode)
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
    }
}
