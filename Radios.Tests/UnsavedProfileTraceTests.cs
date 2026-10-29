using System;
using System.IO;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #763: the radio's two unsaved-changes flags are traced at the station's
    /// first verdict and whenever either moves.
    /// </summary>
    /// <remarks>
    /// <c>OwnerHasUnsavedWork</c> skips a profile on these flags, and until #763
    /// no line in any trace carried them, so nobody could tell whether that
    /// guard ever fired. The fragment is tested on a real <see cref="FlexBase"/>
    /// over an offline vendor <see cref="Radio"/>; where it is written is pinned
    /// in source, because both sites run only on a live connection.
    /// </remarks>
    public sealed class UnsavedProfileTraceTests
    {
        [Fact]
        public void The_fragment_reports_each_flag_the_radio_holds()
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                var radio = (Radio)Activator.CreateInstance(
                    typeof(Radio), Private, binder: null, args: new object[] { false }, culture: null)!;
                rig.theRadio = radio;
                Assert.Equal("unsaved_changes tx=false mic=false", rig.DescribeUnsavedProfileChanges());

                typeof(Radio).GetField("_unsavedProfileChangesTX", Private)!.SetValue(radio, true);
                Assert.Equal("unsaved_changes tx=true mic=false", rig.DescribeUnsavedProfileChanges());

                typeof(Radio).GetField("_unsavedProfileChangesMIC", Private)!.SetValue(radio, true);
                Assert.Equal("unsaved_changes tx=true mic=true", rig.DescribeUnsavedProfileChanges());
            }
            finally
            {
                rig.theRadio = null;
                rig.Dispose();
            }
        }

        [Fact]
        public void The_first_verdict_line_carries_the_flags()
        {
            string source = Read("Radios/FlexBase.StationConnect.cs");
            int at = source.IndexOf("\"GetProfileInfo:radio profile autosave=\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "the GetProfileInfo first-verdict line is gone");
            int end = source.IndexOf(";", at, StringComparison.Ordinal);
            Assert.Contains("DescribeUnsavedProfileChanges()", source.Substring(at, end - at));
        }

        [Fact]
        public void Each_flag_moving_is_traced()
        {
            string source = Read("Radios/FlexBase.cs");
            int handler = source.IndexOf("private void radioPropertyChangedHandler(", StringComparison.Ordinal);
            Assert.True(handler >= 0);
            foreach (string name in new[] { "UnsavedProfileChangesTX", "UnsavedProfileChangesMIC" })
            {
                int at = source.IndexOf("case \"" + name + "\":", handler, StringComparison.Ordinal);
                Assert.True(at > handler, name + " has no case in radioPropertyChangedHandler");
                int brk = source.IndexOf("break;", at, StringComparison.Ordinal);
                Assert.Contains("DescribeUnsavedProfileChanges()", source.Substring(at, brk - at));
            }
        }

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative));
    }
}
