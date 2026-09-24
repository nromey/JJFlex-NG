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
    /// as <c>status=Available</c>, in the trace from two days earlier, and one
    /// empty list cannot say which of four causes removed it. Worse, the same
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

        /// <summary>A list the server pushed during the call being judged.</summary>
        private static Evidence.AccountList Pushed(string account, params string[] serials) =>
            new(account, serials, Evidence.ListSource.ServerPushThisCall);

        /// <summary>The latest list of a held session that is connected now.</summary>
        private static Evidence.AccountList Held(string account, params string[] serials) =>
            new(account, serials, Evidence.ListSource.HeldByAConnectedSession);

        /// <summary>A list held by a session that has since disconnected.</summary>
        private static Evidence.AccountList History(string account, params string[] serials) =>
            new(account, serials, Evidence.ListSource.HeldByADisconnectedSession);

        private static IReadOnlyCollection<Evidence.AccountList> Lists(params Evidence.AccountList[] lists) =>
            lists.ToList();

        private static Evidence.Finding Ask(
            IReadOnlyCollection<Evidence.AccountList> lists,
            string serial = Serial,
            bool overSmartLink = false,
            bool accountInHand = true,
            bool anySaved = true) =>
            Evidence.Judge(serial, overSmartLink, anySaved, accountInHand, lists);

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
        // Positive evidence answers for the present only when it is current
        // ------------------------------------------------------------------

        /// <summary>
        /// The latest list of a session that is connected now is the server's
        /// current word, so a serial in it answers Registered without a round
        /// trip.
        /// </summary>
        [Fact]
        public void A_serial_in_a_connected_sessions_list_is_a_current_answer()
        {
            var f = Ask(Lists(Held(Mine, Serial)));

            Assert.Equal(Verdict.Registered, f.Verdict);
            Assert.Equal(Mine, f.ListedUnderAccount);
        }

        /// <summary>
        /// Replaces Track L's <c>A_serial_found_in_a_stale_list_is_still_proof</c>,
        /// which asserted a present-tense verdict from a list of any age. A
        /// listing proves the radio was registered to that account WHEN it was
        /// sent; it can be unregistered since, and a new registration replaces
        /// an old account's (#619). Every caller reads Registered as "now", so
        /// history is set aside: not proof, and not an account consulted.
        /// </summary>
        [Fact]
        public void A_serial_found_only_in_a_disconnected_sessions_list_is_history_not_a_current_answer()
        {
            var f = Ask(Lists(History(Mine, Serial)));

            Assert.Equal(Verdict.Unknown, f.Verdict);
            Assert.Equal(string.Empty, f.ListedUnderAccount);
            Assert.Equal(0, f.AccountsConsulted);
        }

        /// <summary>
        /// History does not become proof by sitting beside a live answer: a
        /// live push that omits the radio still wins over an old listing.
        /// </summary>
        [Fact]
        public void An_old_listing_does_not_outvote_a_live_list_that_omits_the_radio()
        {
            var f = Ask(Lists(Pushed(Mine), History(Mine, Serial)));

            Assert.Equal(Verdict.NotInAccountList, f.Verdict);
            Assert.Equal(1, f.AccountsConsulted);
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
                Pushed(Mine),
                Held(Other, Serial)));

            Assert.Equal(Verdict.Registered, f.Verdict);
            Assert.Equal(Other, f.ListedUnderAccount);
            Assert.Equal(2, f.AccountsConsulted);
        }

        [Fact]
        public void A_serial_matches_whatever_case_the_server_sent_it_in()
        {
            var f = Ask(Lists(Pushed(Mine, Serial.ToLowerInvariant())),
                serial: Serial.ToUpperInvariant());

            Assert.Equal(Verdict.Registered, f.Verdict);
        }

        /// <summary>
        /// Arriving over SmartLink IS the proof, and it outranks a list that
        /// disagrees — a list can lag, a live connection cannot.
        /// </summary>
        [Fact]
        public void Arriving_over_smartlink_outranks_an_empty_list()
        {
            var f = Ask(Lists(Pushed(Mine)), overSmartLink: true);

            Assert.Equal(Verdict.Registered, f.Verdict);
        }

        // ------------------------------------------------------------------
        // Counting accounts, not lists
        // ------------------------------------------------------------------

        /// <summary>
        /// The caller prints "only the signed-in account was asked" when one
        /// account was consulted, and suppresses it when more were. Track L
        /// counted LISTS, and one account routinely supplies two — the push
        /// captured during the call and the held session carrying the same
        /// list — so a single account read as two and the caveat vanished
        /// exactly when it was true.
        /// </summary>
        [Fact]
        public void One_account_heard_twice_is_one_account_consulted()
        {
            var f = Ask(Lists(Pushed(Mine), Held(Mine), Held(Mine.ToUpperInvariant())));

            Assert.Equal(1, f.AccountsConsulted);
        }

        [Fact]
        public void Two_accounts_are_two_whatever_the_number_of_lists()
        {
            var f = Ask(Lists(Pushed(Mine), Held(Mine), Held(Other)));

            Assert.Equal(2, f.AccountsConsulted);
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
            var f = Ask(Lists(Held(Mine, "9999-0000-6400-0001")));

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
                Lists(Held(Mine)),
                Lists(Held(Mine, "9999-0000-6400-0001")),
                Lists(Held(Mine), Held(Other)),
            };

            foreach (var shape in shapes)
                Assert.NotEqual(Verdict.NotInAccountList, Ask(shape).Verdict);
        }

        /// <summary>
        /// When a server DID answer and the radio is in nobody's list, the
        /// verdict is the narrow one: not in the list. It says nothing about
        /// registration, and the flag says the observation was live.
        /// </summary>
        [Fact]
        public void A_live_empty_answer_reports_absence_from_the_list_and_nothing_more()
        {
            var f = Ask(Lists(Pushed(Mine)));

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
                Ask(Lists(Pushed(Mine)), serial: serial).Verdict);
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
                Ask(Lists(), accountInHand: false, anySaved: false).Verdict);

            Assert.Equal(Verdict.Unknown,
                Ask(Lists(), accountInHand: false, anySaved: true).Verdict);
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
    /// What the query's two WPF callers do with a finding, read from source
    /// because this project cannot construct either window: the connect
    /// advisory's write refuses a premise that cannot carry an answer, and
    /// Radio Setup names the account that listed the radio (#619 for the
    /// first; the second is from Sol's review of Track L, 2026-09-23).
    /// </summary>
    public sealed class SmartLinkRegistrationCallerSourceTests
    {
        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }

        /// <summary>
        /// Sol found the gate evaluated before the dialog and nothing at the
        /// write: <c>RecordSmartLinkIntent</c> checked no premise, so a caller
        /// added later could record a durable answer on a premise nobody had
        /// checked. The recording method now takes the finding its prompt was
        /// built on and refuses before it touches the config.
        /// </summary>
        [Fact]
        public void Recording_an_intent_checks_the_premise_before_it_writes()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));

            int decl = src.IndexOf("private void RecordSmartLinkIntent(", StringComparison.Ordinal);
            Assert.True(decl >= 0,
                "RecordSmartLinkIntent is not where this test looks for it, so nothing below would be checking it.");

            int write = src.IndexOf(".SaveForRadio(serial)", decl, StringComparison.Ordinal);
            Assert.True(write > decl,
                "RecordSmartLinkIntent no longer saves where this test expects, so the ordering check below would be vacuous.");

            string head = src.Substring(decl, write - decl);

            // The premise is a required parameter, so no caller can leave it out.
            Assert.Contains("SmartLinkRegistrationEvidence.Finding premise", head, StringComparison.Ordinal);
            // And it is checked, with a refusal, before the save.
            int check = head.IndexOf("CanCarryADurableAnswer(premise)", StringComparison.Ordinal);
            Assert.True(check >= 0, "RecordSmartLinkIntent writes without checking its premise (#619).");
            Assert.True(head.IndexOf("return;", check, StringComparison.Ordinal) > check,
                "RecordSmartLinkIntent checks its premise but does not refuse the write when it fails.");
        }

        /// <summary>
        /// Radio Setup's step-2 status said a radio found under ANOTHER of the
        /// operator's accounts was registered to the signed-in one, and cached
        /// only the verdict — so the listing account was gone and the wrong
        /// attribution lasted until the dialog reopened (Sol's review of Track
        /// L, 2026-09-23). The whole
        /// finding is cached now, and the Registered line names the account
        /// that listed the radio.
        /// </summary>
        [Fact]
        public void Radio_setup_names_the_account_that_listed_the_radio()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs"));

            Assert.Contains("private SmartLinkRegistrationEvidence.Finding? _registrationQueryResult;",
                src, StringComparison.Ordinal);

            int line = src.IndexOf("\"settings.radio.register.already_registered\"", StringComparison.Ordinal);
            Assert.True(line >= 0,
                "The already-registered status line is not where this test looks for it, so the check below would be vacuous.");

            // The value interpolated into that line, and only that line.
            int argEnd = src.IndexOf(")),", line, StringComparison.Ordinal);
            string args = src.Substring(line, argEnd - line);
            Assert.DoesNotContain("regCheck.AccountEmail", args, StringComparison.Ordinal);
            Assert.Contains("listedUnder", args, StringComparison.Ordinal);

            // And listedUnder is bound from the finding's listing account.
            Assert.Contains("ListedUnderAccount: { Length: > 0 } listedUnder", src, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What a held session contributes to the registration query, read from a
    /// real <see cref="Radios.SmartLink.WanSessionOwner"/> behind a mock server.
    /// </summary>
    public sealed class SmartLinkHeldListTests
    {
        private static (Radios.SmartLink.WanSessionOwner owner, MockWanServer wan) Build(string account)
        {
            var wan = new MockWanServer();
            var owner = new Radios.SmartLink.WanSessionOwner(
                Guid.NewGuid().ToString("N").Substring(0, 12), account, wan,
                new Radios.SmartLink.DirectPassthroughSink(), new[] { 50, 50, 50 });
            return (owner, wan);
        }

        private static void WaitUntil(Func<bool> condition, string because) =>
            Assert.True(System.Threading.SpinWait.SpinUntil(condition, 5000), because);

        /// <summary>
        /// A session starts life holding an EMPTY radio array, before any
        /// server list has arrived. That array is the owner's initial value,
        /// not an answer, and counting it made "we consulted this account"
        /// true of an account that had said nothing.
        /// </summary>
        [Fact]
        public void A_session_that_has_heard_nothing_contributes_nothing()
        {
            var (owner, wan) = Build("friend@example.com");
            try
            {
                owner.Connect();
                WaitUntil(() => owner.IsConnected, "the mock session never reported connected");

                // The trap, stated: an empty array is already sitting there.
                Assert.Empty(owner.AvailableRadios);
                Assert.Null(owner.LastRadioListUtc);

                Assert.Empty(Evidence.HeldLists(new[] { owner }));

                // Positive control: the same session, once the server has
                // spoken — even to say "nothing" — does contribute.
                wan.RaiseWanRadioRadioListReceived(Array.Empty<Flex.Smoothlake.FlexLib.Radio>());

                var lists = Evidence.HeldLists(new[] { owner });
                Assert.Single(lists);
                Assert.Empty(lists.First().Serials);
                Assert.Equal(Evidence.ListSource.HeldByAConnectedSession, lists.First().Source);
            }
            finally
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// A session keeps its last list after it disconnects. That list is
        /// history — the radio was listed then — and must be labelled so,
        /// because every caller of the query reads Registered as "now" (#619).
        /// </summary>
        [Fact]
        public void A_disconnected_sessions_list_is_labelled_history()
        {
            var (owner, wan) = Build("friend@example.com");
            try
            {
                owner.Connect();
                WaitUntil(() => owner.IsConnected, "the mock session never reported connected");
                wan.RaiseWanRadioRadioListReceived(Array.Empty<Flex.Smoothlake.FlexLib.Radio>());
                Assert.Equal(Evidence.ListSource.HeldByAConnectedSession,
                    Evidence.HeldLists(new[] { owner }).Single().Source);

                owner.Disconnect();
                WaitUntil(() => !owner.IsConnected, "the mock session never reported disconnected");

                // The list is still there — the trap is that it looks current.
                Assert.NotNull(owner.LastRadioListUtc);
                Assert.Equal(Evidence.ListSource.HeldByADisconnectedSession,
                    Evidence.HeldLists(new[] { owner }).Single().Source);
            }
            finally
            {
                owner.Dispose();
            }
        }

        private const string Account = "friend@example.com";
        private const string Listed = "4925-1213-8600-6245";

        /// <summary>
        /// Connect, hear a list carrying <see cref="Listed"/>, then lose the
        /// connection and let the monitor dial a new one — without the server
        /// sending the new connection anything. This is the moment Sol's
        /// review of Track L2 named: the session is connected again and still
        /// holds the old connection's list.
        /// </summary>
        private static void ReconnectWithoutANewList(
            Radios.SmartLink.WanSessionOwner owner, MockWanServer wan)
        {
            owner.Connect();
            WaitUntil(() => owner.IsConnected, "the mock session never reported connected");
            wan.RaiseWanRadioRadioListReceived(new[] { SmartLinkRegistrationReplayTests.WanRadio(Listed) });

            // Before the drop the list IS current. Without this, the assertions
            // after the reconnect could pass because the harness never
            // produces a current label at all.
            Assert.Equal(Evidence.ListSource.HeldByAConnectedSession,
                Evidence.HeldLists(new[] { owner }).Single().Source);

            int dialsBefore = wan.ConnectCallCount;
            wan.ForceIsConnected(false);
            WaitUntil(() => wan.ConnectCallCount > dialsBefore && owner.IsConnected,
                "the session never dialled a new connection after the drop");
        }

        /// <summary>
        /// The reconnect Sol found in Track L2 (#619). After a drop and a
        /// reconnect, before the new connection's first list, the session is
        /// connected and still carries the previous connection's list with a
        /// non-null timestamp. Track L2 read that as a current answer, so the
        /// query could say Registered — possibly naming an account the radio
        /// has since left — and count the account as consulted, on nothing the
        /// live connection had said.
        /// </summary>
        [Fact]
        public void A_list_carried_across_a_reconnect_is_history_until_the_new_connection_sends_one()
        {
            var (owner, wan) = Build(Account);
            try
            {
                ReconnectWithoutANewList(owner, wan);

                // The trap, stated: every older signal says "current".
                Assert.True(owner.IsConnected);
                Assert.NotNull(owner.LastRadioListUtc);
                Assert.Contains(owner.AvailableRadios, r => r.Serial == Listed);

                var lists = Evidence.HeldLists(new[] { owner });
                Assert.Equal(Evidence.ListSource.HeldFromAnEarlierConnection, lists.Single().Source);

                var finding = Evidence.Judge(Listed, false, true, true, lists);
                Assert.NotEqual(Verdict.Registered, finding.Verdict);
                Assert.Equal(string.Empty, finding.ListedUnderAccount);
                Assert.Equal(0, finding.AccountsConsulted);
            }
            finally
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// The partner: the same reconnect, then the new connection's own list
        /// arrives, and the answer is current again. Without this, the test
        /// above could be passing because a reconnected session can never
        /// answer at all.
        /// </summary>
        [Fact]
        public void The_new_connections_own_list_answers_again()
        {
            var (owner, wan) = Build(Account);
            try
            {
                ReconnectWithoutANewList(owner, wan);
                wan.RaiseWanRadioRadioListReceived(new[] { SmartLinkRegistrationReplayTests.WanRadio(Listed) });

                var lists = Evidence.HeldLists(new[] { owner });
                Assert.Equal(Evidence.ListSource.HeldByAConnectedSession, lists.Single().Source);

                var finding = Evidence.Judge(Listed, false, true, true, lists);
                Assert.Equal(Verdict.Registered, finding.Verdict);
                Assert.Equal(Account, finding.ListedUnderAccount);
                Assert.Equal(1, finding.AccountsConsulted);
            }
            finally
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// Why the fix stamps the list instead of emptying it at the boundary.
        /// The session's post-drop diagnostic probe fires on the way INTO
        /// Reconnecting and reads the held list to choose which radio to test;
        /// a list emptied when the connection drops would leave it nothing,
        /// and the probe would skip silently. This pins that consumer, so a
        /// later "simpler" fix that clears the list shows up here rather than
        /// as a quieter reconnect announcement nobody notices.
        /// </summary>
        [Fact]
        public void A_drop_still_leaves_the_probe_a_radio_to_test()
        {
            var (owner, wan) = Build(Account);
            try
            {
                ReconnectWithoutANewList(owner, wan);
                WaitUntil(() => wan.SendTestConnectionCallCount > 0,
                    "the post-drop diagnostic probe never ran, so it found no radio in the held list");
                Assert.Equal(Listed, wan.LastSendTestConnectionSerial);
            }
            finally
            {
                owner.Dispose();
            }
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
