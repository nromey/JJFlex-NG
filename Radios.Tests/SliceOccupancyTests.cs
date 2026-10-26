using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Flex.Smoothlake.FlexLib;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #584: the Slice menu said "slice (4 of 0)" on an 8600 holding 2 of 4.
    /// Two causes, both held here: the ceiling was FlexLib's MaxSlices, which
    /// comes back 0, and the count was a field assigned once per connect, so
    /// the menu (which IS rebuilt on every slice add and release) re-read a
    /// frozen number. An impossible pair now degrades rather than being said.
    /// </summary>
    public sealed class SliceOccupancyTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        [Theory]
        [InlineData(2, 4, "2 of 4")]
        [InlineData(0, 2, "0 of 2")]
        [InlineData(8, 8, "8 of 8")]
        [InlineData(4, 0, null)]    // the reported shape: a count over a ceiling of zero
        [InlineData(5, 4, null)]    // more in use than the radio can hold
        [InlineData(-1, 4, null)]
        public void An_impossible_pair_is_not_said(int used, int max, string expected)
        {
            Assert.Equal(expected, FlexBase.SliceOccupancyText(used, max));
        }

        [Fact]
        public void The_count_follows_the_radio_and_the_ceiling_is_the_models()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                Assert.Null(rig.SliceOccupancy);   // no radio: nothing to say

                var radio = (Radio)RuntimeHelpers.GetUninitializedObject(typeof(Radio));
                var slices = new List<Slice>
                {
                    (Slice)RuntimeHelpers.GetUninitializedObject(typeof(Slice)),
                    (Slice)RuntimeHelpers.GetUninitializedObject(typeof(Slice)),
                    (Slice)RuntimeHelpers.GetUninitializedObject(typeof(Slice)),
                    (Slice)RuntimeHelpers.GetUninitializedObject(typeof(Slice)),
                };
                typeof(Radio).GetField("_slices", Private)!.SetValue(radio, slices);
                typeof(Radio).GetField("_model", Private)!.SetValue(radio, "FLEX-8600");
                typeof(FlexBase).GetField("theRadio", Private)!.SetValue(rig, radio);

                // The radio's own MaxSlices is the 0 the bench saw; the model says 4.
                Assert.Equal(0, rig.MaxSlices);
                Assert.Equal("4 of 4", rig.SliceOccupancy);

                // Noel released two slices; nothing re-ran the connect.
                slices.RemoveAt(3);
                slices.RemoveAt(2);
                Assert.Equal(2, rig.TotalNumSlices);
                Assert.Equal("2 of 4", rig.SliceOccupancy);

                typeof(FlexBase).GetField("theRadio", Private)!.SetValue(rig, null);
            }
            finally
            {
                try { rig.Dispose(); } catch { /* teardown of a radioless rig */ }
            }
        }

        /// <summary>The two surfaces that showed the pair read it from
        /// <see cref="FlexBase.SliceOccupancy"/>, and neither reaches for
        /// FlexLib's MaxSlices again.</summary>
        [Theory]
        [InlineData("JJFlexWpf/NativeMenuBar.cs")]
        [InlineData("JJFlexWpf/Dialogs/AboutDialog.xaml.cs")]
        public void The_menu_and_the_About_dialog_use_the_occupancy(string file)
        {
            string text = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), file));
            Assert.DoesNotContain("Rig.MaxSlices", text);
            Assert.Contains("SliceOccupancy", text);
        }
    }
}
