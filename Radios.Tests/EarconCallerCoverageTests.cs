using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every sound the catalogue advertises is either played by the running
    /// application or is on a list saying why not. This is what notices when a
    /// new one is neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Task #383, and it is the third of its kind.</b> Three connect sounds
    /// were found in two days that had never once been emitted. <b>#369</b> —
    /// <c>ConnectSuccessTone</c> never played between 2026-08-07 and
    /// 2026-08-28, because the guard in front of it tested a flag raised by a
    /// side effect thirty-six lines earlier. <b>#379</b> — and *because* it
    /// never played, nobody could discover it was the phase-2 tone with the
    /// volume up; it shipped, was believed correct, and its first playback was
    /// also the first evidence it was wrong. <b>#383</b> — the phase-1 rung,
    /// written, catalogued, described in the UI, never once emitted.
    /// </para>
    /// <para>
    /// <b>A sound that never sounds passes every review.</b> It compiles. It
    /// has a test, if anyone wrote one. It appears in the Earcon Explorer,
    /// where a bench press proves the METHOD works — and none of that touches
    /// the question of whether anything calls it. The Explorer is actively part
    /// of the trap: it makes an unreachable earcon audible and therefore
    /// believable. That is why the two audition surfaces are excluded from
    /// counting as callers below; a sound whose only player is the button that
    /// exists to demonstrate sounds has no production path at all.
    /// </para>
    /// <para>
    /// A source scan rather than reflection, for the same reason
    /// <see cref="AudioGateCoverageTests"/> is one: the thing being checked is
    /// "did anyone write the call", and the failure has to name a file somebody
    /// can open. It also keeps this test in <c>Radios.Tests</c>, which does not
    /// reference <c>JJFlexWpf</c> and must not start to.
    /// </para>
    /// <para>
    /// <b>The list is two-tiered on purpose.</b> <see cref="AuditionOnly"/> is
    /// a decision — these are meant to be reachable only from the bench, and
    /// each entry says why. <see cref="UnwiredAwaitingDecision"/> is a defect
    /// register — these have no caller and nobody has ruled on them yet, and an
    /// entry here is a promise to come back, not an excuse. Both are checked in
    /// BOTH directions by
    /// <see cref="NothingOnTheAuditionListHasQuietlyGainedACaller"/>, so a
    /// sound that later gets wired cannot linger here pretending to be
    /// deliberate — the same self-checking shape as the unbound-key roster in
    /// <c>KeyCommands.ValidateUnboundAnnotations</c>, and for the same reason:
    /// a table nobody verifies rots into a description-drift defect within two
    /// sprints.
    /// </para>
    /// </remarks>
    public sealed class EarconCallerCoverageTests
    {
        /// <summary>The declaring file. Its own calls never count as callers.</summary>
        private const string PlayerFile = @"JJFlexWpf\EarconPlayer.cs";

        /// <summary>
        /// Surfaces that exist to make sounds audible on demand. A sound whose
        /// only player is one of these is not reachable in the field, which is
        /// exactly the thing #383 is about — so they are not callers.
        /// </summary>
        private static readonly string[] AuditionSurfaces =
        {
            @"JJFlexWpf\EarconCatalog.cs",                             // the registry itself
            @"JJFlexWpf\Dialogs\AudioWorkshopDialog.Earcons.cs",       // the Earcon Explorer
            @"JJFlexWpf\Dialogs\EarconScratchpadDialog.xaml.cs",       // the scratchpad bench
        };

        /// <summary>
        /// Sounds that are reachable only from the bench BY DECISION. Each
        /// entry carries the reason, because a bare name on an exclusion list
        /// is indistinguishable from an oversight six months later.
        /// </summary>
        private static readonly Dictionary<string, string> AuditionOnly = new(StringComparer.Ordinal)
        {
            ["ConnectPhase1Tone"] =
                "#383, ruled by Noel 2026-08-29: the bottom rung of the connect ladder does " +
                "NOT sound. ConnectNarration starts at phase 1 and only announces transitions, " +
                "so a real connect starts at step 2. Kept so the ladder is legible when " +
                "auditioned, and against the day the connecting window announces its opening " +
                "phase. Do not 'fix' this by making phase 1 fire.",

            ["ConnectPhase2Tone"] =
                "The sound IS played in the field, by ConnectPhaseTone(2) from " +
                "ConnectingForm.vb. This no-argument twin exists so the ladder has one " +
                "Explorer row per rung; it duplicates a live path rather than describing a " +
                "dead one.",

            ["ConnectPhase3Tone"] =
                "Same as ConnectPhase2Tone: played in the field via ConnectPhaseTone(3).",

            ["FeatureOnToneThreeNoteCandidate"] =
                "#114: a three-note candidate for the confirmation tone, parked for Noel's " +
                "ears. Its own label says 'candidate'. It becomes live or it goes, when that " +
                "is decided.",

            ["FeatureOffToneThreeNoteCandidate"] =
                "The inverse of FeatureOnToneThreeNoteCandidate, same decision.",

            ["FilterSqueezeTone"] =
                "Auditioned from the earcon scratchpad only. Kept as a candidate shape for " +
                "filter-width feedback; nothing on the tuning path has adopted it.",

            ["FilterStretchTone"] =
                "The inverse of FilterSqueezeTone, same status.",
        };

        /// <summary>
        /// Sounds with no caller and no ruling. <b>This list should be empty
        /// and is not.</b> An entry is a defect awaiting a decision — wire it,
        /// or move it to <see cref="AuditionOnly"/> with a reason, or delete
        /// the sound. It is here rather than failing the build because the
        /// decision is not this track's to make; it is not here to be
        /// comfortable.
        /// </summary>
        private static readonly Dictionary<string, string> UnwiredAwaitingDecision =
            new(StringComparer.Ordinal)
        {
            ["DialogOpenTone"] =
                "FOUND 2026-09-01 by the Sprint 43 Track D inventory, and it is the #383 " +
                "shape again: zero callers anywhere in the tree, including inside " +
                "EarconPlayer. Meanwhile the 'Dialogs and panels' family switch in Settings " +
                "and docs/help/md/audio-earcon-control.md both promise the operator 'the " +
                "dings when a dialog opens or closes'. Two shipped surfaces describe a sound " +
                "the application cannot make. Only PlayExpand / PlayCollapse / " +
                "PlayCollapseAll in that family are live.",

            ["DialogCloseTone"] =
                "The other half of DialogOpenTone, identical status and same report.",
        };

        [Fact]
        public void EveryCataloguedEarconIsPlayedBySomethingOrSaysWhyNot()
        {
            var attributed = AttributedEarcons();

            // Positive control FIRST. A scan that parses nothing reports "all
            // clear" in exactly the same words as a scan that found everything
            // wired, and #383 exists because a sound looked fine from every
            // angle except the one nobody checked.
            Assert.True(attributed.Count >= 40,
                "Only " + attributed.Count + " [Earcon] attributes parsed out of " +
                PlayerFile + " and there are more than forty. The attribute shape has " +
                "changed and this rule is proving nothing.");

            Assert.Contains("ConnectSuccessTone", attributed);
            Assert.Contains("ConnectPhase1Tone", attributed);

            var callers = CallerIndex(attributed);

            // The control that matters: the scan must be able to FIND a caller
            // it is known to have. ConnectSuccessTone is played from
            // MainWindow; if this comes back empty the matcher is broken and
            // every "no caller" below is a phantom.
            Assert.True(callers["ConnectSuccessTone"].Count > 0,
                "The scan found no caller for ConnectSuccessTone, which MainWindow plays on " +
                "every successful connect. The call matcher has stopped matching, so every " +
                "'no caller' this test reports is a false alarm.");

            var unexplained = new List<string>();
            foreach (string name in attributed.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (callers[name].Count > 0) continue;
                if (AuditionOnly.ContainsKey(name)) continue;
                if (UnwiredAwaitingDecision.ContainsKey(name)) continue;
                unexplained.Add(name);
            }

            Assert.True(unexplained.Count == 0,
                "These sounds are catalogued, described to the operator in the Earcon " +
                "Explorer and the per-sound loudness picker, and NOTHING IN THE APPLICATION " +
                "PLAYS THEM:\r\n  " + string.Join("\r\n  ", unexplained) +
                "\r\nA sound that never sounds passes every review — it compiles, it has a " +
                "bench button that proves the method works, and none of that is a caller. " +
                "Either call it from the path it was written for, or add it to AuditionOnly " +
                "with the reason, or delete it. See task #383, and #369 and #379 before it.");
        }

        /// <summary>
        /// The lists, checked in the other direction. A sound that has since
        /// been wired must come OFF them, or the next reader learns that a live
        /// sound is bench-only and believes it.
        /// </summary>
        [Fact]
        public void NothingOnTheAuditionListHasQuietlyGainedACaller()
        {
            var attributed = AttributedEarcons();
            var callers = CallerIndex(attributed);

            var stale = new List<string>();
            var missing = new List<string>();

            foreach (var list in new[] { AuditionOnly, UnwiredAwaitingDecision })
            {
                foreach (var entry in list)
                {
                    if (!attributed.Contains(entry.Key))
                    {
                        missing.Add(entry.Key);
                        continue;
                    }

                    // ConnectPhase2Tone and ConnectPhase3Tone are listed for a
                    // different reason than "nothing plays them" — the sound
                    // has a live path under another name — so a caller
                    // appearing for them is not news.
                    if (entry.Key is "ConnectPhase2Tone" or "ConnectPhase3Tone") continue;

                    if (callers[entry.Key].Count > 0)
                        stale.Add(entry.Key + "  (now called from " +
                                  string.Join(", ", callers[entry.Key]) + ")");
                }
            }

            Assert.True(stale.Count == 0,
                "These sounds are listed here as unplayed and something now plays them:\r\n  " +
                string.Join("\r\n  ", stale) +
                "\r\nTake them off the list. Leaving them on teaches the next reader that a " +
                "live sound is bench-only, which is the description drift this whole file " +
                "exists to catch.");

            Assert.True(missing.Count == 0,
                "These names are on the audition lists and no longer carry an [Earcon] " +
                "attribute, so the entry explains nothing:\r\n  " +
                string.Join("\r\n  ", missing) +
                "\r\nThe sound was renamed or deleted; update the list to match.");
        }

        /// <summary>The audition surfaces are real files. If one is renamed the
        /// exclusion silently starts excluding nothing, and every bench-only
        /// sound would then look wired.</summary>
        [Fact]
        public void TheAuditionSurfacesStillExist()
        {
            foreach (string rel in AuditionSurfaces)
                Assert.True(File.Exists(Path.Combine(IntegrationPassTree.Root, rel)),
                    rel + " is not in the tree, so excluding it from the earcon caller rule " +
                    "excludes nothing and a bench-only sound would read as wired. Update " +
                    "AuditionSurfaces to the file's new name.");

            Assert.True(File.Exists(Path.Combine(IntegrationPassTree.Root, PlayerFile)),
                PlayerFile + " is not in the tree, so this rule has nothing to parse.");
        }

        // ------------------------------------------------------------------

        /// <summary>Method names carrying an <c>[Earcon(...)]</c> attribute.</summary>
        private static readonly Regex Attributed = new(
            @"\[Earcon\((?:[^\[\]]|\[[^\]]*\])*?\)\]\s*" +
            @"(?:public|internal|private)[^\n(]*?\s(\w+)\s*\(",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static HashSet<string> AttributedEarcons()
        {
            string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, PlayerFile));
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Attributed.Matches(src)) names.Add(m.Groups[1].Value);
            return names;
        }

        /// <summary>
        /// name -> the files that call it, excluding the declaring file, the
        /// audition surfaces and the tests.
        /// </summary>
        private static Dictionary<string, List<string>> CallerIndex(HashSet<string> names)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string n in names) index[n] = new List<string>();

            var skip = new HashSet<string>(AuditionSurfaces, StringComparer.OrdinalIgnoreCase)
            {
                PlayerFile,
            };

            foreach (string file in IntegrationPassTree.AuthoredSource)
            {
                if (IntegrationPassTree.IsTest(file)) continue;
                string rel = IntegrationPassTree.Relative(file);
                if (skip.Contains(rel)) continue;
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)) continue;

                string text;
                try { text = File.ReadAllText(file); } catch { continue; }

                if (!names.Any(n => text.Contains(n, StringComparison.Ordinal))) continue;
                string code = StripCommentsAndStrings(text, file);

                foreach (string n in names)
                {
                    // Any mention IN CODE counts, not just `Name(`.
                    //
                    // The first version of this required an open paren and
                    // reported five live sounds as unplayed —
                    // CountdownRecordTone, CountdownTransmitTone,
                    // FilterEdgeEnterTone, FilterEdgeExitTone and
                    // FixerStageDoneTone. Every one is passed as a METHOD
                    // GROUP: `Guarded(EarconPlayer.CountdownRecordTone, ...)`
                    // and `entryEarcon: EarconPlayer.FilterEdgeEnterTone`.
                    // A delegate handed to something that will call it later is
                    // a caller; insisting on parentheses is the same
                    // literal-minded matching that made the earlier passes of
                    // task #110 produce mostly phantoms.
                    //
                    // Comments AND string literals are blanked first, so
                    // neither a doc-comment cross reference nor the
                    // `"CountdownRecordTone"` label sitting beside the real
                    // reference can pass as a use.
                    if (Regex.IsMatch(code, @"(?<![\w.])(?:[\w]+\.)*" + Regex.Escape(n) + @"\b"))
                        index[n].Add(rel);
                }
            }
            return index;
        }

        /// <summary>Blanks comments and string literals while preserving
        /// offsets, so a name that appears only in prose — or only as its own
        /// label in a diagnostic string — is not counted as a use.</summary>
        private static string StripCommentsAndStrings(string text, string file)
        {
            string[] patterns = file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
                ? new[] { @"'[^\r\n]*", "\"[^\"\r\n]*\"" }
                : new[] { @"//[^\r\n]*", @"/\*.*?\*/", @"""(?:\\.|[^""\\\r\n])*""" };

            var chars = text.ToCharArray();
            foreach (string p in patterns)
                foreach (Match m in Regex.Matches(text, p, RegexOptions.Singleline))
                    for (int i = m.Index; i < m.Index + m.Length; i++)
                        if (chars[i] != '\n' && chars[i] != '\r') chars[i] = ' ';
            return new string(chars);
        }
    }
}
