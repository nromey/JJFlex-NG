using System.Collections.Generic;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The check that spots a key map whose bindings slipped onto the wrong
    /// commands, as happened to every file written before 2026-08-18.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every test here is a positive control before it is anything else. A
    /// detector that never fires and a healthy key map produce the same
    /// output — silence — which is precisely the failure this project keeps
    /// shipping.
    /// </para>
    /// <para>
    /// The numbers come from a real measurement, not an invention: comparing
    /// the NAS AppData snapshot appdata-20260821-100458.zip against the
    /// operator's live KeyDefs.xml on 2026-08-24 showed 22 ids differing,
    /// beginning at id 96, each holding the previous entry's key.
    /// </para>
    /// </remarks>
    public class KeyMapIntegrityTests
    {
        // A stand-in defaults table: command n has default key (1000 + n).
        // Contiguous and injective, so "the default of the command one number
        // below" is unambiguous and a slip is unmistakable.
        private static Keys DefaultFor(int id) =>
            id >= 0 && id < 122 ? (Keys)(1000 + id) : Keys.None;

        private static List<KeyMapIntegrity.SavedBinding> Healthy(int count)
        {
            var l = new List<KeyMapIntegrity.SavedBinding>();
            for (int i = 0; i < count; i++)
                l.Add(new KeyMapIntegrity.SavedBinding(i, DefaultFor(i), DefaultFor(i)));
            return l;
        }

        /// <summary>Reproduces the 2026-08-18 insertion: everything at or after
        /// <paramref name="insertAt"/> carries the previous command's default.</summary>
        private static List<KeyMapIntegrity.SavedBinding> ShiftedFrom(int count, int insertAt)
        {
            var l = new List<KeyMapIntegrity.SavedBinding>();
            for (int i = 0; i < count; i++)
            {
                // A slipped entry carries the PREVIOUS command's key AND that
                // command's recorded default — which is why it looks internally
                // consistent and why it is safe to repair.
                Keys d = DefaultFor(i < insertAt ? i : i - 1);
                l.Add(new KeyMapIntegrity.SavedBinding(i, d, d));
            }
            return l;
        }

        [Fact]
        public void The_real_shift_is_detected()
        {
            // THE positive control. 122 commands, insertion at 96 — the measured
            // case. If this ever passes by reporting health, the check is
            // decorative and every clean result it has given is worthless.
            var v = KeyMapIntegrity.Check(ShiftedFrom(122, 96), DefaultFor);

            Assert.True(v.LooksShifted, v.Describe());
            Assert.Equal(96, v.FirstSlippedId);
            Assert.Equal(122 - 96, v.SlippedByOne);   // 26 entries at or after the insertion
            Assert.Equal(96, v.Consistent);
        }

        [Fact]
        public void A_healthy_map_is_not_reported_as_shifted()
        {
            // The negative control, without which the test above proves nothing:
            // a check that returned LooksShifted unconditionally would pass it.
            var v = KeyMapIntegrity.Check(Healthy(122), DefaultFor);

            Assert.False(v.LooksShifted, v.Describe());
            Assert.Equal(122, v.Consistent);
            Assert.Equal(0, v.SlippedByOne);
            Assert.Equal(-1, v.FirstSlippedId);
        }

        [Fact]
        public void One_reassigned_default_is_not_a_shift()
        {
            // A single command whose default was deliberately moved produces one
            // mismatch. Reporting that as a corrupted key map would train the
            // operator to ignore the message that matters, so the threshold
            // requires a RUN. This is the test that keeps the check honest
            // rather than merely sensitive.
            var l = Healthy(122);
            l[40] = new KeyMapIntegrity.SavedBinding(40, (Keys)999, (Keys)999);   // was reassigned

            var v = KeyMapIntegrity.Check(l, DefaultFor);

            Assert.False(v.LooksShifted, v.Describe());
            Assert.Equal(1, v.Unexplained);
        }

        [Fact]
        public void Entries_that_never_recorded_a_default_are_skipped_not_guessed()
        {
            // Older builds — and intermediate ones that did not track it cleanly
            // — store Keys.None. That is absence of evidence, not evidence of a
            // problem, and it must not be counted either way.
            var l = new List<KeyMapIntegrity.SavedBinding>();
            for (int i = 0; i < 30; i++)
                l.Add(new KeyMapIntegrity.SavedBinding(i, (Keys)(500 + i), Keys.None));

            var v = KeyMapIntegrity.Check(l, DefaultFor);

            Assert.Equal(30, v.Untracked);
            Assert.Equal(0, v.Consistent);
            Assert.Equal(0, v.SlippedByOne);
            Assert.False(v.LooksShifted);
        }

        [Fact]
        public void A_shift_just_under_the_threshold_is_not_reported()
        {
            // Boundary, from the quiet side. Four slipped entries must stay
            // quiet; the threshold is five.
            var l = Healthy(122);
            for (int i = 118; i < 122; i++)
                l[i] = new KeyMapIntegrity.SavedBinding(i, DefaultFor(i - 1), DefaultFor(i - 1));

            var v = KeyMapIntegrity.Check(l, DefaultFor);

            Assert.Equal(4, v.SlippedByOne);
            Assert.False(v.LooksShifted, v.Describe());
        }

        [Fact]
        public void A_shift_exactly_at_the_threshold_is_reported()
        {
            // The other side of the same boundary. Without this pair, the
            // threshold could be off by one in either direction unnoticed.
            var l = Healthy(122);
            for (int i = 117; i < 122; i++)
                l[i] = new KeyMapIntegrity.SavedBinding(i, DefaultFor(i - 1), DefaultFor(i - 1));

            var v = KeyMapIntegrity.Check(l, DefaultFor);

            Assert.Equal(KeyMapIntegrity.SlipRunThreshold, v.SlippedByOne);
            Assert.True(v.LooksShifted, v.Describe());
            Assert.Equal(117, v.FirstSlippedId);
        }

        [Fact]
        public void The_first_slipped_id_names_the_insertion_point()
        {
            // The operator-facing value of the whole check: not just "something
            // is wrong" but WHERE it started, which is what makes it diagnosable
            // rather than merely alarming.
            var v = KeyMapIntegrity.Check(ShiftedFrom(122, 40), DefaultFor);

            Assert.True(v.LooksShifted);
            Assert.Equal(40, v.FirstSlippedId);
            Assert.Contains("id 40", v.Describe());
        }

        [Fact]
        public void Nothing_throws_on_empty_or_null_input()
        {
            Assert.False(KeyMapIntegrity.Check(new List<KeyMapIntegrity.SavedBinding>(), DefaultFor).LooksShifted);
            Assert.False(KeyMapIntegrity.Check(null!, DefaultFor).LooksShifted);
            Assert.False(KeyMapIntegrity.Check(Healthy(5), null!).LooksShifted);
        }

        [Fact]
        public void The_description_says_which_way_it_went()
        {
            // The trace line is the only thing a human ever reads from this, so
            // it is part of the contract, not decoration.
            Assert.Contains("SHIFTED", KeyMapIntegrity.Check(ShiftedFrom(122, 96), DefaultFor).Describe());
            Assert.Contains("consistent", KeyMapIntegrity.Check(Healthy(122), DefaultFor).Describe());
        }

        [Fact]
        public void An_untouched_slipped_binding_is_repairable()
        {
            // DON'S CASE, and the one that decides whether this can be fixed
            // silently. He upgrades from a pre-2026-08-18 build and (as far as
            // anyone knows) never customised a key. Every slipped entry then
            // still carries the default it was saved with, so replacing it with
            // the correct default loses nothing he chose.
            var v = KeyMapIntegrity.Check(ShiftedFrom(122, 96), DefaultFor);

            Assert.True(v.LooksShifted, v.Describe());
            Assert.Equal(v.SlippedByOne, v.RepairableIds.Count);
            Assert.Empty(v.CustomisedIds);
            Assert.Contains(96, v.RepairableIds);
        }

        [Fact]
        public void A_slipped_binding_the_operator_chose_is_NOT_repairable()
        {
            // The other half, and the reason this is not a blanket reset. If the
            // key differs from the default recorded beside it, the operator
            // picked it on purpose. The binding is real and merely filed under
            // the wrong command — only they know what they meant, so it is
            // reported and left alone.
            var l = ShiftedFrom(122, 96);
            l[100] = new KeyMapIntegrity.SavedBinding(100, (Keys)7777, DefaultFor(99));

            var v = KeyMapIntegrity.Check(l, DefaultFor);

            Assert.True(v.LooksShifted, v.Describe());
            Assert.Contains(100, v.CustomisedIds);
            Assert.DoesNotContain(100, v.RepairableIds);
            // Everything else in the run is still free to repair.
            Assert.Equal(v.SlippedByOne - 1, v.RepairableIds.Count);
        }

        [Fact]
        public void A_healthy_map_offers_nothing_to_repair()
        {
            // Negative control for both lists at once.
            var v = KeyMapIntegrity.Check(Healthy(122), DefaultFor);

            Assert.Empty(v.RepairableIds);
            Assert.Empty(v.CustomisedIds);
        }

        [Fact]
        public void The_description_reports_both_halves_of_the_split()
        {
            // The trace line is the only thing a human reads, and "22 slipped"
            // without "how many can be fixed for free" is not actionable.
            var l = ShiftedFrom(122, 96);
            l[100] = new KeyMapIntegrity.SavedBinding(100, (Keys)7777, DefaultFor(99));
            string d = KeyMapIntegrity.Check(l, DefaultFor).Describe();

            Assert.Contains("never customised", d);
            Assert.Contains("operator chose", d);
        }
    }
}
