using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using JJTrace;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Audio: the radio's own outputs, whether radio audio comes
    /// through this computer, and which sound devices carry it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QB Track B, 2026-08-07. The driver was a real evening: Noel plugged
    /// headphones into a FLEX-8600, heard nothing, and had no way from the app
    /// to see or set the radio's output levels. On a non-M radio there is no
    /// front panel, so software is not one volume knob among several — it is
    /// the only one there is. Before this, JJ Flex could nudge those levels
    /// from a menu but could never show you where they stood.
    /// </para>
    /// <para>
    /// Two things on this surface apply live rather than on OK: the output
    /// levels and the mutes. That is deliberate and it is not the same choice
    /// the rest of the dialog makes. Audio feedback is instantaneous — you turn
    /// the level up to find out whether it is now right, and a value that only
    /// takes effect after an OK-and-reopen round trip cannot be found by ear.
    /// The rest of the tab (volumes, devices, CW parameters) still commits on
    /// OK, because those are preferences rather than a knob you are turning.
    /// </para>
    /// </remarks>
    public partial class SettingsDialog
    {
        // Matches the keyboard command step (KeyCommands HeadphonesUp/Down and
        // LineoutUp/Down move by 5). The Audio MENU moves by 10, which is a
        // pre-existing inconsistency; matching the keys is the better anchor
        // because that is the surface an operator uses while listening.
        private const int RadioOutputStep = 5;

        private bool _suppressRadioOutputEvents;

        /// <summary>
        /// Path to audioDevices.xml, handed down from globals via MainWindow.
        /// Null when it could not be resolved — the Audio Devices button says so
        /// rather than opening a picker that cannot save.
        /// </summary>
        public string? AudioDevicesFile { get; set; }

        private void InitializeAudioTab()
        {
            HeadphoneLevelControl.Setup("Headphone level", 0, 100, RadioOutputStep);
            LineOutLevelControl.Setup("Line out level", 0, 100, RadioOutputStep);

            HeadphoneLevelControl.ValueChanged += HeadphoneLevel_ValueChanged;
            LineOutLevelControl.ValueChanged += LineOutLevel_ValueChanged;

            // PC output volume (Audio Arc Track A) — app-level, works with or
            // without a radio, applies live, persisted on OK through
            // CaptureFromEngine like the rest of the audio config.
            PcOutputVolumeControl.Setup("PC output volume",
                FlexBase.PcOutputVolumeDbMin, FlexBase.PcOutputVolumeDbMax, 1,
                FlexBase.PcOutputVolumeDbSetting, unit: "dB");
            PcOutputVolumeControl.ValueChanged += PcOutputVolume_ValueChanged;
        }

        private void PcOutputVolume_ValueChanged(object? sender, int value)
        {
            if (_suppressRadioOutputEvents) return;
            var rig = _rig;
            if (rig != null)
                rig.PcOutputVolumeDb = value; // applies live to the running stream
            else
                FlexBase.PcOutputVolumeDbSetting = value; // takes effect at next connect
            Tracing.TraceLine("Settings: PcOutputVolumeDb set to " + value, TraceLevel.Info);
        }

        /// <summary>
        /// Pull the radio's current output state into the tab. Called from the
        /// Rig setter and after anything on this surface changes it.
        /// </summary>
        private void RefreshAudioTabFromRig()
        {
            // The Rig setter can fire before InitializeComponent has built these.
            if (RadioOutputsPanel == null) return;

            var rig = _rig;
            bool connected = rig != null && rig.IsConnected;

            RadioOutputsPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

            _suppressRadioOutputEvents = true;
            try
            {
                if (connected)
                {
                    HeadphoneLevelControl.SuppressEvents = true;
                    LineOutLevelControl.SuppressEvents = true;
                    HeadphoneLevelControl.Value = rig!.HeadphoneGain;
                    LineOutLevelControl.Value = rig.LineoutGain;
                    HeadphoneLevelControl.SuppressEvents = false;
                    LineOutLevelControl.SuppressEvents = false;

                    HeadphoneMuteCheck.IsChecked = rig.HeadphoneMute;
                    LineOutMuteCheck.IsChecked = rig.LineoutMute;
                    FrontSpeakerMuteCheck.IsChecked = rig.FrontSpeakerMute;
                }

                PcAudioCheck.IsEnabled = connected;
                PcAudioCheck.IsChecked = connected && rig!.PCAudio;

                // App-level, so refreshed regardless of connection — another
                // surface (menu, leader volume mode, Home expander) may have
                // moved it while this dialog was open.
                PcOutputVolumeControl.SuppressEvents = true;
                PcOutputVolumeControl.Value = FlexBase.PcOutputVolumeDbSetting;
                PcOutputVolumeControl.SuppressEvents = false;
            }
            finally
            {
                _suppressRadioOutputEvents = false;
            }

            RefreshRadioAudioAdvisory();
            RefreshPcAudioStatus();
        }

        /// <summary>
        /// Show whichever rung of the silent-radio ladder is currently true, or
        /// a plain statement of the levels when nothing is wrong.
        /// </summary>
        private void RefreshRadioAudioAdvisory()
        {
            if (RadioOutputsAdvisory == null) return;

            var rig = _rig;
            if (rig == null || !rig.IsConnected)
            {
                AudioDevicesDialog.SetStatusLine(RadioOutputsAdvisory,
                    "No radio is connected, so there is nothing to set here yet. Worth knowing: a Flex makes no audio "
                    + "at all until a client connects to it — including at its own headphone jack. A powered-on radio "
                    + "with headphones plugged in is silent by design until you connect.");
                return;
            }

            string? advisory = rig.SilentRadioAdvisory();
            AudioDevicesDialog.SetStatusLine(RadioOutputsAdvisory, advisory
                ?? $"Headphone level {rig.HeadphoneGain}, line out level {rig.LineoutGain}, nothing muted.");
        }

        private void RefreshPcAudioStatus()
        {
            if (PcAudioStatusText == null) return;

            var rig = _rig;
            if (rig == null || !rig.IsConnected)
            {
                AudioDevicesDialog.SetStatusLine(PcAudioStatusText,
                    "Available once a radio is connected. This setting is not remembered between sessions.");
                return;
            }

            if (rig.PCAudio)
            {
                AudioDevicesDialog.SetStatusLine(PcAudioStatusText, rig.RemoteRig
                    ? "On. Radio audio is playing through this computer, which on a remote connection is the only way to hear it."
                    : "On. Radio audio is playing through this computer.");
            }
            else
            {
                AudioDevicesDialog.SetStatusLine(PcAudioStatusText, rig.RemoteRig
                    ? "Off. On a remote connection there is no other way to hear the radio, so it is silent here."
                    : "Off. You will hear the radio at its own headphone, line out, or speaker outputs.");
            }
        }

        // -------------------------------------------------------- live handlers

        private void HeadphoneLevel_ValueChanged(object? sender, int value)
        {
            if (_suppressRadioOutputEvents) return;
            var rig = _rig;
            if (rig == null || !rig.IsConnected)
            {
                ScreenReaderOutput.SpeakNoRadioConnected("set the headphone level");
                return;
            }
            rig.HeadphoneGain = value;
            Tracing.TraceLine("Settings: HeadphoneGain set to " + value, TraceLevel.Info);
            RefreshRadioAudioAdvisory();
        }

        private void LineOutLevel_ValueChanged(object? sender, int value)
        {
            if (_suppressRadioOutputEvents) return;
            var rig = _rig;
            if (rig == null || !rig.IsConnected)
            {
                ScreenReaderOutput.SpeakNoRadioConnected("set the line out level");
                return;
            }
            rig.LineoutGain = value;
            Tracing.TraceLine("Settings: LineoutGain set to " + value, TraceLevel.Info);
            RefreshRadioAudioAdvisory();
        }

        /// <summary>
        /// One handler for all three mute boxes. Each speaks its own new state —
        /// a checkbox that toggles without saying which output it just silenced
        /// is exactly the kind of change you cannot verify by ear, because the
        /// evidence is the absence of a sound.
        /// </summary>
        private void RadioOutputMute_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioOutputEvents) return;
            if (sender is not CheckBox box) return;

            bool wanted = box.IsChecked == true;
            var rig = _rig;

            if (rig == null || !rig.IsConnected)
            {
                // Put the box back where it was — the radio did not change, so
                // the control must not claim otherwise.
                _suppressRadioOutputEvents = true;
                box.IsChecked = !wanted;
                _suppressRadioOutputEvents = false;
                ScreenReaderOutput.SpeakNoRadioConnected("change the radio's outputs");
                return;
            }

            string label;
            if (ReferenceEquals(box, HeadphoneMuteCheck))
            {
                rig.HeadphoneMute = wanted;
                label = "Headphones";
            }
            else if (ReferenceEquals(box, LineOutMuteCheck))
            {
                rig.LineoutMute = wanted;
                label = "Line out";
            }
            else
            {
                rig.FrontSpeakerMute = wanted;
                label = "Front speaker";
            }

            Tracing.TraceLine($"Settings: {label} mute set to {wanted}", TraceLevel.Info);
            ScreenReaderOutput.Speak($"{label} {(wanted ? "muted" : "unmuted")}", VerbosityLevel.Terse, true);
            RefreshRadioAudioAdvisory();
        }

        private void PcAudioCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioOutputEvents) return;

            bool wanted = PcAudioCheck.IsChecked == true;
            var rig = _rig;

            if (rig == null || !rig.IsConnected)
            {
                _suppressRadioOutputEvents = true;
                PcAudioCheck.IsChecked = !wanted;
                _suppressRadioOutputEvents = false;
                ScreenReaderOutput.SpeakNoRadioConnected("turn radio audio through this computer on or off");
                return;
            }

            // Turning it on with nothing to play through is the fresh-install
            // failure this whole track exists to kill. Check before starting,
            // say what is wrong in words, and put the picker one keystroke away
            // instead of describing where to find it.
            if (wanted && !ConfirmDevicesBeforeStartingPcAudio())
            {
                _suppressRadioOutputEvents = true;
                PcAudioCheck.IsChecked = false;
                _suppressRadioOutputEvents = false;
                RefreshPcAudioStatus();
                return;
            }

            rig.PCAudio = wanted;

            // Read the radio back rather than trusting the request: turning PC
            // audio on can fail on a machine with no usable sound device, and
            // the box must not sit there checked while nothing is playing.
            bool actual = rig.PCAudio;
            _suppressRadioOutputEvents = true;
            PcAudioCheck.IsChecked = actual;
            _suppressRadioOutputEvents = false;

            if (actual != wanted)
            {
                // The failure itself was already announced by the audio path.
                ScreenReaderOutput.Speak("Radio audio through this computer is still off.",
                    VerbosityLevel.Critical, true);
            }
            else if (actual)
            {
                ScreenReaderOutput.Speak("Radio audio will now play through this computer.",
                    VerbosityLevel.Terse, true);
            }
            else
            {
                // Say what turning it off actually costs. On a remote connection
                // it costs everything, and that is not obvious from the words
                // "PC audio off".
                ScreenReaderOutput.Speak(
                    rig.RemoteRig
                        ? "Radio audio will no longer play through this computer. On a remote connection there is no other way to hear the radio."
                        : "Radio audio will no longer play through this computer. You will hear the radio at its own outputs.",
                    VerbosityLevel.Critical, true);
            }

            RefreshPcAudioStatus();
            RefreshRadioAudioAdvisory();
        }

        /// <summary>
        /// Make sure PC audio has devices to use before it starts, offering the
        /// picker in words when it does not.
        /// </summary>
        /// <returns>true when PC audio should go ahead.</returns>
        /// <remarks>
        /// Distinguishes three situations that need different words: no sound
        /// hardware at all (nothing to offer, say so and stop), a saved device
        /// that is no longer connected (name it — "your headset is unplugged" is
        /// a different problem from "you never chose one"), and never
        /// configured. Declining the picker leaves PC audio off and says so;
        /// silently proceeding into an audio path that cannot run is the
        /// behaviour being replaced.
        /// </remarks>
        private bool ConfirmDevicesBeforeStartingPcAudio()
        {
            if (string.IsNullOrEmpty(AudioDevicesFile)) return true;

            try
            {
                var devices = new JJPortaudio.Devices(AudioDevicesFile);
                if (!devices.Setup(out _, out string enumMessage))
                {
                    ScreenReaderOutput.Speak(
                        string.IsNullOrEmpty(enumMessage)
                            ? "Radio audio cannot start: this computer's sound devices could not be read."
                            : "Radio audio cannot start. " + enumMessage,
                        VerbosityLevel.Critical, true);
                    return false;
                }

                bool haveIn = devices.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.input) != null;
                bool haveOut = devices.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.output) != null;
                if (haveIn && haveOut) return true;

                devices.IsSavedDeviceMissing(JJPortaudio.Devices.DeviceTypes.input, out string? savedIn);
                devices.IsSavedDeviceMissing(JJPortaudio.Devices.DeviceTypes.output, out string? savedOut);
                string gone = savedOut ?? savedIn ?? "";

                ScreenReaderOutput.Speak(
                    gone.Length > 0
                        ? $"The sound device chosen for radio audio, {gone}, is not connected. Opening Audio Devices."
                        : "Radio audio needs a sound device on this computer and none has been chosen yet. Opening Audio Devices.",
                    VerbosityLevel.Critical, true);

                var picker = new AudioDevicesDialog(AudioDevicesFile, _audioConfig);
                picker.Owner = this;
                bool configured = picker.ShowDialog() == true && picker.RadioAudioConfigured;
                if (configured)
                {
                    SyncDeviceCombosFromConfig();
                    return true;
                }

                ScreenReaderOutput.Speak(
                    "Radio audio through this computer is still off. Choose audio devices to turn it on.",
                    VerbosityLevel.Critical, true);
                return false;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ConfirmDevicesBeforeStartingPcAudio failed: " + ex.Message, TraceLevel.Error);
                // Errors are never suppressed, but they are also not a reason to
                // refuse: the audio path has its own spoken fallback, so let it
                // try rather than blocking on a check that itself broke.
                ScreenReaderOutput.Speak(
                    "Audio devices could not be checked: " + ex.Message, VerbosityLevel.Critical, true);
                return true;
            }
        }

        /// <summary>
        /// Speak the ladder's current answer on demand.
        /// </summary>
        /// <remarks>
        /// The rungs run in the order that actually bites, and the first one is
        /// the one that catches operators coming from a conventional rig: a Flex
        /// produces no audio until a client connects, headphone jack included.
        /// Everything else — muted outputs, levels at the floor, PC audio off
        /// with no local path — comes after, because none of it matters if
        /// nothing is connected.
        /// </remarks>
        private void WhySilentButton_Click(object sender, RoutedEventArgs e)
        {
            var rig = _rig;

            string message;
            if (rig == null || !rig.IsConnected)
            {
                message = "No radio is connected. A Flex makes no audio at all until a client connects to it, "
                        + "including at its own headphone jack. Connect first.";
            }
            else
            {
                message = rig.SilentRadioAdvisory()
                    ?? $"Nothing obvious is wrong. Headphone level {rig.HeadphoneGain}, line out level {rig.LineoutGain}, "
                     + "nothing muted. If the radio is still silent, check the slice volume and that a slice is not muted.";
            }

            RefreshRadioAudioAdvisory();
            ScreenReaderOutput.Speak(message, VerbosityLevel.Critical, true);
        }

        private void AudioDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(AudioDevicesFile))
            {
                // Never a dead button: say why instead of doing nothing.
                ScreenReaderOutput.Speak(
                    "The audio device settings file could not be located, so devices cannot be chosen here.",
                    VerbosityLevel.Critical, true);
                return;
            }

            // The picker's alert/meter selections write into the same config
            // this dialog is editing, so a change made in there is still live
            // when Settings' own OK runs. Persisting is Settings' job, so no
            // persist callback is passed.
            var dlg = new AudioDevicesDialog(AudioDevicesFile, _audioConfig);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
            {
                // Keep the tab's own alert/meter combos honest — the picker may
                // have moved them out from under this dialog.
                SyncDeviceCombosFromConfig();
            }
        }

        /// <summary>
        /// Re-point the Audio tab's alert and meter combos at whatever the
        /// config now says, after the picker has changed it.
        /// </summary>
        private void SyncDeviceCombosFromConfig()
        {
            var devices = EarconPlayer.GetOutputDevices();

            for (int i = 0; i < devices.Count && i < EarconDeviceCombo.Items.Count; i++)
            {
                if (devices[i].deviceNumber == _audioConfig.EarconDeviceNumber)
                {
                    EarconDeviceCombo.SelectedIndex = i;
                    break;
                }
            }

            if (_audioConfig.MeterDeviceNumber == -1)
            {
                MeterDeviceCombo.SelectedIndex = 0;
            }
            else
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].deviceNumber == _audioConfig.MeterDeviceNumber)
                    {
                        MeterDeviceCombo.SelectedIndex = i + 1;
                        break;
                    }
                }
            }
        }
    }
}
