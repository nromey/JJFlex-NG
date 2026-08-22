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
            HeaderText.Text = Lexicon.Get("audio.power.no_radio");
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
                RfPowerField.Setup(Lexicon.Get("audio.power.field_transmit_drive"),
                    FlexBase.XvtrDriveMinCentiDbm, _rig.XvtrDriveMaxCentiDbm,
                    FlexBase.XvtrDriveIncrementCentiDbm,
                    _rig.XvtrDrivePowerCentiDbm,
                    decimalPlaces: 2, unit: Lexicon.Get("audio.power.unit_label_dbm"));
                // Two whole sentences rather than one sentence plus a
                // space-prefixed fragment: the operator edits these in a text
                // editor with a screen reader, and a leading space inside a
                // JSON value is exactly the kind of thing that cannot be heard.
                HeaderText.Text = string.IsNullOrEmpty(name)
                    ? Lexicon.Get("audio.power.header_xvtr", ("antenna", _rig.TXAntennaName))
                    : Lexicon.Get("audio.power.header_xvtr_named",
                        ("name", name), ("antenna", _rig.TXAntennaName));
                RangeText.Text = Lexicon.Get("audio.power.range_dbm",
                    ("min", $"{FlexBase.XvtrDriveMinCentiDbm / 100.0:F2}"),
                    ("max", $"{_rig.XvtrDriveMaxCentiDbm / 100.0:F2}"));
            }
            else
            {
                RfPowerField.Setup(Lexicon.Get("audio.power.field_transmit_power"), 0, 100, 1, _rig.XmitPower,
                    decimalPlaces: 0, unit: Lexicon.Get("audio.power.unit_label_watts"));
                HeaderText.Text =
                    Lexicon.Get("audio.power.header_watts", ("antenna", _rig.TXAntennaName));
                RangeText.Text = Lexicon.Get("audio.power.range_watts");
            }

            // Tune power stays integer watts in both personalities — the ATU
            // carrier level is a main-PA concern, not a transverter one.
            TunePowerField.Setup(Lexicon.Get("audio.power.field_tune_power"), 0, 100, 1, _rig.TunePower,
                decimalPlaces: 0, unit: Lexicon.Get("audio.power.unit_label_watts"));
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
            // Same reasoning as the header above — two full utterances rather
            // than a sentence with a comma-prefixed fragment glued on.
            ScreenReaderOutput.Speak(
                string.IsNullOrEmpty(name)
                    ? Lexicon.Get("audio.power.unit_dbm")
                    : Lexicon.Get("audio.power.unit_dbm_named", ("name", name)),
                VerbosityLevel.Terse, interrupt);
        }
        else
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.power.unit_watts"), VerbosityLevel.Terse, interrupt);
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
