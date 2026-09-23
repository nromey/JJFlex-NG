using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;

using Verdict = Radios.FlexBase.SmartLinkRegistrationQuery;
using Evidence = Radios.SmartLinkRegistrationEvidence;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 45 Track L, task #352: what a SmartLink account's radio list can
    /// prove about one radio, and what it cannot.
    ///
    /// <para><b>The incident these pin.</b> On 2026-09-23 at 05:42 the
    /// application connected locally to a FLEX-8600, read an empty SmartLink
    /// list for the operator's own account, and told him the radio was not
    /// registered. It was. The same serial appears in that same account's list,
    /// as <c>status=Available</c>, in the trace from two days earlier;
    /// registration lives in the radio and does not lapse. Worse, the same
    /// empty push had been announced 660 ms earlier on the Remote path as "No
    /// SmartLink radios available. The remote radio may be turned off" — the
    /// application held both readings of one fact and shipped the wrong one
    /// where it could do damage.</para>
    ///
    /// <para><b>Why a test and not a comment.</b> The old code carried a
    /// comment calling an empty list "a definitive answer", so the reasoning
    /// was not absent, it was written down and wrong. A sentence cannot refuse
    /// a change; these can.</para>
    /// </summary>
    public sealed class SmartLinkRegistrationEvidenceTests
    {
        private const string Serial = "4925-1213-8600-6245";
        private const string Mine = "operator@example.com";
        private const string Other = "friend@example.com";

        private static IReadOnlyCollection<Evidence.AccountList> Lists(
            params (string account, string[] serials)[] rows) =>
            rows.Select(r => new Evidence.AccountList(r.account, r.serials)).ToList();

        private static Evidence.Finding Ask(
            IReadOnlyCollection<Evidence.AccountList> lists,
            bool serverAnswered,
            string serial = Serial,
            bool overSmartLink = false,
            bool accountInHand = true,
            bool anySaved = true) =>
            Evidence.Judge(serial, overSmartLink, anySaved, accountInHand, lists, serverAnswered);

        // ------------------------------------------------------------------
        // The vocabulary
        // ------------------------------------------------------------------

        /// <summary>
        /// The word that did the damage. "NotRegistered" is a claim about the
        /// radio; nothing in this application can establish it, because the
        /// radio cannot be asked (probed 2026-08-03) and the server reports
        /// only what it can route to. A future author who reaches for the old
        /// name is reaching for a fact we do not have.
        /// </summary>
        [Fact]
        public void No_verdict_claims_the_radio_is_unregistered()
        {
            var names = Enum.GetNames(typeof(Verdict));

            Assert.DoesNotContain("NotRegistered", names);
            Assert.Contains("NotInAccountList", names);
        }

        // ------------------------------------------------------------------
        // Positive evidence is keepable
        // ------------------------------------------------------------------

        /// <summary>
        /// A serial in a list is proof however old the list is: a radio the
        /// broker once offered is a radio that completed registration, and
        /// registration does not expire. This is what lets the query answer
        /// "registered" without a round trip.
        /// </summary>
        [Fact]
        public void A_serial_found_in_a_stale_list_is_still_proof()
        {
            var f = Ask(Lists((Mine, new[] { Serial })), serverAnswered: false);

            Assert.Equal(Verdict.Registered, f.Verdict);
            Assert.Equal(Mine, f.ListedUnderAccount);
        }

        /// <summary>
        /// #352's own ask. The radio belongs to an account the operator is not
        /// signed in with; the honest answer is "registered, over there", not
        /// the silence the borrowed-account guard falls back to.
        /// </summary>
        [Fact]
        public void A_radio_listed_under_another_of_the_operators_accounts_is_registered()
        {
            var f = Ask(Lists(
                (Mine, Array.Empty<string>()),
                (Other, new[] { Serial })), serverAnswered: true);

            Assert.Equal(Verdict.Registered, f.Verdict);
            Assert.Equal(Other, f.ListedUnderAccount);
            Assert.Equal(2, f.AccountsConsulted);
        }

        [Fact]
        public void A_serial_matches_whatever_case_the_server_sent_it_in()
        {
            var f = Ask(Lists((Mine, new[] { Serial.ToLowerInvariant() })),
                serverAnswered: true, serial: Serial.ToUpperInvariant());

            Assert.Equal(Verdict.Registered, f.Verdict);
        }

        /// <summary>
        /// Arriving over SmartLink IS the proof, and it outranks a list that
        /// disagrees — a list can lag, a live connection cannot.
        /// </summary>
        [Fact]
        public void Arriving_over_smartlink_outranks_an_empty_list()
        {
            var f = Ask(Lists((Mine, Array.Empty<string>())),
                serverAnswered: true, overSmartLink: true);

            Assert.Equal(Verdict.Registered, f.Verdict);
        }

        // ------------------------------------------------------------------
        // An absence is not
        // ------------------------------------------------------------------

        /// <summary>
        /// The 2026-09-23 path exactly: a list latched earlier in the run, the
        /// serial not in it, nothing asked this call. The old code returned a
        /// verdict here in microseconds and an offer collected a permanent
        /// answer on it.
        /// </summary>
        [Fact]
        public void An_absence_in_a_cache_is_not_an_answer()
        {
            var f = Ask(Lists((Mine, new[] { "9999-0000-6400-0001" })), serverAnswered: false);

            Assert.Equal(Verdict.Unknown, f.Verdict);
            Assert.False(f.FromALiveServerAnswer);
        }

        /// <summary>
        /// No shape of held evidence may produce the negative verdict without a
        /// server having spoken during the call. Swept rather than asserted
        /// once, because the tempting optimisation is always "we already have a
        /// list, use it".
        /// </summary>
        [Fact]
        public void The_negative_verdict_cannot_be_manufactured_from_held_lists()
        {
            var shapes = new[]
            {
                Lists(),
                Lists((Mine, Array.Empty<string>())),
                Lists((Mine, new[] { "9999-0000-6400-0001" })),
                Lists((Mine, Array.Empty<string>()), (Other, Array.Empty<string>())),
            };

            foreach (var shape in shapes)
                Assert.NotEqual(Verdict.NotInAccountList, Ask(shape, serverAnswered: false).Verdict);
        }

        /// <summary>
        /// When a server DID answer and the radio is in nobody's list, the
        /// verdict is the narrow one: not in the list. It says nothing about
        /// registration, and the flag says the observation was live.
        /// </summary>
        [Fact]
        public void A_live_empty_answer_reports_absence_from_the_list_and_nothing_more()
        {
            var f = Ask(Lists((Mine, Array.Empty<string>())), serverAnswered: true);

            Assert.Equal(Verdict.NotInAccountList, f.Verdict);
            Assert.True(f.FromALiveServerAnswer);
            Assert.Equal(string.Empty, f.ListedUnderAccount);
        }

        /// <summary>
        /// A radio we cannot name has not been found absent from anything.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_radio_with_no_serial_never_produces_a_negative(string serial)
        {
            Assert.Equal(Verdict.Unknown,
                Ask(Lists((Mine, Array.Empty<string>())), serverAnswered: true, serial: serial).Verdict);
        }

        // ------------------------------------------------------------------
        // Having nobody to ask
        // ------------------------------------------------------------------

        /// <summary>
        /// "Never heard of SmartLink" and "could not reach SmartLink" want
        /// opposite behaviour from a caller — one is an invitation, the other
        /// is a reason for silence — so they must not collapse into one
        /// value.
        /// </summary>
        [Fact]
        public void Never_having_had_an_account_is_distinct_from_not_being_able_to_ask()
        {
            Assert.Equal(Verdict.NoAccount,
                Ask(Lists(), serverAnswered: false, accountInHand: false, anySaved: false).Verdict);

            Assert.Equal(Verdict.Unknown,
                Ask(Lists(), serverAnswered: false, accountInHand: false, anySaved: true).Verdict);
        }

        // ------------------------------------------------------------------
        // The rule: when a prompt may collect something durable
        // ------------------------------------------------------------------

        /// <summary>
        /// An advisory that is wrong is ignored and self-corrects. An offer
        /// that is wrong collects a permanent answer to a false premise, and
        /// the operator's own click then suppresses the correction forever
        /// (#352). So the premise a prompt states has to be a live observation
        /// before the button beside it may write to disk.
        /// </summary>
        [Fact]
        public void A_prompt_may_collect_only_on_a_premise_that_was_observed()
        {
            var live = new Evidence.Finding(Verdict.NotInAccountList, true, 1, string.Empty);
            var cached = new Evidence.Finding(Verdict.NotInAccountList, false, 1, string.Empty);

            Assert.True(Evidence.CanCarryADurableAnswer(live));
            Assert.False(Evidence.CanCarryADurableAnswer(cached));
        }

        /// <summary>
        /// A shrug is never a premise, however it was arrived at.
        /// </summary>
        [Fact]
        public void An_unknown_can_never_carry_a_durable_answer()
        {
            Assert.False(Evidence.CanCarryADurableAnswer(Evidence.Finding.Unknown()));
            Assert.False(Evidence.CanCarryADurableAnswer(
                new Evidence.Finding(Verdict.Unknown, true, 3, string.Empty)));
        }

        /// <summary>
        /// The two facts the application really does observe: a radio in a
        /// list, and a computer with no SmartLink account on it. Both are read
        /// directly from the authority for them.
        /// </summary>
        [Fact]
        public void Positive_observations_may_carry_a_durable_answer()
        {
            Assert.True(Evidence.CanCarryADurableAnswer(
                new Evidence.Finding(Verdict.Registered, false, 1, Mine)));
            Assert.True(Evidence.CanCarryADurableAnswer(
                new Evidence.Finding(Verdict.NoAccount, false, 0, string.Empty)));
        }
    }

    /// <summary>
    /// The words an operator reads when SmartLink has not listed their radio,
    /// pinned as assembled sentences.
    /// </summary>
    /// <remarks>
    /// The defect was a false claim in prose, so prose is where it can come
    /// back. These assert the shape of the claim rather than the whole
    /// sentence: Noel rules the wording, and a test that pins every word would
    /// make his edits look like failures.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SmartLinkNotListedWordingTests
    {
        private static readonly string[] TheSentencesAnOperatorReads =
        {
            "connect.smartlink.not_in_list_title",
            "connect.smartlink.not_in_list_body",
            "connect.smartlink.reach_from_away_title",
            "connect.smartlink.reach_from_away_body",
            "connect.smartlink.action_local_only",
            "settings.radio.register.not_in_account_list",
        };

        /// <summary>
        /// Nothing on this path may tell an operator their radio is not
        /// registered. We cannot know it, and on 2026-09-23 saying it cost a
        /// permanent wrong answer.
        /// </summary>
        [Fact]
        public void Nothing_tells_the_operator_the_radio_is_not_registered()
        {
            foreach (var key in TheSentencesAnOperatorReads)
            {
                string text = Lexicon.Get(key,
                    ("account", "operator@example.com"),
                    ("accountEmail", "operator@example.com"),
                    ("otherAccountsNote", ""));

                Assert.DoesNotContain("not registered", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("is not registered", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unregistered", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Both bodies name the other causes. An operator who knows their radio
        /// is registered has to be able to see, in the sentence itself, that we
        /// are describing a symptom — otherwise the only thing on offer is to
        /// disbelieve the application or disbelieve themselves.
        /// </summary>
        [Theory]
        [InlineData("connect.smartlink.not_in_list_body")]
        [InlineData("connect.smartlink.reach_from_away_body")]
        [InlineData("settings.radio.register.not_in_account_list")]
        public void The_body_admits_the_other_reasons_a_radio_leaves_the_list(string key)
        {
            string text = Lexicon.Get(key,
                ("account", "operator@example.com"),
                ("accountEmail", "operator@example.com"),
                ("otherAccountsNote", ""));

            Assert.Contains("never been registered", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                text.Contains("switched off", StringComparison.OrdinalIgnoreCase)
                || text.Contains("cannot be reached", StringComparison.OrdinalIgnoreCase),
                $"'{key}' states an absence without naming a cause other than registration.");
        }

        /// <summary>
        /// Noel, 2026-09-23: "that button says 'I only use this radio here' ...
        /// that is kind of ambiguous don't you think?" It was — "here" reads as
        /// this room, this computer or this application, and the truth is the
        /// second. The label may not lean on a word that has to be resolved
        /// from context a blind operator cannot see.
        /// </summary>
        [Fact]
        public void The_settling_button_does_not_say_here()
        {
            string label = Lexicon.Get("connect.smartlink.action_local_only");

            Assert.DoesNotContain(" here", label, StringComparison.OrdinalIgnoreCase);
        }
    }
}
