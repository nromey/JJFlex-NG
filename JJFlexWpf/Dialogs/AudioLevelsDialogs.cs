using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

// Audio Arc Track A-2 (field feedback, Noel at the radio 2026-08-11):
// a menu is the wrong instrument for riding a value — it dismisses after
// each activation, so nudging a level five times means five trips through
// two menu levels. These two dialogs replace the Audio menu's up/down
// PAIRS with a single door each; inside, every level is a focusable field
// you ride with Up/Down while the dialog stays open.
//
// They are deliberately TWO dialogs, not one: Track A established that
// "PC audio" and "the radio's own jacks" are different things on two
// sides of the wire, and one combined surface would blur exactly the
// distinction the labels just bought. The Home audio expander and the
// Ctrl+J, V volume mode are unchanged — those stay the in-context and
// fast routes; these dialogs are the discoverable, see-everything-at-once
// route for operators who are not layered-command people.

/// <summary>
/// Base for the two audio levels dialogs: a vertical stack of value fields
/// and mute toggles over the live rig, polled so values another surface
/// changes stay honest, with every adjustment speaking its new value
/// (the field controls handle that). Escape closes (house rule, via
/// JJFlexDialog); adjustments apply immediately — there is no OK/Cancel,
/// because the radio is the document and it is already saved.
/// </summary>
public abstract class AudioLevelsDialogBase : JJFlexDialog
{
    protected readonly FlexBase? Rig;
    protected readonly StackPanel Panel;
    protected bool Polling;
    private readonly DispatcherTimer _pollTimer;

    protected AudioLevelsDialogBase(FlexBase? rig, string title)
    {
        Rig = rig;
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;

        Panel = new StackPanel { Margin = new Thickness(12) };
        Content = Panel;

        // 2 Hz poll keeps the fields honest when another surface (the Home
        // expander, volume mode, another client) moves a level while the
        // dialog sits open. SuppressEvents/Polling guards stop the refresh
        // from speaking or writing back.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += (s, e) => { Polling = true; try { PollValues(); } finally { Polling = false; } };
        if (Rig != null) _pollTimer.Start();
        Closed += (s, e) => _pollTimer.Stop();
    }

    /// <summary>Refresh every control from the rig. Runs under the Polling guard.</summary>
    protected abstract void PollValues();

    protected ValueFieldControl AddValue(string label, int min, int max, int step,
        int initial, string unit = "")
    {
        var ctl = new ValueFieldControl();
        ctl.Setup(label, min, max, step, initial, 0, unit);
        Panel.Children.Add(ctl);
        return ctl;
    }

    protected CheckBox AddToggle(string label, bool initial, Action<bool> setter)
    {
        var cb = new CheckBox
        {
            Content = label,
            Margin = new Thickness(2, 4, 2, 4),
            FontSize = 12,
            IsChecked = initial
        };
        AutomationProperties.SetName(cb, label);
        cb.Checked += (s, e) => Toggle(label, true, setter);
        cb.Unchecked += (s, e) => Toggle(label, false, setter);
        Panel.Children.Add(cb);
        return cb;
    }

    private void Toggle(string label, bool on, Action<bool> setter)
    {
        if (Polling || Rig == null) return;
        setter(on);
        ScreenReaderOutput.Speak($"{label} {(on ? "on" : "off")}",
            VerbosityLevel.Terse, interrupt: true);
    }

    /// <summary>Set a value field from the rig without events or speech.</summary>
    protected static void Sync(ValueFieldControl? ctl, int value)
    {
        if (ctl == null) return;
        ctl.SuppressEvents = true;
        ctl.Value = value;
        ctl.SuppressEvents = false;
    }

    /// <summary>Set a toggle from the rig; the Polling guard mutes the handlers.</summary>
    protected void SyncToggle(CheckBox? cb, bool value)
    {
        if (cb == null || cb.IsChecked == value) return;
        cb.IsChecked = value;
    }
}

