using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// Meter tone configuration: which of the radio's meters you can hear, what
/// each one sounds like, and when.
/// </summary>
/// <remarks>
/// <para>
/// <b>This panel is a LIVE VIEW over <see cref="MeterToneEngine.Slots"/>.</b>
/// It used to build a group of controls per slot, once, in its constructor —
/// so a slot added later existed in the engine with no controls anywhere. Noel
/// added one, was told he had slot 5, and could see nothing (#129). Everything
/// here is rebuilt from the engine on <see cref="MeterToneEngine.SlotsChanged"/>
/// instead. There is no snapshot to go stale.
/// </para>
/// <para>
/// <b>Shape.</b> One meter selector plus one set of controls that retarget,
/// rather than N stacks of controls to tab through. Noel: "making it so that
/// you have tabs to go through all slots is not efficient, so you'd need a
/// combo to select a tone and then modify / enable / do whatever with it. Also
/// would allow for del key / remove yes/no query."
/// </para>
/// <para>
/// <b>Threading.</b> Both <see cref="MeterToneEngine.SlotsChanged"/> and the
/// inventory's InventoryChanged can be raised on FlexLib's meter thread.
/// Everything that touches a control goes through the dispatcher.
/// </para>
/// </remarks>
public partial class MetersPanel : UserControl
{
    /// <summary>Fired when user presses Escape — wired to return focus to FreqOut.</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Callback to return focus to the FreqOut control.</summary>
    public Action? ReturnFocusToFreqOut { get; set; }

    /// <summary>
    /// True while the code is pushing engine state INTO the controls, so the
    /// change handlers know not to push it straight back out again. Without it,
    /// selecting a different meter writes the previous meter's settings onto
    /// the new one.
    /// </summary>
    private bool _loading;

    /// <summary>The inventory we are currently subscribed to, so we can let go
    /// of it when the radio changes.</summary>
    private MeterInventory? _inventory;

    /// <summary>The stable id of the slot the operator is editing. Ids survive
    /// reorder and deletion; an index does not.</summary>
    private string _selectedSlotId = "";

    /// <summary>
    /// The meters offered when "show every meter" is off. Names as the RADIO
    /// reports them. This is the #62 device-picker precedent applied to meters:
    /// a hundred entries in a combo is its own accessibility problem, so the
    /// default is a short list and the long one is one checkbox away.
    /// </summary>
    private static readonly string[] CommonMeterNames =
    {
        "LEVEL",    // the slice S-meter
        "FWDPWR",
        "REFPWR",
        "SWR",
        "ALC",      // software ALC: transmit drive
        "HWALC",    // the external-amplifier ALC line
        "SC_MIC",   // transmit audio from either source
        "MIC",
        "COMPPEAK",
        "PATEMP",
        "+13.8A",
    };

    // The labels are looked up at construction rather than held in a static
    // initialiser, so an operator's overlay is in force by the time the combo
    // is built. The VALUE is what the code matches on; the label is only ever
    // displayed, and the combo maps back by index.
    private static readonly (MeterActivation Value, string Key)[] ActivationChoices =
    {
        (MeterActivation.Always, "audio.meters.panel.activation_always"),
        (MeterActivation.ReceiveOnly, "audio.meters.panel.activation_receive_only"),
        (MeterActivation.TransmitOnly, "audio.meters.panel.activation_transmit_only"),
    };

    public MetersPanel()
    {
        InitializeComponent();

        foreach (string name in MeterVoiceLibrary.AllNames) VoiceCombo.Items.Add(name);
        foreach (var choice in ActivationChoices) ActivationCombo.Items.Add(Lexicon.Get(choice.Key));

        LoadGlobalsFromEngine();

        SubscribeToEngine();
        RefreshSourceChoices();
        RefreshSlotList();

        // Loaded and Unloaded are SYMMETRIC on purpose. The subscription used
        // to be made once in this constructor while the unsubscribe ran on
        // every Unloaded, so the first Unloaded left the panel permanently
        // deaf to SlotsChanged — #129 again, in the one form that would be
        // hardest to reproduce. Home is a WPF UserControl inside a WinForms
        // ElementHost, and ElementHost content is reloaded on host handle
        // recreation, so Unloaded/Loaded pairs are a real event here, not a
        // theoretical one. Both calls are idempotent; resubscribing rebuilds
        // from the live engine rather than trusting whatever the controls
        // still held.
        Loaded += OnPanelLoaded;
        Unloaded += OnPanelUnloaded;
    }

