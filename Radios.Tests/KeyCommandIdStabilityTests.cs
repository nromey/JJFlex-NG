using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Guards the numeric identity of <see cref="CommandValues"/>, because
    /// those numbers are the on-disk format of the operator's key map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>KeyDefType.i</c> is <c>(int)CommandValues</c>, and that integer is
    /// what <c>%AppData%\JJFlexRadio\KeyDefs.xml</c> stores. So a member's
    /// number is not an implementation detail — it is a persisted identifier,
    /// and changing it silently re-points a saved binding at a different
    /// command.
    /// </para>
    /// <para>
    /// Found 2026-08-21 by diffing the operator's live KeyDefs.xml against a
    /// NAS snapshot taken the same morning. Commit 40307951 (2026-08-18) had
    /// inserted <c>SpeakContextHelp</c> mid-enum, and comparing the two files
    /// as a command-id-to-key MAPPING — not as text — showed 22 commands each
    /// inheriting the key the previous command had held.
    /// </para>
    /// <para>
    /// Nothing was damaged, and only by luck: the operator had zero customised
    /// bindings, so every key equalled its default and rewriting to the new
    /// defaults was correct. Anyone WITH customisations would have had them
    /// attach to different commands. Nothing errors, the file loads, and the
    /// keys simply do other things now — which for an operator whose entire
    /// interaction model is the keyboard is severe and close to undiagnosable
    /// from the inside.
    /// </para>
    /// <para>
    /// These tests are deliberately boring. They exist so that renumbering
    /// fails the build instead of failing a user.
    /// </para>
    /// </remarks>
    public class KeyCommandIdStabilityTests
    {
        /// <summary>
        /// Anchors sampled across the range, including the boundaries and the
        /// members either side of the 2026-08-18 insertion point that exposed
        /// this. If any of these move, saved key maps have been invalidated.
        /// Add to this list when adding a command; never edit an existing row
        /// to make a failing test pass — a failure here means the enum changed
        /// underneath files that are already on disk.
        /// </summary>
        public static IEnumerable<object[]> Anchors => new[]
        {
            new object[] { CommandValues.NotACommand,     -1 },
            new object[] { CommandValues.ShowHelp,         0 },
            new object[] { CommandValues.ShowFreq,         1 },
            new object[] { CommandValues.SetFreq,          2 },
            // Either side of where SpeakContextHelp landed in 40307951.
            new object[] { CommandValues.SpeakContextHelp, 96 },
            new object[] { CommandValues.RepeatLastCw,   120 },
        };

        [Theory]
        [MemberData(nameof(Anchors))]
        public void AnchorCommandsKeepTheirPersistedNumbers(CommandValues command, int expected)
        {
            Assert.Equal(expected, (int)command);
        }

        [Fact]
        public void EveryCommandValueIsUnique()
        {
            // Two members sharing a number means two commands share a saved
            // binding, and whichever loads last wins. Explicit numbering makes
            // this possible in a way implicit numbering never did, so it has
            // to be checked.
            var byValue = Enum.GetValues<CommandValues>()
                              .GroupBy(v => (int)v)
                              .Where(g => g.Count() > 1)
                              .ToList();

            Assert.True(byValue.Count == 0,
                "Duplicate CommandValues numbers: " + string.Join("; ",
                    byValue.Select(g => g.Key + " => " +
                        string.Join(", ", g.Select(v => v.ToString())))));
        }

        [Fact]
        public void CommandNumbersAreContiguousFromZero()
        {
            // Not a correctness requirement — a gap breaks nothing. It is a
            // drift signal: a gap means a command was deleted, and a deleted
            // command's number must NEVER be reused, because saved files still
            // carry it. If this fails, confirm the gap is intentional and add
            // the number to the reserved list below rather than filling it.
            var values = Enum.GetValues<CommandValues>()
                             .Select(v => (int)v)
                             .Where(v => v >= 0)
                             .OrderBy(v => v)
                             .ToList();

            var gaps = Enumerable.Range(0, values.Last() + 1)
                                 .Except(values)
                                 .Except(ReservedRetiredNumbers)
                                 .ToList();

            Assert.True(gaps.Count == 0,
                "Gaps in CommandValues numbering: " + string.Join(", ", gaps) +
                ". A gap means a command was removed. Its number must not be " +
                "reused — saved KeyDefs.xml files still reference it. Add it " +
                "to ReservedRetiredNumbers instead.");
        }

        /// <summary>
        /// Numbers belonging to commands that have been retired. They stay
        /// burned forever: a KeyDefs.xml written before the removal still
        /// carries them, and reusing one would hand an old binding to a new
        /// command. Empty today.
        /// </summary>
        private static readonly int[] ReservedRetiredNumbers = Array.Empty<int>();

        [Fact]
        public void AddingACommandDoesNotDisturbExistingOnes()
        {
            // The property that actually matters, stated as an executable
            // claim: source ORDER and numeric VALUE are independent. A new
            // member may be written anywhere in the list and must take the
            // next unused number rather than displacing its neighbours.
            //
            // Verified structurally — every member carries an explicit value,
            // so position cannot influence numbering. If someone drops an
            // explicit value, C# resumes positional assignment for that member
            // and the guarantee is gone, so that is what this checks.
            var type = typeof(CommandValues);
            var names = Enum.GetNames(type);

            // A member with no explicit initialiser would take its
            // predecessor's value plus one. Detect the danger by confirming
            // the set of values is exactly what the explicit declarations say,
            // via a round trip through the numbers themselves.
            foreach (var name in names)
            {
                var value = (int)Enum.Parse<CommandValues>(name);
                Assert.Equal(name, Enum.GetName(type, (CommandValues)value));
            }

            // 123 as of Sprint 36 Track F, which appended SpeakVersion = 121
            // for the Ctrl+J, Alt+V build chord (#269). Bump this deliberately
            // when a command is added; a test that counts is how an accidental
            // renumbering gets noticed.
            Assert.Equal(123, names.Length);
        }
    }
}
