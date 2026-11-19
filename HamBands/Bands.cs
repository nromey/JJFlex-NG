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
        public enum BandNames
        {
            m2190, m560, m160, m80, m60, m40, m30, m20, m17, m15, m12, m10,
            m6, m2, m125, m70, m35, m23, m13, m9, m6c, m3c, m125c,
            m6m, m4m, m25m, m2m, m1m,
            NumBands
        }
        private static string[] bandName = new string[] {
            "2190m", "560m", "160m", "80m", "60m", "40m", "30m", 
            "20m", "17m", "15m", "12m", "10m", "6m", "2m", "1.25m",
            "70cm", "35cm", "23cm", "13cm", "9cm", "6cm", "3cm", "1.25cm",
            "6mm", "4mm", "2.5mm", "2mm", "1mm", ""
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
        public enum Modes
        {
            none, CW, MCW, PhoneCW, USB, Image, RTTYData, M60Data
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
            internal BandItem(BandNames b, ulong l, ulong h, BandDivision[] d)
            {
                Band = b;
                Low = l;
                High = h;
                Divisions = d;
            }
            public BandItem BaseCopy()
            {
                return new BandItem(Band, Low, High, null);
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

        /// <summary>
        /// Main band array.
        /// </summary>
        public static BandItem[] TheBands = new BandItem[] {
            new BandItem(BandNames.m2190, 136000, 137000, null),
            new BandItem(BandNames.m560, 501000, 504000, null),
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
            new BandItem(BandNames.m60, 5300000, 5500000, 
                new BandDivision[] {
                    new BandDivision(5300000, 5500000,
                        new Licenses[] {Licenses.general, Licenses.advanced, Licenses.extra},
                        new Modes[] {Modes.USB, Modes.M60Data}),
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
                    new BandDivision(28300000, 28500000,
                        new Licenses[] {Licenses.novice, Licenses.technition},
                        new Modes[] {Modes.PhoneCW}),
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
            new BandItem(BandNames.m2, 144000000, 148000000, null),
            new BandItem(BandNames.m125, 222000000, 225000000, null),
            new BandItem(BandNames.m70, 420000000, 450000000, null),
            new BandItem(BandNames.m35, 902000000, 928000000, null),
            new BandItem(BandNames.m23, 1240000000, 1300000000, null),
            new BandItem(BandNames.m13, 2300000000, 2450000000, null),
            new BandItem(BandNames.m9, 3300000000, 3500000000, null),
            new BandItem(BandNames.m6c, 5650000000, 5925000000, null),
            new BandItem(BandNames.m3c, 10000000000, 10500000000, null),
            new BandItem(BandNames.m125c, 24000000000, 24250000000, null), // 1.25 cm
            new BandItem(BandNames.m6m, 47000000000, 47200000000, null),
            new BandItem(BandNames.m4m, 75500000000, 81000000000, null),
            new BandItem(BandNames.m25m, 119980000000, 120020000000, null), // 2.5mm
            new BandItem(BandNames.m2m, 142000000000, 149000000000, null),
            new BandItem(BandNames.m1m, 241000000000, 250000000000, null)
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
