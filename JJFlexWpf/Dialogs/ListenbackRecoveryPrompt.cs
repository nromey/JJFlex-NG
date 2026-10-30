using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Radios;
using Radios.Listenback;

namespace JJFlexWpf.Dialogs;

internal static class ListenbackRecoveryPrompt
{
    private static string SettingText(string name)
    {
        string key = "audio.iq.setting." + name;
        return Lexicon.Get(key);
    }
    internal static bool Confirm(RadioRecoverySnapshot snapshot, FlexBase rig)
    {
        bool approved = false;
        if (snapshot.UnreadableHash != null)
        {
            AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), Lexicon.Get("audio.iq.unreadable_offer"), null,
                new AdvisoryDialog.AdvisoryAction(Lexicon.Get("audio.iq.unreadable_acknowledge"), () => approved = true));
            return approved;
        }
        string changes = string.Join(Environment.NewLine, snapshot.Settings.Select(s =>
            s.Name.StartsWith("created-", StringComparison.Ordinal) ?
                Lexicon.Get(rig.ListenbackResourcePresent(snapshot, s) == false ? "audio.iq.review_absent" :
                    rig.ListenbackResourcePresent(snapshot, s) == true ? "audio.iq.review_present" : "audio.iq.review_unknown",
                    ("setting", SettingText(s.Name)), ("id", s.ResourceKind == "slice" ? s.ResourceId.ToString() : "0x" + s.ResourceId.ToString("X"))) :
                Lexicon.Get("audio.iq.saved_setting", ("setting", SettingText(s.Name)), ("value", s.Value))));
        string body = Lexicon.Get("audio.iq.recovery_offer", ("serial", snapshot.Serial), ("changes", changes));
        if (snapshot.ResourceCreationPending) body += Environment.NewLine + Lexicon.Get("audio.iq.creation_uncertain");
        AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), body, null,
            new AdvisoryDialog.AdvisoryAction(Lexicon.Get("audio.iq.restore_now"), () => approved = true));
        return approved;
    }
    internal static async Task<(string Key, bool Failure)?> ReviewAsync(FlexBase rig)
    {
        var saved = rig.PendingListenbackRecovery;
        if (saved == null) return ("audio.iq.no_recovery", false);
        if (!Confirm(saved, rig)) return null;
        if (saved.UnreadableHash != null)
        {
            bool archived = rig.AcknowledgeUnreadableListenbackRecovery(saved, true);
            return (archived ? "audio.iq.unreadable_archived" : "audio.iq.recovery_pending", !archived);
        }
        bool restored;
        try { restored = await rig.RestoreListenbackSnapshotAsync(saved, true); }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Listenback restore: " + ex, System.Diagnostics.TraceLevel.Error);
            restored = false;
        }
        if (restored) return ("audio.iq.restored", false);
        string key = ListenbackRecoveryAcknowledgement.Offer(rig, saved);
        return (key, key != "audio.iq.ack.cleared");
    }
    internal static async Task OfferOnConnectAsync(FlexBase rig)
    {
        try
        {
            var pending = rig.PendingListenbackRecovery;
            if (pending == null) return;
            // An off-air take's record is the next connect's put-back now, with
            // no question asked (#835, fix round 1030 P2, ruled 2026-10-29 23:49).
            // The put-back reads it at the same connect; whichever runs first,
            // this offer never shows it.
            if (FlexBase.OffAirRecoveryGoesToPutBack(pending))
            {
                JJTrace.Tracing.TraceLine("Listenback recovery: the off-air take's record goes to the connect's put-back; not offered",
                    System.Diagnostics.TraceLevel.Info);
                return;
            }
            var result = await ReviewAsync(rig);
            if (result is { } notice) AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), Lexicon.Get(notice.Key));
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Listenback recovery: " + ex, System.Diagnostics.TraceLevel.Error);
            AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), Lexicon.Get("audio.iq.recovery_state_unknown"));
        }
    }
}
