namespace Radios
{
    /// <summary>
    /// The three availability facts a radio-picker row carries, as the
    /// picker's sighting decision reads and writes them.
    /// </summary>
    /// <param name="Lan">Local discovery can see the radio.</param>
    /// <param name="Wan">A SmartLink list carried the radio, so the SmartLink
    /// leg is one a connect may try.</param>
    /// <param name="WanUnconfirmed">The list that carried it is no longer
    /// the session's current knowledge: the SmartLink half is LAST SEEN, not
    /// online (#619, Noel's ruling of 2026-09-30).</param>
    public readonly record struct PickerRowPaths(bool Lan, bool Wan, bool WanUnconfirmed)
    {
        /// <summary>Reachable by a path something currently confirms.</summary>
        public bool IsLive => Lan || (Wan && !WanUnconfirmed);

        /// <summary>A connect has at least one leg to try — live, or last
        /// seen on SmartLink. Choosing a last-seen row starts a fresh
        /// connection attempt, because connecting is itself the check.</summary>
        public bool HasPathToTry => Lan || Wan;
    }

    /// <summary>What the picker does with one sighting.</summary>
    public enum PickerSightingTreatment
    {
        /// <summary>News about now: the row is rewritten as reachable, the
        /// connecting window may close, an arrival may be announced, and the
        /// sighting is recorded to the roster.</summary>
        Live,

        /// <summary>From a list that is no longer current: the row shows the
        /// radio as last seen on SmartLink and keeps it selectable, and
        /// nothing live happens — no "radios have arrived", no arrival, no
        /// roster record.</summary>
        LastSeen,

        /// <summary>From a list that is no longer current, about a row a
        /// current list vouches for right now: the older sighting does not
        /// speak over the newer one, and the row is left alone.</summary>
        Ignored,
    }

    /// <summary>The picker's decision about one sighting.</summary>
    public readonly record struct PickerSightingOutcome(
        PickerSightingTreatment Treatment, PickerRowPaths Paths, bool Arrived)
    {
        /// <summary>Whether the sighting may be treated as live news.</summary>
        public bool TakenAsLive => Treatment == PickerSightingTreatment.Live;
    }

    /// <summary>
    /// The radio picker's two decisions about a sighting, stated away from the
    /// window so the suite can hold them (#619). Sol's review of Track L8 found
    /// both dialog decisions untested because both lived inside a WPF handler
    /// no test constructs; this is the seam.
    /// </summary>
    /// <remarks>
    /// <para><b>Decision one, Noel's ruling of 2026-09-30.</b> After a
    /// SmartLink drop, a radio from the last SmartLink list is shown as LAST
    /// SEEN, not online, and stays selectable: choosing it starts a fresh
    /// connection attempt, because connecting is itself the check. So a
    /// sighting whose list is no longer current neither sets the picker's
    /// "a live radio has been seen", nor closes the connecting window, nor
    /// announces an arrival, nor reads as online. Before this, the replay the
    /// picker asks for on opening raised every held row with no list at all,
    /// which answered "current", and the rows of a dropped session opened as
    /// online.</para>
    /// <para><b>Decision two, the same ruling.</b> The arrival announcement
    /// is made on the UI thread, a dispatch after the row decision, and the
    /// list can stop being current in between. So the sighting is asked
    /// again at the last instant, on the dispatcher, immediately before the
    /// announcement: <see cref="MayAnnounceArrival"/>. The owner's gate is
    /// NOT held across the UI to close the rest of the window — that risks
    /// freezing the connection behind a busy window — and the remainder, the
    /// distance from that check to the next line, is accepted by ruling.</para>
    /// </remarks>
    public static class PickerSighting
    {
        /// <summary>
        /// Decide what a sighting does to its row. Called by the picker under
        /// its list lock, at the moment it decides.
        /// </summary>
        /// <param name="sighting">The sighting's <see cref="FlexBase.RigData"/>,
        /// as the row carries it. Anything else is a sighting with no
        /// SmartLink list behind it, and is live.</param>
        /// <param name="sightingLan">The sighting's local-network flag.</param>
        /// <param name="sightingWan">The sighting's SmartLink flag.</param>
        /// <param name="row">The row's facts before this sighting, or null
        /// when the picker has no row for the radio yet.</param>
        /// <param name="rowSighting">The sighting the row last took, or null.</param>
        public static PickerSightingOutcome Decide(
            object sighting, bool sightingLan, bool sightingWan,
            PickerRowPaths? row, object rowSighting)
        {
            var rd = sighting as FlexBase.RigData;
            bool wasLive = row?.IsLive ?? false;

            if (rd == null || rd.StillCurrent())
            {
                // A sighting whose list is current confirms the SmartLink half
                // it reports. One with no list behind it — a local-network
                // sighting, whose SmartLink flag is read from the WAN bank —
                // confirms nothing about that half and leaves it as it was.
                bool confirmsWan = rd != null && rd.FromWanList != null;
                var paths = new PickerRowPaths(
                    sightingLan,
                    sightingWan,
                    confirmsWan ? false : (row?.WanUnconfirmed ?? false));
                return new PickerSightingOutcome(
                    PickerSightingTreatment.Live, paths, !wasLive && paths.IsLive);
            }

            // The list behind this sighting is no longer current. A row that a
            // CURRENT list vouches for right now is not spoken over by it: a
            // replay reads its list, releases the lock and raises later, and a
            // current push can land in between.
            if (row != null
                && rowSighting is FlexBase.RigData held
                && held.FromWanList != null
                && held.StillCurrent())
            {
                return new PickerSightingOutcome(PickerSightingTreatment.Ignored, row.Value, false);
            }

            // Last seen. Never local evidence, so the local flag is the row's
            // own; the SmartLink leg stays one a connect may try.
            var lastSeen = new PickerRowPaths(
                row?.Lan ?? false,
                sightingWan || (row?.Wan ?? false),
                sightingWan || (row?.WanUnconfirmed ?? false));
            return new PickerSightingOutcome(PickerSightingTreatment.LastSeen, lastSeen, false);
        }

