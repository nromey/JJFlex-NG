using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJTrace;
using Radios;
using Radios.SignalCapture;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The saved QSO signal captures: every capture the analyzer has recorded,
/// each viewable, renamable, exportable and deletable (#271).
/// </summary>
/// <remarks>
/// <para>
/// The management surface Noel ruled captures need: a capture is otherwise
/// identified by nothing but a timestamp, and "the one from 9:14" is useless
/// a week later — the same reason the Fixer has a Test ID. Rename gives it a
/// name; the id stays, because the id is what a conversation quotes.
/// </para>
/// <para>
/// Viewing reuses the report baked when the capture stopped, inside
/// <see cref="HtmlInfoDialog.ShowHtml"/> — the #246 document pattern, same as
/// the saved check runs. No second renderer exists on this path, and a stored
/// capture is never re-analyzed: the report describes the window as it was
/// measured.
/// </para>
/// </remarks>
public sealed class SignalCapturesDialog : JJFlexDialog
{
    private readonly QsoSignalCaptureStore? _store;
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new();
    private IReadOnlyList<QsoSignalCaptureRecord> _captures =
        Array.Empty<QsoSignalCaptureRecord>();

    private SignalCapturesDialog()
    {
        Title = "Signal captures — JJ Flexible";
        Width = 640;
        Height = 480;
        ResizeMode = ResizeMode.CanResize;

        string root = RadioConfig.AppDataRoot;
        _store = string.IsNullOrEmpty(root) ? null : QsoSignalCaptureStore.Default();

        var rootPanel = new DockPanel { Margin = new Thickness(12) };

        var intro = new TextBlock
        {
            Text = "Every capture the QSO signal analyzer takes is saved here when you "
                 + "stop it. Open one to read its report, rename it so you can find it "
                 + "again, export it to send to someone, or delete it. JJ Flexible "
                 + "keeps the newest " + QsoSignalCaptureStore.MaxCapturesKept
                 + " captures; export anything you want to keep forever.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(intro, Dock.Top);
        rootPanel.Children.Add(intro);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        rootPanel.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(MakeButton("_View report", "View report", ViewSelected,
            isDefault: true));
        buttons.Children.Add(MakeButton("_Rename...", "Rename", RenameSelected));
        buttons.Children.Add(MakeButton("_Export...", "Export", ExportSelected));
        buttons.Children.Add(MakeButton("_Delete...", "Delete", DeleteSelected));
        var close = MakeButton("_Close", "Close", () => CloseWithResult(true));
        close.IsCancel = true;
        buttons.Children.Add(close);
        rootPanel.Children.Add(buttons);

        AutomationProperties.SetName(_list, "Signal captures");
        JJFlexHelp.SetText(_list,
            "One line per saved capture: its name or id, when it started, how long "
            + "it ran, and its peak. Newest first. Enter opens the report.");
        _list.MouseDoubleClick += (_, _) => ViewSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; ViewSelected(); }
        };
        rootPanel.Children.Add(_list);

