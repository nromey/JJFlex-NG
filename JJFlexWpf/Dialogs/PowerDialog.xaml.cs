using System;
using System.Windows;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// QB Track I — the Power dialog. The addressable menu path for transmit
/// power (Radio → Transmit → Power, Slice → Transmission → Power); until now
/// power was only reachable through the ScreenFields Transmission expander.
///
/// XVTR-aware: when the active slice's TX antenna is the transverter port,
/// the power field switches to transverter drive in dBm with decimal entry
/// (hundredths), honoring the FlexLib drive limits. Mixer overdrive is the
/// classic transverter killer — the radio's own design puts fine drive
/// control only in the XVTR band, and this dialog follows it. Integer watts
/// otherwise (Radio.RFPower is an int — whole watts is a radio-side reality).
///
/// Changes apply live (no OK/Apply): each arrow step or confirmed typed value
/// goes straight to the radio, and the control speaks the result with its
/// unit. Escape closes (JJFlexDialog base behavior).
/// </summary>
public partial class PowerDialog : JJFlexDialog
{
    private readonly FlexBase? _rig;
    private DispatcherTimer? _pollTimer;
    private bool _xvtrMode;
    private bool _polling;

    public PowerDialog(FlexBase? rig)
    {
        _rig = rig;
        InitializeComponent();

        ConfigureForCurrentMode();

        RfPowerField.ValueChanged += (s, v) =>
        {
            if (_polling || _rig == null) return;
            if (_xvtrMode) _rig.XvtrDrivePowerCentiDbm = v;
            else _rig.XmitPower = v;
        };
        TunePowerField.ValueChanged += (s, v) =>
        {
            if (_polling || _rig == null) return;
            _rig.TunePower = v;
        };

        Loaded += PowerDialog_Loaded;
        Closed += (s, e) => { _pollTimer?.Stop(); _pollTimer = null; };
    }

    /// <summary>
    /// Configure both fields for the current watts-vs-dBm personality.
    /// Called at open and again live if the TX antenna moves on or off the
    /// transverter port while the dialog is up.
    /// </summary>
    private void ConfigureForCurrentMode()
    {
        if (_rig == null)
        {
            HeaderText.Text = "No radio connected.";
            System.Windows.Automation.AutomationProperties.SetName(HeaderText, HeaderText.Text);
            RfPowerField.IsEnabled = false;
            TunePowerField.IsEnabled = false;
            return;
        }

        _xvtrMode = _rig.XvtrPowerAvailable;
        _polling = true;
        try
        {
            if (_xvtrMode)
            {
                string name = _rig.ActiveXvtrName;
                string forName = string.IsNullOrEmpty(name) ? "" : $" {name}";
                RfPowerField.Setup("Transmit drive",
                    FlexBase.XvtrDriveMinCentiDbm, _rig.XvtrDriveMaxCentiDbm,
                    FlexBase.XvtrDriveIncrementCentiDbm,
                    _rig.XvtrDrivePowerCentiDbm,
                    decimalPlaces: 2, unit: "dBm");
                HeaderText.Text =
                    $"Power in dBm — transverter{forName} drive level. TX antenna: {_rig.TXAntennaName}.";
                RangeText.Text =
                    $"Range {FlexBase.XvtrDriveMinCentiDbm / 100.0:F2} to {_rig.XvtrDriveMaxCentiDbm / 100.0:F2} dBm, " +
                    "hundredths of a dB. Arrows adjust by 0.1 dB, Shift plus arrows by 0.01. " +
                    "Type digits, minus, and point, then Enter.";
            }
            else
            {
                RfPowerField.Setup("Transmit power", 0, 100, 1, _rig.XmitPower,
                    decimalPlaces: 0, unit: "watts");
                HeaderText.Text =
                    $"Power in watts. TX antenna: {_rig.TXAntennaName}.";
                RangeText.Text =
                    "Range 0 to 100 watts, whole watts only — the radio's power control is " +
                    "an integer. Arrows adjust by 1, type digits then Enter for an exact value.";
            }

            // Tune power stays integer watts in both personalities — the ATU
            // carrier level is a main-PA concern, not a transverter one.
            TunePowerField.Setup("Tune power", 0, 100, 1, _rig.TunePower,
                decimalPlaces: 0, unit: "watts");
        }
        finally
        {
            _polling = false;
        }

        System.Windows.Automation.AutomationProperties.SetName(HeaderText, HeaderText.Text);
        System.Windows.Automation.AutomationProperties.SetName(RangeText, RangeText.Text);
    }

    private void PowerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // Track outside changes (another client, a profile load) and the
        // TX-antenna personality while open. 500 ms is plenty for a dialog.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += PollTick;
        _pollTimer.Start();

        // Announce the unit on entry — the load-bearing accessibility promise
        // of this dialog. Delayed so the base class's title announcement lands
        // first; not interrupting, so the two read in sequence.
        Dispatcher.BeginInvoke(async () =>
        {
            await System.Threading.Tasks.Task.Delay(700);
            SpeakUnitLine(interrupt: false);
        });
    }

    private void SpeakUnitLine(bool interrupt)
    {
        if (_rig == null) return;
        if (_xvtrMode)
        {
            string name = _rig.ActiveXvtrName;
            string forName = string.IsNullOrEmpty(name) ? "" : $", transverter {name} drive";
            ScreenReaderOutput.Speak($"Power, in d B m{forName}.",
                VerbosityLevel.Terse, interrupt);
        }
        else
        {
            ScreenReaderOutput.Speak("Power, in watts.", VerbosityLevel.Terse, interrupt);
        }
    }

    private void PollTick(object? sender, EventArgs e)
    {
        if (_rig == null) return;

        // Personality change while open (TX antenna moved on/off the XVTR
        // port, here or from another client): reconfigure and say so —
        // silently changing the meaning of a focused number would be lying.
        if (_rig.XvtrPowerAvailable != _xvtrMode)
        {
            ConfigureForCurrentMode();
            SpeakUnitLine(interrupt: true);
            return;
        }

        _polling = true;
        try
        {
            RfPowerField.Value = _xvtrMode ? _rig.XvtrDrivePowerCentiDbm : _rig.XmitPower;
            TunePowerField.Value = _rig.TunePower;
        }
        finally
        {
            _polling = false;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch (InvalidOperationException) { }
        Close();
    }
}
