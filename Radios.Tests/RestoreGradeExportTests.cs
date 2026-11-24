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
    // #469: shares the process-wide profile-walk flag with
    // ComparisonReportGuardTests, which holds it on purpose; both sit in the
    // RadioConfig statics collection so neither refuses the other.
    [Collection(RadioConfigStaticsCollection.Name)]
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
        public void WithNoRadio_TheOperatorsListIsNeverSubstitutedForTheRadios()
        {
            var rig = MakeRig();
            // Bait: the OPERATOR's profile references. Until #418 the export
            // walked and counted THIS list — reporting the operator's
            // bookkeeping as the radio's contents, and "0 stored" for a radio
            // full of profiles nobody on this computer had named. With no
            // radio, the honest file says the lists could not be read; it
            // must not dress the bait up as a finding.
            rig.Callouts.Profiles = new List<Profile_t>
            {
                new Profile_t("Contest", ProfileTypes.global, false),
                new Profile_t("SSB Rag Chew", ProfileTypes.tx, false),
            };

            var export = ProfileReporter.GenerateRestoreGradeExport(rig);
            Assert.NotNull(export);
            string text = export.Text;

            // The operator's references appear NOWHERE: not counted, not
            // walked, not listed.
            Assert.DoesNotContain("Contest", text);
            Assert.DoesNotContain("SSB Rag Chew", text);

            // The count lines say "could not ask" — never "0", which would
            // claim the radio answered when nobody could reach it.
            Assert.Contains("global profiles stored = unreadable: no radio connection", text);
            Assert.Contains("TX profiles stored = unreadable: no radio connection", text);
            Assert.Contains("mic profiles stored = unreadable: no radio connection", text);

            // Each type's gap gets a section saying what is missing.
            Assert.Contains("[global profiles not walked]" + Environment.NewLine
                + "reason = no radio connection; any global profiles the radio holds are not captured in this file", text);
            Assert.Contains("[TX profiles not walked]", text);
            Assert.Contains("[mic profiles not walked]", text);

            // No load request ever went out — so the file must say the radio
            // was not moved, rather than raise a false alarm.
            Assert.True(export.WalkRan);
            Assert.False(export.AnyLoadRequested);
            Assert.Contains("global profile put back = no load was ever sent — the global profile list could not be read (no radio connection), so the radio was not moved", text);
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
                    new ProfileReporter.SettingLine("slice A frequency", "14.250000 megahertz", null),
                    new ProfileReporter.SettingLine("mic gain", null, "the read threw mid-walk"),
                });
            string text = sb.ToString();

            Assert.StartsWith("[global profile: HomeStation]", text);
            Assert.Contains("captured = yes", text);
            Assert.Contains("rf power = 100 watts", text);
            Assert.Contains("slice A frequency = 14.250000 megahertz", text);
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
            Assert.False(export.AnyLoadRequested); // nothing was touched
            Assert.Contains("[profile walk skipped]", export.Text);
            Assert.Contains("radio setup protection is on for this radio", export.Text);
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
            // #469 moved the walk into RunWalk, which reaches the radio
            // through the load it is handed; WalkProfiles hands it
            // LoadProfileAndWait (pinned in ComparisonReportGuardTests).
            // #469 round six: the put-back is REQUESTED and its arrival is not
            // claimed; the comparison counts any requested load for the
            // pointer sentence.
            Assert.Contains("var result = load(outcome.OriginalSelection);", source);
            Assert.Contains("if (walk.LoadAttempts > 0) result.AnyLoadRequested = true;", source);

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
            string text = Generate(rig);

            var sections = ParseIni(text);

            // Every named section arrived — including one gap section per
            // profile type, because offline no list can be read and a gap
            // that says so is the file's whole promise (#418).
            foreach (var name in new[]
            {
                "capture", "radio-wide settings", "settings now",
                "global profiles not walked", "TX profiles not walked",
                "mic profiles not walked",
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
            // The count is a marked gap, not a number: "0" here would claim
            // the radio answered when nobody could ask it (#418).
            Assert.StartsWith("unreadable:", capture.Values["global profiles stored"]);

            var now = sections.First(s => s.Name == "settings now");
            Assert.True(now.Values.ContainsKey("rf power"));
            Assert.True(now.Values.ContainsKey("slices open"));

            var gap = sections.First(s => s.Name == "mic profiles not walked");
            Assert.True(gap.Values.ContainsKey("reason"));

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
        // Every quantity carries its unit or its scale (#426)
        // ------------------------------------------------------------------

        // A unit, or the radio's 0 to 100 scale. Deliberately NOT "watts" as
        // a requirement on the power lines: whether the radio's power figure
        // is watts on every model (and in AM) is an open question of its own,
        // so this accepts any unit or scale and refuses only a bare number.
        private static readonly System.Text.RegularExpressions.Regex Quantity =
            new System.Text.RegularExpressions.Regex(
                @"^-?\d+(\.\d+)? (watts|hertz|megahertz|milliseconds|words per minute|decibels|of 100)$");

        // A toggled level: "off", or "on, level" with its scale.
        private static readonly System.Text.RegularExpressions.Regex ToggledLevel =
            new System.Text.RegularExpressions.Regex(@"^(off|on, level -?\d+ of 100)$");

        // A toggled offset: "off", or "on at" with its unit.
        private static readonly System.Text.RegularExpressions.Regex ToggledOffset =
            new System.Text.RegularExpressions.Regex(@"^(off|on at -?\d+ hertz)$");

        [Fact]
        public void The_patterns_refuse_a_bare_number()
        {
            // Instrument control: the very shape this fix removes must fail.
            Assert.DoesNotMatch(Quantity, "100");
            Assert.DoesNotMatch(Quantity, "-10");
            Assert.DoesNotMatch(ToggledLevel, "on, level 40");
            Assert.DoesNotMatch(ToggledOffset, "on at 250");
            Assert.Matches(Quantity, "100 watts");
            Assert.Matches(Quantity, "100 of 100");
            Assert.Matches(Quantity, "-10 decibels");
            Assert.Matches(ToggledLevel, "on, level 40 of 100");
        }

        [Fact]
        public void Every_quantity_in_the_settings_capture_carries_a_unit_or_a_scale()
        {
            const System.Reflection.BindingFlags Private =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var vendor = new Radios.Tests.StationConnect.VendorRadioFixture("1111-2222-6300-4426");
            var slice = (Flex.Smoothlake.FlexLib.Slice)Activator.CreateInstance(
                typeof(Flex.Smoothlake.FlexLib.Slice), Private, null, new object[] { vendor.Radio }, null)!;
            typeof(Flex.Smoothlake.FlexLib.Slice).GetField("_active", Private)!.SetValue(slice, true);
            // Every toggled level on, so its "on, level" form is what is read.
            slice.NROn = true; slice.NRSOn = true; slice.NRLOn = true; slice.NRFOn = true;
            slice.NBOn = true; slice.WNBOn = true; slice.ANFOn = true; slice.APFOn = true;
            slice.RITOn = true; slice.XITOn = true;
            // A real frequency, so the line is checked for its exact form,
            // not only its shape.
            typeof(Flex.Smoothlake.FlexLib.Slice).GetField("_freq", Private)!.SetValue(slice, 14.25);
            vendor.Radio.SliceList.Add(slice);
            var rig = MakeRig();
            rig.theRadio = vendor.Radio;
            try
            {
                var lines = ProfileReporter.CaptureKeyedSettings(rig);
                var values = lines.ToDictionary(l => l.Key, l => l);

                string[] quantities =
                {
                    "rf power", "tune power", "mic gain", "compander level",
                    "tx filter low", "tx filter high", "am carrier level",
                    "transmit monitor level, sideband", "transmit monitor level, CW",
                    "vox gain", "vox delay",
                    "cw speed", "cw sidetone pitch", "cw break-in delay",
                    "lineout gain", "headphone gain",
                };
                string sl = "slice " + slice.Letter + " ";
                string[] sliceQuantities =
                {
                    "frequency", "filter low", "filter high", "tune step", "agc threshold",
                    "audio gain", "audio pan", "rf gain", "squelch level",
                };
                string[] sliceToggledLevels =
                {
                    "noise reduction", "spectral noise reduction", "legacy noise reduction",
                    "noise reduction filter", "noise blanker", "wideband noise blanker",
                    "legacy auto notch", "audio peaking filter",
                };

                void Check(string key, System.Text.RegularExpressions.Regex shape)
                {
                    Assert.True(values.TryGetValue(key, out var line), "the capture has no line for " + key);
                    Assert.True(line.Readable, key + " was not readable from the vendor radio: " + line.Problem);
                    Assert.True(shape.IsMatch(line.Value),
                        key + " = " + line.Value + " carries no unit or scale");
                }
                foreach (string k in quantities) Check(k, Quantity);
                foreach (string k in sliceQuantities) Check(sl + k, Quantity);
                foreach (string k in sliceToggledLevels) Check(sl + k, ToggledLevel);
                Check(sl + "rit", ToggledOffset);
                Check(sl + "xit", ToggledOffset);

                // The AM carrier level is never claimed as watts here: what
                // 100 delivers in AM is an open question on the bench.
                Assert.EndsWith(" of 100", values["am carrier level"].Value, StringComparison.Ordinal);
                // The slice frequency names its unit, and is not the dotted
                // display form, which names none.
                Assert.Equal("14.250000 megahertz", values[sl + "frequency"].Value);
            }
            finally
            {
                rig.theRadio = null;   // never FlexBase's to disconnect
                rig.Dispose();
            }
        }

        [Fact]
        public void Every_quantity_in_a_memory_carries_a_unit_or_a_scale()
        {
            var vendor = new Radios.Tests.StationConnect.VendorRadioFixture("1111-2222-6300-4427");
            var memory = new Flex.Smoothlake.FlexLib.Memory(vendor.Radio, 0);
            memory.SquelchOn = true;
            memory.SquelchLevel = 20;
            // A repeater memory: a real frequency and a CTCSS tone, so both
            // physical quantities are read in the form that needs a unit.
            const System.Reflection.BindingFlags Private =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(Flex.Smoothlake.FlexLib.Memory);
            type.GetField("_freq", Private)!.SetValue(memory, 145.25);
            type.GetField("_toneMode", Private)!.SetValue(memory, Flex.Smoothlake.FlexLib.FMToneMode.CTCSS_TX);
            type.GetField("_toneValue", Private)!.SetValue(memory, "100.0");
            var lines = new List<ProfileReporter.SettingLine>();
            ProfileReporter.AddMemoryLines(lines, 1, memory);
            var values = lines.ToDictionary(l => l.Key, l => l);

            foreach (string k in new[] { "frequency", "tune step", "repeater offset", "tone value", "rf power", "rx filter low", "rx filter high" })
            {
                var line = values["memory 1 " + k];
                Assert.True(line.Readable, k + " was not readable: " + line.Problem);
                Assert.Matches(Quantity, line.Value);
            }
            Assert.Equal("on, level 20 of 100", values["memory 1 squelch"].Value);
            Assert.Equal("145.250000 megahertz", values["memory 1 frequency"].Value);
            Assert.Equal("CTCSS_TX", values["memory 1 tone mode"].Value);
            Assert.Equal("100.0 hertz", values["memory 1 tone value"].Value);
        }

        [Fact]
        public void A_memory_with_its_tone_off_writes_no_tone_unit()
        {
            // The other half of the tone rule: with the tone off the value
            // names no tone, so it must not be dressed as a frequency.
            var vendor = new Radios.Tests.StationConnect.VendorRadioFixture("1111-2222-6300-4428");
            var memory = new Flex.Smoothlake.FlexLib.Memory(vendor.Radio, 0);
            const System.Reflection.BindingFlags Private =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(Flex.Smoothlake.FlexLib.Memory);
            type.GetField("_toneMode", Private)!.SetValue(memory, Flex.Smoothlake.FlexLib.FMToneMode.Off);
            type.GetField("_toneValue", Private)!.SetValue(memory, "100.0");
            var lines = new List<ProfileReporter.SettingLine>();
            ProfileReporter.AddMemoryLines(lines, 1, memory);
            var values = lines.ToDictionary(l => l.Key, l => l);

            Assert.Equal("Off", values["memory 1 tone mode"].Value);
            Assert.Equal("100.0", values["memory 1 tone value"].Value);
            // And the helper itself: a CTCSS tone with no value stays empty
            // rather than becoming a unit with no number.
            Assert.Equal("", ProfileReporter.ToneValue(Flex.Smoothlake.FlexLib.FMToneMode.CTCSS_TX, ""));
            Assert.Equal("7.000100 megahertz", ProfileReporter.Megahertz(7.0001));
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
