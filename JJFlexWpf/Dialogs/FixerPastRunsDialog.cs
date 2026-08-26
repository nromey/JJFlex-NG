using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJTrace;
using Radios;
using Radios.Fixer.Evidence;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The saved check runs: every run the Fixer has recorded, each viewable,
/// exportable and deletable. The list is what makes a Test ID quotable — a
/// run from last Tuesday sits here with the same ID the report carried, which
/// is the comparison "it worked Tuesday" has never been able to cite (#252).
/// </summary>
/// <remarks>
/// <para>
/// Named by what it holds, not by a product noun, per the 2026-08-25 naming
/// ruling; internals keep the Fixer name deliberately.
/// </para>
/// <para>
/// Viewing reuses the recorded report — rendered by FixerReport when the run
/// was recorded — inside <see cref="HtmlInfoDialog.ShowHtml"/>, the #246
/// document pattern: report-shaped, prose-heavy, read by arrowing, with Copy.
/// No second renderer exists anywhere on this path. For a run that stopped
/// part-way, the view leads with what has changed since, from the settings
/// fingerprints, so the operator knows which stages still hold before
/// deciding anything.
/// </para>
/// </remarks>
public sealed class FixerPastRunsDialog : JJFlexDialog
{
    private readonly FixerRunStore? _store;
    private readonly Func<FlexBase?> _radio;
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new();
    private IReadOnlyList<FixerRunRecord> _runs = Array.Empty<FixerRunRecord>();

    private FixerPastRunsDialog(Func<FlexBase?> radio)
    {
        _radio = radio;

        Title = "Saved check runs — JJ Flexible";
        Width = 640;
        Height = 480;
        ResizeMode = ResizeMode.CanResize;

        string root = RadioConfig.AppDataRoot;
        _store = string.IsNullOrEmpty(root) ? null : FixerRunStore.Default();

        var rootPanel = new DockPanel { Margin = new Thickness(12) };

        var intro = new TextBlock
        {
            Text = "Every check run saves itself as it happens, named by its test ID. "
                 + "Open one to read its report, export it to send to someone, or "
                 + "delete it.",
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
        buttons.Children.Add(MakeButton("_Export...", "Export", ExportSelected));
        buttons.Children.Add(MakeButton("_Delete...", "Delete", DeleteSelected));
        var close = MakeButton("_Close", "Close", () => CloseWithResult(true));
        close.IsCancel = true;
        buttons.Children.Add(close);
        rootPanel.Children.Add(buttons);

        AutomationProperties.SetName(_list, "Saved check runs");
        JJFlexHelp.SetText(_list,
            "One line per saved run: its test ID, when it started, how many stages "
            + "have results, and whether it finished. Newest first. Enter opens the "
            + "report.");
        _list.MouseDoubleClick += (_, _) => ViewSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; ViewSelected(); }
        };
        rootPanel.Children.Add(_list);

