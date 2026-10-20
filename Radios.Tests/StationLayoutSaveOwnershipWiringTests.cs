using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junkbox #777: every path that writes a radio's GLOBAL profile from this
    /// machine asks whose radio it is first, and refuses unless the answer is
    /// "mine". Read as source rather than run, because the thing being pinned
    /// is which writers consult the gate — a radio three states away cannot
    /// be put in a test, and the harm (Don's 6300 loading a guest's layout on
    /// his next connect) is to somebody else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The writers, found by walking every <c>.SaveGlobalProfile(</c> call
    /// site in the tree (the FlexLib method that sends <c>profile global
    /// save</c>):</b>
    /// </para>
    /// <list type="bullet">
    /// <item><c>FlexBase.SaveProfile</c> in <c>Radios/FlexBase.Profiles.cs</c>
    /// — the funnel. Reached from <c>SaveCurrentStationLayout</c> (the menu's
    /// Save Station Setup to Radio and the disconnect offer, both of which
    /// ask <c>StationLayoutSaveBlocker()</c> first), from the Profiles dialog's
    /// Save button (<c>NativeMenuBar.ShowProfilesDialog</c>'s <c>OnSave</c>),
    /// and from the WinForms <c>Profile.vb</c> form's Save button.</item>
    /// <item>The station port's <c>SaveGlobalProfile</c> in
    /// <c>Radios/FlexBase.StationConnect.cs</c> — the create-on-first-use
    /// at teardown, whose only caller is <c>DeferredCreationRun</c>, which
    /// decides through <c>DeferredGlobalCreation.Decide</c>; that decision
    /// refuses a radio not declared ours and is pinned in
    /// <c>DeferredGlobalCreationTests</c>.</item>
    /// <item>Round two: <c>FlexBase.DeleteProfileGuarded</c>, the delete
    /// funnel (Profiles dialog's Delete), which asked only the hold; and
    /// <c>RemoveProfileRestorePoint</c>, which deletes only our own marker
    /// names after a guest visit.</item>
    /// </list>
    /// <para>
    /// A new save or delete call site anywhere fails
    /// <see cref="EveryGlobalProfileSaveAndDeleteCallSiteIsKnownAndNamesItsGate"/>,
    /// which is the point: the finding that matters most in #777 is a writer
    /// that bypasses the gate, and a new one must be unable to arrive quietly.
    /// These are source pins; the same paths are driven by execution in
    /// <see cref="StationProfileOwnershipExecutionTests"/>.
    /// </para>
    /// </remarks>
    public sealed class StationLayoutSaveOwnershipWiringTests
    {
        private const string FlexBaseProfiles = "Radios/FlexBase.Profiles.cs";
        private const string Menu = "JJFlexWpf/NativeMenuBar.cs";
        private const string SettingsJson = "Radios/Lexicon/settings.json";
        private const string DeferredRun = "Radios/StationConnect/DeferredCreationRun.cs";
        private const string StationOutcome = "Radios/StationConnect/StationOutcome.cs";
        private const string LegacyProfileForm = "Profile.vb";

        // ------------------------------------------------------------------
        // The blocker — the menu save and the disconnect offer both ask it
        // ------------------------------------------------------------------

        [Fact]
        public void TheBlockerAsksWhoseRadioItIs()
        {
            // #777's defect in one line: the blocker refused for four reasons
            // and ownership was not one of them, so a guest alone on Don's
            // radio could replace Don's global profile from the menu.
            string body = MethodBody(Read(FlexBaseProfiles), "public string StationLayoutSaveBlocker(");
            Assert.Contains("RadioConfig.OwnershipOf(", body);
            Assert.Contains("StationLayoutSaveGuard.Decide(", body);
        }

        [Fact]
        public void TheMenuSaveAndTheDisconnectOfferBothGoThroughTheBlocker()
        {
            string profiles = Read(FlexBaseProfiles);
            Assert.Contains("StationLayoutSaveBlocker();",
                MethodBody(profiles, "public string SaveCurrentStationLayout("));
            Assert.Contains("StationLayoutSaveBlocker() != null) return false;",
                MethodBody(profiles, "public bool ShouldOfferStationLayoutSave("));

            string menu = Read(Menu);
            Assert.Contains("Rig.StationLayoutSaveBlocker();",
                MethodBody(menu, "private void SaveStationSetupFromMenu("));
            Assert.Contains("rig.SaveCurrentStationLayout();",
                MethodBody(menu, "private void OfferStationSaveBeforeDisconnect("));
        }

        // ------------------------------------------------------------------
        // The funnel — the Profiles dialog and the WinForms form bypass the
        // blocker, so the ownership question has to be asked where the write
        // actually goes out
        // ------------------------------------------------------------------

        [Fact]
        public void TheFunnelRefusesAGlobalWriteOnARadioThatIsNotOursBeforeSending()
        {
            string body = MethodBody(Read(FlexBaseProfiles), "public GuardedOutcome SaveProfileGuarded(Profile_t p, bool immediately = false)");
            int refusal = body.IndexOf("OwnershipRefusesProfileWrite(", StringComparison.Ordinal);
            int send = body.IndexOf("notSent = SendGlobalProfileSave(p.Name);", StringComparison.Ordinal);
            Assert.True(refusal >= 0,
                "SaveProfileGuarded no longer asks whose radio it is. The Profiles dialog's Save button and the "
                + "WinForms form's Save button reach the radio through this method and nothing else, so "
                + "without it a global profile on somebody else's radio can be rewritten from here.");
            Assert.True(send >= 0, "SaveProfileGuarded no longer sends the global save; the funnel moved and this test must follow it.");
            Assert.True(refusal < send, "the ownership refusal must come BEFORE the command goes out, not after.");
        }

        [Fact]
        public void TheDeleteFunnelRefusesAGlobalDeleteOnARadioThatIsNotOursBeforeSending()
        {
            // Round two, Codex B1: Delete asked only the hold, which defaults
            // off, so a guest could remove the owner's global profile.
            string body = MethodBody(Read(FlexBaseProfiles), "public GuardedOutcome DeleteProfileGuarded(");
            int refusal = body.IndexOf("OwnershipRefusesProfileWrite(", StringComparison.Ordinal);
            int hold = body.IndexOf("GuardRefuses(\"settings.guard.action.profile_delete\")", StringComparison.Ordinal);
            int send = body.IndexOf(".DeleteGlobalProfile(", StringComparison.Ordinal);
            Assert.True(refusal >= 0 && hold >= 0 && send >= 0, "the delete funnel lost its ownership gate, its hold, or its send");
            Assert.True(refusal < hold && hold < send, "ownership, then the hold, then the send — the blocker's order");
            // Round three (ruled 2026-10-19): all three kinds, so the gate
            // must not be conditioned on the global type any more.
            Assert.DoesNotContain("prof.ProfileType == ProfileTypes.global\r\n                && OwnershipRefusesProfileWrite", body);
            Assert.DoesNotContain("prof.ProfileType == ProfileTypes.global\n                && OwnershipRefusesProfileWrite", body);
            Assert.True(body.IndexOf(".DeleteTXProfile(", StringComparison.Ordinal) > refusal
                     && body.IndexOf(".DeleteMICProfile(", StringComparison.Ordinal) > refusal,
                "the transmit and microphone deletes must sit behind the same ownership gate");
        }

        [Fact]
        public void TheSelectFunnelRefusesTheCreateHalfOnARadioThatIsNotOurs()
        {
            // A transmit or microphone profile is only ever WRITTEN from the
            // operator surfaces by Select creating an absent name; that is
            // their save (ruled 2026-10-19).
            string body = MethodBody(Read(FlexBaseProfiles), "public GuardedOutcome SelectProfileGuarded(");
            int gate = body.IndexOf("WouldCreateOnSelect(prof)", StringComparison.Ordinal);
            int refusal = body.IndexOf("OwnershipRefusesProfileWrite(", StringComparison.Ordinal);
            int createTx = body.IndexOf(".CreateTXProfile(", StringComparison.Ordinal);
            int createMic = body.IndexOf(".CreateMICProfile(", StringComparison.Ordinal);
            Assert.True(gate >= 0 && refusal > gate && createTx > refusal && createMic > refusal,
                "the create-on-select must ask whose radio it is before either create");
        }

        [Fact]
        public void TheProfilesDialogSaveHonoursARefusalAndReportsAnUnsentSave()
        {
            // Before #777 the dialog spoke "saved" unconditionally, so a
            // refusal from the change-nothing hold was spoken and then
            // overwritten by a receipt for a save that never happened (the
            // #486 shape). Round two: an unsent save is spoken as a failure,
            // not left silent.
            string body = MethodBody(Read(Menu), "OnSave = (profileData) =>");
            Assert.Contains("switch (Rig.SaveProfileGuarded(profile, immediately: true))", body);
            Assert.Contains("case Radios.GuardedOutcome.Done:", body);
            Assert.Contains("case Radios.GuardedOutcome.Refused:", body);
            Assert.Contains("settings.profile.save_not_sent", body);
            Assert.DoesNotContain("Rig.SaveProfile(profile, immediately: true);", body);
        }

        [Fact]
        public void TheLayoutSaveClearsTheDirtyFlagOnlyAfterTheCommandWentOut()
        {
            string body = MethodBody(Read(FlexBaseProfiles), "public string SaveCurrentStationLayout(");
            int outcome = body.IndexOf("if (outcome != GuardedOutcome.Done)", StringComparison.Ordinal);
            int clear = body.IndexOf("OperatorChangedStationThisSession = false;", StringComparison.Ordinal);
            Assert.True(outcome >= 0 && clear > outcome, "the dirty flag must be cleared only after a Done outcome");
            Assert.DoesNotContain("The radio did not accept the save.", body);
        }

        [Fact]
        public void TheWinFormsProfileFormStillReachesTheRadioOnlyThroughTheFunnel()
        {
            // Not a writer of its own: it calls the same SaveProfile, so the
            // funnel's refusal covers it. Pinned so that if somebody gives the
            // form a direct route, this says where the gate was.
            string vb = Read(LegacyProfileForm);
            Assert.Contains("RigControl.SaveProfile(val)", vb);
            Assert.DoesNotContain("SaveGlobalProfile", vb);
        }

        // ------------------------------------------------------------------
        // The teardown create — the one automatic writer, with its own gate
        // ------------------------------------------------------------------

        [Fact]
        public void TheTeardownCreateDecidesOwnershipBeforeItsPortSends()
        {
            string run = Read(DeferredRun);
            int decide = run.IndexOf("DeferredGlobalCreation.Decide(", StringComparison.Ordinal);
            int send = run.IndexOf("_port.SaveGlobalProfile(", StringComparison.Ordinal);
            Assert.True(decide >= 0 && send >= 0 && decide < send,
                "the deferred create must decide before its port sends the save");

            string decision = MethodBody(Read(StationOutcome), "public static CreationDecision Decide(CreationFacts f)");
            Assert.Contains("f.Ownership != RadioOwnership.Mine", decision);
        }

        // ------------------------------------------------------------------
        // The inventory — a third writer cannot arrive quietly
        // ------------------------------------------------------------------

        [Fact]
        public void EveryGlobalProfileSaveAndDeleteCallSiteIsKnownAndNamesItsGate()
        {
            // WHAT THIS CHECKS, AND ONLY THAT (round two, Codex F2): the member
            // calls of FlexLib's two global-profile mutators, SaveGlobalProfile
            // and DeleteGlobalProfile, AND — since round three replaced the
            // save funnel's vendor call with a raw command through the
            // reply-bearing send — any literal "profile global save" or
            // "profile global delete" command text, in Radios, JJFlexWpf and
            // the root VB. It does not see the database import upload (F3) or
            // a writer in another tree. A leading dot on the member calls, so
            // the port's own declaration and the interface member do not count.
            // Round three widened it to every kind: the vendor's save, create
            // and delete for global, transmit and microphone profiles, and the
            // raw command text for any of them.
            //
            // THE COUNT (round four, Codex F2 of the third re-verify): this
            // regex finds SIXTEEN matches in four files, not the fourteen the
            // round-three report claimed: one in FlexBase.MainThread.cs (the
            // dead "#if zero" create, which never compiles), eleven in
            // FlexBase.Profiles.cs (the raw save literal among them), three in
            // FlexBase.StationConnect.cs and one in DeferredCreationRun.cs
            // (the port's call). The expected list below keeps multiplicity,
            // so a seventeenth match fails it. What it CANNOT see: a command
            // string built at run time rather than written as a literal, a
            // literal spelled "profile tx ..." (this file's own info command
            // uses that spelling; the regex knows only "transmit"), any call
            // spelled without the leading dot, the database import upload
            // (F3), any .vb file below the repository root, and every other
            // source tree. It is a textual inventory of these trees, not proof
            // that nothing else writes a profile.
            var callSite = new Regex(
                @"\.(SaveGlobalProfile|DeleteGlobalProfile|SaveTXProfile|CreateTXProfile|DeleteTXProfile|SaveMICProfile|CreateMICProfile|DeleteMICProfile)\s*\("
                + @"|""(profile (?:global|transmit|mic) (?:save|create|delete))\b", RegexOptions.Compiled);
            var found = new List<string>();
            foreach (string path in SourceFiles())
            {
                string text = File.ReadAllText(path);
                var names = callSite.Matches(text)
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : "\"" + m.Groups[2].Value + "\"")
                    .OrderBy(s => s, StringComparer.Ordinal).ToList();
                if (names.Count > 0) found.Add(Relative(path) + ": " + string.Join(", ", names));
            }
            found.Sort(StringComparer.Ordinal);

            var expected = new[]
            {
                // The two operator funnels: SaveProfileGuarded (ownership,
                // then the hold, then the raw "profile global save" through
                // SendRadioCommandWithReply, whose 0-means-disconnected is
                // what makes Done honest) and DeleteProfileGuarded (ownership,
                // then the hold, then the vendor delete, confirmed by a
                // one-second readback of the profile list); and
                // RemoveProfileRestorePoint, which deletes only a name
                // ProfileRestorePoints.IsRestorePoint recognises as one of
                // OUR markers, on a radio we were a guest on — gated by that
                // name test, not by ownership, because it exists to clean up
                // after a guest visit.
                // Transmit and microphone (round three): the creates in
                // SelectProfileGuarded sit behind WouldCreateOnSelect plus the
                // ownership gate; the creates in ApplyProfileSelection run
                // only when the stewardship plan's mayCreate is true, which a
                // pure function decides and is false for every radio not
                // declared ours; the deletes in DeleteProfileGuarded sit
                // behind the ownership gate; the deletes in
                // RemoveProfileRestorePoint are the marker-name cleanup above.
                // Dead code: createProfile() inside an "#if zero" block that
                // has not compiled in years. Textually present, never built.
                "Radios/FlexBase.MainThread.cs: CreateTXProfile",
                "Radios/FlexBase.Profiles.cs: \"profile global save\", CreateMICProfile, CreateMICProfile, CreateTXProfile, CreateTXProfile, "
                    + "DeleteGlobalProfile, DeleteGlobalProfile, DeleteMICProfile, DeleteMICProfile, DeleteTXProfile, DeleteTXProfile",
                // The station port: the deferred global create decides through
                // DeferredGlobalCreation.Decide (ownership Mine, hold off,
                // intent, only-us roster); the transmit and microphone creates
                // run inside the owner initialisation, which
                // MayWriteSharedStateAutomatically gates on the same facts.
                "Radios/FlexBase.StationConnect.cs: CreateMICProfile, CreateTXProfile, SaveGlobalProfile",
                "Radios/StationConnect/DeferredCreationRun.cs: SaveGlobalProfile",
            };
            Assert.True(expected.SequenceEqual(found),
                "The set of places that save or delete a global profile changed. Expected:\n  "
                + string.Join("\n  ", expected) + "\nFound:\n  " + string.Join("\n  ", found)
                + "\nA new one must ask whose radio it is (StationLayoutSaveGuard.OwnershipRefusal) before it sends, "
                + "and then be added here with its gate named.");
        }

        // ------------------------------------------------------------------
        // The two sentences, assembled
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("settings.guard.profile_write_not_our_radio", "settings.guard.action.profile_save", "did not save a profile on it")]
        [InlineData("settings.guard.profile_write_not_our_radio", "settings.guard.action.profile_delete", "did not delete a profile from it")]
        [InlineData("settings.guard.profile_write_not_our_radio", "settings.guard.action.tx_profile_create", "did not create a transmit profile on it")]
        [InlineData("settings.guard.profile_write_not_our_radio", "settings.guard.action.tx_profile_delete", "did not delete a transmit profile from it")]
        [InlineData("settings.guard.profile_write_not_our_radio", "settings.guard.action.mic_profile_create", "did not create a microphone profile on it")]
        [InlineData("settings.guard.profile_write_not_our_radio", "settings.guard.action.mic_profile_delete", "did not delete a microphone profile from it")]
        [InlineData("settings.guard.profile_write_ownership_unknown", "settings.guard.action.profile_save", "did not save a profile on it")]
        [InlineData("settings.guard.profile_write_ownership_unknown", "settings.guard.action.profile_delete", "did not delete a profile from it")]
        [InlineData("settings.guard.profile_write_ownership_unknown", "settings.guard.action.tx_profile_create", "did not create a transmit profile on it")]
        [InlineData("settings.guard.profile_write_ownership_unknown", "settings.guard.action.tx_profile_delete", "did not delete a transmit profile from it")]
        [InlineData("settings.guard.profile_write_ownership_unknown", "settings.guard.action.mic_profile_create", "did not create a microphone profile on it")]
        [InlineData("settings.guard.profile_write_ownership_unknown", "settings.guard.action.mic_profile_delete", "did not delete a microphone profile from it")]
        public void TheRefusalNamesTheVerbAndWhereTheOwnershipAnswerLives(string key, string actionKey, string verb)
        {
            // #638: Don's own radio sat at an unanswered ownership question he
            // may never have heard, so "unknown refuses" will refuse a real
            // owner on his own radio. That is the right direction only if the
            // sentence tells him where the answer is given. And round two
            // (Codex B2): the sentence must be true for a save that would
            // CREATE a profile, for one that would overwrite, and for a
            // delete — so it names the verb and never claims a replacement.
            var entries = LexiconBaseline.Parse(Read(SettingsJson));
            Assert.True(entries.ContainsKey(key), key + " is not in the settings partition");
            Assert.True(entries.ContainsKey(actionKey), actionKey + " is not in the settings partition");
            string template = entries[key].Resolve(VerbosityLevel.Chatty) ?? "";
            string action = entries[actionKey].Resolve(VerbosityLevel.Chatty) ?? "";
            string assembled = template.Replace("{action}", action);
            Assert.Contains("{action}", template);
            Assert.Contains(verb, assembled);
            // Ruled 2026-10-19 (Q4, Q5): the full application name, and the
            // tab called what NVDA reads for it.
            Assert.Contains("JJ Flexible Radio Access", assembled);
            Assert.Contains("Settings, under Per-radio settings", assembled);
            Assert.DoesNotContain("under Radios", assembled);
            Assert.DoesNotContain("replace", assembled);
            Assert.DoesNotContain("saved locally", assembled);
            Assert.DoesNotContain("saved on this computer", assembled);
        }

        [Theory]
        [InlineData("settings.profile.save_not_sent", "profile", "was not saved")]
        [InlineData("settings.station_layout.save_not_sent", "profileName", "was not saved")]
        public void TheUnsentSaveSentenceSaysNothingWasSaved(string key, string placeholder, string phrase)
        {
            var entries = LexiconBaseline.Parse(Read(SettingsJson));
            Assert.True(entries.ContainsKey(key), key + " is not in the settings partition");
            string text = entries[key].Resolve(VerbosityLevel.Chatty) ?? "";
            Assert.Contains("{" + placeholder + "}", text);
            Assert.Contains(phrase, text);
            Assert.DoesNotContain("saved to the radio", text);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "could not find '" + signature + "' — renamed or removed, and whatever "
                + "replaced it needs to ask whose radio it is too");
            int open = source.IndexOf('{', at);
            Assert.True(open > at, "no body after '" + signature + "'");
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(at, i - at + 1);
            }
            Assert.Fail("unbalanced braces after '" + signature + "'");
            return null;
        }

        private static IEnumerable<string> SourceFiles()
        {
            string root = RepoRoot();
            foreach (string dir in new[] { "Radios", "JJFlexWpf" })
                foreach (string f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                    if (!f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                        yield return f;
            foreach (string f in Directory.EnumerateFiles(root, "*.vb", SearchOption.TopDirectoryOnly))
                yield return f;
        }

        private static string Relative(string path) =>
            path.Substring(RepoRoot().Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot find its subject proves nothing about it.");
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
