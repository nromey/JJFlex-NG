using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Accessible radio status dialog. Shows a live snapshot of radio state
/// in a read-only text readout for screen reader navigation (line/word/
/// character, select-and-copy). Auto-refreshes on a timer.
/// Sprint 24 Phase 9A; ListBox converted to read-only TextBox in
/// Phase 0.5d (2026-08-10) — status is prose, not a selection list.
/// </summary>
public partial class StatusDialog : JJFlexDialog
{
    /// <summary>Radio instance. Null when not connected.</summary>
    public FlexBase? Rig { get; set; }

    private readonly DispatcherTimer _refreshTimer;

    public StatusDialog()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refreshTimer.Tick += (s, e) => RefreshStatus();

        Loaded += StatusDialog_Loaded;
        Closing += StatusDialog_Closing;
    }

    protected override void FocusFirstControl()
    {
        // Don't use base MoveFocus — we'll set focus after items are populated
    }

    private void StatusDialog_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        StatusText.CaretIndex = 0;
        StatusText.Focus();
        _refreshTimer.Start();
    }

    private void StatusDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
    }

    // Lines for the current rebuild; joined into StatusText at the end.
    private readonly List<string> _lines = new();

    /// <summary>
    /// Rebuild the status readout from current radio state.
    /// </summary>
    private void RefreshStatus()
    {
        // QB Track D: the identity card refreshes on the same cadence and
        // guards its own focus, so it stays current even while the user is
        // reading the status readout (and vice versa).
        IdentityCard.Rig = Rig;

        // Don't refresh while the user is reading — a rebuild would reset
        // their caret position mid-readout.
        if (StatusText.IsKeyboardFocusWithin) return;

        _lines.Clear();

        if (Rig == null)
        {
            AddItem(Lexicon.Get("connect.status.not_connected"));
            AddItem(Lexicon.Get("connect.status.connect_hint"));
            CommitLines();
            return;
        }

        var snap = RadioStatusBuilder.BuildDetailedStatus(Rig);
        if (!snap.IsConnected)
        {
            AddItem(Lexicon.Get("connect.status.not_connected"));
            CommitLines();
            return;
        }

        // Radio info section
        AddSection(Lexicon.Get("connect.status.section_radio"));
        AddItem($"{snap.RadioModel}");
        if (!string.IsNullOrEmpty(snap.RadioNickname))
            AddItem(Lexicon.Get("connect.status.radio_name", ("nickname", snap.RadioNickname)));
        AddItem(snap.IsRemote
            ? Lexicon.Get("connect.status.via_smartlink")
            : Lexicon.Get("connect.status.via_local"));

        // Slice section
        int numSlices = Rig.MyNumSlices;
        AddSection(Lexicon.Get("connect.status.section_slices", ("numSlices", numSlices)));

        if (numSlices == 0)
        {
            AddItem(Lexicon.Get("connect.status.no_slices"));
        }
        else
        {
            string fullSliceStatus = RadioStatusBuilder.BuildFullSliceStatus(Rig);
            // Split the multi-sentence status into individual items
            foreach (string part in fullSliceStatus.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    AddItem(trimmed);
            }
        }

        // Meters section
        AddSection(Lexicon.Get("connect.status.section_meters"));
        string meterSummary = MeterToneEngine.GetMeterSpeechSummary();
        if (!string.IsNullOrWhiteSpace(meterSummary))
        {
            foreach (string part in meterSummary.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    AddItem(trimmed);
            }
        }

        // TX state
        AddSection(Lexicon.Get("connect.status.section_transmit"));
        AddItem(snap.IsTransmitting
            ? Lexicon.Get("connect.status.transmitting")
            : Lexicon.Get("connect.status.receiving"));
        if (Rig.CanTransmit)
        {
            string txLetter = Rig.TXSliceLetter;
            if (!string.IsNullOrEmpty(txLetter))
                AddItem(Lexicon.Get("connect.status.tx_slice", ("txLetter", txLetter)));
        }

        // ATU section
        if (Rig.HasATU)
        {
            AddSection(Lexicon.Get("connect.status.section_tuner"));
            string tunerState = Rig.FlexTunerType switch
            {
                FlexBase.FlexTunerTypes.auto => Lexicon.Get("connect.status.atu_automatic"),
                FlexBase.FlexTunerTypes.manual => Lexicon.Get("connect.status.atu_manual"),
                _ => Lexicon.Get("connect.status.atu_unavailable")
            };
            AddItem(tunerState);
        }

        CommitLines();
    }

    private void AddSection(string heading)
    {
        // Blank line before section (except first)
        if (_lines.Count > 0)
            _lines.Add("");
        _lines.Add(Lexicon.Get("connect.status.section_marker", ("heading", heading)));
    }

    private void AddItem(string text)
    {
        _lines.Add(text);
    }

    private void CommitLines()
    {
        StatusText.Text = string.Join(Environment.NewLine, _lines);
    }

    /// <summary>
    /// Build a plain-text version of the status for clipboard.
    /// </summary>
    private string BuildClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Lexicon.Get("connect.status.clipboard_title"));
        sb.AppendLine(Lexicon.Get("connect.status.clipboard_generated",
            ("generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))));
        sb.AppendLine();

        sb.AppendLine(StatusText.Text);

        // QB Track D: the identity card's lines belong in the copied snapshot
        // too — a pasted status report that omits how the radio is reached is
        // half a report. Same builder the card uses, so the text matches.
        sb.AppendLine();
        sb.AppendLine(Lexicon.Get("connect.status.clipboard_network_section"));
        foreach (string line in Radios.NetworkIdentityInfo.BuildLines(Rig))
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildClipboardText());
            Radios.ScreenReaderOutput.Speak(Lexicon.Get("connect.status.copied"), VerbosityLevel.Terse, true);
        }
        catch
        {
            Radios.ScreenReaderOutput.Speak(Lexicon.Get("connect.status.copy_failed"), VerbosityLevel.Critical, true);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