        /// <summary>
        /// The last-instant check before an arrival is spoken (Noel's ruling of
        /// 2026-09-30): asked on the dispatcher, immediately before the
        /// announcement. False when the list behind the sighting has stopped
        /// being current since the row decision took it as live.
        /// </summary>
        public static bool MayAnnounceArrival(object sighting) =>
            sighting is not FlexBase.RigData rd || rd.StillCurrent();

        /// <summary>
        /// Re-ask a row the picker already holds, when a session signals that
        /// its lists may have stopped being current
        /// (<see cref="SmartLink.SmartLinkSessionCoordinator.SessionListCurrencyMayHaveChanged"/>).
        /// A row whose SmartLink half a list vouched for, and whose list is
        /// no longer current, becomes LAST SEEN — not live, not an auto-connect
        /// candidate, no occupancy read as online — and keeps its SmartLink
        /// leg so choosing it still starts a connect (Noel's ruling of
        /// 2026-09-30). Anything else is left exactly as it was.
        /// </summary>
        /// <remarks>
        /// <para><b>Why (#619, Sol's review of L9).</b> <see cref="Decide"/>
        /// runs only when a sighting arrives, and a drop raises none. A
        /// picker opened after a drop was right, because its opening replay
        /// asks; one already open kept the rows it had taken as live reading
        /// online until another list happened to come, and the auto-connect
        /// timer could choose one of them from old evidence.</para>
        /// <para>The same question <see cref="Decide"/> asks, of the same
        /// sighting: <see cref="FlexBase.RigData.StillCurrent"/>. There is no
        /// second provenance here. It only ever takes the SmartLink half
        /// away; a row comes back live only when a sighting from a current
        /// list arrives, through <see cref="Decide"/>, exactly as it does
        /// after a picker opens on a dropped session.</para>
        /// <para>The local half is never touched: a list going stale says
        /// nothing about the local network.</para>
        /// </remarks>
        /// <param name="row">The row's facts now.</param>
        /// <param name="wanSighting">The sighting whose SmartLink list the
        /// row's SmartLink half was last taken from —
        /// <see cref="WanHalfSighting"/> — or null when no list ever spoke
        /// for it in this picker.</param>
        public static PickerSightingOutcome Reassess(PickerRowPaths row, object wanSighting)
        {
            if (row.Wan && !row.WanUnconfirmed
                && wanSighting is FlexBase.RigData rd
                && !rd.StillCurrent())
            {
                return new PickerSightingOutcome(
                    PickerSightingTreatment.LastSeen, row with { WanUnconfirmed = true }, false);
            }
            return new PickerSightingOutcome(PickerSightingTreatment.Ignored, row, false);
        }

