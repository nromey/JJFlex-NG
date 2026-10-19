using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The GUI client id JJ Flexible presents to a radio (#782): where it is
    /// stored, that it is stored once and presented again, that two slots and
    /// two radios never share one, that a refusal keeps the stored id and
    /// outlives the rig that saw it, that the store believes a record only
    /// for its own key, and that both connect paths hand the id to the
    /// vendor's <c>Radio.Connect</c>.
    /// </summary>
    /// <remarks>
    /// The vendor's <c>Radio.Connect</c> itself is not driven here: its
    /// <c>InitializeCommandConnection</c> opens a real UDP socket on port
    /// 4991 and starts threads, which the in-memory fixture exists to avoid,
    /// and that method is also where the one line this id reaches —
    /// <c>client gui &lt;id&gt;</c> or a bare <c>client gui</c> — is sent.
    /// So the wire text is pinned by reading the vendor source, and our side
    /// of the seam (the argument both paths pass) by reading ours. The
    /// radio's REPORT of our id is driven for real through the fixture.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class GuiClientIdentityTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(GuiClientIdentityTests));
        private const string Serial = "0638-0000-8600-0001";
        private const string OtherSerial = "0638-0000-8600-0002";
        private const string Id = "1F2E3D4C-0000-4000-8000-0000000000AB";
        private const string OtherId = "7A7A7A7A-0000-4000-8000-0000000000CD";

        public GuiClientIdentityTests() => GuiClientIdentityRefusals.ClearForTests();

        public void Dispose()
        {
            GuiClientIdentityRefusals.ClearForTests();
            _scope.Dispose();
        }

        // ── the store ──────────────────────────────────────────────────────

        [Fact]
        public void TheFileLivesInThePerRadioStore_BesideTheRadiosConfig()
        {
            string path = GuiClientIdentityStore.FilePathFor(Serial, 1);
            Assert.NotNull(path);
            Assert.Equal(Path.Combine(RadioConfig.RadioDirectory(Serial)!, "gui-client-id.json"), path);
            Assert.StartsWith(RadioConfig.ResolvedBaseDirectory!, path, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ASecondSlotHasItsOwnFile_SoTwoCopiesNeverPresentOneId()
        {
            string first = GuiClientIdentityStore.FilePathFor(Serial, 1);
            string second = GuiClientIdentityStore.FilePathFor(Serial, 2);
            Assert.NotEqual(first, second);
            Assert.EndsWith("gui-client-id-2.json", second);
            Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
            Assert.Null(GuiClientIdentityStore.FilePathFor(Serial, 0));   // no slot, no file

            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, Id));
            Assert.Null(GuiClientIdentityStore.Write(Serial, 2, OtherId));
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal(OtherId, GuiClientIdentityStore.Read(Serial, 2));
        }

        [Fact]
        public void EachRadioSerialHasItsOwnId()
        {
            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, Id));
            Assert.Null(GuiClientIdentityStore.Read(OtherSerial, 1));
            Assert.Null(GuiClientIdentityStore.Write(OtherSerial, 1, OtherId));
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal(OtherId, GuiClientIdentityStore.Read(OtherSerial, 1));
        }

        [Fact]
        public void TheFileRecordsItsKeyAndThatTheIdWasTheRadios()
        {
            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, Id));
            string text = File.ReadAllText(GuiClientIdentityStore.FilePathFor(Serial, 1)!);
            Assert.Contains("\"ClientId\": \"" + Id + "\"", text);
            Assert.Contains("\"Source\": \"radio-assigned\"", text);
            Assert.Contains("\"Serial\": \"" + Serial + "\"", text);
            Assert.Contains("\"Slot\": 1", text);
            Assert.False(File.Exists(GuiClientIdentityStore.FilePathFor(Serial, 1) + ".tmp"), "the temporary file of the atomic write was left behind");
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-serial")]
        [InlineData("0638-0000-8600-00011")]
        public void AMalformedSerialHasNoFile_AndNeitherReadNorWriteThrows(string serial)
        {
            Assert.Null(GuiClientIdentityStore.FilePathFor(serial, 1));
            Assert.Null(GuiClientIdentityStore.Read(serial, 1));
            Assert.NotNull(GuiClientIdentityStore.Write(serial, 1, Id));
        }

        [Fact]
        public void ACorruptOrEmptyFileReadsAsNothingStored()
        {
            string path = GuiClientIdentityStore.FilePathFor(Serial, 1)!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");
            Assert.Null(GuiClientIdentityStore.Read(Serial, 1));
            File.WriteAllText(path, "{ \"ClientId\": \"   \" }");
            Assert.Null(GuiClientIdentityStore.Read(Serial, 1));
        }

        [Fact]
        public void ARecordIsBelievedOnlyForTheKeyItWasWrittenUnder()
        {
            // Written for another radio, another slot, another source, or
            // carrying an id the protocol cannot send: valid JSON, not believed.
            string path = GuiClientIdentityStore.FilePathFor(Serial, 1)!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(path, "{ \"ClientId\": \"" + Id + "\", \"Serial\": \"" + OtherSerial + "\", \"Slot\": 1, \"Source\": \"radio-assigned\" }");
            Assert.Null(GuiClientIdentityStore.Read(Serial, 1));

            File.WriteAllText(path, "{ \"ClientId\": \"" + Id + "\", \"Serial\": \"" + Serial + "\", \"Slot\": 2, \"Source\": \"radio-assigned\" }");
            Assert.Null(GuiClientIdentityStore.Read(Serial, 1));

            File.WriteAllText(path, "{ \"ClientId\": \"" + Id + "\", \"Serial\": \"" + Serial + "\", \"Slot\": 1, \"Source\": \"typed-by-hand\" }");
            Assert.Null(GuiClientIdentityStore.Read(Serial, 1));

            File.WriteAllText(path, "{ \"ClientId\": \"has a space\", \"Serial\": \"" + Serial + "\", \"Slot\": 1, \"Source\": \"radio-assigned\" }");
            Assert.Null(GuiClientIdentityStore.Read(Serial, 1));

            // Positive control: the same record with the right key reads back.
            File.WriteAllText(path, "{ \"ClientId\": \"" + Id + "\", \"Serial\": \"" + Serial + "\", \"Slot\": 1, \"Source\": \"radio-assigned\" }");
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
        }

        [Theory]
        [InlineData("short")]
        [InlineData("has a space in it")]
        [InlineData("tab\there-and-long-enough")]
        [InlineData("über-long-enough-but-not-ascii")]
        [InlineData("")]
        public void AnIdTheProtocolCannotCarryIsNeitherStoredNorBelieved(string bad)
        {
            Assert.False(GuiClientIdentityStore.IsWellFormedClientId(bad));
            Assert.NotNull(GuiClientIdentityStore.Write(Serial, 1, bad));
            Assert.Null(GuiClientIdentityStore.Read(Serial, 1));
            Assert.True(GuiClientIdentityStore.IsWellFormedClientId(Id));
        }

        [Fact]
        public void AWriteNeverReplacesAReadableRecordHoldingADifferentId()
        {
            // The only writer is a first mint for a key the reader found
            // empty. A different valid id already there means the reader
            // could not read the file at the time; replacing it would throw
            // away the identity the radio knows.
            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, Id));
            string refusal = GuiClientIdentityStore.Write(Serial, 1, OtherId);
            Assert.NotNull(refusal);
            Assert.Contains("already holds id " + Id, refusal);
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
            // The same id again is fine, and a corrupt file is replaced.
            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, Id));
            File.WriteAllText(GuiClientIdentityStore.FilePathFor(Serial, 1)!, "{ not json");
            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, OtherId));
            Assert.Equal(OtherId, GuiClientIdentityStore.Read(Serial, 1));
        }

        [Fact]
        public void TheStoreFollowsTheSettingsRoot_WhichIsWhatJJFLEX_CONFIG_DIRMoves()
        {
            // Startup resolves BaseConfigDir through ResolveStartupDirectory —
            // the function JJFLEX_CONFIG_DIR acts on — and hands it to
            // RadioConfig.BaseDirectory (globals.vb; pinned below). So a
            // store that follows BaseDirectory follows the override. The
            // test scope has already relocated BaseDirectory to a throwaway
            // tree, exactly as the override would, so the proof is that the
            // store landed there and not under the operator's %AppData%.
            string root = RadioConfig.ResolvedBaseDirectory!;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Assert.False(root.StartsWith(appData, StringComparison.OrdinalIgnoreCase),
                "the scope did not relocate the settings root, so this test would be writing the operator's live folder");
            string path = GuiClientIdentityStore.FilePathFor(Serial, 1)!;
            Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, Id));
            Assert.True(File.Exists(path));
            Assert.False(path.StartsWith(appData, StringComparison.OrdinalIgnoreCase));

            string globals = File.ReadAllText(Path.Combine(RepoRoot(), "globals.vb"));
            int resolve = globals.IndexOf("BaseConfigDir = Radios.RadioConfig.ResolveStartupDirectory(", StringComparison.Ordinal);
            // Split so the hand-rolled-assignment scan (RadioConfigStaticsIsolationTests)
            // does not read this pin on startup's text as an assignment of our own.
            int handoff = globals.IndexOf("Radios.RadioConfig.BaseDirectory" + " = BaseConfigDir", StringComparison.Ordinal);
            Assert.True(resolve > 0, "startup no longer resolves BaseConfigDir through ResolveStartupDirectory, so the override would not reach the per-radio store");
            Assert.True(handoff > resolve, "startup no longer hands the resolved BaseConfigDir to RadioConfig.BaseDirectory after resolving it");
        }

        // ── the identity ───────────────────────────────────────────────────

        [Fact]
        public void TheFirstConnectPresentsNothing_AndStoresWhatTheRadioMints()
        {
            var identity = GuiClientIdentity.ForConnect(Serial, 1);
            Assert.Null(identity.StoredId);
            Assert.Null(identity.PresentedId);
            Assert.False(identity.PresentedStoredId);
            Assert.True(identity.Persistent);
            Assert.Contains("asking the radio to mint one", identity.DescribePresentation());

            string line = identity.RadioReported(Id);
            Assert.Contains("freshly minted", line);
            Assert.Contains("stored at", line);
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal(Id, identity.EffectiveId);
            Assert.False(identity.IsReturning);

            // The same report again says nothing new.
            Assert.Null(identity.RadioReported(Id));
        }

        [Fact]
        public void TheNextConnect_AndAReconnect_AndAFreshStart_AllPresentTheSameStoredId()
        {
            GuiClientIdentity.ForConnect(Serial, 1).RadioReported(Id);

            var reconnect = GuiClientIdentity.ForConnect(Serial, 1);      // RetryConnect, or a replacement rig, in the same run
            var freshStart = GuiClientIdentity.ForConnect(Serial, 1);     // a new process reading the file
            Assert.Equal(Id, reconnect.PresentedId);
            Assert.Equal(Id, freshStart.PresentedId);
            Assert.True(reconnect.PresentedStoredId);
            Assert.Contains("presenting stored id " + Id, reconnect.DescribePresentation());

            string line = reconnect.RadioReported(Id);
            Assert.Contains("RETURNING client", line);
            Assert.True(reconnect.IsReturning);
        }

        [Fact]
        public void ASlotTwoConnect_PresentsItsOwnId_NeverSlotOnes()
        {
            GuiClientIdentity.ForConnect(Serial, 1).RadioReported(Id);
            var second = GuiClientIdentity.ForConnect(Serial, 2);
            Assert.Null(second.PresentedId);
            second.RadioReported(OtherId);
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal(OtherId, GuiClientIdentityStore.Read(Serial, 2));
            Assert.Equal(OtherId, GuiClientIdentity.ForConnect(Serial, 2).PresentedId);
            Assert.Equal(Id, GuiClientIdentity.ForConnect(Serial, 1).PresentedId);
        }

        [Fact]
        public void ARefusal_KeepsTheStoredId_IsRememberedForTheProcess_AndIsPerRadioAndSlot()
        {
            GuiClientIdentity.ForConnect(Serial, 1).RadioReported(Id);
            GuiClientIdentity.ForConnect(OtherSerial, 1).RadioReported(OtherId);

            var refused = GuiClientIdentity.ForConnect(Serial, 1);
            Assert.Equal(Id, refused.PresentedId);
            refused.MarkRefusedAsDuplicate();
            Assert.True(refused.RefusedAsDuplicate);
            Assert.True(GuiClientIdentityRefusals.IsRefused(Serial, 1));

            // The next attempt in this run — a NEW identity object, as a
            // replacement rig would build: nothing presented, the file untouched.
            var fallback = GuiClientIdentity.ForConnect(Serial, 1);
            Assert.Null(fallback.PresentedId);
            Assert.True(fallback.DuplicateRefusedEarlierThisRun);
            Assert.Equal(Id, fallback.StoredId);
            Assert.Contains("refused as a duplicate earlier in this run", fallback.DescribePresentation());
            string line = fallback.RadioReported("5E5E5E5E-0000-4000-8000-0000000000EF");
            Assert.Contains("for this session only", line);
            Assert.Contains("stored id " + Id + " is kept", line);
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));

            // Per radio and slot: another radio, and another slot of this one, are untouched.
            Assert.Equal(OtherId, GuiClientIdentity.ForConnect(OtherSerial, 1).PresentedId);
            Assert.False(GuiClientIdentityRefusals.IsRefused(Serial, 2));

            // Forgetting it restores the stored id to the next attempt.
            Assert.True(GuiClientIdentityRefusals.Forget(Serial, 1));
            Assert.Equal(Id, GuiClientIdentity.ForConnect(Serial, 1).PresentedId);
        }

        [Fact]
        public void OwnershipAndHistoryAreTwoDifferentQuestions()
        {
            GuiClientIdentity.ForConnect(Serial, 1).RadioReported(Id);
            var identity = GuiClientIdentity.ForConnect(Serial, 1);

            // Before the radio has spoken on THIS connection: history yes, ownership no.
            Assert.True(identity.IsAnIdThisCopyHasPresentedOrStored(Id));
            Assert.False(identity.IsThisConnectionsId(Id));

            identity.RadioReported(Id);
            Assert.True(identity.IsThisConnectionsId(Id));
            Assert.False(identity.IsThisConnectionsId(OtherId));
            Assert.False(identity.IsThisConnectionsId(null));

            // A refusal withdraws ownership; history remains.
            identity.MarkRefusedAsDuplicate();
            Assert.False(identity.IsThisConnectionsId(Id));
            Assert.True(identity.IsAnIdThisCopyHasPresentedOrStored(Id));

            // After the refusal, the session-only id is this connection's; the stored one is only history.
            var fallback = GuiClientIdentity.ForConnect(Serial, 1);
            fallback.RadioReported(OtherId);
            Assert.True(fallback.IsThisConnectionsId(OtherId));
            Assert.False(fallback.IsThisConnectionsId(Id));
            Assert.True(fallback.IsAnIdThisCopyHasPresentedOrStored(Id));
            Assert.True(fallback.IsAnIdThisCopyHasPresentedOrStored(OtherId));
        }

        [Fact]
        public void ARadioThatReportsADifferentIdThanPresented_IsAMismatch_AndTheStoredIdIsKept()
        {
            GuiClientIdentity.ForConnect(Serial, 1).RadioReported(Id);
            var identity = GuiClientIdentity.ForConnect(Serial, 1);
            string line = identity.RadioReported(OtherId);
            Assert.Contains("MISMATCH", line);
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal(OtherId, identity.EffectiveId);
            Assert.False(identity.IsReturning);
            Assert.True(identity.IsThisConnectionsId(OtherId));
            Assert.False(identity.IsThisConnectionsId(Id));
            Assert.True(identity.IsAnIdThisCopyHasPresentedOrStored(Id));
        }

        [Fact]
        public void ASessionOnlyIdentity_PresentsNothingAndStoresNothing_EvenWhenAnIdIsStored()
        {
            // The Connection Tester's and the harness's rigs: in the operator's
            // process, possibly beside the live connection that holds the stored id.
            Assert.Null(GuiClientIdentityStore.Write(Serial, 1, Id));
            var tester = GuiClientIdentity.ForConnect(Serial, 1, useStore: false);
            Assert.False(tester.Persistent);
            Assert.Null(tester.StoredId);
            Assert.Null(tester.PresentedId);
            Assert.Null(tester.FilePath);
            Assert.Contains("session-only", tester.DescribePresentation());

            string line = tester.RadioReported(OtherId);
            Assert.Contains("session only", line);
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal(OtherId, tester.EffectiveId);
            // And both throwaway-rig builders are told so.
            Assert.Contains("rig.UseStoredGuiClientId = false;",
                File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "ConnectionTester.cs")), StringComparison.Ordinal);
            Assert.Contains("rig.UseStoredGuiClientId = false;",
                File.ReadAllText(Path.Combine(RepoRoot(), "RadioInTheLoop", "Program.cs")), StringComparison.Ordinal);
        }

        // ── the FlexBase seam, driven through the vendored Radio ───────────

        [Fact]
        public void TheRadiosConnectedStatus_RecordsAndStoresOurId_ThroughTheProductionHandlers()
        {
            using var rig = new RigOnVendorRadio(Serial);
            rig.Rig.WireStationHandlers(rig.Vendor.Radio);
            try
            {
                string presented = rig.Rig.prepareClientIdentityForConnect("test");
                Assert.Null(presented);
                Assert.NotNull(rig.Rig.ClientIdentity);
                int slot = rig.Rig.ClientIdentity.Slot;
                Assert.Equal(GuiClientSlot.Current.Number, slot);
                Assert.True(slot > 0, "the test process holds no client-id slot");

                // The radio's own status for OUR handle, parsed by the vendor
                // and raised through GUIClientAdded into guiClientAdded.
                rig.Vendor.Status("client 0x" + RigOnVendorRadio.OurHandle.ToString("X")
                    + " connected client_id=" + Id + " program=JJFlex station=K5TEST local_ptt=1");

                Assert.Equal(Id, rig.Rig.ClientIdentity.RadioReportedId);
                Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, slot));
                Assert.True(rig.Rig.ClientIdentity.IsThisConnectionsId(Id));

                // The next attempt presents it.
                Assert.Equal(Id, rig.Rig.prepareClientIdentityForConnect("test again"));
            }
            finally
            {
                rig.Rig.UnwireStationHandlers(rig.Vendor.Radio);
            }
        }

        [Fact]
        public void TheRadiosDuplicateRefusal_OutlivesTheRigThatSawIt()
        {
            int slot = GuiClientSlot.Current.Number;
            Assert.Null(GuiClientIdentityStore.Write(Serial, slot, Id));
            Assert.Null(GuiClientIdentityStore.Write(OtherSerial, slot, OtherId));

            // Rig A sees the refusal...
            var a = new RigOnVendorRadio(Serial);
            WireProducer(a);
            typeof(Radio).GetField("_clientHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(a.Vendor.Radio, RigOnVendorRadio.OurHandle);
            try
            {
                Assert.Equal(Id, a.Rig.prepareClientIdentityForConnect("test"));

                // Positive control first: a disconnect WITHOUT the duplicate flag refuses nothing.
                a.Vendor.Status("client 0x" + RigOnVendorRadio.OurHandle.ToString("X") + " disconnected forced=0 wan_validation_failed=0 duplicate_client_id=0");
                Assert.False(a.Rig.ClientIdentity.RefusedAsDuplicate);

                a.Vendor.Status("client 0x" + RigOnVendorRadio.OurHandle.ToString("X") + " disconnected forced=0 wan_validation_failed=0 duplicate_client_id=1");
                Assert.True(a.Rig.ClientIdentity.RefusedAsDuplicate);
                Assert.Null(a.Rig.prepareClientIdentityForConnect("retry on the same rig"));
            }
            finally
            {
                UnwireProducer(a);
                a.Dispose();   // ...and the application disposes it, as CloseTheRadio does...
            }

            // ...then builds a replacement, which must still present nothing for this radio.
            using var b = new RigOnVendorRadio(Serial);
            Assert.Null(b.Rig.prepareClientIdentityForConnect("replacement rig"));
            Assert.True(b.Rig.ClientIdentity.DuplicateRefusedEarlierThisRun);
            Assert.Equal(Id, GuiClientIdentityStore.Read(Serial, slot));

            // And a rig for ANOTHER radio is untouched by it.
            using var other = new RigOnVendorRadio(OtherSerial);
            Assert.Equal(OtherId, other.Rig.prepareClientIdentityForConnect("another radio"));
        }

        [Fact]
        public void ARefusalOnAnotherRadioObject_IsIgnored()
        {
            int slot = GuiClientSlot.Current.Number;
            Assert.Null(GuiClientIdentityStore.Write(Serial, slot, Id));
            using var rig = new RigOnVendorRadio(Serial);
            var stranger = new VendorRadioFixture(OtherSerial);
            typeof(FlexBase).GetMethod("wireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Rig, new object[] { stranger.Radio });
            typeof(Radio).GetField("_clientHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(stranger.Radio, RigOnVendorRadio.OurHandle);
            try
            {
                rig.Rig.prepareClientIdentityForConnect("test");
                stranger.Status("client 0x" + RigOnVendorRadio.OurHandle.ToString("X") + " disconnected forced=0 wan_validation_failed=0 duplicate_client_id=1");
                Assert.False(rig.Rig.ClientIdentity.RefusedAsDuplicate);
                Assert.False(GuiClientIdentityRefusals.IsRefused(Serial, slot));
                Assert.Equal(Id, rig.Rig.prepareClientIdentityForConnect("again"));
            }
            finally
            {
                typeof(FlexBase).GetMethod("unwireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(rig.Rig, new object[] { stranger.Radio });
            }
        }

        // ── the post-connect fence, through the real transport callback ────

        [Fact]
        public void TheFence_IsSentOnlyForAReturningConnect_AndHoldsUntilEveryReportedOwnSliceIsReady()
        {
            int slot = GuiClientSlot.Current.Number;
            using var rig = new RigOnVendorRadio(Serial);
            var sendFence = typeof(FlexBase).GetMethod("sendConnectFence", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var tryRecord = typeof(FlexBase).GetMethod("tryRecordConnectFence", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var addSlice = typeof(Radio).GetMethod("AddSlice", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(sendFence != null && tryRecord != null && addSlice != null, "the fence's seams moved; this test would be vacuous");

            // A FRESH mint: no stored id, so no fence is sent at all.
            Assert.Null(rig.Rig.prepareClientIdentityForConnect("fresh"));
            int before = rig.Vendor.Transport.Commands.Count;
            sendFence.Invoke(rig.Rig, new object[] { "fresh" });
            Assert.Equal(before, rig.Vendor.Transport.Commands.Count);
            Assert.DoesNotContain(rig.Vendor.Transport.Commands, c => c == "ping");

            // A RETURNING connect: the stored id is presented and the fence goes out.
            Assert.Null(GuiClientIdentityStore.Write(Serial, slot, Id));
            Assert.Equal(Id, rig.Rig.prepareClientIdentityForConnect("returning"));
            sendFence.Invoke(rig.Rig, new object[] { "returning" });
            Assert.Equal("ping", rig.Vendor.LastCommand);
            int pingSeq = rig.Vendor.LastSequence;

            // The radio REPORTS one of our slices (it is in the vendor's list)
            // but it is not READY (no SliceAdded, so not in ours).
            var reported = rig.Vendor.NewSlice(0, RigOnVendorRadio.OurHandle, 14.250, "USB");
            addSlice.Invoke(rig.Vendor.Radio, new object[] { reported });

            // A rejected ping is not evidence.
            rig.Vendor.Reply(pingSeq, 0x50000001, "no");
            Assert.Equal(0, rig.Profiles.Snapshot().ConnectFenceAtSequence);

            // Send again; the radio acknowledges; the fence is HELD on the not-ready slice.
            sendFence.Invoke(rig.Rig, new object[] { "returning" });
            rig.Vendor.ReplyOk(rig.Vendor.LastSequence);
            Assert.Equal(0, rig.Profiles.Snapshot().ConnectFenceAtSequence);

            // The slice becomes ready (what sliceAdded does for ours) and the
            // re-check records the fence.
            rig.AddOwnSlice(0, 14.250, "USB");
            tryRecord.Invoke(rig.Rig, new object[] { "test readiness" });
            Assert.True(rig.Profiles.Snapshot().ConnectFenceAtSequence > 0, "the acknowledged fence was not recorded once every reported own slice was ready");

            // Positive control for the hold: with nothing reported and nothing
            // ready, an acknowledged fence records at once.
            using var quick = new RigOnVendorRadio(OtherSerial);
            Assert.Null(GuiClientIdentityStore.Write(OtherSerial, slot, OtherId));
            Assert.Equal(OtherId, quick.Rig.prepareClientIdentityForConnect("returning"));
            sendFence.Invoke(quick.Rig, new object[] { "returning" });
            quick.Vendor.ReplyOk(quick.Vendor.LastSequence);
            Assert.True(quick.Profiles.Snapshot().ConnectFenceAtSequence > 0);
        }

        // ── the wire, pinned at both ends by source ────────────────────────

        [Fact]
        public void BothConnectPaths_HandTheIdentityToTheVendorsConnect_AndNoBareConnectRemains()
        {
            string source = FlexBasePartials.JoinedText();

            // Positive control: both paths are where we think they are.
            Assert.Contains("public bool Connect(string serial, bool lowBW", source, StringComparison.Ordinal);
            Assert.Contains("public bool RetryConnect()", source, StringComparison.Ordinal);

            var withIdentity = Regex.Matches(source, @"theRadio\.Connect\(prepareClientIdentityForConnect\(""(Connect|RetryConnect)""\)\)");
            Assert.Equal(2, withIdentity.Count);
            Assert.Contains(withIdentity, m => m.Groups[1].Value == "Connect");
            Assert.Contains(withIdentity, m => m.Groups[1].Value == "RetryConnect");

            // No connect path calls the vendor with no id any more. Jim's
            // `theRadio.Connect(clientID)` under #if zero is a different text
            // and is deliberately not asserted on either way.
            Assert.Equal(0, Regex.Matches(source, @"theRadio\.Connect\(\s*\)").Count);

            // The slot, not the process count, keys the id: nothing hands
            // ProgramInstance into the identity any more.
            Assert.DoesNotContain("ProgramInstance", source, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenParms.ProgramInstance", File.ReadAllText(Path.Combine(RepoRoot(), "globals.vb")), StringComparison.Ordinal);
            Assert.Contains("GuiClientSlot.Current.Number", source, StringComparison.Ordinal);
        }

        [Fact]
        public void TheVendorSendsTheIdAsClientGui_OrAsksForOneWhenNoneIsGiven()
        {
            // The vendored Radio.Connect(string gui_client_id) and the one
            // place that id reaches the wire. Read, not run: running it opens
            // a UDP socket and threads this fixture exists to avoid.
            string radio = File.ReadAllText(Path.Combine(RepoRoot(), "FlexLib_API", "FlexLib", "Radio.cs"));
            Assert.Contains("public bool Connect(string gui_client_id = null)", radio, StringComparison.Ordinal);
            Assert.Contains("_guiClientID = gui_client_id;", radio, StringComparison.Ordinal);
            Assert.Contains("SendReplyCommand(GUIClientIDReplyHandler, \"client gui\");", radio, StringComparison.Ordinal);
            Assert.Contains("SendCommand(\"client gui \" + _guiClientID);", radio, StringComparison.Ordinal);
            // And the refusal we subscribe to is raised for our own handle only.
            Assert.Contains("if (handle_uint == _clientHandle && duplicate_client_id)", radio, StringComparison.Ordinal);
            Assert.Contains("OnDuplicateClientIdDisconnectReq();", radio, StringComparison.Ordinal);
        }

        private static void WireProducer(RigOnVendorRadio rig) =>
            typeof(FlexBase).GetMethod("wireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Rig, new object[] { rig.Vendor.Radio });

        private static void UnwireProducer(RigOnVendorRadio rig) =>
            typeof(FlexBase).GetMethod("unwireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Rig, new object[] { rig.Vendor.Radio });

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "JJFlexRadio.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.True(dir != null, "the repository root (JJFlexRadio.sln) was not found above the test binary");
            return dir!;
        }
    }
}
