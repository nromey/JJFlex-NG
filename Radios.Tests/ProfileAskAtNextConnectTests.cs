#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// "Ask Me at the Next Connect" under Radio, Profiles on This Radio (#680,
    /// defect 2, ruled by Noel 2026-11-08): a fourth answer that returns the
    /// profile question to unanswered, with a small saved marker so the next
    /// connect genuinely asks whoever owns the radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question is already asked at every connect of a radio that is
    /// unanswered, whoever owns it (<c>PlanConnect</c>'s NotAnswered branch).
    /// The one thing that kept it from being asked on the operator's own radio
    /// is the automatic pre-answer for a known radio of theirs with a connection
    /// history. So the marker has one reader, <c>PreAnswerForKnownRadio</c>, and
    /// nothing in the connect plan, the ownership ruling or the shared-state
    /// gates changes.
    /// </para>
    /// <para>
    /// The discriminating test is the operator's own radio with a history and
    /// the marker set: no pre-answer, and the question raised. A radio marked
    /// someone else's already plans nothing and asks with or without the
    /// marker; that test is kept as a guard on the harm-others gate.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ProfileAskAtNextConnectTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ProfileAskAtNextConnectTests));
        private const string Serial = "0638-0000-8600-0680";

        public void Dispose() => _scope.Dispose();

        private static ProfileSituation Situation(ProfileGuestIntent intent, RadioOwnership ownership)
        {
            var s = new ProfileSituation
            {
                Intent = intent,
                Ownership = ownership,
                ChangeNothingArmed = false,
                OnlyStation = true,
                Connected = true,
                StationPresent = true,
            };
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                s.Types.Add(new ProfileTypeState
                {
                    ProfileType = t,
                    Reported = true,
                    Names = new[] { "Default", "N0CALL" }.ToList(),
                    Selection = "Default",
                    Wanted = "N0CALL",
                });
            }
            return s;
        }

        // ---- the pure decision ----

        [Fact]
        public void The_marker_stops_the_pre_answer_on_the_operators_own_known_radio()
        {
            Assert.Null(ProfileStewardship.PreAnswerForKnownRadio(
                RadioOwnership.Mine, hasConnectedBefore: true, ProfileGuestIntent.NotAnswered,
                holdArmed: false, askAtNextConnect: true));
        }

        [Fact]
        public void Without_the_marker_the_known_radio_is_still_pre_answered()
        {
            // Positive control: the same radio, marker clear, is pre-answered
            // as it always was, so the test above is about the marker.
            Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, ProfileStewardship.PreAnswerForKnownRadio(
                RadioOwnership.Mine, hasConnectedBefore: true, ProfileGuestIntent.NotAnswered,
                holdArmed: false, askAtNextConnect: false));
        }

        // ---- end to end through the saved config ----

        [Fact]
        public void Choosing_the_row_on_a_mine_radio_with_history_asks_at_the_next_connect_and_loads_nothing()
        {
            Assert.True(RadioConfig.RecordOwnership(Serial, RadioOwnership.Mine));
            Assert.True(RadioConfig.RecordProfileIntent(Serial, ProfileGuestIntent.LoadMineAndPutBack));

            Assert.True(RadioConfig.RecordAskProfileQuestionAtNextConnect(Serial));
            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(Serial));
            Assert.True(RadioConfig.ProfileAskAtNextConnectOf(Serial));

            // The next connect, as MigrateProfileIntentForKnownRadio reads it.
            var pre = ProfileStewardship.PreAnswerForKnownRadio(
                RadioConfig.OwnershipOf(Serial), hasConnectedBefore: true,
                RadioConfig.ProfileIntentOf(Serial), holdArmed: false,
                askAtNextConnect: RadioConfig.ProfileAskAtNextConnectOf(Serial));
            Assert.Null(pre);

            var plan = ProfileStewardship.PlanConnect(
                Situation(RadioConfig.ProfileIntentOf(Serial), RadioConfig.OwnershipOf(Serial)));
            Assert.True(plan.AskWhoseRadioThisIs);
            Assert.DoesNotContain(plan.Actions, a => a.Kind == ProfileActionKind.LoadOurs);
        }

        [Fact]
        public void The_row_sets_the_marker_on_a_radio_already_unanswered()
        {
            // RecordProfileIntent writes nothing when the intent does not
            // change, so it could not carry the marker here.
            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(Serial));
            Assert.False(RadioConfig.ProfileAskAtNextConnectOf(Serial));
            Assert.True(RadioConfig.RecordAskProfileQuestionAtNextConnect(Serial));
            Assert.True(RadioConfig.ProfileAskAtNextConnectOf(Serial));
            Assert.True(RadioConfig.LoadForRadio(Serial).ProfileAskAtNextConnect);
        }

        // ---- every writer of an answer clears it ----

        [Theory]
        [InlineData(ProfileGuestIntent.LeaveAlone)]
        [InlineData(ProfileGuestIntent.LoadMineAndPutBack)]
        [InlineData(ProfileGuestIntent.UseMyTransmitAudio)]
        public void The_menus_answer_clears_the_marker(ProfileGuestIntent answer)
        {
            Assert.True(RadioConfig.RecordAskProfileQuestionAtNextConnect(Serial));
            Assert.True(RadioConfig.RecordProfileIntent(Serial, answer));
            Assert.False(RadioConfig.ProfileAskAtNextConnectOf(Serial));
            Assert.False(RadioConfig.LoadForRadio(Serial).ProfileAskAtNextConnect);
        }

        [Fact]
        public void Choosing_a_transmit_audio_profile_clears_the_marker()
        {
            Assert.True(RadioConfig.RecordAskProfileQuestionAtNextConnect(Serial));
            Assert.True(RadioConfig.RecordLocalTransmitAudioChoice(Serial, "Studio"));
            Assert.Equal(ProfileGuestIntent.UseMyTransmitAudio, RadioConfig.ProfileIntentOf(Serial));
            Assert.False(RadioConfig.LoadForRadio(Serial).ProfileAskAtNextConnect);
        }

        [Fact]
        public void The_automatic_pre_answer_clears_the_marker()
        {
            Assert.True(RadioConfig.RecordAskProfileQuestionAtNextConnect(Serial));
            Assert.True(RadioConfig.RecordPreAnsweredProfileIntent(Serial, ProfileGuestIntent.LoadMineAndPutBack));
            Assert.False(RadioConfig.LoadForRadio(Serial).ProfileAskAtNextConnect);
        }

        [Fact]
        public void The_connect_time_ownership_answer_clears_the_marker()
        {
            // RadioOwnershipDialog.AskForConnect writes the intent directly
            // through RadioConfig.Update, as this does.
            Assert.True(RadioConfig.RecordAskProfileQuestionAtNextConnect(Serial));
            Assert.True(RadioConfig.Update(Serial, fresh => {
                fresh.Ownership = RadioOwnership.Mine;
                fresh.ProfileIntent = ProfileGuestIntent.LoadMineAndPutBack;
            }));
            Assert.False(RadioConfig.LoadForRadio(Serial).ProfileAskAtNextConnect);
            Assert.False(RadioConfig.ProfileAskAtNextConnectOf(Serial));
        }

        [Fact]
        public void A_marker_left_beside_an_answer_means_nothing()
        {
            // However it got there, a marker beside a real answer is not read.
            Assert.True(RadioConfig.RecordProfileIntent(Serial, ProfileGuestIntent.LeaveAlone));
            Assert.True(RadioConfig.Update(Serial, cfg => { cfg.ProfileAskAtNextConnect = true; }));
            Assert.True(RadioConfig.LoadForRadio(Serial).ProfileAskAtNextConnect);
            Assert.False(RadioConfig.ProfileAskAtNextConnectOf(Serial));
        }

        // ---- the harm-others guard ----

        [Theory]
        [InlineData(RadioOwnership.SomeoneElses)]
        [InlineData(RadioOwnership.Unset)]
        public void A_radio_that_is_not_the_operators_with_the_marker_plans_nothing_and_asks(RadioOwnership ownership)
        {
            // Passes without the fix: it guards that the marker never becomes
            // a way to load, select, create or write on such a radio.
            Assert.True(RadioConfig.RecordOwnership(Serial, ownership) || ownership == RadioOwnership.Unset);
            Assert.True(RadioConfig.RecordAskProfileQuestionAtNextConnect(Serial));
            Assert.Null(ProfileStewardship.PreAnswerForKnownRadio(
                RadioConfig.OwnershipOf(Serial), hasConnectedBefore: true,
                RadioConfig.ProfileIntentOf(Serial), holdArmed: false,
                askAtNextConnect: RadioConfig.ProfileAskAtNextConnectOf(Serial)));

            var plan = ProfileStewardship.PlanConnect(
                Situation(RadioConfig.ProfileIntentOf(Serial), RadioConfig.OwnershipOf(Serial)));
            Assert.Empty(plan.Actions);
            Assert.True(plan.AskWhoseRadioThisIs);
        }

        [Fact]
        public void The_hold_still_outranks_the_question()
        {
            // The change-nothing hold stays ahead: with it armed the plan
            // changes nothing, marker or not.
            var s = Situation(ProfileGuestIntent.NotAnswered, RadioOwnership.Mine);
            s.ChangeNothingArmed = true;
            var plan = ProfileStewardship.PlanConnect(s);
            Assert.Empty(plan.Actions);
        }

        // ---- wiring, read from source ----

        [Fact]
        public void The_connect_reads_the_marker_into_the_pre_answer()
        {
            string src = Source("Radios", "FlexBase.Profiles.cs");
            int at = src.IndexOf("private bool MigrateProfileIntentForKnownRadio()", StringComparison.Ordinal);
            Assert.True(at >= 0, "the migration has moved; re-point this read");
            string body = src.Substring(at, src.IndexOf("return true;", at, StringComparison.Ordinal) - at);
            Assert.Contains("RadioConfig.ProfileAskAtNextConnectOf(serial)", body, StringComparison.Ordinal);
            Assert.Matches(@"PreAnswerForKnownRadio\(\s*ownership,\s*connectedBefore,\s*current,\s*ChangeNothingActive,\s*askAtNextConnect\)", body);
        }

        [Fact]
        public void The_menu_row_sets_the_marker_and_says_its_own_receipt()
        {
            string src = Source("JJFlexWpf", "NativeMenuBar.cs");
            Assert.Matches(@"AddRadioChecked\(profilesSub,\s*""Ask Me at the Next Connect"",\s*\(\)\s*=>\s*SetGuestProfileIntent\(Radios\.ProfileGuestIntent\.NotAnswered\)", src);
            int at = src.IndexOf("private void SetGuestProfileIntent(", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = src.Substring(at, src.IndexOf("RebuildCurrentMenu();", at, StringComparison.Ordinal) - at);
            Assert.Contains("Rig.AskProfileQuestionAtNextConnect();", body, StringComparison.Ordinal);
            Assert.Contains("\"settings.profile_guest.ask_next_connect\"", body, StringComparison.Ordinal);
            // The default arm's "leave this radio's profiles alone" is untrue
            // for this row, so the row has its own case ahead of it.
            Assert.True(body.IndexOf("case Radios.ProfileGuestIntent.NotAnswered:", StringComparison.Ordinal)
                        < body.IndexOf("default:", StringComparison.Ordinal));
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }
    }
}
