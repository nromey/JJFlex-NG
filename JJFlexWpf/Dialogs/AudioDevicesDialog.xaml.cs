using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using JJPortaudio;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// One surface for every sound device JJ Flex uses: the two PortAudio
    /// devices that carry radio audio to and from this computer, and the NAudio
    /// devices that carry JJ Flex's own alerts, CW notifications, and meter
    /// tones. Since 2026-08-12 it also proves the microphone works, without
    /// keying a transmitter to find out.
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
    /// <para>
    /// Mic Track, 2026-08-12, two changes. First, the lists show one row per
    /// physical device instead of one per host API — a USB interface used to
    /// arrive three times over and picking the wrong copy is how an operator
    /// ends up on a dead endpoint. Second, the Microphone Check: pick an input,
    /// start it, talk, hear a verdict. The question "is my microphone working"
    /// no longer requires a transmitter to answer.
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

        // What each list is actually showing, row for row. Not the same as
        // Devices.InputDevices / OutputDevices: those are every endpoint (the
        // engine's view, and what saved selections resolve against), while
        // these are the folded picker view plus, when it applies, a row for a
        // saved device that is not plugged in.
        private List<Devices.DeviceInfo> _inputRows = new();
        private List<Devices.DeviceInfo> _outputRows = new();

        private List<(int deviceNumber, string name)> _naudioDevices = new();

        // True while a list is being repopulated, so SelectionChanged doesn't
        // narrate a selection the user did not make.
        private bool _loading;

        // ── Microphone Check ──

        private MicProbe? _probe;
        private readonly DispatcherTimer _micTimer;
        private string _micReadingText = "";

        // The privacy verdict for the check currently running. Read once when
        // it is first needed rather than on every 2 Hz tick — the answer cannot
        // change without the operator leaving this dialog, and a registry sweep
        // twice a second to re-learn something we already know is waste.
        private bool _privacyChecked;
        private bool _privacyBlocked;
        private string _privacyExplanation = "";

        /// <summary>
        /// Below this peak, in dBFS, what we are hearing is the interface's own
        /// electronics rather than a room. Chosen from a bench measurement, not
        /// from a table: an Audient interface with nothing plugged in read a
        /// steady -105 dBFS across a four-second check. A live microphone in a
        /// quiet room runs tens of decibels above that, so there is a wide gap
        /// to sit in and -75 sits in it.
        /// </summary>
        private const float NoiseFloorDb = -75f;

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

            // 2 Hz, matching the Audio Workshop's meter cadence: fast enough to
            // follow a voice, slow enough that the value is readable.
            _micTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _micTimer.Tick += (s, e) => UpdateMicCheckReading();

            AdvancedDevicesCheck.IsChecked = Devices.ShowAdvancedDevices;

            // A capture stream must never outlive the window that owns it.
            // Closed fires on every exit — OK, Cancel, Escape, the title bar
            // close, and an owner-window teardown — so this is the one place
            // that has to be right, and every other path just calls the same
            // stop.
            Closed += (s, e) => StopMicCheck(speak: false, reason: "");

            LoadNAudioDevices();
            ReloadPortAudioDevices(announce: false);
            SetMicReading("Microphone check: not running. Choose a microphone above, then start the check.");
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
            // The rows about to be replaced are the rows a running check is
            // holding. Rebuilding the list under a live stream would leave the
            // check pointed at an object no longer on screen.
            StopMicCheck(speak: false, reason: "");

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

                _outputRows = PopulateDeviceList(RadioOutputList, Devices.PickerOutputDevices,
                    _devices.OutputDevice, RadioOutputNote, "radio receive audio");
                _inputRows = PopulateDeviceList(RadioInputList, Devices.PickerInputDevices,
                    _devices.InputDevice, RadioInputNote, "microphone");

                // Say what the app decides about channel counts, and only when
                // it is actually deciding something for a device in THESE
                // lists. Multi-channel devices are opened as stereo; mono
                // devices are shown but cannot carry radio audio yet. A note
                // about neither is noise.
                var shown = _inputRows.Concat(_outputRows).Where(d => !d.IsMissingSaved).ToList();
                bool anyMono = shown.Any(d => !d.UsableForRadioAudio);
                bool anyMultiChannel = shown.Any(d => d.UsableForRadioAudio
                    && d.NativeChannels > Devices.StreamChannels);
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
                    ? $"Device list refreshed. {CountReal(_outputRows)} output and {CountReal(_inputRows)} input devices."
                    : _statusMessage;
                ScreenReaderOutput.Speak(msg, VerbosityLevel.Terse, true);
            }
        }

        private static int CountReal(List<Devices.DeviceInfo> rows) => rows.Count(d => !d.IsMissingSaved);

        /// <summary>
        /// Fill one list and select what is saved, returning the rows the list
        /// is now showing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two rules meet here. A saved device that is no longer present gets
        /// its own flagged row at the top rather than quietly snapping the
        /// selection to whatever sits at index zero — silent remapping is how
        /// an operator ends up transmitting from the wrong microphone without
        /// ever being told, and a device that simply vanishes from the list
        /// leaves them with no way to tell "unplugged" from "never chose one".
        /// </para>
        /// <para>
        /// And a saved device that IS present resolves through
        /// <see cref="Devices.FindPickerRow"/>, so a selection saved against
        /// one host API lands on the folded row that now stands for the same
        /// hardware instead of being reported missing.
        /// </para>
        /// </remarks>
        private List<Devices.DeviceInfo> PopulateDeviceList(
            ListBox list,
            IReadOnlyList<Devices.DeviceInfo> pickerRows,
            Devices.Device? saved,
            TextBlock note,
            string role)
        {
            var rows = new List<Devices.DeviceInfo>();

            bool savedNamed = saved != null && !string.IsNullOrEmpty(saved.Name);
            bool savedMissing = savedNamed && Devices.FindLive(saved) == null;
            if (savedMissing)
            {
                // First, because "the thing you chose is not plugged in" is the
                // most important sentence on the page for the person it applies
                // to, and a list is read from the top.
                var missingRow = Devices.MissingSavedRow(saved!);
                if (missingRow != null) rows.Add(missingRow);
            }
            rows.AddRange(pickerRows);

            list.Items.Clear();
            foreach (var d in rows)
                list.Items.Add(d.Display);

            if (rows.Count == 0)
            {
                // No dead controls in tab order: an empty list is not something
                // you can arrow through, so say why it is empty and disable it.
                list.IsEnabled = false;
                SetStatusLine(note, $"No usable {role} device was found on this computer.");
                return rows;
            }

            list.IsEnabled = true;

            var match = savedMissing ? null : Devices.FindPickerRow(saved);
            if (match != null)
            {
                int idx = IndexOf(rows, match);
                list.SelectedIndex = idx >= 0 ? idx : 0;
                // A multi-channel device is a decision the app makes on the
                // operator's behalf (it opens the first two channels as
                // stereo), so the note says so rather than leaving them to
                // wonder what four channels means for their audio.
                string channelPart = match.NativeChannels > Devices.StreamChannels
                    ? $" It reports {match.NativeChannels} channels; JJ Flex uses it in stereo."
                    : "";
                SetStatusLine(note, $"Currently using {match.Display}.{channelPart}");
                return rows;
            }

            // The pre-selection OK would commit must be a device the engine
            // can open — never a mono row, whose save would be refused, and
            // never the not-connected row, which cannot carry audio at all.
            int fallbackIdx = FirstUsableIndex(rows);

            if (savedMissing)
            {
                // Saved but gone. Pre-select a usable device so OK does the
                // right thing, and say plainly what happened. The saved device
                // is still in the list, first, so it can be chosen deliberately
                // by someone who would rather wait for it than switch.
                if (fallbackIdx < 0)
                {
                    list.SelectedIndex = 0;
                    SetStatusLine(note, $"Saved device not connected: {saved!.Name}. "
                              + $"No usable {role} device is available right now.");
                    return rows;
                }
                list.SelectedIndex = fallbackIdx;
                SetStatusLine(note, $"Saved device not connected: {saved!.Name}. It is still first in the list. "
                          + $"{rows[fallbackIdx].Display} will be used unless you choose another.");
                return rows;
            }

            if (fallbackIdx < 0)
            {
                list.SelectedIndex = 0;
                SetStatusLine(note, $"No usable {role} device was found. The devices listed are mono, "
                          + "and radio audio needs two channels.");
                return rows;
            }
            list.SelectedIndex = fallbackIdx;
            SetStatusLine(note, $"No {role} device chosen yet. {rows[fallbackIdx].Display} will be used unless you choose another.");
            return rows;
        }

        private static int FirstUsableIndex(IReadOnlyList<Devices.DeviceInfo> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsMissingSaved) continue;
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

            int outCount = CountReal(_outputRows);
            int inCount = CountReal(_inputRows);
            bool haveOut = outCount > 0;
            bool haveIn = inCount > 0;

            if (haveOut && haveIn)
            {
                SetStatusLine(StatusText, $"{outCount} output and {inCount} input devices found.");
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
            SpeakSelection(RadioOutputList, _outputRows, "Radio receive audio");
        }

        private void RadioInputList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // A running check belongs to the device it was started on. Moving
            // the selection makes the reading a lie, so the stream closes.
            StopMicCheck(speak: true, reason: "Microphone check stopped — you chose a different microphone.");
            SpeakSelection(RadioInputList, _inputRows, "Microphone");
        }

        // WPF ListBox already narrates the item under the cursor as you arrow,
        // so this stays Chatty rather than interrupting — it adds which list you
        // are in, which is the piece the old form never told you.
        private void SpeakSelection(ListBox list, List<Devices.DeviceInfo> rows, string role)
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= rows.Count) return;
            ScreenReaderOutput.Speak($"{role}: {rows[idx].Display}", VerbosityLevel.Chatty);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadPortAudioDevices(announce: true);
        }

        /// <summary>
        /// Unfold the lists to every endpoint, or fold them back. Session-only:
        /// the advanced view is a diagnostic, and a diagnostic that persists
        /// silently across restarts is how someone ends up living in it without
        /// remembering they turned it on.
        /// </summary>
        private void AdvancedDevicesCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool on = AdvancedDevicesCheck.IsChecked == true;
            if (Devices.ShowAdvancedDevices == on) return;

            Devices.ShowAdvancedDevices = on;
            ReloadPortAudioDevices(announce: false);
            ScreenReaderOutput.Speak(
                on
                    ? $"Showing every sound endpoint. {CountReal(_outputRows)} output and {CountReal(_inputRows)} input entries, "
                      + "including kernel pins. Most of these are the same hardware seen more than once."
                    : $"Showing one entry per device. {CountReal(_outputRows)} output and {CountReal(_inputRows)} input devices.",
                VerbosityLevel.Terse, true);
        }

        // ------------------------------------------------- microphone check

        private Devices.DeviceInfo? SelectedInputRow()
        {
            int idx = RadioInputList.SelectedIndex;
            if (idx < 0 || idx >= _inputRows.Count) return null;
            return _inputRows[idx];
        }

        private void MicCheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_probe != null)
            {
                StopMicCheck(speak: true, reason: "");
                return;
            }
            StartMicCheck();
        }

        /// <summary>
        /// Open the selected microphone and start reporting what it hears.
        /// </summary>
        /// <remarks>
        /// The Windows privacy check runs first, but it does NOT gate the
        /// attempt. A registry read is evidence about a switch, not evidence
        /// about a microphone, and refusing to try would turn a wrong reading of
        /// that switch into a microphone that cannot be tested at all. So the
        /// block is announced, the way out is offered, and the device is opened
        /// anyway — if audio arrives, the audio wins the argument.
        /// </remarks>
        private void StartMicCheck()
        {
            var row = SelectedInputRow();
            if (row == null)
            {
                Announce("Choose a microphone in the list above first.", VerbosityLevel.Critical);
                RadioInputList.Focus();
                return;
            }

            if (row.IsMissingSaved)
            {
                Announce($"{row.Name} is not connected, so there is nothing to check. "
                    + "Plug it back in and choose Refresh device list, or pick a different microphone.",
                    VerbosityLevel.Critical);
                RadioInputList.Focus();
                return;
            }

            CheckPrivacyOnce(force: true);
            ShowPrivacyOffer(_privacyBlocked);

            _probe = new MicProbe();
            MicProbe.StartOutcome outcome = _probe.Start(row, out string failure);

            if (outcome != MicProbe.StartOutcome.Started)
            {
                _probe.Dispose();
                _probe = null;

                // A privacy block is the better explanation when we have one:
                // "Unanticipated host error" tells an operator nothing, and the
                // switch that caused it tells them everything.
                string message = _privacyBlocked
                    ? _privacyExplanation + " " + failure
                    : failure;
                SetMicReading("Microphone check could not start. " + message);
                Announce("Microphone check could not start. " + message, VerbosityLevel.Critical);
                if (_privacyBlocked) MicPrivacyButton.Focus();
                return;
            }

            MicCheckButton.Content = "Stop _microphone check";
            AutomationProperties.SetName(MicCheckButton, "Stop microphone check");
            SetMicReading("Microphone check running. Listening.");
            _micTimer.Start();

            Announce(_privacyBlocked
                ? "Microphone check started, but " + _privacyExplanation
                : $"Microphone check started on {row.Name}. Talk normally.",
                _privacyBlocked ? VerbosityLevel.Critical : VerbosityLevel.Terse);
        }

        /// <summary>
        /// Read the Windows privacy switches, at most once per check.
        /// </summary>
        private void CheckPrivacyOnce(bool force)
        {
            if (_privacyChecked && !force) return;
            _privacyChecked = true;
            var access = MicrophonePrivacy.Check(out _privacyExplanation);
            _privacyBlocked = MicrophonePrivacy.IsBlocked(access);
        }

        /// <summary>
        /// Stop the check and close the device. Idempotent, and called from
        /// every path that could otherwise leave a stream open: the button,
        /// changing the microphone, Refresh, OK, Cancel, and the Closed handler
        /// that catches Escape and the title-bar close.
        /// </summary>
        private void StopMicCheck(bool speak, string reason)
        {
            _micTimer.Stop();
            _privacyChecked = false;

            var probe = _probe;
            _probe = null;
            if (probe == null) return;

            MicProbe.Reading final = probe.Read();
            probe.Stop();
            probe.Dispose();

            if (MicCheckButton != null)
            {
                MicCheckButton.Content = "Start _microphone check";
                AutomationProperties.SetName(MicCheckButton, "Start microphone check");
            }

            string summary;
            if (!final.AnySound)
            {
                summary = "Microphone check stopped. Nothing was heard at all.";
            }
            else if (final.HoldPeakDb <= NoiseFloorDb)
            {
                summary = $"Microphone check stopped. Nothing but the electrical noise floor was "
                    + $"heard, peak {final.HoldPeakDb:F0} dBFS.";
            }
            else
            {
                summary = $"Microphone check stopped. Loudest sound heard: "
                    + $"{MicAudioReport.Verdict(final.HoldPeakDb)}, "
                    + $"peak {final.HoldPeakDb:F0} dBFS.";
            }
            if (!string.IsNullOrEmpty(reason)) summary = reason + " " + summary;

            SetMicReading(summary);
            if (speak) Announce(summary);
        }

        /// <summary>
        /// Refresh the read-only reading. Text only — the accessible name was
        /// set once in XAML and there is deliberately no live region, so a value
        /// moving twice a second never floods a screen reader; the operator's
        /// review command reads the fresh text on demand. Same idiom as the
        /// Audio Workshop's mic reading, on purpose.
        /// </summary>
        private void UpdateMicCheckReading()
        {
            var probe = _probe;
            if (probe == null) { _micTimer.Stop(); return; }

            MicProbe.Reading r = probe.Read();

            if (r.Faulted)
            {
                string fault = r.FaultMessage;
                StopMicCheck(speak: false, reason: "");
                SetMicReading("Microphone check stopped. " + fault);
                Announce("Microphone check stopped. " + fault, VerbosityLevel.Critical);
                return;
            }

            string text;
            if (!r.AnySound)
            {
                // Give the device a moment before passing judgement — some
                // interfaces hand over their first buffers late.
                if (r.Seconds < 1.0)
                {
                    text = "Microphone check running. Listening.";
                }
                else
                {
                    CheckPrivacyOnce(force: false);
                    if (_privacyBlocked)
                    {
                        ShowPrivacyOffer(true);
                        text = "No sound at all. " + _privacyExplanation;
                    }
                    else
                    {
                        // Not "quiet" — exactly zero, every sample. A real
                        // microphone always has a noise floor, so this is
                        // Windows handing us silence rather than a quiet room,
                        // and saying so points at the right place to look.
                        text = "No sound at all — every sample is digital silence. The device is open, "
                            + "but nothing is coming through it. Check that the microphone is not muted "
                            + "in Windows, and that anything with its own mute button is unmuted.";
                    }
                }
            }
            else if (r.RecentPeakDb <= MicProbe.SilenceDb)
            {
                text = $"Mic audio: quiet right now. Loudest so far: "
                    + $"{MicAudioReport.Verdict(r.HoldPeakDb)}, {r.HoldPeakDb:F0} dBFS.";
            }
            else
            {
                // Same vocabulary the Audio Workshop and the Home fields use,
                // reading the same kind of number, so one level never gets two
                // different verdicts depending on where you asked.
                text = $"Mic audio now: {MicAudioReport.Verdict(r.RecentPeakDb)}, "
                    + $"peak {r.RecentPeakDb:F0} dBFS. Loudest so far {r.HoldPeakDb:F0} dBFS.";

                // A fact, not a second verdict. Measured on the bench: an audio
                // interface with nothing plugged into it reads about -105 dBFS
                // — its own electrical noise floor, real non-zero samples, so
                // the digital-silence test above does not fire and the verdict
                // says "turn it up". True, and not the useful sentence. A live
                // microphone in a quiet room sits far above this, so a peak
                // down here means nothing is arriving at the interface at all.
                if (r.HoldPeakDb <= NoiseFloorDb)
                {
                    text += " That is only the electrical noise floor — no sound is reaching the "
                        + "microphone. Check that it is plugged in, and that any gain knob on the "
                        + "interface is turned up.";
                }
            }

            SetMicReading(text);
        }

        private void SetMicReading(string text)
        {
            if (MicCheckReading == null) return;
            // Assign only on change so an unchanged reading does not reset the
            // review cursor twice a second.
            if (_micReadingText == text) return;
            _micReadingText = text;
            MicCheckReading.Text = text;
        }

        /// <summary>
        /// Show or hide the way out of a privacy block. Never shown when
        /// nothing is blocked — an always-present "fix your privacy settings"
        /// button is a nag, and a nag teaches people to ignore it on the day it
        /// matters.
        /// </summary>
        private void ShowPrivacyOffer(bool blocked)
        {
            if (MicPrivacyButton == null) return;
            MicPrivacyButton.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MicPrivacyButton_Click(object sender, RoutedEventArgs e)
        {
            if (MicrophonePrivacy.OpenSettings(out string failure))
            {
                Announce("Windows microphone privacy settings opened. Turn on microphone access, "
                    + "then come back here and start the check again.", VerbosityLevel.Terse);
                return;
            }
            Announce(failure, VerbosityLevel.Critical);
        }

        private static void Announce(string text, VerbosityLevel level = VerbosityLevel.Terse) =>
            ScreenReaderOutput.Speak(text, level, true);

        // -------------------------------------------------------------- save

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var saved = new List<string>();

            if (_devices != null)
            {
                // Refuse — out loud, with the dialog still open — a selection
                // the audio engine cannot open. Saving a mono device would
                // produce a configuration that fails at connect time on a
                // background thread, which is a silent dead microphone.
                if (!ConfirmSelectionUsable(RadioOutputList, _outputRows, "radio audio output")
                    || !ConfirmSelectionUsable(RadioInputList, _inputRows, "microphone"))
                {
                    return;
                }

                StopMicCheck(speak: false, reason: "");

                saved.AddRange(CommitRadioDevice(
                    RadioOutputList, _outputRows, Devices.DeviceTypes.output, "Radio audio output"));
                saved.AddRange(CommitRadioDevice(
                    RadioInputList, _inputRows, Devices.DeviceTypes.input, "Microphone"));

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
        /// <remarks>
        /// The not-connected row passes. Keeping a saved device you intend to
        /// plug back in is a legitimate answer, and the only honest alternative
        /// would be to force a change nobody asked for. What it must not do is
        /// pass silently — see <see cref="CommitRadioDevice"/>.
        /// </remarks>
        private bool ConfirmSelectionUsable(
            ListBox list,
            List<Devices.DeviceInfo> rows,
            string role)
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= rows.Count) return true; // nothing selected, nothing to commit
            if (rows[idx].IsMissingSaved) return true;
            if (rows[idx].UsableForRadioAudio) return true;

            ScreenReaderOutput.Speak(
                $"{rows[idx].Name} is a mono device. Radio audio needs two channels, "
                + $"so JJ Flex cannot use it yet. Choose a different {role}.",
                VerbosityLevel.Critical, true);
            list.Focus();
            return false;
        }

        /// <summary>
        /// Write the chosen device, and say what was written.
        /// </summary>
        /// <remarks>
        /// Two cases deliberately write nothing. The not-connected row leaves
        /// the saved entry exactly as it is, because that IS the saved entry.
        /// And a row that already represents the saved device leaves it alone
        /// too: once the picker folds a device's endpoints into one row, the
        /// row shown is the preferred endpoint, which is not necessarily the
        /// one saved — rewriting it on an OK nobody meant as a change would
        /// silently move a working configuration onto a different host API.
        /// A configuration that works keeps working until someone chooses
        /// otherwise.
        /// </remarks>
        private IEnumerable<string> CommitRadioDevice(
            ListBox list,
            List<Devices.DeviceInfo> rows,
            Devices.DeviceTypes type,
            string role)
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= rows.Count) yield break;

            var chosen = rows[idx];

            if (chosen.IsMissingSaved)
            {
                yield return $"{role}: {chosen.Name}, still saved but not connected";
                yield break;
            }

            var current = (type == Devices.DeviceTypes.input)
                ? _devices!.InputDevice : _devices!.OutputDevice;
            if (Devices.SameDevice(current, chosen))
            {
                yield return $"{role}: {chosen.Name}, unchanged";
                yield break;
            }

            _devices!.SetConfiguredDevice(type, chosen);
            yield return $"{role}: {chosen.Name}";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            StopMicCheck(speak: false, reason: "");
            DialogResult = false;
            Close();
        }

        // -------------------------------------------------------- entry points

        /// <summary>
        /// Show the picker and report whether radio audio is configured
        /// afterwards. Used by the PC-audio rescue path, where the answer
        /// decides whether PC audio can actually start.
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
