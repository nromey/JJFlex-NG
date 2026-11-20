#nullable enable

using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>
    /// The last walk end of a connect whose open failed: a SmartLink tier offer
    /// still pending there is put, once per connect, before the connect gives
    /// up (the tiers work, Track A repair round 2, from Codex's re-check).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Startup auto-connect walks its own legs
    /// inside <c>FlexBase.TryAutoConnect</c>, so no walk is left behind for
    /// the resumed walk to finish. When its SmartLink leg was refused by the
    /// tier (earning the offer) and its Local leg connected but did not open,
    /// with the command connection still up, every earlier walk end was
    /// skipped: the resumed walk had no walk, and the retry ladders run only
    /// once the transport has fallen. The offer was then dismissed unshown,
    /// and the operator never heard the recovery that was theirs by right.</para>
    /// <para><b>What it does not read, on purpose.</b> Whether the transport is
    /// still marked connected, and whether a walk ran, are NOT part of the
    /// decision: an open that failed is a connect that is giving up either
    /// way. The transport state is taken only so the trace can say which case
    /// this was.</para>
    /// <para><b>What stays explicit.</b> A connect the operator cancelled is
    /// never asked: its offer is dismissed with that reason. Nothing pending
    /// means nothing happens, so an open failure that earned no offer still
    /// reaches the connect's ordinary give-up, unchanged, and no failed open
    /// becomes a retry merely by passing through here. The once-per-connect
    /// rule, and No and Cancel, belong to the presenting delegate (the app's
    /// <c>OfferHigherSmartLinkTier</c>), which every other walk end shares.</para>
    /// <para>Pure apart from the delegates and one trace line, so the decision
    /// is driven for real in tests with fakes; it never saves an account and
    /// never shows a window itself.</para>
    /// </remarks>
    public static class TierOfferAtGiveUp
    {
        /// <summary>
        /// Put a pending offer before a connect whose open failed gives up.
        /// Returns True only when the operator raised the tier, the SmartLink
        /// connect was planned again and came up, and the radio then opened.
        /// </summary>
        /// <param name="pending">The rig's pending offer, or null.</param>
        /// <param name="cancelRequested">The operator cancelled this connect.</param>
        /// <param name="transportStillConnected">The command connection is still marked up. For the trace only.</param>
        /// <param name="offerAndReplan">Put the offer (once per connect); on Yes save the tier and plan the SmartLink connect again. True when that session came up.</param>
        /// <param name="open">Open the radio on the re-planned session.</param>
        /// <param name="commitOutcome">Commit the re-planned leg's outcome to the connection history.</param>
        /// <param name="dismiss">Clear the pending offer unshown, saying why.</param>
        public static bool RecoverAfterFailedOpen(
            SmartLinkTierOffer? pending,
            bool cancelRequested,
            bool transportStillConnected,
            Func<SmartLinkTierOffer, bool> offerAndReplan,
            Func<bool> open,
            Action<bool> commitOutcome,
            Action<string> dismiss)
        {
            if (pending == null) return false;
            if (cancelRequested)
            {
                dismiss("the open failed after the operator cancelled the connect");
                return false;
            }

            Tracing.TraceLine(
                $"TierOfferAtGiveUp: the open failed {(transportStillConnected ? "with the command connection still up" : "after the connection fell")}; " +
                $"putting the pending tier {(int)pending.OfferedTier + 1} offer to {pending.AccountEmail} for {pending.Serial} before the connect gives up",
                TraceLevel.Info);

            if (!offerAndReplan(pending)) return false;

            bool opened = open();
            commitOutcome(opened);
            Tracing.TraceLine(
                $"TierOfferAtGiveUp: SmartLink under the raised tier {(opened ? "opened" : "did not open")}",
                opened ? TraceLevel.Info : TraceLevel.Warning);
            return opened;
        }
    }
}
