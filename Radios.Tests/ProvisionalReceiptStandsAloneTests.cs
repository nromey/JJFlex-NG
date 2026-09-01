using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The persistence receipt must make sense arriving alone, seconds late.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #442. The receipt is spoken with <c>SpeechIntent.Queue</c> from two
    /// places in <c>FlexBase</c>, so an interrupt can flush it out of the
    /// reader's queue and the arbiter will SALVAGE it — re-speaking it up to
    /// <see cref="Speech.SpeechArbiter.MaxSalvages"/> times, as late as
    /// <see cref="Speech.SpeechArbiter.SalvageAgeMultiple"/> times its
    /// estimated speaking length allows. That machinery is right and is not
    /// what this file is about.
    /// </para>
    /// <para>
    /// What it is about: on 2026-08-31 the sentence read <i>"This will not
    /// survive disconnect unless you save the profile"</i> and arrived 14.9
    /// seconds after its first emission, behind a tune and an SWR reading.
    /// "This" pointed at an announcement the operator had long since moved past.
    /// A fragment with no referent is worse than silence, because the operator
    /// stops to work out what it meant.
    /// </para>
    /// <para>
    /// A comment asking the next author to keep the sentence self-contained
    /// would be ignored the way every such comment in this codebase eventually
    /// is. So the rule is pinned here instead: <b>a salvageable receipt may not
    /// open with a word whose meaning is the utterance before it.</b> The
    /// wording is Noel's to choose; the property is not negotiable.
    /// </para>
    /// </remarks>
    public sealed class ProvisionalReceiptStandsAloneTests
    {
        private const string SettingsJson = "Radios/Lexicon/settings.json";
        private const string ReceiptKey = "settings.slice.provisional_change_receipt";

        /// <summary>
        /// The sentence exactly as it stood when it was reported, kept so the
        /// check below can be shown catching something it is known to catch.
        /// </summary>
        private const string TheSentenceThatCausedThis =
            "This will not survive disconnect unless you save the profile.";

        /// <summary>
        /// Openers that mean nothing without the utterance in front of them.
        /// Not a grammar checker — a short list of the words that actually
        /// produce a dangling fragment when a rescue arrives late.
        /// </summary>
        private static readonly string[] NeedsAnAntecedent =
        {
            "this", "that", "these", "those", "it", "they", "them",
            "its", "their", "which", "here", "there", "so", "and", "but",
        };

        [Fact]
        public void TheReceiptDoesNotOpenWithAWordThatNeedsAnAntecedent()
        {
            string sentence = Receipt();
            string opener = FirstWord(sentence);

            Assert.False(NeedsAnAntecedent.Contains(opener, StringComparer.OrdinalIgnoreCase),
                "The persistence receipt now reads \"" + sentence + "\". It opens with \""
                + opener + "\", which means whatever was said immediately before it — and "
                + "this sentence is queued, so the speech arbiter may re-speak it up to "
                + Speech.SpeechArbiter.MaxSalvages + " times, many seconds later, with "
                + "that antecedent long gone. Name the subject in the sentence itself "
                + "(#442). Reword it however you like; do not make it depend on its "
                + "neighbour again.");
        }

        [Fact]
        public void TheReceiptStillSaysWhatIsLostAndHowToKeepIt()
        {
            // The two halves that make it a receipt rather than a remark. A
            // reword that drops either one leaves the operator told that
            // something is wrong and not told what to do about it.
            string sentence = Receipt();

            Assert.Contains("disconnect", sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("profile", sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheCheckCatchesTheSentenceThatCausedThis()
        {
            // The positive control. Without it, "no dependent opener found" is
            // equally consistent with a check that inspects nothing at all —
            // and the store is read from disk by path, which is exactly the
            // kind of lookup that can silently start returning the key back to
            // itself.
            Assert.Equal("this", FirstWord(TheSentenceThatCausedThis));
            Assert.Contains(FirstWord(TheSentenceThatCausedThis),
                NeedsAnAntecedent, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void BothSpeakSitesUseTheOneLexiconLine()
        {
            // One fact, one vocabulary. The slice settle and the radio-setting
            // settle both speak this, and if either grows its own sentence the
            // rule above would be pinned on a line nobody speaks any more.
            string src = Read("Radios/FlexBase.cs");
            int uses = CountOf(src, "ProvisionalSliceChangeReceipt,");

            Assert.True(uses >= 2,
                "expected the slice settle and the radio-setting settle to speak the "
                + "same receipt property and found " + uses + " such speak sites. If a "
                + "second sentence has been introduced for the same fact, this file "
                + "needs to cover it too — a receipt that stands alone is only useful "
                + "if it is the receipt actually spoken.");
        }

        // ── Plumbing ──

        private static string Receipt()
        {
            using var doc = JsonDocument.Parse(Read(SettingsJson));
            Assert.True(doc.RootElement.TryGetProperty(ReceiptKey, out var value),
                "the store has no '" + ReceiptKey + "', so this file is checking nothing");

            string sentence = value.GetString() ?? "";
            Assert.False(string.IsNullOrWhiteSpace(sentence),
                "the receipt is empty; silence is invisible to the operator who needs it");
            return sentence;
        }

        private static string FirstWord(string sentence)
        {
            var word = new string(sentence.TrimStart()
                .TakeWhile(c => char.IsLetter(c) || c == '\'').ToArray());
            return word.ToLowerInvariant();
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
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
