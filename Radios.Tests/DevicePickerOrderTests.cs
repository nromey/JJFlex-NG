using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The order the two device lists are presented in (#213).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lists used to arrive in PortAudio's enumeration order, which is
    /// driver and registry order. Noel, 2026-08-24: "The dialog is not sorted
    /// alphabetically, so you get line 3 and line 4." An interface with
    /// numbered ports scatters its ports through the list with unrelated
    /// devices between them.
    /// </para>
    /// <para>
    /// This costs a sighted operator a glance and costs a screen-reader
    /// operator the whole list, because arrowing is linear and type-ahead only
    /// helps when the rows a letter matches are together.
    /// </para>
    /// <para>
    /// <see cref="Devices.CompareDeviceNames"/> is the whole ordering rule and
    /// is a pure function over two strings, so it is tested here rather than
    /// through a dialog. The tie-breaks that follow it — host API preference,
    /// then the system's own index — only decide the order of rows whose names
    /// are identical, which is exactly the advanced view's one-row-per-host-API
    /// case.
    /// </para>
    /// </remarks>
    public class DevicePickerOrderTests
    {
        private static List<string> Sorted(params string[] names)
        {
            var list = new List<string>(names);
            list.Sort((a, b) => Devices.CompareDeviceNames(a, b));
            return list;
        }

        // ────────────────────────────────────────────────────────────────
        //  Prove the instrument before trusting it
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Plain_alphabetical_order_gets_the_numbered_ports_wrong()
        {
            // The positive control for every assertion below. If ordinary
            // string ordering already put these right, this whole comparer
            // would be measuring nothing — so prove it does not, with the
            // exact case that was reported.
            var plain = new List<string> { "Line 3", "Line 10", "Line 4" };
            plain.Sort(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(new[] { "Line 10", "Line 3", "Line 4" }, plain);
        }

        // ────────────────────────────────────────────────────────────────
        //  The reported case
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Numbered_ports_land_next_to_each_other_and_in_number_order()
        {
            var order = Sorted(
                "Speakers (Realtek High Definition Audio)",
                "Line 3 (Audient EVO8)",
                "Microphone (USB Audio Device)",
                "Line 1 (Audient EVO8)",
                "Line 10 (Audient EVO8)",
                "Line 4 (Audient EVO8)",
                "Line 2 (Audient EVO8)");

            var lines = order.Where(n => n.StartsWith("Line ", StringComparison.Ordinal)).ToList();

            Assert.Equal(
                new[]
                {
                    "Line 1 (Audient EVO8)",
                    "Line 2 (Audient EVO8)",
                    "Line 3 (Audient EVO8)",
                    "Line 4 (Audient EVO8)",
                    "Line 10 (Audient EVO8)",
                },
                lines);

            // Adjacent, not merely in order: nothing unrelated between them.
            int first = order.IndexOf("Line 1 (Audient EVO8)");
            int last = order.IndexOf("Line 10 (Audient EVO8)");
            Assert.Equal(lines.Count - 1, last - first);
        }

        [Fact]
        public void Ten_sorts_after_two_not_between_one_and_two()
        {
            Assert.True(Devices.CompareDeviceNames("Line 2", "Line 10") < 0);
            Assert.True(Devices.CompareDeviceNames("Line 10", "Line 2") > 0);
        }

        [Fact]
        public void Leading_zeros_do_not_change_a_number()
        {
            Assert.True(Devices.CompareDeviceNames("Port 007", "Port 8") < 0);
            Assert.True(Devices.CompareDeviceNames("Port 010", "Port 9") > 0);
        }

        [Fact]
        public void Case_does_not_decide_the_order()
        {
            Assert.True(Devices.CompareDeviceNames("audient evo8", "Behringer") < 0);
            Assert.True(Devices.CompareDeviceNames("BEHRINGER", "audient evo8") > 0);
        }

        [Fact]
        public void A_truncated_mme_name_sorts_immediately_before_the_name_it_was_cut_from()
        {
            // MME truncates to 31 characters, so the same hardware arrives
            // under a short name and a long one. Shorter-prefix-first puts the
            // pair together, which is what an operator hearing both needs.
            const string full = "Mic | Line | Instrument 1 (Audient EVO8)";
            const string mme = "Mic | Line | Instrument 1 (Audi";

            var order = Sorted(full, "Speakers (Realtek)", mme);
            Assert.Equal(new[] { mme, full, "Speakers (Realtek)" }, order);
        }

        [Fact]
        public void The_order_is_total_so_a_sort_cannot_shuffle_equal_looking_rows()
        {
            // Names differing only in case must still order deterministically;
            // returning zero for them would let List.Sort place them either way
            // and the list would move between runs for no visible reason.
            Assert.NotEqual(0, Devices.CompareDeviceNames("Speakers", "SPEAKERS"));
            Assert.Equal(0, Devices.CompareDeviceNames("Speakers", "Speakers"));
        }

        [Fact]
        public void Null_and_empty_names_do_not_throw()
        {
            Assert.Equal(0, Devices.CompareDeviceNames(null, null));
            Assert.True(Devices.CompareDeviceNames(null, "Speakers") < 0);
            Assert.True(Devices.CompareDeviceNames("Speakers", "") > 0);
        }

        // ────────────────────────────────────────────────────────────────
        //  A correct comparer nobody calls orders nothing
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Both_picker_views_are_ordered_before_they_are_returned()
        {
            // The comparer is exercised above in isolation, which says nothing
            // about whether the picker uses it. SelectPickerRows returns from
            // two places — the advanced view early, the basic view at the end —
            // and an unordered early return would leave the advanced list
            // exactly as it was. Read the source, because the method is private
            // and the alternative is enumerating real hardware in a test.
            string src = Source(Path.Combine("JJPortaudio", "JJPortaudio", "Devices.cs"));
            string method = MethodBody(src,
                "private static List<DeviceInfo> SelectPickerRows(IReadOnlyList<DeviceInfo> all)");

            // Two real return paths — the empty-list guard at the top returns
            // nothing to order — so two sorts, one on each.
            Assert.Equal(2, Regex.Matches(method, @"\bSortForPicker\(").Count);

            int advanced = method.IndexOf("if (ShowAdvancedDevices)", StringComparison.Ordinal);
            Assert.True(advanced > 0, "the advanced-view branch has moved or gone");

            int advancedReturn = method.IndexOf("return picker;", advanced, StringComparison.Ordinal);
            Assert.True(advancedReturn > advanced);
            Assert.Contains("SortForPicker(",
                method.Substring(advanced, advancedReturn - advanced));

            int lastSort = method.LastIndexOf("SortForPicker(", StringComparison.Ordinal);
            Assert.True(lastSort > advancedReturn, "the basic-view path does not sort");
            Assert.Contains("return picker;", method.Substring(lastSort));
        }

        [Fact]
        public void The_pre_selected_fallback_asks_for_the_default_rather_than_taking_row_zero()
        {
            // Sorting the picker breaks a coincidence the dialog relied on: the
            // Windows default used to be first in enumeration order, so "the
            // first usable row" was the default nearly always. After sorting,
            // row zero is whichever device sorts first — no basis at all for
            // choosing somebody's microphone.
            string src = Source(Path.Combine("JJFlexWpf", "Dialogs", "AudioDevicesDialog.xaml.cs"));

            Assert.Contains("DefaultOrFirstUsableIndex", src);
            // Word-boundary, or the old name matches inside the new one and the
            // assertion fails on the very code that satisfies it.
            Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])FirstUsableIndex\("), src);

            string method = MethodBody(src,
                "private static int DefaultOrFirstUsableIndex(IReadOnlyList<Devices.DeviceInfo> list)");
            Assert.Contains("IsDefault", method);
            Assert.Contains("GroupIsSystemDefault", method);
        }

        [Fact]
        public void The_method_scanner_finds_a_body_and_stops_at_its_end()
        {
            // Positive control for the two source reads above.
            const string sample = @"
    private static int Wanted(int x)
    {
        SortForPicker(rows);
        return picker;
    }

    private static int Other()
    {
        return Marker();
    }
";
            string body = MethodBody(sample, "private static int Wanted(int x)");
            Assert.Contains("SortForPicker(rows);", body);
            Assert.DoesNotContain("Marker()", body);
        }

        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "signature not found: " + signature);

            int open = source.IndexOf('{', at);
            Assert.True(open >= 0, "no body for: " + signature);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(at, i - at + 1);
                }
            }
            Assert.Fail("unbalanced braces after: " + signature);
            return "";
        }

        private static string Source(string relative)
        {
            string path = System.IO.Path.Combine(RepoRoot(), relative);
            Assert.True(System.IO.File.Exists(path), "source not found: " + path);
            return System.IO.File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }

        // ────────────────────────────────────────────────────────────────
        //  Type-ahead is the reason this matters
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_row_starting_with_a_letter_is_reachable_by_repeating_that_letter()
        {
            // Windows list first-letter navigation cycles through matching
            // rows in list order. That is only coherent — and only learnable —
            // if the matching rows are contiguous, which sorting guarantees
            // and enumeration order does not.
            var order = Sorted(
                "Speakers (Realtek High Definition Audio)",
                "Microphone (USB Audio Device)",
                "Speakers (Audient EVO8)",
                "Headset Earphone (Jabra)",
                "Microphone Array (Intel Smart Sound)");

            foreach (char letter in new[] { 'S', 'M', 'H' })
            {
                var hits = Enumerable.Range(0, order.Count)
                    .Where(i => char.ToUpperInvariant(order[i][0]) == letter)
                    .ToList();
                Assert.NotEmpty(hits);
                Assert.Equal(hits.Last() - hits.First(), hits.Count - 1);
            }
        }
    }
}
