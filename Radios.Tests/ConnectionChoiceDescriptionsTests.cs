using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Each connection-mode choice in Settings shows a bold heading with a
/// description under it, and its accessible name is the heading alone, so a
/// screen reader heard the heading and never the description (#1105). The
/// description now answers Ctrl+F1 on the choice, through
/// <c>JJFlexHelp.Text</c>: on demand, not on focus, because a radio group is
/// walked with the arrows and HelpText would recite four sentences on every
/// press (#91).
/// </summary>
/// <remarks>
/// Source-read, as <see cref="ReadOnlyNotesNotTabStopsTests"/> is, because this
/// assembly cannot load WPF. What is verified is the markup: each button's
/// <c>local:JJFlexHelp.Text</c> carries the words shown under its heading.
/// </remarks>
public sealed class ConnectionChoiceDescriptionsTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Local = "clr-namespace:JJFlexWpf";

    private static readonly string[] Choices =
    {
        "Tier1Radio", "Tier2Radio", "Tier3Radio",
        "RadioProfileAutoRadio", "RadioProfileForwardRadio", "RadioProfilePunchRadio",
    };

    /// <summary>
    /// Every problem with the named radio buttons: missing, no description shown,
    /// no on-demand help, help that leaves the description out, or the description
    /// moved into a slot read on every focus.
    /// </summary>
    internal static List<string> Problems(XDocument doc, IEnumerable<string> names)
    {
        var problems = new List<string>();
        foreach (string name in names)
        {
            var button = doc.Descendants(Presentation + "RadioButton")
                .FirstOrDefault(e => (string?)e.Attribute(X + "Name") == name);
            if (button == null) { problems.Add(name + ": not found"); continue; }

            // The description is the shown text that is not the heading: the
            // wrapped TextBlock under the semibold one.
            var description = button.Descendants(Presentation + "TextBlock")
                .Where(t => (string?)t.Attribute("FontWeight") == null)
                .Select(t => (string?)t.Attribute("Text"))
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (description == null) { problems.Add(name + ": shows no description"); continue; }

            string? help = (string?)button.Attribute(Local + "JJFlexHelp.Text");
            if (string.IsNullOrWhiteSpace(help)) problems.Add(name + ": no JJFlexHelp.Text, so Ctrl+F1 cannot read its description");
            else if (!help.Contains(description, StringComparison.Ordinal))
                problems.Add(name + ": its JJFlexHelp.Text does not carry the description shown under it");

            string? hint = (string?)button.Attribute("AutomationProperties.HelpText");
            if (hint != null && hint.Contains(description, StringComparison.Ordinal))
                problems.Add(name + ": the description is in HelpText, which is read on every arrow press");
            string? spoken = (string?)button.Attribute("AutomationProperties.Name");
            if (spoken != null && spoken.Contains(description, StringComparison.Ordinal))
                problems.Add(name + ": the description is in the accessible name");
        }
        return problems;
    }

    private static XDocument Settings() =>
        XDocument.Load(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));

    [Fact]
    public void Every_connection_choice_reads_its_description_on_ctrl_f1()
    {
        var problems = Problems(Settings(), Choices);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private const string Planted = @"<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:JJFlexWpf'>
  <StackPanel>
    <RadioButton x:Name='Bare' AutomationProperties.Name='Heading'>
      <StackPanel><TextBlock Text='Heading' FontWeight='SemiBold'/><TextBlock TextWrapping='Wrap' Text='What it does.'/></StackPanel>
    </RadioButton>
    <RadioButton x:Name='Short' AutomationProperties.Name='Heading' local:JJFlexHelp.Text='a short phrase'>
      <StackPanel><TextBlock Text='Heading' FontWeight='SemiBold'/><TextBlock TextWrapping='Wrap' Text='What it does.'/></StackPanel>
    </RadioButton>
    <RadioButton x:Name='OnFocus' AutomationProperties.Name='Heading' AutomationProperties.HelpText='What it does.' local:JJFlexHelp.Text='What it does.'>
      <StackPanel><TextBlock Text='Heading' FontWeight='SemiBold'/><TextBlock TextWrapping='Wrap' Text='What it does.'/></StackPanel>
    </RadioButton>
    <RadioButton x:Name='Good' AutomationProperties.Name='Heading' local:JJFlexHelp.Text='What it does.'>
      <StackPanel><TextBlock Text='Heading' FontWeight='SemiBold'/><TextBlock TextWrapping='Wrap' Text='What it does.'/></StackPanel>
    </RadioButton>
  </StackPanel>
</Window>";

    [Fact]
    public void The_check_catches_a_planted_choice_with_no_help_short_help_or_help_read_on_focus()
    {
        var doc = XDocument.Parse(Planted);
        Assert.Single(Problems(doc, new[] { "Bare" }));
        Assert.Single(Problems(doc, new[] { "Short" }));
        Assert.Single(Problems(doc, new[] { "OnFocus" }));
        Assert.Empty(Problems(doc, new[] { "Good" }));
    }
}
