using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, Live Meters tab: the read-only meter readings and the
/// poll timer that refreshes them.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Live Meter Readings

    // Read-only TextBoxes, not TextBlocks (Track D1, 2026-08-16). The tab had
    // ZERO tab stops from the day it was built — a TextBlock is not focusable,
    // so an operator could never go and ASK a meter what it said; the polite
    // live region was the only way values ever reached a screen reader.
    private TextBox? _sMeterBox;
    private TextBox? _fwdPowerBox;
    private TextBox? _swrBox;
    private TextBox? _alcBox;      // TX drive, SW ALC
    private TextBox? _ampAlcBox;   // external-amplifier ALC (HWALC), for amp users
    private TextBox? _micAudioBox; // transmit mic audio, SC_MIC (honest for PC + analog)
    private TextBox? _paTempBox;
    private TextBox? _voltsBox;

    #endregion

    #region Tab 2: Live Meters

    /// <summary>
    /// Eight read-only reading boxes in three sections. Tab order is build
    /// order — Receiver, then Transmit top-to-bottom, then Hardware — which
    /// is also signal order, and F6/Shift+F6 crosses the three sections the
    /// same way it does on the TX Audio tab (they finally have something
    /// focusable to land on).
    /// </summary>
    private void BuildLiveMetersTab()
    {
        AddRadioSection(LiveMetersContent, "Receiver");

        _sMeterBox = MakeMeterReading("S-Meter");
        AddToSection(LiveMetersContent, _sMeterBox);

        AddRadioSection(LiveMetersContent, "Transmit");

        _fwdPowerBox = MakeMeterReading("Forward Power");
        AddToSection(LiveMetersContent, _fwdPowerBox);

        _swrBox = MakeMeterReading("SWR");
        AddToSection(LiveMetersContent, _swrBox);

        _micAudioBox = MakeMeterReading("Mic audio");
        AddToSection(LiveMetersContent, _micAudioBox);

        _alcBox = MakeMeterReading("TX drive (ALC)");
        AddToSection(LiveMetersContent, _alcBox);

        _ampAlcBox = MakeMeterReading("Amp ALC");
        AddToSection(LiveMetersContent, _ampAlcBox);

        AddRadioSection(LiveMetersContent, "Hardware");

        _paTempBox = MakeMeterReading("PA Temperature");
        AddToSection(LiveMetersContent, _paTempBox);

        _voltsBox = MakeMeterReading("Supply Voltage");
        AddToSection(LiveMetersContent, _voltsBox);
    }

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        if (_rig == null) return;

        // Test tone housekeeping runs on EVERY tick regardless of tab: the
        // arm checkbox must follow the engine (Ctrl+J, G can change it from
        // outside this dialog), the local monitor must track actual
        // transmit state, and the passband warning must fire if the TX
        // filter moves out from under an armed tone — the operator may be
        // on any tab (or in another window) when that happens, and it must
        // not fail quietly.
        SyncToneArmUi();
        SyncToneMonitor();
        UpdateToneStatus(speakIfNewlyOutside: _rig.TxToneEngaged);

        // Reference audio housekeeping, on every tick and every tab for the
        // same reason: a reference pass ends when the recording runs out, not
        // when the operator does anything, and an operator who is not looking
        // at this tab still needs to hear that their microphone came back.
        SyncReferenceUi();

        // The mic reading refreshes on every tick regardless of tab so a
        // review command always reads fresh the moment the operator lands
        // on it.
        UpdateMicReading();

        // Only update meters when the Live Meters tab is selected
        if (MainTabs.SelectedIndex == 1)
            PollMeters();

        // Also refresh TX Audio tab values when visible
        if (MainTabs.SelectedIndex == 0)
            PollTxAudio();

        // The Meter Inventory tab catches up here when it is showing and a
        // change arrived while it was being read. Asked by name rather than by
        // index because tabs get reordered, and a stale index would silently
        // refresh the wrong tab.
        if (MeterInventoryTab.IsSelected && _inventoryPending
            && _inventoryReportBox?.IsKeyboardFocusWithin != true)
        {
            RefreshMeterInventory(announce: false);
        }
    }

    private void PollMeters()
    {
        if (_rig == null) return;

        int sVal = _rig.SMeter;
        // Over S9 the excess is already dB — SMeter returns dB-over-S9
        // plus 9, so the old x6 inflated the reading sixfold.
        string sText = sVal <= 9 ? $"S{sVal}" : $"S9+{sVal - 9} dB";
        SetMeterText(_sMeterBox, $"S-Meter: {sText}");

        // dBm AND watts. dBm is the honest raw figure the bench notes are
        // written in; watts is what an operator sets power in. Showing both
        // means nobody converts in their head at the moment they are trying
        // to read an instrument — and it is the readout the transverter
        // session lives on, where drive is measured in milliwatts.
        SetMeterText(_fwdPowerBox, $"Forward Power: {_rig.PowerDBM:F1} dBm"
            + $" ({FlexBase.FormatForwardPowerSpoken(_rig.ForwardPowerWatts)})");

        SetMeterText(_swrBox, $"SWR: {_rig.SWRValue:F1}");

        // TX drive is SW ALC, not HWALC (the external-amp jack the old readout
        // showed — always ~0). Mic audio is SC_MIC, honest for PC audio AND the
        // analog mic, where the old "Mic Level" (COD-/MIC) read -120 for PC.
        SetMeterText(_alcBox, $"TX drive (ALC): {_rig.SwAlcDb:F1} dBFS");

        SetMeterText(_ampAlcBox, $"Amp ALC: {_rig.ALC:F2}");

        SetMeterText(_micAudioBox, $"Mic audio: {_rig.ScMicDb:F1} dBFS ({MicAudioReport.Verdict(_rig.ScMicMaxDb)})");

        SetMeterText(_paTempBox, $"PA Temperature: {_rig.PATemp:F1} °C");

        SetMeterText(_voltsBox, $"Supply Voltage: {_rig.Volts:F1} V");
    }

    /// <summary>
    /// Assign a meter reading only on change, same reason as the mic reading
    /// box: rewriting identical text twice a second would reset a screen
    /// reader's review position for nothing. When the value genuinely moved,
    /// the caret reset is the price of a live reading — the operator lands,
    /// reads fresh, and the steadier meters (SWR, PA temp, volts) review
    /// undisturbed between real changes.
    /// </summary>
    private static void SetMeterText(TextBox? box, string text)
    {
        if (box == null || box.Text == text) return;
        box.Text = text;
    }

    /// <summary>
    /// Put every reading into a named waiting state — "no radio connected"
    /// when the poll is dead, "no reading yet" when a different rig arrives.
    /// A focusable box holding "Supply Voltage: 13.8 V" with no radio (or the
    /// wrong radio) behind it would be a confident lie — same honesty rule as
    /// UpdateMicReading's disconnect text.
    /// </summary>
    private void ResetMeterReadings(string state)
    {
        SetMeterText(_sMeterBox, $"S-Meter: {state}");
        SetMeterText(_fwdPowerBox, $"Forward Power: {state}");
        SetMeterText(_swrBox, $"SWR: {state}");
        SetMeterText(_micAudioBox, $"Mic audio: {state}");
        SetMeterText(_alcBox, $"TX drive (ALC): {state}");
        SetMeterText(_ampAlcBox, $"Amp ALC: {state}");
        SetMeterText(_paTempBox, $"PA Temperature: {state}");
        SetMeterText(_voltsBox, $"Supply Voltage: {state}");
    }

    /// <summary>
    /// Refresh the read-only mic reading edit. Text only — the accessible
    /// name was set once at build time and live-region notifications are
    /// deliberately absent, so a value moving twice a second never floods
    /// NVDA; the operator's review command reads the fresh text on demand.
    /// Live recent-peak while transmitting (it follows a level back down),
    /// the whole-transmit peak after unkey, honest wording before any
    /// transmit. Mirrors the Home expander's verdict field (Track A).
    /// </summary>
    private void UpdateMicReading()
    {
        if (_micReadingBox == null) return;
        var rig = _rig;
        string text;
        if (rig == null)
        {
            text = "Mic audio: no radio connected";
        }
        else
        {
            float recent = rig.ScMicRecentDb;
            float max = rig.ScMicMaxDb;
            if (rig.Transmit && recent > -140f)
                text = MicAudioReport.Compose(rig, "Mic audio now:", recent, live: true);
            else if (max > -140f)
                text = MicAudioReport.Compose(rig, "Mic audio last transmit:", max, live: false);
            else
                text = "Mic audio: transmit to measure";
        }
        // Assign only on change so an unchanged reading doesn't reset the
        // review cursor twice a second.
        if (_micReadingBox.Text != text)
            _micReadingBox.Text = text;
    }

    #endregion
}
