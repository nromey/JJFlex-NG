using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJPortaudio;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// One surface for every sound device JJ Flex uses: the two PortAudio
    /// devices that carry radio audio to and from this computer, and the NAudio
    /// devices that carry JJ Flex's own alerts, CW notifications, and meter
    /// tones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QB Track B, 2026-08-07. Replaces the JJ-era <c>devList</c> WinForms form,
    /// which showed TWO modal dialogs back to back — input then output — with no
    /// announcement that a second one was coming and a list control labelled
    /// "device list" both times. The window title was the only thing that said
    /// which device you were choosing, and on the path that mattered most (first
    /// connect on a fresh machine) the form was raised from a background audio
    /// thread with no owner window, where it could land behind the main window
    /// entirely. Choosing audio devices by ear was, in practice, not possible.
    /// </para>
    /// <para>
    /// Two enumeration stacks meet on this page and there is no getting around
    /// it: radio audio runs on PortAudio, alerts and meters on NAudio, and the
    /// two render different display names for the same physical hardware. The
    /// headings name the domain rather than the plumbing so the difference reads
    /// as "what this sound is" rather than "which library found it".
    /// </para>
    /// </remarks>
    public partial class AudioDevicesDialog : JJFlexDialog
    {
        /// <summary>
        /// Set a status line's text and its accessible name together.
        /// </summary>
        /// <remarks>
        /// A focusable TextBlock reports AutomationProperties.Name, not its
        /// Text, so a status line whose Name was authored once in XAML reads the
        /// same sentence forever no matter what it is displaying. These lines
        /// exist precisely because their content changes; they have to say the
        /// current thing. Empty text becomes a single space — a genuinely blank
        /// line is a hole a screen reader arrows straight past.
        /// </remarks>
        internal static void SetStatusLine(TextBlock block, string text)
        {
            if (block == null) return;
            string value = string.IsNullOrEmpty(text) ? " " : text;
            block.Text = value;
            AutomationProperties.SetName(block, value);
        }

        private readonly string _audioDevicesFile;
        private readonly AudioOutputConfig? _audioConfig;
        private readonly Action? _persistAudioConfig;

        private Devices? _devices;
        private Devices.EnumerationStatus _status = Devices.EnumerationStatus.Ok;
        private string _statusMessage = "";

        private List<(int deviceNumber, string name)> _naudioDevices = new();

        // True while a list is being repopulated, so SelectionChanged doesn't
        // narrate a selection the user did not make.
        private bool _loading;

        /// <summary>
        /// True when both radio-audio devices are configured and present after
        /// this dialog closed. The rescue path uses it to decide whether PC
        /// audio can actually start.
        /// </summary>
        public bool RadioAudioConfigured { get; private set; }

        /// <param name="audioDevicesFile">
        /// Full path to audioDevices.xml. Machine scope — one file per Windows
        /// profile, deliberately not per-radio or per-operator, because a sound
        /// card belongs to the computer.
        /// </param>
        /// <param name="audioConfig">
        /// Alert/meter device config to read and write. Optional; when absent
        /// that section is still shown but reads the live engine values.
        /// </param>
        /// <param name="persistAudioConfig">
        /// Called after OK has written the alert/meter selections into
        /// <paramref name="audioConfig"/>, so the caller can save it wherever it
        /// keeps that file. Optional — Settings does its own saving on its OK.
        /// </param>
        public AudioDevicesDialog(
            string audioDevicesFile,
            AudioOutputConfig? audioConfig = null,
            Action? persistAudioConfig = null)
        {
            _audioDevicesFile = audioDevicesFile ?? "";
            _audioConfig = audioConfig;
            _persistAudioConfig = persistAudioConfig;

            InitializeComponent();

            LoadNAudioDevices();
            ReloadPortAudioDevices(announce: false);
        }

        /// <summary>
        /// Land on the radio output list rather than the first control in tab
        /// order.
        /// </summary>
        /// <remarks>
        /// The button row is declared first so it docks to the bottom, which
        /// means the base class's "first focusable element" is Refresh — a
        /// perfectly good button and completely the wrong place to start. Radio
        /// receive audio is what almost everyone opens this dialog to set, so
        /// that is where focus goes, with its current value already spoken.
        /// </remarks>
        protected override void FocusFirstControl()
        {
            if (RadioOutputList != null && RadioOutputList.IsEnabled)
            {
                RadioOutputList.Focus();
                return;
            }
            base.FocusFirstControl();
        }

        // ---------------------------------------------------------------- load

        private void LoadNAudioDevices()
        {
            _naudioDevices = EarconPlayer.GetOutputDevices();

            int alertNumber = _audioConfig?.EarconDeviceNumber ?? EarconPlayer.GetAlertDeviceNumber();
            int meterNumber = _audioConfig?.MeterDeviceNumber ?? EarconPlayer.GetMeterDeviceNumber();

            AlertDeviceCombo.Items.Clear();
            foreach (var (devNum, name) in _naudioDevices)
            {
                AlertDeviceCombo.Items.Add(name);
                if (devNum == alertNumber)
                    AlertDeviceCombo.SelectedIndex = AlertDeviceCombo.Items.Count - 1;
            }
            if (AlertDeviceCombo.SelectedIndex < 0 && AlertDeviceCombo.Items.Count > 0)
                AlertDeviceCombo.SelectedIndex = 0;

            // "Same as Alerts" first, so the common answer is the reachable one.
            MeterDeviceCombo.Items.Clear();
            MeterDeviceCombo.Items.Add("Same as alerts");
            foreach (var (_, name) in _naudioDevices)
                MeterDeviceCombo.Items.Add(name);

            if (meterNumber == -1)
            {
                MeterDeviceCombo.SelectedIndex = 0;
            }
            else
            {
                int idx = 0;
                for (int i = 0; i < _naudioDevices.Count; i++)
                {
                    if (_naudioDevices[i].deviceNumber == meterNumber) { idx = i + 1; break; }
                }
                MeterDeviceCombo.SelectedIndex = idx;
            }
        }

        /// <summary>
        /// Re-enumerate PortAudio and rebuild both radio-audio lists. Also the
        /// Refresh button's job, because enumeration is a snapshot: a headset
        /// plugged in while this dialog is open is invisible until this runs.
        /// </summary>
        private void ReloadPortAudioDevices(bool announce)
        {
            _loading = true;
            try
            {
                _status = Devices.Enumerate(out _statusMessage);

                _devices = new Devices(_audioDevicesFile);
                // Load the saved selection only — Setup would enumerate again,
                // and a second Pa_Initialize/Pa_Terminate cycle per Refresh is
                // a real cost on a live audio machine for an answer we are
                // already holding.
                _devices.LoadSavedSelection();

                PopulateDeviceList(RadioOutputList, Devices.OutputDevices,
                    _devices.OutputDevice, RadioOutputNote, "radio receive audio");
                PopulateDeviceList(RadioInputList, Devices.InputDevices,
                    _devices.InputDevice, RadioInputNote, "microphone");

                // Say what the app decides about channel counts, and only when
                // it is actually deciding something for a device in THESE
                // lists. Multi-channel devices are opened as stereo; mono
                // devices are shown but cannot carry radio audio yet. A note
                // about neither is noise.
                bool anyMono = Devices.InputDevices.Concat(Devices.OutputDevices)
                    .Any(d => !d.UsableForRadioAudio);
                bool anyMultiChannel = Devices.InputDevices.Concat(Devices.OutputDevices)
                    .Any(d => d.UsableForRadioAudio && d.NativeChannels > Devices.StreamChannels);
                string filterNote = "";
                if (anyMultiChannel)
                    filterNote = "Devices with more than two channels are fine — JJ Flex uses them in stereo.";
                if (anyMono)
                    filterNote += (filterNote.Length > 0 ? " " : "")
                        + "Mono devices are shown so you know JJ Flex can see them, but radio audio needs two channels, so they cannot be chosen yet.";
                SetStatusLine(FilterNoteText, filterNote);
                FilterNoteText.Visibility = filterNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

                UpdateStatusText();
            }
            finally
            {
                _loading = false;
            }

            if (announce)
            {
                string msg = _status == Devices.EnumerationStatus.Ok
                    ? $"Device list refreshed. {Devices.OutputDevices.Count} output and {Devices.InputDevices.Count} input devices."
                    : _statusMessage;
                ScreenReaderOutput.Speak(msg, VerbosityLevel.Terse, true);
            }
        }

        /// <summary>
        /// Fill one list and select what is saved. A saved device that is no
        /// longer present is shown as its own flagged row rather than quietly
        /// snapping the selection to whatever sits at index zero — silent
        /// remapping is how an operator ends up transmitting from the wrong
        /// microphone without ever being told.
        /// </summary>
        private void PopulateDeviceList(
            ListBox list,
            IReadOnlyList<Devices.DeviceInfo> live,
            Devices.Device? saved,
            TextBlock note,
            string role)
        {
            list.Items.Clear();

            foreach (var d in live)
                list.Items.Add(d.Display);

            if (live.Count == 0)
            {
                // No dead controls in tab order: an empty list is not something
                // you can arrow through, so say why it is empty and disable it.
                list.IsEnabled = false;
                SetStatusLine(note, $"No usable {role} device was found on this computer.");
                return;
            }

            list.IsEnabled = true;

            var match = Devices.FindLive(saved);
            if (match != null)
            {
                int idx = IndexOf(live, match);
                list.SelectedIndex = idx >= 0 ? idx : 0;
                // A multi-channel device is a decision the app makes on the
                // operator's behalf (it opens the first two channels as
                // stereo), so the note says so rather than leaving them to
                // wonder what four channels means for their audio.
                string channelPart = match.NativeChannels > Devices.StreamChannels
                    ? $" It reports {match.NativeChannels} channels; JJ Flex uses it in stereo."
                    : "";
                SetStatusLine(note, $"Currently using {match.Display}.{channelPart}");
                return;
            }

            // The pre-selection OK would commit must be a device the engine
            // can open — never a mono row, whose save would be refused.
            int fallbackIdx = FirstUsableIndex(live);

            if (saved != null && !string.IsNullOrEmpty(saved.Name))
            {
                // Saved but gone. Pre-select a usable device so OK does the
                // right thing, and say plainly what happened.
                if (fallbackIdx < 0)
                {
                    list.SelectedIndex = 0;
                    SetStatusLine(note, $"Saved device not connected: {saved.Name}. "
                              + $"No usable {role} device is available right now.");
                    return;
                }
                list.SelectedIndex = fallbackIdx;
                SetStatusLine(note, $"Saved device not connected: {saved.Name}. "
                          + $"{live[fallbackIdx].Display} will be used unless you choose another.");
                return;
            }

            if (fallbackIdx < 0)
            {
                list.SelectedIndex = 0;
                SetStatusLine(note, $"No usable {role} device was found. The devices listed are mono, "
                          + "and radio audio needs two channels.");
                return;
            }
            list.SelectedIndex = fallbackIdx;
            SetStatusLine(note, $"No {role} device chosen yet. {live[fallbackIdx].Display} will be used unless you choose another.");
        }

        private static int FirstUsableIndex(IReadOnlyList<Devices.DeviceInfo> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].UsableForRadioAudio) return i;
            }
            return -1;
        }

        private static int IndexOf(IReadOnlyList<Devices.DeviceInfo> list, Devices.DeviceInfo target)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], target)) return i;
            }
            return -1;
        }

        private void UpdateStatusText()
        {
            if (_status != Devices.EnumerationStatus.Ok)
            {
                SetStatusLine(StatusText, _statusMessage);
                return;
            }

            bool haveOut = Devices.OutputDevices.Count > 0;
            bool haveIn = Devices.InputDevices.Count > 0;

            if (haveOut && haveIn)
            {
                SetStatusLine(StatusText, $"{Devices.OutputDevices.Count} output and {Devices.InputDevices.Count} input devices found.");
            }
            else if (haveOut)
            {
                SetStatusLine(StatusText, "Output devices were found but no usable microphone was. "
                                + "Radio audio will play, but this computer cannot send audio to the radio.");
            }
            else
            {
                SetStatusLine(StatusText, "A microphone was found but no usable playback device was. "
                                + "You will not hear radio audio through this computer.");
            }
        }

        // ------------------------------------------------------------- events

        private void RadioOutputList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            SpeakSelection(RadioOutputList, Devices.OutputDevices, "Radio receive audio");
        }

        private void RadioInputList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            SpeakSelection(RadioInputList, Devices.InputDevices, "Microphone");
        }

        // WPF ListBox already narrates the item under the cursor as you arrow,
        // so this stays Chatty rather than interrupting — it adds which list you
        // are in, which is the piece the old form never told you.
        private void SpeakSelection(ListBox list, IReadOnlyList<Devices.DeviceInfo> live, string role)
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= live.Count) return;
            ScreenReaderOutput.Speak($"{role}: {live[idx].Display}", VerbosityLevel.Chatty);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadPortAudioDevices(announce: true);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var saved = new List<string>();

            if (_devices != null)
            {
                // Refuse — out loud, with the dialog still open — a selection
                // the audio engine cannot open. Saving a mono device would
                // produce a configuration that fails at connect time on a
                // background thread, which is a silent dead microphone.
                if (!ConfirmSelectionUsable(RadioOutputList, Devices.OutputDevices, "radio audio output")
                    || !ConfirmSelectionUsable(RadioInputList, Devices.InputDevices, "microphone"))
                {
                    return;
                }

                saved.AddRange(CommitRadioDevice(
                    RadioOutputList, Devices.OutputDevices, Devices.DeviceTypes.output, "Radio audio output"));
                saved.AddRange(CommitRadioDevice(
                    RadioInputList, Devices.InputDevices, Devices.DeviceTypes.input, "Microphone"));

                RadioAudioConfigured =
                    _devices.GetConfiguredDevice(Devices.DeviceTypes.output) != null
                    && _devices.GetConfiguredDevice(Devices.DeviceTypes.input) != null;
            }

            if (_audioConfig != null)
            {
                int alertIdx = AlertDeviceCombo.SelectedIndex;
                if (alertIdx >= 0 && alertIdx < _naudioDevices.Count)
                    _audioConfig.EarconDeviceNumber = _naudioDevices[alertIdx].deviceNumber;

                int meterIdx = MeterDeviceCombo.SelectedIndex;
                if (meterIdx <= 0)
                {
                    _audioConfig.MeterDeviceNumber = -1;
                }
                else if (meterIdx - 1 < _naudioDevices.Count)
                {
                    _audioConfig.MeterDeviceNumber = _naudioDevices[meterIdx - 1].deviceNumber;
                }

                // Alerts move to the new device immediately — waiting for a
                // restart to find out whether you picked right is not a thing we
                // do to someone who is choosing by ear.
                EarconPlayer.SetAlertDevice(_audioConfig.EarconDeviceNumber);
                EarconPlayer.SetMeterDevice(_audioConfig.MeterDeviceNumber);
                _persistAudioConfig?.Invoke();
            }

            // Every change speaks. Naming the devices back is the confirmation
            // the old form never gave — it saved silently and closed.
            ScreenReaderOutput.Speak(
                saved.Count > 0
                    ? "Audio devices saved. " + string.Join(". ", saved) + "."
                    : "Audio devices saved.",
                VerbosityLevel.Terse, true);

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// True when this list's selection is something the engine can open.
        /// A mono selection speaks the reason, puts focus back on the list,
        /// and keeps the dialog open so the operator can choose again.
        /// </summary>
        private bool ConfirmSelectionUsable(
            ListBox list,
            IReadOnlyList<Devices.DeviceInfo> live,
            string role)
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= live.Count) return true; // nothing selected, nothing to commit
            if (live[idx].UsableForRadioAudio) return true;

            ScreenReaderOutput.Speak(
                $"{live[idx].Name} is a mono device. Radio audio needs two channels, "
                + $"so JJ Flex cannot use it yet. Choose a different {role}.",
                VerbosityLevel.Critical, true);
            list.Focus();
            return false;
        }

        private IEnumerable<string> CommitRadioDevice(
            ListBox list,
            IReadOnlyList<Devices.DeviceInfo> live,
            Devices.DeviceTypes type,
            string role)
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= live.Count) yield break;

            var chosen = live[idx];
            _devices!.SetConfiguredDevice(type, chosen);
            yield return $"{role}: {chosen.Name}";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // -------------------------------------------------------- entry points

        /// <summary>
        /// Show the picker and report whether radio audio is configured
        /// afterwards. Used by the PC-audio rescue path, where the answer
        /// decides whether PC audio can start.
        /// </summary>
        public static bool ShowPicker(
            Window? owner,
            string audioDevicesFile,
            AudioOutputConfig? audioConfig = null,
            Action? persistAudioConfig = null)
        {
            var dlg = new AudioDevicesDialog(audioDevicesFile, audioConfig, persistAudioConfig);
            if (owner != null) dlg.Owner = owner;
            bool ok = dlg.ShowDialog() == true;
            return ok && dlg.RadioAudioConfigured;
        }
    }
}
