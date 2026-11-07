#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Repair round 3 (Codex's second re-check of #798 and #613): the saved
    /// accounts belong to the reading the plan is made from; a list that
    /// arrives during a call is that call's answer on every path, the replay
    /// included; and a picture that never steadies decides nothing. Every
    /// schedule waits on the barrier it names, never on a fixed timer.
    /// </summary>
    public sealed partial class SmartLinkSignInPlanTests
    {
        // ------------------------------------------------------------------
        // Codex B2-R2: an account saved while the accounts are read
        // ------------------------------------------------------------------

        /// <summary>
        /// Only A is saved and A has answered empty. Between the two readings
        /// B is saved, with no session and no answer. "Not on any of your
        /// accounts" would be false: B has not been asked. Until round 3 the
        /// saved accounts were read once, outside the picture, so the two
        /// readings matched and the verdict was said.
        /// </summary>
        [Fact]
        public void An_account_saved_while_the_accounts_are_read_blocks_the_verdict_of_absence()
        {
            Listed(Operator);
            var rig = NewRig();
            rig.ShowAccountSelector = _ => (false, null!, false);
            var saved = Saved(Operator);
            bool added = false;
            rig.AfterAccountPictureTaken = () =>
            {
                if (added) return;
                added = true;
                saved.Add(new SmartLinkAccount { Email = Owner, FriendlyName = Owner });
            };

            rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: saved);

            Assert.True(added, "the schedule never ran");
            Assert.NotEqual(ConnectFailureClass.RadioNotFound, rig.LastConnectFailureReport?.Class);
        }

        /// <summary>
        /// The control for the same guarantee: with nothing saved meanwhile,
        /// the same accounts DO give the verdict, so the test above is red for
        /// the reason it names.
        /// </summary>
        [Fact]
        public void With_nothing_saved_meanwhile_the_same_accounts_give_the_verdict()
        {
            Listed(Operator);
            var rig = NewRig();
            rig.ShowAccountSelector = _ => (false, null!, false);

            rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: Saved(Operator));

            Assert.Equal(ConnectFailureClass.RadioNotFound, rig.LastConnectFailureReport?.Class);
        }

        // ------------------------------------------------------------------
        // Codex F3: a picture that never steadies decides nothing
        // ------------------------------------------------------------------

        /// <summary>
        /// The radio's chosen account changes after every first reading, so
        /// no picture steadies in three attempts. Until round 3 the plan was
        /// still made from the last, rejected picture, and signed in as its
        /// chosen account. Now nothing is decided from it: the usual sign-in
        /// (here the selector, which declines), and no account is chosen.
        /// </summary>
        [Fact]
        public void A_picture_that_never_steadies_signs_in_as_usual_not_as_its_rejected_account()
        {
            ConnectedNotYetListed(Operator);
            ConnectedNotYetListed(Owner);
            var rig = NewRig();
            int selectorAsked = 0;
            rig.ShowAccountSelector = _ => { selectorAsked++; return (false, null!, false); };
            int flips = 0;
            rig.AfterAccountPictureTaken = () =>
            {
                flips++;
                string next = flips % 2 == 1 ? Owner : Operator;
                Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = next; }));
            };
            Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = Operator; }));
            try
            {
                rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: Saved(Operator, Owner));

                Assert.Equal(FlexBase.PictureReadAttempts, flips);
                Assert.Null(CurrentAccountField!.GetValue(rig));
            }
            finally
            {
                RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = ""; });
            }
        }

        // ------------------------------------------------------------------
        // Codex B3-R2a: a first non-empty list arriving during the call
        // ------------------------------------------------------------------

        /// <summary>
        /// A held account (earlier in the coordinator's order) is replayed;
        /// while it is, the asked account's FIRST list, non-empty and not
        /// naming the radio, is accepted and parked before it is forwarded.
        /// The replay then reached the asked account and replayed that list
        /// as a held copy, which satisfied the call before the push reached
        /// its latch: the verdict came back Unknown. The call must wait for
        /// the push and hear the server's answer.
        /// </summary>
        [Fact]
        public void A_first_list_with_radios_arriving_during_the_call_is_the_calls_answer_not_a_replayed_copy()
        {
            Listed(Third, SomeOtherRadio);                              // replayed first
            var (session, wan) = ConnectedNotYetListed("default-account");
            var owner = (WanSessionOwner)session;
            var release = new ManualResetEventSlim();
            var accepted = new ManualResetEventSlim();
            var rig = NewRig();

            bool parked = false;
            rig.WanIntakeStageReached = stage =>
            {
                if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken || parked) return;
                parked = true;
                owner.BeforeListForwarded = () =>
                {
                    owner.BeforeListForwarded = null;
                    accepted.Set();
                    release.Wait(10000);
                };
                Task.Run(() => wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) }));
                Assert.True(accepted.Wait(5000), "the account's list was never accepted");
            };

            var query = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, "default-account", "test-jwt"));
            bool reached = accepted.Wait(8000);
            bool finishedWhileParked = query.Wait(600);
            release.Set();
            Assert.True(query.Wait(8000));

            Assert.True(reached, "the schedule never ran: the list was never accepted during the call");
            Assert.False(finishedWhileParked, "the call finished on a replayed copy while its own answer was on its way");
            Assert.True(query.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, query.Result.Verdict);
        }

        // ------------------------------------------------------------------
        // Codex B3-R2b: identity and emptiness from ONE reading
        // ------------------------------------------------------------------

        private static SessionRadioListSnapshot Snap(long generation, DateTime? received, params string[] serials) =>
            new SessionRadioListSnapshot(serials.Select(WanRadio).ToList(), received,
                SessionConnected: true, ArrivedOnTheLiveConnection: received.HasValue, generation, "s-1")
            // The list's revision: one per accepted list (round 4). Here each
            // distinct arrival is a distinct list.
            { ListRevision = received.HasValue ? generation * 100 + received.Value.Second : 0 };

        /// <summary>
        /// The empty-list decision is two readings compared as values: the
        /// one the call found, and the one it decides from. A reconnect's
        /// empty list is not the list found at the start, however empty both
        /// are.
        /// </summary>
        [Fact]
        public void Only_the_very_list_found_at_the_start_is_found_at_the_start()
        {
            var t = new DateTime(2026, 11, 6, 5, 0, 0, DateTimeKind.Utc);
            var start = Snap(1, t);
            Assert.True(FlexBase.TheListWasFoundAtStart(start, Snap(1, t)));
            Assert.False(FlexBase.TheListWasFoundAtStart(start, Snap(2, t.AddSeconds(1))));   // a reconnect's own empty list
            Assert.False(FlexBase.TheListWasFoundAtStart(start, Snap(1, t.AddSeconds(1))));   // a newer list, same connection
            Assert.False(FlexBase.TheListWasFoundAtStart(start, Snap(2, null)));              // reconnected, nothing yet
            Assert.False(FlexBase.TheListWasFoundAtStart(Snap(1, null), Snap(1, null)));      // no answer at the start
            Assert.False(FlexBase.TheListWasFoundAtStart(start, start with { SessionId = "s-2" }));
            Assert.False(FlexBase.TheListWasFoundAtStart(start, start with { SessionConnected = false }));
        }

        /// <summary>
        /// And the connect takes exactly two readings of its account's list:
        /// the one it found, under the lock that opens its latch, and the one
        /// both shortcuts decide from. No helper reads the session again.
        /// </summary>
        [Fact]
        public void The_connect_decides_its_shortcuts_from_one_reading()
        {
            string wan = string.Join("\n", System.IO.Directory.GetFiles(
                System.IO.Path.Combine(DiscoveryMergeWritesTests.RepoRoot(), "Radios"), "FlexBase.Wan.cs")
                .Select(System.IO.File.ReadAllText));
            string connect = Body(wan, "out SmartLinkRegistrationEvidence.AccountList? serverPush, bool activateSession = true)");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(connect, @"session\.RadioListSnapshot").Count);
            Assert.Contains("var listNow = session.RadioListSnapshot;", connect, StringComparison.Ordinal);
            Assert.Contains("OwnRowsMayAnswerTheConnect(listNow, accountEmail, sw)", connect, StringComparison.Ordinal);
            Assert.Contains("TheLiveListAnsweredEmpty(listNow, accountEmail, sw)", connect, StringComparison.Ordinal);
            Assert.Contains("ReplayHeldListsIntoTheIntake(accountEmail, sessionWasAlreadyConnected, sw, listAtStart)", connect, StringComparison.Ordinal);
            // The start reading is taken inside the block that opens the latch.
            int opened = connect.IndexOf("_openLatches.Add(latch);", StringComparison.Ordinal);
            int read = connect.IndexOf("listAtStart = session.RadioListSnapshot;", StringComparison.Ordinal);
            int closes = connect.IndexOf('}', Math.Max(opened, 0));
            Assert.True(opened > 0 && read > opened && read < closes, "the start reading is not taken where the latch opens");
        }
    }
}
