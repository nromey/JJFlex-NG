using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// The invariants themselves. Each one takes a realized dialog and its two
/// walked trees and yields findings.
///
/// <para>Every check here is deliberately phrased as a property that survives a
/// redesign. Nothing asserts an ordinal position, a control name, or a count -
/// the moment a suite asserts "the third tab stop is Load Preset" it starts
/// failing for reasons nobody cares about, and a suite that cries wolf is worse
/// than no suite.</para>
/// </summary>
public static class InvariantChecks
{
    /// <summary>
    /// Elements that belong to another control's template are that control's
    /// internals: a ComboBox's editable TextBox, a ScrollViewer inside a
    /// TextBox. The control carries the name and the automation identity;
    /// holding its parts to the same standard produces noise, not defects.
    /// </summary>
    private static bool IsAuthored(UIElement element)
        => element is not FrameworkElement fe || fe.TemplatedParent == null;

    // ---------------------------------------------------------------- 1 -----

    public static IEnumerable<Finding> FocusableHasName(string dialog, TreeSnapshot snapshot)
    {
        foreach (var element in snapshot.VisualElements)
        {
            if (element is Window) continue;
            if (!IsAuthored(element)) continue;
            if (!TreeWalk.IsFocusableStop(element)) continue;

            var peer = TreeWalk.CreatePeer(element);
            if (peer == null) continue;   // no peer at all is invariant 2's problem, not this one

            var name = TreeWalk.GetNameSafely(peer);
            if (!string.IsNullOrWhiteSpace(name)) continue;

            yield return new Finding(
                Invariant.FocusableHasName, dialog, TreeWalk.Identify(element),
                "Keyboard focus can land here and the automation Name is empty, so a screen reader has nothing to announce.");
        }
    }

    // ---------------------------------------------------------------- 2 -----

    public static IEnumerable<Finding> AutomationSubtreeComplete(string dialog, RealizedDialog realized, TreeSnapshot snapshot)
    {
        // A peer that throws mid-walk is the Audio Workshop signature exactly:
        // the client walk dies there and everything below it is absent from the
        // tree while remaining focusable and correctly named.
        foreach (var fault in snapshot.Faults)
        {
            yield return new Finding(
                Invariant.AutomationSubtreeComplete, dialog, $"{fault.PeerType} at {fault.Path}",
                $"Automation peer threw during {fault.Operation} - the tree walk stops here and everything below it is invisible to a screen reader. {fault.Exception}");
        }

        if (!realized.LoadedFired)
        {
            // Do not report emptiness we caused ourselves.
            yield break;
        }

        var focusableAuthored = snapshot.VisualElements
            .Where(e => e is not Window)
            .Where(IsAuthored)
            .Where(TreeWalk.IsFocusableStop)
            .ToList();

        if (focusableAuthored.Count > 0 && snapshot.PeerOwners.Count <= 1)
        {
            yield return new Finding(
                Invariant.AutomationSubtreeComplete, dialog, "(window)",
                $"The window exposes an empty automation subtree while {focusableAuthored.Count} of its controls remain focusable.");
        }

        foreach (var element in focusableAuthored)
        {
            if (snapshot.PeerOwners.Contains(element)) continue;
            if (TreeWalk.CreatePeer(element) == null) continue;

            yield return new Finding(
                Invariant.AutomationSubtreeComplete, dialog, TreeWalk.Identify(element),
                "Focusable and has an automation peer, but the peer is not reachable by walking the tree from the window - a screen reader cannot see it.");
        }

        foreach (var finding in EmptyLabelledContainers(dialog, snapshot)) yield return finding;
    }

    /// <summary>
    /// A container that announces itself and contains nothing. This is the
    /// meters-panel signature: the slot UI is built once and never resynced, so
    /// a named slot survives with no controls inside it and the operator tabs
    /// into a heading with no content.
    /// </summary>
    private static IEnumerable<Finding> EmptyLabelledContainers(string dialog, TreeSnapshot snapshot)
    {
        foreach (var element in snapshot.VisualElements)
        {
            if (!IsAuthored(element)) continue;
            if (element is not (GroupBox or Expander or TabItem or HeaderedContentControl or HeaderedItemsControl)) continue;
            if (!TreeWalk.IsEffectivelyVisible(element)) continue;
            if (!snapshot.PeerOwners.Contains(element)) continue;

            var peer = TreeWalk.CreatePeer(element);
            if (peer == null) continue;
            var name = TreeWalk.GetNameSafely(peer);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var descendants = TreeWalk.VisualDescendantsAndSelf(element).Where(e => !ReferenceEquals(e, element)).ToList();
            var hasFocusable = descendants.Any(TreeWalk.IsFocusableStop);
            var hasText = descendants.OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
            if (hasFocusable || hasText) continue;

            yield return new Finding(
                Invariant.AutomationSubtreeComplete, dialog, TreeWalk.Identify(element),
                $"Named container \"{TreeWalk.Truncate(name, 60)}\" is in the automation tree with nothing inside it - no focusable control and no text.");
        }
    }

    // ---------------------------------------------------------------- 3 -----

