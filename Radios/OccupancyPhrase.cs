using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// The roster row's answer to "is anyone already on this radio?" (#394),
    /// assembled in one place so the row, the tests, and any future consumer
    /// agree on the words.
    /// </summary>
    /// <remarks>
    /// <para><b>Silence is the common case and it stays silent.</b> Most rows
    /// on most lists have nobody on them, and the row is read in full on every
    /// arrow keypress. An empty clause here costs nothing; a "nobody connected"
    /// clause would cost a phrase per row per keypress, forever, to state the
    /// default. So an unoccupied radio contributes an empty string, and the
    /// display template swallows it whole.</para>
    ///
    /// <para><b>Why this matters before connecting.</b> MultiFlex admits a
    /// second client, but transmit is a mutex — a transmit test against a radio
    /// whose owner is sitting on it simply fails, and the failure does not say
    /// why. The count-plus-name here is the operator's chance to know BEFORE
    /// keying somebody else's transmitter that they will not be alone on it.
    /// It deliberately does not explain MultiFlex: the operator is a licensed
    /// ham with a MultiFlex radio, and the row's job is who, not what.</para>
    ///
    /// <para><b>A client with no station name still counts.</b> The radio
    /// reports a fresh client with an empty station for a moment (the name
    /// always arrives in a later update — #402), and a nameless occupant is
    /// exactly as capable of holding the transmit slice as a named one.</para>
    /// </remarks>
    public static class OccupancyPhrase
    {
        /// <summary>
        /// The row's occupancy clause, leading comma included — or "" when
        /// <paramref name="stations"/> reports no clients. One raw entry per
        /// connected GUI client, "" for a client that has not asserted a
        /// station name yet.
        /// </summary>
        public static string RowSuffix(IReadOnlyList<string> stations)
        {
            if (stations == null || stations.Count == 0) return string.Empty;

            var named = stations
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();

            string phrase;
            if (stations.Count == 1)
            {
                phrase = named.Count == 1
                    ? Lexicon.Get("connect.row.occupied_one_named", ("station", named[0]))
                    : Lexicon.Get("connect.row.occupied_one");
            }
            else
            {
                phrase = named.Count > 0
                    ? Lexicon.Get("connect.row.occupied_many_named",
                        ("count", stations.Count), ("stations", JoinNames(named)))
                    : Lexicon.Get("connect.row.occupied_many",
                        ("count", stations.Count));
            }

            return Lexicon.Get("connect.row.occupancy_suffix", ("occupants", phrase));
        }

        /// <summary>
        /// "k5ner", "k5ner and don", or a comma list beyond two. The pair
        /// joiner lives in the lexicon with the rest of the row's words.
        /// </summary>
        private static string JoinNames(IReadOnlyList<string> named)
        {
            if (named.Count == 1) return named[0];
            if (named.Count == 2)
                return Lexicon.Get("connect.row.occupied_pair",
                    ("first", named[0]), ("second", named[1]));
            return string.Join(", ", named);
        }
    }
}
