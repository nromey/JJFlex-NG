using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJTrace;
using Radios;
using Radios.ChainChecks;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, Diagnostics: the two questions an operator actually asks
/// when audio is wrong, and the evidence to send to Flex when the answer is
/// not something they can fix.
/// </summary>
/// <remarks>
/// <para>
/// "Why is my radio silent" and "why can nobody hear me" are the same tool
/// pointed in opposite directions, so they live in one room. The receive half
/// moved here from Settings, Audio (Sprint 32 Track C, on Noel's ruling); a
/// pointer stays where it was.
/// </para>
/// <para>
/// <b>Every result is a read-only multi-line edit, not a list of controls and
/// not a table.</b> A report with twenty readings in it would be twenty tab
/// stops as controls and unreadable aloud as a table; as text it is ONE tab
/// stop that a screen reader walks line by line at the operator's own pace,
/// with select-all and copy already working. That is the same idiom the meter
/// inventory and the remove-radio dialog use, and the reason the evidence block
/// needs no copy mechanism of its own — the button beside it is a convenience,
/// not the only route.
/// </para>
/// <para>
/// <b>The check runs on demand and says when it ran.</b> A diagnostic that
/// refreshed itself under the operator would be a report they could never
/// finish reading, and an evidence block that described no single moment.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    #region Diagnostics tab

    private TextBox? _rxAdvisoryBox;
    private TextBox? _txReportBox;
    private TextBox? _evidenceBox;
    private Button? _copyEvidenceButton;

    /// <summary>The last transmit report, kept so the evidence block and the
    /// staleness note refer to the same run.</summary>
    private ChainReport? _lastTxReport;

    /// <summary>The inventory we are following. Re-pointed on each check rather
    /// than on a rig change, because the tab has no hook into SetRig and a
    /// subscription to a departed radio's inventory would keep it alive.</summary>
    private MeterInventory? _watchedInventory;

    /// <summary>Set when the radio's meter list changed after a report was
    /// produced, so the report can say it is out of date instead of quietly
    /// describing a radio that has moved on.</summary>
    private bool _txReportStale;

    private const string NoTxReportYet =
        "No transmit check has been run yet. Choose Check My Transmit Chain above.";

    private const string NoEvidenceYet =
        "Run the transmit check above and the evidence to send to Flex support will appear here.";

    /// <summary>
    /// Build the Diagnostics tab: the receive question, the transmit question,
    /// and the evidence block.
    /// </summary>
    private void BuildDiagnosticsTab()
    {
        // NOT AddRadioSection. A disconnected radio is not a reason to disable
        // this tab — it is the FIRST verdict the transmit check gives, and a
        // diagnostic that refuses to run when nothing is connected withholds
        // exactly the answer the operator needs at that moment.
        AddSectionHeader(DiagnosticsContent, "Why is my radio silent");

        var rxIntro = new TextBlock
        {
            Text = "Checks the reasons a Flex plays no audio, in the order they actually bite.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        };
        AddToSection(DiagnosticsContent, rxIntro);

        var rxButton = new Button
        {
            Content = "Check My Receive Audio",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(rxButton, "Check my receive audio");
        JJFlexHelp.SetText(rxButton,
            "Works through the reasons a Flex makes no sound, starting with the one "
            + "that catches everybody: a Flex is silent by design, headphone jack "
            + "included, until a client connects to it.");
        rxButton.Click += (s, e) => RunReceiveCheck(speak: true);
        AddToSection(DiagnosticsContent, rxButton);

        _rxAdvisoryBox = MakeReportBox("Receive audio check result", 3, 8);
        AddToSection(DiagnosticsContent, _rxAdvisoryBox);

        AddSectionHeader(DiagnosticsContent, "Why can nobody hear me");

        var txIntro = new TextBlock
        {
            Text = "Walks your transmit chain from the microphone to the antenna and reports "
                 + "the first stage that is dead. It also says how much of the chain it could "
                 + "not see, because a check that could not be made is not a check that passed.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        };
        AddToSection(DiagnosticsContent, txIntro);

        var txButton = new Button
        {
            Content = "Check My Transmit Chain",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(txButton, "Check my transmit chain");
        JJFlexHelp.SetText(txButton,
            "Reads your microphone, this computer, and every transmit setting the radio "
            + "will report, and tells you the first thing in the way. Some stages can only "
            + "be measured while you are transmitting, and the report says which those are.");
        txButton.Click += (s, e) => RunTransmitCheck(speak: true);
        AddToSection(DiagnosticsContent, txButton);

        _txReportBox = MakeReportBox("Transmit chain check result", 8, 20);
        _txReportBox.Text = NoTxReportYet;
        AddToSection(DiagnosticsContent, _txReportBox);

        AddSectionHeader(DiagnosticsContent, "Evidence for a support ticket");

        var evIntro = new TextBlock
        {
            Text = "Every reading behind the answer above, with its units, its age and where it "
                 + "came from, plus your radio's model, serial and firmware. Paste it straight "
                 + "into an email to Flex support — nothing in it needs translating first.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        };
        AddToSection(DiagnosticsContent, evIntro);

        _evidenceBox = MakeReportBox("Evidence to send to Flex support", 8, 24);
        _evidenceBox.Text = NoEvidenceYet;
        AddToSection(DiagnosticsContent, _evidenceBox);

        _copyEvidenceButton = new Button
        {
            Content = "Copy Evidence to the Clipboard",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false,
        };
        AutomationProperties.SetName(_copyEvidenceButton, "Copy evidence to the clipboard");
        JJFlexHelp.SetText(_copyEvidenceButton,
            "The evidence box above is an ordinary text box, so Control A then Control C "
            + "does the same thing. This is here for when that is not to hand.");
        _copyEvidenceButton.Click += (s, e) => CopyEvidence();
        AddToSection(DiagnosticsContent, _copyEvidenceButton);

        _rxAdvisoryBox.Text = "Choose Check My Receive Audio above.";

        // Fill the receive box for real once the dialog is up. It cannot be
        // done here: this runs from the constructor, before SetRig, so the
        // answer would always be "no radio is connected" and would stay that
        // way on a connected radio until the operator pressed the button.
        Loaded += (s, e) => RunReceiveCheck(speak: false);

        // And again on the way into the tab, because the radio can connect or
        // disappear while the workshop sits open. Guarded and additive: if the
        // navigation is ever rebuilt so this never fires, the box simply keeps
        // the answer it has — which is honest, because it is stamped with the
        // time it was taken.
        try
        {
            MainTabs.SelectionChanged += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
                if (DiagnosticsTab.IsSelected) RunReceiveCheck(speak: false);
            };
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Diagnostics: could not follow tab selection — " + ex.Message,
                              TraceLevel.Warning);
        }

        // The inventory subscription must not outlive the window. Added as its
        // own handler rather than by editing the shell's Closed lambda.
        Closed += (s, e) => UnwatchInventory();
    }

    /// <summary>
    /// A read-only multi-line edit for a report.
    /// </summary>
    /// <remarks>
    /// Read-only rather than disabled, so it keeps its place in the tab order
    /// and a screen reader's review commands still reach it; multi-line with a
    /// visible caret, so the operator can walk it a line at a time rather than
    /// having the whole thing read at them once and then being unable to get it
    /// back. No live region: this changes only when the operator asks it to,
    /// and the asking already carries the announcement.
    /// </remarks>
    private static TextBox MakeReportBox(string name, int minLines, int maxLines)
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinLines = minLines,
            MaxLines = maxLines,
            Margin = new Thickness(2, 0, 2, 8),
            FontSize = 12,
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    /// <summary>
    /// Answer "why is my radio silent" and show it.
    /// </summary>
    /// <remarks>
    /// The ladder itself is <see cref="FlexBase.SilentRadioAdvisory"/>, moved
    /// here from Settings unchanged — same method, same signature, same order
    /// of rungs. Only the room changed.
    /// </remarks>
    private void RunReceiveCheck(bool speak)
    {
        if (_rxAdvisoryBox == null) return;

        string message;
        try
        {
            FlexBase? rig = _rig;
            if (rig == null || !rig.IsConnected)
            {
                message = "No radio is connected. A Flex makes no audio at all until a client "
                        + "connects to it, including at its own headphone jack. Connect first.";
            }
            else
            {
                message = rig.SilentRadioAdvisory()
                    ?? $"Nothing obvious is wrong. Headphone level {rig.HeadphoneGain}, line out "
                     + $"level {rig.LineoutGain}, nothing muted. If the radio is still silent, "
                     + "check the slice volume and that a slice is not muted.";
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Diagnostics: receive check failed — " + ex.Message, TraceLevel.Error);
            message = "The receive audio check could not run: " + ex.Message;
        }

        _rxAdvisoryBox.Text = message + Environment.NewLine
                            + "Checked at " + DateTime.Now.ToString("HH:mm:ss") + ".";

        if (!speak) return;
        _rxAdvisoryBox.Focus();
        ScreenReaderOutput.Speak(message, VerbosityLevel.Critical, true);
    }

    /// <summary>
    /// Walk the transmit chain and show the answer, the honest census and the
    /// evidence.
    /// </summary>
    private void RunTransmitCheck(bool speak)
    {
        if (_txReportBox == null || _evidenceBox == null) return;

        ChainReport report;
        try
        {
            FlexBase? rig = _rig;

            // Collect the computer's facts FIRST so they sit ahead of the
            // radio's in the evidence block: the block reads as a walk along
            // the signal path, and the microphone is where the path starts.
            IReadOnlyList<DiagnosticFact> pcFacts;
            try
            {
                pcFacts = TxChainPcFacts.Collect(AudioDevicesPath?.Invoke());
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Diagnostics: PC-side facts failed — " + ex.Message, TraceLevel.Warning);
                pcFacts = Array.Empty<DiagnosticFact>();
            }

            DiagnosticFacts facts = TxChainFacts.Collect(rig, pcFacts);
            report = ChainAnalyzer.Run(RuleSetLoader.TxChain(), facts);

            WatchInventory(rig);
            _txReportStale = false;
            _lastTxReport = report;
        }
        catch (Exception ex)
        {
            // A diagnostic that throws is worse than one that says it could not
            // look, so this never reaches the operator as a crash.
            Tracing.TraceLine("Diagnostics: transmit check failed — " + ex, TraceLevel.Error);
            _lastTxReport = null;
            _txReportBox.Text = "The transmit chain check could not run: " + ex.Message;
            _evidenceBox.Text = NoEvidenceYet;
            if (_copyEvidenceButton != null) _copyEvidenceButton.IsEnabled = false;
            if (speak)
            {
                _txReportBox.Focus();
                ScreenReaderOutput.Speak("The transmit chain check could not run. " + ex.Message,
                                         VerbosityLevel.Critical, true);
            }
            return;
        }

        _txReportBox.Text = ReportText(report);
        _evidenceBox.Text = EvidenceText(report);
        if (_copyEvidenceButton != null) _copyEvidenceButton.IsEnabled = true;

        if (!speak) return;
        _txReportBox.Focus();

        // The operator pressed a button to ask a question, and the answer is
        // not carried by the control that just took focus — a screen reader
        // landing in a long edit reads its first line, not its verdict. This is
        // the case app speech exists for, and it is the same behaviour the
        // receive advisory has always had in Settings.
        ScreenReaderOutput.Speak(report.Headline() + " " + report.Census(),
                                 VerbosityLevel.Critical, true);
    }

    /// <summary>
    /// The report as the operator reads it: the answer first, then when it was
    /// taken, then how much of the chain was actually seen, then the walk.
    /// </summary>
    /// <remarks>
    /// Answer first is deliberate. A screen reader entering this box reads the
    /// line the caret is on, so whatever is at the top is what an operator
    /// hears when they tab into it — and that has to be the verdict, not a
    /// heading and a date.
    /// </remarks>
    private string ReportText(ChainReport report)
    {
        var sb = new StringBuilder();

        if (_txReportStale)
        {
            sb.AppendLine("This check is out of date: the radio's meter list changed after it ran. "
                        + "Run it again for a current answer.");
            sb.AppendLine();
        }

        sb.AppendLine(report.Headline());
        sb.AppendLine();
        sb.AppendLine(report.Census());
        sb.AppendLine();
        sb.AppendLine("Checked at " + report.At.ToString("HH:mm:ss") + ".");
        sb.AppendLine();
        sb.AppendLine("Stage by stage:");
        foreach (StageResult s in report.Stages) sb.AppendLine(s.Line());

        if (report.RuleProblems.Count != 0)
        {
            sb.AppendLine();
            sb.AppendLine("Some checks are missing because the rule file has lines this build "
                        + "could not read:");
            foreach (string p in report.RuleProblems) sb.AppendLine(p);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The evidence block, with the radio's identity from the radio layer and
    /// the software's from <see cref="DiagnosticSnapshot"/> — which stays the
    /// only assembler of version strings in the app.
    /// </summary>
    private string EvidenceText(ChainReport report)
    {
        string body;
        try
        {
            body = report.EvidenceText(TxChainFacts.StationLines(_rig), TxChainPcFacts.BuildLines());
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Diagnostics: evidence block failed — " + ex.Message, TraceLevel.Warning);
            // Half an evidence block is still worth sending; an exception is
            // not.
            body = report.EvidenceText();
        }

        // A pointer rather than a copy. This block quotes only the readings
        // behind the verdict; the whole census runs to a hundred meters on an
        // 8600 and belongs in its own export, not pasted into the middle of a
        // support email nobody asked for.
        return body + Environment.NewLine
             + "The radio's full meter list, with every reading and its age, is on the "
             + "Meter Inventory page of this workshop and can be copied separately."
             + Environment.NewLine;
    }

    private void CopyEvidence()
    {
        string text = _evidenceBox?.Text ?? "";
        if (text.Length == 0 || text == NoEvidenceYet)
        {
            ScreenReaderOutput.Speak("There is nothing to copy yet. Run the transmit check first.",
                                     VerbosityLevel.Critical, true);
            return;
        }

        try
        {
            Clipboard.SetText(text);
            // A receipt, because nothing visible changes when a copy succeeds —
            // and a copy that silently failed would be discovered only when the
            // operator pasted an empty email.
            ScreenReaderOutput.Speak("Evidence copied to the clipboard.", VerbosityLevel.Critical, true);
        }
        catch (Exception ex)
        {
            // The clipboard is genuinely refusable: another process can hold it
            // open. Never announce a copy that did not happen.
            Tracing.TraceLine("Diagnostics: clipboard copy failed — " + ex.Message, TraceLevel.Warning);
            ScreenReaderOutput.Speak(
                "The clipboard could not be opened, so nothing was copied. Another program may be "
                + "holding it. You can select the evidence box and copy it yourself.",
                VerbosityLevel.Critical, true);
        }
    }

    /// <summary>
    /// Follow the connected radio's meter inventory, so a report can say when
    /// it has been overtaken.
    /// </summary>
    /// <remarks>
    /// FlexLib raises nothing when a meter appears and the list GROWS DURING
    /// REGISTRATION, so a check run seconds after connecting can honestly
    /// report a meter as absent that arrives a moment later. Binding to the
    /// change event is the contract; sampling once is what produces a
    /// confidently wrong answer.
    /// </remarks>
    private void WatchInventory(FlexBase? rig)
    {
        MeterInventory? inv = null;
        try { inv = rig?.MeterInventory; } catch { inv = null; }

        if (ReferenceEquals(inv, _watchedInventory)) return;
        UnwatchInventory();
        if (inv == null) return;

        _watchedInventory = inv;
        inv.InventoryChanged += OnMeterInventoryChanged;
    }

    private void UnwatchInventory()
    {
        MeterInventory? inv = _watchedInventory;
        _watchedInventory = null;
        if (inv == null) return;
        try { inv.InventoryChanged -= OnMeterInventoryChanged; }
        catch (Exception ex)
        {
            Tracing.TraceLine("Diagnostics: unhooking the meter inventory failed — " + ex.Message,
                              TraceLevel.Warning);
        }
    }

    /// <summary>
    /// The radio's meter list changed. Mark the report stale rather than
    /// re-running it.
    /// </summary>
    /// <remarks>
    /// Raised on FlexLib's meter thread, never the UI thread, so this marshals
    /// before touching anything. It deliberately does NOT re-run the check: a
    /// report that rewrote itself while being read would lose the operator's
    /// place, and it would stop describing the single moment its evidence block
    /// claims to describe.
    /// </remarks>
    private void OnMeterInventoryChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            // The meter thread can raise this between the window starting to
            // close and Closed running the unsubscribe, and BeginInvoke on a
            // shut-down dispatcher throws. A diagnostic must not be the thing
            // that crashes the app on the way out.
            try { Dispatcher.BeginInvoke(() => OnMeterInventoryChanged(sender, e)); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Diagnostics: meter change arrived after the window closed — "
                                  + ex.Message, TraceLevel.Verbose);
            }
            return;
        }

        if (_lastTxReport == null || _txReportStale) return;
        _txReportStale = true;
        RewriteReportWhenNotBeingRead();
    }

    /// <summary>
    /// Put the staleness note into the report box, but not while the operator
    /// is standing in it.
    /// </summary>
    /// <remarks>
    /// Assigning Text resets the caret to the start, which for someone reading
    /// the report line by line with a screen reader means being thrown back to
    /// the top mid-sentence with no explanation. Waiting for focus to leave
    /// costs nothing — the note is a correction to read next time, not an
    /// alarm — and the alternative, leaving a known-stale report standing, is
    /// the thing this whole track exists to avoid.
    /// </remarks>
    private void RewriteReportWhenNotBeingRead()
    {
        if (_txReportBox == null || _lastTxReport == null) return;

        if (!_txReportBox.IsKeyboardFocusWithin)
        {
            _txReportBox.Text = ReportText(_lastTxReport);
            return;
        }

        void OnLeft(object? s, RoutedEventArgs e)
        {
            _txReportBox.LostKeyboardFocus -= OnLeft;
            if (_lastTxReport != null) _txReportBox.Text = ReportText(_lastTxReport);
        }
        _txReportBox.LostKeyboardFocus += OnLeft;
    }

    #endregion
}
