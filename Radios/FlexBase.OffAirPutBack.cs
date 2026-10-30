#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;
using Radios.Listenback;

namespace Radios
{
    /// <summary>
    /// FlexBase, the off-air take's recovery folded into the next connect's
    /// automatic put-back (#835, fix round 1030 P2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ruling (Noel, 2026-10-29 23:49, decision (a)).</b> The off-air
    /// take's recovery becomes automatic too, "if the put back is reliable".
    /// Until this commit, a take the radio went away during left its own record
    /// (<see cref="RadioRecoveryStore"/>, written before its first change),
    /// and the next connect OFFERED it for the operator to approve, under that
    /// store's rule "Reconnection is a discovery operation, never implicit
    /// permission to replay commands". The ruling overrides that rule for this
    /// one case, the off-air take.
    /// </para>
    /// <para>
    /// <b>The same put-back, not a second one.</b> At connect, the take's
    /// station-wide settings (the transmit antenna, transmit power, the
    /// monitor, full duplex, the transverter drive) go into the check's
    /// put-back record, each with the value it had before the take and the
    /// value the take set, and the take's record is archived beside itself,
    /// never deleted. The put-back then does what it does for a check: the
    /// same reads, only a setting still at the take's value is written, the
    /// same confirmation, the same sentence with the alarm.
    /// </para>
    /// <para>
    /// <b>Left to the reconnect, as for a check:</b> the slices' mutes, the
    /// listening slice and its pan, the IQ stream and its channel. They belong
    /// to the connection that made them; the radio removes a client's slices,
    /// pans and streams when it goes, and the reconnect's station restore
    /// makes the operator's slices again with their own mutes. On a new
    /// connection the approve-first path could not prove those were ours
    /// anyway, and refused them.
    /// </para>
    /// <para>
    /// <b>Still approve-first:</b> a record that cannot be read (only the
    /// operator can acknowledge it), and the receive-only diagnostic take's,
    /// which the ruling did not name. A take whose restore failed while the
    /// radio stayed connected is still offered in that session, from "Review
    /// unrestored take settings".
    /// </para>
    /// <para>
    /// <b>Removable in one commit.</b> If the bench finds the put-back
    /// unreliable, reverting the commit that added this file puts the
    /// approve-first offer back exactly as it was.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>
        /// True when the next connect's put-back takes this off-air take's
        /// record, so the connect's approve-first offer must not show it.
        /// </summary>
        /// <remarks>
        /// Only a readable off-air record (it alone holds the transmit antenna)
        /// in which every station-wide setting carries the value the take set:
        /// the put-back writes nothing it cannot first prove still holds that
        /// value, so a record written before takes recorded it stays with the
        /// approve-first offer rather than being archived with nothing done.
        /// </remarks>
        public static bool OffAirRecoveryGoesToPutBack(RadioRecoverySnapshot? snapshot) =>
            snapshot != null && snapshot.UnreadableHash == null && snapshot.Settings != null &&
            snapshot.Settings.Any(s => s?.Name == "tx-antenna") &&
            snapshot.Settings.Where(s => s != null && StationWide.Contains(s.Name))
                .All(s => s.Value != null && s.RequestedValue != null && (s.Name != "xvtr-drive" || DriveIndex(s) != null));

        /// <summary>The take's settings that belong to the station rather than to its connection.</summary>
        private static readonly string[] StationWide = { "tx-antenna", "rf-power", "monitor", "full-duplex", "xvtr-drive" };

        /// <summary>The transverter band a drive setting belongs to, from its "xvtr N" status prefix.</summary>
        private static string? DriveIndex(SavedRadioSetting s) =>
            (s.StatusPrefix ?? "").Split(' ') is { Length: 2 } p && p[0] == "xvtr" && int.TryParse(p[1], out _) ? p[1] : null;

        /// <summary>
        /// The station-wide settings an off-air take's record holds, as the
        /// put-back's settings: the original value and the value the take set.
        /// A setting with no recorded take value is left out.
        /// </summary>
        internal static IReadOnlyList<CheckPutBack> OffAirPutBacks(RadioRecoverySnapshot snapshot)
        {
            var items = new List<CheckPutBack>();
            foreach (var s in snapshot?.Settings ?? new List<SavedRadioSetting>())
            {
                if (s?.Value == null || s.RequestedValue == null) continue;
                CheckPutBack? item = s.Name switch
                {
                    "tx-antenna" => new CheckPutBack(CheckPutBack.TxAntenna, "", s.Value, s.RequestedValue),
                    "rf-power" => new CheckPutBack(CheckPutBack.RfPower, "", s.Value, s.RequestedValue),
                    "monitor" => new CheckPutBack(CheckPutBack.Monitor, "", s.Value, s.RequestedValue),
                    "full-duplex" => new CheckPutBack(CheckPutBack.FullDuplex, "", s.Value, s.RequestedValue),
                    "xvtr-drive" when DriveIndex(s) is { } band =>
                        new CheckPutBack(CheckPutBack.XvtrDrive, band, s.Value, s.RequestedValue),
                    _ => null,
                };
                if (item != null && item.Changed) items.Add(item);
            }
            return items;
        }

        /// <summary>
        /// At connect, before the put-back reads its record: move an off-air
        /// take's record for this radio into it, and archive the take's record
        /// once the put-back's record is confirmed to hold every setting. Never
        /// throws; anything that goes wrong leaves the take's record where it
        /// was, for the next connect.
        /// </summary>
        private void TakeOffAirRecoveryIntoPutBack(string? serial, CheckRestoreReminderStore store)
        {
            if (string.IsNullOrEmpty(serial)) return;
            try
            {
                var takes = IqRecovery;
                RadioRecoverySnapshot? snapshot = takes.Find(serial);
                if (snapshot == null) return;
                if (!OffAirRecoveryGoesToPutBack(snapshot))
                {
                    Tracing.TraceLine("Off-air put-back: the take record for " + serial
                        + (snapshot.UnreadableHash != null ? " cannot be read"
                            : " is not an off-air take's, or does not say what the take set")
                        + ", so it stays with the approve-first offer", TraceLevel.Info);
                    return;
                }
                var items = OffAirPutBacks(snapshot);
                store.AddPutBacks(serial, items);
                var held = store.PutBacksFor(serial);
                var missing = items.Where(i => !held.Any(h => h.SameSettingAs(i))).ToList();
                if (missing.Count > 0)
                {
                    Tracing.TraceLine("Off-air put-back: the put-back record did not take " + string.Join(", ", missing)
                        + "; the take record for " + serial + " is kept for the next connect", TraceLevel.Warning);
                    return;
                }
                foreach (var item in items)
                    Tracing.TraceLine("Off-air put-back: take " + snapshot.TakeDirectory + " left " + item
                        + "; recorded for the put-back", TraceLevel.Info);
                bool archived = takes.ArchiveTakenIntoPutBack(snapshot);
                Tracing.TraceLine("Off-air put-back: the take record for " + serial + " "
                    + (archived ? "is archived, its settings now the put-back's (" + items.Count + ")"
                                : "changed while it was read, so it is kept; the put-back has its settings"),
                    archived ? TraceLevel.Info : TraceLevel.Warning);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Off-air put-back: the take record for " + serial + " could not be taken; kept: " + ex.Message,
                    TraceLevel.Warning);
            }
        }
    }
}