    /// <summary>True while this panel holds engine subscriptions.</summary>
    private bool _subscribedToEngine;

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToEngine();
        RefreshSourceChoices();
        RefreshSlotList();
    }

    private void OnPanelUnloaded(object sender, RoutedEventArgs e)
    {
        // A preview tone must not outlive the panel that started it (#131).
        StopMeterTestTone();
        UnsubscribeFromEngine();
    }

    private void SubscribeToEngine()
    {
        if (_subscribedToEngine) return;
        MeterToneEngine.SlotsChanged += OnEngineSlotsChanged;
        MeterToneEngine.RadioChanged += OnEngineRadioChanged;
        _subscribedToEngine = true;
        HookInventory();
    }

    private void UnsubscribeFromEngine()
    {
        if (!_subscribedToEngine) return;
        MeterToneEngine.SlotsChanged -= OnEngineSlotsChanged;
        MeterToneEngine.RadioChanged -= OnEngineRadioChanged;
        _subscribedToEngine = false;
        UnhookInventory();
    }

    #region Panel visibility (no audio state)

    /// <summary>
    /// Show the panel and put the operator in it. Ctrl+M's whole job.
    /// </summary>
    /// <remarks>
    /// Showing the panel and turning the tones on used to be ONE action, so an
    /// operator who only wanted to look at the settings started a noise, and an
    /// operator who wanted the noise off had to be looking at the panel (#126).
    /// They are separate now: this is the panel, and the tone switch lives on
    /// Ctrl+J then T (and in the Meter Tones menu). Nothing here changes what
    /// you can hear.
    /// </remarks>
    public void ShowPanel()
    {
        Visibility = Visibility.Visible;
        MetersExpander.IsExpanded = true;
        ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.panel.opened"), VerbosityLevel.Terse);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SlotCombo.Items.Count > 0) SlotCombo.Focus();
            else MetersExpander.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Hide the panel again. Leaves meter tones exactly as they were.</summary>
    public void HidePanel()
    {
        // A preview belongs to the panel. Closing the panel ends it rather than
        // leaving a tone sounding over receive audio with no visible control
        // left to stop it (#131).
        StopMeterTestTone();
        MetersExpander.IsExpanded = false;
        Visibility = Visibility.Collapsed;
        ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.panel.closed"), VerbosityLevel.Terse);
        ReturnFocusToFreqOut?.Invoke();
    }

    /// <summary>Show the panel if it is hidden, hide it if it is showing.</summary>
    public void TogglePanelVisibility()
    {
        // #128: tone at the toggle choke (both roads — Ctrl+M and the menu
        // item — come through here), matching the Show Field Panel menu item,
        // which has toned since Sprint 32 Track E. Not in ShowPanel/HidePanel:
        // those are also navigation calls, and a tone on every programmatic
        // show would violate the internal-transitions-stay-silent rule (#58).
        if (Visibility == Visibility.Visible && MetersExpander.IsExpanded)
        {
            EarconPlayer.ToggleTone(false);
            HidePanel();
        }
        else
        {
            EarconPlayer.ToggleTone(true);
            ShowPanel();
        }
    }

    #endregion

    #region Live binding to the engine and the radio

    private void OnEngineSlotsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(RefreshSlotList));

    private void OnEngineRadioChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            HookInventory();
            RefreshSourceChoices();
            LoadSelectedSlot();
        }));

    private void OnInventoryChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshSourceChoices();
            LoadSelectedSlot();
        }));

    /// <summary>
    /// Follow the current radio's inventory. Bound rather than sampled on
    /// purpose: FlexLib raises nothing when a meter appears and the list GROWS
    /// during registration, so a single read at construction catches a
    /// truncated census with the transmit-side meters still to arrive.
    /// </summary>
    private void HookInventory()
    {
        MeterInventory? next = MeterToneEngine.Inventory;
        if (ReferenceEquals(next, _inventory)) return;
        UnhookInventory();
        _inventory = next;
        if (_inventory != null) _inventory.InventoryChanged += OnInventoryChanged;
    }

    private void UnhookInventory()
    {
        if (_inventory != null) _inventory.InventoryChanged -= OnInventoryChanged;
        _inventory = null;
    }

    #endregion

    #region The slot selector

    /// <summary>Rebuild the meter selector from the engine's live slot list.</summary>
    private void RefreshSlotList()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            var slots = MeterToneEngine.Slots;
            SlotCombo.Items.Clear();

            int selectIndex = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                MeterDefinition def = slots[i].Definition;
                string name = string.IsNullOrWhiteSpace(def.Name) ? def.Source.Key : def.Name;
                SlotCombo.Items.Add(Lexicon.Get(
                    def.Enabled
                        ? "audio.meters.panel.slot_item_sounding"
                        : "audio.meters.panel.slot_item_silent",
                    ("number", i + 1), ("name", name)));
                if (def.Id == _selectedSlotId) selectIndex = i;
            }

            if (selectIndex < 0 && slots.Count > 0) selectIndex = 0;
            SlotCombo.SelectedIndex = selectIndex;
            _selectedSlotId = selectIndex >= 0 ? slots[selectIndex].Definition.Id : "";

            AddSlotButton.IsEnabled = slots.Count < MeterToneEngine.MaxSlots;
            DeleteButton.IsEnabled = slots.Count > 1;
            SetSlotControlsEnabled(slots.Count > 0);
            AutomationProperties.SetName(SlotCombo,
                Lexicon.Get("audio.meters.panel.slot_combo_name",
                    ("count", slots.Count), ("max", MeterToneEngine.MaxSlots)));
        }
        finally
        {
            _loading = wasLoading;
        }

        LoadSelectedSlot();
    }

    private void SetSlotControlsEnabled(bool on)
    {
        SourceCombo.IsEnabled = on;
        AllMetersCheck.IsEnabled = on;
        VoiceCombo.IsEnabled = on;
        ActivationCombo.IsEnabled = on;
        PanSlider.IsEnabled = on;
        VolumeSlider.IsEnabled = on;
        PitchLowBox.IsEnabled = on;
        PitchHighBox.IsEnabled = on;
        SlotEnabledCheck.IsEnabled = on;
        TestButton.IsEnabled = on;
    }

    /// <summary>The slot being edited, or null when there are none.</summary>
    private MeterSlot? SelectedSlot
    {
        get
        {
            var slots = MeterToneEngine.Slots;
            int i = SlotCombo.SelectedIndex;
            return i >= 0 && i < slots.Count ? slots[i] : null;
        }
    }

    private void SlotCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        MeterSlot? slot = SelectedSlot;
        _selectedSlotId = slot?.Definition.Id ?? "";
        LoadSelectedSlot();
    }

    /// <summary>
    /// Delete removes the selected meter, with a confirm. Noel asked for the
    /// key explicitly; the confirm is there because a meter carries a voice and
    /// a pitch mapping somebody tuned by ear, and there is no undo.
    /// </summary>
    private void SlotCombo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        DeleteSelectedSlot();
    }

    #endregion

    #region The source picker

    /// <summary>
    /// One choosable meter. Carries everything needed to retarget a slot, so
    /// picking one does not require a second lookup that could fail.
    /// </summary>
    private sealed class SourceChoice
    {
        public string Key { get; init; } = "";
        public string Display { get; init; } = "";

        /// <summary>
        /// What to CALL the meter once it is chosen, as opposed to how to list
        /// it. The list entry carries the radio's description so you can tell
        /// two unfamiliar meters apart while browsing; the slot is then named
        /// with this, because the slot selector reads every entry aloud and a
        /// sentence per slot is not a list you can navigate.
        /// </summary>
        public string ShortName { get; init; } = "";

        public int SliceIndex { get; init; } = -1;
        public MeterRange Range { get; init; } = new();
        public MeterActivation Activation { get; init; } = MeterActivation.Always;
        public string Detail { get; init; } = "";
        public override string ToString() => Display;
    }

    private readonly List<SourceChoice> _sourceChoices = new();

    /// <summary>
    /// Rebuild the source list from the radio's own meter inventory, honouring
    /// the common-versus-all switch.
    /// </summary>
    /// <remarks>
    /// With no radio the list falls back to the meters we know the hardware
    /// family reports. A settings panel that empties itself when the rig is off
    /// is a settings panel you cannot prepare with — settings are intents, and
    /// an intent does not need the radio present to be expressed.
    /// </remarks>
    private void RefreshSourceChoices()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            _sourceChoices.Clear();
            bool all = AllMetersCheck.IsChecked == true;
            MeterInventory? inv = MeterToneEngine.Inventory;

            if (inv != null && inv.Count > 0)
            {
                // Slice meters get an "active slice" entry as well as a pinned
                // one per slice. Without it the commonest setting in the
                // application could not be SELECTED: a slice source of -1 means
                // "follow whichever slice I am listening to", which is what
                // every default and every migrated config carries, while every
                // choice built from a live reading carries that slice's real
                // number. -1 never equalled 0, so the S-meter matched nothing,
                // and the panel told the operator their S-meter was "not
                // reported by this radio" while its tone played perfectly.
                int firstSliceChoice = -1;
                var activeSliceChoices = new List<SourceChoice>();
                var seenSliceMeters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (MeterGroup group in inv.Groups)
                {
                    bool isSlice = string.Equals(group.Source, "SLC", StringComparison.OrdinalIgnoreCase);
                    foreach (MeterReading r in group.Meters)
                    {
                        if (!all && !IsCommon(r.Name)) continue;
                        if (isSlice && firstSliceChoice < 0) firstSliceChoice = _sourceChoices.Count;
                        _sourceChoices.Add(FromReading(group, r, isSlice));
                        if (isSlice && seenSliceMeters.Add(r.Name))
                            activeSliceChoices.Add(FromActiveSliceReading(r));
                    }
                }

                // Ahead of the pinned entries, because following the active
                // slice is what an operator who does not think in slice numbers
                // wants, and the first matching entry is the one they land on.
                if (activeSliceChoices.Count > 0 && firstSliceChoice >= 0)
                    _sourceChoices.InsertRange(firstSliceChoice, activeSliceChoices);
            }
            else
            {
                foreach (var entry in LegacyMeterCatalog.Entries)
                    _sourceChoices.Add(FromCatalog(entry));
            }

            // A saved meter this radio does not report must still be visible and
            // still be selectable. Dropping it out of the list is how a setting
            // silently becomes something else.
            MeterDefinition? def = SelectedSlot?.Definition;
            if (def != null && def.Source.Kind == MeterSourceKind.RadioReported &&
                !string.IsNullOrWhiteSpace(def.Source.Key) &&
                !_sourceChoices.Any(c => Matches(c, def.Source)))
            {
                // Say WHICH thing is missing. A meter the radio has never heard
                // of and a meter on a slice that is not running are different
                // problems with different fixes, and one message for both sent
                // the operator looking for the wrong one.
                bool nameIsReported = inv?.Find(def.Source.Key) != null;
                bool pinnedToSlice = def.Source.SliceIndex >= 0;
                string sliceNumber = def.Source.SliceIndex.ToString(CultureInfo.CurrentCulture);

                string display = nameIsReported && pinnedToSlice
                    ? Lexicon.Get("audio.meters.panel.missing_slice_display",
                        ("key", def.Source.Key), ("slice", sliceNumber))
                    : Lexicon.Get("audio.meters.panel.missing_meter_display", ("key", def.Source.Key));

                string detail = nameIsReported && pinnedToSlice
                    ? Lexicon.Get("audio.meters.panel.missing_slice_detail",
                        ("key", def.Source.Key), ("slice", sliceNumber))
                    : Lexicon.Get("audio.meters.panel.missing_meter_detail");

                _sourceChoices.Add(new SourceChoice
                {
                    Key = def.Source.Key,
                    SliceIndex = def.Source.SliceIndex,
                    Display = display,
                    ShortName = def.Source.Key,
                    Range = def.Range.Clone(),
                    Activation = def.Activation,
                    Detail = detail,
                });
            }

            SourceCombo.Items.Clear();
            foreach (SourceChoice choice in _sourceChoices) SourceCombo.Items.Add(choice);
            AutomationProperties.SetName(SourceCombo,
                Lexicon.Get("audio.meters.panel.source_combo_name", ("count", _sourceChoices.Count)));
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private static bool IsCommon(string name) =>
        CommonMeterNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(SourceChoice choice, MeterSourceRef source) =>
        string.Equals(choice.Key, source.Key, StringComparison.OrdinalIgnoreCase) &&
        choice.SliceIndex == source.SliceIndex;

    private static SourceChoice FromReading(MeterGroup group, MeterReading r, bool isSlice)
    {
        // The radio does not say when a meter is meaningful, only what it
        // measures. For the meters we have historical knowledge of, keep that
        // knowledge; for the rest, Always is the honest answer.
        var known = LegacyMeterCatalog.Find(r.Name);
        string units = MeterReading.UnitsText(r.Units);
        string label = r.Description.Length != 0
            ? Lexicon.Get("audio.meters.panel.source_label",
                ("name", r.Name), ("description", r.Description))
            : r.Name;

        return new SourceChoice
        {
            Key = r.Name,
            SliceIndex = isSlice ? r.SourceIndex : -1,
            Display = Lexicon.Get("audio.meters.panel.source_display",
                ("group", group.Label), ("label", label)),
            ShortName = isSlice
                ? Lexicon.Get("audio.meters.panel.short_name_on_slice",
                    ("name", r.Name),
                    ("slice", r.SourceIndex.ToString(CultureInfo.CurrentCulture)))
                : r.Name,
            Range = new MeterRange
            {
                Low = r.Low,
                High = r.High,
                Units = TranslateUnits(r.Units),
                UnitsLabel = units,
            },
            Activation = known?.Activation ?? MeterActivation.Always,
            // Two keys rather than one with an optional " {units}" fragment —
            // the unitless variant is a different sentence, not the same one
            // with a hole in it.
            Detail = Lexicon.Get(
                units.Length != 0
                    ? "audio.meters.panel.detail_range_reading"
                    : "audio.meters.panel.detail_range_reading_no_units",
                ("low", r.Low.ToString("0.##", CultureInfo.CurrentCulture)),
                ("high", r.High.ToString("0.##", CultureInfo.CurrentCulture)),
                ("units", units),
                ("reading", r.ValueText())),
        };
    }

    /// <summary>
    /// The same slice meter, following whichever slice the operator is actually
    /// listening to rather than one fixed receiver.
    /// </summary>
    /// <remarks>
    /// A source slice index of -1 is what the engine already means by "follow
    /// the active slice" — <c>MeterToneEngine.SourceMatches</c> has resolved it
    /// against the active slice all along. What was missing was any way for the
    /// operator to SAY it: the picker only ever offered pinned entries, so the
    /// setting every default ships with could be heard but not selected, and
    /// re-picking a source silently pinned an S-meter to one slice.
    /// </remarks>
    private static SourceChoice FromActiveSliceReading(MeterReading r)
    {
        var known = LegacyMeterCatalog.Find(r.Name);
        string units = MeterReading.UnitsText(r.Units);
        string label = r.Description.Length != 0
            ? Lexicon.Get("audio.meters.panel.source_label",
                ("name", r.Name), ("description", r.Description))
            : r.Name;

        return new SourceChoice
        {
            Key = r.Name,
            SliceIndex = -1,
            Display = Lexicon.Get("audio.meters.panel.active_slice_display", ("label", label)),
            ShortName = Lexicon.Get("audio.meters.panel.short_name_active_slice", ("name", r.Name)),
            Range = new MeterRange
            {
                Low = r.Low,
                High = r.High,
                Units = TranslateUnits(r.Units),
                UnitsLabel = units,
            },
            Activation = known?.Activation ?? MeterActivation.Always,
            Detail = Lexicon.Get(
                units.Length != 0
                    ? "audio.meters.panel.active_slice_detail"
                    : "audio.meters.panel.active_slice_detail_no_units",
                ("low", r.Low.ToString("0.##", CultureInfo.CurrentCulture)),
                ("high", r.High.ToString("0.##", CultureInfo.CurrentCulture)),
                ("units", units)),
        };
    }

    private static SourceChoice FromCatalog(LegacyMeterCatalog.Entry entry)
    {
        string key = LegacyMeterCatalog.RadioMeterName(entry.Key);
        return new SourceChoice
        {
            Key = key,
            SliceIndex = -1,
            Display = Lexicon.Get("audio.meters.panel.catalog_display",
                ("displayName", entry.DisplayName), ("key", key)),
            ShortName = entry.DisplayName,
            Range = new MeterRange
            {
                Low = entry.Low,
                High = entry.High,
                Units = entry.Units,
                UnitsLabel = entry.UnitsLabel,
            },
            Activation = entry.Activation,
            Detail = Lexicon.Get("audio.meters.panel.catalog_detail"),
        };
    }

    /// <summary>
    /// FlexLib's unit vocabulary onto the model's. They overlap almost exactly;
    /// the model additionally carries units only we compute (LUFS, S units).
    /// </summary>
    private static MeterUnits TranslateUnits(Flex.Smoothlake.FlexLib.MeterUnits units) => units switch
    {
        Flex.Smoothlake.FlexLib.MeterUnits.Volts => MeterUnits.Volts,
        Flex.Smoothlake.FlexLib.MeterUnits.Amps => MeterUnits.Amps,
        Flex.Smoothlake.FlexLib.MeterUnits.Db => MeterUnits.Db,
        Flex.Smoothlake.FlexLib.MeterUnits.Dbfs => MeterUnits.Dbfs,
        Flex.Smoothlake.FlexLib.MeterUnits.Dbm => MeterUnits.Dbm,
        Flex.Smoothlake.FlexLib.MeterUnits.DegreesC => MeterUnits.DegreesC,
        Flex.Smoothlake.FlexLib.MeterUnits.DegreesF => MeterUnits.DegreesF,
        Flex.Smoothlake.FlexLib.MeterUnits.SWR => MeterUnits.Swr,
        Flex.Smoothlake.FlexLib.MeterUnits.Watts => MeterUnits.Watts,
        Flex.Smoothlake.FlexLib.MeterUnits.Percent => MeterUnits.Percent,
        _ => MeterUnits.None,
    };

    private void AllMetersCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshSourceChoices();
        LoadSelectedSlot();
        int n = _sourceChoices.Count;
        // #128: tone before the sentence — the checkbox is operator-facing
        // and the tone confirms the flip landed even if the count that
        // follows gets talked over.
        EarconPlayer.ToggleTone(AllMetersCheck.IsChecked == true);
        ScreenReaderOutput.Speak(
            Lexicon.Get(
                AllMetersCheck.IsChecked == true
                    ? "audio.meters.panel.showing_all"
                    : "audio.meters.panel.showing_common",
                ("count", n)),
            VerbosityLevel.Terse);
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        MeterSlot? slot = SelectedSlot;
        if (slot == null || SourceCombo.SelectedItem is not SourceChoice choice) return;

        // Name the slot with the SHORT name, not the browsing label. The slot
        // selector reads every entry aloud, and "Slice 0: LEVEL — S-Meter
        // Level (sounding)" is a sentence where a name belongs.
        string name = string.IsNullOrWhiteSpace(choice.ShortName) ? choice.Key : choice.ShortName;
        slot.Retarget(choice.Key, name, choice.Range.Clone(),
                      choice.Activation, choice.SliceIndex);
        UpdateSourceDetail(choice);

        // The slot's NAME changed, so the selector that lists slots by name is
        // now out of date. Same signal, one path.
        MeterToneEngine.NotifySlotContentChanged();
    }

    private void UpdateSourceDetail(SourceChoice? choice)
    {
        SourceDetailText.Text = choice?.Detail ?? "";
    }

    #endregion

    #region Pushing a slot into the controls

    /// <summary>Show the selected slot's settings. Never writes back.</summary>
    private void LoadSelectedSlot()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            MeterSlot? slot = SelectedSlot;
            if (slot == null)
            {
                SourceCombo.SelectedIndex = -1;
                UpdateSourceDetail(null);
                return;
            }

            MeterDefinition def = slot.Definition;

            int sourceIndex = _sourceChoices.FindIndex(c => Matches(c, def.Source));
            SourceCombo.SelectedIndex = sourceIndex;
            UpdateSourceDetail(sourceIndex >= 0 ? _sourceChoices[sourceIndex] : null);

            VoiceCombo.SelectedItem = def.VoiceName;
            if (VoiceCombo.SelectedIndex < 0 && VoiceCombo.Items.Count > 0)
                VoiceCombo.SelectedIndex = 0;

            ActivationCombo.SelectedIndex =
                Array.FindIndex(ActivationChoices, a => a.Value == def.Activation);

            PanSlider.Value = Math.Clamp(def.Pan, -1f, 1f) * 100.0;
            PanText.Text = DescribePan(def.Pan);

            VolumeSlider.Value = Math.Clamp(def.Volume, 0f, 1f) * 100.0;
            VolumeText.Text = Lexicon.Get("audio.meters.panel.percent",
                ("percent", ((int)Math.Round(VolumeSlider.Value)).ToString(CultureInfo.CurrentCulture)));

            PitchLowBox.Text = ((int)def.PitchLowHz).ToString(CultureInfo.CurrentCulture);
            PitchHighBox.Text = ((int)def.PitchHighHz).ToString(CultureInfo.CurrentCulture);
            SlotEnabledCheck.IsChecked = def.Enabled;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>
    /// Pan in words. A slider announces a bare number, and "minus 40" does not
    /// tell a listener which ear that is.
    /// </summary>
    private static string DescribePan(float pan)
    {
        int percent = (int)Math.Round(Math.Clamp(pan, -1f, 1f) * 100f);
        if (percent == 0) return Lexicon.Get("audio.meters.panel.pan_centre");
        int magnitude = Math.Abs(percent);
        bool left = percent < 0;
        // Whole phrases per side rather than a bare "left" / "right" word glued
        // onto a template. The side word alone is not reviewable, and languages
        // do not all put it last.
        if (magnitude >= 100)
            return Lexicon.Get(left
                ? "audio.meters.panel.pan_full_left"
                : "audio.meters.panel.pan_full_right");
        return Lexicon.Get(
            left ? "audio.meters.panel.pan_left" : "audio.meters.panel.pan_right",
            ("percent", magnitude.ToString(CultureInfo.CurrentCulture)));
    }

    #endregion

    #region Control handlers

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        MeterSlot? slot = SelectedSlot;
        if (slot != null && VoiceCombo.SelectedItem is string voiceName)
            slot.VoiceName = voiceName;
    }

    private void ActivationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        MeterSlot? slot = SelectedSlot;
        int i = ActivationCombo.SelectedIndex;
        if (slot != null && i >= 0 && i < ActivationChoices.Length)
            slot.Definition.Activation = ActivationChoices[i].Value;
    }

    private void PanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        float pan = (float)(e.NewValue / 100.0);
        PanText.Text = DescribePan(pan);
        if (_loading) return;
        MeterSlot? slot = SelectedSlot;
        if (slot != null) slot.Pan = pan;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VolumeText.Text = Lexicon.Get("audio.meters.panel.percent",
            ("percent", ((int)Math.Round(e.NewValue)).ToString(CultureInfo.CurrentCulture)));
        if (_loading) return;
        MeterSlot? slot = SelectedSlot;
        if (slot != null) slot.Volume = (float)(e.NewValue / 100.0);
    }

    private void PitchLowBox_LostFocus(object sender, RoutedEventArgs e)
    {
        MeterSlot? slot = SelectedSlot;
        if (slot == null) return;
        if (int.TryParse(PitchLowBox.Text, out int hz))
        {
            hz = Math.Clamp(hz, 100, 2000);
            slot.Definition.PitchLowHz = hz;
        }
        PitchLowBox.Text = ((int)slot.Definition.PitchLowHz).ToString(CultureInfo.CurrentCulture);
    }

    private void PitchHighBox_LostFocus(object sender, RoutedEventArgs e)
    {
        MeterSlot? slot = SelectedSlot;
        if (slot == null) return;
        if (int.TryParse(PitchHighBox.Text, out int hz))
        {
            hz = Math.Clamp(hz, 100, 4000);
            slot.Definition.PitchHighHz = hz;
        }
        PitchHighBox.Text = ((int)slot.Definition.PitchHighHz).ToString(CultureInfo.CurrentCulture);
    }

    private void SlotEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MeterSlot? slot = SelectedSlot;
        if (slot == null) return;

        bool on = SlotEnabledCheck.IsChecked == true;
        slot.Enabled = on;
        if (!on) slot.ToneProvider.Active = false;

        // #128: an operator-facing boolean answers back. At the handler, not
        // in MeterSlot.Enabled itself, because the engine also flips slots
        // programmatically (preset apply, config load) and those transitions
        // must stay silent — the #58 chime-storm rule.
        EarconPlayer.ToggleTone(on);

        // The selector lists each meter with its state, so it has to follow.
        MeterToneEngine.NotifySlotContentChanged();
    }

    private void AddSlotButton_Click(object sender, RoutedEventArgs e)
    {
        MeterSlot? slot = MeterToneEngine.AddSlot();
        if (slot == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.panel.max_reached"), VerbosityLevel.Terse);
            return;
        }

        // Select what was just added — the panel is a live view, so the slot
        // list has already rebuilt itself by the time this runs.
        _selectedSlotId = slot.Definition.Id;
        RefreshSlotList();
        ScreenReaderOutput.Speak(
            Lexicon.Get("audio.meters.panel.slot_added", ("number", MeterToneEngine.Slots.Count)),
            VerbosityLevel.Terse);
        SlotCombo.Focus();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteSelectedSlot();

    private void DeleteSelectedSlot()
    {
        var slots = MeterToneEngine.Slots;
        int index = SlotCombo.SelectedIndex;
        if (index < 0 || index >= slots.Count) return;

        if (slots.Count <= 1)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.panel.cannot_delete_only"), VerbosityLevel.Terse);
            return;
        }

        MeterDefinition def = slots[index].Definition;
        string name = string.IsNullOrWhiteSpace(def.Name) ? def.Source.Key : def.Name;

        // Home is a WPF panel hosted inside a WinForms window, so
        // Window.GetWindow(this) is NULL here — there is no WPF Window above us
        // to own the dialog. Passing that null to the owner overload throws, the
        // throw got swallowed, and Delete did nothing at all with no error and
        // no sound. Found by pressing the button on a real build; it compiled
        // and reviewed clean. Ask for an owner, use the ownerless overload when
        // there isn't one.
        string question = Lexicon.Get("audio.meters.panel.delete_question", ("name", name));
        string caption = Lexicon.Get("audio.meters.panel.delete_caption");
        Window? owner = Window.GetWindow(this);
        MessageBoxResult answer = owner != null
            ? MessageBox.Show(owner, question, caption, MessageBoxButton.YesNo,
                              MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(question, caption, MessageBoxButton.YesNo,
                              MessageBoxImage.Question, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.panel.slot_kept", ("name", name)), VerbosityLevel.Terse);
            SlotCombo.Focus();
            return;
        }

        // The slot is about to stop existing; anything it was previewing has to
        // stop with it.
        StopMeterTestTone();

        if (!MeterToneEngine.RemoveSlot(index))
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.panel.cannot_delete_only"), VerbosityLevel.Terse);
            return;
        }

        // Land on a neighbour rather than jumping to the top; the operator was
        // working somewhere in the list.
        var remaining = MeterToneEngine.Slots;
        int next = Math.Min(index, remaining.Count - 1);
        _selectedSlotId = next >= 0 ? remaining[next].Definition.Id : "";
        RefreshSlotList();

        ScreenReaderOutput.Speak(
            Lexicon.Get("audio.meters.panel.slot_deleted",
                ("name", name), ("remaining", remaining.Count)),
            VerbosityLevel.Terse);
        SlotCombo.Focus();
    }

    /// <summary>
    /// Play a two-second preview of the selected meter's tone.
    /// </summary>
    /// <remarks>
    /// The stop is UNCONDITIONAL (#131). It used to silence the tone only when
    /// meter tones were globally disabled — and the only way into this panel,
    /// Ctrl+M, ENABLED them, so the stop condition was guaranteed false and a
    /// test tone ran until the app closed. When meters are on, the engine's
    /// next reading for that slot reactivates the tone within about a tenth of
    /// a second, so stopping unconditionally costs a live meter nothing and
    /// costs a test tone everything it was missing.
    /// </remarks>
    /// <summary>
    /// The one timer that ends a preview, and the slot it will silence. ONE of
    /// each, deliberately: a timer created per click meant the second press of
    /// Test scheduled a second stop, and the FIRST stop then cut the second
    /// preview short — two seconds of tone became a tenth of a second, which
    /// reads as the button being broken. Restarting a single timer gives every
    /// press its own full two seconds.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _meterTestToneTimer;
    private MeterSlot? _meterTestToneSlot;

    /// <summary>
    /// This preview's entry in the running-cost register (#253), held only
    /// while it sounds.
    /// </summary>
    /// <remarks>
    /// The transient half of the register, and this is the right first one to
    /// build it on: the Test button is where a tone once ran until the
    /// application closed (#131), and the only reason anybody found out was
    /// that it was audible. The stop is unconditional now — but a register
    /// whose whole job is naming what is still going ought to be able to name
    /// this, and a stranded preview would now show up in the on-demand read
    /// instead of waiting to be noticed.
    /// </remarks>
    private IDisposable? _meterTestToneRegistration;

    /// <summary>
    /// Silence a preview immediately. Safe to call when nothing is playing, so
    /// every path that could strand a tone calls it unconditionally.
    /// </summary>
    private void StopMeterTestTone()
    {
        _meterTestToneTimer?.Stop();
        _meterTestToneRegistration?.Dispose();
        _meterTestToneRegistration = null;
        if (_meterTestToneSlot != null)
        {
            _meterTestToneSlot.ToneProvider.Active = false;
            _meterTestToneSlot = null;
        }
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        MeterSlot? slot = SelectedSlot;
        if (slot == null) return;

        // End whatever was already previewing before starting the next one.
        StopMeterTestTone();

        MeterDefinition def = slot.Definition;
        string name = string.IsNullOrWhiteSpace(def.Name) ? def.Source.Key : def.Name;
        ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.panel.testing_tone", ("name", name)), VerbosityLevel.Terse);

        slot.ToneProvider.Frequency = (def.PitchLowHz + def.PitchHighHz) / 2f;
        slot.ToneProvider.Volume = def.Volume * MeterToneEngine.MasterVolume;
        slot.ToneProvider.Voice = def.EffectiveVoice();
        slot.ToneProvider.Pan = def.Pan;
        slot.ToneProvider.Active = true;
        _meterTestToneSlot = slot;

        // Registered for as long as it sounds. No IsRunning predicate: unlike
        // every standing registrant there is no state anywhere to ask, so
        // "registered" IS "running" here, and the registration is released by
        // the same unconditional stop that silences the tone.
        DateTime startedUtc = DateTime.UtcNow;
        _meterTestToneRegistration = RunningCostRegister.Register(
            new RunningCost("meter-test-tone", "Meter test tone")
            {
                DescribeCost = () => Lexicon.Get("logging.running.tone_seconds",
                    ("count", Math.Max(0, (int)(DateTime.UtcNow - startedUtc).TotalSeconds)
                        .ToString(CultureInfo.CurrentCulture))),
                // Marshalled: the register can be asked to stop things from the
                // exit prompt, and the timer and tone provider behind this one
                // belong to the dispatcher.
                Stop = () => Dispatcher.Invoke(StopMeterTestTone),
                StopHow = "close the meters panel",
                Weight = RunningCostWeight.Routine
            });

        if (_meterTestToneTimer == null)
        {
            _meterTestToneTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _meterTestToneTimer.Tick += (s, args) => StopMeterTestTone();
        }

        _meterTestToneTimer.Stop();
        _meterTestToneTimer.Start();
    }

    private void SpeechIntervalBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SpeechIntervalBox.Text, out int val))
        {
            val = Math.Clamp(val, 1, 10);
            SpeechIntervalBox.Text = val.ToString(CultureInfo.CurrentCulture);
            MeterToneEngine.SpeechIntervalSeconds = val;
            MeterToneEngine.UpdateSpeechTimerInterval();
        }
    }

    /// <summary>
    /// The meters expander was the only one on Home with no expand or collapse
    /// earcon (#127). Same handlers as ScreenFieldsPanel: the earcon carries the
    /// state change, and the screen reader's own focus announcement carries the
    /// identity — speaking here as well would double-announce.
    /// </summary>
    private void MetersExpander_Expanded(object sender, RoutedEventArgs e) =>
        EarconPlayer.PlayExpand();

    private void MetersExpander_Collapsed(object sender, RoutedEventArgs e) =>
        EarconPlayer.PlayCollapse();

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    #endregion

    #region Panel-wide settings

    private void LoadGlobalsFromEngine()
    {
        AutoTuneCheck.IsChecked = MeterToneEngine.AutoEnableOnTune;
        SpeechTimerCheck.IsChecked = MeterToneEngine.SpeechTimerActive;
        SpeechIntervalBox.Text = MeterToneEngine.SpeechIntervalSeconds.ToString(CultureInfo.CurrentCulture);
        PeakWatcherCheck.IsChecked = MeterToneEngine.PeakWatcherEnabled;

        AutoTuneCheck.Checked += (s, e) => MeterToneEngine.AutoEnableOnTune = true;
        AutoTuneCheck.Unchecked += (s, e) => MeterToneEngine.AutoEnableOnTune = false;

        SpeechTimerCheck.Checked += (s, e) => MeterToneEngine.SpeechTimerActive = true;
        SpeechTimerCheck.Unchecked += (s, e) => MeterToneEngine.SpeechTimerActive = false;

        PeakWatcherCheck.Checked += (s, e) => MeterToneEngine.PeakWatcherEnabled = true;
        PeakWatcherCheck.Unchecked += (s, e) => MeterToneEngine.PeakWatcherEnabled = false;
    }

    /// <summary>
    /// Save current panel state to the AudioOutputConfig.
    /// Called when persisting settings.
    /// </summary>
    public void SaveToConfig(AudioOutputConfig config)
    {
        config.AutoEnableOnTune = MeterToneEngine.AutoEnableOnTune;
        config.MeterSpeechTimerActive = MeterToneEngine.SpeechTimerActive;
        config.MeterSpeechIntervalSeconds = MeterToneEngine.SpeechIntervalSeconds;
        config.PeakWatcherEnabled = MeterToneEngine.PeakWatcherEnabled;
        config.MeterTonesEnabled = MeterToneEngine.Enabled;
        config.Meters = MeterToneEngine.ExportDefinitions();
        config.MeterConfigVersion = MeterConfigMigration.CurrentVersion;
    }

    #endregion
}
