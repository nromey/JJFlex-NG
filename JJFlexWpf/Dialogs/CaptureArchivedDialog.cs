using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJTrace;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// "Your radio's connection went while I was recording. Here is the file, and
/// here is its path." Opened when <see cref="Radios.CaptureArchive"/> has archived a
/// session as <c>connection_dropped</c> (#566's bridge, Sprint 45 Track H).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled by Noel, 2026-09-22 19:34:</b> <i>"if the system has to seal, it
/// needs a dialog that allows user to read the error and copy the capture path
/// to clipboard, just make it easy to get to until infra is set things up to
/// upload."</i> The temporary clause is the design brief for the whole window:
/// it is a bridge to an upload that does not exist yet, and when the upload
/// lands, the copy-the-path step is what goes away, not the window.
/// </para>
/// <para>
/// <b>Escape closes it</b>, like every dialog — inherited from
/// <see cref="JJFlexDialog"/>, which is also what announces the title when it
/// opens. Nothing here speaks over that.
/// </para>
/// <para>
/// <b>Two reachable controls, in the order the errand runs.</b> The explanation
/// is one read-only box the operator can arrow through; then the path, in its
/// own read-only box so it can be read a character at a time and selected with
/// Ctrl+A, Ctrl+C by anyone who would rather not trust a button; then Copy
/// path, which is the default, because pressing Enter on arrival should do the
/// thing the window exists for.
/// </para>
/// <para>
/// <b>Modal, deliberately.</b> A non-modal window can be buried behind the
/// shell, and a window a blind operator cannot find is a window that did not
/// open. <see cref="JJFlexDialog.ShowModalDialog"/> also pushes the attention
/// claim that makes ConnectingForm's focus-reclaim timer stand down — which is
/// precisely the collision a mid-connect drop creates (#331).
/// </para>
/// <para>
/// <b>NOT gated on "are we transmitting".</b> That gate was considered and is a
/// trap here. <c>FlexBase.Transmit</c> only changes when the radio REPORTS
/// Mox, and a radio whose connection has died reports nothing — so the flag
/// stays true after exactly the drop this window exists for, and gating on it
/// would suppress the dialog in the one case that matters. There is no RF
/// either way: the radio is gone.
/// </para>
/// </remarks>
public sealed class CaptureArchivedDialog : JJFlexDialog
{
    private readonly CaptureArchiveNotice _notice;
    private readonly TextBlock _status = new();

    /// <summary>
    /// Internal rather than private since Track H7 so <c>JJFlexWpf.Tests</c>
    /// can realise the window non-modally under its own guard and read what
    /// the explanation box carries when a recording's recovery is at risk.
    /// Production still enters only through <see cref="Show"/>.
    /// </summary>
    internal CaptureArchivedDialog(CaptureArchiveNotice notice)
    {
        _notice = notice;
        Title = notice.Title;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResize;

        var panel = new StackPanel { Margin = new Thickness(14) };

        // READ HERE, AND NOWHERE EARLIER (Sol's review of H10, blocker 2).
        // Explanation asks the notice's recording reader at the moment it is
        // composed, and this constructor runs on the UI thread inside the
        // action CaptureArchiveWatch dispatched — so this line is where "the
        // next thing that happens is being kept too" is decided, after every
        // Settings action and fault retire that was queued ahead of it has
        // run. Nothing may compose or cache the text on the worker.
        var explanation = new TextBox
        {
            Text = notice.Explanation,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 220,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 12),
        };
        AutomationProperties.SetName(explanation, "What happened");
        JJFlexHelp.SetText(explanation,
            "What happened to the connection, what was saved, and what to do with it. "
            + "Arrow through it to read it a line at a time.");
        panel.Children.Add(explanation);

        var pathLabel = new TextBlock
        {
            Text = notice.PathLabel,
            Margin = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(pathLabel);

        var pathBox = new TextBox
        {
            Text = notice.ArchivePath,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        AutomationProperties.SetName(pathBox, "Saved recording, full path");
        JJFlexHelp.SetText(pathBox,
            "The whole path of the saved recording. Read it a character at a time with "
            + "the arrow keys, or press the Copy path button to put it on the clipboard.");
        panel.Children.Add(pathBox);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(MakeButton(notice.CopyButtonLabel, CopyPath, isDefault: true));
        var close = MakeButton(notice.CloseButtonLabel, () => CloseWithResult(true));
        close.IsCancel = true;
        buttons.Children.Add(close);
        panel.Children.Add(buttons);

        Content = panel;
    }

    /// <summary>
    /// Show the notice. Call on the UI thread; <see cref="CaptureArchiveWatch"/>
    /// is what marshals from the thread the archive ran on.
    /// </summary>
    public static void Show(CaptureArchiveNotice notice)
    {
        if (notice == null || string.IsNullOrEmpty(notice.ArchivePath)) return;
        Tracing.TraceLine(
            "CaptureArchivedDialog: showing the operator where the archived recording went — "
            + notice.ArchivePath, TraceLevel.Info);
        new CaptureArchivedDialog(notice).ShowModalDialog();
    }

    private void CopyPath()
    {
        string message;
        try
        {
            Clipboard.SetText(_notice.ClipboardText);
            message = _notice.CopyConfirmation;
        }
        catch (Exception ex)
        {
            // The clipboard genuinely refuses sometimes — another process holds
            // it open. Say so rather than appearing to succeed; the path is
            // still on screen and still readable, which the sentence says.
            Tracing.TraceLine("CaptureArchivedDialog: clipboard refused: " + ex.Message,
                TraceLevel.Warning);
            message = _notice.CopyFailed;
        }

        _status.Text = message;
        // Through the arbiter, with a subject, so a second press replaces the
        // first answer instead of queueing behind it.
        Radios.ScreenReaderOutput.Speak(
            message,
            Radios.Speech.SpeechIntent.Latest,
            Radios.VerbosityLevel.Critical,
            subject: Radios.Speech.SpeechSubject.CaptureArchivedPath);
    }

    private static Button MakeButton(string label, Action onClick, bool isDefault = false)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 110,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = isDefault,
        };
        AutomationProperties.SetName(button, label.Replace("_", ""));
        button.Click += (_, _) => onClick();
        return button;
    }
}
