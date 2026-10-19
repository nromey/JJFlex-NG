using Radios;
using Radios.Listenback;

namespace JJFlexWpf.Dialogs;

// Temporary, removable operator-inspection escape. Its sole call site is the
// unsuccessful-restore branch of ListenbackRecoveryPrompt.ReviewAsync.
internal static class ListenbackRecoveryAcknowledgement
{
    internal static string Offer(FlexBase rig, RadioRecoverySnapshot offered)
    {
        string body = ListenbackInspectionSummary.Describe(offered, s => rig.ListenbackResourcePresent(offered, s));
        bool approved = false;
        AdvisoryDialog.Show(Lexicon.Get("audio.iq.ack.title"), body, null,
            new AdvisoryDialog.AdvisoryAction(Lexicon.Get("audio.iq.ack.clear"), () => approved = true));
        if (!approved) return "audio.iq.recovery_pending";
        return rig.AcknowledgeInspectedListenbackRecovery(offered, true)
            ? "audio.iq.ack.cleared" : "audio.iq.ack.changed";
    }

}
