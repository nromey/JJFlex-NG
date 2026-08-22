using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using JJPortaudio;
using JJTrace;
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

        // The host APIs the combo is showing, row for row.
        private List<Devices.HostApi> _hostApis = new();

        // The Opus transmit rates the quality combo is showing, row for row.
        private static readonly uint[] TxRates = JJAudioStream.OpusTxRates;

        // Both the audio system and the transmit rate take effect the moment
        // they are chosen — the audio system because the lists below it have to
        // show what it offers, the transmit rate because the note under it has
        // to describe the new value. Cancel discards, so what they were on the
        // way in is remembered here and put back on any exit that is not OK.
        private readonly int _hostApiOnEntry = Devices.SelectedHostApiTypeId;
        private readonly uint _txRateOnEntry = Radios.FlexBase.OpusTxSampleRateSetting;

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

        // ── Windows input level (stage one) ──

        // The Core Audio endpoint volume for the selected microphone, or null
        // when no confident match exists — in which case the control is
        // disabled and the note says why. See WindowsMicLevel for the
        // matching rules and why refusing beats guessing.
        private WindowsMicLevel? _micLevel;

        // True while code is moving the sliders (initial bind, an external
        // change echoing back), so ValueChanged does not write a value we
        // just read straight back to Windows.
        private bool _micLevelUpdatingUi;

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
        /// At or above this peak, in dBFS, the capture has clipped — samples
        /// slammed into full scale. Loudness figures are withheld from that
        /// reading: clipping raises RMS energy, so a loudness measured through
        /// it reads HIGHER than the true program loudness, and reporting a
        /// number the clipping corrupted would be worse than reporting none.
        /// -1 rather than exactly 0 because drivers hand back peaks a hair
        /// under full scale for a signal that already hit the rail.
        /// </summary>
        private const float ClippedDb = -1f;

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
            // stop. The Windows level binding holds a COM notification
            // registration, so it gets the same guarantee.
            Closed += (s, e) =>
            {
                StopMicCheck(speak: false, reason: "");
                DisposeMicLevel();

                // Escape, Cancel, the title-bar close, an owner teardown — every
                // way out that is not OK. Both settings were applied live so the
                // dialog could describe what they do; leaving them applied after
                // Cancel would make Cancel a lie.
                if (DialogResult == true) return;
                Radios.FlexBase.OpusTxSampleRateSetting = _txRateOnEntry;
                if (Devices.SelectedHostApiTypeId != _hostApiOnEntry)
                    Devices.ApplyHostApiSelection(_hostApiOnEntry);
            };

            LoadNAudioDevices();
            LoadTxRates();
            ReloadPortAudioDevices(announce: false);
            SetMicReading("Microphone check: not running. Choose a microphone above, then press "
                + "Alt+M to start it. JJ Flexible listens to that microphone and tells you what "
                + "it hears. Nothing is transmitted and the radio is not involved.");
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
            // Arrived here to check a microphone? Then the microphone is the
            // subject, not radio receive audio: start the check and put focus
            // where the answer will appear. Running it before focus moves means
            // the reading is already live when the operator lands on it.
            if (_startCheckOnOpen)
            {
                _startCheckOnOpen = false;
                if (RadioInputList != null && RadioInputList.Items.Count > 0)
                {
                    if (RadioInputList.SelectedIndex < 0) RadioInputList.SelectedIndex = 0;
                    StartMicCheck();
                    if (MicCheckReading != null && MicCheckReading.Focus()) return;
                }
            }

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
        /// Fill the audio-system combo and select the one in force.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Track E, 2026-08-16. Windows offers the same sound card through
        /// several driver models — MME, DirectSound, WASAPI, and the kernel
        /// pins — so every device enumerated once per model and the lists
        /// folded the duplicates away. Folding had to pick one endpoint to
        /// stand for the rest, which meant the app was choosing a driver model
        /// silently, and it landed on MME often enough to matter. MME
        /// resamples on the way through, so it reports a tidy 48 kHz whatever
        /// the hardware is really running at, and no rate problem could be seen
        /// from inside the app at all.
        /// </para>
        /// <para>
        /// So this control DELETED code rather than adding it: choose the
        /// audio system first and there are no duplicates left to fold.
        /// </para>
        /// <para>
        /// Disabled with the reason in the note when the machine offers only
        /// one — a combo with a single item is a tab stop that cannot do
        /// anything, and the house rule keeps dead controls out of the tab
        /// order.
        /// </para>
        /// </remarks>
        private void LoadHostApis()
        {
            _hostApis = new List<Devices.HostApi>(Devices.HostApis);

            HostApiCombo.Items.Clear();
            foreach (var api in _hostApis) HostApiCombo.Items.Add(api.Display);

            int idx = _hostApis.FindIndex(a => a.TypeId == Devices.SelectedHostApiTypeId);
            if (idx < 0 && _hostApis.Count > 0) idx = 0;
            HostApiCombo.SelectedIndex = idx;

            if (_hostApis.Count == 0)
            {
                HostApiCombo.IsEnabled = false;
                SetStatusLine(HostApiNote, "No audio system was found on this computer, so there "
                    + "are no sound devices to choose from.");
                return;
            }

            if (_hostApis.Count == 1)
            {
                HostApiCombo.IsEnabled = false;
                SetStatusLine(HostApiNote, $"This computer offers only {_hostApis[0].Name}, "
                    + "so there is nothing to choose between.");
                return;
            }

            HostApiCombo.IsEnabled = true;
            SetStatusLine(HostApiNote, HostApiNoteText());
        }

        /// <summary>
        /// The always-current sentence under the audio-system combo: what the
        /// current choice means for the operator's audio, in the honest form.
        /// </summary>
        /// <remarks>
        /// The trade is stated both ways round on purpose. MME is genuinely
        /// the most compatible and it is genuinely the one that hides the
        /// truth; WASAPI is genuinely honest and it genuinely refuses devices
        /// MME would have accepted. An operator whose interface will not open
        /// under WASAPI needs to know MME exists, and an operator whose
        /// transmit audio is mysteriously wrong needs to know MME is why they
        /// cannot see it.
        /// </remarks>
        private string HostApiNoteText()
        {
            int selected = Devices.SelectedHostApiTypeId;
            if (Devices.ShowAdvancedDevices)
            {
                return "Showing every sound endpoint, so this choice is not filtering the lists "
                    + "right now. Each row names its own audio system, and the microphone and the "
                    + "receive audio can be set to different ones. It still applies again when you "
                    + "turn that off.";
            }

            switch (selected)
            {
                case Devices.WasapiTypeId:
                    return "WASAPI is the modern Windows path and the one that tells you the truth: "
                        + "it reports the rate your hardware is really running at, and refuses a "
                        + "device that cannot do what the radio needs instead of quietly converting. "
                        + "If a device you own will not work here, MME is the forgiving one.";
                case Devices.MmeTypeId:
                    return "MME is the most compatible and the most forgiving — it converts sample "
                        + "rates for you, so devices WASAPI refuses will usually work. The cost is "
                        + "that it reports 48 kHz for everything, so you cannot tell from here what "
                        + "rate your hardware is actually running at. Device names are also cut "
                        + "short to 31 characters by Windows under MME.";
                case Devices.DirectSoundTypeId:
                    return "DirectSound sits between MME and WASAPI: it converts sample rates like "
                        + "MME does, without MME's truncated device names. WASAPI is the better "
                        + "default if your devices work under it.";
                case Devices.WdmKsTypeId:
                    return "Kernel streaming talks to the hardware directly. These are raw endpoints, "
                        + "including physical jacks with nothing plugged into them — picking one of "
                        + "those transmits silence and nothing warns you. Use it only if you know "
                        + "which endpoint you want.";
                default:
                    return $"Devices are listed through {Devices.NameOfHostApi(selected)}.";
            }
        }

        /// <summary>
        /// Fill the transmit-quality combo. Rate values come from
        /// <see cref="JJAudioStream.OpusTxRates"/> so the list and the encoder
        /// cannot drift apart.
        /// </summary>
        private void LoadTxRates()
        {
            TxRateCombo.Items.Clear();
            foreach (uint rate in TxRates) TxRateCombo.Items.Add(TxRateLabel(rate));

            uint current = Radios.FlexBase.OpusTxSampleRateSetting;
            int idx = Array.IndexOf(TxRates, current);
            TxRateCombo.SelectedIndex = (idx >= 0) ? idx : 0;
            SetStatusLine(TxRateNote, TxRateNoteText());
        }

        /// <summary>
        /// A transmit rate in words first, figures second. The kilohertz
        /// number is meaningless to most operators and load-bearing to some,
        /// so both are there and the plain word leads.
        /// </summary>
        private static string TxRateLabel(uint rate)
        {
            string quality = rate switch
            {
                48000 => "Full quality",
                24000 => "Reduced",
                16000 => "Low",
                12000 => "Very low",
                _ => "Lowest",
            };
            return $"{quality} — {rate / 1000.0:0.#} kHz"
                + (rate == Radios.FlexBase.OpusTxSampleRateDefault ? " (default)" : "");
        }

        /// <summary>
        /// What the transmit rate setting will and will not do, honestly.
        /// </summary>
        /// <remarks>
        /// The important sentence is that the device gets the last word. The
        /// rate is settled against the hardware before the encoder is built —
        /// that ordering is the fix that stopped an encoder running at a rate
        /// its stream was not — so a request the device refuses is simply not
        /// what happens, and a control that silently does nothing is worse
        /// than no control.
        /// </remarks>
        private string TxRateNoteText()
        {
            uint rate = Radios.FlexBase.OpusTxSampleRateSetting;
            string lead = (rate == Radios.FlexBase.OpusTxSampleRateDefault)
                ? "Full quality is the tested setting; leave it here unless your connection "
                  + "cannot carry it."
                : $"Your microphone will be encoded at {rate / 1000.0:0.#} kHz, which uses less of "
                  + "your connection and sounds duller. Worth trying when transmit audio breaks up "
                  + "on a poor link.";
            return lead + " Your sound card has the last word: if it cannot run at this rate, "
                + "JJ Flexible opens at a rate it can and encodes to match, rather than sending "
                + "something the radio cannot follow. MME converts rates, so the lower settings "
                + "are most likely to take effect there.";
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
                // already holding. This also applies the saved audio system,
                // which rebuilds the picker lists, so it must run before the
                // lists are read below.
                _devices.LoadSavedSelection();

                LoadHostApis();
                PopulateBothLists();
            }
            finally
            {
                _loading = false;
            }

            // Rebind the Windows level control to whatever the input list now
            // selects. SelectionChanged skipped it — _loading was true — and a
            // control left bound to a device from before the refresh would be
            // adjusting something no longer on screen.
            UpdateMicLevelControl();

            if (announce)
            {
                string msg = _status == Devices.EnumerationStatus.Ok
                    ? Lexicon.Get("audio.device.list_refreshed",
                        ("outputs", CountReal(_outputRows)), ("inputs", CountReal(_inputRows)))
                    : _statusMessage;
                ScreenReaderOutput.Speak(msg, VerbosityLevel.Terse, true);
            }
        }

        /// <summary>
        /// Rebuild both device lists from the picker view currently in force,
        /// WITHOUT re-enumerating. What the audio-system selector needs:
        /// every endpoint is already in hand, and only the filter over them
        /// changed. A Pa_Initialize/Pa_Terminate cycle per selector move would
        /// be a real cost on a live audio machine for an answer we hold.
        /// </summary>
        private void RebuildDeviceLists()
        {
            _loading = true;
            try
            {
                PopulateBothLists();
            }
            finally
            {
                _loading = false;
            }
            // SelectionChanged skipped the rebind while _loading was true, and
            // a level control left pointed at a device from the previous view
            // would be adjusting something no longer on screen.
            UpdateMicLevelControl();
        }

        /// <summary>
        /// Fill the two radio-audio lists and the note beneath them. Caller
        /// holds <c>_loading</c>.
        /// </summary>
        private void PopulateBothLists()
        {
            _outputRows = PopulateDeviceList(RadioOutputList, Devices.PickerOutputDevices,
                _devices?.OutputDevice, RadioOutputNote,
                Lexicon.Get("audio.device.role_output_note"));
            _inputRows = PopulateDeviceList(RadioInputList, Devices.PickerInputDevices,
                _devices?.InputDevice, RadioInputNote,
                Lexicon.Get("audio.device.role_input_note"));

            // Say what the app decides about channel counts and rates, and
            // only when it is actually deciding something for a device in
            // THESE lists. A note about none of them is noise.
            var shown = _inputRows.Concat(_outputRows).Where(d => !d.IsMissingSaved).ToList();
            bool anyMono = shown.Any(d => d.IsMono);
            bool anyMultiChannel = shown.Any(d => d.NativeChannels > Devices.StreamChannels);
            bool anyBadRate = shown.Any(d => Devices.DescribeRate(d).Length > 0);
            string filterNote = "";
            if (anyMultiChannel)
                filterNote = "Devices with more than two channels are fine — JJ Flex uses them in stereo.";
            if (anyMono)
                filterNote += (filterNote.Length > 0 ? " " : "")
                    + "Mono devices work too — a mono microphone is sent to the radio on both "
                    + "channels, and a mono speaker gets both channels mixed together.";
            if (anyBadRate)
                filterNote += (filterNote.Length > 0 ? " " : "")
                    + "Any device listed as running at a rate the radio cannot use needs setting "
                    + "to 48000 hertz in Windows Sound settings — or choose MME as the audio "
                    + "system above, which converts the rate for you.";
            SetStatusLine(FilterNoteText, filterNote);
            FilterNoteText.Visibility = filterNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateStatusText();
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
        /// And a saved device that IS present but is not in the current view —
        /// its audio system is not the one selected, or its kind is hidden —
        /// keeps its own row too, at the top, labelled with the reason. It
        /// stays selected, so OK writes nothing and a working configuration
        /// keeps working; see <see cref="SelectFilteredSavedRow"/>.
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
                // Even an empty picker can be hiding the operator's own saved
                // device (a machine whose only inputs are virtual cables, all
                // filtered by the basic view). Their choice outranks the
                // filter — see SelectFilteredSavedRow.
                if (savedNamed && !savedMissing)
                {
                    var liveSaved = Devices.FindLive(saved!);
                    if (liveSaved != null)
                        return SelectFilteredSavedRow(list, rows, liveSaved, note);
                }

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
                if (idx < 0)
                {
                    // Belt and braces: FindPickerRow only returns rows that are
                    // in the current view, so this should not fire. If it ever
                    // does, keeping the saved device on screen beats snapping
                    // to row 0 — that would be a silent substitution, and OK
                    // would then COMMIT row 0, quietly replacing a deliberate
                    // choice with something the operator never picked and
                    // cannot glance at.
                    return SelectFilteredSavedRow(list, rows, match, note);
                }
                list.SelectedIndex = idx;
                // Channel handling is a decision the app makes on the
                // operator's behalf — a multi-channel device is used in stereo,
                // a mono one is duplicated or mixed — so the note says so
                // rather than leaving them to wonder what four channels, or
                // one, means for their audio. One vocabulary, from
                // Devices.DescribeChannels, so this cannot drift from the row.
                string channels = Devices.DescribeChannels(match);
                string channelPart = channels.Length > 0 ? $" It is {channels}." : "";
                string rateWarning = Devices.DescribeRate(match);
                string ratePart = rateWarning.Length > 0
                    ? $" {char.ToUpperInvariant(rateWarning[0])}{rateWarning.Substring(1)}. "
                      + "Set it to 48000 hertz in Windows Sound settings, or choose MME as the "
                      + "audio system above, which converts the rate for you."
                    : "";
                SetStatusLine(note, $"Currently using {match.Display}.{channelPart}{ratePart}");
                return rows;
            }

            // The normal route for a saved device that is live but not in the
            // current view: a different audio system is selected, or its kind
            // is hidden. Falling through to the not-chosen-yet message would
            // read as if the operator never chose anything, and OK would then
            // commit the fallback over the top of a deliberate choice. So the
            // choice stays on screen, with the reason.
            if (savedNamed && !savedMissing)
            {
                var live = Devices.FindLive(saved!);
                if (live != null)
                {
                    return SelectFilteredSavedRow(list, rows, live, note);
                }
            }

            // The pre-selection OK would commit must be a device the engine
            // can open — never the not-connected row, which cannot carry audio
            // at all. Mono rows qualify since 2026-08-16; the engine opens them
            // as mono and duplicates to stereo.
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
                SetStatusLine(note, $"No usable {role} device was found. Nothing in this list reports "
                          + "a channel JJ Flexible can open. Try a different audio system above, or "
                          + "turn on Show every sound endpoint.");
                return rows;
            }
            list.SelectedIndex = fallbackIdx;
            SetStatusLine(note, $"No {role} device chosen yet. {rows[fallbackIdx].Display} will be used unless you choose another.");
            return rows;
        }

        /// <summary>
        /// Keep a saved device visible and selected when the picker's current
        /// view does not contain it. The row goes first — same precedent as
        /// the not-connected row: the sentence that applies to the person's
        /// own configuration is the most important one in the list — and the
        /// note says plainly WHY it is on screen when nothing else like it is,
        /// so the list and the note can never disagree.
        /// </summary>
        /// <remarks>
        /// Two reasons a live saved device can be off the list, and they need
        /// different sentences. Its kind is hidden (a loopback or a virtual
        /// cable, in the basic view) — the original case. Or, since
        /// 2026-08-16, it belongs to a different audio system than the one
        /// selected: an operator upgrading into this change has a device saved
        /// under whatever the old folding rule silently picked, usually MME,
        /// while the selector now reads WASAPI. Saying "this kind of device is
        /// normally hidden" about a perfectly ordinary microphone would be
        /// description drift on the day it shipped.
        /// </remarks>
        private List<Devices.DeviceInfo> SelectFilteredSavedRow(
            ListBox list,
            List<Devices.DeviceInfo> rows,
            Devices.DeviceInfo savedRow,
            TextBlock note)
        {
            rows.Insert(0, savedRow);
            list.Items.Insert(0, savedRow.Display);
            list.IsEnabled = true;
            list.SelectedIndex = 0;

            bool otherApi = !Devices.ShowAdvancedDevices
                && Devices.SelectedHostApiTypeId >= 0
                && savedRow.HostApiTypeId != Devices.SelectedHostApiTypeId;

            string why = otherApi
                ? $"It uses {savedRow.HostApiName}, not the {Devices.NameOfHostApi(Devices.SelectedHostApiTypeId)} "
                  + "chosen above, so it is shown here to keep your saved choice yours. It will keep "
                  + "working. To move this device onto the audio system above, pick it from the rest "
                  + "of the list."
                : "This kind of device is normally hidden from the list — it is shown here so your "
                  + "saved choice stays yours. Turn on Show every sound endpoint to see others like it.";

            SetStatusLine(note, $"Currently using {savedRow.Display}. {why}");
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

        // Neither list narrates its own selection any more (Mic Level Track,
        // 2026-08-13). The ListBox rows are plain strings, so NVDA already
        // reads the focused row on every arrow press, and each list's
        // AutomationProperties.Name says which list you are in — once, on
        // entry, at no per-item cost. The app-pushed repeat of both was heard
        // twice per keystroke. The standing rule it leaves behind: only speak
        // to the screen reader when the control does not already convey the
        // information; prefer repairing the accessibility tree over narrating
        // around it. (The output list needs no handler at all now.)
        private void RadioInputList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // A running check belongs to the device it was started on. Moving
            // the selection makes the reading a lie, so the stream closes.
            StopMicCheck(speak: true, reason: "Microphone check stopped — you chose a different microphone.");
            // The Windows level control follows the selection for the same
            // reason: it must never be left pointed at the previous device.
            UpdateMicLevelControl();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadPortAudioDevices(announce: true);
        }

        /// <summary>
        /// Switch the audio system and rebuild both lists around it.
        /// </summary>
        /// <remarks>
        /// Takes effect immediately rather than on OK, because the whole point
        /// of the control is what the lists then contain — an operator has to
        /// see the devices an audio system offers before they can choose one
        /// of them. The choice is only PERSISTED on OK, with the devices.
        ///
        /// <para>
        /// This speaks, and that is not a violation of speak-only-when-the-UI-
        /// does-not-convey: the change happens two controls away from the one
        /// with focus, and how many devices survived the change is exactly what
        /// the operator needs and cannot hear from the combo they are standing
        /// on.
        /// </para>
        /// </remarks>
        private void HostApiCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int idx = HostApiCombo.SelectedIndex;
            if (idx < 0 || idx >= _hostApis.Count) return;

            int wanted = _hostApis[idx].TypeId;
            if (wanted == Devices.SelectedHostApiTypeId) return;

            // A running check belongs to the device it was started on, and the
            // row it was started from is about to be replaced.
            StopMicCheck(speak: false, reason: "");

            Devices.ApplyHostApiSelection(wanted);
            RebuildDeviceLists();

            SetStatusLine(HostApiNote, HostApiNoteText());
            Announce(Lexicon.Get("audio.device.audio_system_chosen",
                ("system", _hostApis[idx].Name),
                ("outputs", CountReal(_outputRows)), ("inputs", CountReal(_inputRows))));
        }

        /// <summary>
        /// Change the transmit rate. Applied to the static immediately so the
        /// note can describe the new value; persisted on OK.
        /// </summary>
        private void TxRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int idx = TxRateCombo.SelectedIndex;
            if (idx < 0 || idx >= TxRates.Length) return;
            Radios.FlexBase.OpusTxSampleRateSetting = TxRates[idx];
            SetStatusLine(TxRateNote, TxRateNoteText());
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
            // #128 sweep audit (2026-08-21): operator-facing boolean answers
            // back — and this one triggers a device re-enumeration whose only
            // other evidence is combo contents quietly changing.
            EarconPlayer.ToggleTone(on);
            // A full re-enumeration, not just a picker rebuild: WDM-KS devices
            // are skipped at enumeration time, so they are not in InputDevices
            // to be filtered back in. LoadHostApis runs inside, which is how
            // kernel streaming appears in and disappears from the audio-system
            // combo along with its devices.
            ReloadPortAudioDevices(announce: false);
            SetStatusLine(HostApiNote, HostApiNoteText());
            ScreenReaderOutput.Speak(
                on
                    ? Lexicon.Get("audio.device.showing_every_endpoint",
                        ("outputs", CountReal(_outputRows)), ("inputs", CountReal(_inputRows)))
                    : Lexicon.Get("audio.device.showing_one_audio_system",
                        ("system", Devices.NameOfHostApi(Devices.SelectedHostApiTypeId)),
                        ("outputs", CountReal(_outputRows)), ("inputs", CountReal(_inputRows))),
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
                Announce(Lexicon.Get("audio.device.choose_a_microphone_first"), VerbosityLevel.Critical);
                RadioInputList.Focus();
                return;
            }

            if (row.IsMissingSaved)
            {
                Announce(Lexicon.Get("audio.device.microphone_not_connected", ("device", row.Name)),
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
                string couldNotStart = Lexicon.Get("audio.device.mic_check_could_not_start",
                    ("reason", message));
                SetMicReading(couldNotStart);
                Announce(couldNotStart, VerbosityLevel.Critical);
                if (_privacyBlocked) MicPrivacyButton.Focus();
                return;
            }

            MicCheckButton.Content = "Stop _microphone check";
            AutomationProperties.SetName(MicCheckButton, "Stop microphone check");
            SetMicReading(Lexicon.Get("audio.device.mic_check_listening") + HostApiCaveat() + RateCaveat());
            _micTimer.Start();

            Announce(_privacyBlocked
                ? Lexicon.Get("audio.device.mic_check_started_but_blocked",
                    ("reason", _privacyExplanation))
                : Lexicon.Get("audio.device.mic_check_started", ("device", row.Name))
                  + HostApiCaveat() + RateCaveat(),
                _privacyBlocked ? VerbosityLevel.Critical : VerbosityLevel.Terse);
        }

        /// <summary>
        /// Alt+L speaks the current microphone-check reading from anywhere in
        /// this dialog.
        /// </summary>
        /// <remarks>
        /// Noel, 2026-08-13, setting his level for the first time with the new
        /// sliders: "can I see a meter value on the pc mic check in an edit box
        /// as I adjust it or do I have to stop the test to hear the value and
        /// then repeat".
        ///
        /// <para>
        /// The reading box does update live, twice a second, and deliberately
        /// is not a live region so it never interrupts. But while adjusting,
        /// focus is on the SLIDER, and the reading lives in a different control
        /// two stops away. So the honest loop was: arrow the slider, tab to the
        /// reading, read it, tab back, arrow again — four keystrokes of
        /// overhead per adjustment, and the slider position lost each time.
        /// </para>
        ///
        /// <para>
        /// This is the legitimate case for app speech under
        /// feedback_speak_only_when_ui_does_not_convey: the level a microphone
        /// is producing is genuinely not something a slider conveys, and no
        /// repair to the accessibility tree can make it so. It is also
        /// operator-initiated rather than pushed, which is the distinction that
        /// matters — nothing is said unless the key is pressed.
        /// </para>
        ///
        /// <para>
        /// Deliberately NOT wired to the slider's own value-change: moving the
        /// level RESETS the peak hold (see ResetMicCheckLevels), so the figure
        /// immediately after a move describes nothing yet. Speaking then would
        /// announce a number that is meaningless by construction. The operator
        /// adjusts, talks, then asks — and asking is one key.
        /// </para>
        /// </remarks>
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            // With Alt held, WPF reports Key.System and puts the real key in
            // SystemKey. Testing e.Key alone can never match, which is exactly
            // how this shipped broken on 2026-08-13: the chord was simply never
            // handled, so the screen reader read the focused control instead
            // and the key looked like it did nothing.
            var pressed = (e.Key == System.Windows.Input.Key.System)
                ? e.SystemKey : e.Key;
            if (pressed == System.Windows.Input.Key.L
                && (System.Windows.Input.Keyboard.Modifiers
                    & System.Windows.Input.ModifierKeys.Alt) != 0
                && (System.Windows.Input.Keyboard.Modifiers
                    & System.Windows.Input.ModifierKeys.Control) == 0)
            {
                SpeakMicReadingOnDemand();
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        /// <summary>
        /// Say the current reading, or say why there isn't one. Never silent:
        /// a key that sometimes does nothing is indistinguishable from a key
        /// that is broken.
        /// </summary>
        private void SpeakMicReadingOnDemand()
        {
            if (_probe == null)
            {
                Announce(Lexicon.Get("audio.device.mic_check_not_running"),
                    VerbosityLevel.Critical);
                return;
            }

            string text = _micReadingText;
            Announce(string.IsNullOrWhiteSpace(text)
                ? Lexicon.Get("audio.device.mic_check_no_reading_yet")
                : text, VerbosityLevel.Critical);
        }

        /// <summary>
        /// An observation about the rate the check opened at, or empty — which
        /// is the normal case and is the point. Same discipline as the noise
        /// note: added to a reading, never substituted for one, and silent
        /// unless it changes what the operator should do.
        ///
        /// It matters because the check and the radio link have different
        /// tolerances. The check will happily run at 44.1 kHz; the radio link
        /// cannot, because Opus has no 44.1 kHz mode. So a microphone can pass
        /// this check completely and still be unable to transmit — a divergence
        /// the operator has no other way to find out about except by keying up
        /// and being told nothing was heard.
        /// </summary>
        /// <summary>
        /// An observation when the check opened through a different audio
        /// system than the one selected, or empty — which is the normal case.
        /// </summary>
        /// <remarks>
        /// The probe re-resolves the device by name and host API inside its own
        /// PortAudio initialisation, and when the exact endpoint has gone it
        /// falls back to the same name under another audio system rather than
        /// claiming the microphone is unplugged. That is the right call, and
        /// silent it would be a trap: a check that passes under MME while
        /// transmit is configured for WASAPI proves nothing about transmit, and
        /// "the check works but the radio cannot hear me" is exactly the report
        /// nobody can act on.
        /// </remarks>
        private string HostApiCaveat()
        {
            var probe = _probe;
            if (probe == null) return "";
            if (Devices.ShowAdvancedDevices) return "";
            string opened = probe.Read().HostApiName ?? "";
            if (opened.Length == 0) return "";
            string selected = Devices.NameOfHostApi(Devices.SelectedHostApiTypeId);
            if (string.Equals(opened, selected, StringComparison.Ordinal)) return "";
            return $" Note: this check opened through {opened}, not the {selected} you chose above "
                + "— that endpoint was not available. What you hear here may not match what the "
                + "radio gets. Choose Refresh device list and pick the microphone again.";
        }

        private string RateCaveat()
        {
            var probe = _probe;
            if (probe == null) return "";
            int rate = probe.Read().SampleRate;
            if (rate <= 0 || JJAudioStream.IsOpusRate((uint)rate)) return "";
            // "needs 48 kHz" was hardcoded here, and stopped being true the
            // moment the transmit rate became selectable — Opus works at 48,
            // 24, 16, 12 and 8 kHz. Naming the whole set is both more accurate
            // and more useful, since it tells an operator with a 24 kHz device
            // that they already have a rate that works.
            return $" Note: Windows is running this microphone at {rate / 1000.0:0.#} kHz. "
                + "The check works at that rate, but audio to the radio is carried by Opus, "
                + "which works at 48, 24, 16, 12 and 8 kHz. Set this device to 48000 hertz in "
                + "Windows Sound settings, or choose MME as the audio system above, which "
                + "converts the rate for you, before you rely on it for transmit.";
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
                summary = Lexicon.Get("audio.device.mic_check_nothing_heard");
            }
            else if (final.HoldPeakDb <= NoiseFloorDb)
            {
                summary = Lexicon.Get("audio.device.mic_check_only_noise_floor",
                    ("peak", PeakText(final.HoldPeakDb)));
            }
            else
            {
                // The advice sentence rides along whatever the output mode is:
                // it is the one part that is neither a figure nor a verdict but
                // a direction, and an operator who suppressed the coaching
                // still needs to be told which knob to move. Its absence is
                // what made "coming in hot" a dead end.
                summary = Reading(Lexicon.Get("audio.device.mic_check_loudest_heard"),
                    final.HoldPeakDb, final.IntegratedLufs,
                    withAdvice: true,
                    advice: LevelAdvice(MicAudioReport.Verdict(final.HoldPeakDb)));
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
                string stopped = Lexicon.Get("audio.device.mic_check_stopped_faulted", ("reason", fault));
                SetMicReading(stopped);
                Announce(stopped, VerbosityLevel.Critical);
                return;
            }

            string text;
            if (!r.AnySound)
            {
                // Give the device a moment before passing judgement — some
                // interfaces hand over their first buffers late.
                if (r.Seconds < 1.0)
                {
                    // Same key as the line StartMicCheck shows, because it is
                    // the same sentence: two copies of it would age apart.
                    text = Lexicon.Get("audio.device.mic_check_listening");
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
                // The gated whole-check figure, not the 3 s window: the last
                // three seconds are the quiet this branch is reporting, while
                // the integrated figure still describes the speech before it.
                text = Reading("Mic audio: quiet right now. Loudest so far:",
                    r.HoldPeakDb, r.IntegratedLufs, withAdvice: false, advice: "");
            }
            else
            {
                // Same vocabulary the Audio Workshop and the Home fields use,
                // reading the same kind of number, so one level never gets two
                // different verdicts depending on where you asked. Loudness
                // rides along (Mic Level Track, 2026-08-13): peak answers "am
                // I clipping", but this check is the one place the operator is
                // actually SETTING a level, and peak cannot tell them when
                // they have stopped being too quiet — that is loudness's job.
                text = Reading("Mic audio now:", r.RecentPeakDb, r.ShortTermLufs,
                        withAdvice: false, advice: "")
                    + $" Loudest so far {PeakText(r.HoldPeakDb)}.";

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
                Announce(Lexicon.Get("audio.device.privacy_settings_opened"), VerbosityLevel.Terse);
                return;
            }
            Announce(failure, VerbosityLevel.Critical);
        }

        /// <summary>
        /// ", loudness N LUFS" — or empty when there is no honest figure:
        /// nothing has gated in yet, or the capture clipped (see
        /// <see cref="ClippedDb"/>). Display carries both numbers whenever
        /// both are honest; the verdict vocabulary stays peak's, from the
        /// same frozen <see cref="MicAudioReport.Verdict"/> every other
        /// surface uses.
        /// </summary>
        /// <summary>
        /// A reading, numbers first and coaching after, honouring the
        /// operator's verdict-output preference.
        /// </summary>
        /// <remarks>
        /// Noel, 2026-08-13, after setting a level with it for real: "I'd read
        /// the level number first and then the coaching... it's easier if
        /// you're adjusting to hear a level and then coaching."
        ///
        /// <para>
        /// The verdict used to lead. That was built so a fast listener could
        /// hear one token and stop — and it is the right instinct aimed at the
        /// wrong target. While ADJUSTING, the number IS the thing being
        /// tracked, and it is strictly more informative than the token that
        /// summarises it. Leading with it serves the same goal better: the
        /// earliest word out is now the most precise one, and the coaching is
        /// what you stay for rather than what you sit through.
        /// </para>
        ///
        /// <para>
        /// Also honours MicVerdictOutputMode, which this dialog previously
        /// ignored -- it called Verdict directly, so an operator who had
        /// chosen "Decibels only" in Settings still got the full coaching
        /// here. A preference that does not reach every surface is not a
        /// preference.
        /// </para>
        /// </remarks>
        private static string Reading(string lead, float peakDb, float lufs, bool withAdvice,
                                      string advice)
        {
            var mode = MicAudioReport.VerdictMode;
            string numbers = PeakText(peakDb) + LoudnessPart(lufs, peakDb);
            string verdict = MicAudioReport.Verdict(peakDb);

            // Plain English only: the operator asked not to hear figures.
            if (mode == MicVerdictOutputMode.Plain)
                return Lexicon.Get("audio.device.reading_plain",
                    ("lead", lead), ("verdict", verdict)) + (withAdvice ? advice : "");

            // Decibels only: the figures, and the direction to move them,
            // without the coaching sentence around it.
            if (mode == MicVerdictOutputMode.Numbers)
                return Lexicon.Get("audio.device.reading_numbers",
                    ("lead", lead), ("numbers", numbers)) + (withAdvice ? advice : "");

            return Lexicon.Get("audio.device.reading_full",
                ("lead", lead), ("numbers", numbers), ("verdict", verdict))
                + (withAdvice ? advice : "");
        }

        /// <summary>
        /// A level, in words that cannot lie about which end of the scale it
        /// is at.
        /// </summary>
        /// <remarks>
        /// Noel, 2026-08-13: "when it's peaking or when it's at 0% -140 dbfs,
        /// it's speaking that it's -0 or 0 which both say it's clipping which
        /// is wrong."
        ///
        /// <para>
        /// Two separate ways plain "F0" formatting misreports a level, and
        /// both land on the same wrong answer:
        /// </para>
        ///
        /// <para>
        /// Anything between -0.5 and 0 renders as "-0", which a screen reader
        /// says as zero. Zero dBFS is FULL SCALE — the ceiling — so a value
        /// just under it and a value at the floor can both be spoken as the
        /// loudest possible reading. Silence reported as clipping is the worst
        /// direction this can fail in, because the operator's correct response
        /// to each is the exact opposite of the other.
        /// </para>
        ///
        /// <para>
        /// So: at or under the floor there is no number worth saying, and the
        /// words say so. Near the ceiling a decimal place is kept, because
        /// -0.3 and -0.0 are genuinely different situations there. Everywhere
        /// in between, whole decibels are what an operator can act on.
        /// </para>
        /// </remarks>
        private static string PeakText(float db)
        {
            if (db <= MicProbe.SilenceDb) return Lexicon.Get("audio.device.peak_nothing_at_all");
            if (db > -1f) return Lexicon.Get("audio.device.peak_dbfs", ("db", $"{db:F1}"));
            return Lexicon.Get("audio.device.peak_dbfs", ("db", $"{db:F0}"));
        }

        private static string LoudnessPart(float lufs, float peakDb)
        {
            if (lufs <= LufsMeter.Floor || peakDb >= ClippedDb) return "";
            return Lexicon.Get("audio.device.loudness_lufs", ("lufs", $"{lufs:F0}"));
        }

        /// <summary>
        /// What to do about an off-target verdict, and where. Direction and
        /// stage, never just a state: this check measures stage one — the
        /// level Windows captures at — so the remedy names the Windows input
        /// level, not the radio. Turning the radio's mic gain down on a
        /// capture that clipped here yields quieter distortion, not clean
        /// audio. Empty for "just right", which needs no help.
        /// </summary>
        private string LevelAdvice(string verdict)
        {
            // The two poles come from the same function that produced the
            // verdict, so a vocabulary change there cannot silently strand
            // this branch on a stale string.
            string hot = MicAudioReport.Verdict(0f);
            string quiet = MicAudioReport.Verdict(-100f);
            if (verdict != hot && verdict != quiet) return "";

            var level = _micLevel;

            if (verdict == hot)
            {
                // A boost left up is the likeliest culprit for a pinned
                // reading, and it is the control Windows Settings does not
                // show — name it first when it is actually turned up.
                float boost = 0f;
                try { boost = (level != null && level.HasBoost) ? level.BoostDb : 0f; }
                catch { /* device gone; the plain advice still stands */ }
                if (boost > 0f)
                {
                    return " " + Lexicon.Get("audio.device.advice_lower_boost",
                        ("boost", $"{boost:F0}"));
                }
                return " " + (level != null
                    ? Lexicon.Get("audio.device.advice_lower_slider")
                    : Lexicon.Get("audio.device.advice_lower_in_windows"));
            }

            return " " + (level != null
                ? Lexicon.Get("audio.device.advice_raise_slider")
                : Lexicon.Get("audio.device.advice_raise_in_windows"));
        }

        // ------------------------------------------------ windows input level

        /// <summary>
        /// Bind the Windows level control to the microphone the input list
        /// selects, or disable it with the reason when no confident match
        /// exists. The measurement (the check above) and the control that
        /// moves it live side by side on purpose — stage one of the capture
        /// chain, adjustable at the stage where it is measured.
        /// </summary>
        private void UpdateMicLevelControl()
        {
            DisposeMicLevel();

            _micLevel = WindowsMicLevel.TryFind(SelectedInputRow(), out string whyNot);
            if (_micLevel == null)
            {
                DisableMicLevelControl(whyNot);
                return;
            }

            _micLevel.VolumeChanged += OnWindowsVolumeChanged;

            MicLevelSlider.IsEnabled = true;
            // The Alt+L hint lives on the slider itself, not only in the
            // prose above: this is the control an operator is sitting on when
            // they need it, and a shortcut mentioned two stops away is a
            // shortcut nobody finds.
            AutomationProperties.SetName(MicLevelSlider,
                $"Windows input level for {_micLevel.FriendlyName}, percent. "
                + "Alt+L speaks the current reading.");

            if (_micLevel.HasBoost)
            {
                _micLevelUpdatingUi = true;
                try
                {
                    MicBoostSlider.Minimum = _micLevel.BoostMinDb;
                    MicBoostSlider.Maximum = _micLevel.BoostMaxDb;
                    // Drivers step boost coarsely (10 dB is typical); the
                    // slider moves in the driver's own steps so every value
                    // shown is one the hardware actually has.
                    double step = _micLevel.BoostStepDb > 0f ? _micLevel.BoostStepDb : 1.0;
                    MicBoostSlider.SmallChange = step;
                    MicBoostSlider.LargeChange = step;
                    MicBoostSlider.TickFrequency = step;
                }
                finally
                {
                    _micLevelUpdatingUi = false;
                }
                AutomationProperties.SetName(MicBoostSlider,
                    $"Microphone Boost for {_micLevel.FriendlyName}, decibels");
                MicBoostLabel.Visibility = Visibility.Visible;
                MicBoostSlider.Visibility = Visibility.Visible;
            }
            else
            {
                MicBoostLabel.Visibility = Visibility.Collapsed;
                MicBoostSlider.Visibility = Visibility.Collapsed;
            }

            RefreshMicLevelFromWindows();
        }

        /// <summary>
        /// The honest-failure shape: slider disabled (and therefore out of
        /// the tab order), boost and unmute hidden, and the note — which IS
        /// still in the tab order — carrying the reason. A blind operator
        /// tabbing through the check lands on the explanation exactly where
        /// the control would have been.
        /// </summary>
        private void DisableMicLevelControl(string reason)
        {
            // Focus must not be standing on a control about to be disabled —
            // WPF would strand it on a dead element.
            if (MicLevelSlider.IsKeyboardFocused || MicBoostSlider.IsKeyboardFocused)
                RadioInputList.Focus();

            _micLevelUpdatingUi = true;
            try
            {
                MicLevelSlider.IsEnabled = false;
                MicLevelSlider.Value = 0;
            }
            finally
            {
                _micLevelUpdatingUi = false;
            }
            AutomationProperties.SetName(MicLevelSlider, "Windows input level, not available");
            MicBoostLabel.Visibility = Visibility.Collapsed;
            MicBoostSlider.Visibility = Visibility.Collapsed;
            MicUnmuteButton.Visibility = Visibility.Collapsed;
            SetMicLevelNote(reason);
        }

        /// <summary>
        /// Read the endpoint's current level, boost, and mute into the
        /// controls. Called on bind and whenever Windows reports a change —
        /// including our own writes echoing back, which arrive holding the
        /// value the sliders already show and therefore move nothing.
        /// </summary>
        private void RefreshMicLevelFromWindows()
        {
            var level = _micLevel;
            if (level == null) return;
            try
            {
                float percent = level.Percent;
                bool muted = level.Muted;
                float boost = level.HasBoost ? level.BoostDb : 0f;

                _micLevelUpdatingUi = true;
                try
                {
                    MicLevelSlider.Value = Math.Round(percent);
                    if (level.HasBoost) MicBoostSlider.Value = boost;
                }
                finally
                {
                    _micLevelUpdatingUi = false;
                }

                MicUnmuteButton.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
                SetMicLevelNote(BuildMicLevelNote(level, muted, boost));
            }
            catch (Exception ex)
            {
                MicLevelFailed(ex);
            }
        }

        /// <summary>
        /// The always-current sentence under the level controls: which Windows
        /// device the slider actually moves — the honesty guarantee the
        /// matching rules earn — with mute leading when it applies, because a
        /// Windows mute wins over every slider on this page.
        /// </summary>
        private static string BuildMicLevelNote(WindowsMicLevel level, bool muted, float boostDb)
        {
            if (muted)
            {
                return $"{level.FriendlyName} is muted in Windows — a mute wins over every level "
                    + "slider. Unmute it, then run the check again.";
            }

            string text = level.FollowsWindowsDefault
                ? $"This device follows your Windows default microphone. Right now that is "
                  + $"{level.FriendlyName}, and the slider moves its input level."
                : $"This slider moves the Windows input level for {level.FriendlyName} — the same "
                  + "level as Windows Sound settings.";

            if (level.HasBoost && boostDb > 0f)
            {
                text += $" Microphone Boost is turned up, plus {boostDb:F0} dB — if the check says "
                    + "you are coming in hot, lower the boost first.";
            }
            return text;
        }

        private void SetMicLevelNote(string text)
        {
            if (MicLevelNote == null) return;
            // Assign only on change: notifications can arrive in bursts, and
            // rewriting identical text would reset a screen reader's review
            // position for nothing.
            if (MicLevelNote.Text == text) return;
            SetStatusLine(MicLevelNote, text);
        }

        /// <summary>
        /// Core Audio raises volume notifications on a COM worker thread —
        /// for external changes (Windows Settings, another app) and for our
        /// own writes alike. Hop to the UI thread and re-read.
        /// </summary>
        private void OnWindowsVolumeChanged()
        {
            Dispatcher.BeginInvoke(new Action(RefreshMicLevelFromWindows));
        }

        // No app speech in either slider handler, deliberately: a slider's
        // value is exactly what a screen reader announces natively on every
        // arrow press, and saying it again is the same speak-what-the-control-
        // already-says bug this dialog just had removed from its lists.
        /// <summary>
        /// Zero the running check's peak-hold and loudness after the gain has
        /// moved. No-op when no check is running.
        /// </summary>
        /// <remarks>
        /// Found live by Noel on 2026-08-13, the morning these sliders shipped:
        /// he turned the input level all the way down and the check still said
        /// 0 dBFS and "clipping". It was reporting the loudest thing heard
        /// since the check began — from before he touched anything — because
        /// reading the display means pausing, and the quiet branch of the
        /// reading is the one that reports the hold peak. Every adjustment
        /// looked like it did nothing.
        ///
        /// <para>
        /// A meter that cannot be zeroed cannot be used to set a level, which
        /// made the control useless on the day it arrived. The hold peak and
        /// the integrated loudness describe a gain setting that stopped
        /// existing the instant the slider moved: carrying them forward is not
        /// stale, it is wrong.
        /// </para>
        /// </remarks>
        private void ResetMicCheckLevels()
        {
            _probe?.ResetLevels();
        }

        private void MicLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_micLevelUpdatingUi) return;
            var level = _micLevel;
            if (level == null) return;
            try
            {
                level.Percent = (float)e.NewValue;
                // Everything measured before this moment describes the old
                // gain. Zeroing here is what makes adjust-and-listen work.
                ResetMicCheckLevels();
            }
            catch (Exception ex)
            {
                MicLevelFailed(ex);
            }
        }

        private void MicBoostSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_micLevelUpdatingUi) return;
            var level = _micLevel;
            if (level == null || !level.HasBoost) return;
            try
            {
                level.BoostDb = (float)e.NewValue;
                ResetMicCheckLevels();
                // Boost changes do not raise the endpoint's volume
                // notification (the boost is a topology part, not the
                // endpoint volume), so the note's boost sentence is refreshed
                // by hand.
                RefreshMicLevelFromWindows();
            }
            catch (Exception ex)
            {
                MicLevelFailed(ex);
            }
        }

        private void MicUnmuteButton_Click(object sender, RoutedEventArgs e)
        {
            var level = _micLevel;
            if (level == null) return;
            try
            {
                level.Muted = false;
                // Everything measured while Windows had the microphone muted
                // is a fact about the mute, not about the microphone.
                ResetMicCheckLevels();
                // A button whose whole effect happens inside Windows has to
                // say it happened — this is state the control itself cannot
                // convey, so speaking it is not a repeat.
                Announce(Lexicon.Get("audio.device.unmuted_in_windows", ("device", level.FriendlyName)),
                    VerbosityLevel.Terse);
                RefreshMicLevelFromWindows();
                // The refresh just collapsed this button out from under the
                // keyboard. Land on the level slider — the adjacent control,
                // and the natural next thing to set now that sound can flow.
                MicLevelSlider.Focus();
            }
            catch (Exception ex)
            {
                MicLevelFailed(ex);
            }
        }

        /// <summary>
        /// A read or write against the endpoint failed — almost always a
        /// device unplugged while the dialog was open. Say so, out loud,
        /// because the operator was mid-adjustment; never leave a control
        /// silently wired to hardware that stopped answering.
        /// </summary>
        private void MicLevelFailed(Exception ex)
        {
            Tracing.TraceLine("AudioDevicesDialog: Windows level control failed — " + ex.Message,
                TraceLevel.Error);
            DisposeMicLevel();
            string reason = Lexicon.Get("audio.device.level_control_stopped_responding");
            DisableMicLevelControl(reason);
            Announce(reason, VerbosityLevel.Critical);
        }

        private void DisposeMicLevel()
        {
            var level = _micLevel;
            _micLevel = null;
            if (level == null) return;
            level.VolumeChanged -= OnWindowsVolumeChanged;
            level.Dispose();
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
                // NOT normalised, deliberately: these two roles are also named
                // in audio.device.role_output_note / role_input_note and in
                // audio.device.role_output_receipt / role_input_receipt, and
                // the three spellings of the output role differ today. Which
                // wording survives is the owner's call, not the extractor's.
                if (!ConfirmSelectionUsable(RadioOutputList, _outputRows,
                        Lexicon.Get("audio.device.role_output_refusal"))
                    || !ConfirmSelectionUsable(RadioInputList, _inputRows,
                        Lexicon.Get("audio.device.role_input_refusal")))
                {
                    return;
                }

                StopMicCheck(speak: false, reason: "");

                // The audio system first, so the two device writes below record
                // the selection that was in force when they were chosen. It is
                // saved even when neither device changed — an operator who
                // switched from MME to WASAPI and kept the same hardware made a
                // real choice, and losing it on OK would make the selector look
                // broken in exactly the case it exists for.
                bool apiChanged = _devices.SavedHostApiTypeId != Devices.SelectedHostApiTypeId;
                _devices.SaveHostApiSelection();
                if (apiChanged)
                    saved.Add(Lexicon.Get("audio.device.commit_audio_system",
                        ("system", Devices.NameOfHostApi(Devices.SelectedHostApiTypeId))));

                saved.AddRange(CommitRadioDevice(
                    RadioOutputList, _outputRows, Devices.DeviceTypes.output,
                    Lexicon.Get("audio.device.role_output_receipt")));
                saved.AddRange(CommitRadioDevice(
                    RadioInputList, _inputRows, Devices.DeviceTypes.input,
                    Lexicon.Get("audio.device.role_input_receipt")));

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

                // Transmit rate. Already applied to the static by the combo's
                // handler so the note could describe it; this is the write that
                // makes it survive a restart. It takes effect on the next
                // connect, because the encoder is built during connect — say so
                // rather than letting an operator wonder why nothing changed.
                int txIdx = TxRateCombo.SelectedIndex;
                if (txIdx >= 0 && txIdx < TxRates.Length
                    && _audioConfig.OpusTxSampleRate != (int)TxRates[txIdx])
                {
                    _audioConfig.OpusTxSampleRate = (int)TxRates[txIdx];
                    saved.Add($"Transmit audio quality: {TxRateLabel(TxRates[txIdx])}"
                        + ", from your next connection");
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
                    ? Lexicon.Get("audio.device.saved") + " " + string.Join(". ", saved) + "."
                    : Lexicon.Get("audio.device.saved"),
                VerbosityLevel.Terse, true);

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// True when this list's selection is something the engine can open.
        /// A selection it cannot speaks the reason, puts focus back on the
        /// list, and keeps the dialog open so the operator can choose again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The not-connected row passes. Keeping a saved device you intend to
        /// plug back in is a legitimate answer, and the only honest alternative
        /// would be to force a change nobody asked for. What it must not do is
        /// pass silently — see <see cref="CommitRadioDevice"/>.
        /// </para>
        /// <para>
        /// <b>Mono passes too, as of 2026-08-16.</b> This used to refuse it,
        /// in words the list never used: the row was tagged "mono, not usable
        /// yet" and the refusal here said "it needs a stereo device", so one
        /// limitation had two vocabularies and neither gave a reason. The right
        /// unification turned out to be deleting the refusal rather than
        /// rewording it — the engine opens mono now. What is left is a guard
        /// that can only fire on a device reporting no channels at all, phrased
        /// through the same helper the rows use so the two cannot drift apart
        /// again.
        /// </para>
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
                Lexicon.Get("audio.device.no_audio_channels",
                    ("device", rows[idx].Name), ("role", role)),
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
                yield return Lexicon.Get("audio.device.commit_not_connected",
                    ("role", role), ("device", chosen.Name));
                yield break;
            }

            var current = (type == Devices.DeviceTypes.input)
                ? _devices!.InputDevice : _devices!.OutputDevice;
            if (Devices.SameDevice(current, chosen))
            {
                yield return Lexicon.Get("audio.device.commit_unchanged",
                    ("role", role), ("device", chosen.Name));
                yield break;
            }

            _devices!.SetConfiguredDevice(type, chosen);
            yield return Lexicon.Get("audio.device.commit_chosen",
                ("role", role), ("device", chosen.Name));
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
            Action? persistAudioConfig = null,
            bool startMicCheck = false)
        {
            var dlg = new AudioDevicesDialog(audioDevicesFile, audioConfig, persistAudioConfig);
            if (owner != null) dlg.Owner = owner;
            if (startMicCheck) dlg._startCheckOnOpen = true;
            bool ok = dlg.ShowDialog() == true;
            return ok && dlg.RadioAudioConfigured;
        }

        /// <summary>
        /// Set by a caller that opened this dialog specifically to check a
        /// microphone — the Audio Workshop's "Check Microphone" button — so the
        /// operator does not arrive at a dialog they asked a question of and
        /// have to ask it a second time. Runs on Loaded, after the device lists
        /// are populated and a row is selected.
        /// </summary>
        private bool _startCheckOnOpen;
    }
}
