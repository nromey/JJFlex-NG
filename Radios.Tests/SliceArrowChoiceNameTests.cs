using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The "Slice arrow direction" choices in Settings, Accessibility, are
    /// announced by the words they show (#1069): a screen reader user and a
    /// sighted helper read the same sentence.
    /// </summary>
    /// <remarks>
    /// <para>Each choice's label is a TextBlock INSIDE the radio button, which
    /// <see cref="SpokenNameContainsLabelTests"/> does not see (its scan reads
    /// only a <c>Content</c> or <c>Header</c> attribute), so this holds these
    /// two directly. Before the fix the down choice showed "Down arrow goes A
    /// to B to C (recommended)" and was announced "Down arrow moves toward
    /// slice B".</para>
    /// </remarks>
    public sealed class SliceArrowChoiceNameTests
    {
        [Theory]
        [InlineData("SliceOrderTopToBottomRadio")]
        [InlineData("SliceOrderBottomToTopRadio")]
        public void Each_slice_arrow_choice_is_announced_by_its_visible_text(string control)
        {
            string xaml = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(),
                "JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
            var m = Regex.Match(xaml,
                "<RadioButton x:Name=\"" + control + "\"(?<attrs>[^>]*)>\\s*<TextBlock Text=\"(?<shown>[^\"]*)\"",
                RegexOptions.Singleline);
            Assert.True(m.Success, control + " is not shaped as a radio button with a TextBlock label any more; re-point this test");
            var name = Regex.Match(m.Groups["attrs"].Value, "AutomationProperties\\.Name=\"(?<n>[^\"]*)\"");
            Assert.True(name.Success, control + " has no spoken name");
            Assert.Equal(m.Groups["shown"].Value, name.Groups["n"].Value);
            Assert.Contains("A to B to C", m.Groups["shown"].Value);   // positive control: the label was read
        }
    }
}
