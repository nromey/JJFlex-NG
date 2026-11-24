using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The radio's address mode choices are announced with a name that
    /// CONTAINS their visible label, character for character. The shared label
    /// guard compares with punctuation set aside, so "Automatic, the router
    /// assigns the address. Default." passed it beside the visible "Automatic —
    /// the router assigns the address (default)" (found on the bench, #1121).
    /// This pins the exact containment for the two choices in the group.
    /// </summary>
    public sealed class StaticIpAddressModeNameTests
    {
        [Theory]
        [InlineData("DhcpRadio")]
        [InlineData("StaticRadio")]
        public void Each_address_mode_choice_is_named_with_its_visible_label_inside_it(string name)
        {
            string path = Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Controls", "StaticIpControl.xaml");
            Assert.True(File.Exists(path), "StaticIpControl.xaml not found at " + path);
            var doc = XDocument.Load(path);

            var radio = doc.Descendants()
                .SingleOrDefault(e => e.Name.LocalName == "RadioButton"
                    && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
            Assert.True(radio != null, "no RadioButton named " + name + "; this test would check nothing");

            string? spoken = radio!.Attributes()
                .SingleOrDefault(a => a.Name.LocalName == "AutomationProperties.Name")?.Value;
            string? visible = radio.Descendants()
                .Where(e => e.Name.LocalName == "TextBlock")
                .Select(e => e.Attribute("Text")?.Value)
                .SingleOrDefault();

            Assert.False(string.IsNullOrEmpty(spoken), name + " has no AutomationProperties.Name");
            Assert.False(string.IsNullOrEmpty(visible), name + " has no visible TextBlock label");
            Assert.Contains(visible!, spoken!, StringComparison.Ordinal);
        }
    }
}
