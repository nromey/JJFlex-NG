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
    internal static bool Confirm(RadioRecoverySnapshot snapshot)
    {
        bool approved = false;
        if (snapshot.UnreadableHash != null)
        {
            AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), Lexicon.Get("audio.iq.unreadable_offer"), null,
                new AdvisoryDialog.AdvisoryAction(Lexicon.Get("audio.iq.unreadable_acknowledge"), () => approved = true));
            return approved;
        }
        string changes = string.Join(Environment.NewLine, snapshot.Settings.Select(s =>
            Lexicon.Get("audio.iq.saved_setting", ("setting", SettingText(s.Name)), ("value", s.Value))));
        string body = Lexicon.Get("audio.iq.recovery_offer", ("serial", snapshot.Serial), ("changes", changes));
        if (snapshot.ResourceCreationPending) body += Environment.NewLine + Lexicon.Get("audio.iq.creation_uncertain");
        AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), body, null,
            new AdvisoryDialog.AdvisoryAction(Lexicon.Get("audio.iq.restore_now"), () => approved = true));
        return approved;
    }
    internal static async Task OfferOnConnectAsync(FlexBase rig)
    {
        try
        {
            var saved = rig.PendingListenbackRecovery;
            if (saved == null || !Confirm(saved)) return;
            if (saved.UnreadableHash != null)
            {
                bool archived = rig.AcknowledgeUnreadableListenbackRecovery(saved, true);
                AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), Lexicon.Get(archived ? "audio.iq.unreadable_archived" : "audio.iq.recovery_pending"));
                return;
            }
            bool restored = await rig.RestoreListenbackSnapshotAsync(saved, true);
            AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"),
                Lexicon.Get(restored ? "audio.iq.restored" : "audio.iq.recovery_pending"));
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Listenback recovery: " + ex, System.Diagnostics.TraceLevel.Error);
            AdvisoryDialog.Show(Lexicon.Get("audio.iq.recovery_title"), Lexicon.Get("audio.iq.recovery_pending"));
        }
    }
}
