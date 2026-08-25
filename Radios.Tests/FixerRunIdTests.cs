using System;
using System.Linq;
using System.Text.RegularExpressions;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The test ID that travels with a Fixer run.
    /// </summary>
    /// <remarks>
    /// The property under test is not "the ID looks like A7R-4W2". It is that
    /// the ID is BUILT ONLY from an alphabet culled against the ways symbols
    /// get confused when read down a phone — by ear (rhyme families, the
    /// five/nine problem that gave aviation "niner") and by eye (0/O, 1/I/l,
    /// 5/S, 8/B, 2/Z, 6/G). The confusion families here are facts about
    /// English and about type, not values the code derives — the same
    /// standing as the real filter list in TxToneLadderDerivationTests.
    /// </remarks>
    public class FixerRunIdTests
    {
        // At most ONE member of each family may appear in the alphabet.
        // Two members of one family in a run ID is a support call that goes
        // "no, T as in Tango, not D" — the exact conversation the cull exists
        // to prevent.
        public static TheoryData<string, string> ConfusionFamilies => new TheoryData<string, string>
        {
            { "AHJK8", "the 'ay' rhyme family, plus eight against aitch" },
            { "BCDEGPTVZ", "the 'ee' rhyme family" },
            { "FLMNSX", "the short-e openers, including the classic M/N pair" },
            { "QUW", "the 'you' rhymes" },
            { "IY", "eye and why" },
            { "O0", "oh and zero-read-as-oh" },
            { "59", "five and nine — the confusion that gave aviation 'niner'" },
            { "1IL", "one, capital i, and lowercase L by eye" },
            { "5S", "five and S by eye" },
            { "8B", "eight and B by eye" },
            { "2Z", "two and Z by eye" },
            { "6G", "six and G by eye" },
        };

        [Theory]
        [MemberData(nameof(ConfusionFamilies))]
        public void At_most_one_member_of_each_confusion_family_survives(string family, string why)
        {
            int members = FixerRunId.Alphabet.Count(c => family.Contains(c));
            Assert.True(members <= 1,
                why + ": the alphabet contains " + members + " of \"" + family + "\"");
        }

        [Fact]
        public void The_alphabet_has_no_duplicates_and_no_lowercase()
        {
            Assert.Equal(FixerRunId.Alphabet.Length, FixerRunId.Alphabet.Distinct().Count());
            Assert.Equal(FixerRunId.Alphabet.ToUpperInvariant(), FixerRunId.Alphabet);
        }

        [Fact]
        public void The_alphabet_keeps_enough_entropy_to_tell_runs_apart()
        {
            // The cull must not go so far that two operators in one week get
            // the same ID as a matter of course. A million distinct IDs from
            // the symbol positions is the floor.
            double combinations = Math.Pow(FixerRunId.Alphabet.Length,
                                           FixerRunId.GroupLength * 2);
            Assert.True(combinations >= 1_000_000,
                "only " + combinations + " distinct IDs are possible");
        }

        [Fact]
        public void Every_generated_id_is_built_from_the_alphabet_and_the_separator()
        {
            var rng = new Random(1);
            for (int i = 0; i < 200; i++)
            {
                string id = FixerRunId.New(rng);
                Assert.Equal(FixerRunId.Length, id.Length);
                Assert.Equal(FixerRunId.Separator, id[FixerRunId.GroupLength]);
                foreach (char c in id.Where(c => c != FixerRunId.Separator))
                    Assert.Contains(c, FixerRunId.Alphabet);
            }
        }

        [Fact]
        public void The_id_stays_short_enough_to_read_down_a_phone()
        {
            // Ten characters is already a stretch to hold in your head while
            // typing; the format must stay under it.
            Assert.True(FixerRunId.Length <= 10, "the ID has grown to " + FixerRunId.Length);
        }

        [Fact]
        public void Ids_differ_across_generations()
        {
            var seen = Enumerable.Range(0, 50).Select(_ => FixerRunId.New()).ToHashSet();
            Assert.True(seen.Count > 1, "fifty generations produced one ID");
        }

        [Fact]
        public void The_shape_is_two_groups_either_side_of_one_separator()
        {
            string alphabetClass = "[" + Regex.Escape(FixerRunId.Alphabet) + "]";
            var shape = new Regex("^" + alphabetClass + "{" + FixerRunId.GroupLength + "}"
                + Regex.Escape(FixerRunId.Separator.ToString())
                + alphabetClass + "{" + FixerRunId.GroupLength + "}$");
            Assert.Matches(shape, FixerRunId.New(new Random(7)));
        }
    }
}
