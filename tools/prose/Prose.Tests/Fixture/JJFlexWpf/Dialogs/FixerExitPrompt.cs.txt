using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The way out of the transmit checks, asked as the three choices that
/// actually exist (#376) — not as a yes-or-no over a compound question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Noel asked for this at the bench, 2026-08-28:</b> "Why not give the
/// person three options, yes, exit without saving, no, continue the test, or
/// stop the test in progress, use the (whatever we call it tool) to resume."
/// The third capability — resume from the saved test runs list — was fully
/// built and simply never offered at the one moment an operator needs it.
/// </para>
/// <para>
/// <b>The order and the default are deliberate.</b> His order: exit without
/// saving, continue, stop-and-resume-later. Continue is the default and the
/// Escape action — a stray Enter or a reflexive Escape must land on the one
/// choice that costs nothing. Each button carries its COST in its help text,
/// announced on focus, because "exit without saving" on a run with keyed
/// measurements in it throws away something paid for with RF.
/// </para>
/// <para>
/// The resume choice appears only when the caller says the run is genuinely
/// persisted and holds results — an offer to "pick it up later" over a
/// journal that never opened would be a lie with a button on it. The labels
/// must never sound like the page's "Stop everything" control, which is the
/// emergency abort; this prompt is the calm decision that control must never
/// be mistaken for.
/// </para>
/// <para>
/// Follows the AdvisoryDialog pattern: the question is a read-only text box
/// so it can be re-read line by line at the operator's own pace, and focus
/// lands in it on open — the base dialog speaks the title, the reader then
/// reads the question naturally, and Tab reaches the choices.
/// </para>
/// </remarks>
public sealed class FixerExitPrompt : JJFlexDialog
{
    /// <summary>What the operator decided.</summary>
    public enum Choice
    {
        /// <summary>Stay in the checks. The default, and what closing this
        /// prompt by any other means answers.</summary>
        Continue = 0,
        /// <summary>Close the window and keep the saved run for later.</summary>
        ResumeLater,
        /// <summary>Close the window and delete what was recorded.</summary>
        DiscardAndExit,
    }

    private Choice _choice = Choice.Continue;

    private FixerExitPrompt(string title, string question,
                            string exitWithoutSavingHelp, string? resumeLaterHelp)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;

        var root = new StackPanel { Margin = new Thickness(12) };

        var text = new TextBox
        {
            Text = ScreenReaderText.NormalizeLineBreaks(question),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 220,
        };
        AutomationProperties.SetName(text, Radios.Lexicon.Get("connect.dialog.message_label"));
        root.Children.Add(text);

        var buttons = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        // His order, kept: the destructive choice first because he named it
        // first, the safe one as the default. Vertical, because these labels
        // are sentences-in-miniature and a row would truncate them.
        buttons.Children.Add(MakeChoice("Exit without saving", exitWithoutSavingHelp,
                                        Choice.DiscardAndExit));

        Button keep = MakeChoice("Continue the test",
            "Nothing changes. You go back to the tests where you left off.",
            Choice.Continue);
        keep.IsDefault = true;
        keep.IsCancel = true;
        // IsDefault registers the literal \r character as an access key, and
        // NVDA reads it back as "carriage return". Explicit values preempt
        // the phantom one — the AdvisoryDialog precedent.
        AutomationProperties.SetAcceleratorKey(keep, "Enter");
        buttons.Children.Add(keep);

        if (resumeLaterHelp != null)
            buttons.Children.Add(MakeChoice("Stop tests and resume later",
                                            resumeLaterHelp, Choice.ResumeLater));

        root.Children.Add(buttons);
        Content = root;
    }

    private Button MakeChoice(string label, string help, Choice choice)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 220,
            Height = 30,
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, label);
        JJFlexHelp.SetText(button, help);
        button.Click += (_, _) =>
        {
            _choice = choice;
            CloseWithResult(true);
        };
        return button;
    }

    /// <summary>
    /// Ask, modally. <paramref name="resumeLaterHelp"/> null means the run is
    /// not genuinely resumable and the third choice is not offered. Closing
    /// the prompt any other way answers Continue — the choice that costs
    /// nothing.
    /// </summary>
    public static Choice Ask(Window? owner, string title, string question,
                             string exitWithoutSavingHelp, string? resumeLaterHelp)
    {
        var prompt = new FixerExitPrompt(title, question,
                                         exitWithoutSavingHelp, resumeLaterHelp);
        if (owner != null) prompt.Owner = owner;
        prompt.ShowModalDialog();
        return prompt._choice;
    }
}
