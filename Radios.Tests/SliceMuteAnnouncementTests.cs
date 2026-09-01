using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 43 Track E, #313. Every mute announcement names the slice it
    /// happened to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this refuses.</b> Six call sites toggled the active
    /// slice's mute and five of them said a bare "Muted". On a multi-slice
    /// radio "which one" is the entire question the operator is asking, so
    /// "Muted" answers the one they did not ask — and because one door DID
    /// name the slice, the same operation announced itself two different ways
    /// depending on how it was invoked. The correctly-named strings had been
    /// in the lexicon, unused by five of the six, since 2026-02: somebody
    /// added the right string and did not finish the sweep.
    /// </para>
    /// <para>
    /// <b>Why a source test.</b> The call sites live in JJFlexWpf, which this
    /// project does not reference and must not — constructing that assembly's
    /// types is what puts dialogs on the operator's desktop. Reading the
    /// source is how a safe test project asserts something about it, the same
    /// arrangement <see cref="MeterSpeechSummarySourceTests"/> uses.
    /// </para>
    /// <para>
    /// <b>What this does NOT claim.</b> It cannot prove the sentence is
    /// pleasant, only that no surface reaches past the named strings for the
    /// anonymous ones. That is the exact failure that shipped, and it is the
    /// one a comment saying "keep these in step" has historically failed to
    /// prevent in this codebase.
    /// </para>
    /// </remarks>
    public sealed class SliceMuteAnnouncementTests
    {
        /// <summary>
        /// The anonymous strings. Naming a slice is what these four cannot do,
        /// which is why no mute announcement may reach for them again. They
        /// are left in the lexicon deliberately — removing them is a change to
        /// a shared file for no behavioural gain — so the guard has to be here.
        /// </summary>
        private static readonly string[] AnonymousKeys =
        {
            "\"settings.slice.muted\"",
            "\"settings.slice.unmuted\"",
            "\"audio.mute.muted\"",
            "\"audio.mute.unmuted\"",
        };

        /// <summary>
        /// The named strings, and the positive control for the whole file. An
        /// absence check on a file that had been emptied, renamed or resolved
        /// to the wrong path would pass every assertion below while verifying
        /// nothing; these make the scan find something known to be there first.
        /// </summary>
        private static readonly string[] NamedKeys =
        {
            "\"settings.slice.muted_named\"",
            "\"settings.slice.unmuted_named\"",
            "\"audio.mute.slice_muted\"",
            "\"audio.mute.slice_unmuted\"",
        };

        private static readonly string[] Surfaces =
        {
            "JJFlexWpf/FreqOutHandlers.cs",
            "JJFlexWpf/KeyCommands.cs",
            "JJFlexWpf/NativeMenuBar.cs",
        };

        [Fact]
        public void NoMuteAnnouncementReachesPastTheNamedStrings()
        {
            var offenders = new List<string>();

            foreach (string surface in Surfaces)
            {
                string code = StripComments(Read(surface));
                foreach (string key in AnonymousKeys)
                {
                    if (code.Contains(key, StringComparison.Ordinal))
                        offenders.Add(surface + " uses " + key);
                }
            }

            Assert.True(offenders.Count == 0,
                "A mute announcement is back to saying a bare \"Muted\": "
                + string.Join("; ", offenders)
                + ". The named strings (settings.slice.muted_named, "
                + "audio.mute.slice_muted) already exist and already carry the letter. "
                + "On a multi-slice radio WHICH SLICE is the whole question, and an "
                + "operator who cannot see the screen has no second way to ask (#313).");
        }

        [Fact]
        public void TheNamedStringsAreActuallyInUse()
        {
            var text = string.Join("\n", Surfaces.Select(Read));

            var missing = NamedKeys
                .Where(key => !text.Contains(key, StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "These named mute strings are no longer used by any announcement surface: "
                + string.Join(", ", missing)
                + ". Either the announcement stopped naming its slice, or the wording moved "
                + "somewhere this test cannot see — in which case point it at the new home "
                + "rather than deleting it, because without this the absence check above is "
                + "verifying an empty set.");
        }

        [Fact]
        public void EveryNamedStringExistsInTheLexiconAndCarriesTheLetter()
        {
            string audio = Read("Radios/Lexicon/audio.json");
            string settings = Read("Radios/Lexicon/settings.json");
            string both = audio + "\n" + settings;

            foreach (string quoted in NamedKeys)
            {
                string key = quoted.Trim('"');
                Assert.True(both.Contains("\"" + key + "\":", StringComparison.Ordinal),
                    "The lexicon has no entry for " + key + ", so every announcement using it "
                    + "would speak the key itself.");
            }

            // The letter placeholder is the point of these entries. An entry
            // reworded to drop it would satisfy the existence check above while
            // putting the operator back where they started.
            foreach (string line in both.Split('\n'))
            {
                foreach (string quoted in NamedKeys)
                {
                    string key = quoted.Trim('"');
                    if (!line.Contains("\"" + key + "\":", StringComparison.Ordinal)) continue;
                    Assert.True(line.Contains("{letter}", StringComparison.Ordinal),
                        key + " no longer carries {letter}, so it names no slice: " + line.Trim());
                }
            }
        }

        // ────────────────────────────────────────────────────────────────

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject passes every absence check it makes.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Drop comments so prose ABOUT the anonymous strings — of which the
        /// helper's own doc comment is full — does not read as a use of one.
        /// </summary>
        private static string StripComments(string text)
        {
            var kept = new List<string>();
            bool inBlock = false;

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimStart();

                if (inBlock)
                {
                    if (trimmed.Contains("*/", StringComparison.Ordinal)) inBlock = false;
                    continue;
                }

                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                if (trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    if (!trimmed.Contains("*/", StringComparison.Ordinal)) inBlock = true;
                    continue;
                }

                int slashes = line.IndexOf("//", StringComparison.Ordinal);
                kept.Add(slashes >= 0 ? line[..slashes] : line);
            }

            return string.Join("\n", kept);
        }

        [Fact]
        public void TheCommentStripperKeepsCodeAndDropsProse()
        {
            string raw = Read("JJFlexWpf/FreqOutHandlers.cs");
            string code = StripComments(raw);

            // The helper's doc comment names the anonymous string in prose. If
            // the stripper stopped working, the absence test would fail on that
            // sentence and look like a real regression; if it became over-eager
            // and returned nothing, the absence test would pass while checking
            // an empty string. This pins both ends.
            Assert.Contains("ToggleSliceMuteAndAnnounce", code, StringComparison.Ordinal);
            Assert.True(code.Length > raw.Length / 4,
                "Stripping comments removed more than three quarters of the file ("
                + raw.Length + " to " + code.Length + " characters), which means it ate code "
                + "and the absence check is verifying far less than it appears to.");
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
