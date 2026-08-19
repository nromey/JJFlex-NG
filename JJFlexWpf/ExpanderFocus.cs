using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace JJFlexWpf;

/// <summary>
/// Put keyboard focus on an <see cref="Expander"/>'s HEADER, not on the
/// Expander itself.
///
/// <para><b>The bug this exists to prevent, and why it is worth a shared
/// class.</b> An Expander's interactive part is a <see cref="ToggleButton"/>
/// inside its control template, and a ToggleButton is what responds to Space.
/// <c>Expander.Focus()</c> puts keyboard focus on the Expander CONTAINER —
/// focusable, but not the interactive element — so Space has no ToggleButton
/// to reach, and a screen reader is left describing a bare container. The two
/// symptoms always arrive together: Space does not toggle (Enter does), and
/// landing on the expander is SILENT when it is collapsed. They were never two
/// problems.</para>
///
/// <para><b>The failure mode is silence</b>, which is the one an operator
/// cannot report precisely and the one review cannot see. That is why this is
/// one function rather than a convention: the same fix was independently
/// derived twice — <c>ScreenFieldsPanel</c> in Sprint 28 and
/// <c>RigSelectorDialog</c> on 2026-08-19 — with neither implementation aware
/// of the other, and three further call sites were still focusing the
/// container raw. Task #105.</para>
///
/// <para><b>The ToggleButton is found by walking the visual tree, deliberately,
/// rather than by looking up the stock template part name ("HeaderSite").</b>
/// A restyle that renames the part would silently reintroduce exactly the bug
/// this fixes. Do not "optimise" this into a FindName call.</para>
/// </summary>
public static class ExpanderFocus
{
    /// <summary>
    /// Focus <paramref name="expander"/>'s header toggle. Returns true when
    /// focus actually landed somewhere.
    ///
    /// <para>Falls back to focusing the Expander itself if no ToggleButton is
    /// in its visual tree — focus always lands on something real, even under a
    /// custom template that has no toggle at all.</para>
    /// </summary>
    public static bool FocusHeader(Expander? expander)
    {
        if (expander == null) return false;

        // ApplyTemplate so the header exists even on the very FIRST focus,
        // before the expander has ever been rendered. Without this the tree
        // walk finds nothing on a cold dialog and quietly falls through to the
        // container — the silent case again, arriving only on the first press.
        expander.ApplyTemplate();

        var toggle = FindDescendant<ToggleButton>(expander);
        if (toggle != null && toggle.Focus()) return true;

        return expander.Focus();
    }

    /// <summary>
    /// First descendant of type <typeparamref name="T"/> in the visual tree,
    /// depth-first, or null. Shared because the expander walk needs it; safe
    /// for any visual-tree lookup where the element has no name to bind to.
    /// </summary>
    public static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
