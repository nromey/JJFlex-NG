using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #625, repair round two: the drop window and the changelog may not claim
    /// that every kind of line in the saved file explains itself. Round one
    /// took that claim out of the file's opening, where
    /// <see cref="TraceSelfDescriptionTests.OverclaimingPhrases"/> guards it;
    /// that guard reads only the opening, so the window and the changelog
    /// fragment kept it. This applies the same list to them.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class DropWindowClaimsTests
    {
        private static string Fragment() => File.ReadAllText(
            Path.Combine(FlexBasePartials.RepoRoot(), "docs", "changelog.d", "junkbox-625.md"));

        [Fact]
        public void No_drop_window_sentence_claims_every_kind_of_line_explains_itself()
        {
            string[] keys = LexiconBaseline.FromShipped("logging").Keys
                .Where(k => k.StartsWith("logging.capture.dropped.", StringComparison.Ordinal)).ToArray();
            Assert.True(keys.Length >= 15, "the drop window's keys were not found; this guard would be vacuous");
            foreach (string key in keys)
            {
                string text = Lexicon.Get(key);
                foreach (string phrase in TraceSelfDescriptionTests.OverclaimingPhrases)
                    Assert.False(text.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                        key + " claims more than the file guarantees (\"" + phrase + "\"): " + text);
            }
        }

        [Fact]
        public void The_changelog_fragment_does_not_claim_it_either()
        {
            string fragment = Fragment();
            Assert.Contains("Capture Note", fragment, StringComparison.Ordinal);   // the right file
            foreach (string phrase in TraceSelfDescriptionTests.OverclaimingPhrases)
                Assert.False(fragment.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                    "junkbox-625.md claims more than the file guarantees (\"" + phrase + "\")");
        }
    }
}
