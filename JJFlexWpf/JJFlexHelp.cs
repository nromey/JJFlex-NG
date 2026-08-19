using System;
using System.Windows;
using System.Windows.Media;

namespace JJFlexWpf;

/// <summary>
/// The on-demand explanation channel for Ctrl+F1 ("Explain this").
///
/// WHY THIS EXISTS — the HelpText defect (#91). AutomationProperties.HelpText
/// is a UIA property, and NVDA reads a control's UIA HelpText aloud as its
/// DESCRIPTION every time the control gains focus. The 2026-08-18 change that
/// moved long explanations out of accessible NAMES and into HelpText therefore
/// changed nothing the operator could hear: same words, same moment, same
/// cost — they just arrived from a different UIA slot. Settings stayed noisy.
///
/// JJFlexHelp.Text is a plain WPF attached property. It is NOT an
/// AutomationProperties member, no AutomationPeer surfaces it, and UIA has no
/// reason to announce a property it does not know about. The ONLY reader is
/// the Ctrl+F1 handler, which makes the text genuinely on-demand: silent on
/// focus, spoken when asked for.
///
/// RULES OF USE:
/// - Long explanations ("what does this actually do, and how do I set it")
///   go here, never in AutomationProperties.HelpText.
/// - AutomationProperties.HelpText remains legitimate ONLY for a short
///   interaction hint a screen reader SHOULD read on every focus (the
///   canonical example: CycleFieldControl's "Arrows to change"). If you are
///   writing a sentence, it belongs here instead.
/// - The Ctrl+F1 walk checks this property first and HelpText second at each
///   element, so a control carrying only a hint still answers the key.
///
/// XAML: with xmlns:local="clr-namespace:JJFlexWpf" in scope, write
/// local:JJFlexHelp.Text="...". Code-behind: JJFlexHelp.SetText(element, "...").
/// </summary>
public static class JJFlexHelp
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(JJFlexHelp),
            new FrameworkPropertyMetadata(null));

    public static void SetText(DependencyObject element, string? value) =>
        element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) =>
        (string?)element.GetValue(TextProperty);

    /// <summary>
    /// Find the explanation for the control the operator would say they are
    /// "on": starting at <paramref name="start"/>, walk toward the root, and
    /// at each element take JJFlexHelp.Text first, then
    /// AutomationProperties.HelpText. First non-empty answer wins, so the
    /// nearest explanation beats an outer one and the on-demand text beats
    /// the focus-time hint on the same element.
    ///
    /// The walk prefers the visual tree but falls back to the logical tree
    /// wherever the visual chain runs out. That fallback is load-bearing:
    /// inside a dropped-down ComboBox (or any Popup) the visual chain ends at
    /// the popup's root, which has no visual parent, while the logical chain
    /// still leads to the ComboBox that carries the explanation.
    /// </summary>
    /// <param name="start">The element to start from, usually keyboard focus.</param>
    /// <param name="trace">Optional per-step observer for diagnostics.</param>
    public static string? FindExplanation(
        DependencyObject? start, Action<string>? trace = null)
    {
        var node = start;
        int guard = 0; // trees are finite, but a cycle here would hang the UI thread
        while (node != null && guard++ < 128)
        {
            string? help = GetText(node);
            string source = "JJFlexHelp";
            if (string.IsNullOrWhiteSpace(help))
            {
                help = System.Windows.Automation.AutomationProperties.GetHelpText(node);
                source = "HelpText";
            }
            trace?.Invoke(
                $"walk {node.GetType().Name} " +
                (string.IsNullOrWhiteSpace(help) ? "(none)" : source + "='" + help + "'"));
            if (!string.IsNullOrWhiteSpace(help))
                return help;

            node = ParentOf(node);
        }
        return null;
    }

    /// <summary>
    /// One step toward the root: visual parent where one exists, logical
    /// parent where it does not (Popup roots, ContextMenus, content elements
    /// like Run/Paragraph that are not Visuals at all).
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node)
    {
        DependencyObject? parent = null;
        if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            parent = VisualTreeHelper.GetParent(node);
        return parent ?? LogicalTreeHelper.GetParent(node);
    }
}
