using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The tracking notch dialog restates the radio's limits, and this refuses
    /// to let the two copies drift apart (#482).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why there are two copies at all.</b> <c>TNFDialog</c> takes every TNF
    /// operation as a delegate in plain ints, strings and bools, and references
    /// neither <c>FlexBase</c> nor FlexLib's <c>TNF</c> type. That is a good
    /// boundary and it is not being moved for this — but it means the dialog
    /// cannot read <c>FlexBase.TNFWidthMinHz</c>, so it carries its own
    /// <c>WidthMin</c> to clamp what it shows.
    /// </para>
    /// <para>
    /// <b>Why drift here would be silent.</b> Both sides clamp, so the radio is
    /// never sent a bad value whatever happens — the failure is entirely in what
    /// the operator is told. Widen the dialog's ceiling past the radio layer's
    /// and the box displays 6000 while <c>FlexBase</c> quietly sends 5000; the
    /// number in the ear stops being the number on the air, and nothing throws,
    /// nothing logs, and no other test notices. Narrow it and the arrow keys
    /// stop at a rail that is not really there. Without sight the displayed
    /// value IS the feedback, so a display that disagrees with the radio is not
    /// a cosmetic defect.
    /// </para>
    /// <para>
    /// Reads SOURCE because the thing being checked is a literal a person wrote
    /// on each side, and because <c>Radios.Tests</c> cannot reference JJFlexWpf.
    /// </para>
    /// </remarks>
    public sealed class TNFLimitAgreementTests
    {
        private const string DialogSource = "JJFlexWpf/Dialogs/TNFDialog.xaml.cs";

        [Theory]
        [InlineData("WidthMin", FlexBase.TNFWidthMinHz)]
        [InlineData("WidthMax", FlexBase.TNFWidthMaxHz)]
        [InlineData("DepthMin", FlexBase.TNFDepthMin)]
        [InlineData("DepthMax", FlexBase.TNFDepthMax)]
        public void Dialog_limit_matches_the_radio_layer(string constant, int expected)
        {
            int actual = ConstantIn(DialogSource, constant);
            Assert.True(expected == actual,
                "TNFDialog's " + constant + " is " + actual + " but FlexBase says "
                + expected + ". The dialog clamps what it SHOWS and FlexBase clamps "
                + "what it SENDS, so a disagreement makes the box announce a value "
                + "the radio never received — silently, because both clamps still "
                + "work. Change both or neither.");
        }

        /// <summary>
        /// Positive control. A checker that cannot find its subject reports
        /// agreement for the same reason it would report anything else:
        /// nothing. This asserts the scan actually reads a value it could not
        /// have guessed.
        /// </summary>
        [Fact]
        public void The_scan_can_actually_read_a_constant()
        {
            Assert.Equal(50, ConstantIn(DialogSource, "WidthIncrement"));
        }

        private static int ConstantIn(string relative, string name)
        {
            string source = Read(relative);
            var m = Regex.Match(source,
                @"const\s+int\s+" + Regex.Escape(name) + @"\s*=\s*(-?\d+)\s*;");
            Assert.True(m.Success,
                "No `const int " + name + "` in " + relative + ". Either it was "
                + "renamed or the limits stopped being constants — in both cases "
                + "this check has stopped checking anything, which is worse than "
                + "the drift it exists to catch.");
            return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that "
                + "cannot find its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
