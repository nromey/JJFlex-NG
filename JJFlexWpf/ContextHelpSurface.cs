using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf;

/// <summary>
/// The navigable companion to Ctrl+F1: the same explanation Explain This
/// speaks, on a surface the operator can move through, re-read, and stop in
/// the middle of. Sprint 44 Track K (#519).
/// </summary>
/// <remarks>
/// <para>
/// <b>Noel, 2026-09-02:</b> <i>"Which key do we press to speak keys that are
/// available on the currently focussed field / tool? That one we need to have
/// also listed not just spoken out."</i>
/// </para>
/// <para>
/// <b>What was established before this was designed, because the brief said
/// to check rather than assume.</b> F1 is <c>ShowContextHelp</c> and opens the
/// help FILE. The dialog driven by <c>DisplayField.HelpItems</c> is
/// <c>ShowHelpDialog</c>, opened by <c>MainWindow.DisplayHelp()</c>, which is
/// the handler of the registry command <c>ShowHelp</c> ("Show keys help") —
/// and that command has no default key, so the per-field key list has existed
/// all along and been reachable only through the Command Finder. It DOES list
/// the keys, as rows. So for the Home fields this is a routing problem and
/// this class routes: it sends those cases to the list that already exists
/// rather than building a second one.
/// </para>
/// <para>
/// Everywhere else Ctrl+F1 speaks prose — a control's <see cref="JJFlexHelp"/>
/// explanation and its status notes — and prose wants a different control from
/// a key list: a read-only edit, where the arrows move by line, word and
/// character. That is what <see cref="Dialogs.ContextHelpDialog"/> is.
/// </para>
/// <para>
/// <b>No key reaches this yet.</b> Ctrl+F1 keeps speaking, exactly as today;
/// which key opens this, and whether Ctrl+F1 itself should open it when the
/// answer is long, is the dispatcher's decision and is reported rather than
/// taken here.
/// </para>
/// </remarks>
public static class ContextHelpSurface
{
    /// <summary>
    /// Show the explanation for <paramref name="focused"/> on a navigable
    /// surface. On a Home field or a field group, that is the per-field key
    /// list the main window already has; elsewhere it is the Ctrl+F1 text on
    /// a read-only edit. With nothing to show it says so, the same words
    /// Ctrl+F1 uses.
    /// </summary>
    public static void Present(DependencyObject? focused, MainWindow? mainWindow)
    {
        if (mainWindow != null &&
            (mainWindow.FreqOut.IsKeyboardFocusWithin || mainWindow.FieldsPanel.IsKeyboardFocusWithin))
        {
            mainWindow.DisplayHelp();
            return;
        }

        string? help = JJFlexHelp.FindExplanation(focused);
        if (string.IsNullOrWhiteSpace(help))
        {
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("help.context.none_here"),
                Radios.Speech.SpeechIntent.Interrupt, Radios.VerbosityLevel.Critical);
            return;
        }

        var dialog = new Dialogs.ContextHelpDialog
        {
            Subject = SubjectOf(focused),
            Body = help,
        };
        dialog.ShowModalDialog();
    }

    /// <summary>
    /// The accessible name of the control the operator is on, if it has one,
    /// so the reader's title says what is being explained.
    /// </summary>
    private static string SubjectOf(DependencyObject? focused)
    {
        if (focused == null) return "";
        try
        {
            string? name = AutomationProperties.GetName(focused);
            return string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        }
        catch
        {
            return "";
        }
    }
}
