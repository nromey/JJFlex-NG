#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// One MultiFlex client as the roster holds it, with how we know about it.
    /// </summary>
    /// <param name="Program">The client program, "Unknown" when none was reported.</param>
    /// <param name="Station">The station name, "" until one is asserted.</param>
    /// <param name="Handle">The radio's client handle.</param>
    /// <param name="IsThisClient">Our own client.</param>
    /// <param name="OwnedSlices">Slice letters this client owns, comma-joined; "" for none.</param>
    /// <param name="ConfirmedByRadio">The radio's own status reported this
    /// client connected on this connection, and has not reported it gone
    /// since. False for a row only a SmartLink list or a discovery broadcast
    /// has mentioned.</param>
    /// <param name="MayHaveLeft">Reported earlier; a list or broadcast has
    /// since stopped mentioning it, and the radio has not said it left.</param>
    public readonly record struct ClientRow(
        string Program,
        string Station,
        uint Handle,
        bool IsThisClient,
        string OwnedSlices,
        bool ConfirmedByRadio,
        bool MayHaveLeft)
    {
        /// <summary>The name a sentence about this client uses: station, else program.</summary>
        public string NameForSentence => !string.IsNullOrEmpty(Station) ? Station : Program;

        /// <summary>A row the operator reads with a caveat: not ours, and
        /// either not the radio's own word or marked as possibly gone.</summary>
        public bool Unconfirmed => !IsThisClient && (!ConfirmedByRadio || MayHaveLeft);
    }

    /// <summary>
    /// The MultiFlex view's sentences, assembled in one place so the dialog,
    /// the tests and any future consumer agree on the words (the
    /// <see cref="OccupancyPhrase"/> pattern).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a row says how we know (#634, ruled 2026-09-26).</b> The
    /// vendor merges SmartLink's list and discovery broadcasts into the same
    /// client list the radio's own status writes, so until Track L6 every row
    /// read as the radio's word. A client seen only in a list is shown,
    /// labelled as reported, and cannot be disconnected from that row until
    /// the radio identifies it; a client the radio confirmed that a list has
    /// since omitted is KEPT and shown as possibly gone, because a list can
    /// omit a live client.</para>
    ///
    /// <para><b>EVERY SENTENCE HERE IS A DRAFT.</b> They are Noel's words from
    /// the 2026-09-26 question file, carried in unpolished so the surface can
    /// be built, and none was ruled on. FOR NOEL'S PROSE REVIEW:
    /// <c>connect.client.reported_by_smartlink</c>,
    /// <c>connect.client.reported_by_smartlink_no_station</c>,
    /// <c>connect.client.may_have_left</c>,
    /// <c>connect.client.info_unavailable</c>,
    /// <c>connect.multiflex.some_unconfirmed</c>,
    /// <c>connect.multiflex.disconnect_unavailable</c>.</para>
    ///
    /// <para><b>Readable, not only spoken (#643).</b> These are the texts of
    /// controls — the list's rows, the summary line, the reason line under
    /// the list — so a flushed speech queue loses none of them.</para>
    /// </remarks>
    public static class ClientRowPhrase
    {
        /// <summary>The list row for one client.</summary>
        public static string Line(ClientRow row)
        {
            string slices = !string.IsNullOrEmpty(row.OwnedSlices)
                ? Lexicon.Get("connect.multiflex.slices_suffix", ("ownedSlices", row.OwnedSlices))
                : "";

            if (row.MayHaveLeft && !row.IsThisClient)
            {
                // Draft 3: "{station} was reported earlier. The radio has not
                // confirmed that this client left."
                return Lexicon.Get("connect.client.may_have_left", ("station", row.NameForSentence)) + slices;
            }
            if (!row.ConfirmedByRadio && !row.IsThisClient)
            {
                // Drafts 1 and 2: reported by SmartLink, not yet confirmed.
                return (string.IsNullOrEmpty(row.Station)
                        ? Lexicon.Get("connect.client.reported_by_smartlink_no_station")
                        : Lexicon.Get("connect.client.reported_by_smartlink",
                            ("program", row.Program), ("station", row.Station)))
                    + slices;
            }

            string tag = row.IsThisClient ? Lexicon.Get("connect.multiflex.this_client_tag") : "";
            string station = !string.IsNullOrEmpty(row.Station)
                ? Lexicon.Get("connect.multiflex.station_suffix", ("station", row.Station))
                : "";
            return Lexicon.Get("connect.multiflex.client_line",
                ("program", row.Program), ("station", station), ("slices", slices), ("tag", tag));
        }

        /// <summary>
        /// The line above the list. Draft 4 when the rig cannot say who is on
        /// the radio at all — an empty list would read as an empty radio;
        /// draft 5 when some rows are a list's word rather than the radio's;
        /// the plain count otherwise.
        /// </summary>
        public static string Summary(IReadOnlyList<ClientRow> rows, bool informationUnavailable)
        {
            if (informationUnavailable) return Lexicon.Get("connect.client.info_unavailable");
            if (rows.Any(r => r.Unconfirmed)) return Lexicon.Get("connect.multiflex.some_unconfirmed");
            return rows.Count == 1
                ? Lexicon.Get("connect.multiflex.one_client")
                : Lexicon.Get("connect.multiflex.many_clients", ("count", rows.Count));
        }

        /// <summary>
        /// Whether the selected row may be disconnected from here: not ours,
        /// and the radio's own word — a client only a list has reported
        /// cannot be disconnected from that row until the radio identifies
        /// it (an accepted cost of #634).
        /// </summary>
        public static bool MayDisconnect(ClientRow? selected) =>
            selected is { } row && !row.IsThisClient && row.ConfirmedByRadio && !row.MayHaveLeft;

        /// <summary>
        /// The readable reason Disconnect is unavailable for the selected
        /// row (draft 6), or null when it is available or nothing that needs
        /// a reason is selected. Our own row needs none: the button has
        /// always been disabled for it.
        /// </summary>
        public static string? DisconnectReason(ClientRow? selected)
        {
            if (selected is not { } row || row.IsThisClient) return null;
            return MayDisconnect(row) ? null : Lexicon.Get("connect.multiflex.disconnect_unavailable");
        }
    }
}