    public static IEnumerable<Finding> HelpTextNotEmpty(string dialog, TreeSnapshot snapshot)
    {
        foreach (var element in snapshot.VisualElements)
        {
            if (!IsAuthored(element)) continue;

            if (HasLocalValue(element, AutomationProperties.HelpTextProperty))
            {
                var value = element.GetValue(AutomationProperties.HelpTextProperty) as string;
                if (string.IsNullOrWhiteSpace(value))
                {
                    yield return new Finding(
                        Invariant.HelpTextNotEmpty, dialog, TreeWalk.Identify(element),
                        "Declares AutomationProperties.HelpText and the text is empty.");
                }
            }

            if (HasLocalValue(element, JJFlexHelp.TextProperty))
            {
                var value = element.GetValue(JJFlexHelp.TextProperty) as string;
                if (string.IsNullOrWhiteSpace(value))
                {
                    yield return new Finding(
                        Invariant.HelpTextNotEmpty, dialog, TreeWalk.Identify(element),
                        "Declares JJFlexHelp.Text - the Ctrl+F1 explanation - and the text is empty, so Explain This has nothing to say here.");
                }
            }
        }
    }

    private static bool HasLocalValue(DependencyObject element, DependencyProperty property)
    {
        try { return element.ReadLocalValue(property) != DependencyProperty.UnsetValue; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 4 -----

    public static IEnumerable<Finding> FocusConserved(string dialog, TabOrderResult tab)
    {
        if (!tab.Executed) yield break;
        if (tab.Order.Count == 0) yield break;

        foreach (var stuck in tab.StuckAt)
        {
            yield return new Finding(
                Invariant.FocusConserved, dialog, TreeWalk.Identify(stuck),
                "Tab from here reported a move but focus did not change - the cycle dead-ends on this control.");
        }

        if (tab.Order.Count > 1 && !tab.Cycled && tab.StuckAt.Count == 0)
        {
            yield return new Finding(
                Invariant.FocusConserved, dialog, "(window)",
                $"Tab visited {tab.Order.Count} stops and never returned to the first one. Dialogs on this base are supposed to cycle.");
        }

        // N moves, N focus events. A move that produces no event is a stop the
        // screen reader is never told about.
        var successfulMoves = tab.MovesRequested - tab.StuckAt.Count;
        if (successfulMoves > 0 && tab.FocusEventsObserved < successfulMoves)
        {
            yield return new Finding(
                Invariant.FocusConserved, dialog, "(window)",
                $"{successfulMoves} focus moves raised only {tab.FocusEventsObserved} GotKeyboardFocus events. " +
                "A move with no event is a tab stop a screen reader is never told about.");
        }
    }

    // ---------------------------------------------------------------- 5 -----

    public static IEnumerable<Finding> UniqueAutomationIds(string dialog, TreeSnapshot snapshot)
    {
        var byId = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var node in snapshot.Peers)
        {
            string id;
            try { id = node.Peer.GetAutomationId() ?? string.Empty; }
            catch { continue; }
            if (string.IsNullOrEmpty(id)) continue;

            var identity = node.Owner != null ? TreeWalk.Identify(node.Owner) : node.Path;
            if (!byId.TryGetValue(id, out var list)) byId[id] = list = new List<string>();
            if (!list.Contains(identity, StringComparer.Ordinal)) list.Add(identity);
        }

        foreach (var (id, owners) in byId)
        {
            if (owners.Count < 2) continue;
            yield return new Finding(
                Invariant.UniqueAutomationIds, dialog, $"AutomationId \"{id}\"",
                "Used by more than one control in the same window: " + string.Join(" ; ", owners) +
                ". An automation id that names two things cannot be used to tell them apart.");
        }
    }

    // ---------------------------------------------------------------- 6 -----

    public static IEnumerable<Finding> KeyboardReachable(string dialog, TreeSnapshot snapshot, TabOrderResult tab)
    {
        if (!tab.Executed) yield break;

        var actionable = snapshot.VisualElements
            .Where(e => e is not Window)
            .Where(IsAuthored)
            .Where(TreeWalk.IsFocusableStop)
            .Where(TreeWalk.IsActionable)
            .ToList();

        foreach (var element in actionable)
        {
            if (tab.Reachable.Contains(element)) continue;

            yield return new Finding(
                Invariant.KeyboardReachable, dialog, TreeWalk.Identify(element),
                "Present, enabled and focusable, but neither Tab nor the arrow keys ever land on it. " +
                "A control the operator cannot reach is invisible no matter how well it is labelled.");
        }

        foreach (var element in actionable)
        {
            if (!tab.ArrowOnly.Contains(element)) continue;
            if (element is RadioButton) continue;      // arrowing a radio group is the documented pattern
            if (element is ListBoxItem or ComboBoxItem or TabItem) continue;

            yield return new Finding(
                Invariant.KeyboardReachable, dialog, TreeWalk.Identify(element),
                "Reachable only with the arrow keys, never by Tab. An operator who tabs through the dialog never encounters it.");
        }
    }

    /// <summary>
    /// Radio groups that Tab treats as a single stop. Not automatically a
    /// defect - it is textbook WPF and right on a settings page - but it is
    /// exactly what made the destructive option in the radio-remove dialog
    /// unencounterable, so it is reported as its own class for triage.
    /// </summary>
    public static IEnumerable<Finding> ArrowOnlyRadioOptions(string dialog, TabOrderResult tab)
    {
        if (!tab.Executed) yield break;

        foreach (var group in tab.ArrowOnly.OfType<RadioButton>()
                     .GroupBy(r => r.GroupName ?? string.Empty, StringComparer.Ordinal))
        {
            var options = group.ToList();
            if (options.Count == 0) continue;

            yield return new Finding(
                Invariant.KeyboardReachable, dialog,
                $"RadioButton group \"{(string.IsNullOrEmpty(group.Key) ? "(unnamed)" : group.Key)}\"",
                $"{options.Count} option(s) that Tab never visits, reachable only by arrowing: " +
                string.Join(" ; ", options.Select(TreeWalk.Identify)) +
                ". Correct WPF, and the exact shape that hid the destructive option in the remove-radio dialog.");
        }
    }
}
