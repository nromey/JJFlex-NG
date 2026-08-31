using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #418: the settings exports enumerate profiles from the RADIO, never
    /// from the operator's profile references — and they render which of
    /// three states the reading ended in: the radio reports none; the radio
    /// never reported its list; we could not ask.
    ///
    /// <para><b>Why this exists.</b> Noel's first real run of the restore
    /// export reported "0 mic profiles stored" on his FLEX-8600 — false as a
    /// statement about the radio, because the count came from
    /// Callouts.Profiles, the operator's per-human reference list on this
    /// computer. And the "0" rendered identically whether the radio had none
    /// or was never asked. The capture exists for a factory reset that
    /// destroys the originals; the reset the fix was needed for was on a
    /// radio whose fault WAS a mic profile.</para>
    ///
    /// <para><b>How the radio is simulated.</b> FlexLib's Radio object is
    /// constructed offline (its internal constructor opens no sockets), and
    /// the radio's answers are injected by invoking the same private update
    /// methods its status parser calls — so the reading under test sees
    /// exactly what a real "profile mic list=..." status produces, including
    /// the PropertyChanged event that is the one honest "the radio answered"
    /// signal FlexLib exposes (its list getters collapse "never reported" to
    /// an empty collection).</para>
    /// </summary>
    public sealed class RadioProfileEnumerationTests
    {
        private static FlexBase MakeRig()
            => new FlexBase(new FlexBase.OpenParms
            {
                ProgramName = "JJFlexTests",
                // Defense for the finalizer: FlexBase.Dispose runs the
                // disconnect-time save path when a radio is attached, and
                // that path enumerates the operator's list. A null list
                // NREs on the GC thread and takes the whole test host down.
                Profiles = new List<Profile_t>(),
            });

        /// <summary>
        /// Detach the prop radio before the rig is collected. FlexBase's
        /// finalizer treats a non-null theRadio as a live session and runs
        /// disconnect-time work against it; our offline Radio is a prop, not
        /// a session. Call from a finally in every test that attaches one.
        /// </summary>
        private static void Detach(FlexBase rig) => rig.theRadio = null;

        /// <summary>
        /// A FlexLib Radio with no network behind it. The internal
        /// constructor initializes lists and sub-objects only; nothing
        /// connects until Connect() is called, which no test here does.
        /// </summary>
        private static Radio MakeOfflineRadio()
        {
            var radio = (Radio?)Activator.CreateInstance(
                typeof(Radio),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { false },
                culture: null);
            Assert.NotNull(radio);
            return radio!;
        }

        /// <summary>
        /// Deliver a mic profile list the way ParseProfilesStatus does:
        /// caret-separated names, empty string for "none stored". Raises the
        /// same PropertyChanged event a real status message raises.
        /// </summary>
        private static void RadioReportsMicList(Radio radio, string caretSeparated)
        {
            var m = typeof(Radio).GetMethod("UpdateProfileMicList",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(m != null,
                "FlexLib no longer has Radio.UpdateProfileMicList — the simulation seam "
                + "moved in a FlexLib upgrade, and this suite must follow it.");
            m!.Invoke(radio, new object[] { caretSeparated });
        }

        /// <summary>
        /// Deliver a selection the way a "profile mic current=..." status
        /// does — through the parser's own update path, which raises the
        /// event WITHOUT sending a load command (the public setter sends).
        /// </summary>
        private static void RadioReportsMicSelection(Radio radio, string name)
        {
            var m = typeof(Radio).GetMethod("UpdateProfileListSelection",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(m != null,
                "FlexLib no longer has Radio.UpdateProfileListSelection — the simulation "
                + "seam moved in a FlexLib upgrade, and this suite must follow it.");
            m!.Invoke(radio, new object[] { "mic", name });
        }

        // ------------------------------------------------------------------
        // THE pin: the radio's list wins, the operator's list is not consulted
        // ------------------------------------------------------------------

        [Fact]
        public void EnumerationComesFromTheRadio_NotTheOperatorsList()
        {
            var rig = MakeRig();
            // The operator has exactly one mic reference, and it is NOT one
            // of the radio's profiles. Before #418 this list was the answer.
            rig.Callouts.Profiles = new List<Profile_t>
            {
                new Profile_t("OperatorOnly", ProfileTypes.mic, true),
            };

            var radio = MakeOfflineRadio();
            RadioReportsMicList(radio, "Default^RadioOnly");
            rig.theRadio = radio;
            try
            {
                var list = rig.ReadRadioProfileList(ProfileTypes.mic, timeoutMs: 250);

                Assert.True(list.Reported);
                Assert.Equal(new[] { "Default", "RadioOnly" }, list.Names);
                Assert.DoesNotContain("OperatorOnly", list.Names);
            }
            finally { Detach(rig); }
        }

        // ------------------------------------------------------------------
        // The three states, end to end through the reading
        // ------------------------------------------------------------------

        [Fact]
        public void ARadioThatAnswersNone_IsReportedAsNone_NotAsUnasked()
        {
            var rig = MakeRig();
            var radio = MakeOfflineRadio();
            rig.theRadio = radio;

            try
            {
                // The answer arrives WHILE the reading waits — the
                // asynchronous arrival the fix exists to wait for, not read
                // past.
                var answer = System.Threading.Tasks.Task.Run(() =>
                {
                    Thread.Sleep(100);
                    RadioReportsMicList(radio, "");
                });
                var list = rig.ReadRadioProfileList(ProfileTypes.mic, timeoutMs: 4000);
                answer.Wait();

                Assert.True(list.Reported);
                Assert.True(list.FreshAnswer);
                Assert.Empty(list.Names);
                Assert.Null(list.Problem);

                var line = ProfileReporter.ProfileCountLine(list);
                Assert.True(line.Readable);
                Assert.Equal("0", line.Value);
            }
            finally { Detach(rig); }
        }

        [Fact]
        public void ARadioThatNeverAnswers_IsNotReportedAsZero()
        {
            var rig = MakeRig();
            rig.theRadio = MakeOfflineRadio(); // never reports any list
            try
            {
                var list = rig.ReadRadioProfileList(ProfileTypes.mic, timeoutMs: 200);

                Assert.False(list.Reported);
                Assert.False(list.CouldNotAsk);

                var line = ProfileReporter.ProfileCountLine(list);
                Assert.False(line.Readable);
                Assert.Null(line.Value);
                Assert.Contains("never reported", line.Problem);
            }
            finally { Detach(rig); }
        }

        [Fact]
        public void WithNoRadio_TheReadingSaysCouldNotAsk_AndReturnsWithoutServingTheWait()
        {
            var rig = MakeRig();

            var sw = Stopwatch.StartNew();
            var list = rig.ReadRadioProfileList(ProfileTypes.mic, timeoutMs: 5000);
            sw.Stop();

            Assert.True(list.CouldNotAsk);
            Assert.Equal("no radio connection", list.Problem);
            Assert.False(list.Reported);
            // With nobody to wait FOR, waiting the full bound would be five
            // seconds of theater; the reading returns at once.
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"a no-radio reading took {sw.ElapsedMilliseconds} ms — it waited a bound with nobody to answer");

            var line = ProfileReporter.ProfileCountLine(list);
            Assert.False(line.Readable);
            Assert.Equal("no radio connection", line.Problem);
        }

        [Fact]
        public void AListTheRadioSentEarlier_CountsAsReported_ButSaysTheRefreshExpired()
        {
            var rig = MakeRig();
            var radio = MakeOfflineRadio();
            // The list arrived BEFORE this reading (connect-time answer);
            // nothing will answer the fresh ask.
            RadioReportsMicList(radio, "Default");
            rig.theRadio = radio;
            try
            {
                var list = rig.ReadRadioProfileList(ProfileTypes.mic, timeoutMs: 200);

                Assert.True(list.Reported);
                Assert.False(list.FreshAnswer);
                Assert.Equal(new[] { "Default" }, list.Names);

                var line = ProfileReporter.ProfileCountLine(list);
                Assert.True(line.Readable);
                Assert.StartsWith("1 (", line.Value);
                Assert.Contains("earlier in this session", line.Value);
            }
            finally { Detach(rig); }
        }

        // ------------------------------------------------------------------
        // The three states render distinguishably — the file's promise
        // ------------------------------------------------------------------

        [Fact]
        public void TheThreeStates_RenderThreeVisiblyDifferentCountLines()
        {
            var reportedNone = new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Reported = true,
                FreshAnswer = true,
                Selection = "",
            };
            var neverReported = new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                WaitedMs = 5000,
            };
            var couldNotAsk = new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Problem = "no radio connection",
            };

            var rendered = new[] { reportedNone, neverReported, couldNotAsk }
                .Select(l =>
                {
                    var sb = new StringBuilder();
                    ProfileReporter.AppendSettingLines(sb,
                        new[] { ProfileReporter.ProfileCountLine(l) });
                    return sb.ToString().TrimEnd();
                })
                .ToArray();

            // Pairwise distinct — the collapse of these three into one "0"
            // is the exact defect #418 names.
            Assert.Equal(3, rendered.Distinct().Count());

            Assert.Equal("mic profiles stored = 0", rendered[0]);
            Assert.Contains("unreadable: the radio never reported its mic profile list (waited 5 seconds)", rendered[1]);
            Assert.Contains("unreadable: no radio connection", rendered[2]);
        }

        [Fact]
        public void TheTextExport_RendersAllThreeStatesDistinguishably()
        {
            string Render(FlexBase.RadioProfileList list)
            {
                var sb = new StringBuilder();
                ProfileReporter.AppendProfileNames(sb, list, "Mic profiles");
                return sb.ToString();
            }

            string reportedNone = Render(new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Reported = true,
                FreshAnswer = true,
                Selection = "",
            });
            string neverReported = Render(new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                WaitedMs = 5000,
                Selection = "",
            });
            string couldNotAsk = Render(new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Problem = "no radio connection",
            });

            Assert.Contains("the radio reports none stored", reportedNone);
            Assert.Contains("the radio never reported its list (waited 5 seconds)", neverReported);
            Assert.Contains("could not be read — no radio connection", couldNotAsk);

            // Each names its own state and no other's.
            Assert.DoesNotContain("none stored", neverReported);
            Assert.DoesNotContain("none stored", couldNotAsk);
            Assert.DoesNotContain("never reported", reportedNone);
        }

        [Fact]
        public void AReportedListOfNames_RendersTheNames_AndTheSelection()
        {
            var list = new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Reported = true,
                FreshAnswer = true,
                Names = new List<string> { "Default", "PR781" },
                Selection = "Default",
            };
            var sb = new StringBuilder();
            ProfileReporter.AppendProfileNames(sb, list, "Mic profiles");
            string text = sb.ToString();

            Assert.Contains("Mic profiles (loaded now: Default):", text);
            Assert.Contains("  - Default", text);
            Assert.Contains("  - PR781", text);
        }

        // ------------------------------------------------------------------
        // The cross-check: a contradiction is stated, never averaged away
        // ------------------------------------------------------------------

        [Fact]
        public void ZeroProfilesWithANamedSelection_IsStatedAsAContradiction()
        {
            // The exact shape of Don's radio seen through a blind list: the
            // fault was an empty mic SELECTION with a Default profile
            // demonstrably present. A list reporting zero while the radio
            // names a loaded profile proves the list wrong.
            var list = new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Reported = true,
                FreshAnswer = true,
                Selection = "Default",
            };

            Assert.True(list.SelectionContradictsCount);
            var line = ProfileReporter.ProfileCrossCheckLine(list);
            Assert.NotNull(line);
            Assert.True(line!.Readable);
            Assert.Contains("'Default'", line.Value);
            Assert.Contains("at least one exists", line.Value);
        }

        [Fact]
        public void TheCrossCheck_StaysQuietWhenThereIsNothingToReport()
        {
            // A healthy radio: names and a selection that is one of them.
            Assert.Null(ProfileReporter.ProfileCrossCheckLine(new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Reported = true,
                FreshAnswer = true,
                Names = new List<string> { "Default" },
                Selection = "Default",
            }));

            // Zero profiles, nothing selected: consistent, no contradiction.
            Assert.Null(ProfileReporter.ProfileCrossCheckLine(new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Reported = true,
                FreshAnswer = true,
                Selection = "",
            }));

            // Could not ask: the selection was never read either; a
            // contradiction cannot be claimed from two unread facts.
            Assert.Null(ProfileReporter.ProfileCrossCheckLine(new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Problem = "no radio connection",
            }));
        }

        [Fact]
        public void TheUnwalkedSection_CarriesTheContradiction()
        {
            var list = new FlexBase.RadioProfileList
            {
                ProfileType = ProfileTypes.mic,
                Reported = true,
                FreshAnswer = true,
                Selection = "Default",
            };
            var sb = new StringBuilder();
            ProfileReporter.AppendUnwalkedTypeSection(sb, list);
            string text = sb.ToString();

            Assert.StartsWith("[mic profiles not walked]", text);
            Assert.Contains("reason = the radio reports no stored mic profiles", text);
            Assert.Contains("contradiction = the radio names 'Default'", text);
        }

        // ------------------------------------------------------------------
        // The walk runs from the reading — and records it
        // ------------------------------------------------------------------

        [Fact]
        public void TheWalk_CarriesItsListReading_AndWalksNothingWithoutOne()
        {
            var rig = MakeRig();
            // Bait again: operator references that must not be walked.
            rig.Callouts.Profiles = new List<Profile_t>
            {
                new Profile_t("OperatorOnly", ProfileTypes.mic, true),
            };

            var walked = new List<string>();
            var outcome = ProfileReporter.WalkProfiles(rig, ProfileTypes.mic,
                walked.Add);

            Assert.NotNull(outcome.RadioList);
            Assert.True(outcome.RadioList.CouldNotAsk);
            Assert.Empty(walked);
            Assert.Equal(0, outcome.LoadAttempts);
        }

        // ------------------------------------------------------------------
        // THE SOURCE SCAN — the assertion nobody had
        // ------------------------------------------------------------------

        [Fact]
        public void TheEnumeration_DoesNotConsultTheOperatorsList()
        {
            string reporter = Read("Radios/ProfileReporter.cs");

            // Positive control FIRST: prove this scan sees the forbidden
            // tokens where they are known to live. FlexBase.cs defines the
            // operator-list default this test exists to keep out of the
            // reporter — if these lines ever fail, the scan itself is broken
            // or the operator-list layer was renamed, and either way the
            // DoesNotContain results below prove nothing.
            string flexBase = Read("Radios/FlexBase.cs");
            Assert.Contains("Callouts.Profiles", flexBase);
            Assert.Contains("GetProfilesByType", flexBase);

            // The reporter reads the radio, and only the radio.
            Assert.Contains("ReadRadioProfileList", reporter);
            Assert.DoesNotContain("GetProfilesByType", reporter);
            Assert.DoesNotContain("GetDefaultProfiles", reporter);
            Assert.DoesNotContain("Callouts", reporter);
        }

        // ------------------------------------------------------------------
        // Plumbing (same shape as RestoreGradeExportTests)
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