        Content = rootPanel;
        Refresh(selectFirst: true);
    }

    /// <summary>Open the signal captures list.</summary>
    public static void Show(Window? owner = null)
    {
        var dialog = new SignalCapturesDialog();
        if (owner != null) dialog.Owner = owner;
        dialog.ShowModalDialog();
    }

    private Button MakeButton(string label, string accessibleName, Action onClick,
                              bool isDefault = false)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 100,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = isDefault,
        };
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("SignalCapturesDialog: " + accessibleName + " failed — "
                                  + ex.Message, TraceLevel.Error);
                Say("That could not be done: " + ex.Message);
            }
        };
        return button;
    }

    // ---------------- the list ----------------

    private void Refresh(bool selectFirst)
    {
        int previousIndex = _list.SelectedIndex;

        if (_store == null)
        {
            _captures = Array.Empty<QsoSignalCaptureRecord>();
            _list.Items.Clear();
            _status.Text = "The settings folder could not be resolved, so no saved "
                         + "captures can be shown.";
            return;
        }

        _captures = _store.LoadAll(out int unreadable);
        _list.Items.Clear();
        foreach (QsoSignalCaptureRecord capture in _captures)
            _list.Items.Add(capture.Summary());

        if (_captures.Count == 0)
        {
            _status.Text = "No captures have been saved yet. Press Control J, then "
                         + "Control Q during a contact to start one; stopping it the "
                         + "same way saves it here." + UnreadableNote(unreadable);
        }
        else
        {
            _status.Text = (_captures.Count == 1
                ? "One saved capture."
                : _captures.Count + " saved captures, newest first.")
                + UnreadableNote(unreadable);
            _list.SelectedIndex = selectFirst || previousIndex < 0
                ? 0
                : Math.Min(previousIndex, _captures.Count - 1);
        }
    }

    /// <summary>A file that exists but cannot be read is counted out loud —
    /// a list that silently shrank would hide exactly the loss it exists to
    /// prevent.</summary>
    private static string UnreadableNote(int unreadable)
        => unreadable == 0 ? ""
         : unreadable == 1 ? " One saved capture could not be read and is not listed."
         : " " + unreadable + " saved captures could not be read and are not listed.";

    private QsoSignalCaptureRecord? Selected()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _captures.Count)
        {
            Say("No capture is selected.");
            return null;
        }
        return _captures[i];
    }

    // ---------------- view ----------------

    private void ViewSelected()
    {
        QsoSignalCaptureRecord? capture = Selected();
        if (capture == null) return;

        HtmlInfoDialog.ShowHtml(
            "QSO signal capture — " + capture.DisplayName,
            QsoSignalCaptureExport.StandaloneHtml(capture),
            capture.ReportText,
            this,
            new AdvisoryDialog.AdvisoryAction("Copy report", () => CopyReport(capture)));
    }

    private void CopyReport(QsoSignalCaptureRecord capture)
    {
        try
        {
            Clipboard.SetText(capture.ReportText);
            Say(Radios.Fixer.Evidence.EvidenceStrings.CopiedToClipboard);
        }
        catch (Exception ex)
        {
            // Never announce a copy that did not happen.
            Tracing.TraceLine("SignalCapturesDialog: clipboard failed — " + ex.Message,
                              TraceLevel.Warning);
            Say("The report could not be copied.");
        }
    }

    // ---------------- rename ----------------

    private void RenameSelected()
    {
        QsoSignalCaptureRecord? capture = Selected();
        if (capture == null || _store == null) return;

        string? name = RenameDialog.Ask(this, capture);
        if (name == null) return; // cancelled

        capture.Label = name.Trim();
        if (_store.Save(capture))
        {
            Say(capture.Label.Length == 0
                ? "Name cleared. The capture goes back to its id, " + capture.CaptureId + "."
                : "Renamed to " + capture.Label + ". It keeps its id, "
                  + capture.CaptureId + ".");
            Refresh(selectFirst: false);
        }
        else
        {
            Say("The new name could not be saved.");
        }
    }

    /// <summary>A one-field prompt: the capture's name. Empty clears the name,
    /// Escape cancels.</summary>
    private sealed class RenameDialog : JJFlexDialog
    {
        private readonly TextBox _name = new();
        private bool _accepted;

        private RenameDialog(QsoSignalCaptureRecord capture)
        {
            Title = "Rename capture " + capture.CaptureId + " — JJ Flexible";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { Margin = new Thickness(12) };

            var label = new Label
            {
                Content = "_Name for this capture:",
                Target = _name,
            };
            panel.Children.Add(label);

            _name.Text = capture.Label;
            _name.Margin = new Thickness(0, 2, 0, 8);
            AutomationProperties.SetName(_name, "Name for this capture");
            JJFlexHelp.SetText(_name,
                "A name you will recognize later, like the station's callsign. "
                + "Leave it empty to go back to the capture's id.");
            panel.Children.Add(_name);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var ok = new Button
            {
                Content = "_OK", MinWidth = 80, Height = 26,
                Margin = new Thickness(0, 0, 8, 0), IsDefault = true,
            };
            AutomationProperties.SetName(ok, "OK");
            ok.Click += (_, _) => { _accepted = true; CloseWithResult(true); };
            var cancel = new Button
            {
                Content = "_Cancel", MinWidth = 80, Height = 26, IsCancel = true,
            };
            AutomationProperties.SetName(cancel, "Cancel");
            cancel.Click += (_, _) => CloseWithResult(false);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
        }

        protected override void FocusFirstControl()
        {
            _name.Focus();
            _name.SelectAll();
        }

        /// <summary>The new name, or null when cancelled.</summary>
        internal static string? Ask(Window owner, QsoSignalCaptureRecord capture)
        {
            var dialog = new RenameDialog(capture) { Owner = owner };
            dialog.ShowModalDialog();
            return dialog._accepted ? dialog._name.Text : null;
        }
    }

    // ---------------- export ----------------

    private void ExportSelected()
    {
        QsoSignalCaptureRecord? capture = Selected();
        if (capture == null) return;

        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export capture " + capture.DisplayName,
            FileName = QsoSignalCaptureExport.FileBaseName(capture),
            DefaultExt = ".html",
            Filter = Radios.Fixer.Evidence.EvidenceStrings.ExportFilter,
        };
        if (picker.ShowDialog(this) != true) return;

        bool wantText = picker.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        bool written = wantText
            ? QsoSignalCaptureExport.WriteText(capture, picker.FileName)
            : QsoSignalCaptureExport.WriteHtml(capture, picker.FileName);

        // A receipt either way: nothing visible changes when an export
        // succeeds, and a silent failure is an email that never gets its
        // attachment.
        Say(written
            ? "Exported to " + System.IO.Path.GetFileName(picker.FileName) + "."
            : "The export failed. Nothing was written.");
    }

    // ---------------- delete ----------------

    private void DeleteSelected()
    {
        QsoSignalCaptureRecord? capture = Selected();
        if (capture == null) return;

        MessageBoxResult answer = MessageBox.Show(this,
            "Delete capture " + capture.DisplayName + "? Its report and readings will "
            + "be gone for good — there is no undo.",
            "Signal captures — JJ Flexible",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        if (_store != null && _store.Delete(capture))
        {
            Say("Capture " + capture.DisplayName + " deleted.");
            Refresh(selectFirst: false);
        }
        else
        {
            Say("Capture " + capture.DisplayName + " could not be deleted.");
        }
    }

    private static void Say(string sentence)
        => ScreenReaderOutput.Speak(sentence, VerbosityLevel.Critical, interrupt: true);
}
