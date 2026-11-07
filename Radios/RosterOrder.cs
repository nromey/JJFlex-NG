using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// The three facts the radio list sorts on. Implemented by the picker's row
    /// type; declared here so the rule itself can be stated and tested away from
    /// a WPF window.
    /// </summary>
    public interface IRosterOrderKey
    {
        /// <summary>The operator marked this radio a favourite.</summary>
        bool IsFavorite { get; }

        /// <summary>Reachable by at least one path RIGHT NOW.</summary>
        bool IsLive { get; }

        /// <summary>
        /// Local discovery can see it RIGHT NOW. A radio that is on the local
        /// network AND reachable over SmartLink is local.
        /// </summary>
        /// <remarks>
        /// The tense is the whole of task #254, which happened to the SmartLink
        /// flag this clause used to read. While that meant "some SmartLink list
        /// mentioned it at some point this session" the third clause below
        /// sorted by which account had been refreshed most recently — a fact the
        /// operator never chose and cannot see, encoded in the one thing a
        /// keyboard user navigates by. The same holds for this flag: if it ever
        /// comes to mean "was on the network once", a radio that has gone away
        /// climbs over one that is really here.
        /// </remarks>
        bool LanAvailable { get; }
    }

    /// <summary>
    /// How the radio list is ordered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Favourites first, because that is what a favourites list means. Then live
    /// radios above roster rows, because a row you can dial beats a row that is
    /// only history. Then local radios above remote-only ones: the top row is
    /// where focus lands and what Enter acts on, and the radio on the operator's
    /// own network belongs there, not someone else's radio reachable only over
    /// the internet. A radio reachable both ways counts as local. Ruled by Noel
    /// 2026-10-10, "Local radios should be on top" (#711). Stable within each
    /// group.
    /// </para>
    /// <para>
    /// <b>Until 2026-11-07 the third clause put remote-capable radios first.</b>
    /// It was written for a Remote button that opened a list of remote radios,
    /// where remote-first was what the operator had asked for. That button and
    /// its list are gone, and the clause had gone on applying to the one list
    /// everybody sees.
    /// </para>
    /// <para>
    /// <b>The stability clause is not decoration.</b> A LAN radio re-announces
    /// itself about once a second. Without the original-index tiebreak, every
    /// announcement is licence to rearrange rows that compare equal, and for an
    /// operator navigating by arrow keys position IS memory: "the row below
    /// mine" has to mean the same radio from one press to the next. Noel arrowed
    /// to Don's 6300 on 2026-08-05, pressed Enter, and connected to his own
    /// 8600.
    /// </para>
    /// <para>
    /// <b>Fix the flag, not the clause, when an ordering complaint arrives.</b>
    /// The SmartLink clause was blamed for #254 and it was not the cause: the
    /// flag feeding it had come to mean "was reachable once" instead of "is
    /// reachable now", so the clause faithfully expressed a fact that had
    /// stopped being true. Fixing the flag fixed the order, and the same is true
    /// of the local flag this clause reads now. Deleting the clause would leave
    /// a stale-flag defect intact everywhere else it shows — the row text
    /// included.
    /// </para>
    /// </remarks>
    public static class RosterOrder
    {
        /// <summary>
        /// The rows in display order. Input order is the tiebreak, so equal rows
        /// never move relative to one another.
        /// </summary>
        public static List<T> Apply<T>(IEnumerable<T> rows) where T : IRosterOrderKey
        {
            if (rows == null) return new List<T>();
            return rows
                .Select((r, i) => (row: r, index: i))
                .OrderByDescending(x => x.row.IsFavorite)
                .ThenByDescending(x => x.row.IsLive)
                .ThenByDescending(x => x.row.LanAvailable)
                .ThenBy(x => x.index)
                .Select(x => x.row)
                .ToList();
        }
    }
}
