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
    /// registered". The second reading was wrong: the radio in question had
    /// been in that very account's list two days earlier, as
    /// <c>status=Available</c>. Registration lives in the radio and does not
    /// lapse. The query's own comment had called an empty list "a definitive
    /// answer", which is <i>absence is not evidence</i> written down as
    /// intent.</para>
    ///
    /// <para><b>Positive evidence is keepable; absence is not.</b> Hence the
    /// asymmetry in <see cref="Judge"/>: a serial found in a list we are
    /// holding is proof, however old that list is, because a radio that was
    /// once listed was once registered and registration does not expire. A
    /// serial missing from a list we are holding proves nothing at all, so it
    /// is never the final word — the caller must go and ask.</para>
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
        /// <param name="AccountsConsulted">How many account lists were
        /// examined. One means the old, narrower question was asked; more
        /// means the answer describes the operator rather than the session
        /// (#352).</param>
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
            /// The most recent list a server pushed to a session this process
            /// is holding. Positive evidence only.
            /// </summary>
            HeldBySession,
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
            var lists = listsInHand ?? Array.Empty<AccountList>();
            bool aServerAnsweredThisCall =
                lists.Any(l => l.Source == ListSource.ServerPushThisCall);

            // No serial means no question. Never a negative: a radio we cannot
            // name has not been found absent from anything.
            if (string.IsNullOrEmpty(serial))
                return Finding.Unknown(lists.Count);

            // Connected over SmartLink. The connection IS the proof, and it is
            // better evidence than any list.
            if (arrivedOverSmartLink)
                return new Finding(
                    FlexBase.SmartLinkRegistrationQuery.Registered,
                    aServerAnsweredThisCall, lists.Count, string.Empty);

            if (!anAccountIsInHand)
                return anySavedAccount
                    ? Finding.Unknown(lists.Count)
                    : new Finding(FlexBase.SmartLinkRegistrationQuery.NoAccount,
                        false, 0, string.Empty);

            // Positive evidence first, and from ANY account we hold — this is
            // #352's ask. A radio in another of the operator's own accounts is
            // registered; saying "not registered" because the wrong account was
            // asked is the 2026-08-05 incident, and refusing to answer at all
            // is only the polite version of the same gap.
            foreach (var list in lists)
            {
                if (list.Serials == null) continue;
                if (list.Serials.Any(s => string.Equals(s, serial, StringComparison.OrdinalIgnoreCase)))
                    return new Finding(
                        FlexBase.SmartLinkRegistrationQuery.Registered,
                        aServerAnsweredThisCall, lists.Count, list.Account ?? string.Empty);
            }

            // Nothing positive. An absence only counts as an observation when
            // a server actually answered; otherwise we simply did not look.
            if (!aServerAnsweredThisCall)
                return Finding.Unknown(lists.Count);

            return new Finding(
                FlexBase.SmartLinkRegistrationQuery.NotInAccountList,
                true, lists.Count, string.Empty);
        }

        /// <summary>
        /// The list each held SmartLink session is carrying, labelled
        /// <see cref="ListSource.HeldBySession"/>.
        /// </summary>
        internal static IReadOnlyCollection<AccountList> HeldLists(
            IEnumerable<Radios.SmartLink.IWanSessionOwner?>? sessions)
        {
            var lists = new List<AccountList>();
            if (sessions == null) return lists;
            foreach (var held in sessions)
            {
                if (held == null) continue;
                var available = held.AvailableRadios;
                if (available == null) continue;
                lists.Add(new AccountList(
                    held.AccountId ?? string.Empty,
                    available.Select(r => r.Serial).Where(s => !string.IsNullOrEmpty(s)).ToList(),
                    ListSource.HeldBySession));
            }
            return lists;
        }

        /// <summary>
        /// Whether a prompt built on this finding may collect an answer that
        /// outlives the prompt.
        /// </summary>
        /// <remarks>
        /// <para><b>The rule, and it is general.</b> A prompt may record a
        /// durable answer only when the premise it states is a fact the
        /// application positively observed, from the authority for that fact,
        /// about the exact radio being asked about. A premise that rests on an
        /// absence, a timeout, a cache, or one account's view of a
        /// multi-account world may be spoken — but it may not be
        /// <i>collected on</i>.</para>
        ///
        /// <para><b>Why the two are different.</b> An advisory that is wrong is
        /// ignored, and the next run corrects it; being wrong costs one
        /// sighting. An offer that is wrong collects a permanent answer to a
        /// false premise, and the operator's own click then suppresses the
        /// correction forever. That is the ruling in #352, arrived at the hard
        /// way on 2026-09-23.</para>
        ///
        /// <para><b>What this does not forbid.</b> A "do not show me this
        /// again" checkbox is fine on any advisory, true premise or false: the
        /// operator is the authority on whether they want to read a sentence
        /// again, the record claims nothing about the radio, and Settings lists
        /// every silenced message back in its own words. The line is between
        /// silencing a MESSAGE and asserting a FACT — or switching off a class
        /// of future help on the strength of one.</para>
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
                // call. Even then it is evidence of unreachability, never of
                // non-registration — so a prompt may say what was seen, and may
                // ask the operator about their OWN intent, which they are the
                // authority on. It may never record a conclusion of ours.
                FlexBase.SmartLinkRegistrationQuery.NotInAccountList =>
                    finding.FromALiveServerAnswer,

                _ => false,
            };
    }
}