        Content = rootPanel;
        Refresh(selectFirst: true);
    }

    /// <summary>Open the saved runs list.</summary>
    public static void Show(Func<FlexBase?> radio, Window? owner = null)
    {
        var dialog = new FixerPastRunsDialog(radio);
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
                Tracing.TraceLine("FixerPastRunsDialog: " + accessibleName + " failed — "
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
            _runs = Array.Empty<FixerRunRecord>();
            _list.Items.Clear();
            _status.Text = "The settings folder could not be resolved, so no saved runs "
                         + "can be shown.";
            return;
        }

        _runs = _store.LoadAll(out int unreadable);
        _list.Items.Clear();
        foreach (FixerRunRecord run in _runs)
            _list.Items.Add(run.Summary());

        if (_runs.Count == 0)
        {
            _status.Text = "No check runs have been saved yet. Runs save themselves as "
                         + "they happen — run a check and it will appear here."
                         + UnreadableNote(unreadable);
        }
        else
        {
            _status.Text = _runs.Count == 1
                ? "One saved run." + UnreadableNote(unreadable)
                : _runs.Count + " saved runs, newest first." + UnreadableNote(unreadable);
            _list.SelectedIndex = selectFirst || previousIndex < 0
                ? 0
                : Math.Min(previousIndex, _runs.Count - 1);
        }
    }

    /// <summary>A file that exists but cannot be read is counted out loud —
    /// a list that silently shrank would hide exactly the loss it exists to
    /// prevent.</summary>
    private static string UnreadableNote(int unreadable)
        => unreadable == 0 ? ""
         : unreadable == 1 ? " One saved run could not be read and is not listed."
         : " " + unreadable + " saved runs could not be read and are not listed.";

    private FixerRunRecord? Selected()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _runs.Count)
        {
            Say("No run is selected.");
            return null;
        }
        return _runs[i];
    }

    // ---------------- view ----------------

    private void ViewSelected()
    {
        FixerRunRecord? run = Selected();
        if (run == null) return;

        string lead = StalenessLead(run, out string leadText);
        HtmlInfoDialog.ShowHtml(
            run.StageSetName + " check report — " + run.RunId,
            FixerRunExport.StandaloneHtml(run, lead),
            leadText + run.ReportText,
            this,
            new AdvisoryDialog.AdvisoryAction("Copy report", () => CopyReport(run)));
    }

    /// <summary>
    /// For a run that stopped part-way: what has changed since it stopped,
    /// from the settings fingerprints, so the report is read with today's
    /// radio in mind. Returns an HTML fragment (and the same words as plain
    /// text) or empty when there is nothing to say.
    /// </summary>
    private string StalenessLead(FixerRunRecord run, out string plainText)
    {
        plainText = "";
        if (run.IsComplete()) return "";
        if (!string.Equals(run.StageSetId, "transmit", StringComparison.OrdinalIgnoreCase))
            return "";   // only the transmit set declares fingerprints today

        try
        {
            FixerStalenessReport report = FixerStalenessCheck.Check(run,
                TransmitSettingProbes.Build(FixerEvidenceKit.Readers(_radio)));
            string summary = report.Summary();
            if (summary.Length == 0) return "";

            plainText = "Since this run stopped: " + summary + Environment.NewLine
                      + Environment.NewLine;
            return "<h2>Since this run stopped</h2><p>" + Esc(summary) + "</p>";
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerPastRunsDialog: staleness lead failed — " + ex.Message,
                              TraceLevel.Warning);
            return "";
        }
    }

    private void CopyReport(FixerRunRecord run)
    {
        try
        {
            Clipboard.SetText(run.ReportText);
            Say("The report is on the clipboard, as plain text.");
        }
        catch (Exception ex)
        {
            // Never announce a copy that did not happen.
            Tracing.TraceLine("FixerPastRunsDialog: clipboard failed — " + ex.Message,
                              TraceLevel.Warning);
            Say("The report could not be copied. Windows refused the clipboard.");
        }
    }

    // ---------------- export ----------------

    private void ExportSelected()
    {
        FixerRunRecord? run = Selected();
        if (run == null) return;

        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export check run " + run.RunId,
            FileName = FixerRunExport.FileBaseName(run),
            DefaultExt = ".html",
            Filter = "Web page (*.html)|*.html|Plain text (*.txt)|*.txt",
        };
        if (picker.ShowDialog(this) != true) return;

        bool wantText = picker.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        bool written = wantText
            ? FixerRunExport.WriteText(run, picker.FileName)
            : FixerRunExport.WriteHtml(run, picker.FileName);

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
        FixerRunRecord? run = Selected();
        if (run == null) return;

        MessageBoxResult answer = MessageBox.Show(this,
            "Delete run " + run.RunId + "? Its report and measurements will be gone "
            + "for good — there is no undo.",
            "Saved check runs — JJ Flexible",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        if (_store != null && _store.Delete(run))
        {
            Say("Run " + run.RunId + " deleted.");
            Refresh(selectFirst: false);
        }
        else
        {
            Say("Run " + run.RunId + " could not be deleted.");
        }
    }

    private static void Say(string sentence)
        => ScreenReaderOutput.Speak(sentence, VerbosityLevel.Critical, interrupt: true);

    private static string Esc(string s)
        => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
