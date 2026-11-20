#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;
using Radios.SmartLink;

namespace Radios
{
    /// <summary>
    /// FlexBase, SmartLink connection tiers: what this connect is allowed to
    /// use, what it used, and the one-time offer. The tiers plan, ruled by Noel
    /// 2026-11-19 (Track A).
    /// </summary>
    /// <remarks>
    /// A separate file so the connect path in FlexBase.Wan.cs carries only the
    /// calls. The decision itself is <see cref="SmartLinkMethodPolicy"/>; the
    /// record of it is <see cref="SmartLinkConnectRecord"/>.
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>
        /// The SmartLink method this connection was dialled with, set when the
        /// broker's handle arrives; null before that, and for a connection not
        /// made over SmartLink by this build's connect path.
        /// </summary>
        public SmartLinkMethod? SmartLinkMethodInUse { get; private set; }

        /// <summary>
        /// True when the live connection is a hole-punched SmartLink session:
        /// the question every punched-session guard asks. It reads the METHOD
        /// ACTUALLY USED, not the radio's punch flag, which says what the radio
        /// reported (and a list refresh rewrites) rather than how we connected.
        /// A "Hole punch always" connect to a radio that advertises ports is
        /// punched, and must be guarded, even though the radio says no punch is
        /// needed. When a WAN connection carries no recorded method (a path
        /// that predates the record), the radio's flag is the fallback, so the
        /// guard errs towards protecting the session.
        /// </summary>
        public bool ConnectedByHolePunch
        {
            get
            {
                try
                {
                    if (theRadio == null || !theRadio.IsWan) return false;
                    if (SmartLinkMethodInUse is SmartLinkMethod m) return m == SmartLinkMethod.HolePunch;
                    return theRadio.RequiresHolePunch;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Set by the connect walk: the operator pinned the SmartLink path from
        /// the context menu. Recorded in the connect record; it never changes
        /// the methods (decision 9: a forced path is not a forced method).
        /// </summary>
        public bool SmartLinkPathForced { get; set; }

        /// <summary>
        /// The one-time tier offer the most recent refused SmartLink connect
        /// earned, or null. Cleared at the start of every SmartLink dial.
        /// </summary>
        public SmartLinkTierOffer? PendingTierOffer { get; private set; }

        /// <summary>
        /// The account whose tier governs a connect brokered by
        /// <paramref name="brokeringEmail"/>'s session (#798): the saved
        /// account with that email, else the session's own adopted account when
        /// it is that one, else none (Tier 1, never chosen). Never the current
        /// account by habit: when the brokering account differs, the current
        /// one is ignored.
        /// </summary>
        internal static SmartLinkAccount? AccountWhoseTierGoverns(string? brokeringEmail,
            IEnumerable<SmartLinkAccount>? saved, SmartLinkAccount? current)
        {
            if (string.IsNullOrEmpty(brokeringEmail)) return null;
            var match = saved?.FirstOrDefault(a => a != null
                && string.Equals(a.Email, brokeringEmail, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            if (current != null && string.Equals(current.Email, brokeringEmail, StringComparison.OrdinalIgnoreCase))
                return current;
            return null;
        }

        /// <summary>
        /// The operator answered the offer. Yes saves the offered tier as
        /// chosen; No saves the account's CURRENT tier as chosen, so declining
        /// never lowers an account (Noel, 2026-11-19 20:34). Writes only our own accounts
        /// file, never a radio. Returns the tier saved, or null when the
        /// account is no longer saved here. A cancelled offer is not an answer
        /// and must not reach this method.
        /// </summary>
        public SmartLinkConnectionMode? AnswerTierOffer(SmartLinkTierOffer offer, bool yes)
        {
            if (offer == null) return null;
            var tier = SmartLinkTierOffers.TierFor(offer, yes);
            bool saved = AccountManager.ChooseConnectionMode(offer.AccountEmail, tier);
            Tracing.TraceLine(
                $"AnswerTierOffer: {(yes ? "yes" : "no")} for {offer.AccountEmail} (radio {offer.Serial}): tier {(int)tier + 1} saved as chosen={saved}",
                saved ? TraceLevel.Info : TraceLevel.Warning);
            if (PendingTierOffer == offer) PendingTierOffer = null;
            return saved ? tier : null;
        }

        /// <summary>
        /// Clears a pending offer that is not going to be presented, saying why
        /// in the trace (the radio opened over another path, the operator
        /// cancelled, it was already asked on this connect, or it was answered
        /// with Cancel). Saves nothing. A no-op, and silent, when none is pending.
        /// </summary>
        public void DismissTierOffer(string why)
        {
            var offer = PendingTierOffer;
            if (offer == null) return;
            PendingTierOffer = null;
            Tracing.TraceLine(
                $"DismissTierOffer: the tier {(int)offer.OfferedTier + 1} offer to {offer.AccountEmail} for {offer.Serial} is not presented: {why}",
                TraceLevel.Info);
        }

        /// <summary>The offer's question, naming the radio the operator's way. DRAFT words.</summary>
        public string TierOfferQuestion(SmartLinkTierOffer offer) =>
            SmartLinkTierOffers.Question(offer, RadioNameForSpeech(offer.Serial));

        /// <summary>
        /// The Settings warning for a per-radio connection choice the radio's
        /// known owning SmartLink account would refuse, or null. The owner is
        /// the account the SmartLink bank attributes the radio to, and the
        /// report is the newest this run has: the last connect's record, else
        /// the banked list radio. Either unknown means no warning, because
        /// Settings can only warn when the owner is known. Reads only.
        /// </summary>
        public static string? TierRefusalWarningFor(string serial, RadioConnectionPreference choice, string radioName)
        {
            try
            {
                if (string.IsNullOrEmpty(serial)) return null;
                string owner = GetWanAccountForSerial(serial);
                if (string.IsNullOrEmpty(owner)) return null;
                var account = AccountManager.GetAccountByEmail(owner);
                var report = SmartLinkConnectRecords.For(serial)?.Report;
                if (report == null)
                {
                    var listed = findWanEntry(serial, out _);
                    if (listed != null) report = RadioPortReport.FromRadio(listed);
                }
                return SmartLinkTierOffers.SettingsWarning(serial, owner,
                    account?.ConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly,
                    account?.TierChosen ?? false, choice, report, radioName);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"TierRefusalWarningFor({serial}): {ex.Message}", TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>Clears what one SmartLink dial decides, at its start.</summary>
        private void ResetConnectTierState()
        {
            PendingTierOffer = null;
            SmartLinkMethodInUse = null;
        }
    }
}
