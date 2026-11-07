using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// One press of Delete, one removal (#774), under both ways a held key
    /// arrives: NVDA's auto-repeat downs, and JAWS's synthesised down/up pairs
    /// about 250 ms apart with no repeat flag. Codex's check of the batch found
    /// a held Delete removed item after item; FilterPresetEditorDialog removes
    /// without asking, so nothing else stood in the way.
    /// </summary>
    public sealed class DeleteKeyGateTests
    {
        private const int Gap = 750;   // a 500 ms repeat delay, sized as the push-to-talk filter sizes it

        [Fact]
        public void A_tap_presses_once_and_a_later_tap_presses_again()
        {
            // Positive control: the gate lets real presses through.
            var g = new DeleteKeyGate(Gap);
            Assert.True(g.Down(false, 0));
            g.Up(80);
            Assert.True(g.Down(false, 2000));
            g.Up(2080);
        }

        [Fact]
        public void A_key_held_under_auto_repeat_presses_once()
        {
            var g = new DeleteKeyGate(Gap);
            int presses = 0;
            if (g.Down(false, 0)) presses++;
            for (long t = 500; t <= 4000; t += 30)
                if (g.Down(true, t)) presses++;
            g.Up(4010);
            Assert.Equal(1, presses);
            Assert.True(g.Down(false, 4010 + Gap + 1)); // released, then pressed again
        }

        [Fact]
        public void A_key_held_under_jaws_pairs_presses_once()
        {
            // The first pair at the Windows repeat delay, then one every 250 ms,
            // each a fresh-looking down.
            var g = new DeleteKeyGate(Gap);
            int presses = 0;
            if (g.Down(false, 0)) presses++;
            g.Up(5);   // the first press is itself a pair under JAWS
            for (long t = 512; t <= 4000; t += 250)
            {
                if (g.Down(false, t)) presses++;
                g.Up(t + 5);
            }
            Assert.Equal(1, presses);
        }

        [Fact]
        public void A_removal_still_landing_presses_nothing()
        {
            var g = new DeleteKeyGate(Gap);
            Assert.True(g.Down(false, 0));
            g.Busy = true;
            g.Up(80);
            Assert.False(g.Down(false, 5000)); // a real later press, but the last one is still landing
            g.Busy = false;
            g.Up(5080);
            Assert.True(g.Down(false, 7000));
        }

        [Fact]
        public void A_repeat_with_no_press_seen_starts_a_hold_without_pressing()
        {
            // Focus arrived on the list while Delete was already down.
            var g = new DeleteKeyGate(Gap);
            Assert.False(g.Down(true, 0));
            Assert.False(g.Down(true, 30));
        }

        [Fact]
        public void An_up_that_went_to_another_window_is_not_waited_for_forever()
        {
            // The press opened a question; its up went there. Long after, a new
            // press on the list presses.
            var g = new DeleteKeyGate(Gap);
            Assert.True(g.Down(false, 0));
            Assert.True(g.Down(false, 3000));
        }

        [Theory]
        [InlineData(2, 5, 4, 2)]
        [InlineData(4, 5, 4, 3)]
        [InlineData(0, 1, 0, -1)]
        [InlineData(1, 5, 5, -1)]
        public void Focus_lands_on_the_next_item_or_the_previous(int at, int before, int after, int landing)
            => Assert.Equal(landing, DeleteKeyGate.LandingIndex(at, before, after));

        [Fact]
        public void The_operator_and_memory_group_lists_the_product_opens_are_wired()
        {
            // The WinForms forms are the live ones (ApplicationEvents.vb and
            // globals.vb open Lister; MemoryScan.vb opens ManageGroups); the WPF
            // ListerDialog and ManageGroupsDialog are constructed nowhere.
            string root = Root();
            Assert.Contains("Radios.WinFormsDeleteKey.Wire(ScreenList, DeleteButton,",
                File.ReadAllText(Path.Combine(root, "Lister.vb")));
            Assert.Contains("Radios.WinFormsDeleteKey.Wire(GroupsControl.GroupsListBox, DeleteButton,",
                File.ReadAllText(Path.Combine(root, "ManageGroups.vb")));
            Assert.Contains("Lister.ShowDialog()", File.ReadAllText(Path.Combine(root, "ApplicationEvents.vb")));
            Assert.Contains("ManageGroups.ShowDialog()", File.ReadAllText(Path.Combine(root, "MemoryScan.vb")));
        }

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }
    }
}
