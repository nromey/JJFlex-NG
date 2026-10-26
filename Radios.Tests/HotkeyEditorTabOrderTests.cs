using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #705, Noel's bench ruling 2026-10-25 19:24: in the Hotkey Editor,
    /// Shift+Tab from the key list reaches Arrange by and Category, and Tab
    /// from the list goes straight to Change Key.
    /// </summary>
    /// <remarks>
    /// Read from the XAML, because the dialog is a window and Radios.Tests
    /// cannot build one. WPF tabs a group in tree order when no TabIndex is
    /// set, and the dialog base sets the window's tab navigation to Cycle, so
    /// the order of the focusable elements in the file IS the order Tab walks.
    /// That is why this test also refuses a TabIndex: one would make the file
    /// order a lie. Pressing Tab on a real build is still the bench's.
    /// </remarks>
    public sealed class HotkeyEditorTabOrderTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        [Fact]
        public void The_selectors_come_before_the_list_and_Change_Key_straight_after_it()
        {
            string path = Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf/Dialogs/KeysDialog.xaml");
            var doc = XDocument.Load(path);

            var order = doc.Descendants()
                .Select(e => (string?)e.Attribute(X + "Name"))
                .Where(n => n is "ViewCombo" or "CategoryCombo" or "KeysList" or "ChangeKeyButton"
                    or "UnbindButton" or "ResetButton" or "ResetAllButton" or "ExportButton" or "CloseButton")
                .ToList();

            Assert.Equal(new[]
            {
                "ViewCombo", "CategoryCombo", "KeysList",
                "ChangeKeyButton", "UnbindButton", "ResetButton", "ResetAllButton", "ExportButton", "CloseButton",
            }, order);

            Assert.DoesNotContain(doc.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName.EndsWith("TabIndex", StringComparison.Ordinal)));

            // Nothing focusable between the list and Change Key: the two
            // TextBlocks are not tab stops, and nothing else may sit there.
            var elements = doc.Descendants().ToList();
            int list = elements.FindIndex(e => (string?)e.Attribute(X + "Name") == "KeysList");
            int change = elements.FindIndex(e => (string?)e.Attribute(X + "Name") == "ChangeKeyButton");
            var between = elements.Skip(list + 1).Take(change - list - 1)
                .Where(e => e.Ancestors().All(a => a != elements[list]))
                .Select(e => e.Name.LocalName)
                .Where(n => n is "Button" or "ComboBox" or "TextBox" or "CheckBox" or "ListBox" or "ListView" or "RadioButton")
                .ToList();
            Assert.Empty(between);
        }
    }
}
