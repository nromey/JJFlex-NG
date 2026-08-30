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
    /// <para><b>Zero speaks, deliberately — this reverses the first design.</b>
    /// The clause originally stayed silent when nobody was connected, on the
    /// theory that silence costs nothing on a row read aloud at every arrow
    /// keypress. Noel hit the real cost within hours, looking at his tester's
    /// radio while deciding whether to transmit on it: <i>"I'm not seeing that
    /// Don's connected or no one's connected."</i> Silence is indistinguishable
    /// from a feature that is not working, and that ambiguity peaks at the
    /// exact moment the answer matters most — standing in front of a foreign
    /// radio about to key it. So every live row states its count, zero
    /// included: "online with 0 connected clients" (#394, #391 — his words,
    /// 2026-08-30). The clause's absence now means exactly one thing: the row
    /// is not live, so there is no current knowledge to count.</para>
    ///
    /// <para><b>Why this matters before connecting.</b> MultiFlex admits a
    /// second client, but transmit is a mutex — a transmit test against a radio
    /// whose owner is sitting on it simply fails, and the failure does not say
    /// why. The count-plus-callsign here is the operator's chance to know
    /// BEFORE keying somebody else's transmitter that they will not be alone
    /// on it. It deliberately does not explain MultiFlex: the operator is a
    /// licensed ham with a MultiFlex radio, and the row's job is who, not
    /// what.</para>
    ///
    /// <para><b>Count first, callsigns in parentheses.</b> "online with 1
    /// connected client (wa2iwc)" — the count is the constant-shape part an
    /// operator can rely on hearing in the same place on every visit, and the
    /// station names ride behind it. Stations are named by callsign because
    /// that is what gui_client_stations carries for an amateur station and how
    /// operators identify each other; there is no per-client account to fall
    /// back to, so a client with no station name simply goes unnamed while
    /// still being counted. More than two are listed, not truncated — on a
    /// crowded radio, WHO is the whole point.</para>
    ///
    /// <para><b>A client with no station name still counts.</b> The radio
    /// reports a fresh client with an empty station for a moment (the name
    /// always arrives in a later update — #402), and a nameless occupant is
    /// exactly as capable of holding the transmit slice as a named one.</para>
    /// </remarks>
    public static class OccupancyPhrase
    {
        /// <summary>
        /// The row's availability-and-occupancy clause, leading comma included
        /// — always words, never empty. Zero clients is a report, not a
        /// silence: ", online with 0 connected clients". Callers speak this
        /// ONLY for live rows whose client list a source actually delivered —
        /// a sighting event or the WAN bank; a live row still waiting on its
        /// source speaks <see cref="UnknownSuffix"/> instead, and a row that
        /// is not live speaks nothing. Zero from a source that never spoke
        /// would be a confident false claim in the sentence read before
        /// keying somebody else's transmitter — the 2026-08-30 field trace
        /// (pushes dropped with no intake, Don on the radio, row silent) is
        /// the state that rule exists for. One raw entry per connected GUI
        /// client, "" for a client that has not asserted a station name yet.
        /// </summary>
        public static string RowSuffix(IReadOnlyList<string> stations)
        {
            int count = stations?.Count ?? 0;
            var named = (stations ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();

            string phrase;
            if (count == 0)
            {
                phrase = Lexicon.Get("connect.row.occupied_zero");
            }
            else if (count == 1)
            {
                phrase = named.Count == 1
                    ? Lexicon.Get("connect.row.occupied_one_named", ("station", named[0]))
                    : Lexicon.Get("connect.row.occupied_one");
            }
            else
            {
                phrase = named.Count > 0
                    ? Lexicon.Get("connect.row.occupied_many_named",
                        ("count", count), ("stations", JoinNames(named)))
                    : Lexicon.Get("connect.row.occupied_many",
                        ("count", count));
            }

            return Lexicon.Get("connect.row.occupancy_suffix", ("occupants", phrase));
        }

        /// <summary>
        /// The honest third state, leading comma included: ", online, client
        /// count unknown". Spoken by a live row whose client list no source
        /// has delivered yet — reachable, so "online" still leads, but the
        /// count is not invented. Rare by design (the WAN bank answers for
        /// any radio it made live), and rare is the point: this is the
        /// fallback that keeps a broken or not-yet-spoken pipe from ever
        /// rendering as an empty radio.
        /// </summary>
        public static string UnknownSuffix() =>
            Lexicon.Get("connect.row.occupancy_suffix",
                ("occupants", Lexicon.Get("connect.row.occupancy_unknown")));

        /// <summary>
        /// "wa2iwc", "wa2iwc and k5ner", or a comma list beyond two — listed,
        /// never truncated. The pair joiner lives in the lexicon with the rest
        /// of the row's words.
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
