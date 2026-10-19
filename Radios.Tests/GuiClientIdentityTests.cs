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
    /// stored, that it is stored once and presented again, that two
    /// instances and two radios never share one, that a refusal keeps the
    /// stored id and makes the rest of the run present none, and that both
    /// connect paths hand it to the vendor's <c>Radio.Connect</c>.
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

        public void Dispose() => _scope.Dispose();

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
        public void ASecondInstanceHasItsOwnFile_SoTwoCopiesNeverPresentOneId()
        {
            string first = GuiClientIdentityStore.FilePathFor(Serial, 1);
            string second = GuiClientIdentityStore.FilePathFor(Serial, 2);
            Assert.NotEqual(first, second);
            Assert.EndsWith("gui-client-id-2.json", second);
            Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));

            Assert.True(GuiClientIdentityStore.Write(Serial, 1, "id-for-instance-one"));
            Assert.True(GuiClientIdentityStore.Write(Serial, 2, "id-for-instance-two"));
            Assert.Equal("id-for-instance-one", GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal("id-for-instance-two", GuiClientIdentityStore.Read(Serial, 2));
        }

        [Fact]
        public void EachRadioSerialHasItsOwnId()
        {
            Assert.True(GuiClientIdentityStore.Write(Serial, 1, "id-for-the-8600"));
            Assert.Null(GuiClientIdentityStore.Read(OtherSerial, 1));
            Assert.True(GuiClientIdentityStore.Write(OtherSerial, 1, "id-for-the-other-8600"));
            Assert.Equal("id-for-the-8600", GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal("id-for-the-other-8600", GuiClientIdentityStore.Read(OtherSerial, 1));
        }

        [Fact]
        public void TheFileRecordsThatTheIdWasTheRadios_NotInvented()
        {
            Assert.True(GuiClientIdentityStore.Write(Serial, 1, "8E1C2C19-0000-4000-8000-000000000001"));
            string text = File.ReadAllText(GuiClientIdentityStore.FilePathFor(Serial, 1)!);
            Assert.Contains("\"ClientId\": \"8E1C2C19-0000-4000-8000-000000000001\"", text);
            Assert.Contains("\"Source\": \"radio-assigned\"", text);
            Assert.Contains("\"Serial\": \"" + Serial + "\"", text);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-serial")]
        [InlineData("0638-0000-8600-00011")]
        public void AMalformedSerialHasNoFile_AndNeitherReadNorWriteThrows(string serial)
        {
            Assert.Null(GuiClientIdentityStore.FilePathFor(serial, 1));
            Assert.Null(GuiClientIdentityStore.Read(serial, 1));
            Assert.False(GuiClientIdentityStore.Write(serial, 1, "anything"));
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
        public void TheStoreFollowsTheSettingsRoot_WhichIsWhatJJFLEX_CONFIG_DIRMoves()
        {
            // Startup resolves BaseConfigDir through ResolveStartupDirectory —
            // the function JJFLEX_CONFIG_DIR acts on — and hands it to
            // RadioConfig.BaseDirectory (globals.vb; pinned below). So a
            // store that follows BaseDirectory follows the override.
            string relocated = Path.Combine(Path.GetTempPath(), "jjflex-clientid-relocated-" + Guid.NewGuid().ToString("N"));
            string? before = RadioConfig.BaseDirectory;
            try
            {
                Directory.CreateDirectory(relocated);
                RadioConfig.BaseDirectory = relocated;
                string path = GuiClientIdentityStore.FilePathFor(Serial, 1)!;
                Assert.StartsWith(relocated, path, StringComparison.OrdinalIgnoreCase);
                Assert.True(GuiClientIdentityStore.Write(Serial, 1, "relocated-id"));
                Assert.True(File.Exists(path));
            }
            finally
            {
                RadioConfig.BaseDirectory = before;
                try { Directory.Delete(relocated, true); } catch { }
            }

            string globals = File.ReadAllText(Path.Combine(RepoRoot(), "globals.vb"));
            int resolve = globals.IndexOf("BaseConfigDir = Radios.RadioConfig.ResolveStartupDirectory(", StringComparison.Ordinal);
            int handoff = globals.IndexOf("Radios.RadioConfig.BaseDirectory = BaseConfigDir", StringComparison.Ordinal);
            Assert.True(resolve > 0, "startup no longer resolves BaseConfigDir through ResolveStartupDirectory, so the override would not reach the per-radio store");
            Assert.True(handoff > resolve, "startup no longer hands the resolved BaseConfigDir to RadioConfig.BaseDirectory after resolving it");
        }

        // ── the identity ───────────────────────────────────────────────────

        [Fact]
        public void TheFirstConnectPresentsNothing_AndStoresWhatTheRadioMints()
        {
            var identity = GuiClientIdentity.ForConnect(Serial, 1, duplicateRefusedThisRun: false);
            Assert.Null(identity.StoredId);
            Assert.Null(identity.PresentedId);
            Assert.False(identity.PresentedStoredId);
            Assert.Contains("asking the radio to mint one", identity.DescribePresentation());

            string line = identity.RadioReported("minted-by-the-radio");
            Assert.Contains("freshly minted", line);
            Assert.Contains("stored at", line);
            Assert.Equal("minted-by-the-radio", GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal("minted-by-the-radio", identity.EffectiveId);
            Assert.False(identity.IsReturning);
            Assert.True(identity.IsOurs("minted-by-the-radio"));
            Assert.False(identity.IsOurs("somebody-else"));

            // The same report again says nothing new.
            Assert.Null(identity.RadioReported("minted-by-the-radio"));
        }

        [Fact]
        public void TheNextConnect_AndAReconnect_AndAFreshStart_AllPresentTheSameStoredId()
        {
            GuiClientIdentity.ForConnect(Serial, 1, false).RadioReported("minted-by-the-radio");

            var reconnect = GuiClientIdentity.ForConnect(Serial, 1, false);      // RetryConnect in the same run
            var freshStart = GuiClientIdentity.ForConnect(Serial, 1, false);     // a new process reading the file
            Assert.Equal("minted-by-the-radio", reconnect.PresentedId);
            Assert.Equal("minted-by-the-radio", freshStart.PresentedId);
            Assert.True(reconnect.PresentedStoredId);
            Assert.Contains("presenting stored id minted-by-the-radio", reconnect.DescribePresentation());

            string line = reconnect.RadioReported("minted-by-the-radio");
            Assert.Contains("RETURNING client", line);
            Assert.True(reconnect.IsReturning);
        }

        [Fact]
        public void AnInstanceTwoConnect_PresentsItsOwnId_NeverInstanceOnes()
        {
            GuiClientIdentity.ForConnect(Serial, 1, false).RadioReported("instance-one-id");
            var second = GuiClientIdentity.ForConnect(Serial, 2, false);
            Assert.Null(second.PresentedId);
            second.RadioReported("instance-two-id");
            Assert.Equal("instance-one-id", GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal("instance-two-id", GuiClientIdentityStore.Read(Serial, 2));
            Assert.Equal("instance-two-id", GuiClientIdentity.ForConnect(Serial, 2, false).PresentedId);
            Assert.Equal("instance-one-id", GuiClientIdentity.ForConnect(Serial, 1, false).PresentedId);
        }

        [Fact]
        public void ARefusal_KeepsTheStoredId_AndTheRestOfTheRunPresentsNone()
        {
            GuiClientIdentity.ForConnect(Serial, 1, false).RadioReported("stored-id");

            var refused = GuiClientIdentity.ForConnect(Serial, 1, false);
            Assert.Equal("stored-id", refused.PresentedId);
            refused.MarkRefusedAsDuplicate();
            Assert.True(refused.RefusedAsDuplicate);

            // The next attempt in this run: nothing presented, the file untouched.
            var fallback = GuiClientIdentity.ForConnect(Serial, 1, duplicateRefusedThisRun: true);
            Assert.Null(fallback.PresentedId);
            Assert.Equal("stored-id", fallback.StoredId);
            Assert.Contains("refused as a duplicate earlier in this run", fallback.DescribePresentation());
            string line = fallback.RadioReported("session-only-id");
            Assert.Contains("for this session only", line);
            Assert.Contains("stored id stored-id is kept", line);
            Assert.Equal("stored-id", GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal("session-only-id", fallback.EffectiveId);
            // Both are ours: the session's id and the stored one.
            Assert.True(fallback.IsOurs("session-only-id"));
            Assert.True(fallback.IsOurs("stored-id"));
        }

        [Fact]
        public void ARadioThatReportsADifferentIdThanPresented_IsAMismatch_AndTheStoredIdIsKept()
        {
            GuiClientIdentity.ForConnect(Serial, 1, false).RadioReported("stored-id");
            var identity = GuiClientIdentity.ForConnect(Serial, 1, false);
            string line = identity.RadioReported("something-the-radio-chose");
            Assert.Contains("MISMATCH", line);
            Assert.Equal("stored-id", GuiClientIdentityStore.Read(Serial, 1));
            Assert.Equal("something-the-radio-chose", identity.EffectiveId);
            Assert.False(identity.IsReturning);
            Assert.True(identity.IsOurs("something-the-radio-chose"));
            Assert.True(identity.IsOurs("stored-id"));
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

                // The radio's own status for OUR handle, parsed by the vendor
                // and raised through GUIClientAdded into guiClientAdded.
                rig.Vendor.Status("client 0x" + RigOnVendorRadio.OurHandle.ToString("X")
                    + " connected client_id=1F2E3D4C-0000-4000-8000-0000000000AB program=JJFlex station=K5TEST local_ptt=1");

                Assert.Equal("1F2E3D4C-0000-4000-8000-0000000000AB", rig.Rig.ClientIdentity.RadioReportedId);
                Assert.Equal("1F2E3D4C-0000-4000-8000-0000000000AB", GuiClientIdentityStore.Read(Serial, 1));
                Assert.True(rig.Rig.ClientIdentity.IsOurs("1F2E3D4C-0000-4000-8000-0000000000AB"));

                // The next attempt presents it.
                Assert.Equal("1F2E3D4C-0000-4000-8000-0000000000AB", rig.Rig.prepareClientIdentityForConnect("test again"));
            }
            finally
            {
                rig.Rig.UnwireStationHandlers(rig.Vendor.Radio);
            }
        }

        [Fact]
        public void TheRadiosDuplicateRefusal_MarksTheIdentityRefused_AndTheNextAttemptPresentsNone()
        {
            GuiClientIdentityStore.Write(Serial, 1, "stored-id");
            using var rig = new RigOnVendorRadio(Serial);
            // Wire the producer seam the way Connect does; the duplicate-id
            // handler rides with it.
            typeof(FlexBase).GetMethod("wireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Rig, new object[] { rig.Vendor.Radio });
            // FlexLib raises the refusal only for ITS OWN handle.
            typeof(Radio).GetField("_clientHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(rig.Vendor.Radio, RigOnVendorRadio.OurHandle);
            try
            {
                Assert.Equal("stored-id", rig.Rig.prepareClientIdentityForConnect("test"));

                // Positive control first: a disconnect WITHOUT the duplicate flag
                // does not refuse anything.
                rig.Vendor.Status("client 0x" + RigOnVendorRadio.OurHandle.ToString("X") + " disconnected forced=0 wan_validation_failed=0 duplicate_client_id=0");
                Assert.False(rig.Rig.ClientIdentity.RefusedAsDuplicate);

                rig.Vendor.Status("client 0x" + RigOnVendorRadio.OurHandle.ToString("X") + " disconnected forced=0 wan_validation_failed=0 duplicate_client_id=1");
                Assert.True(rig.Rig.ClientIdentity.RefusedAsDuplicate);

                // The next attempt in this run presents nothing; the file keeps the id.
                Assert.Null(rig.Rig.prepareClientIdentityForConnect("retry"));
                Assert.True(rig.Rig.ClientIdentity.DuplicateRefusedEarlierThisRun);
                Assert.Equal("stored-id", GuiClientIdentityStore.Read(Serial, 1));
            }
            finally
            {
                typeof(FlexBase).GetMethod("unwireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(rig.Rig, new object[] { rig.Vendor.Radio });
            }
        }

        [Fact]
        public void ARefusalOnAnotherRadioObject_IsIgnored()
        {
            GuiClientIdentityStore.Write(Serial, 1, "stored-id");
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
                Assert.Equal("stored-id", rig.Rig.prepareClientIdentityForConnect("again"));
            }
            finally
            {
                typeof(FlexBase).GetMethod("unwireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(rig.Rig, new object[] { stranger.Radio });
            }
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
