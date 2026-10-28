using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// F6 and Shift+F6 between the sections of a tabbed window: the GroupBoxes
/// sitting directly in the selected tab's panel. One implementation, shared by
/// the Audio Workshop (where it began) and Settings (#822, ruled by Noel
/// 2026-10-25: "yeah I like f6 and shift f6").
/// </summary>
/// <remarks>
/// <para>
/// A section is a GroupBox, not a bold line of text, because a screen reader
/// announces a group as focus enters it and says nothing about a TextBlock.
/// Settings' sections were bold text until 2026-10-25, and Noel heard none of
/// them ("the groups or headings as you call them get ignored by NVDA").
/// </para>
/// <para>
/// Deliberately shallow: only GroupBoxes directly in the tab's own panel count,
/// so a group nested inside a section stays part of that section.
/// </para>
/// </remarks>
internal static class SectionNavigator
{
    /// <summary>The selected tab's own panel, unwrapping the containers a tab is built inside.</summary>
    internal static Panel? TabPanel(TabControl tabs)
    {
        object? content = (tabs.SelectedItem as TabItem)?.Content;
        for (int depth = 0; depth < 4 && content is not Panel; depth++)
        {
            content = content switch
            {
                ScrollViewer sv => sv.Content,
                Border b => b.Child,
                ContentControl cc => cc.Content,
                Decorator d => d.Child,
                _ => null,
            };
            if (content == null) break;
        }
        return content as Panel;
    }

    /// <summary>The visible sections of the selected tab, in order.</summary>
    internal static List<GroupBox> Sections(TabControl tabs) =>
        TabPanel(tabs) is Panel panel ? Sections(panel) : new List<GroupBox>();

    /// <summary>The visible sections directly in <paramref name="panel"/>, in order. For a window with no tabs (Check your audio, #835).</summary>
    internal static List<GroupBox> Sections(Panel panel)
    {
        var found = new List<GroupBox>();
        foreach (object child in panel.Children)
            if (child is GroupBox g && g.Visibility == Visibility.Visible)
                found.Add(g);
        return found;
    }

    /// <summary>
    /// Handle F6 or Shift+F6 for <paramref name="tabs"/>. True when it was one
    /// of them, whether or not there was anywhere to go.
    /// </summary>
    internal static bool TryHandle(KeyEventArgs e, TabControl tabs)
    {
        if (e.Key != Key.F6) return false;
        var modifiers = Keyboard.Modifiers;
        if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift) return false;
        Move(tabs, forward: modifiers == ModifierKeys.None);
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Move focus to the first control of the next or previous section, wrapping
    /// at the ends, and say which section it is: a programmatic focus change is
    /// not reliably narrated, and landing somewhere without being told where is
    /// the failure this exists to end.
    /// </summary>
    internal static void Move(TabControl tabs, bool forward) => Move(Sections(tabs), forward);

    /// <summary>F6 and Shift+F6 for a window with no tabs, over the sections directly in <paramref name="panel"/>.</summary>
    internal static void Move(Panel panel, bool forward) => Move(Sections(panel), forward);

    private static void Move(List<GroupBox> sections, bool forward)
    {
        if (sections.Count == 0) return;
        int current = -1;
        for (int i = 0; i < sections.Count; i++)
            if (sections[i].IsKeyboardFocusWithin) { current = i; break; }
        int next = current < 0
            ? (forward ? 0 : sections.Count - 1)
            : (forward ? (current + 1) % sections.Count : (current - 1 + sections.Count) % sections.Count);

        GroupBox target = sections[next];
        string name = target.Header as string ?? Lexicon.Get("audio.workshop.unnamed_section");
        if (!target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.workshop.section_nothing_to_adjust", ("section", name)),
                VerbosityLevel.Terse, true);
            return;
        }
        ScreenReaderOutput.Speak(name, VerbosityLevel.Terse, true);
    }
}
