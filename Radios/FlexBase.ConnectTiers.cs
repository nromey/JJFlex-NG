#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Flex.Smoothlake.FlexLib;
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

        /// <summary>
        /// Yes to the tier offer: plan the SmartLink connect to
        /// <paramref name="serial"/> again under the tier just saved, through
        /// the same brokering lookup (sendRemoteConnect re-reads the account
        /// from the shared manager). Returns True when the SmartLink session
        /// came up; the open is the caller's.
        /// </summary>
        /// <remarks>
        /// <para><b>A session this rig still holds open is retired FIRST</b>
        /// (Track A repair round 3, from Codex's re-check of round 2). A Local
        /// open can fail with its command connection still up (the antenna
        /// list never arrives), and the offer is put at that walk end. The
        /// re-plan's <see cref="Connect"/> then replaces <c>theRadio</c> with
        /// the WAN object without closing the old one, so two sessions
        /// presenting this program's one GUI client identity
        /// (FlexBase.ClientIdentity.cs) would be open to the same radio, which
        /// the identity contract says the radio may refuse by dropping either.
        /// See <see cref="RetireSessionBeforeReplan"/>.</para>
        /// <para>Every route into the re-plan comes through here, so a route
        /// whose transport has already fallen behaves exactly as before: there
        /// is nothing to retire, and the trace says so.</para>
        /// </remarks>
        public bool ReplanSmartLinkUnderRaisedTier(string serial, bool lowBW, bool pathForced, string where) =>
            ReplanSmartLinkUnderRaisedTier(serial, lowBW, pathForced, where,
                () => ReconnectRemote(serial, lowBW, forceWanPath: true, allowInteractiveLogin: true));

        /// <summary>
        /// The re-plan with its dial supplied, so a test can stand at the
        /// boundary with a real rig and a real live radio object and see what
        /// the dial would meet. Production passes <see cref="ReconnectRemote"/>.
        /// </summary>
        internal bool ReplanSmartLinkUnderRaisedTier(string serial, bool lowBW, bool pathForced, string where, Func<bool> dial)
        {
            RetireSessionBeforeReplan(where);
            SmartLinkPathForced = pathForced;
            Tracing.TraceLine($"{where}: dialling SmartLink again for {serial} (lowBW={lowBW}, pathForced={pathForced}) under the raised tier", TraceLevel.Info);
            return dial();
        }

        /// <summary>
        /// Close the session this rig still holds open on <c>theRadio</c>, if
        /// any, before a re-plan replaces the object. The open that used it has
        /// already failed, so this is that open's teardown, the one
        /// <see cref="Start"/> itself uses when an open fails part way (a
        /// cancel, a station-name timeout): the goodbye for this client's own
        /// registration (<c>client disconnect</c> with our handle, the same one
        /// an ordinary disconnect sends) and the socket closed as our own,
        /// so the fall is classed self-initiated and archives nothing. Then
        /// this rig's handlers are let go of on the object, as
        /// <see cref="Disconnect"/> does once it has fallen.
        /// </summary>
        /// <remarks>
        /// <para>Deliberately NOT <see cref="Disconnect"/>: that is the
        /// operator's hang-up. It announces the disconnect, plays the farewell,
        /// and puts profiles, PC audio, the microphone input and the speaker
        /// back, which are writes to the radio, for a session whose open never
        /// finished. Nothing here writes a setting or sends anything but the
        /// goodbye.</para>
        /// <para>The pending offer is already answered; the serial, bandwidth
        /// and forced path are the caller's parameters, and the brokering
        /// lookup is the shared manager's, so none of them is touched.</para>
        /// </remarks>
        /// <returns>True when a live session was retired.</returns>
        internal bool RetireSessionBeforeReplan(string where)
        {
            Radio? radio = theRadio;
            bool live;
            try { live = radio != null && radio.Connected; }
            catch { live = false; }
            if (!live)
            {
                Tracing.TraceLine($"{where}: no session still open on this rig; nothing to retire before the SmartLink re-plan", TraceLevel.Info);
                return false;
            }

            string why = where + ": the failed open's session retired before the SmartLink re-plan";
            Tracing.TraceLine($"{where}: {radio!.Serial} ({(radio.IsWan ? "SmartLink" : "local")}) still holds this program's session open after its open failed; retiring it before the SmartLink re-plan", TraceLevel.Info);
            releaseGuiClientRegistration(why);
            teardownDisconnect(why);
            if (!radio.Connected)
            {
                unwireProducerHandlers(radio);
                UnwireStationHandlers(radio);
            }
            else
            {
                Tracing.TraceLine($"{where}: {radio.Serial} still reports connected after the teardown; its handlers stay until it lets go", TraceLevel.Warning);
            }
            return true;
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

        /// <summary>
        /// Clears what one SmartLink dial decides, at its start. An offer an
        /// earlier dial left unshown is cleared through
        /// <see cref="DismissTierOffer"/>, so the trace says so (repair round
        /// 2, from Codex's re-check); the new dial earns its own or none.
        /// </summary>
        private void ResetConnectTierState()
        {
            DismissTierOffer("a new SmartLink dial on this rig starts, and decides its own offer");
            SmartLinkMethodInUse = null;
        }
    }
}
