using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #744: the Feature Availability box on the Radio Information dialog must
    /// show its caret while read-only, or a screen reader's Down, Up and
    /// Ctrl+End all re-read the first line and the rest of the status is
    /// unreachable.
    /// </summary>
    /// <remarks>
    /// Read from the XAML, because the dialog is a window and Radios.Tests
    /// cannot build one. Arrowing through the lines under NVDA on a real build
    /// is still the bench's.
    /// </remarks>
    public sealed class FeatureAvailabilityCaretTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        [Fact]
        public void The_feature_availability_box_shows_its_caret_so_every_line_can_be_read()
        {
            string path = Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf/Dialogs/RadioInfoDialog.xaml");
            var doc = XDocument.Load(path);

            var box = doc.Descendants()
                .Single(e => (string?)e.Attribute(X + "Name") == "FeatureAvailabilityBox");

            Assert.Equal("TextBox", box.Name.LocalName);
            Assert.Equal("True", (string?)box.Attribute("IsReadOnly"));
            Assert.Equal("True", (string?)box.Attribute("AcceptsReturn"));
            Assert.Equal("True", (string?)box.Attribute("IsReadOnlyCaretVisible"));
        }
    }
}
