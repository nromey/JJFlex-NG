using System.Windows;
using System.Windows.Input;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>The result of driving Tab, and then the arrow keys, around a dialog.</summary>
public sealed class TabOrderResult
{
    public bool Executed { get; init; }
    public string Diagnostic { get; set; } = string.Empty;

    /// <summary>Distinct stops visited by Tab, in order.</summary>
    public List<UIElement> Order { get; } = new();

    /// <summary>Everything Tab or the arrows can land on.</summary>
    public HashSet<UIElement> Reachable { get; } = new();

    /// <summary>Reachable by arrows but never by Tab.</summary>
    public HashSet<UIElement> ArrowOnly { get; } = new();

    /// <summary>Moves that reported success but produced no focus change.</summary>
    public List<UIElement> StuckAt { get; } = new();

    /// <summary>How many times MoveFocus was asked to move during the Tab walk.</summary>
    public int MovesRequested { get; set; }

    /// <summary>How many GotKeyboardFocus events the window actually saw during the Tab walk.</summary>
    public int FocusEventsObserved { get; set; }

    public bool Cycled { get; set; }
}

/// <summary>
/// Drives real WPF focus around a dialog.
///
/// <para><b>Why real focus is safe here.</b> Win32 keyboard focus is per message
/// queue. SetFocus can only target a window created by the calling thread, and
/// when that thread is not the foreground thread the call updates only that
/// thread's own focus record - the operator's foreground window keeps the
/// keyboard, and no foreground change event is raised for a screen reader to
/// announce. So the suite can move focus around the dialogs under test as often
/// as it likes without anything leaving the operator's hands. That is what makes
/// invariants 4 and 6 answerable by measurement instead of by re-implementing
/// WPF's tab algorithm and hoping the re-implementation is faithful.</para>
/// </summary>
public static class FocusWalk
{
    private const int MaxStops = 400;
    private const int MaxArrowSteps = 24;

    public static TabOrderResult Walk(RealizedDialog dialog)
    {
        var window = dialog.Window;

        var candidates = TreeWalk.VisualDescendantsAndSelf(window)
            .Where(e => e is not Window)
            .Where(TreeWalk.IsFocusableStop)
            .ToList();

        if (candidates.Count == 0)
        {
            return new TabOrderResult
            {
                Executed = true,
                Diagnostic = "No focusable element in the window at all.",
            };
        }

        if (TrySetFocus(candidates[0]) == null)
        {
            return new TabOrderResult
            {
                Executed = false,
                Diagnostic =
                    "WPF refused to place keyboard focus in this window, so tab order could not be measured " +
                    $"(realization strategy {dialog.Strategy}).",
            };
        }

        var result = new TabOrderResult { Executed = true };

        var focusEvents = 0;
        void CountFocus(object sender, KeyboardFocusChangedEventArgs e) => focusEvents++;
        window.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(CountFocus), handledEventsToo: true);

        try
        {
            RunTabCycle(result);
            result.FocusEventsObserved = focusEvents;
        }
        finally
        {
            window.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(CountFocus));
        }

        foreach (var stop in result.Order) result.Reachable.Add(stop);
        AddArrowReachable(result);

        return result;
    }

    private static void RunTabCycle(TabOrderResult result)
    {
        var start = (UIElement)Keyboard.FocusedElement!;
        var current = start;
        result.Order.Add(current);
        var seen = new HashSet<UIElement> { current };

        for (var step = 0; step < MaxStops; step++)
        {
            if (current is not FrameworkElement fe) break;

            bool moved;
            result.MovesRequested++;
            try
            {
                moved = fe.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            catch (Exception ex)
            {
                result.Diagnostic = "MoveFocus threw: " + TreeWalk.Describe(ex);
                break;
            }

            var next = Keyboard.FocusedElement as UIElement;
            if (!moved || next == null || ReferenceEquals(next, current))
            {
                result.StuckAt.Add(current);
                break;
            }

            current = next;
            if (ReferenceEquals(current, start) || !seen.Add(current))
            {
                result.Cycled = true;
                break;
            }

            result.Order.Add(current);
        }
    }

    /// <summary>
    /// A control that Tab never visits is not automatically unreachable - radio
    /// groups and list boxes are navigated with the arrows on purpose. So from
    /// every tab stop, walk the arrows too, and treat the union as what the
    /// keyboard can actually get to.
    /// </summary>
    private static void AddArrowReachable(TabOrderResult result)
    {
        var directions = new[]
        {
            FocusNavigationDirection.Down,
            FocusNavigationDirection.Right,
            FocusNavigationDirection.Up,
            FocusNavigationDirection.Left,
        };

        foreach (var stop in result.Order.ToList())
        {
            foreach (var direction in directions)
            {
                if (TrySetFocus(stop) == null) continue;
                var current = stop;

                for (var step = 0; step < MaxArrowSteps; step++)
                {
                    if (current is not FrameworkElement fe) break;
                    bool moved;
                    try { moved = fe.MoveFocus(new TraversalRequest(direction)); }
                    catch { break; }

                    var next = Keyboard.FocusedElement as UIElement;
                    if (!moved || next == null || ReferenceEquals(next, current)) break;

                    current = next;
                    if (result.Reachable.Add(current)) result.ArrowOnly.Add(current);
                    else if (!result.Order.Contains(current)) result.ArrowOnly.Add(current);
                }
            }
        }

        foreach (var stop in result.Order) result.ArrowOnly.Remove(stop);
    }

    private static UIElement? TrySetFocus(UIElement element)
    {
        try
        {
            element.Focus();
            var focused = Keyboard.FocusedElement as UIElement;
            return ReferenceEquals(focused, element) ? focused : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Releases focus so the next dialog starts clean.</summary>
    public static void Release()
    {
        try { Keyboard.ClearFocus(); } catch { /* nothing to clear */ }
    }
}
