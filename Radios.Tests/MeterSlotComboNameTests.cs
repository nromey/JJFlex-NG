using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The meters panel's slot combo names itself with the number of meters
    /// in use. Said as "4 of 8", a screen reader heard it as the selected
    /// item's list position on every selection, whichever meter was picked.
    /// </summary>
    public class MeterSlotComboNameTests
    {
        [Theory]
        [InlineData(1, 8)]
        [InlineData(4, 8)]
        [InlineData(8, 8)]
        public void The_slot_combos_name_never_reads_as_a_list_position(int count, int max)
        {
            string name = Lexicon.Get("audio.meters.panel.slot_combo_name",
                ("count", count), ("max", max));

            // Positive control: the count and the room are both still in it.
            Assert.Contains(count.ToString(), name);
            Assert.Contains(max.ToString(), name);
            Assert.DoesNotMatch(new Regex(@"\b\d+\s+of\s+\d+\b", RegexOptions.IgnoreCase), name);
        }
    }
}
