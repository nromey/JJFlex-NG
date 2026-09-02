using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Which slice a transmit-chain setting goes to (#496), and a scan that
    /// refuses the binding that caused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule itself is small. The scan is the part that keeps it: the
    /// transmit antenna setter bound to <c>ActiveSlice</c> for years, every
    /// echo said the change took, and it was found only when 100 W into a
    /// port believed to be bare came back with no foldback. A future edit that
    /// puts <c>ActiveSlice</c> back into that setter would compile, run, and
    /// echo success exactly as before — so the scan reads the source.
    /// </para>
    /// </remarks>
    public class TransmitSettingTargetTests
    {
        private sealed class FakeSlice
        {
            public FakeSlice(int index) { Index = index; }
            public int Index { get; }
        }

        [Fact]
        public void When_the_selected_slice_is_not_the_transmit_slice_the_transmit_slice_takes_the_setting()
        {
            // The multi-slice case: slice 3 selected, slice 0 transmitting —
            // the arrangement on the bench on 2026-09-01.
            var tx = new FakeSlice(0);
            var selected = new FakeSlice(3);

            FakeSlice? target = TransmitSettingTarget.Resolve(tx, selected, out TransmitSettingTarget.Basis basis);

            Assert.Same(tx, target);
            Assert.Equal(TransmitSettingTarget.Basis.TransmitSlice, basis);

            string words = TransmitSettingTarget.Describe(basis, tx.Index, selected.Index);
            Assert.Contains("selected slice is 3", words);
            Assert.Contains("NOT transmitting", words);
        }

        [Fact]
        public void When_they_coincide_nothing_changes()
        {
            var slice = new FakeSlice(0);

            FakeSlice? target = TransmitSettingTarget.Resolve(slice, slice, out TransmitSettingTarget.Basis basis);

            Assert.Same(slice, target);
            Assert.Equal(TransmitSettingTarget.Basis.TransmitSlice, basis);
            Assert.Contains("also the selected slice", TransmitSettingTarget.Describe(basis, 0, 0));
        }

        [Fact]
        public void With_no_transmit_slice_the_selected_slice_takes_it_and_the_basis_says_so()
        {
            var selected = new FakeSlice(2);

            FakeSlice? target = TransmitSettingTarget.Resolve<FakeSlice>(null, selected, out TransmitSettingTarget.Basis basis);

            Assert.Same(selected, target);
            Assert.Equal(TransmitSettingTarget.Basis.SelectedSliceBecauseNothingTransmits, basis);
            Assert.Contains("no slice is transmitting", TransmitSettingTarget.Describe(basis, -1, 2));
        }

        [Fact]
        public void With_no_slice_at_all_nothing_is_chosen()
        {
            FakeSlice? target = TransmitSettingTarget.Resolve<FakeSlice>(null, null, out TransmitSettingTarget.Basis basis);

            Assert.Null(target);
            Assert.Equal(TransmitSettingTarget.Basis.None, basis);
        }

        // ---- the scan ----

        private static string FlexBaseSource() =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios", "FlexBase.cs"));

        /// <summary>The source from <paramref name="start"/> up to the next
        /// <paramref name="end"/> after it.</summary>
        private static string Block(string src, string start, string end)
        {
            int a = src.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, "member not found: " + start);
            int b = src.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            Assert.True(b > a, "end of member not found after: " + start);
            return src.Substring(a, b - a);
        }

        [Fact]
        public void The_transmit_antenna_no_longer_binds_to_the_selected_slice()
        {
            string src = FlexBaseSource();
            string property = Block(src, "public string TXAntennaName", "/// <summary>");

            Assert.Contains("SliceForTransmitSetting(", property);
            Assert.DoesNotContain("ActiveSlice", property);
        }

        [Fact]
        public void The_transmit_antenna_list_follows_the_same_slice()
        {
            string src = FlexBaseSource();
            string property = Block(src, "public List<string> TXAntennaList", ";");

            Assert.Contains("SliceForTransmitSetting(", property);
            Assert.DoesNotContain("ActiveSlice", property);
        }

        [Fact]
        public void The_transmit_offset_no_longer_binds_to_the_selected_slice()
        {
            string src = FlexBaseSource();
            string property = Block(src, "public RITData XIT", "internal const int BreakinDelayMin");

            Assert.Contains("SliceForTransmitSetting(", property);
            Assert.DoesNotContain("ActiveSlice", property);
        }

        [Fact]
        public void The_resolver_prefers_the_radio_s_own_transmit_slice_and_checks_the_tracked_one()
        {
            // TransmitChainSliceOrNull asks FlexLib first, and only trusts the
            // VFO this app tracks when that slice's own flag agrees. A stale
            // VFO must never name a slice that has stopped transmitting.
            string src = FlexBaseSource();
            string method = Block(src, "internal Slice TransmitChainSliceOrNull()", "/// <summary>");

            Assert.Contains("r.TransmitSlice", method);
            Assert.Contains("tracked.IsTransmitSlice", method);
            Assert.DoesNotContain("ActiveSlice", method);
        }
    }
}
