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
    /// </list>
    /// <para>
    /// A third call site appearing anywhere fails
    /// <see cref="EveryGlobalProfileWriterIsOneOfTheKnownTwo"/>, which is
    /// the point: the finding that matters most in #777 is a writer that
    /// bypasses the gate, and a new one must be unable to arrive quietly.
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
            string body = MethodBody(Read(FlexBaseProfiles), "public bool SaveProfile(Profile_t p, bool immediately = false)");
            int refusal = body.IndexOf("OwnershipRefusesGlobalProfileWrite(", StringComparison.Ordinal);
            int send = body.IndexOf(".SaveGlobalProfile(", StringComparison.Ordinal);
            Assert.True(refusal >= 0,
                "SaveProfile no longer asks whose radio it is. The Profiles dialog's Save button and the "
                + "WinForms form's Save button reach the radio through this method and nothing else, so "
                + "without it a global profile on somebody else's radio can be rewritten from here.");
            Assert.True(send >= 0, "SaveProfile no longer sends the global save; the funnel moved and this test must follow it.");
            Assert.True(refusal < send, "the ownership refusal must come BEFORE the command goes out, not after.");
        }

        [Fact]
        public void TheProfilesDialogSaveHonoursARefusal()
        {
            // Before #777 the dialog spoke "saved" unconditionally, so a
            // refusal from the change-nothing hold was spoken and then
            // overwritten by a receipt for a save that never happened (the
            // #486 shape). The ownership refusal would have met the same
            // fate.
            string body = MethodBody(Read(Menu), "OnSave = (profileData) =>");
            Assert.Contains("if (Rig.SaveProfile(profile, immediately: true))", body);
            Assert.DoesNotContain("Rig.SaveProfile(profile, immediately: true);", body);
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
        public void EveryGlobalProfileWriterIsOneOfTheKnownTwo()
        {
            // Member calls only (a leading dot), so the port's own declaration
            // and the interface member do not count as writers.
            var callSite = new Regex(@"\.SaveGlobalProfile\s*\(", RegexOptions.Compiled);
            var found = new List<string>();
            foreach (string path in SourceFiles())
            {
                string text = File.ReadAllText(path);
                int n = callSite.Matches(text).Count;
                if (n > 0) found.Add(Relative(path) + " x" + n);
            }
            found.Sort(StringComparer.Ordinal);

            var expected = new[]
            {
                "Radios/FlexBase.Profiles.cs x1",            // the funnel, SaveProfile
                "Radios/FlexBase.StationConnect.cs x1",      // the station port
                "Radios/StationConnect/DeferredCreationRun.cs x1", // the port's one caller
            };
            Assert.True(expected.SequenceEqual(found),
                "The set of places that send a global profile save changed. Expected:\n  "
                + string.Join("\n  ", expected) + "\nFound:\n  " + string.Join("\n  ", found)
                + "\nA new writer must ask whose radio it is (StationLayoutSaveGuard) before it sends, "
                + "and then be added here with its gate named.");
        }

        // ------------------------------------------------------------------
        // The two sentences, assembled
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("settings.guard.layout_save_not_our_radio")]
        [InlineData("settings.guard.layout_save_ownership_unknown")]
        public void TheRefusalNamesWhereTheOwnershipAnswerLives(string key)
        {
            // #638: Don's own radio sat at an unanswered ownership question he
            // may never have heard, so "unknown refuses" will refuse a real
            // owner on his own radio. That is the right direction only if the
            // sentence tells him where the answer is given.
            var entries = LexiconBaseline.Parse(Read(SettingsJson));
            Assert.True(entries.ContainsKey(key), key + " is not in the settings partition");
            string text = entries[key].Resolve(VerbosityLevel.Chatty) ?? "";
            Assert.Contains("Settings, under Radios", text);
            Assert.Contains("not save", text);
            Assert.DoesNotContain("saved locally", text);
            Assert.DoesNotContain("saved on this computer", text);
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
