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

    /// <summary>The last transmit walk, kept so the evidence block and the
    /// staleness note refer to the same run.</summary>
    /// <remarks>
    /// The shared check's result, not a bare report (#400). Every string this
    /// tab shows about the transmit chain now comes out of
    /// <see cref="TransmitChainCheck"/>, which is the same object the Fixer's
    /// keying stages build — so a rule added to <c>tx-chain-rules.txt</c>
    /// reaches this box and that report with no second edit.
    /// </remarks>
    private TransmitCheckResult? _lastTxWalk;

    /// <summary>The last receive report. Kept for the same reason as the
    /// transmit one, and because the evidence block should be able to carry
    /// receive readings too once the two checks live in one place.</summary>
    private ChainReport? _lastRxReport;

    /// <summary>The inventory we are following. Re-pointed on each check rather
    /// than on a rig change, because the tab has no hook into SetRig and a
    /// subscription to a departed radio's inventory would keep it alive.</summary>
    private MeterInventory? _watchedInventory;

    /// <summary>Set when the radio's meter list changed after a report was
    /// produced, so the report can say it is out of date instead of quietly
    /// describing a radio that has moved on.</summary>
    private bool _txReportStale;

    private static string NoTxReportYet => Lexicon.Get("audio.diagnostics.no_tx_report_yet");

    private static string NoEvidenceYet => Lexicon.Get("audio.diagnostics.no_evidence_yet");

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
        // WHICH SURFACE IS WHICH (#234). Noel, using both for the first time:
        // "it is confusing to me looking at it now with the fix tool being
        // available."
        //
        // NOT resolved by deleting this page, which was the obvious move and is
        // wrong. These checks WALK THIRTEEN STAGES from a rule file an operator
        // can edit, and the Fixer's five coded stages cover a subset of them —
        // stages 0, 4 and 10 have no Fixer equivalent at all.
        //
        // AND THE RECEIVE HALF IS NOW LITERALLY THE SAME CHECK (#367). Not a
        // similar one, not a subset: RunReceiveCheck below and the Fixer's
        // stage 0 both call ReceiveAudioCheck.Run, so the two doors cannot
        // drift apart and a rule added to rx-chain-rules.txt shows up at both.
        //
        // That makes the boundary sayable rather than merely arguable, which is
        // what #234 asked for: this room is where you ADJUST audio deliberately
        // and want an answer now; the Fixer is where you go when something is
        // WRONG and you want a document to send. The orientation line below is
        // that sentence, in the operator's words, because an operator who knows
        // the difference is not confused by two surfaces and one who does not
        // is confused by any number of them.
        var orient = new TextBlock
        {
            Text = "Nothing here transmits. Use this room while you are setting audio up and "
                 + "want an answer now; use JJ Flexible Fix, under Tools, when something is "
                 + "wrong and you want a report to send — it runs this very same receive "
                 + "test as its first stage, then goes on to key the radio and measure what "
                 + "comes back.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 10),
        };
        // STRAIGHT ONTO THE PANEL, not AddToSection. That helper appends to
        // (_section ?? fallback), and _section still points at the LAST section
        // opened — which at this moment belongs to the Amplifier tab, built
        // immediately before this one. Using it here would have put this
        // paragraph in a different tab, silently, with nothing failing.
        DiagnosticsContent.Children.Add(orient);

        AddSectionHeader(DiagnosticsContent, "Why is my radio silent");

        var rxIntro = new TextBlock
        {
            Text = "Walks the reasons a Flex plays no audio, in the order they actually bite, "
                 + "and measures how much audio has really been arriving from the radio. "
                 + "This is the same test JJ Flexible Fix runs at stage 0.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        };
        AddToSection(DiagnosticsContent, rxIntro);

        var rxButton = new Button
        {
            Content = "Test My Receive Audio",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(rxButton, "Test my receive audio");
        JJFlexHelp.SetText(rxButton,
            "Works through the reasons a Flex makes no sound, starting with the one "
            + "that catches everybody: a Flex is silent by design, headphone jack "
            + "included, until a client connects to it. It ends with the one fact that "
            + "is about the radio rather than about a setting of ours — how much audio "
            + "has actually been crossing the network from it.");
        rxButton.Click += (s, e) => RunReceiveCheck(speak: true);
        AddToSection(DiagnosticsContent, rxButton);

        _rxAdvisoryBox = MakeReportBox("Receive audio test result", 3, 12);
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
            Content = "Test My Transmit Chain",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(txButton, "Test my transmit chain");
        JJFlexHelp.SetText(txButton,
            "Reads your microphone, this computer, and every transmit setting the radio "
            + "will report, and tells you the first thing in the way. Some stages can only "
            + "be measured while you are transmitting, and the report says which those are.");
        txButton.Click += (s, e) => RunTransmitCheck(speak: true);
        AddToSection(DiagnosticsContent, txButton);

        _txReportBox = MakeReportBox("Transmit chain test result", 8, 20);
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

        _rxAdvisoryBox.Text = "Choose Test My Receive Audio above.";

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
            // ONE CALL, AND IT IS THE ONE THE FIXER MAKES (#367).
            //
            // This door and the Fixer's stage 0 run the same check, on the same
            // rules, phrased by the same code — Noel's ruling: "as stage 0, it
            // does rx audio as well, but you can just go to the other submenu
            // option if you just wanted to test rx audio." Add a rule to
            // rx-chain-rules.txt and it appears at both doors with no second
            // edit. That property is the whole design and it survives only
            // while this stays a call rather than a copy.
            //
            // Before that this method held its own branching, and the branching
            // was the bug. It read:
            //
            //     message = rx.StagesBroken > 0 ? ReportText(rx) : "nothing wrong"
            //
            // so a total failure to LOAD the rules — a missing embedded copy, an
            // unreadable override, an override that is empty or all comments —
            // reported as good news, because StagesBroken is zero in every one
            // of those cases (#370). "Nothing is wrong", "something is wrong"
            // and "we could not check" are three different answers, and
            // collapsing the third into the first is the worst available
            // collapse. The analyzer has always produced the right sentence for
            // it; this call site threw it away.
            ReceiveCheckResult rx = ReceiveAudioCheck.Run(_rig!);
            _lastRxReport = rx.Report;

            // The verdict first, because a screen reader entering this box
            // reads the line the caret is on. Then the measurement, then the
            // honest census and the walk. Every one of those strings is
            // assembled in ReceiveAudioCheck, so this box and the Fixer's
            // report can never say different things about the same radio.
            var sb = new StringBuilder();
            sb.AppendLine(rx.Verdict);
            if (rx.Arrival.Length != 0)
            {
                sb.AppendLine();
                sb.AppendLine(rx.Arrival);
            }
            sb.AppendLine();
            sb.AppendLine(rx.Census);
            sb.AppendLine();
            sb.AppendLine("Stage by stage:");
            sb.Append(rx.Walk);
            message = sb.ToString();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Diagnostics: receive check failed — " + ex.Message, TraceLevel.Error);
            message = Lexicon.Get("audio.diagnostics.rx_check_failed", ("reason", ex.Message));
        }

        _rxAdvisoryBox.Text = message + Environment.NewLine
                            + Lexicon.Get("audio.diagnostics.checked_at",
                                  ("time", DateTime.Now.ToString("HH:mm:ss")));

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

        TransmitCheckResult walk;
        try
        {
            FlexBase? rig = _rig;

            // Subscribe BEFORE reading anything, and clear the stale flag
            // first, so a meter arriving mid-collection is caught rather than
            // dropped. Subscribing afterwards leaves a window in which the
            // answer changes unnoticed — and that window is the first check
            // after a connect, which is the exact case the subscription exists
            // for, because the meter list grows during registration.
            _txReportStale = false;
            WatchInventory(rig);

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

            // ONE CALL, AND IT IS THE ONE THE FIXER MAKES (#400).
            //
            // This door and the Fixer's keying stages walk the same thirteen
            // stages, on the same rules, phrased by the same code. Add a rule to
            // tx-chain-rules.txt and it appears at both with no second edit —
            // the property is the whole design, and it survives only while this
            // stays a call rather than a copy. The receive half was joined this
            // way on 2026-08-28 (#367); this is the other half.
            //
            // AND THE TWO DOORS ARE NOT INTERCHANGEABLE, which is why joining
            // them mattered. Nothing in this room can key a radio, so the three
            // stages that only exist during a transmission — stage 2, the
            // microphone actually capturing; stage 11, what the radio says it
            // hears; stage 12, radio frequency out of the radio — will report
            // "transmit and run the test again" from here for ever. The Fixer
            // fills exactly those three, and until now it did not run the walk
            // at all.
            walk = TransmitChainCheck.Run(rig, pcFacts);
            _lastTxWalk = walk;
        }
        catch (Exception ex)
        {
            // A diagnostic that throws is worse than one that says it could not
            // look, so this never reaches the operator as a crash.
            Tracing.TraceLine("Diagnostics: transmit check failed — " + ex, TraceLevel.Error);
            _lastTxWalk = null;
            _txReportBox.Text = Lexicon.Get("audio.diagnostics.tx_check_failed_shown", ("reason", ex.Message));
            _evidenceBox.Text = NoEvidenceYet;
            if (_copyEvidenceButton != null) _copyEvidenceButton.IsEnabled = false;
            if (speak)
            {
                _txReportBox.Focus();
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.diagnostics.tx_check_failed_spoken", ("reason", ex.Message)),
                    VerbosityLevel.Critical, true);
            }
            return;
        }

        _txReportBox.Text = ReportText(walk);
        _evidenceBox.Text = EvidenceText(walk);
        if (_copyEvidenceButton != null) _copyEvidenceButton.IsEnabled = true;

        if (!speak) return;
        _txReportBox.Focus();

        // The operator pressed a button to ask a question, and the answer is
        // not carried by the control that just took focus — a screen reader
        // landing in a long edit reads its first line, not its verdict. This is
        // the case app speech exists for, and it is the same behaviour the
        // receive advisory has always had in Settings.
        ScreenReaderOutput.Speak(walk.Verdict + " " + walk.Census,
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
    private string ReportText(TransmitCheckResult walk)
    {
        var sb = new StringBuilder();

        if (_txReportStale)
        {
            sb.AppendLine("This check is out of date: the radio's meter list changed after it ran. "
                        + "Run it again for a current answer.");
            sb.AppendLine();
        }

        // EVERY SENTENCE BELOW COMES OUT OF THE SHARED CHECK. What this method
        // owns is the LAYOUT — which parts a one-text-box surface shows and in
        // what order — and the staleness note above, which is about this
        // window's own subscription and belongs to nobody else. The Fixer
        // renders the same parts into a findings list and an evidence block
        // because its container is different; neither door owns the words.
        sb.AppendLine(walk.Verdict);
        sb.AppendLine();
        sb.AppendLine(walk.Census);
        sb.AppendLine();
        sb.AppendLine("Checked at " + walk.Report.At.ToString("HH:mm:ss") + ".");
        sb.AppendLine();
        sb.AppendLine("Stage by stage:");
        sb.AppendLine(walk.Walk);

        return sb.ToString();
    }

    /// <summary>
    /// The evidence block, with the radio's identity from the radio layer and
    /// the software's from <see cref="DiagnosticSnapshot"/> — which stays the
    /// only assembler of version strings in the app.
    /// </summary>
    private string EvidenceText(TransmitCheckResult walk)
    {
        // The shared check owns the fallback too: a station or build line that
        // throws must not cost the whole block, and half an evidence block is
        // still worth sending where an exception is not.
        string body = walk.EvidenceForSupport(TxChainFacts.StationLines(_rig),
                                              TxChainPcFacts.BuildLines());

        // A pointer rather than a copy. This block quotes only the readings
        // behind the verdict; the whole census runs to a hundred meters on an
        // 8600 and belongs in its own export, not pasted into the middle of a
        // support email nobody asked for.
        return body + Environment.NewLine
             + "The radio's full meter list, with every reading and its age, is behind "
             + "Show All Meters on the Meters page of this workshop, and can be copied "
             + "separately."
             + Environment.NewLine;
    }

    private void CopyEvidence()
    {
        string text = _evidenceBox?.Text ?? "";
        if (text.Length == 0 || text == NoEvidenceYet)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.diagnostics.nothing_to_copy"),
                                     VerbosityLevel.Critical, true);
            return;
        }

        try
        {
            Clipboard.SetText(text);
            // A receipt, because nothing visible changes when a copy succeeds —
            // and a copy that silently failed would be discovered only when the
            // operator pasted an empty email.
            ScreenReaderOutput.Speak(Lexicon.Get("audio.diagnostics.evidence_copied"), VerbosityLevel.Critical, true);
        }
        catch (Exception ex)
        {
            // The clipboard is genuinely refusable: another process can hold it
            // open. Never announce a copy that did not happen.
            Tracing.TraceLine("Diagnostics: clipboard copy failed — " + ex.Message, TraceLevel.Warning);
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.diagnostics.clipboard_refused"),
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
        inv.InventoryChanged += OnDiagnosticsInventoryChanged;
    }

    private void UnwatchInventory()
    {
        MeterInventory? inv = _watchedInventory;
        _watchedInventory = null;
        if (inv == null) return;
        try { inv.InventoryChanged -= OnDiagnosticsInventoryChanged; }
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
    private void OnDiagnosticsInventoryChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            // The meter thread can raise this between the window starting to
            // close and Closed running the unsubscribe, and BeginInvoke on a
            // shut-down dispatcher throws. A diagnostic must not be the thing
            // that crashes the app on the way out.
            try { Dispatcher.BeginInvoke(() => OnDiagnosticsInventoryChanged(sender, e)); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Diagnostics: meter change arrived after the window closed — "
                                  + ex.Message, TraceLevel.Verbose);
            }
            return;
        }

        if (_txReportStale) return;
        _txReportStale = true;

        // The flag is set even when no report exists yet, because the
        // subscription now starts BEFORE the facts are collected: a meter that
        // arrives mid-collection has to taint the report that is about to be
        // written, not be dropped for having arrived early.
        if (_lastTxWalk == null) return;
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
        if (_txReportBox == null || _lastTxWalk == null) return;

        if (!_txReportBox.IsKeyboardFocusWithin)
        {
            _txReportBox.Text = ReportText(_lastTxWalk);
            return;
        }

        void OnLeft(object? s, RoutedEventArgs e)
        {
            _txReportBox.LostKeyboardFocus -= OnLeft;
            if (_lastTxWalk != null) _txReportBox.Text = ReportText(_lastTxWalk);
        }
        _txReportBox.LostKeyboardFocus += OnLeft;
    }

    #endregion
}
