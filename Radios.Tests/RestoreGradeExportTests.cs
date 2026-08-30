using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The restore-grade settings capture (#414's capture half): the export
    /// that walks every profile and writes a file a person can restore a
    /// factory-reset radio from.
    ///
    /// <para>No radio is available under test, and the export's whole promise
    /// is exactly about that situation: every fact it cannot read must be
    /// WRITTEN DOWN as unreadable, never omitted — after the reset, a missing
    /// line and a setting nobody captured must not look the same. So the
    /// offline shape is the load-bearing shape: every section present, every
    /// key present, unreadable marked as unreadable, a profile that could not
    /// be read still getting its section, and the whole file round-tripping
    /// through a parser.</para>
    /// </summary>
    public sealed class RestoreGradeExportTests
    {
        private static FlexBase MakeRig()
            => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        private static string Generate(FlexBase rig)
        {
            var export = ProfileReporter.GenerateRestoreGradeExport(rig);
            Assert.NotNull(export);
            return export.Text;
        }

        // ------------------------------------------------------------------
        // Sections: all of them, always
        // ------------------------------------------------------------------

        [Fact]
        public void Export_WithNoRadio_StillCarriesEverySection()
        {
            string text = Generate(MakeRig());

            foreach (var section in new[]
            {
                "[capture]",
                "[radio-wide settings]",
                "[settings now]",
                "[memories]",
                "[after the walk]",
            })
            {
                Assert.Contains(section + Environment.NewLine, text);
            }
            Assert.Contains("End of capture.", text);
        }

        [Fact]
        public void Export_IsScreenReaderLinear()
        {
            // Read straight through, one fact per line: no tabs, no box
            // ruling, no column layout.
            string text = Generate(MakeRig());
            Assert.DoesNotContain("\t", text);
            Assert.DoesNotContain("|", text);
        }

        // ------------------------------------------------------------------
        // Unreadable is written down, never omitted
        // ------------------------------------------------------------------

        [Fact]
        public void FactsTheRadioDidNotGiveUp_AreMarkedUnreadable_NotDropped()
        {
            string text = Generate(MakeRig());

            // One representative per section. Offline, every one of these is
            // unreadable — and every one must still be a line in the file.
            Assert.Contains("radio serial = unreadable: no radio connection", text);
            Assert.Contains("remote power on (REM ON) = unreadable: no radio connection", text);
            Assert.Contains("rf power = unreadable: no radio connection", text);
            Assert.Contains("cw speed = unreadable: no radio connection", text);
            Assert.Contains("slices open = unreadable: no radio connection", text);
            Assert.Contains("memories stored = unreadable: no radio connection", text);
        }

        [Fact]
        public void EveryTransmitAndCwKey_IsPresentEvenWhenNothingIsReadable()
        {
            // The key SET is the contract: a restorer reading this file down
            // a phone needs the same lines in the same order every time,
            // whether or not the radio answered.
            string text = Generate(MakeRig());
            foreach (var key in new[]
            {
                "tx profile selected", "mic profile selected",
                "rf power", "tune power", "mic input", "mic gain", "mic boost",
                "mic bias", "speech processor", "speech processor level",
                "compander", "compander level", "tx filter low", "tx filter high",
                "am carrier level", "transmit monitor", "vox", "vox gain", "vox delay",
                "cw speed", "cw sidetone pitch", "cw sidetone", "cw break-in",
                "cw break-in delay", "cw iambic", "cw iambic mode",
                "lineout gain", "headphone gain", "binaural receive",
            })
            {
                Assert.Contains(Environment.NewLine + key + " = ", text);
            }
        }

        // ------------------------------------------------------------------
        // The walk: a profile that cannot be read still gets its section
        // ------------------------------------------------------------------

        [Fact]
        public void ProfileThatCouldNotBeRead_GetsASectionSayingSo()
        {
            var rig = MakeRig();
            // The rig knows these profiles exist; with no radio, loading them
            // fails. That failure must be IN the file, per profile, by name.
            rig.Callouts.Profiles = new List<Profile_t>
            {
                new Profile_t("Contest", ProfileTypes.global, false),
                new Profile_t("SSB Rag Chew", ProfileTypes.tx, false),
            };

            var export = ProfileReporter.GenerateRestoreGradeExport(rig);
            Assert.NotNull(export);
            string text = export.Text;

            Assert.Contains("[global profile: Contest]" + Environment.NewLine
                + "captured = no", text);
            Assert.Contains("[TX profile: SSB Rag Chew]" + Environment.NewLine
                + "captured = no", text);
            Assert.Contains("problem = no radio connection; none of this profile's settings are in this file", text);

            // With no radio, no load request ever went out — so the file must
            // say the radio was not moved, rather than raise a false alarm
            // about profiles it never touched.
            Assert.True(export.WalkRan);
            Assert.True(export.EverythingPutBack);
            Assert.Contains("global profile put back = no load was ever sent — there was no radio connection, so the radio was not moved", text);
        }

        [Fact]
        public void WalkedProfileSection_CarriesItsSettingsUnderItsHeader()
        {
            // The success half of the walk needs a live radio, so the section
            // writer is pinned directly: a captured profile's section is its
            // header, "captured = yes", then one line per setting — readable
            // or explicitly unreadable.
            var sb = new StringBuilder();
            ProfileReporter.AppendProfileSection(sb, "global", "HomeStation",
                new List<ProfileReporter.SettingLine>
                {
                    new ProfileReporter.SettingLine("rf power", "100 watts", null),
                    new ProfileReporter.SettingLine("slice A frequency", "14.250.000", null),
                    new ProfileReporter.SettingLine("mic gain", null, "the read threw mid-walk"),
                });
            string text = sb.ToString();

            Assert.StartsWith("[global profile: HomeStation]", text);
            Assert.Contains("captured = yes", text);
            Assert.Contains("rf power = 100 watts", text);
            Assert.Contains("slice A frequency = 14.250.000", text);
            Assert.Contains("mic gain = unreadable: the read threw mid-walk", text);
        }

        // ------------------------------------------------------------------
        // The change-nothing hold: no walk, and the file says so
        // ------------------------------------------------------------------

        [Fact]
        public void ChangeNothingHold_SkipsTheWalkAndSaysSoInTheFile()
        {
            var rig = MakeRig();
            rig.Callouts.Profiles = new List<Profile_t>
            {
                new Profile_t("Contest", ProfileTypes.global, false),
            };
            rig.SetChangeNothingActive(true);

            var export = ProfileReporter.GenerateRestoreGradeExport(rig);
            Assert.NotNull(export);

            Assert.False(export.WalkRan);
            Assert.True(export.EverythingPutBack); // nothing was touched
            Assert.Contains("[profile walk skipped]", export.Text);
            Assert.Contains("change nothing is on for this radio", export.Text);
            Assert.DoesNotContain("[global profile:", export.Text);
            Assert.Contains("nothing was loaded = the walk was skipped, so the radio was not touched",
                export.Text);
        }

        // ------------------------------------------------------------------
        // The restore check exists — in the walker and in the old report
        // ------------------------------------------------------------------

        [Fact]
        public void TheWalkerChecksItsRestoreAndTheComparisonReportSurfacesIt()
        {
            // The walker's restore used to be fire-and-forget: the return
            // value of the restoring LoadProfileAndWait was discarded, so a
            // failed restore left the radio on the wrong profile with nothing
            // anywhere saying so. Both halves of the fix are pinned by source
            // because the failing path needs a radio that refuses a load.
            string source = Read("Radios/ProfileReporter.cs");
            Assert.Contains("RestoreConfirmed = LoadProfileAndWait", source);
            Assert.Contains("walk.RestoreAttempted && !walk.RestoreConfirmed", source);

            // The positive control for this scan: a token known absent.
            Assert.DoesNotContain("NoSuchRestoreSymbol", source);
        }

        // ------------------------------------------------------------------
        // One walk at a time, and the flag never leaks
        // ------------------------------------------------------------------

        [Fact]
        public void TheWalkFlag_IsReleasedAfterEveryExport()
        {
            var rig = MakeRig();
            Assert.False(ProfileReporter.WalkInProgress);

            var first = ProfileReporter.GenerateRestoreGradeExport(rig);
            Assert.NotNull(first);
            Assert.False(ProfileReporter.WalkInProgress);

            // A second export succeeding proves the first released the flag
            // rather than leaking it — a leaked flag would refuse every
            // export until restart, including the one before a real reset.
            var second = ProfileReporter.GenerateRestoreGradeExport(rig);
            Assert.NotNull(second);
            Assert.False(ProfileReporter.WalkInProgress);
        }

        // ------------------------------------------------------------------
        // The file parses: the machine-readable half of the promise
        // ------------------------------------------------------------------

        [Fact]
        public void TheWholeFile_RoundTripsThroughAnIniParser()
        {
            var rig = MakeRig();
            rig.Callouts.Profiles = new List<Profile_t>
            {
                new Profile_t("Contest", ProfileTypes.global, false),
                new Profile_t("PR781", ProfileTypes.mic, false),
            };
            string text = Generate(rig);

            var sections = ParseIni(text);

            // Every named section arrived.
            foreach (var name in new[]
            {
                "capture", "radio-wide settings", "settings now",
                "global profile: Contest", "mic profile: PR781",
                "memories", "after the walk",
            })
            {
                Assert.Contains(sections, s => s.Name == name);
            }

            // Specific facts land in their sections, as keys.
            var capture = sections.First(s => s.Name == "capture");
            Assert.True(capture.Values.ContainsKey("taken"));
            Assert.True(capture.Values.ContainsKey("radio model"));
            Assert.True(capture.Values.ContainsKey("global profile loaded at start"));
            Assert.Equal("1", capture.Values["global profiles stored"]);

            var now = sections.First(s => s.Name == "settings now");
            Assert.True(now.Values.ContainsKey("rf power"));
            Assert.True(now.Values.ContainsKey("slices open"));

            var contest = sections.First(s => s.Name == "global profile: Contest");
            Assert.Equal("no", contest.Values["captured"]);
            Assert.True(contest.Values.ContainsKey("problem"));

            // The unreadable convention is detectable mechanically.
            Assert.StartsWith("unreadable:", now.Values["rf power"]);
        }

        /// <summary>
        /// The parser the file promises to satisfy: prose header until the
        /// first [section]; then sections of "key = value" lines, keys unique
        /// within a section, split on the FIRST " = "; blank lines between;
        /// "End of capture." closes the file. Any other line is a failure.
        /// </summary>
        private static List<(string Name, Dictionary<string, string> Values)> ParseIni(string text)
        {
            var sections = new List<(string Name, Dictionary<string, string> Values)>();
            Dictionary<string, string> current = null;
            bool ended = false;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                Assert.False(ended, "content after the end marker: " + line);

                if (line == "End of capture.")
                {
                    ended = true;
                    continue;
                }
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    Assert.EndsWith("]", line);
                    current = new Dictionary<string, string>();
                    sections.Add((line.Substring(1, line.Length - 2), current));
                    continue;
                }
                if (current == null) continue; // prose header before the first section

                int sep = line.IndexOf(" = ", StringComparison.Ordinal);
                Assert.True(sep > 0, "line inside a section is not key = value: " + line);
                string key = line.Substring(0, sep);
                string value = line.Substring(sep + 3);
                Assert.False(current.ContainsKey(key), "duplicate key in one section: " + key);
                current[key] = value;
            }

            Assert.True(ended, "the file never said End of capture.");
            return sections;
        }

        // ------------------------------------------------------------------
        // Plumbing (same shape as ChangeNothingGuardTests)
        // ------------------------------------------------------------------

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
