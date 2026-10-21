using System;
using System.Collections.Generic;
using System.Reflection;
using Radios.StationConnect;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junkbox #777, round two: every refusal path driven through the REAL
    /// <see cref="FlexBase"/> funnels and the vendored Radio on its fake
    /// transport, the way Codex's <see cref="StationProfileOwnershipReviewTests"/>
    /// do — what went out on the wire, what came back, and what the operator
    /// would have heard. Nothing here reads source text. No socket, no window,
    /// no radio; speech is suppressed by the fixture and read back through
    /// <c>FlexBase.LastGlobalProfileWriteRefusal</c>.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class StationProfileOwnershipExecutionTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(StationProfileOwnershipExecutionTests));
        private const string Serial = "1234-5678-9012-3456";
        private const string Name = "owner-station";
        public void Dispose() => _scope.Dispose();

        private static void Declare(RadioOwnership ownership) =>
            Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));

        private const string SaveAction = "settings.guard.action.profile_save";
        private const string DeleteAction = "settings.guard.action.profile_delete";

        private static string Expected(RadioOwnership ownership, string actionKey)
        {
            string key = ownership == RadioOwnership.SomeoneElses
                ? "settings.guard.profile_write_not_our_radio"
                : "settings.guard.profile_write_ownership_unknown";
            return Lexicon.Get(key, ("action", Lexicon.Get(actionKey)));
        }

        // ------------------------------------------------------------------
        // Save, through SaveProfileGuarded
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void ANamedGlobalSaveOnARadioThatIsNotOursIsRefusedBeforeAnythingIsSent(RadioOwnership ownership)
        {
            Declare(ownership);
            using var fixture = new RigOnVendorRadio(Serial);

            var outcome = fixture.Rig.SaveProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), immediately: true);

            Assert.Equal(GuardedOutcome.Refused, outcome);
            Assert.Empty(fixture.Vendor.Transport.Commands);
            // What NVDA would have spoken, assembled: the verb is "save the
            // profile", true whether the name exists on the radio or not.
            string heard = fixture.Rig.LastGlobalProfileWriteRefusal;
            Assert.Equal(Expected(ownership, SaveAction), heard);
            Assert.Contains("did not save the profile.", heard);
            Assert.Contains("Settings, under Per-radio settings", heard);
            Assert.DoesNotContain("replace", heard);
            Assert.DoesNotContain("{action}", heard);
        }

        [Fact]
        public void ANamedGlobalSaveOnOurOwnRadioGoesOutAndIsDone()
        {
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);

            // Before the main loop the queue runs the delegate at once, so the
            // immediate wait is satisfied and the command is on the wire.
            var outcome = fixture.Rig.SaveProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), immediately: true);

            Assert.Equal(GuardedOutcome.Done, outcome);
            Assert.Contains("profile global save \"" + Name + "\"", fixture.Vendor.Transport.Commands);
            Assert.Null(fixture.Rig.LastGlobalProfileWriteRefusal);
        }

        [Fact]
        public void AnImmediateSaveWhoseCommandNeverLeavesTheQueueIsFailedNotDone()
        {
            // Codex B3: the 3000 ms wait's result was ignored and true came
            // back for a save still sitting in the queue. The queue is held
            // undrained by pretending the main loop owns it.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            using var held = new HeldQueue(fixture.Rig);

            var outcome = fixture.Rig.SaveProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), immediately: true);

            Assert.Equal(GuardedOutcome.Failed, outcome);
            Assert.Empty(fixture.Vendor.Transport.Commands);
            Assert.False(fixture.Rig.SaveProfile(new Profile_t(Name, ProfileTypes.global, false), immediately: true),
                "the bool wrapper must not read an unsent save as true either");
        }

        [Fact]
        public void ATransmitOrMicrophoneProfileIsSkippedBySaveNotFailed()
        {
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);

            Assert.Equal(GuardedOutcome.Skipped, fixture.Rig.SaveProfileGuarded(new Profile_t("mic-1", ProfileTypes.mic, false)));
            Assert.Equal(GuardedOutcome.Skipped, fixture.Rig.SaveProfileGuarded(new Profile_t("tx-1", ProfileTypes.tx, false)));
            Assert.Empty(fixture.Vendor.Transport.Commands);
        }

        // ------------------------------------------------------------------
        // Delete, through DeleteProfileGuarded
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void AGlobalDeleteOnARadioThatIsNotOursIsRefusedBeforeAnythingIsQueued(RadioOwnership ownership)
        {
            // Codex B1: with the hold off (its default) a guest could delete
            // the owner's global profile. The queue is held so that, were the
            // gate missing, the delete would sit visibly in it rather than
            // run; the refusal must come before the enqueue.
            Declare(ownership);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Vendor.Status("profile global list=" + Name + "^");
            fixture.Rig.Callouts.Profiles = new List<Profile_t>();
            using var held = new HeldQueue(fixture.Rig);

            var outcome = fixture.Rig.DeleteProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), new List<Profile_t>());

            Assert.Equal(GuardedOutcome.Refused, outcome);
            Assert.Equal(0, held.Count);
            Assert.Empty(fixture.Vendor.Transport.Commands);
            string heard = fixture.Rig.LastGlobalProfileWriteRefusal;
            Assert.Equal(Expected(ownership, DeleteAction), heard);
            Assert.Contains("did not delete the profile.", heard);
            Assert.Contains("Settings, under Per-radio settings", heard);
        }

        [Fact]
        public void AGlobalDeleteOnOurOwnRadioStillGoesOut()
        {
            // The positive control for the delete gate: Mine sends.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Vendor.Status("profile global list=" + Name + "^");
            fixture.Rig.Callouts.Profiles = new List<Profile_t>();

            // The delete is enqueued WITHOUT the run-before-the-main-loop
            // flag the save carries, so with the queue held the item waits
            // (and the method's one-second wait for the name to vanish from
            // a list the fake never updates reports Failed). The queued item
            // is then run by hand, the way the main loop would: the wire is
            // what this asserts.
            using var held = new HeldQueue(fixture.Rig);
            fixture.Rig.DeleteProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), new List<Profile_t>());

            Assert.Equal(1, held.Count);
            held.RunOne();
            Assert.Contains("profile global delete \"" + Name + "\"", fixture.Vendor.Transport.Commands);
            Assert.Null(fixture.Rig.LastGlobalProfileWriteRefusal);
        }

        // ------------------------------------------------------------------
        // The layout save, through SaveCurrentStationLayout
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void TheLayoutSaveOnARadioThatIsNotOursReturnsTheOwnershipSentenceAndSendsNothing(RadioOwnership ownership)
        {
            Declare(ownership);
            using var fixture = new RigOnVendorRadio(Serial);
            MakeSaveable(fixture);

            string blocker = fixture.Rig.SaveCurrentStationLayout();

            Assert.Equal(Expected(ownership, SaveAction), blocker);
            Assert.Empty(fixture.Vendor.Transport.Commands);
        }

        [Fact]
        public void TheLayoutSaveOnOurOwnRadioAloneGoesOutAndClearsTheDirtyFlag()
        {
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            MakeSaveable(fixture);
            SetDirty(fixture.Rig, true);

            Assert.Null(fixture.Rig.StationLayoutSaveBlocker());
            string error = fixture.Rig.SaveCurrentStationLayout();

            Assert.Null(error);
            Assert.Contains("profile global save \"" + Name + "\"", fixture.Vendor.Transport.Commands);
            Assert.False(fixture.Rig.OperatorChangedStationThisSession);
        }

        [Fact]
        public void TheLayoutSaveWhoseCommandNeverLeavesTheQueueSaysSoAndKeepsTheDirtyFlag()
        {
            // Codex B3 at the second caller: SaveCurrentStationLayout cleared
            // the dirty flag on a save that never went out, and its menu
            // callers then announced "Station setup saved".
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            MakeSaveable(fixture);
            SetDirty(fixture.Rig, true);
            using var held = new HeldQueue(fixture.Rig);

            string error = fixture.Rig.SaveCurrentStationLayout();

            Assert.Equal(Lexicon.Get("settings.station_layout.save_not_sent", ("profileName", Name)), error);
            Assert.Contains("was not saved to " + Name, error);
            Assert.Empty(fixture.Vendor.Transport.Commands);
            Assert.True(fixture.Rig.OperatorChangedStationThisSession, "an unsent save must leave the changes unsaved");
        }

        // ------------------------------------------------------------------
        // The transport drops after the press and before the dispatch
        // (round three, Codex B3 residual)
        // ------------------------------------------------------------------

        [Fact]
        public void ASaveWhoseTransportDroppedBeforeDispatchIsFailedAndSendsNothing()
        {
            // The vendor's void SaveGlobalProfile discarded SendCommand's
            // 0-for-disconnected, so this used to come back Done with zero
            // commands on the wire. Codex's StationProfileRoundTwoReviewTests
            // pins the same case; this one also reads what the operator's
            // layout save would then do with it.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            MakeSaveable(fixture);
            SetDirty(fixture.Rig, true);
            using var held = new HeldQueue(fixture.Rig);
            var save = System.Threading.Tasks.Task.Run(() => fixture.Rig.SaveCurrentStationLayout());
            Assert.True(System.Threading.SpinWait.SpinUntil(() => held.Count > 0 || save.IsCompleted, 2000));
            Assert.False(save.IsCompleted);

            DropTransport(fixture);
            held.RunOne();
            string error = save.GetAwaiter().GetResult();

            Assert.Empty(fixture.Vendor.Transport.Commands);
            Assert.Equal(Lexicon.Get("settings.station_layout.save_not_sent", ("profileName", Name)), error);
            Assert.True(fixture.Rig.OperatorChangedStationThisSession, "a save the transport refused leaves the changes unsaved");
        }

        [Fact]
        public void ADeleteWhoseTransportDroppedBeforeDispatchIsFailedNotDone()
        {
            // Delete has the same void vendor call, but it already confirms
            // by readback — the name must leave the radio's reported list
            // within a second — so a dropped delete reports Failed and its
            // caller says "Could not delete". Proven here rather than changed.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Vendor.Status("profile global list=" + Name + "^");
            fixture.Rig.Callouts.Profiles = new List<Profile_t>();
            using var held = new HeldQueue(fixture.Rig);
            var delete = System.Threading.Tasks.Task.Run(() =>
                fixture.Rig.DeleteProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), new List<Profile_t>()));
            Assert.True(System.Threading.SpinWait.SpinUntil(() => held.Count > 0 || delete.IsCompleted, 2000));
            Assert.False(delete.IsCompleted);

            DropTransport(fixture);
            held.RunOne();
            var outcome = delete.GetAwaiter().GetResult();

            Assert.Empty(fixture.Vendor.Transport.Commands);
            Assert.Equal(GuardedOutcome.Failed, outcome);
        }

        [Fact]
        public void TheRadiosReplyToASaveIsTakenWithoutDisturbingTheOutcome()
        {
            // Done is "handed to a connected transport"; the reply arrives
            // later and is only traced. A rejection must not throw on the
            // receive path and must not retroactively change anything here.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);

            var outcome = fixture.Rig.SaveProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), immediately: true);
            Assert.Equal(GuardedOutcome.Done, outcome);
            Assert.Equal("profile global save \"" + Name + "\"", fixture.Vendor.LastCommand);

            fixture.Vendor.Reply(fixture.Vendor.LastSequence, 0x50000015, "profile save failed");
            fixture.Vendor.ReplyOk(fixture.Vendor.LastSequence);
            Assert.Single(fixture.Vendor.Transport.Commands);
        }

        private static void DropTransport(RigOnVendorRadio fixture)
        {
            // The transport's own disconnected-state transition, the one
            // SendCommand checks; nothing subscribes to the fixture's events,
            // so Radio.Connected stays true and only the sequence number can
            // tell. Not Radio.Disconnect, which tears down the whole rig.
            typeof(Flex.Smoothlake.FlexLib.CommandCommunicationBase)
                .GetMethod("PublishDisconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Vendor.Transport, new object[] { fixture.Vendor.Transport.CurrentConnection! });
            Assert.False(fixture.Vendor.Transport.IsConnected);
        }

        // ------------------------------------------------------------------
        // Transmit and microphone profiles (Noel, 2026-10-19: refused exactly
        // like global ones, saving and deleting alike)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(RadioOwnership.Unset, ProfileTypes.tx, "did not delete the transmit profile.")]
        [InlineData(RadioOwnership.SomeoneElses, ProfileTypes.tx, "did not delete the transmit profile.")]
        [InlineData(RadioOwnership.Unset, ProfileTypes.mic, "did not delete the microphone profile.")]
        [InlineData(RadioOwnership.SomeoneElses, ProfileTypes.mic, "did not delete the microphone profile.")]
        public void ATransmitOrMicrophoneDeleteOnARadioThatIsNotOursIsRefusedBeforeAnythingIsQueued(
            RadioOwnership ownership, ProfileTypes type, string verb)
        {
            Declare(ownership);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Rig.Callouts.Profiles = new List<Profile_t>();
            using var held = new HeldQueue(fixture.Rig);

            var outcome = fixture.Rig.DeleteProfileGuarded(new Profile_t("K5TEST-" + type, type, false), new List<Profile_t>());

            Assert.Equal(GuardedOutcome.Refused, outcome);
            Assert.Equal(0, held.Count);
            Assert.Empty(fixture.Vendor.Transport.Commands);
            string heard = fixture.Rig.LastGlobalProfileWriteRefusal;
            Assert.Equal(Expected(ownership, FlexBase.ProfileWriteActionKey(type, delete: true)), heard);
            Assert.Contains(verb, heard);
            Assert.Contains("Settings, under Per-radio settings", heard);
        }

        [Theory]
        [InlineData(ProfileTypes.tx, "profile transmit delete \"K5TEST-tx\"")]
        [InlineData(ProfileTypes.mic, "profile mic delete \"K5TEST-mic\"")]
        public void ATransmitOrMicrophoneDeleteOnOurOwnRadioStillGoesOut(ProfileTypes type, string wire)
        {
            // Positive control: the same held-queue drain as the global case.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Rig.Callouts.Profiles = new List<Profile_t>();
            using var held = new HeldQueue(fixture.Rig);

            fixture.Rig.DeleteProfileGuarded(new Profile_t("K5TEST-" + type, type, false), new List<Profile_t>());

            Assert.Equal(1, held.Count);
            held.RunOne();
            Assert.Contains(wire, fixture.Vendor.Transport.Commands);
            Assert.Null(fixture.Rig.LastGlobalProfileWriteRefusal);
        }

        [Theory]
        [InlineData(RadioOwnership.Unset, ProfileTypes.tx, "did not create the transmit profile.")]
        [InlineData(RadioOwnership.SomeoneElses, ProfileTypes.tx, "did not create the transmit profile.")]
        [InlineData(RadioOwnership.Unset, ProfileTypes.mic, "did not create the microphone profile.")]
        [InlineData(RadioOwnership.SomeoneElses, ProfileTypes.mic, "did not create the microphone profile.")]
        public void SelectingATransmitOrMicrophoneNameTheRadioDoesNotHaveIsRefusedOnARadioThatIsNotOurs(
            RadioOwnership ownership, ProfileTypes type, string verb)
        {
            // Select is how a transmit or microphone profile gets WRITTEN from
            // the Profiles dialog: an absent name is created on the radio
            // first. That create is the "save" the ruling covers.
            Declare(ownership);
            using var fixture = new RigOnVendorRadio(Serial);

            var outcome = fixture.Rig.SelectProfileGuarded(new Profile_t("K5TEST-" + type, type, false));

            Assert.Equal(GuardedOutcome.Refused, outcome);
            Assert.Empty(fixture.Vendor.Transport.Commands);
            string heard = fixture.Rig.LastGlobalProfileWriteRefusal;
            Assert.Equal(Expected(ownership, FlexBase.ProfileWriteActionKey(type, delete: false)), heard);
            Assert.Contains(verb, heard);
        }

        [Theory]
        [InlineData(ProfileTypes.tx, "profile transmit create \"K5TEST-tx\"")]
        [InlineData(ProfileTypes.mic, "profile mic create \"K5TEST-mic\"")]
        public void SelectingATransmitOrMicrophoneNameOnOurOwnRadioStillCreatesIt(ProfileTypes type, string wire)
        {
            // Positive control. Select enqueues with the run-before-the-main-
            // loop flag, so it goes out inline here.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);

            var outcome = fixture.Rig.SelectProfileGuarded(new Profile_t("K5TEST-" + type, type, false));

            Assert.Equal(GuardedOutcome.Done, outcome);
            Assert.Contains(wire, fixture.Vendor.Transport.Commands);
            Assert.Null(fixture.Rig.LastGlobalProfileWriteRefusal);
        }

        [Theory]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void SelectingATransmitProfileTheRadioAlreadyHasIsNotAProfileWriteAndIsUnchanged(RadioOwnership ownership)
        {
            // Not this ruling's subject: selecting an existing profile writes
            // no profile. Pinned so the gate is seen to stop at the create.
            Declare(ownership);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Vendor.Status("profile tx list=K5TEST-tx^");
            Assert.Contains("K5TEST-tx", fixture.Vendor.Radio.ProfileTXList);

            var outcome = fixture.Rig.SelectProfileGuarded(new Profile_t("K5TEST-tx", ProfileTypes.tx, false));

            Assert.Equal(GuardedOutcome.Done, outcome);
            Assert.DoesNotContain(fixture.Vendor.Transport.Commands, c => c.Contains("create"));
            Assert.Null(fixture.Rig.LastGlobalProfileWriteRefusal);
        }

        // ------------------------------------------------------------------
        // Plumbing
        // ------------------------------------------------------------------

        /// <summary>
        /// Everything the blocker needs besides ownership: connected, only us
        /// on the roster, a global profile selected. All set the way the
        /// production feeds would set them, one layer down.
        /// </summary>
        private static void MakeSaveable(RigOnVendorRadio fixture)
        {
            typeof(FlexBase).GetField("_IsConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(fixture.Rig, true);
            var tracker = (RosterTracker)typeof(FlexBase)
                .GetProperty("RosterTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Rig)!;
            int generation = fixture.Binding.Generation;
            tracker.OwnHandleEstablished(RigOnVendorRadio.OurHandle, generation);
            tracker.ClientAdded(new RosterEntry(RigOnVendorRadio.OurHandle, "client-1", true, "K5TEST", "JJFlexTests"), generation);
            fixture.Vendor.Status("profile global current=" + Name);
            Assert.Equal(Name, fixture.Rig.CurrentGlobalProfileName);
            Assert.Equal(RosterVerdict.OnlyUs, fixture.Rig.OtherOperatorPresence);
        }

        private static void SetDirty(FlexBase rig, bool dirty) =>
            typeof(FlexBase).GetProperty(nameof(FlexBase.OperatorChangedStationThisSession))!
                .GetSetMethod(nonPublic: true)!.Invoke(rig, new object[] { dirty });

        /// <summary>
        /// Holds FlexBase's command queue undrained by telling it the main loop
        /// owns it (so Enqueue queues instead of running inline), and lets the
        /// test read how many items are waiting. Restores on dispose.
        /// </summary>
        private sealed class HeldQueue : IDisposable
        {
            private readonly object _queue;
            private readonly PropertyInfo _mainLoop;
            private readonly PropertyInfo _count;

            public HeldQueue(FlexBase rig)
            {
                _queue = typeof(FlexBase).GetField("q", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig)!;
                _mainLoop = _queue.GetType().GetProperty("MainLoop")!;
                _count = _queue.GetType().GetProperty("Count")!;
                _mainLoop.SetValue(_queue, true);
            }

            public int Count => (int)_count.GetValue(_queue)!;

            /// <summary>Take the next queued item and run its delegate, as
            /// the main loop would.</summary>
            public void RunOne()
            {
                object item = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
                ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
            }

            public void Dispose() => _mainLoop.SetValue(_queue, false);
        }
    }
}
