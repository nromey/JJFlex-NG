#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// Where the knowledge of a MultiFlex client came from (#634, Sol's
    /// review of L6). The vendor merges three sources into one client list,
    /// and a row that names the wrong one tells the operator something false:
    /// until Track L7 every row the radio had not confirmed said SmartLink
    /// had reported it, including a client only a LAN discovery broadcast had
    /// ever mentioned, to an operator who had never touched SmartLink.
    /// </summary>
    public enum ClientRowSource
    {
        /// <summary>The radio's own status reported this client connected
        /// on this connection and has not reported it gone since.</summary>
        Radio,

        /// <summary>Not confirmed by the radio; a SmartLink list — from the
        /// account this connection was brokered through — reported it.</summary>
        SmartLinkList,

        /// <summary>Not confirmed by the radio; a discovery broadcast on the
        /// local network reported it.</summary>
        LocalDiscovery,

        /// <summary>
        /// Not confirmed by the radio, and no source was observed reporting
        /// it on this connection: the record was already on the vendor
        /// object when this attempt attached to it, or it arrived with a
        /// client_id and without the radio's own report (#634, Sol's review
        /// of L7). The row names no source. Until Track L8 such a record was
        /// named by the radio object's channel - SmartLink for a WAN object -
        /// which asserted a report nobody had seen: a client the radio's own
        /// TCP status added after the session's one list, deliberately left
        /// unrecorded, survives on the reused WAN object into the next
        /// attempt and was then said to have been reported by SmartLink.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// One MultiFlex client as the roster holds it, with how we know about it.
    /// </summary>
    /// <param name="Program">The client program, "Unknown" when none was reported.</param>
    /// <param name="Station">The station name, "" until one is asserted.</param>
    /// <param name="Handle">The radio's client handle.</param>
    /// <param name="IsThisClient">Our own client.</param>
    /// <param name="OwnedSlices">Slice letters this client owns, comma-joined; "" for none.</param>
    /// <param name="Source">Who told us about this client: the radio's own
    /// status, or — when the radio has not confirmed it — a SmartLink list or
    /// a local discovery broadcast. See <see cref="ClientRowSource"/>.</param>
    /// <param name="MayHaveLeft">Reported earlier; a list or broadcast has
    /// since stopped mentioning it, and the radio has not said it left.</param>
    public readonly record struct ClientRow(
        string Program,
        string Station,
        uint Handle,
        bool IsThisClient,
        string OwnedSlices,
        ClientRowSource Source,
        bool MayHaveLeft)
    {
        /// <summary>The radio's own status reported this client connected on
        /// this connection, and has not reported it gone since. False for a
        /// row only a SmartLink list or a discovery broadcast has mentioned.</summary>
        public bool ConfirmedByRadio => Source == ClientRowSource.Radio;

        /// <summary>The name a sentence about this client uses: station, else program.</summary>
        public string NameForSentence => !string.IsNullOrEmpty(Station) ? Station : Program;

        /// <summary>A row the operator reads with a caveat: not ours, and
        /// either not the radio's own word or marked as possibly gone. What a
        /// confirmation may not name as definitely connected
        /// (<see cref="ClientRowPhrase.Company"/>).</summary>
        /// <remarks>Named <c>Unconfirmed</c> until Track L11, which was false
        /// for half of what it covers: a row the radio confirmed that may
        /// have left since IS confirmed by the radio, and the summary line
        /// read the name and said so (#634, Sol's scoped review of L9 and
        /// L10). "Not confirmed by the radio" is <see cref="AwaitingRadio"/>.</remarks>
        public bool Uncertain => !IsThisClient && (!ConfirmedByRadio || MayHaveLeft);

        /// <summary>Not ours, and the radio has not confirmed it: only a
        /// SmartLink list, a broadcast, or no observed source has reported
        /// it. A row the radio confirmed that may have left since is NOT
        /// this — the radio did confirm it, and its caveat is sentence 3's.</summary>
        public bool AwaitingRadio => !IsThisClient && !ConfirmedByRadio;
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
    /// labelled with the source that reported it — SmartLink's list or a
    /// broadcast on the local network (Track L7) — and cannot be
    /// disconnected from that row until the radio identifies it; a client
    /// the radio confirmed that a list has
    /// since omitted is KEPT and shown as possibly gone, because a list can
    /// omit a live client.</para>
    ///
    /// <para><b>The wording is Noel's, APPROVED 2026-09-30</b> (Track L10),
    /// with his own capitalisation and punctuation kept and only the facts
    /// his drafts stated falsely corrected, on his authority: a client may
    /// have left, not been disconnected by us; client information is not
    /// available yet, not undetectable; Disconnect waits for the radio, not
    /// SmartLink. The not-yet-confirmed rows are terse and chatty ladders,
    /// and <see cref="Line(ClientRow, VerbosityLevel)"/> takes the operator's
    /// verbosity. Approved: <c>connect.client.reported_by_smartlink</c>,
    /// <c>connect.client.reported_by_smartlink_no_station</c>,
    /// <c>connect.client.reported_on_local_network</c>,
    /// <c>connect.client.not_yet_confirmed</c>,
    /// <c>connect.client.may_have_left</c>,
    /// <c>connect.client.info_unavailable</c>,
    /// <c>connect.multiflex.some_unconfirmed</c>,
    /// <c>connect.multiflex.disconnect_unavailable</c>. The two no-station
    /// companions, <c>connect.client.reported_on_local_network_no_station</c>
    /// and <c>connect.client.not_yet_confirmed_no_station</c>, are Noel's
    /// terse and chatty pairs APPROVED 2026-10-01 (Track L13), exactly as
    /// written. STILL A DRAFT:
    /// <c>connect.multiflex.disconnect_unavailable_may_have_left</c> (Track
    /// L11), the Disconnect reason for a client the radio identified that may
    /// have left since, written in sentence 3's terms on Noel's authority of
    /// 2026-09-30 to fit wording to the actual situation.</para>
    ///
    /// <para><b>Readable, not only spoken (#643).</b> These are the texts of
    /// controls — the list's rows, the summary line, the reason line under
    /// the list — so a flushed speech queue loses none of them.</para>
    /// </remarks>
    public static class ClientRowPhrase
    {
        /// <summary>The list row for one client, in its chatty form. The
        /// dialog calls <see cref="Line(ClientRow, VerbosityLevel)"/> with the
        /// operator's verbosity; this overload is the long form.</summary>
        public static string Line(ClientRow row) => Line(row, VerbosityLevel.Chatty);

        /// <summary>The list row for one client at a verbosity tier. The
        /// not-yet-confirmed rows carry Noel's terse and chatty pairs
        /// (approved 2026-09-30); every other row reads the same at both.</summary>
        public static string Line(ClientRow row, VerbosityLevel level)
        {
            string slices = !string.IsNullOrEmpty(row.OwnedSlices)
                ? Lexicon.Get("connect.multiflex.slices_suffix", ("ownedSlices", row.OwnedSlices))
                : "";

            if (row.MayHaveLeft && !row.IsThisClient)
            {
                // Sentence 3, approved 2026-09-30, one text for both tiers.
                // {station} is the station, else the program (NameForSentence).
                return Lexicon.Get("connect.client.may_have_left", ("station", row.NameForSentence)) + slices;
            }
            if (!row.ConfirmedByRadio && !row.IsThisClient)
            {
                // Not yet confirmed, and the sentence names the source that
                // actually reported it (#634, Sol's review of L6). Sentences
                // 1 and 2 for SmartLink, and the local-network and
                // unknown-source companions in their pattern, are Noel's
                // approved terse and chatty pairs of 2026-09-30. They name
                // the station only: the drafts' "{program}, station" lead-in
                // is gone from the approved wording, so the program is no
                // longer passed. The two no-station companions for the local
                // network and an unknown source are Noel's terse and chatty
                // pairs approved 2026-10-01 (Track L13).
                //
                // A client no source was observed reporting names none
                // (Sol's review of L7).
                if (row.Source == ClientRowSource.Unknown)
                {
                    return (string.IsNullOrEmpty(row.Station)
                            ? Lexicon.Get("connect.client.not_yet_confirmed_no_station", level)
                            : Lexicon.Get("connect.client.not_yet_confirmed", level,
                                ("station", row.Station)))
                        + slices;
                }
                bool lan = row.Source == ClientRowSource.LocalDiscovery;
                return (string.IsNullOrEmpty(row.Station)
                        ? Lexicon.Get(lan
                            ? "connect.client.reported_on_local_network_no_station"
                            : "connect.client.reported_by_smartlink_no_station", level)
                        : Lexicon.Get(lan
                            ? "connect.client.reported_on_local_network"
                            : "connect.client.reported_by_smartlink", level,
                            ("station", row.Station)))
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
        /// The line above the list. Sentence 4 when the rig cannot say who is on
        /// the radio at all — an empty list would read as an empty radio;
        /// sentence 5 when some rows are a list's word rather than the radio's;
        /// the plain count otherwise.
        /// </summary>
        /// <remarks>
        /// Sentence 5 says "Some entries have not been confirmed by the
        /// radio", so it counts only rows the radio has not confirmed
        /// (<see cref="ClientRow.AwaitingRadio"/>). A row the radio confirmed
        /// that a list has since stopped mentioning does not trigger it: the
        /// radio did confirm it, and its own row already says it may have
        /// disconnected (sentence 3). Until Track L11 it did trigger it, and
        /// the summary said something false about it (#634, Sol's scoped
        /// review of L9 and L10). When only such rows are uncertain the
        /// ordinary count line is read, on Noel's authority of 2026-09-30 to
        /// fit wording to the actual situation.
        /// </remarks>
        public static string Summary(IReadOnlyList<ClientRow> rows, bool informationUnavailable)
        {
            if (informationUnavailable) return Lexicon.Get("connect.client.info_unavailable");
            if (rows.Any(r => r.AwaitingRadio)) return Lexicon.Get("connect.multiflex.some_unconfirmed");
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
        /// row, or null when it is available or nothing that needs a reason
        /// is selected. Our own row needs none: the button has always been
        /// disabled for it.
        /// </summary>
        /// <remarks>
        /// <para>Two reasons, because there are two situations (#634, Sol's
        /// scoped review of L9 and L10). A row the radio never identified
        /// waits for the radio: sentence 6, approved 2026-09-30, "until the
        /// radio identifies it". A row the radio DID identify, that a list or
        /// a broadcast has since stopped mentioning, is not waiting for
        /// anything — its uncertainty is that it may have left, so sentence 6
        /// would state a reason that is false for it. That row gets
        /// <c>connect.multiflex.disconnect_unavailable_may_have_left</c>, in
        /// the terms of sentence 3, which the same row already reads, and
        /// named the way sentence 3 names it: station, else program. DRAFT
        /// (Track L11), one text for both tiers.</para>
        /// </remarks>
        public static string? DisconnectReason(ClientRow? selected)
        {
            if (selected is not { } row || row.IsThisClient) return null;
            if (MayDisconnect(row)) return null;
            return row.ConfirmedByRadio && row.MayHaveLeft
                ? Lexicon.Get("connect.multiflex.disconnect_unavailable_may_have_left", ("station", row.NameForSentence))
                : Lexicon.Get("connect.multiflex.disconnect_unavailable");
        }

        /// <summary>
        /// The other clients on the radio, split by what a confirmation may
        /// say about them (#634, Sol's review of L6). <c>Confirmed</c> are the
        /// ones the radio itself has reported connected on this connection
        /// and nothing has since omitted: only they may be named in a
        /// definite claim — "are connected", "will disconnect", "will lose the
        /// radio". <c>Reported</c> are the rest — a SmartLink list's or a
        /// broadcast's word, or a confirmed client something has since stopped
        /// listing — and they go into the "may be affected" caveat instead.
        /// Never both; our own client is in neither.
        /// </summary>
        /// <remarks>
        /// Until Track L7 the definite list was the vendor's merged client
        /// list, so a client only a stale list mentioned was named as
        /// connected on the decisions that restart or reconfigure a shared
        /// radio, and L6's caveat after it could not make that claim true.
        /// A confirmed client with no station is now named by its program,
        /// where the old list left it out of the claim altogether.
        /// </remarks>
        public static (IReadOnlyList<string> Confirmed, IReadOnlyList<string> Reported) Company(IEnumerable<ClientRow> rows)
        {
            var confirmed = new List<string>();
            var reported = new List<string>();
            foreach (var row in rows ?? Array.Empty<ClientRow>())
            {
                if (row.IsThisClient) continue;
                (row.Uncertain ? reported : confirmed).Add(CompanyName(row));
            }
            return (confirmed, reported);
        }

        /// <summary>A client as a confirmation names it: station, else
        /// program, else the unknown-client word. <c>"Unknown"</c> is the
        /// program <see cref="FlexBase.GetGuiClients"/> writes for a client
        /// that reported none, and is not a name.</summary>
        public static string CompanyName(ClientRow row) =>
            !string.IsNullOrEmpty(row.Station) ? row.Station
            : !string.IsNullOrEmpty(row.Program) && row.Program != "Unknown" ? row.Program
            : Lexicon.Get("connect.client.unknown_added");
    }

    /// <summary>
    /// What the readable line under the MultiFlex list says: the outcome of
    /// the operator's last disconnect request while it still describes the
    /// row in front of them, and otherwise the selected row's reason (#643).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a rule and not a write (Sol's review of L6).</b> L6 wrote
    /// "the disconnect request was sent" into the line and spoke it, then
    /// refreshed the list half a second later — and the refresh rewrote the
    /// line from the selected row, which for an ordinary client still on the
    /// radio clears it. After 500 ms the only record of the outcome was the
    /// speech, which #643 shows can be cancelled before word one, so the
    /// operator could not tell a request that went out from one that never
    /// did. The failed branch was speech only from the start.</para>
    ///
    /// <para><b>How long an outcome stays (ruled).</b> Until the operator
    /// selects a different row, or until the radio reports that client's
    /// departure. The roster drops a client only when the radio's own status
    /// reports it gone (or a new connection starts); a list or a broadcast
    /// omitting it keeps the row, marked as possibly gone. So the row
    /// leaving the list IS the radio's report, and when it leaves, the
    /// selection necessarily moves to another row or to none — which is the
    /// first condition. One test therefore covers both; a separate
    /// "departed" check was written and removed on Track L7 when its
    /// mutation turned nothing red. A row marked possibly gone keeps the
    /// outcome, because the radio has not spoken. No refresh, timed or
    /// event driven, can replace it otherwise.</para>
    ///
    /// <para>Here, in Radios, so the suite reads the rule without a window;
    /// the dialog asks it for the line on every refresh and every selection
    /// change the operator makes.</para>
    /// </remarks>
    public sealed class DisconnectOutcomeLine
    {
        private uint? _handle;
        private string? _text;

        /// <summary>Pin <paramref name="text"/>, the outcome of a request about
        /// <paramref name="handle"/>, to the line.</summary>
        public void Record(uint handle, string text)
        {
            _handle = handle;
            _text = text;
        }

        /// <summary>
        /// The line's text with <paramref name="selected"/> the row the list
        /// has selected now (null for none), or null for no line. Forgets the
        /// outcome once the selection is on any other row — the operator
        /// moved, or the client's row left the list because the radio
        /// reported it gone.
        /// </summary>
        public string? TextFor(ClientRow? selected)
        {
            if (_handle is uint h)
            {
                if (selected is { } s && s.Handle == h) return _text;
                _handle = null;
                _text = null;
            }
            return ClientRowPhrase.DisconnectReason(selected);
        }
    }
}