/// <summary>
/// PC Audio Levels: the levels that shape audio on THIS COMPUTER's side of
/// the wire — how loud the radio plays through the PC, and the mic level
/// feeding transmit. Opened from the Audio menu's "PC Audio Levels" item.
/// </summary>
public sealed class PcAudioLevelsDialog : AudioLevelsDialogBase
{
    private readonly ValueFieldControl _pcVolume;
    private readonly ValueFieldControl _micLevel;
    private readonly Action? _persistPcVolume;

    public PcAudioLevelsDialog(FlexBase? rig, Action? persistPcVolume)
        : base(rig, "PC Audio Levels")
    {
        _persistPcVolume = persistPcVolume;

        // Step 1 matches the Home expander's PC volume field — the range is
        // only 0 to 24 dB, and this surface exists precisely for fine rides.
        _pcVolume = AddValue("PC Output Volume",
            FlexBase.PcOutputVolumeDbMin, FlexBase.PcOutputVolumeDbMax, 1,
            rig?.PcOutputVolumeDb ?? FlexBase.PcOutputVolumeDbSetting, "dB");
        _pcVolume.ValueChanged += (s, v) =>
        {
            if (Rig == null || Polling) return;
            Rig.PcOutputVolumeDb = v;
            // App-level setting — persist as it changes (24 steps max, tiny file).
            _persistPcVolume?.Invoke();
        };

        _micLevel = AddValue("Mic Level", 0, 100, 5, rig?.MicGain ?? 0);
        _micLevel.ValueChanged += (s, v) =>
        {
            if (Rig != null && !Polling) Rig.MicGain = v;
        };
    }

    protected override void PollValues()
    {
        if (Rig == null) return;
        Sync(_pcVolume, Rig.PcOutputVolumeDb);
        Sync(_micLevel, Rig.MicGain);
    }
}

/// <summary>
/// On-Radio Levels: the radio's OWN jacks — headphone, line out, and the
/// three output mutes. "On-radio" stays the load-bearing word: a remote
/// PC-audio operator cannot hear any of these move. Opened from the Audio
/// menu's "On-Radio Levels" item.
/// </summary>
public sealed class OnRadioLevelsDialog : AudioLevelsDialogBase
{
    private readonly ValueFieldControl _headphone;
    private readonly ValueFieldControl _lineout;
    private readonly CheckBox _headphoneMute;
    private readonly CheckBox _lineoutMute;
    private readonly CheckBox _frontSpeakerMute;

    public OnRadioLevelsDialog(FlexBase? rig)
        : base(rig, "On-Radio Levels")
    {
        _headphone = AddValue("On-Radio Headphone Volume", 0, 100, 5,
            rig?.HeadphoneGain ?? 0);
        _headphone.ValueChanged += (s, v) =>
        {
            if (Rig != null && !Polling) Rig.HeadphoneGain = v;
        };

        _lineout = AddValue("On-Radio Line Out Volume", 0, 100, 5,
            rig?.LineoutGain ?? 0);
        _lineout.ValueChanged += (s, v) =>
        {
            if (Rig != null && !Polling) Rig.LineoutGain = v;
        };

        _headphoneMute = AddToggle("Mute Headphone Jack",
            rig?.HeadphoneMute == true, v => { if (Rig != null) Rig.HeadphoneMute = v; });
        _lineoutMute = AddToggle("Mute Line Out",
            rig?.LineoutMute == true, v => { if (Rig != null) Rig.LineoutMute = v; });
        _frontSpeakerMute = AddToggle("Mute Front Speaker",
            rig?.FrontSpeakerMute == true, v => { if (Rig != null) Rig.FrontSpeakerMute = v; });
    }

    protected override void PollValues()
    {
        if (Rig == null) return;
        Sync(_headphone, Rig.HeadphoneGain);
        Sync(_lineout, Rig.LineoutGain);
        SyncToggle(_headphoneMute, Rig.HeadphoneMute);
        SyncToggle(_lineoutMute, Rig.LineoutMute);
        SyncToggle(_frontSpeakerMute, Rig.FrontSpeakerMute);
    }
}
