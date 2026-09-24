#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// What a SmartLink account's radio list can prove about one radio, and —
    /// the part that matters — what it cannot.
    /// </summary>
    /// <remarks>
    /// <para><b>The list is a reachability list, not a registration roster.</b>
    /// The SmartLink server tells an account which of its radios it can route
    /// to <i>right now</i>. A radio drops out of that list when it is powered
    /// off, when its own path to the internet is down, when SmartLink is
    /// switched off at the radio, and when it was never registered — four
    /// causes, one observation. The application already reads it correctly in
    /// one place: an empty list on the Remote path is announced as "No
    /// SmartLink radios available. The remote radio may be turned off."</para>
    ///
    /// <para><b>The defect this class exists to end.</b> On 2026-09-23 the same
    /// empty list, from the same push, was rendered twice 660 ms apart — once
    /// as "the remote radio may be turned off" and once as "this radio is not
    /// registered". The second reading claimed more than an empty list can
    /// carry: the radio in question had been in that very account's list two
    /// days earlier, as <c>status=Available</c>, and one empty list cannot say
    /// which of the four causes removed it. The query's own comment had called
    /// an empty list "a definitive answer", which is <i>absence is not
    /// evidence</i> written down as intent.</para>
    ///
    /// <para><b>What a listing proves, and for how long.</b> A serial in a list
    /// proves the radio was registered to that account, and reachable, at the
    /// moment the list was sent. It does NOT prove either is still true later:
    /// a radio can be unregistered — this application has a command for it —
    /// and FlexRadio documents that registering a radio to a new account
    /// replaces the old account's registration. Track L treated a listing as
    /// proof "however old", on the reasoning that registration does not lapse;
    /// Sol's review of 2026-09-23 found that reasoning omits both (#619).</para>
    ///
    /// <para><b>Every caller asks about NOW.</b> The connect advisory stays
    /// silent on Registered because it concludes the operator has nothing to
    /// gain from being told about SmartLink for this radio; the Radio Setup
    /// checklist prints "Done. This radio is already registered to …". Both
    /// are present-tense claims, and nothing asks the historical question
    /// "was this radio ever listed". So only a CURRENT list may answer
    /// Registered: one the server pushed during the call, or the latest list
    /// held by a session that is connected now. A list held by a session that
    /// is no longer connected is history, and is set aside rather than
    /// reported.</para>
    ///
    /// <para><b>An absence is weaker still.</b> A serial missing from a list
    /// is never the final word unless the server sent that list during this
    /// call — and even then it says only that this account's list omitted the
    /// radio at that moment.</para>
    ///
    /// <para>Pure on purpose. Every rule here is decided without a radio, a
    /// network or a FlexBase, which is what lets the suite hold it.</para>
    /// </remarks>
    public static class SmartLinkRegistrationEvidence
    {
        /// <summary>
        /// Everything the evidence supports, and how good the evidence was.
        /// </summary>
        /// <param name="Verdict">The answer to "is this radio reachable
        /// through SmartLink for this operator".</param>
        /// <param name="FromALiveServerAnswer">True only when the server
        /// answered during the call that produced this. A cache can carry
        /// positive evidence forward but can never establish an absence, so
        /// this is the flag a caller checks before building anything durable
        /// on a negative.</param>
        /// <param name="AccountsConsulted">How many DISTINCT accounts answered
        /// — accounts, not lists. One account routinely supplies two lists (the
        /// push captured during the call and the held session carrying the
        /// same one), and a session that has not yet heard from the server
        /// supplies none. One means the old, narrower question was asked; more
        /// means the answer describes the operator rather than the session
        /// (#352). Callers print "only the signed-in account was asked" on
        /// this number, so a list count here suppressed a true caveat.</param>
        /// <param name="ListedUnderAccount">The account whose list contains
        /// the serial, when one does. Empty otherwise.</param>
        public readonly record struct Finding(
            FlexBase.SmartLinkRegistrationQuery Verdict,
            bool FromALiveServerAnswer,
            int AccountsConsulted,
            string ListedUnderAccount)
        {
            /// <summary>An answer nobody could establish.</summary>
            public static Finding Unknown(int accountsConsulted = 0) => new(
                FlexBase.SmartLinkRegistrationQuery.Unknown, false, accountsConsulted, string.Empty);
        }

        /// <summary>
        /// Where a list came from. The verdict is derived from THIS, and never
        /// from a flag a caller sets by hand.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the source travels with the list.</b> Track L gave
        /// <see cref="Judge"/> a boolean — "a server answered this call" — and
        /// the caller built it from a latch. Sol's review of 2026-09-23 found
        /// the latch could be set by something that was not a server: the
        /// connect flow replays a held copy of an account's list through the
        /// same handler a live push uses, and that handler set the latch. The
        /// flag was therefore set and substantively false, and the negative
        /// verdict could be built from held state (#619). A flag constructed
        /// away from the evidence can always drift from it; a label attached
        /// at the moment the list is received cannot.</para>
        /// </remarks>
        public enum ListSource
        {
            /// <summary>
            /// The SmartLink server pushed this list during the call being
            /// judged, and it was captured by the live-push entry point at the
            /// moment of receipt. The only source that can establish an
            /// absence. A replay of a held copy is never labelled this.
            /// </summary>
            ServerPushThisCall,

            /// <summary>
            /// The most recent list a server pushed to a session that is
            /// connected now. Positive evidence of the present, and positive
            /// only: it can say the radio is listed, never that it is not.
            /// </summary>
            /// <remarks>
            /// Current because the session that received it is still the live
            /// one — a list from before this session connected is not in play.
            /// One gap, stated rather than hidden: a session keeps its list
            /// across a drop, and records neither when it reconnected nor
            /// whether the list predates that, so between a reconnect and the
            /// new connection's first list (79 ms after registration in the
            /// 2026-09-23 trace) a connected session still carries the previous
            /// connection's list. Closing that needs the session owner to reset
            /// or stamp its list per connection, which is outside the query.
            /// </remarks>
            HeldByAConnectedSession,

            /// <summary>
            /// A list held by a session that is not connected now. History:
            /// the radio was listed then. No caller asks that question, so the
            /// judge sets these aside — they are neither proof nor an account
            /// consulted.
            /// </summary>
            HeldByADisconnectedSession,
        }

        /// <summary>
        /// One account's list as we hold it: whose it is, which serials are in
        /// it, and where it came from.
        /// </summary>
        public readonly record struct AccountList(
            string Account, IReadOnlyCollection<string> Serials, ListSource Source);

        /// <summary>
        /// Turn what was observed into a verdict, with its provenance
        /// attached.
        /// </summary>
        /// <param name="serial">The connected radio's serial.</param>
        /// <param name="arrivedOverSmartLink">We are talking to this radio
        /// through SmartLink at this moment. That is proof of registration by
        /// itself and outranks every list.</param>
        /// <param name="anySavedAccount">Any SmartLink account has ever been
        /// saved on this computer. Distinguishes "we could not ask" from "the
        /// operator has never been told SmartLink exists".</param>
        /// <param name="anAccountIsInHand">An account was resolved to ask
        /// about.</param>
        /// <param name="listsInHand">Every account list available, each
        /// labelled with where it came from. Whether a server answered during
        /// this call is read from those labels — a
        /// <see cref="ListSource.ServerPushThisCall"/> list is the answer — and
        /// is deliberately not a parameter, so no caller can assert it.</param>
        public static Finding Judge(
            string? serial,
            bool arrivedOverSmartLink,
            bool anySavedAccount,
            bool anAccountIsInHand,
            IReadOnlyCollection<AccountList>? listsInHand)
        {
            // History is set aside before anything else reads the lists: it
            // is not proof of the present, and an account whose only list is
            // history has not answered this question.
            var lists = (listsInHand ?? Array.Empty<AccountList>())
                .Where(l => l.Source != ListSource.HeldByADisconnectedSession)
                .ToList();
            bool aServerAnsweredThisCall =
                lists.Any(l => l.Source == ListSource.ServerPushThisCall);
            int accounts = DistinctAccounts(lists);

            // No serial means no question. Never a negative: a radio we cannot
            // name has not been found absent from anything.
            if (string.IsNullOrEmpty(serial))
                return Finding.Unknown(accounts);

            // Connected over SmartLink. The connection IS the proof, and it is
            // better evidence than any list.
            if (arrivedOverSmartLink)
                return new Finding(
                    FlexBase.SmartLinkRegistrationQuery.Registered,
                    aServerAnsweredThisCall, accounts, string.Empty);

            if (!anAccountIsInHand)
                return anySavedAccount
                    ? Finding.Unknown(accounts)
                    : new Finding(FlexBase.SmartLinkRegistrationQuery.NoAccount,
                        false, 0, string.Empty);

            // Positive evidence first, from any CURRENT list of any account we
            // hold — this is #352's ask. A radio in another of the operator's
            // own accounts is registered; saying "not registered" because the
            // wrong account was asked is the 2026-08-05 incident, and refusing
            // to answer at all is only the polite version of the same gap.
            foreach (var list in lists)
            {
                if (list.Serials == null) continue;
                if (list.Serials.Any(s => string.Equals(s, serial, StringComparison.OrdinalIgnoreCase)))
                    return new Finding(
                        FlexBase.SmartLinkRegistrationQuery.Registered,
                        aServerAnsweredThisCall, accounts, list.Account ?? string.Empty);
            }

            // Nothing positive. An absence only counts as an observation when
            // a server actually answered; otherwise we simply did not look.
            if (!aServerAnsweredThisCall)
                return Finding.Unknown(accounts);

            return new Finding(
                FlexBase.SmartLinkRegistrationQuery.NotInAccountList,
                true, accounts, string.Empty);
        }

        /// <summary>
        /// How many different accounts these lists came from. Account ids are
        /// emails and are matched the way the account manager and the session
        /// coordinator match them, ignoring case.
        /// </summary>
        private static int DistinctAccounts(IEnumerable<AccountList> lists) =>
            lists.Select(l => l.Account ?? string.Empty)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .Count();

        /// <summary>
        /// The list each held SmartLink session is carrying — from every
        /// session the server has actually sent a list to, and from no other —
        /// labelled by whether that session is connected now.
        /// </summary>
        /// <remarks>
        /// A session is created holding an EMPTY radio array, and keeps it
        /// until the server's first list arrives. That array is the owner's
        /// initial value, not an answer: it looks exactly like "this account
        /// has no radios", and counting it made an account that had said
        /// nothing read as consulted. <c>LastRadioListUtc</c> is null until
        /// the first list lands, so it is what tells the two apart.
        /// </remarks>
        internal static IReadOnlyCollection<AccountList> HeldLists(
            IEnumerable<Radios.SmartLink.IWanSessionOwner?>? sessions)
        {
            var lists = new List<AccountList>();
            if (sessions == null) return lists;
            foreach (var held in sessions)
            {
                if (held == null) continue;
                if (held.LastRadioListUtc == null) continue;
                var available = held.AvailableRadios;
                if (available == null) continue;
                lists.Add(new AccountList(
                    held.AccountId ?? string.Empty,
                    available.Select(r => r.Serial).Where(s => !string.IsNullOrEmpty(s)).ToList(),
                    held.IsConnected
                        ? ListSource.HeldByAConnectedSession
                        : ListSource.HeldByADisconnectedSession));
            }
            return lists;
        }

        /// <summary>
        /// Whether a prompt built on this finding may collect an answer that
        /// outlives the prompt.
        /// </summary>
        /// <remarks>
        /// <para><b>The rule, and it is general: the premise a prompt states
        /// must be supported at the scope and time it claims.</b> Before a
        /// prompt may record a durable answer, what it says it observed has to
        /// have been observed — by the authority for that fact, about the exact
        /// radio asked about, for the account it names, at the moment it
        /// implies. Wording is part of the premise: a sentence that claims more
        /// than the evidence carries is a false premise even when the evidence
        /// is live.</para>
        ///
        /// <para><b>What each verdict supports, at its own scope.</b>
        /// <see cref="FlexBase.SmartLinkRegistrationQuery.Registered"/>: this
        /// account's current list carried the radio, so it is registered to
        /// that account now.
        /// <see cref="FlexBase.SmartLinkRegistrationQuery.NoAccount"/>: this
        /// computer holds no SmartLink account, read from this computer.
        /// <see cref="FlexBase.SmartLinkRegistrationQuery.NotInAccountList"/>,
        /// from a live push, supports ONLY "this account's server list omitted
        /// the radio at that moment". It does not establish that the radio is
        /// unregistered, or unreachable for any other account, or that it will
        /// still be missing a minute later. A held absence supports nothing at
        /// all.</para>
        ///
        /// <para><b>So a live absence may carry one kind of durable answer and
        /// not another.</b> The operator is the authority on their own use of
        /// the radio, so a prompt whose words state only the narrow observation
        /// may ask them for a preference about that use and keep the answer.
        /// It may never record a conclusion of ours about the radio, and its
        /// words may not claim more than the observation. That is why this
        /// returns true for a live absence: the permission is for the
        /// operator's preference, and it is only as good as the sentence
        /// beside the button. (Track L's version of this rule said an absence
        /// could never be collected on while this very method allowed a live
        /// one — the two now say the same thing.)</para>
        ///
        /// <para><b>Why a prompt is held to more than an advisory.</b> An
        /// advisory that is wrong is ignored, and the next run corrects it;
        /// being wrong costs one sighting. An offer that is wrong collects a
        /// permanent answer to a false premise, and the operator's own click
        /// then suppresses the correction forever. That is the ruling in #352,
        /// arrived at the hard way on 2026-09-23.</para>
        ///
        /// <para><b>What this does not forbid.</b> A "do not show me this
        /// again" checkbox is fine on any advisory, true premise or false: the
        /// operator is the authority on whether they want to read a sentence
        /// again, the record claims nothing about the radio, and Settings lists
        /// every silenced message back in its own words. The line is between
        /// silencing a MESSAGE and asserting a FACT — or switching off a class
        /// of future help on the strength of one.</para>
        ///
        /// <para><b>Where it is enforced.</b> At the write, not only before the
        /// prompt: the connect advisory's recording method takes the finding
        /// its prompt was built on and refuses to write without this, so a
        /// caller added later cannot record a durable answer on a premise
        /// nobody checked (#619).</para>
        /// </remarks>
        public static bool CanCarryADurableAnswer(Finding finding) =>
            finding.Verdict switch
            {
                // Positive observation, from the authority, about this radio.
                FlexBase.SmartLinkRegistrationQuery.Registered => true,

                // "No SmartLink account has ever been saved here" is a fact
                // about this computer, read directly from this computer.
                FlexBase.SmartLinkRegistrationQuery.NoAccount => true,

                // An absence, and only when a server actually produced it this
                // call. It supports "this account's list omitted the radio at
                // that moment" and nothing wider — so a prompt may say exactly
                // that, and may ask the operator about their OWN use, which
                // they are the authority on. It may never record a conclusion
                // of ours about the radio.
                FlexBase.SmartLinkRegistrationQuery.NotInAccountList =>
                    finding.FromALiveServerAnswer,

                _ => false,
            };
    }
}
