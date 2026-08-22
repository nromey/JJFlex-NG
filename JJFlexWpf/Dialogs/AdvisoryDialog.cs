using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Screen-reader-first replacement for MessageBox.Show on informational
/// advisories.
///
/// The body is a read-only text box, not a static label, so the user can
/// arrow up and down through it line by line and re-read at their own pace —
/// a message box's text can only be heard once, on the dialog's terms.
/// Focus lands in the text on open: the base dialog speaks the title (the
/// one-line gist), then the screen reader reads from the text naturally.
/// Callers should NOT pre-announce "details in the message box" — the
/// dialog speaking for itself is the point.
///
/// Optional action buttons sit one tab past the text ("Open Radio Setup"
/// beats directions to it — the app does the walking). An optional
/// "Don't show this again" checkbox persists through
/// <see cref="AdvisorySuppression"/>; Show() returns without displaying
/// anything when the key is already suppressed.
/// </summary>
public sealed class AdvisoryDialog : JJFlexDialog
{
    /// <summary>A labelled choice; its action runs after the dialog has closed.</summary>
    public sealed record AdvisoryAction(string Label, Action OnChosen);

    private readonly CheckBox? _dontShowAgain;
    private Action? _chosenAction;

    private AdvisoryDialog(string title, string body, string? suppressKey,
        IReadOnlyList<AdvisoryAction> actions)
    {
        Title = title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 580;

        var root = new StackPanel { Margin = new Thickness(12) };

        var text = new TextBox
        {
            Text = ScreenReaderText.NormalizeLineBreaks(body),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400,
        };
        AutomationProperties.SetName(text, Radios.Lexicon.Get("connect.dialog.message_label"));
        root.Children.Add(text);

        if (suppressKey != null)
        {
            string dontShowAgain = Radios.Lexicon.Get("connect.dialog.dont_show_again");
            _dontShowAgain = new CheckBox
            {
                Content = dontShowAgain,
                Margin = new Thickness(0, 10, 0, 0),
            };
            AutomationProperties.SetName(_dontShowAgain, dontShowAgain.Replace("_", ""));
            root.Children.Add(_dontShowAgain);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        // Tab order: text, then the buttons (the likely next act), then the
        // rarely-wanted suppression checkbox last — even though the checkbox
        // sits above the buttons visually.
        System.Windows.Input.KeyboardNavigation.SetTabIndex(text, 1);
        System.Windows.Input.KeyboardNavigation.SetTabIndex(buttons, 2);
        if (_dontShowAgain != null)
            System.Windows.Input.KeyboardNavigation.SetTabIndex(_dontShowAgain, 3);

        foreach (var action in actions)
        {
            var button = new Button
            {
                Content = action.Label,
                MinWidth = 110,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
            };
            AutomationProperties.SetName(button, action.Label.Replace("_", ""));
            button.Click += (_, _) =>
            {
                // Run after close, not here: the action typically opens another
                // modal dialog, and it should own the foreground cleanly rather
                // than nesting inside this one.
                _chosenAction = action.OnChosen;
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(button);
        }

        string closeLabel = Radios.Lexicon.Get("connect.dialog.close");
        var close = new Button
        {
            Content = closeLabel,
            MinWidth = 80,
            Height = 28,
            IsDefault = true,
            IsCancel = true,
        };
        AutomationProperties.SetName(close, closeLabel.Replace("_", ""));
        // IsDefault registers the literal \r character as an access key, and
        // NVDA reads it back as "Close button, carriage return". Explicit
        // values preempt the phantom one.
        AutomationProperties.SetAccessKey(close, "Alt+C");
        AutomationProperties.SetAcceleratorKey(close, "Enter");
        close.Click += (_, _) =>
        {
            if (DialogResult == null) DialogResult = false;
            Close();
        };
        buttons.Children.Add(close);

        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>
    /// Show the advisory modally. No-op when <paramref name="suppressKey"/> is
    /// already suppressed; passing a key also adds the "Don't show this again"
    /// checkbox. Any chosen action runs after the dialog closes.
    /// </summary>
    public static void Show(string title, string body, string? suppressKey = null,
        params AdvisoryAction[] actions)
    {
        if (suppressKey != null && AdvisorySuppression.IsSuppressed(suppressKey))
            return;

        var dialog = new AdvisoryDialog(title, body, suppressKey, actions);
        dialog.ShowModalDialog();

        if (suppressKey != null && dialog._dontShowAgain?.IsChecked == true)
            AdvisorySuppression.Suppress(suppressKey);

        dialog._chosenAction?.Invoke();
    }
}
