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
/// Audio Workshop, TX Audio tab: the test tone section — a steady tone
/// sent in place of the microphone, its passband check, and the local
/// monitor of it.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Test Tone Section (Audio Track C)

    /// <summary>
    /// The built-in TX test tone: a known tone at a known level that REPLACES
    /// the microphone in the PC-audio transmit path (the mic is muted while it
    /// runs — never mixed, no room bleed). Frequency is an accessibility
    /// choice, not a convenience: a test tone the operator cannot hear is
    /// useless for confirming the check is running, so it is adjustable, with
    /// named presets plus free entry, and persists per-operator in app
    /// settings. Passband policy is allow-and-warn (flexibility principle) —
    /// see UpdateToneStatus and BuildToneAnnouncement for the warning ladder
    /// that keeps it unmissable.
    /// </summary>
    private void BuildTestToneSection()
    {
        AddRadioSection(HearYourselfContent, "Test Tone");

        _toneCheck = MakeToggle("Test tone instead of microphone");
        _toneCheck.Checked += (s, e) => ToneArmChanged(true);
        _toneCheck.Unchecked += (s, e) => ToneArmChanged(false);
        AddToSection(HearYourselfContent, _toneCheck);

        _tonePresetControl = MakeCycle("Tone frequency", new[]
        {
            "440 hertz reference",
            "700 hertz CW tone",
            "1000 hertz standard test",
            "Custom frequency"
        });
        _tonePresetControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            bool custom = idx >= TonePresetHz.Length;
            if (_toneFreqControl != null)
                _toneFreqControl.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            ToneParamsChanged(speakPassband: true);
        };
        AddToSection(HearYourselfContent, _tonePresetControl);

        _toneFreqControl = new ValueFieldControl();
        _toneFreqControl.Setup("Custom frequency", 50, 10000, 10, 440, 0, "hertz");
        _toneFreqControl.Visibility = Visibility.Collapsed;
        _toneFreqControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            ToneParamsChanged(speakPassband: true);
        };
        AddToSection(HearYourselfContent, _toneFreqControl);

        _toneLevelControl = new ValueFieldControl();
        _toneLevelControl.Setup("Tone level", -40, 0, 1, -10, 0, "dBFS");
        _toneLevelControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            ToneParamsChanged(speakPassband: false);
        };
        AddToSection(HearYourselfContent, _toneLevelControl);

        _toneMonitorCheck = MakeToggle("Hear the tone while it transmits");
        _toneMonitorCheck.IsChecked = true;
        _toneMonitorCheck.Checked += (s, e) => ToneMonitorChanged(true);
        _toneMonitorCheck.Unchecked += (s, e) => ToneMonitorChanged(false);
        AddToSection(HearYourselfContent, _toneMonitorCheck);

        _toneInfo = new TextBlock
        {
            Text = "",
            Margin = new Thickness(2, 2, 2, 4),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(_toneInfo, "Test tone passband status");
        AutomationProperties.SetLiveSetting(_toneInfo, AutomationLiveSetting.Polite);
        AddToSection(HearYourselfContent, _toneInfo);
    }

    /// <summary>The effective tone frequency: preset value, or the custom field.</summary>
    private int CurrentToneFrequencyHz()
    {
        int idx = _tonePresetControl?.SelectedIndex ?? 0;
        if (idx >= 0 && idx < TonePresetHz.Length) return TonePresetHz[idx];
        return _toneFreqControl?.Value ?? 440;
    }

    /// <summary>
    /// Reflect persisted per-operator tone settings (app settings store) in
    /// the controls, and push them to the rig's generator.
    /// </summary>
    private void LoadToneSettings()
    {
        var cfg = AudioConfigSource?.Invoke();
        int freq = Math.Clamp(cfg?.TxToneFrequencyHz ?? 440, 50, 10000);
        int level = Math.Clamp(cfg?.TxToneLevelDb ?? -10, -40, 0);
        bool monitor = cfg?.TxToneLocalMonitor ?? true;

        int presetIdx = Array.IndexOf(TonePresetHz, freq);
        if (presetIdx < 0) presetIdx = TonePresetHz.Length; // Custom

        _polling = true;
        try
        {
            if (_tonePresetControl != null)
            {
                _tonePresetControl.SuppressEvents = true;
                _tonePresetControl.SelectedIndex = presetIdx;
                _tonePresetControl.SuppressEvents = false;
            }
            if (_toneFreqControl != null)
            {
                _toneFreqControl.SuppressEvents = true;
                _toneFreqControl.Value = freq;
                _toneFreqControl.SuppressEvents = false;
                _toneFreqControl.Visibility = presetIdx >= TonePresetHz.Length
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_toneLevelControl != null)
            {
                _toneLevelControl.SuppressEvents = true;
                _toneLevelControl.Value = level;
                _toneLevelControl.SuppressEvents = false;
            }
            if (_toneMonitorCheck != null)
                _toneMonitorCheck.IsChecked = monitor;
        }
        finally
        {
            _polling = false;
        }

        if (_rig != null)
        {
            _rig.TxToneFrequency = freq;
            _rig.TxToneLevelDb = level;
        }
    }

    /// <summary>Persist tone settings to the per-operator app settings store.</summary>
    private void SaveToneSettings()
    {
        var cfg = AudioConfigSource?.Invoke();
        if (cfg == null) return;
        cfg.TxToneFrequencyHz = CurrentToneFrequencyHz();
        cfg.TxToneLevelDb = _toneLevelControl?.Value ?? -10;
        cfg.TxToneLocalMonitor = _toneMonitorCheck?.IsChecked == true;
        AudioConfigSave?.Invoke();
    }

    /// <summary>
    /// Frequency or level changed: push to the generator, persist, refresh the
    /// passband status, and — for frequency changes — speak the passband
    /// verdict when there is trouble. The field control already spoke the new
    /// value; the warning queues right behind it.
    /// </summary>
    private void ToneParamsChanged(bool speakPassband)
    {
        int freq = CurrentToneFrequencyHz();
        if (_rig != null)
        {
            _rig.TxToneFrequency = freq;
            _rig.TxToneLevelDb = _toneLevelControl?.Value ?? -10;
        }
        SaveToneSettings();
        UpdateToneStatus(speakIfNewlyOutside: false);
        if (speakPassband)
        {
            string trouble = PassbandCheck(freq, out bool outside);
            if (!string.IsNullOrEmpty(trouble))
            {
                if (outside) EarconPlayer.Warning2Beep();
                ScreenReaderOutput.Speak(trouble,
                    outside ? VerbosityLevel.Critical : VerbosityLevel.Terse);
            }
            _toneOutsideWarned = outside;
        }
    }

    /// <summary>
    /// The passband trap check. SSB transmit filters typically pass roughly
    /// 100-2900 Hz; a tone moved to where the operator hears best can land
    /// outside the filter and transmit NOTHING — silently, while they believe
    /// they are testing. Policy is allow-and-warn (never remove the choice),
    /// so this must be unmissable: it speaks at set time, at arm time, at
    /// every key-down, and when the filter later moves out from under the
    /// tone. Returns the plain-language warning, a near-edge note, or "".
    /// </summary>
    private string PassbandCheck(int freqHz, out bool outside)
    {
        outside = false;
        var rig = _rig;
        if (rig == null) return "";
        int low = rig.TXFilterLow;
        int high = rig.TXFilterHigh;
        if (high <= low) return ""; // filter unknown — nothing honest to say
        if (freqHz < low || freqHz > high)
        {
            outside = true;
            return Lexicon.Get("audio.tone.outside_passband",
                ("freq", freqHz), ("low", low), ("high", high));
        }
        if (freqHz - low < 50 || high - freqHz < 50)
        {
            return Lexicon.Get("audio.tone.near_passband_edge",
                ("freq", freqHz), ("low", low), ("high", high));
        }
        return "";
    }

    /// <summary>
    /// Refresh the visible passband status line, and — when asked — speak an
    /// edge-triggered warning if the TX filter has moved out from under an
    /// armed tone (the operator can change the filter at any time, including
    /// while the tone transmits; that must not fail quietly).
    /// </summary>
    private void UpdateToneStatus(bool speakIfNewlyOutside)
    {
        if (_toneInfo == null) return;
        var rig = _rig;
        int freq = CurrentToneFrequencyHz();
        string text;
        bool outside = false;
        if (rig == null)
        {
            text = Lexicon.Get("audio.tone.no_radio_for_passband_check");
        }
        else
        {
            string trouble = PassbandCheck(freq, out outside);
            text = string.IsNullOrEmpty(trouble)
                ? Lexicon.Get("audio.tone.inside_passband",
                    ("freq", freq), ("low", rig.TXFilterLow), ("high", rig.TXFilterHigh))
                : trouble;
        }
        if (_toneInfo.Text != text)
        {
            _toneInfo.Text = text;
            AutomationProperties.SetName(_toneInfo, text);
        }
        if (speakIfNewlyOutside)
        {
            if (outside && !_toneOutsideWarned)
            {
                _toneOutsideWarned = true;
                EarconPlayer.Warning2Beep();
                ScreenReaderOutput.Speak(text, VerbosityLevel.Critical, interrupt: true);
            }
            else if (!outside)
            {
                _toneOutsideWarned = false;
            }
        }
    }

    /// <summary>
    /// Arm or release the tone. Arming refuses out loud when the tone cannot
    /// reach the transmitter at all (PC audio off, transmit input not PC, CW
    /// mode) — that is not a choice being removed, it is a trap being named:
    /// with the wrong path armed "successfully", something OTHER than the tone
    /// keeps transmitting while the operator believes they are testing.
    /// Passband trouble, by contrast, arms anyway and warns (allow-and-warn).
    /// </summary>
    private void ToneArmChanged(bool armed)
    {
        if (_polling) return;

        if (!armed)
        {
            DisarmTone(speak: true);
            return;
        }

        var rig = _rig;
        if (rig == null)
        {
            SetToneCheckSilently(false);
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical, interrupt: true);
            return;
        }
        string pathTrouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(pathTrouble))
        {
            SetToneCheckSilently(false);
            EarconPlayer.Warning2Beep();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.tone.not_armed", ("reason", pathTrouble)),
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        int freq = CurrentToneFrequencyHz();
        int level = _toneLevelControl?.Value ?? -10;
        rig.TxToneFrequency = freq;
        rig.TxToneLevelDb = level;
        rig.TxToneStart();

        // Every key-down anywhere in the app now says the tone is riding it.
        PttSafetyController.KeyDownAnnouncementExtra = () => _instance?.BuildToneAnnouncement();

        var line = new StringBuilder();
        line.Append(Lexicon.Get("audio.tone.armed", ("freq", freq), ("level", level)));
        line.Append(' ');
        line.Append(Lexicon.Get("audio.tone.armed_replaces_mic"));
        string pb = PassbandCheck(freq, out bool outside);
        if (!string.IsNullOrEmpty(pb)) line.Append(' ').Append(pb);
        _toneOutsideWarned = outside;
        // #128 sweep audit (2026-08-21): the Ctrl+J, G chord answers an arm
        // with the feature-on tone (or the warning pair when the tone sits
        // outside the transmit filter) and this checkbox — the other road
        // into the same armed state — answered with nothing. Mirror the
        // chord exactly: warning outranks confirmation, never both.
        if (outside) EarconPlayer.Warning2Beep();
        else EarconPlayer.FeatureOnTone();
        ScreenReaderOutput.Speak(line.ToString(), VerbosityLevel.Critical, interrupt: true);
        UpdateToneStatus(speakIfNewlyOutside: false);
    }

    /// <summary>
    /// Release the tone and restore the microphone: stop the generator, clear
    /// the key-down announcement hook, and silence the local monitor. Runs on
    /// operator unarm, dialog close, and radio teardown (pass the departing
    /// rig for the teardown case, where _rig is already null).
    /// </summary>
    private void DisarmTone(bool speak, FlexBase? rig = null)
    {
        (rig ?? _rig)?.TxToneStop();
        PttSafetyController.KeyDownAnnouncementExtra = null;
        EarconPlayer.StopTxToneMonitor();
        _toneMonitorSounding = false;
        _toneMonitorProvider = null;
        // #128: tone only on the spoken (operator-visible) path. The silent
        // callers are dialog close and radio teardown, where a feature-off
        // chime would narrate housekeeping — the #58 rule. The chord road
        // plays its own off-tone in KeyCommands, and never comes through here.
        if (speak)
        {
            EarconPlayer.FeatureOffTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.tone.disarmed"),
                VerbosityLevel.Critical, interrupt: true);
        }
    }

    /// <summary>Set the arm checkbox without firing its handlers.</summary>
    private void SetToneCheckSilently(bool value)
    {
        if (_toneCheck == null) return;
        _polling = true;
        try { _toneCheck.IsChecked = value; }
        finally { _polling = false; }
    }

    /// <summary>
    /// Keep the arm checkbox honest against the ENGINE's tone state. The
    /// Ctrl+J, G leader binding (Keys Track, 2026-08-11) arms and disarms
    /// the tone by driving FlexBase directly, so the workshop no longer
    /// owns every state change. Rides the existing meter poll — no second
    /// timer — and syncs silently: the leader already announced the
    /// change, so re-speaking here would double-talk. The key-down
    /// announcement hook follows the same truth (it is how EVERY transmit
    /// path warns that the tone is riding it, so an externally armed tone
    /// must set it too); the local monitor and passband status already
    /// derive from engine state on this same tick.
    /// </summary>
    private void SyncToneArmUi()
    {
        var rig = _rig;
        if (rig == null || _toneCheck == null) return;
        bool engaged = rig.TxToneEngaged;
        if ((_toneCheck.IsChecked == true) == engaged) return;

        SetToneCheckSilently(engaged);
        if (engaged)
            PttSafetyController.KeyDownAnnouncementExtra = () => _instance?.BuildToneAnnouncement();
        else
            PttSafetyController.KeyDownAnnouncementExtra = null;
        UpdateToneStatus(speakIfNewlyOutside: false);
    }

    private void ToneMonitorChanged(bool on)
    {
        if (_polling) return;
        SaveToneSettings();

        // #128 sweep audit (2026-08-21): an operator-facing boolean answers
        // back. The tone is especially load-bearing here because the thing
        // being enabled — the local monitor — only sounds while armed AND
        // transmitting, so flipping it while unkeyed produces no other
        // audible change at all.
        EarconPlayer.ToggleTone(on);

        // DELETED: pure state echo of a CheckBox the screen reader already
        // announces.
        SyncToneMonitor(); // apply immediately, not at the next timer tick
    }

    /// <summary>
    /// Keep the local monitor honest: it sounds ONLY while the tone is armed
    /// AND the radio is actually transmitting (a monitor that sounds while
    /// unkeyed would imply the tone is going out when it is not), and only
    /// when the operator wants it. Runs on every meter-timer tick regardless
    /// of the selected tab, and follows live frequency changes.
    /// </summary>
    private void SyncToneMonitor()
    {
        var rig = _rig;
        bool shouldSound = rig != null && rig.TxToneEngaged && rig.Transmit
            && _toneMonitorCheck?.IsChecked == true;
        if (shouldSound && !_toneMonitorSounding)
        {
            _toneMonitorProvider = EarconPlayer.StartTxToneMonitor(rig!.TxToneFrequency);
            _toneMonitorSounding = true;
        }
        else if (!shouldSound && _toneMonitorSounding)
        {
            EarconPlayer.StopTxToneMonitor();
            _toneMonitorSounding = false;
            _toneMonitorProvider = null;
        }
        else if (shouldSound && _toneMonitorProvider != null)
        {
            _toneMonitorProvider.Frequency = rig!.TxToneFrequency;
        }
    }

    /// <summary>
    /// The spoken line for a transmission the tone is riding. Used by the
    /// PTT controller's key-down hook (every transmit path) and by the Audio
    /// Check session's safety line. Re-checks the path and the passband at
    /// the moment of key-down, because both can have changed since arming.
    /// Returns null when the tone is not engaged.
    /// </summary>
    internal string? BuildToneAnnouncement()
    {
        var rig = _rig;
        if (rig == null || !rig.TxToneEngaged) return null;
        int freq = (int)rig.TxToneFrequency;
        string pathTrouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(pathTrouble))
            return Lexicon.Get("audio.tone.armed_but_not_going_out", ("reason", pathTrouble));
        string line = Lexicon.Get("audio.tone.sending", ("freq", freq));
        string pb = PassbandCheck(freq, out _);
        if (!string.IsNullOrEmpty(pb)) line += " " + pb;
        return line;
    }

    #endregion
}