        /// <summary>
        /// Apply the connection layer's answer to a row: which paths reach
        /// the radio, and, for SmartLink, which list says so
        /// (<see cref="FlexBase.RadioAvailability(string, out FlexBase.RigData)"/>).
        /// Called by the picker's availability reconcile, the first step of
        /// every repaint, and by its radio-removed handler — every place the
        /// picker writes a row's paths from that answer rather than from a
        /// sighting.
        /// </summary>
        /// <remarks>
        /// <para><b>Why (#619, Sol's review of L11).</b> That answer's
        /// SmartLink half comes from FlexBase's WAN bank, which outlives the
        /// rig that filled it and keeps its entries across a drop. The
        /// reconcile used to write it as a bare flag, so a roster row fed
        /// only by the bank — a push no rig consumed, then a picker on a new
        /// rig whose replay raises nothing for the radio — became live with
        /// no sighting for <see cref="Reassess"/> to question. After a drop it
        /// read online, kept its occupancy clause, and the auto-connect timer
        /// could choose it.</para>
        /// <para><b>The rule, the same question every other path asks.</b>
        /// The SmartLink half is vouched for by a list this picker took if
        /// that list is still current, and otherwise by the bank's list. The
        /// half is LAST SEEN exactly when that list is not current
        /// (<see cref="FlexBase.RigData.StillCurrent"/>), and the vouching
        /// sighting becomes the row's <see cref="WanHalfSighting"/>, so the
        /// next <see cref="Reassess"/> questions the same list. A bank whose
        /// list is current makes the half online, as a current list does
        /// anywhere else (Noel's ruling of 2026-09-30: last-list radios show
        /// as last seen, never online, still selectable). A SmartLink answer
        /// with nothing to vouch for it is last seen: nothing this picker
        /// holds says it is current, the same defensive answer as a held row
        /// with no list recorded.</para>
        /// <para>The local half is written as answered. With no SmartLink
        /// answer the half's confirmation is left as it was; it means nothing
        /// while there is no SmartLink leg, and the next sighting or reconcile
        /// that brings one decides it afresh.</para>
        /// </remarks>
        /// <param name="row">The row's facts now.</param>
        /// <param name="rowWanSighting">The row's <see cref="WanHalfSighting"/>.</param>
        /// <param name="lan">The answer's local-network flag.</param>
        /// <param name="wan">The answer's SmartLink flag: a handle a connect
        /// may try.</param>
        /// <param name="bankSighting">The answer's SmartLink sighting — the
        /// list behind the bank's handle — or null.</param>
        /// <returns>The row's new paths, and the sighting its SmartLink half
        /// now speaks for.</returns>
        public static (PickerRowPaths Paths, object WanSighting) Reconcile(
            PickerRowPaths row, object rowWanSighting, bool lan, bool wan, object bankSighting)
        {
            if (!wan)
                return (new PickerRowPaths(lan, false, row.WanUnconfirmed), rowWanSighting);

            object vouching =
                rowWanSighting is FlexBase.RigData held
                && held.FromWanList != null
                && held.StillCurrent()
                    ? rowWanSighting
                    : bankSighting ?? rowWanSighting;

            bool lastSeen = vouching is not FlexBase.RigData rd || !rd.StillCurrent();
            return (new PickerRowPaths(lan, true, lastSeen), vouching);
        }

        /// <summary>
        /// Which sighting a row's SmartLink half speaks for after the row
        /// takes <paramref name="sighting"/>: the new sighting when it came
        /// from a SmartLink list (or is a held SmartLink row no list is
        /// recorded for), otherwise the one the row already held. A
        /// local-network sighting says nothing about the SmartLink half, so
        /// it does not replace the list that did — which is what lets a
        /// dual-homed row, whose rig data a LAN broadcast refreshes every
        /// second, still be re-asked when SmartLink drops.
        /// </summary>
        public static object WanHalfSighting(object sighting, object held) =>
            sighting is FlexBase.RigData rd && (rd.FromWanList != null || rd.HeldWithoutAList)
                ? sighting
                : held;

        /// <summary>
        /// Whether the auto-connect timer may choose this row for the saved
        /// radio: only a row something confirms this moment. A last-seen row
        /// stays selectable by the operator, because connecting is itself the
        /// check, but an automatic connect started from old evidence is not
        /// the operator's check — it is the picker acting on a list it knows
        /// may be history (#619, Sol's review of L9).
        /// </summary>
        public static bool AutoConnectMayChoose(PickerRowPaths row) => row.IsLive;

        /// <summary>
        /// Where a last-seen row is, in words: the row's place clause, at the
        /// operator's verbosity. DRAFT sentences, unruled (#629, #617). Terse
        /// is deliberately short — Noel asked that terse be short.
        /// </summary>
        /// <param name="brokerAccount">The account a SmartLink connect would
        /// route through when it is not the one in play; empty otherwise, as
        /// for a live SmartLink row.</param>
        /// <param name="level">The operator's verbosity.</param>
        public static string LastSeenWhere(string brokerAccount, VerbosityLevel level) =>
            string.IsNullOrWhiteSpace(brokerAccount)
                ? Lexicon.Get("connect.row.last_seen_unconfirmed", level)
                : Lexicon.Get("connect.row.last_seen_unconfirmed_via", level, ("account", brokerAccount));
    }
}
