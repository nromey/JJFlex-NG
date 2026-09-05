using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 45 Track C, #537. Binaural receive is reachable from three
    /// places, and all three say one sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What happened.</b> Binaural was wired end to end —
    /// <c>FlexBase.Binaural</c> through to FlexLib's <c>BinauralRX</c> — and
    /// reachable only by <c>Ctrl+B</c> inside the audio layer. Jim had a
    /// control for it; the WinForms-to-WPF move lost the control and kept the
    /// capability, and Sprint 44 gave the capability a key. A key you must
    /// already know about is not a feature anyone finds, so this sprint gave
    /// it a checkbox in Home's audio expander and a row on the Audio menu.
    /// </para>
    /// <para>
    /// <b>What this file refuses.</b> Not the missing control — that is back —
    /// but the failure that replaces it. Three surfaces for one radio flag is
    /// three chances to invent wording, and nothing fails when they disagree:
    /// each one works, each one announces something, and the operator simply
    /// hears the same switch described two ways depending on which door they
    /// came through. That already happened to mute (#313), where five of six
    /// call sites said a bare "Muted" while the sixth named the slice, and it
    /// took a year to notice.
    /// </para>
    /// <para>
    /// <b>Why a source test.</b> The surfaces live in JJFlexWpf, which this
    /// project does not reference and must not — constructing that assembly's
    /// types is what puts dialogs on the operator's desktop. Reading the
    /// source is how a safe test project asserts something about it, the same
    /// arrangement <see cref="SliceMuteAnnouncementTests"/> uses.
    /// </para>
    /// </remarks>
    public sealed class BinauralOneVocabularyTests
    {
        /// <summary>The one pair of strings, quoted as a call site writes them.</summary>
        private static readonly string[] SharedKeys =
        {
            "\"audio.binaural.on\"",
            "\"audio.binaural.off\"",
        };

        /// <summary>
        /// Every surface an operator can flip binaural from. Add a fourth door
        /// and add it here — a surface this list does not name is a surface
        /// free to grow its own wording, which is the entire defect.
        /// </summary>
        private static readonly string[] Surfaces =
        {
            // The audio layer's Ctrl+B (Sprint 44 Track N).
            "JJFlexWpf/KeyCommands.cs",
            // The Audio menu's Binaural row.
            "JJFlexWpf/NativeMenuBar.cs",
            // Home's "Audio and Slice" expander.
            "JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs",
        };

        [Fact]
        public void EverySurfaceThatFlipsBinauralNamesTheSameTwoStrings()
        {
            var missing = new List<string>();

            foreach (string surface in Surfaces)
            {
                string code = StripComments(Read(surface));
                foreach (string key in SharedKeys)
                {
                    if (!code.Contains(key, StringComparison.Ordinal))
                        missing.Add(surface + " does not name " + key);
                }
            }

            Assert.True(missing.Count == 0,
                "A binaural surface stopped speaking the shared sentence: "
                + string.Join("; ", missing)
                + ". One radio flag, three doors, one wording — audio.binaural.on and "
                + "audio.binaural.off. If a surface needs different words, change the "
                + "lexicon entry so every door changes with it, rather than composing a "
                + "second phrasing that agrees only by coincidence (#537).");
        }

        [Fact]
        public void TheTwoStringsExistInTheLexicon()
        {
            // The positive control for everything above: a key nothing defines
            // would be spoken as its own name, and every "the surfaces agree"
            // assertion would still pass.
            string audio = Read("Radios/Lexicon/audio.json");

            foreach (string quoted in SharedKeys)
            {
                string key = quoted.Trim('"');
                Assert.True(audio.Contains("\"" + key + "\":", StringComparison.Ordinal),
                    "The lexicon has no entry for " + key + ", so all three surfaces would "
                    + "speak the key itself.");
            }

            Assert.NotEqual(ValueOf(audio, "audio.binaural.on"),
                            ValueOf(audio, "audio.binaural.off"));
        }

        [Fact]
        public void TheCheckboxLabelAndTheSpokenSentenceUseTheSameNoun()
        {
            // The subtler half of one vocabulary. A checkbox called "Binaural
            // Receive" that announces "Binaural on" is two names for one
            // switch, and a screen reader reads the label on focus and the
            // sentence on the press — so an operator hears both, back to back,
            // and has to work out they are the same thing.
            string audio = Read("Radios/Lexicon/audio.json");

            string label = ValueOf(audio, "audio.fields.binaural");
            string on = ValueOf(audio, "audio.binaural.on");
            string off = ValueOf(audio, "audio.binaural.off");

            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.StartsWith(label, on, StringComparison.Ordinal);
            Assert.StartsWith(label, off, StringComparison.Ordinal);
        }

        [Fact]
        public void NoOtherShippedFileWritesTheWordsItself()
        {
            // The fourth door, written by someone who did not know about the
            // first three. A literal "Binaural on" in source is a second copy
            // of the sentence that no lexicon edit can reach.
            string on = ValueOf(Read("Radios/Lexicon/audio.json"), "audio.binaural.on");
            string off = ValueOf(Read("Radios/Lexicon/audio.json"), "audio.binaural.off");

            var offenders = new List<string>();
            int scanned = 0;

            foreach (string path in ShippedSourceFiles())
            {
                scanned++;
                string code = StripComments(File.ReadAllText(path));
                if (code.Contains("\"" + on + "\"", StringComparison.Ordinal) ||
                    code.Contains("\"" + off + "\"", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/'));
                }
            }

            // A sweep that read nothing reports no offenders and looks exactly
            // like a clean result.
            Assert.True(scanned > 200,
                "only " + scanned + " source files scanned — the walk is broken, so its "
                + "silence means nothing");

            Assert.True(offenders.Count == 0,
                "The binaural sentence is written out as a literal in: "
                + string.Join(", ", offenders)
                + ". Ask the lexicon for audio.binaural.on instead, so the wording has "
                + "one home (#537). (The key is named without a call here on purpose: "
                + "the coverage scanner in LexiconKeyCoverageTests reads the first "
                + "argument of every lexicon call it finds in source, and a call spelled "
                + "out inside a message string reads to it as a real one.)");
        }

        [Fact]
        public void TheAudioMenuCarriesABinauralRowAndItNeedsARadio()
        {
            var row = AudioMenuLayout.Entries.SingleOrDefault(e => e.Id == "binaural");

            Assert.NotNull(row);
            Assert.Equal(AudioMenuEntryKind.Toggle, row!.Kind);

            // Radio-gated like every other row that writes to the radio, so it
            // is greyed with its own reason rather than absent — an absent row
            // moves every position after it, which is #214.
            Assert.True(row.NeedsRadio);

            // Its first letter is its own. Two rows sharing one letter turns a
            // press into a cycle, and this menu has already been through that
            // with two rows starting "Audio" (#297).
            var firsts = AudioMenuLayout.Entries
                .Where(e => e.Kind != AudioMenuEntryKind.Separator)
                .Select(e => char.ToUpperInvariant(e.Label[0]))
                .ToList();

            Assert.Single(firsts.Where(c => c == 'B'));
        }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The value of a plain <c>"key": "value"</c> line in a lexicon
        /// partition. Enough for these entries, which are all plain strings —
        /// a verbosity ladder would come back empty and fail loudly rather
        /// than quietly comparing nothing.
        /// </summary>
        private static string ValueOf(string json, string key)
        {
            string needle = "\"" + key + "\":";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, "no lexicon entry for " + key);

            int open = json.IndexOf('"', at + needle.Length);
            Assert.True(open >= 0, "no value for " + key);

            int close = json.IndexOf('"', open + 1);
            Assert.True(close > open, "unterminated value for " + key);

            string value = json[(open + 1)..close];
            Assert.False(string.IsNullOrWhiteSpace(value), key + " resolves to nothing");
            return value;
        }

        /// <summary>
        /// Hand-written C# and VB that ships. Tests are excluded on purpose:
        /// an expectation naming the sentence is what a test is FOR.
        /// </summary>
        private static IEnumerable<string> ShippedSourceFiles()
        {
            string root = RepoRoot();
            foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(path);
                if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".vb", StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("FlexLib_API/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("PortAudioSharp-src", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains(".Tests/", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return path;
            }
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject passes every absence check it makes.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Drop comments, so prose ABOUT the strings — of which the surfaces
        /// are now full — does not read as a use of one.
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
                if (trimmed.StartsWith("'", StringComparison.Ordinal)) continue;

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
            // Both ends of it. Over-eager and the absence check studies an
            // empty string; broken and the surfaces' own explanatory comments
            // read as uses, so a door that stopped speaking the sentence still
            // passes.
            string raw = Read("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs");
            string code = StripComments(raw);

            // Structural rather than a quoted phrase, so rewording a comment
            // cannot fail this for no reason.
            int commentLines = raw.Split('\n').Count(
                l => l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            Assert.True(commentLines > 20,
                "this file is meant to be heavily commented; only " + commentLines
                + " comment lines found, so the stripper is being asked to do nothing");

            Assert.DoesNotContain(code.Split('\n'),
                l => l.TrimStart().StartsWith("//", StringComparison.Ordinal));

            Assert.Contains("BuildAudioControls", code, StringComparison.Ordinal);
            Assert.True(code.Length > raw.Length / 4,
                "Stripping comments removed more than three quarters of the file ("
                + raw.Length + " to " + code.Length + " characters), which means it ate code "
                + "and the checks above are verifying far less than they appear to.");
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
