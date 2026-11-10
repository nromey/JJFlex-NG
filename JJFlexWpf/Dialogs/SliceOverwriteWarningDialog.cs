using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;
using Radios.Speech;

namespace JJFlexWpf.Dialogs;

/// <summary>What the operator chose in the overwrite warning (#999).</summary>
public enum SliceOverwriteAnswer { Record, BackUpFirst, Cancel }

/// <summary>The overwrite warning's answer, and whether its "don't show this again" box was ticked and honoured.</summary>
public sealed record SliceOverwriteReply(SliceOverwriteAnswer Answer, bool DontShowAgain);

/// <summary>
/// The warning before a recording starts over one already in the slice (#999).
/// Two forms: <b>not backed up</b> offers Record, Back up first and Cancel;
/// <b>backed up</b> offers Record and Cancel. Both carry "don't show this again".
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled S4 (Noel, 2026-11-07 20:40 to 20:42):</b> Record is the default button
/// and has focus when the warning opens, so F11 then Enter records at once.
/// Escape cancels. Tab order: Record, then Back up first (in the not-backed-up
/// form), then Cancel, then the box. The warning opens with a line telling the
/// operator what to press, and that line is the dialog's first text and is said
/// when the warning opens.
/// </para>
/// <para>
/// <b>The box takes effect only when the operator goes ahead</b> (Record or Back
/// up first). Cancel or Escape leaves the setting as it was, even with the box
/// ticked, so an operator who backs out is never silently opted out. This is the
/// orchestrator's choice, not a ruling; the #999 report flags it.
/// </para>
/// <para>
/// <b>A held or repeated F11 never answers it.</b> F11 is swallowed here, every
/// press of it, and a repeated Enter is ignored, so only a fresh Enter presses
/// Record.
/// </para>
/// <para>Every sentence is a PLACEHOLDER for Codex (Noel's ruling 2026-11-07).</para>
/// </remarks>
public sealed class SliceOverwriteWarningDialog : JJFlexDialog
{
    private readonly Button _record;
    private readonly CheckBox _dontShow;
    private readonly string _opening;

    /// <summary>What was chosen; Cancel until a button is pressed.</summary>
    public SliceOverwriteReply Reply { get; private set; } = new(SliceOverwriteAnswer.Cancel, false);

    /// <param name="backedUp">True for the backed-up form, which offers no Back up first.</param>
    /// <param name="letter">The slice whose recording would be replaced.</param>
    /// <param name="seconds">That recording's length, as the radio reports it.</param>
    public SliceOverwriteWarningDialog(bool backedUp, string letter, double seconds)
    {
        // PLACEHOLDER: Codex writes (#999). Every string here is an audio.recorder.overwrite.* key.
        string length = ((int)Math.Round(seconds)).ToString(CultureInfo.CurrentCulture);
        Title = Lexicon.Get("audio.recorder.overwrite.title", ("letter", letter));
        // The opening line: Noel's own draft for the not-backed-up form, verbatim.
        _opening = Lexicon.Get(backedUp ? "audio.recorder.overwrite.opening_backed_up" : "audio.recorder.overwrite.opening_not_backed_up",
            ("letter", letter));
        string detail = Lexicon.Get(backedUp ? "audio.recorder.overwrite.detail_backed_up" : "audio.recorder.overwrite.detail_not_backed_up",
            ("letter", letter), ("seconds", length));
        AutomationProperties.SetHelpText(this, _opening);
        Width = 460;
        SizeToContent = SizeToContent.Height;

        var root = new StackPanel { Margin = new Thickness(16) };
        // The opening line is the dialog's first text.
        root.Children.Add(new TextBlock { Text = _opening, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        // Children in TAB ORDER: Record, Back up first, Cancel, then the box.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _record = AddButton(buttons, "audio.recorder.overwrite.record", "audio.recorder.overwrite.help.record",
            () => Answer(SliceOverwriteAnswer.Record));
        _record.IsDefault = true;
        if (!backedUp)
            AddButton(buttons, "audio.recorder.overwrite.back_up_first", "audio.recorder.overwrite.help.back_up_first",
                () => Answer(SliceOverwriteAnswer.BackUpFirst));
        AddButton(buttons, "audio.recorder.overwrite.cancel", "audio.recorder.overwrite.help.cancel",
            () => Answer(SliceOverwriteAnswer.Cancel));
        root.Children.Add(buttons);

        string dontShow = Lexicon.Get("audio.recorder.overwrite.dont_show");
        _dontShow = new CheckBox { Content = dontShow, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(_dontShow, dontShow);
        JJFlexHelp.SetText(_dontShow, Lexicon.Get("audio.recorder.overwrite.help.dont_show"));
        root.Children.Add(_dontShow);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            // A held or repeated F11 must not answer the warning, and neither
            // must the F11 that opened it arriving again: F11 does nothing here.
            if (e.Key == Key.F11 || e.SystemKey == Key.F11) { e.Handled = true; return; }
            if (e.Key == Key.Enter && e.IsRepeat) e.Handled = true;
        };
        // The opening line is said when the warning opens (S4) by the screen
        // reader itself: it is the window's description (HelpText above), read
        // with the title as the warning takes focus. We do not also speak it.
        // Measured on the 8600 desk check, 2026-11-09: our queued copy repeated
        // the description 1.35 s later, and when Enter or Back up first answered
        // the warning first, the arbiter salvaged the copy and replayed "press
        // Enter to record" after the warning had closed, over a recording
        // already running.
    }

    /// <summary>Record has focus when the warning opens (S4).</summary>
    protected override void FocusFirstControl() => _record.Focus();

    private void Answer(SliceOverwriteAnswer answer)
    {
        // The box counts only when the operator goes ahead.
        bool dontShow = answer != SliceOverwriteAnswer.Cancel && _dontShow.IsChecked == true;
        Reply = new(answer, dontShow);
        CloseWithResult(answer != SliceOverwriteAnswer.Cancel);
    }

    private static Button AddButton(Panel panel, string labelKey, string helpKey, Action onClick)
    {
        string label = Lexicon.Get(labelKey);
        var button = new Button { Content = label, MinWidth = 96, Height = 28, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(button, label);
        JJFlexHelp.SetText(button, Lexicon.Get(helpKey));
        button.Click += (_, _) => onClick();
        panel.Children.Add(button);
        return button;
    }
}
