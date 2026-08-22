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
/// Audio Workshop, TX Audio tab: the PC Cleanup section — transmit noise
/// reduction, the noise gate, and the residual monitor.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region PC Cleanup Section (Track I)

    /// <summary>
    /// PC-side transmit cleanup: noise reduction and the noise gate, with a
    /// monitor that can play WHAT WAS REMOVED. The radio's own processing
    /// (compander, EQ) sculpts the audio it is given; this cleans the room
    /// out of the audio before the radio ever hears it — the fan, the
    /// computer, the air conditioner.
    ///
    /// The removed-audio monitor is the tuning instrument: listening to the
    /// output can never reveal over-processing, because the missing parts
    /// are not there to hear. Hear your own words in the REMOVED audio and
    /// the cleanup is eating your voice — turn the strength down while it
    /// plays until the words fade. The strength control is live for exactly
    /// that loop.
    ///
    /// The gate threshold is not a knob here: it is derived from the room's
    /// measured noise floor (the same figure the Microphone Check reports),
    /// floor plus a margin, so a quiet 3 AM shack and the same shack with
    /// the air conditioner on both land right without retuning.
    /// </summary>
    private void BuildTxCleanupSection()
    {
        AddSectionHeader(TxAudioContent, "PC Cleanup");

        _txNrCheck = MakeToggle("Noise Reduction (cleans the room before the radio hears it)");
        _txNrCheck.Checked += (s, e) => SetTxCleanupToggle("Noise reduction", true);
        _txNrCheck.Unchecked += (s, e) => SetTxCleanupToggle("Noise reduction", false);
        AddToSection(TxAudioContent, _txNrCheck);

        _txNrStrengthControl = new ValueFieldControl();
        _txNrStrengthControl.Setup(
            "Noise reduction strength (lower it if you hear your words in the removed audio)",
            0, 100, 5, 80, 0, "percent");
        _txNrStrengthControl.Visibility = Visibility.Collapsed;
        _txNrStrengthControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            TxAudioConditioning.NrStrength = v / 100f;
        };
        AddToSection(TxAudioContent, _txNrStrengthControl);

        _txGateCheck = MakeToggle("Noise Gate (quiets the microphone between words — never to silence)");
        _txGateCheck.Checked += (s, e) => SetTxCleanupToggle("Noise gate", true);
        _txGateCheck.Unchecked += (s, e) => SetTxCleanupToggle("Noise gate", false);
        AddToSection(TxAudioContent, _txGateCheck);

        // Advanced gate settings, visible only while the gate is on. Each
        // label explains its own default — that is what stops values being
        // changed at random and the result sounding odd.
        _txGateMarginControl = new ValueFieldControl();
        _txGateMarginControl.Setup(
            "Gate margin above your room noise (smaller opens on quieter speech)",
            3, 15, 1, 8, 0, "dB");
        _txGateMarginControl.Visibility = Visibility.Collapsed;
        _txGateMarginControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            TxAudioConditioning.ThresholdMarginDb = v;
        };
        AddToSection(TxAudioContent, _txGateMarginControl);

        _txGateAttackControl = new ValueFieldControl();
        _txGateAttackControl.Setup(
            "Gate attack (fast so it does not clip the start of your words)",
            1, 20, 1, 3, 0, "milliseconds");
        _txGateAttackControl.Visibility = Visibility.Collapsed;
        _txGateAttackControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            var gate = TxAudioConditioning.Conditioner?.Gate;
            if (gate != null) gate.AttackMs = v;
        };
        AddToSection(TxAudioContent, _txGateAttackControl);

        _txGateHoldControl = new ValueFieldControl();
        _txGateHoldControl.Setup(
            "Gate hold (bridges the natural pauses in a sentence so it does not chatter)",
            50, 1000, 25, 150, 0, "milliseconds");
        _txGateHoldControl.Visibility = Visibility.Collapsed;
        _txGateHoldControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            var gate = TxAudioConditioning.Conditioner?.Gate;
            if (gate != null) gate.HoldMs = v;
        };
        AddToSection(TxAudioContent, _txGateHoldControl);

        _txGateReleaseControl = new ValueFieldControl();
        _txGateReleaseControl.Setup(
            "Gate release (how gently it fades down after you stop talking)",
            50, 1000, 25, 200, 0, "milliseconds");
        _txGateReleaseControl.Visibility = Visibility.Collapsed;
        _txGateReleaseControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            var gate = TxAudioConditioning.Conditioner?.Gate;
            if (gate != null) gate.ReleaseMs = v;
        };
        AddToSection(TxAudioContent, _txGateReleaseControl);

        _txGateRangeControl = new ValueFieldControl();
        _txGateRangeControl.Setup(
            "Gate depth (how far it turns the mic down — capped short of silence so you never sound dropped)",
            6, 40, 1, 25, 0, "dB");
        _txGateRangeControl.Visibility = Visibility.Collapsed;
        _txGateRangeControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            var gate = TxAudioConditioning.Conditioner?.Gate;
            if (gate != null) gate.RangeDb = v;
        };
        AddToSection(TxAudioContent, _txGateRangeControl);

        // Read-only EDIT, same reasoning as the mic reading: focusable,
        // review-readable, updated silently so the screen reader's own
        // read-current-control command speaks a fresh value on demand.
        _txCleanupStatusBox = new TextBox
        {
            Text = "Cleanup: off",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2),
            FontSize = 12
        };
        AutomationProperties.SetName(_txCleanupStatusBox, "Cleanup status");
        AddToSection(TxAudioContent, _txCleanupStatusBox);

        _txCleanupMonitorControl = MakeCycle("Listen to cleanup",
            new[] { "Off", "What goes out", "What was removed", "Both, out left and removed right" });
        _txCleanupMonitorControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            TxAudioConditioning.MonitorMode = (JJPortaudio.TxAudioConditioner.MonitorModes)idx;
            if (_txCleanupMonitorVolumeControl != null)
                _txCleanupMonitorVolumeControl.Visibility =
                    idx == 0 ? Visibility.Collapsed : Visibility.Visible;
            // The one thing the control's own announcement cannot carry:
            // what hearing something HERE means.
            if (idx == (int)JJPortaudio.TxAudioConditioner.MonitorModes.Residual)
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.cleanup.residual_monitor_meaning"),
                    VerbosityLevel.Terse);
        };
        AddToSection(TxAudioContent, _txCleanupMonitorControl);

        _txCleanupMonitorVolumeControl = new ValueFieldControl();
        _txCleanupMonitorVolumeControl.Setup("Cleanup monitor volume", 0, 100, 5, 100, 0, "percent");
        _txCleanupMonitorVolumeControl.Visibility = Visibility.Collapsed;
        _txCleanupMonitorVolumeControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            TxAudioConditioning.MonitorVolume = v / 100f;
        };
        AddToSection(TxAudioContent, _txCleanupMonitorVolumeControl);

        var resetButton = new Button
        {
            Content = "Reset cleanup to recommended",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(resetButton, "Reset cleanup to recommended");
        resetButton.Click += (s, e) =>
        {
            TxAudioConditioning.ResetToRecommended();
            PollTxCleanup();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.cleanup.reset_to_recommended"),
                VerbosityLevel.Terse, interrupt: true);
        };
        AddToSection(TxAudioContent, resetButton);
    }

    /// <summary>Toggle handler for the two cleanup switches — same earcon and
    /// speech shape as the radio toggles, but these live on the PC.</summary>
    private void SetTxCleanupToggle(string label, bool isOn)
    {
        if (_polling) return;
        // The chain lives on the rig; with no radio connected the switch
        // cannot take effect, and announcing "on" while the poll quietly
        // unchecks it again would be a lie.
        if (TxAudioConditioning.Conditioner == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.cleanup.no_radio_yet"),
                VerbosityLevel.Terse, interrupt: true);
            PollTxCleanup();
            return;
        }
        if (label.StartsWith("Noise r", StringComparison.OrdinalIgnoreCase))
            TxAudioConditioning.NrEnabled = isOn;
        else
            TxAudioConditioning.GateEnabled = isOn;
        if (isOn) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();

        // DELETED: CheckBox ToggleState is announced by the screen reader on
        // the focused control. The earcon above already marks the change for
        // anyone not listening to speech.
        PollTxCleanup();
    }

    /// <summary>
    /// Keep the cleanup controls synced with the live chain and refresh the
    /// status line. Runs inside PollTxAudio's _polling guard and from the
    /// toggle handlers directly.
    /// </summary>
    private void PollTxCleanup()
    {
        bool wasPolling = _polling;
        _polling = true;
        try
        {
            bool nrOn = TxAudioConditioning.NrEnabled;
            bool gateOn = TxAudioConditioning.GateEnabled;

            if (_txNrCheck != null) _txNrCheck.IsChecked = nrOn;
            if (_txNrStrengthControl != null)
            {
                _txNrStrengthControl.Visibility = nrOn ? Visibility.Visible : Visibility.Collapsed;
                if (nrOn) _txNrStrengthControl.Value =
                    (int)Math.Round(TxAudioConditioning.NrStrength * 100f);
            }

            if (_txGateCheck != null) _txGateCheck.IsChecked = gateOn;
            var gate = TxAudioConditioning.Conditioner?.Gate;
            var gateVis = gateOn ? Visibility.Visible : Visibility.Collapsed;
            if (_txGateMarginControl != null)
            {
                _txGateMarginControl.Visibility = gateVis;
                if (gateOn) _txGateMarginControl.Value =
                    (int)Math.Round(TxAudioConditioning.ThresholdMarginDb);
            }
            if (_txGateAttackControl != null)
            {
                _txGateAttackControl.Visibility = gateVis;
                if (gateOn && gate != null) _txGateAttackControl.Value = (int)Math.Round(gate.AttackMs);
            }
            if (_txGateHoldControl != null)
            {
                _txGateHoldControl.Visibility = gateVis;
                if (gateOn && gate != null) _txGateHoldControl.Value = (int)Math.Round(gate.HoldMs);
            }
            if (_txGateReleaseControl != null)
            {
                _txGateReleaseControl.Visibility = gateVis;
                if (gateOn && gate != null) _txGateReleaseControl.Value = (int)Math.Round(gate.ReleaseMs);
            }
            if (_txGateRangeControl != null)
            {
                _txGateRangeControl.Visibility = gateVis;
                if (gateOn && gate != null) _txGateRangeControl.Value = (int)Math.Round(gate.RangeDb);
            }

            // The chain is the truth for the monitor mode — a selection made
            // with no radio connected never took effect, so the control must
            // not pretend it did.
            if (_txCleanupMonitorControl != null && TxAudioConditioning.Conditioner != null)
            {
                int mode = (int)TxAudioConditioning.MonitorMode;
                _txCleanupMonitorControl.SelectedIndex = mode;
                if (_txCleanupMonitorVolumeControl != null)
                    _txCleanupMonitorVolumeControl.Visibility =
                        mode == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            if (_txCleanupStatusBox != null)
                _txCleanupStatusBox.Text = BuildTxCleanupStatus(nrOn, gateOn, gate);
        }
        finally
        {
            _polling = wasPolling;
        }
    }

    /// <summary>
    /// The status sentence: what is running, and where the gate threshold
    /// actually came from — the measured room floor when there is one, the
    /// deliberately inert default when there is not. Plain words first,
    /// figures after, per the level-verdict rule.
    /// </summary>
    private string BuildTxCleanupStatus(bool nrOn, bool gateOn, JJPortaudio.TxNoiseGate? gate)
    {
        if (!nrOn && !gateOn) return "Cleanup: off";
        if (_rig == null) return "Cleanup: on, waiting for a radio connection.";

        var parts = new System.Text.StringBuilder();
        if (nrOn) parts.Append("Noise reduction on. ");
        if (gateOn && gate != null)
        {
            var profile = _rig.TxLoudnessProfile;
            bool derived = profile.IsValid
                && profile.NoiseFloorLufs > JJPortaudio.LufsMeter.Floor;
            if (derived)
            {
                parts.Append($"Gate set from your room: opens {TxAudioConditioning.ThresholdMarginDb:F0} dB "
                    + $"above the measured floor of {profile.NoiseFloorLufs:F0} "
                    + $"(threshold {gate.ThresholdDb:F0}). ");
            }
            else
            {
                parts.Append("Gate waiting to learn your room — transmit a few seconds "
                    + "of normal speech and it will set itself. ");
            }
            parts.Append(gate.IsOpen ? "Gate is open." : "Gate is closed.");
        }
        return "Cleanup: " + parts.ToString().TrimEnd();
    }

    #endregion
}
