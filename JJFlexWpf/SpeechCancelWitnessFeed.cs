using System;
using System.Windows;
using System.Windows.Input;
using Radios.Speech;

namespace JJFlexWpf;

/// <summary>
/// Feeds <see cref="SpeechCancelWitness"/> from WPF: a key going down and
/// keyboard focus moving, on every element of every window, on every thread
/// (#709 item 3). Diagnostic only: it reads the events, never handles them.
/// </summary>
/// <remarks>
/// <para>
/// Class handlers on <see cref="UIElement"/>, registered once for the process
/// with handledEventsToo, so a key a field has already handled is still
/// seen. Each routed event visits every element on its route, so only the
/// visit to the element the event is about (the original source) is noted.
/// </para>
/// <para>
/// Deliberately NOT in <c>MainWindow</c>'s key handler: that handler sees
/// only Home, and dialogs are where a cancel most needs naming.
/// </para>
/// <para>
/// <b>Not proven in the running app by a test.</b> Unit tests drive the
/// witness with planted events; whether WPF delivers these two routed events
/// to a class handler on every window is the harness's to confirm (a key
/// pressed during a long announcement, then the trace's line).
/// </para>
/// </remarks>
public static class SpeechCancelWitnessFeed
{
    private static int _installed;

    /// <summary>Register the class handlers. Idempotent; call once from the shell.</summary>
    public static void Install()
    {
        if (System.Threading.Interlocked.Exchange(ref _installed, 1) == 1) return;
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), handledEventsToo: true);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || !ReferenceEquals(sender, e.OriginalSource)) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        SpeechCancelWitness.Shared.Note(SpeechCancelWitness.EventKind.Key, key.ToString());
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource)) return;
        SpeechCancelWitness.Shared.Note(SpeechCancelWitness.EventKind.FocusChange, Describe(e.NewFocus));
    }

    internal static string Describe(object? element)
    {
        if (element == null) return "nothing";
        string type = element.GetType().Name;
        return element is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? type + " " + fe.Name : type;
    }
}
