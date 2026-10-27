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
/// Audio Workshop, Meters category: the read-only meter readings and the
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

    #region Meters category: the eight live readings

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

        // The test tone, reference recording, take playback and mic reading
        // housekeeping ran here on every tick until #835; it runs in Check
        // your audio's own poll now, which owns those controls.

        // Both of these asked SelectedIndex == 1 and == 0 until 2026-08-25,
        // which was correct right up until the meter merge removed a category.
        // An index comparison does not fail loudly when the order changes; it
        // silently starts polling the wrong panel, and a meter readout that has
        // quietly stopped refreshing looks exactly like a meter that is steady.
        if (MetersTab.IsSelected)
            PollMeters();

        // TX Audio was one category until 2026-08-25, then three, and since
        // #835 two (Hear Yourself became Check your audio). The radio-side
        // values this refreshes are spread across both — the mic source, gain
        // and processing in Transmit Settings, the device readout in This
        // Computer — so the poll runs while EITHER is showing. Testing only
        // one would leave the other frozen, which reads as steady rather than
        // as stopped.
        if (ThisComputerTab.IsSelected || TransmitSettingsTab.IsSelected)
            PollTxAudio();

        // The inventory catches up here when it is showing and a change arrived
        // while it was being read. It now lives inside the Meters category, so
        // the tab test is the same one the readings use — but it also has to be
        // REVEALED, because there is no point rebuilding a hundred lines that
        // are collapsed behind a button nobody has pressed.
        if (MetersTab.IsSelected && InventoryShowing && _inventoryPending
            && _inventoryReportBox?.IsKeyboardFocusWithin != true)
        {
            RefreshMeterInventory(announce: false);
        }
    }

    private void PollMeters()
    {
        if (_rig == null) return;

        // SMeterReading owns the arithmetic (Radios/SMeterReading.cs). This
        // was a fifth independent copy of a rule that has already shipped
        // wrong once — correct here today, but only by luck rather than by
        // structure. Compact rather than Display because this is a readout,
        // not speech: "S9+5 dB" reads fine on a screen and the spoken surfaces
        // say "plus" from the same numbers.
        SetMeterText(_sMeterBox, "S-Meter: " + SMeterReading.Compact(_rig.SMeter));

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

    #endregion
}
