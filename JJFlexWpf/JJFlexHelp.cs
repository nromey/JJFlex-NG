using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
///
/// STATUS NOTES (#211, 2026-08-27). A dialog's read-only note lines — "Currently
/// using X", "Saved device not connected" — used to be tab stops, because WPF
/// dialogs run in focus mode and a plain TextBlock is not somewhere the Tab key
/// goes. Focusable="True" made them reachable and, in doing so, put the
/// explanation AHEAD of the thing it explains in the one ordering a keyboard
/// operator actually walks: Shift+Tab from the device list landed on prose, and
/// concluding from that there was no control above it is the correct inference
/// from what the operator was given.
///
/// <see cref="SetNoteFor"/> registers a note against the CONTROL it describes.
/// The note keeps its words, keeps its place on screen and keeps its accessible
/// name — it simply stops being a stop. Ctrl+F1 on the control now answers with
/// the authored explanation AND every note registered to it, read live, so the
/// answer is never stale. See <see cref="FindExplanation"/>.
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
    /// The status notes registered against a control, in the order they were
    /// registered. Lives on the CONTROL, not on the note.
    /// </summary>
    private static readonly DependencyProperty NotesProperty =
        DependencyProperty.RegisterAttached(
            "Notes",
            typeof(List<DependencyObject>),
            typeof(JJFlexHelp),
            new FrameworkPropertyMetadata(null));

    /// <summary>
    /// Register <paramref name="note"/> as an explanation belonging to
    /// <paramref name="control"/>, so Ctrl+F1 on the control reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this for every read-only note that has stopped being a tab stop.
    /// Removing the tab stop alone would silently take the words away from the
    /// operator who needs them most, which is worse than the friction — the
    /// note has to stay REACHABLE, just not be walked through on the way
    /// somewhere else.
    /// </para>
    /// <para>
    /// The note's text is read at ASK time, not at registration time, so a
    /// line that changes twice a second answers with what it currently says.
    /// A note that is <see cref="UIElement.Visibility"/> Collapsed is skipped:
    /// a note the operator cannot see is one the app has decided does not
    /// apply, and reading it anyway would contradict the screen.
    /// </para>
    /// <para>
    /// Several notes may share one control; they are read in registration
    /// order after the control's own explanation.
    /// </para>
    /// </remarks>
    public static void SetNoteFor(DependencyObject note, DependencyObject control)
    {
        if (note == null || control == null) return;
        if (control.GetValue(NotesProperty) is not List<DependencyObject> notes)
        {
            notes = new List<DependencyObject>();
            control.SetValue(NotesProperty, notes);
        }
        if (!notes.Contains(note)) notes.Add(note);
    }

    /// <summary>
    /// The explanation <paramref name="node"/> offers by itself: its own
    /// on-demand text (or its focus-time hint), followed by the current words
    /// of any notes registered to it. Empty when it offers nothing.
    /// </summary>
    private static string OwnExplanation(
        DependencyObject node, Action<string>? trace, out string source)
    {
        // Three channels, nearest-first on this one element: a live Provider
        // (#184) outranks a static string, which outranks the UIA hint. Notes
        // (#211) are appended to whichever answered, because a note explains
        // the CONTROL, not the channel that happened to describe it.
        string? help = null;
        source = "Provider";
        var provider = GetProvider(node);
        if (provider != null)
        {
            try { help = provider(); }
            catch (Exception ex)
            {
                // A throwing provider must not take Ctrl+F1 down with it —
                // fall through to the static channels and say so.
                trace?.Invoke($"provider on {node.GetType().Name} threw: {ex.Message}");
            }
        }
        if (string.IsNullOrWhiteSpace(help))
        {
            help = GetText(node);
            source = "JJFlexHelp";
        }
        if (string.IsNullOrWhiteSpace(help))
        {
            help = System.Windows.Automation.AutomationProperties.GetHelpText(node);
            source = "HelpText";
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(help)) sb.Append(help!.Trim());

        if (node.GetValue(NotesProperty) is List<DependencyObject> notes)
        {
            foreach (DependencyObject note in notes)
            {
                if (note is UIElement ui && ui.Visibility != Visibility.Visible) continue;
                string words = note is TextBlock tb
                    ? tb.Text
                    : System.Windows.Automation.AutomationProperties.GetName(note);
                if (string.IsNullOrWhiteSpace(words)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(words.Trim());
                source += "+note";
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The DYNAMIC explanation channel (#184). Text is right for controls
    /// whose explanation is a constant; the Home Frequency field's answer
    /// depends on which tuning mode is live, where the cursor stands, and
    /// what the step values are — state that no attached string can carry.
    /// A provider is asked at the moment Ctrl+F1 is pressed, so its answer
    /// is the state the operator is actually in.
    ///
    /// Checked BEFORE Text at each step of the walk: an element that went to
    /// the trouble of computing its answer live outranks its own static
    /// fallback. A provider returning null or empty falls through to Text
    /// and HelpText on the same element, then to the parent.
    /// </summary>
    public static readonly DependencyProperty ProviderProperty =
        DependencyProperty.RegisterAttached(
            "Provider",
            typeof(Func<string?>),
            typeof(JJFlexHelp),
            new FrameworkPropertyMetadata(null));

    public static void SetProvider(DependencyObject element, Func<string?>? value) =>
        element.SetValue(ProviderProperty, value);

    public static Func<string?>? GetProvider(DependencyObject element) =>
        (Func<string?>?)element.GetValue(ProviderProperty);

    /// <summary>
    /// Find the explanation for the control the operator would say they are
    /// "on": starting at <paramref name="start"/>, walk toward the root, and
    /// at each element take JJFlexHelp.Provider first (live answers beat
    /// static ones), then JJFlexHelp.Text, then
    /// AutomationProperties.HelpText. First non-empty answer wins, so the
    /// nearest explanation beats an outer one and the on-demand text beats
    /// the focus-time hint on the same element.
    ///
    /// Any status notes registered to an element with <see cref="SetNoteFor"/>
    /// are read after that element's own explanation, in registration order
    /// and in their current words. An element carrying only notes still
    /// answers, so a control with no authored help is not silent just because
    /// its explanation happens to live in a line beneath it.
    ///
    /// Two readers: the Ctrl+F1 handler, and the availability cue (#275),
    /// which resolves the same walk after focus settles so its tone is an
    /// honest promise about what Ctrl+F1 would say.
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
            string help = OwnExplanation(node, trace, out string source);
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
