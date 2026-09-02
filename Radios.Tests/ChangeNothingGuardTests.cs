using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #403: the per-radio "Change nothing on this radio" hold. Three things
    /// are pinned here, and the third is the one that earns the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First, the store: the flag round-trips by serial, and — the upgrade
    /// guarantee — a config.xml written before it existed loads as OFF, so
    /// shipping this changes nothing for anyone until they arm it. That is
    /// the setting's whole contract: opt-in, per radio, no new default.
    /// </para>
    /// <para>
    /// Second, the words: every guard key the code names exists in the store,
    /// so a refusal can never be spoken as its own key.
    /// </para>
    /// <para>
    /// Third, the WIRING, as a source scan in the CountdownKeyUpRuleTests
    /// shape. The hold is only as good as the set of writers that consult it
    /// — a guard that stops three of five writers is worse than none, because
    /// it will be trusted — and nothing else can fail when a writer stops
    /// consulting it: the build stays green, the merge stays clean, and the
    /// radio just quietly gets written to again. So each gated writer is
    /// pinned to the guard by name, with a positive control proving the
    /// reader discriminates (it finds what is there, and does NOT find guard
    /// tokens in a method that deliberately has none).
    /// </para>
    /// </remarks>
    // LEXICON_SCANNER_EXEMPT — the Lexicon.Get texts below are assertion
    // SUBJECTS quoted from FlexBase, not call sites of their own; the real
    // call sites live in FlexBase.cs and the sweep verifies them there. The
    // store-side halves of the same keys are pinned by this file's own
    // TheGuardSentencesExistInTheStore.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ChangeNothingGuardTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ChangeNothingGuardTests));

        public void Dispose() => _scope.Dispose();

        private const string FlexBase = "Radios/FlexBase.cs";
        private const string Reporter = "Radios/ProfileReporter.cs";
        private const string MainWindow = "JJFlexWpf/MainWindow.xaml.cs";
        private const string SettingsJson = "Radios/Lexicon/settings.json";

        // ------------------------------------------------------------------
        // The store: default off, round trip, upgrade guarantee
        // ------------------------------------------------------------------

        [Fact]
        public void NewConfig_HasTheHoldOff()
        {
            Assert.False(new RadioConfig().ChangeNothingOnThisRadio);
        }

        [Fact]
        public void ConfigWrittenBeforeTheHoldExisted_LoadsAsOff()
        {
            // The shape an existing install has on disk: no element at all.
            // It must deserialise to OFF — an upgrade that silently armed a
            // hold would flip behaviour for every existing operator, which is
            // exactly what this setting is not.
            var dir = Path.Combine(_scope.Directory, "radios", "1234-5678-9012-3456");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>1234-5678-9012-3456</RadioId>\n" +
                "  <Nickname>shack rig</Nickname>\n" +
                "</RadioConfig>\n");

            var cfg = RadioConfig.LoadForRadio("1234-5678-9012-3456");
            Assert.Equal("shack rig", cfg.Nickname);
            Assert.False(cfg.ChangeNothingOnThisRadio);
        }

        [Fact]
        public void TheHold_RoundTripsThroughDisk()
        {
            var cfg = new RadioConfig { ChangeNothingOnThisRadio = true };
            Assert.True(cfg.SaveForRadio("0000-1111-2222-3333"));

            var back = RadioConfig.LoadForRadio("0000-1111-2222-3333");
            Assert.True(back.ChangeNothingOnThisRadio);

            back.ChangeNothingOnThisRadio = false;
            Assert.True(back.SaveForRadio("0000-1111-2222-3333"));
            Assert.False(RadioConfig.LoadForRadio("0000-1111-2222-3333").ChangeNothingOnThisRadio);
        }

        [Fact]
        public void ChangeNothingOf_ReadsFalseForUnknownAndEmptyIds()
        {
            Assert.False(RadioConfig.ChangeNothingOf(""));
            Assert.False(RadioConfig.ChangeNothingOf(null));
            Assert.False(RadioConfig.ChangeNothingOf("9999-8888-6300-7777"));
        }

        [Fact]
        public void TheHold_DoesNotDisturbOwnership()
        {
            // The hold outranks ownership at the writers but does not rewrite
            // it in the store: lifting the hold must give back exactly the
            // radio the operator declared.
            var cfg = new RadioConfig
            {
                Ownership = RadioOwnership.Mine,
                ChangeNothingOnThisRadio = true,
            };
            Assert.True(cfg.SaveForRadio("0000-1111-2222-4444"));

            var back = RadioConfig.LoadForRadio("0000-1111-2222-4444");
            Assert.Equal(RadioOwnership.Mine, back.Ownership);
            Assert.True(back.ChangeNothingOnThisRadio);
        }

        // ------------------------------------------------------------------
        // The words: every key the guard names exists in the store
        // ------------------------------------------------------------------

        [Fact]
        public void EveryGuardActionKeyNamedInSourceExistsInTheStore()
        {
            // GuardRefuses takes its key as a plain argument, which the
            // lexicon coverage sweep cannot see (it reads only Lexicon.Get
            // literals). So this test is that sweep for the guard's own call
            // shape: harvest every GuardRefuses("...") literal and look each
            // one up.
            var storeKeys = StoreKeys();
            var named = new List<string>();
            foreach (Match m in Regex.Matches(Read(FlexBase), "GuardRefuses\\(\"([^\"]+)\"\\)"))
            {
                named.Add(m.Groups[1].Value);
            }

            Assert.True(named.Count >= 10,
                "expected at least ten GuardRefuses call sites in FlexBase and found "
                + named.Count + " — writers have stopped consulting the guard, or the "
                + "call shape changed and this harvest needs to follow it");

            foreach (var key in named.Distinct())
            {
                Assert.True(storeKeys.Contains(key),
                    "GuardRefuses names the key '" + key + "' and the store does not "
                    + "have it, so that refusal would be spoken as its own key");
            }
        }

        [Theory]
        [InlineData("settings.guard.connected")]
        [InlineData("settings.guard.refused")]
        [InlineData("settings.guard.mic_stays")]
        [InlineData("settings.guard.firmware_blocked")]
        [InlineData("settings.guard.layout_save_blocked")]
        [InlineData("settings.profile.change_nothing_on_warning")]
        [InlineData("settings.profile.change_nothing_off")]
        [InlineData("settings.profile.saved_change_nothing_on")]
        [InlineData("settings.profile.saved_change_nothing_off")]
        [InlineData("settings.profile.describe_change_nothing")]
        public void TheGuardSentencesExistInTheStore(string key)
        {
            Assert.True(StoreKeys().Contains(key),
                "the store has no '" + key + "'");
        }

        [Fact]
        public void TheProfileDescriptionTemplateCarriesTheHold()
        {
            // The Radios-tab description is composed from a template with
            // named slots. A clause whose slot is missing from the template
            // silently never renders — the argument is simply unused — so the
            // slot itself has to be pinned.
            using var doc = JsonDocument.Parse(Read(SettingsJson));
            string template = doc.RootElement.GetProperty("settings.profile.describe").GetString() ?? "";
            Assert.Contains("{changeNothing}", template, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // The wiring: each gated writer consults the guard
        // ------------------------------------------------------------------

        /// <summary>
        /// Operator-initiated writers refuse OUT LOUD: the guard call, within
        /// the opening of the method that owns the write.
        /// </summary>
        [Theory]
        [InlineData("public bool RenameRadio(", "GuardRefuses(\"settings.guard.action.rename\")", 700)]
        [InlineData("public bool SetSmartLinkPortForwarding(", "GuardRefuses(\"settings.guard.action.ports\")", 900)]
        [InlineData("public bool RemoteOnEnabled", "GuardRefuses(\"settings.guard.action.rem_on\")", 900)]
        [InlineData("public bool EnforcePrivateIPConnections", "GuardRefuses(\"settings.guard.action.private_ip\")", 900)]
        [InlineData("public bool ApplyStaticIp(", "GuardRefuses(\"settings.guard.action.network\")", 500)]
        [InlineData("public bool RevertToDhcp(", "GuardRefuses(\"settings.guard.action.network\")", 500)]
        [InlineData("private bool SendRegistrationCommand(", "GuardRefuses(\"settings.guard.action.registration\")", 900)]
        [InlineData("public bool SetSelectedOscillator(", "GuardRefuses(\"settings.guard.action.oscillator\")", 500)]
        [InlineData("public bool BeginFirmwareUpdate(", "GuardRefuses(\"settings.guard.action.firmware\")", 900)]
        [InlineData("public string RadioCallsign", "GuardRefuses(\"settings.guard.action.callsign\")", 1000)]
        [InlineData("public string FrontPanelDisplayMode", "GuardRefuses(\"settings.guard.action.front_panel\")", 1400)]
        [InlineData("public GuardedOutcome SelectProfileGuarded(", "GuardRefuses(\"settings.guard.action.profile_load\")", 1000)]
        [InlineData("public bool SelectMicProfileIfPresent(", "GuardRefuses(\"settings.guard.action.profile_load\")", 900)]
        [InlineData("public bool SaveProfile(", "GuardRefuses(\"settings.guard.action.profile_save\")", 800)]
        // The gap the #397 write-path audit found in this very list: under the
        // hold you could still DELETE a profile off the radio while loading and
        // saving one both refused. Delete is the most destructive of the three
        // — a load is reversible and a save overwrites one profile, while a
        // delete removes the only copy of somebody's station settings.
        [InlineData("public GuardedOutcome DeleteProfileGuarded(", "GuardRefuses(\"settings.guard.action.profile_delete\")", 800)]
        public void TheOperatorFacingWriterRefusesAndSpeaks(string signature, string guard, int window)
        {
            AssertGuardInside(FlexBase, signature, guard, window);
        }

        /// <summary>
        /// Automatic connect- and audio-path writers skip with a trace; the
        /// one connect-time announcement covers them.
        /// </summary>
        [Theory]
        [InlineData("private bool saveNewGlobalProfile(", "GuardSkips(\"saveNewGlobalProfile", 1200)]
        [InlineData("internal bool GetProfileInfo(", "GuardSkips(\"default profile selection", 2200)]
        [InlineData("private void ApplyAccountUPnPPreferenceIfAny(", "GuardSkips(\"UPnP router mapping", 900)]
        [InlineData("private bool startOpusOutputChannel(", "GuardSkips(\"MicInput=PC", 900)]
        [InlineData("private void issue7620(", "GuardSkips(\"issue7620 CW keyer restore", 3200)]
        [InlineData("private bool setupFromScratch(", "GuardSkips(\"scratch-setup", 7000)]
        public void TheAutomaticWriterSkipsUnderTheHold(string signature, string guard, int window)
        {
            AssertGuardInside(FlexBase, signature, guard, window);
        }

        /// <summary>
        /// The writers that are LINES rather than methods — pinned by their
        /// guarded form, which cannot exist without the guard around it.
        /// </summary>
        [Theory]
        [InlineData("if (!GuardSkips(\"TNFEnabled=true on connect\")) theRadio.TNFEnabled = true;")]
        [InlineData("GuardSkips(\"IsMuteLocalAudioWhenRemoteOn=false on local connect\")")]
        [InlineData("GuardSkips(\"IsMuteLocalAudioWhenRemoteOn=true on remote audio start\")")]
        [InlineData("GuardSkips(\"MicInput=mic on local open\")")]
        [InlineData("GuardSkips(\"SimpleVOXEnable=false / CWBreakIn=false on open\")")]
        [InlineData("GuardSkips(\"TX1Enabled=true on open\")")]
        public void TheConnectPathLiteralIsGuarded(string guardedForm)
        {
            Assert.Contains(guardedForm, Read(FlexBase), StringComparison.Ordinal);
        }

        [Fact]
        public void TheGuardIsArmedBeforeTheFirstConnectPathWrite()
        {
            // The flag is read from the per-radio config inside Connect, and
            // it has to be read BEFORE the first write it governs — a guard
            // loaded after the writes is scenery. File order stands in for
            // execution order here because both live in one straight-line
            // stretch of the same method.
            string source = Read(FlexBase);
            int armed = source.IndexOf(
                "SetChangeNothingActive(knownRadioProfile.ChangeNothingOnThisRadio)",
                StringComparison.Ordinal);
            int firstWrite = source.IndexOf(
                "GuardSkips(\"TNFEnabled=true on connect\")", StringComparison.Ordinal);

            Assert.True(armed > 0, "the connect path no longer arms the hold from the per-radio config");
            Assert.True(firstWrite > 0, "the TNF connect write lost its guard");
            Assert.True(armed < firstWrite,
                "the hold is armed AFTER the first connect-path write it exists to stop");
        }

        [Fact]
        public void TheConnectAnnouncementIsWiredIntoFlexOpen()
        {
            // THE OPERATOR MUST KNOW IT IS ON. A protection that is silently
            // active is how someone concludes the app is broken when a
            // setting will not stick — so flex open says it, once, before the
            // writes it suppresses would have run.
            AssertGuardInside(FlexBase, "private void mainThreadProc(",
                "Lexicon.Get(\"settings.guard.connected\")", 3200);
        }

        [Fact]
        public void ThePreflightAndTheBlockerNameTheHold()
        {
            AssertGuardInside(FlexBase, "public FirmwareUpdateCheck PreflightFirmwareUpdate(",
                "Lexicon.Get(\"settings.guard.firmware_blocked\")", 1600);
            AssertGuardInside(FlexBase, "public string StationLayoutSaveBlocker(",
                "Lexicon.Get(\"settings.guard.layout_save_blocked\")", 1200);
        }

        [Fact]
        public void TheSilentTxRepairYieldsToTheHoldButTheWarningDoesNot()
        {
            // Ownership says Mine, the hold says not today — the repair must
            // wait and the WARNING must still fire, because saying a failure
            // out loud needs no permission from anyone.
            AssertGuardInside(FlexBase, "private void CheckMicProfileForSilentTx(",
                "ownership == RadioOwnership.Mine && !ChangeNothingActive", 4200);
        }

        [Fact]
        public void ThePcMicReassertYieldsToTheHold()
        {
            AssertGuardInside(FlexBase, "private void checkPcMicSelection(",
                "ChangeNothingActive) return;", 900);
        }

        [Fact]
        public void ThePcAudioPathSaysTheMicIsStaying()
        {
            // Refusing the mic swap without saying so is the silent-transmit
            // shape: the operator keys up and puts out nothing. The refusal
            // has to carry its own Critical sentence.
            AssertGuardInside(FlexBase, "private bool startOpusOutputChannel(",
                "Lexicon.Get(\"settings.guard.mic_stays\")", 1600);
        }

        [Fact]
        public void TheOnConnectRemOnApplyYieldsToTheHold()
        {
            AssertGuardInside(MainWindow, "private void ApplyRemOnOnConnect(",
                "cfg.ChangeNothingOnThisRadio", 1600);
        }

        [Fact]
        public void TheProfileReportSkipsItsLoadEveryProfilePassAndSaysSo()
        {
            string source = Read(Reporter);
            int gate = source.IndexOf("rig.ChangeNothingActive", StringComparison.Ordinal);
            int pass = source.IndexOf("CaptureAllProfiles(rig", StringComparison.Ordinal);

            Assert.True(gate > 0,
                "the profile report no longer consults the hold before its comparison "
                + "pass, which loads every stored profile on the radio in turn (#414)");
            Assert.True(pass > 0, "the comparison pass itself is gone — re-inspect this test");
            Assert.True(gate < pass,
                "the hold is consulted only after the comparison pass has already run");
            Assert.Contains("PROFILE COMPARISON SKIPPED", source, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // The positive control
        // ------------------------------------------------------------------

        [Fact]
        public void TheSourceReaderFindsWhatIsThereAndDiscriminates()
        {
            // Every assertion above is a source scan, and a scan that reads
            // the wrong file — or slices at the wrong place — passes for the
            // wrong reason. Prove the reader finds something known present,
            // rejects something known absent, and that the SLICER genuinely
            // bounds its window: RefreshLicenseState is a deliberate
            // non-writer with no guard in it, and its opening must not
            // contain one.
            string flexBase = Read(FlexBase);
            Assert.Contains("GuardRefuses", flexBase, StringComparison.Ordinal);
            Assert.Contains("GuardSkips", flexBase, StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchGuardSymbol", flexBase, StringComparison.Ordinal);

            string slice = Slice(flexBase, "public void RefreshLicenseState(", 400);
            Assert.DoesNotContain("GuardRefuses", slice, StringComparison.Ordinal);
            Assert.DoesNotContain("GuardSkips", slice, StringComparison.Ordinal);

            // And the store reader reads a real store.
            var keys = StoreKeys();
            Assert.Contains("settings.profile.press_apply_or_ok", keys);
            Assert.DoesNotContain("settings.guard.no_such_key", keys);
        }

        // ------------------------------------------------------------------
        // Plumbing
        // ------------------------------------------------------------------

        private static void AssertGuardInside(string file, string signature, string guard, int window)
        {
            string slice = Slice(Read(file), signature, window);
            Assert.True(slice.Contains(guard, StringComparison.Ordinal),
                file + ": expected '" + guard + "' within " + window + " characters of '"
                + signature + "'. Either the writer stopped consulting the change-nothing "
                + "hold — which quietly re-opens a write to a radio the operator asked us "
                + "to leave alone — or the method moved and this window needs to follow it.");
        }

        private static string Slice(string source, string signature, int window)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0,
                "could not find '" + signature + "' — the method was renamed or removed, "
                + "and whatever replaced it needs to consult the change-nothing hold too");
            return source.Substring(at, Math.Min(window, source.Length - at));
        }

        private static HashSet<string> StoreKeys()
        {
            using var doc = JsonDocument.Parse(Read(SettingsJson));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject()) keys.Add(p.Name);
            return keys;
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that "
                + "cannot find its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
