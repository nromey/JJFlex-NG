#nullable enable

using System;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The words of the picker row for a radio last seen on SmartLink and not
    /// currently confirmed (#619, Noel's ruling of 2026-09-30). The decision
    /// that puts a row in this state is held in
    /// <see cref="SmartLinkListProvenanceTests"/>; this class reads the
    /// sentence it produces. In the settings-root collection because the
    /// lexicon resolves under that root.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class PickerSightingPhraseTests
    {
        /// <summary>
        /// Ruling one's words: the row says last seen, not online, with a
        /// terse form shorter than the chatty one. DRAFTS (#629) — the
        /// assertions are about what the sentences may not claim, never the
        /// wording.
        /// </summary>
        [Fact]
        public void A_last_seen_row_says_so_at_both_verbosities_and_never_says_online()
        {
            foreach (var account in new[] { "", "dbreda@example.com" })
            {
                string chatty = PickerSighting.LastSeenWhere(account, VerbosityLevel.Chatty);
                string terse = PickerSighting.LastSeenWhere(account, VerbosityLevel.Terse);

                foreach (var text in new[] { chatty, terse })
                {
                    Assert.False(Lexicon.LooksLikeKey(text), $"the row read a raw key: {text}");
                    Assert.Contains("last seen", text, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("SmartLink", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("online", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("{", text, StringComparison.Ordinal);
                    if (account.Length > 0) Assert.Contains(account, text, StringComparison.Ordinal);
                }
                Assert.Contains("not currently confirmed", chatty, StringComparison.Ordinal);
                Assert.True(terse.Length < chatty.Length,
                    $"terse is not shorter than chatty: '{terse}' against '{chatty}'");
            }
        }
    }
}
