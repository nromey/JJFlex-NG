using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every sound the app makes goes through one gate, and this is what says
    /// so about a sound added tomorrow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Task #233.</b> Tier 1 makes itself silent by turning
    /// <c>OutputChannelRecorder.RenderEnabled</c> off, on the strength of a
    /// claim that everything which opens an audio device consults it. The claim
    /// was nearly true and the exception was the interesting part:
    /// <c>ClusterDialog</c> called <c>Console.Beep()</c> raw, two files away
    /// from <c>EarconPlayer.FallbackBeep</c>, which carries the rule verbatim —
    /// "Console.Beep is still an audio device as far as a blind operator's ears
    /// are concerned". Nothing failed. The suppression reported itself complete
    /// and the dialog could still be heard.
    /// </para>
    /// <para>
    /// That is the defect class this sprint is named for: an instrument
    /// reporting a state it has not established. A guard that covers most sound
    /// is more dangerous than no guard, because it is trusted — so the coverage
    /// is now a standing assertion rather than a sentence in a doc comment.
    /// </para>
    /// <para>
    /// <b>Scoped to assemblies that CAN consult the gate.</b>
    /// <c>OutputChannelRecorder</c> lives in <c>Radios</c>, and two projects
    /// that make sound do not reference it — see
    /// <see cref="UnreachableProjects"/>. Excluding them is a structural fact,
    /// not a suppression, and <see cref="TheExcludedProjectsStillCannotSeeTheGate"/>
    /// is what stops it quietly becoming one.
    /// </para>
    /// <para>
    /// A source scan and not reflection, because the thing being checked is
    /// "did the author write the gate next to the call" and the failure message
    /// has to name a file and a line somebody can open.
    /// </para>
    /// </remarks>
    public sealed class AudioGateCoverageTests
    {
        /// <summary>
        /// Calls that put sound on the machine's audio device without going
        /// through <c>EarconPlayer</c> or <c>ScreenReaderOutput</c>.
        /// </summary>
        private static readonly Regex RawAudio = new(
            @"\b(Console\.Beep\s*\(|SystemSounds\.\w+\s*\.\s*Play\s*\(|new\s+SoundPlayer\s*\(|MessageBeep\s*\(|PlaySound\s*\()",
            RegexOptions.Compiled);

        /// <summary>The gate itself.</summary>
        private const string Gate = "RenderEnabled";

        /// <summary>
        /// How far above a call site the gate may sit and still count. Small on
        /// purpose: a gate ten methods away is not a gate on this call, and the
        /// remedy for a false positive is to move the check next to the sound,
        /// which is where a reader looks for it anyway.
        /// </summary>
        private const int GateWindowLines = 12;

        /// <summary>
        /// Projects whose assemblies do not reference <c>Radios</c>, so the gate
        /// is not in scope for them at all. Each still makes sound, and that is
        /// worth knowing rather than hiding — named in the report for Track G,
        /// owned by nobody yet.
        /// </summary>
        private static readonly string[] UnreachableProjects = { "JJArclusterLib", "JJLogLib" };

        [Fact]
        public void EverySoundTheAppMakesIsBehindTheRenderGate()
        {
            var findings = new List<string>();
            var gated = new List<string>();

            foreach (string file in InScopeSource())
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsComment(lines[i])) continue;
                    if (!RawAudio.IsMatch(lines[i])) continue;

                    string where = IntegrationPassTree.Relative(file) + ":" + (i + 1);
                    if (GateIsNearby(lines, i)) gated.Add(where);
                    else findings.Add(where + "  " + lines[i].Trim());
                }
            }

            // The finding first, so the common failure reads as the problem it
            // is rather than as a broken matcher.
            Assert.True(findings.Count == 0,
                "These calls put sound on the operator's audio device without consulting " +
                "OutputChannelRecorder." + Gate + ", so a run that has turned rendering off — " +
                "and reported itself silent — can still be heard from here:\r\n  " +
                string.Join("\r\n  ", findings) +
                "\r\nPut the check immediately above the call, as EarconPlayer.FallbackBeep does. " +
                "See task #233.");

            // Then the positive control, which is what makes the line above
            // mean something. A scan that found nothing reads identically
            // whether every sound is gated or the pattern stopped matching;
            // these are the call sites that exist today and are correctly
            // gated, and the rule has to be able to see them.
            Assert.True(gated.Count >= 3,
                "The scan found only " + gated.Count + " gated audio call sites and there are at " +
                "least three. The pattern has stopped matching, so this rule is proving nothing. " +
                "Found: " + string.Join(", ", gated));
        }

        /// <summary>
        /// The exclusion, kept honest. The day one of these projects gains a
        /// reference to <c>Radios</c> the exclusion stops being structural and
        /// becomes a suppression, and this is what notices.
        /// </summary>
        [Fact]
        public void TheExcludedProjectsStillCannotSeeTheGate()
        {
            foreach (string project in UnreachableProjects)
            {
                string dir = Path.Combine(IntegrationPassTree.Root, project);
                Assert.True(Directory.Exists(dir),
                    project + " is not in the tree, so excluding it from the audio rule excludes " +
                    "nothing and the exclusion should be deleted.");

                string[] projectFiles = Directory.GetFiles(dir, "*.*proj");
                Assert.NotEmpty(projectFiles);

                foreach (string proj in projectFiles)
                {
                    Assert.DoesNotContain(@"Radios\Radios.csproj", File.ReadAllText(proj),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// The rule, shown to go red. The two assertions above only ever report
        /// the tree as it is; this one hands the matcher a call it must catch.
        /// </summary>
        [Fact]
        public void AnUngatedSoundIsFoundAndAGatedOneIsNot()
        {
            string[] ungated =
            {
                "private static void Ding()",
                "{",
                "    Console.Beep(880, 250);",
                "}",
            };
            Assert.True(RawAudio.IsMatch(ungated[2]));
            Assert.False(GateIsNearby(ungated, 2),
                "The matcher reported a gate above a call that has none, so it would pass an " +
                "ungated sound.");

            string[] gated =
            {
                "private static void Ding()",
                "{",
                "    if (!Radios.OutputChannelRecorder.RenderEnabled) return;",
                "    Console.Beep(880, 250);",
                "}",
            };
            Assert.True(GateIsNearby(gated, 3));

            // A comment ABOUT the gate is not the gate. This is the case the
            // deliberate break found: the real doc comment above
            // ClusterDialog.SpotBeep names RenderEnabled, and counting it kept
            // the rule green with the actual check deleted.
            string[] commentedOnly =
            {
                "/// <remarks>It used to skip OutputChannelRecorder.RenderEnabled.</remarks>",
                "private static void Ding()",
                "{",
                "    Console.Beep(880, 250);",
                "}",
            };
            Assert.False(GateIsNearby(commentedOnly, 3),
                "A doc comment naming the gate was accepted in place of the gate.");

            // And the far-away case, which must NOT count: a gate is a gate on
            // the call it stands next to.
            var distant = new List<string> { "if (!OutputChannelRecorder.RenderEnabled) return;" };
            for (int i = 0; i < GateWindowLines + 2; i++) distant.Add("    DoSomethingElse();");
            distant.Add("    Console.Beep();");
            Assert.False(GateIsNearby(distant.ToArray(), distant.Count - 1));
        }

        /// <summary>
        /// True when executable code within <see cref="GateWindowLines"/> above
        /// the call consults the gate.
        /// </summary>
        /// <remarks>
        /// <b>Comment lines are skipped, and finding that out cost a run.</b>
        /// The first version of this counted any line at all, so the doc comment
        /// written ABOVE <c>ClusterDialog.SpotBeep</c> — explaining that the
        /// call had once failed to consult <c>RenderEnabled</c> — read as a
        /// gate. Deleting the real check left the rule green. A rule that
        /// accepts a sentence about the gate in place of the gate is precisely
        /// the instrument this file exists to stop, and only the deliberate
        /// break revealed it.
        /// </remarks>
        private static bool GateIsNearby(IReadOnlyList<string> lines, int index)
        {
            for (int i = Math.Max(0, index - GateWindowLines); i <= index; i++)
            {
                if (IsComment(lines[i])) continue;
                if (lines[i].Contains(Gate, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool IsComment(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith("'", StringComparison.Ordinal)
                || t.StartsWith("*", StringComparison.Ordinal);
        }

        private static IEnumerable<string> InScopeSource()
            => IntegrationPassTree.AuthoredSource
                .Where(f => !IntegrationPassTree.IsTest(f))
                .Where(f => !UnreachableProjects.Any(p =>
                    IntegrationPassTree.Relative(f)
                        .Replace('/', '\\')
                        .StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)));
    }
}
