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
/// Audio Workshop: non-modal WPF dialog for TX audio sculpting, live meters,
/// and earcon exploration. Three tabs with real-time feedback.
/// </summary>
public partial class AudioWorkshopDialog : JJFlexDialog
{
    private FlexBase? _rig;
    private bool _polling;
    private readonly DispatcherTimer _meterTimer;

    // Singleton instance for non-modal Show()
    private static AudioWorkshopDialog? _instance;

    // ── Audio Check session (QB Track G, 2026-08-07) ──

    /// <summary>
    /// Live path to the PTT safety controller. Set by MainWindow when the
    /// controller is created; resolved per use because the controller is
    /// recreated on operator switch and nulled at power-off. EVERY keying
    /// path in this dialog rides the controller — the warning ladder, the
    /// license lockout, and the 15-minute hard kill all apply unchanged.
    /// Never set rig.Transmit directly from here.
    /// </summary>
    public static Func<PttSafetyController?>? PttControllerSource { get; set; }

    private AudioCheckSession? _session;
    private TextBox? _micReadingBox;
    private Button? _startCheckButton;
    private CycleFieldControl? _listenMethodControl;
    private CycleFieldControl? _checkPowerControl;
    private ValueFieldControl? _checkWattsControl;
    private Button? _playTakeButton;
    private Button? _loopbackButton;
    private TextBlock? _loopbackInfo;
    private CycleFieldControl? _micSourceControl;
    private GroupBox? _monitorHeader;

    /// <summary>
    /// The content panel of the section most recently opened by
    /// <see cref="AddSectionHeader"/>. Every control added while building a
    /// tab goes here rather than straight onto the tab's outer panel, which
    /// is what puts it inside a real group in the accessibility tree.
    /// Build order is strictly sequential, so this is always the section
    /// currently being filled.
    /// </summary>
    private StackPanel? _section;

    // ── Test tone (Audio Track C) ──

    /// <summary>
    /// Per-operator app settings store (AudioOutputConfig). Set by MainWindow
    /// when the config loads. The tone frequency/level/monitor persist here,
    /// NOT in the serial-keyed per-radio config — the frequency is an
    /// accessibility choice and hearing does not change when you switch rigs.
    /// </summary>
    public static Func<AudioOutputConfig?>? AudioConfigSource { get; set; }

    /// <summary>Immediate save of the app settings store. Set by MainWindow.</summary>
    public static Action? AudioConfigSave { get; set; }

    // ── This Computer section (2026-08-12) ──

    /// <summary>
    /// Opens the Audio Devices picker. Set by MainWindow, which forwards to
    /// the callback globals.vb owns. Resolved per call, never captured — the
    /// underlying callback is set during startup and this dialog can be
    /// constructed before that finishes.
    /// </summary>
    public static Action? OpenAudioDevices { get; set; }

    /// <summary>
    /// Full path to audioDevices.xml, so this dialog can NAME the chosen input
    /// device rather than offering a bare button. Set by MainWindow from the
    /// path globals.vb owns — a handoff, not a second place that knows how to
    /// build it, matching <see cref="MainWindow.AudioDevicesFilePath"/>.
    /// </summary>
    public static Func<string?>? AudioDevicesPath { get; set; }

    private TextBox? _deviceReadingBox;

    private CheckBox? _toneCheck;
    private CycleFieldControl? _tonePresetControl;
    private ValueFieldControl? _toneFreqControl;
    private ValueFieldControl? _toneLevelControl;
    private CheckBox? _toneMonitorCheck;
    private TextBlock? _toneInfo;
    private ContinuousToneSampleProvider? _toneMonitorProvider;
    private bool _toneMonitorSounding;
    private bool _toneOutsideWarned; // edge trigger: filter moved out from under an armed tone
    private static readonly int[] TonePresetHz = { 440, 700, 1000 }; // index 3 = Custom

    // Per-radio preferences (serial-keyed store). Loaded on SetRig.
    private RadioConfig? _radioCfg;
    private string _radioCfgSerial = "";


    // Preset callbacks (wired from outside).
    //
    // STATIC as of 2026-08-12, and that is the whole bug fix. They were
    // instance properties that nothing ever assigned: the dialog is
    // constructed in two places (ShowOrFocus and Settings' Audio Workshop
    // button) and neither wired them, so Load always answered "No presets
    // available" — the three built-in defaults included — and Save captured a
    // valid preset, handed it to a null callback, and announced "Preset saved"
    // over the top of dropping it on the floor. Every other cross-boundary
    // hook in this dialog (PttControllerSource, AudioConfigSource) is static
    // and wired once in MainWindow, which is why those work; these were the
    // odd ones out. The null-conditional invoke is what let it fail silently.
    //
    // Save RETURNS A BOOL, and that is not decoration. As an Action it could
    // fail — MainWindow no-ops when there is no operator to own the file — and
    // the dialog had no way to know, so every caller announced success
    // regardless. That is the same lying receipt in a second costume: the
    // first version dropped presets because the callback was null, this one
    // would drop them because the callback declined, and both said "saved".
    // A save that did not happen must never be announced as one.
    public static Func<AudioChainPresets>? GetPresetsCallback { get; set; }
    public static Func<AudioChainPresets, bool>? SavePresetsCallback { get; set; }

    // Microphone profile store (Track F, 2026-08-16). Same static-wired shape
    // as the preset callbacks above, for the same reason those are static —
    // and the same honest-save contract: the bool is whether the file landed.
    public static Func<MicrophoneProfileStore>? GetMicProfilesCallback { get; set; }
    public static Func<MicrophoneProfileStore, bool>? SaveMicProfilesCallback { get; set; }

    /// <summary>
    /// Persist the preset collection, reporting whether it actually landed.
    /// False when nothing is wired to save it or the store declined — never
    /// treat it as success. Speaks nothing itself: each caller knows what it
    /// was trying to do and says so in its own words.
    /// </summary>
    private static bool PersistPresets(AudioChainPresets presets)
    {
        return SavePresetsCallback?.Invoke(presets) ?? false;
    }

    /// <summary>
    /// What to say when a preset change could not be written. Names the cause
    /// rather than the symptom: with no operator loaded there is no per-operator
    /// file to write, and telling someone their preset "could not be saved" and
    /// stopping there gives them nothing to do about it.
    /// </summary>
    private static string PresetSaveFailed => Lexicon.Get("audio.preset.save_failed_reason");

    /// <summary>
    /// Category-list navigation (Sprint 32 Track G, task #134). Owns the list
    /// contents, the two-way sync with MainTabs, and Ctrl+Tab / Ctrl+Shift+Tab.
    /// </summary>
    private readonly CategoryNavigator _categories;

    public AudioWorkshopDialog()
    {
        InitializeComponent();

        // Non-modal: show in taskbar, allow resize, independent of main window.
        // Clear Owner so Alt+Tab works properly — owned windows steal focus
        // from their owner in WinForms/WPF interop.
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        new System.Windows.Interop.WindowInteropHelper(this).Owner = IntPtr.Zero;

        // Category navigation (Sprint 32 Track G, task #134): Ctrl+Tab and
        // Ctrl+Shift+Tab step categories from anywhere in the window, and the
        // list down the left names every one of them. It ENUMERATES the
        // TabControl, so a category added by any other track shows up here
        // with no edit to this file.
        _categories = CategoryNavigator.Attach(this, MainTabs, CategoryListBox);

        BuildTxAudioTab();
        ApplyTxAudioTabOrder();
        BuildLiveMetersTab();
        BuildEarconExplorerTab();
        BuildMeterInventorySection();   // inside Meters, below the readings
        BuildAmplifierTab();
        BuildDiagnosticsTab();   // Sprint 32 Track C

        // Every section exists now, so set radio-side availability for the rig
        // we already have (or do not have). SetRig covers every change after
        // this; without this line a Workshop opened while disconnected shows
        // its radio controls enabled until the next connect or disconnect.
        // It sits AFTER all three tab builders on purpose - it used to sit at
        // the end of BuildTxAudioTab, which would have left the Meters
        // sections un-gated because they enrol two builders later.
        UpdateRadioControlAvailability();

        // Meter poll timer at ~2 Hz
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _meterTimer.Tick += MeterTimer_Tick;

        Closed += (s, e) =>
        {
            // A session must never outlive its dialog: unkey (through the
            // controller), restore every changed state, stop playback.
            _session?.ForceEnd("Audio check ended");
            _session = null;
            // The test tone must never outlive its dialog either — closing
            // the workshop restores the microphone (the tone is armed only
            // while the workshop is open, and arming is never persisted).
            DisarmTone(speak: false);
            // Sprint 33 Track I: the same rule for the reference recording. An
            // armed reference that outlived its dialog would replace the
            // microphone at the next key-down with nothing on screen saying so.
            // DetachReferenceAudio also drops the recorder's event
            // subscriptions, which would otherwise go on relabelling a button
            // that no longer exists.
            DisarmReference(speak: false);
            DetachReferenceAudio();
            // And local take playback (#455): the player is static and would
            // otherwise keep sounding, and keep calling back into a button that
            // no longer exists, after the window is gone.
            DetachTakePlayback();
            _meterTimer.Stop();
            // The Core Audio endpoint and its volume callback must not
            // outlive the dialog that subscribed them.
            ReleasePcLevel();
            // Nor must the meter-inventory subscription: the inventory lives as
            // long as the rig, so a closed dialog left subscribed is a closed
            // dialog kept alive by the radio.
            BindMeterInventory(null);
            _instance = null;
        };
    }

    /// <summary>
    /// Two-stage Escape while an Audio Check is transmitting: the first press
    /// unkeys ("Transmit off") and STAYS in the dialog; the second press
    /// closes it. Escape never leaves you transmitting — this extends the
    /// house Escape rule rather than bending it. Class handler runs before
    /// JJFlexDialog's instance handler, so we can consume the first press.
    ///
    /// Workshop-local document keys (Noel, 2026-08-11): Ctrl+S saves a
    /// preset, Ctrl+O loads one — standard document verbs, learnable
    /// because universal. Ctrl+S also fixes a live defect: Save Preset
    /// used to answer to its Alt+S button mnemonic, which (WPF access keys
    /// match with Shift held) shadowed the GLOBAL Alt+Shift+S Speak
    /// Transmit Status chord in the one dialog where an operator most
    /// needs to query their audio. Ctrl+Enter starts or stops the Audio
    /// Check from anywhere in the dialog, so the adjust-and-hear loop
    /// never requires hunting the button. All three are LOCAL to this
    /// dialog — none is a global binding — and each requires exactly the
    /// Ctrl modifier so chords like Ctrl+Shift+S pass through untouched.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _session != null && _session.EscapeStopsTransmit)
        {
            _session.StopCheck();
            e.Handled = true;
            return;
        }
        // F6 / Shift+F6 — move between sections. The Windows convention for
        // crossing panes within a window, and the answer to a gap that has
        // been open since the sections were built.
        //
        // Noel asked for section navigation twice. The first answer
        // (2026-08-12) correctly rejected Ctrl+Tab, which already switches
        // tabs here, and then stopped -- so the need went unmet rather than
        // resolved. The second attempt (2026-08-13) gave the sections
        // AutomationProperties.HeadingLevel expecting NVDA's H key to jump
        // between them. That could never work: single-letter navigation lives
        // in BROWSE mode, for web pages and documents, and a WPF dialog runs
        // in focus mode where H simply types the letter. Containment
        // announcements landed; jump navigation was never possible that way.
        //
        // F6 has no such problem: it is a real key, handled here, and an
        // operator who knows Windows may already reach for it.
        if (e.Key == Key.F6)
        {
            MoveToSection(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S:
                    SavePreset_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.O:
                    LoadPreset_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.Enter:
                    ToggleAudioCheck();
                    e.Handled = true;
                    return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Initial focus lands on Start Audio Check (Noel, 2026-08-11): set up,
    /// or just loaded a profile? Press Enter and you are running — zero
    /// navigation for the common case. The tab ring then runs Start,
    /// mic reading, Mic Gain (Threads Track, 2026-08-12): during a check
    /// focus sits on Mic Gain, so the reading is one Shift+Tab back and
    /// the Stop button one more. Falls back to the base first-control
    /// behaviour when the workshop opens on another tab, where the button
    /// isn't visible to take focus.
    ///
    /// Exception added 2026-08-12 with the walk-through reorder: when no
    /// input device has been chosen on this computer, focus lands on step
    /// one instead. Those are the only two states worth optimising for and
    /// they do not overlap — an operator with no microphone selected cannot
    /// run a meaningful check, and landing them on a button that keys the
    /// transmitter is the least useful thing this dialog could do. Everyone
    /// else gets the express lane, unchanged. This is deliberately NOT a
    /// preference: it reads the one fact that distinguishes the two cases.
    /// </summary>
    protected override void FocusFirstControl()
    {
        // ALWAYS the category list, whichever category you arrived on.
        //
        // Until 2026-08-25 opening the Workshop plainly took you straight to
        // Start Audio Check — an express lane that made sense while this was
        // the ONLY place to check your audio. The Fixer Tool now owns that
        // job, so the shortcut had become a second door to a thing that has
        // its own door, and landing on it told you nothing about where you
        // were. Noel, on opening it with both surfaces present: "it is
        // confusing to me looking at it now with the fix tool being
        // available."
        //
        // The category list says which category you are in, which is what an
        // operator arriving anywhere needs first — whether they came by menu,
        // by key, or by a deep link from somewhere else.
        //
        // Dropped with it: the special case that put you on the device
        // reading when no input was chosen. That was help for running a
        // CHECK, and the check is the Fixer's now. If it turns out to be
        // missed, it belongs in the Fixer's audio-setup stage rather than
        // back here.
        if (_categories.FocusSelectedCategory()) return;
        base.FocusFirstControl();
    }

    /// <summary>
    /// Explicit tab order across the three transmit categories (Threads Track,
    /// 2026-08-12): Start Audio Check first, the live mic reading second, Mic
    /// Gain third, then every remaining control in build order. A running
    /// check is an adjust-and-listen loop between exactly those three stops:
    /// forward tab does things, backward tab inspects what just happened.
    ///
    /// Mic Gain used to sit VISUALLY in the Microphone section and join this
    /// cluster only in the ring. When TX Audio split into three categories on
    /// 2026-08-25 that stopped being possible — Tab does not cross a category
    /// — so the gain moved to Hear Yourself and now sits beside the reading in
    /// both senses. The ring and the layout finally agree.
    ///
    /// Noel also asked about Ctrl+Tab section navigation. Deliberately NOT
    /// added: Ctrl+Tab moves between CATEGORIES in this window, and
    /// overloading it for section movement inside one would collide with that.
    /// Sections move on F6 / Shift+F6. (Ctrl+Tab used to be the WPF
    /// TabControl's own default behaviour; since Sprint 32 Track G it is
    /// handled explicitly by CategoryNavigator, which also moves focus to the
    /// category list so the arrival is announced. Same key, same meaning, no
    /// longer inherited.)
    /// </summary>
    private void ApplyTxAudioTabOrder()
    {
        int idx = 1;
        if (_startCheckButton != null) KeyboardNavigation.SetTabIndex(_startCheckButton, idx++);
        if (_micReadingBox != null) KeyboardNavigation.SetTabIndex(_micReadingBox, idx++);
        if (_micGainControl != null) KeyboardNavigation.SetTabIndex(_micGainControl, idx++);
        // The PC-source stand-ins take the very next slots. Only one of the
        // two gain controls is ever visible — Mic Gain for the jack, the
        // Windows input level for PC audio — so whichever applies sits third
        // in the ring: the express lane follows the gain that actually works,
        // not a particular control (Track PC Gain, 2026-08-13).
        if (_pcLevelControl != null) KeyboardNavigation.SetTabIndex(_pcLevelControl, idx++);
        if (_pcLevelNote != null) KeyboardNavigation.SetTabIndex(_pcLevelNote, idx++);
        // One panel until 2026-08-25, three now. Every one gets numbered, in
        // the order an operator walks them: what this computer does, what the
        // radio does, then listening to the result. Numbering only the first
        // would leave the other two on declaration order, which is where the
        // express lane above would quietly stop mattering.
        ApplyTabOrderWithin(ThisComputerContent, ref idx);
        ApplyTabOrderWithin(TransmitSettingsContent, ref idx);
        ApplyTabOrderWithin(HearYourselfContent, ref idx);
    }

    /// <summary>
    /// Number every control in a panel, descending into sections.
    /// </summary>
    /// <remarks>
    /// This walked one level deep until 2026-08-13, which was correct while
    /// sections were flat headers in a single panel. Now that a section is a
    /// GroupBox holding its own panel, a one-level walk would hand a single
    /// index to an entire section and leave every control inside it
    /// unnumbered — the express lane would survive and the rest of the ring
    /// would fall back to declaration order.
    /// </remarks>
    private void ApplyTabOrderWithin(Panel panel, ref int idx)
    {
        foreach (object child in panel.Children)
        {
            if (child is not UIElement el) continue;

            if (el is GroupBox group)
            {
                // The frame itself is not a stop: entering the group is what
                // the screen reader announces, and a tab stop on the border
                // would be a keypress that does nothing. Continue keeps the
                // ring flat across sections rather than trapping it inside
                // one.
                KeyboardNavigation.SetTabNavigation(group, KeyboardNavigationMode.Continue);
                if (group.Content is Panel inner) ApplyTabOrderWithin(inner, ref idx);
                continue;
            }

            if (ReferenceEquals(el, _startCheckButton) || ReferenceEquals(el, _micReadingBox)
                || ReferenceEquals(el, _micGainControl) || ReferenceEquals(el, _pcLevelControl)
                || ReferenceEquals(el, _pcLevelNote)) continue;
            KeyboardNavigation.SetTabIndex(el, idx++);
        }
    }

    /// <summary>
    /// The sections of the tab currently on screen, in visual order, skipping
    /// any that are collapsed.
    /// </summary>
    /// <remarks>
    /// Collapsed sections must be skipped rather than counted: the Microphone
    /// section's contents change with the transmit source and TX Monitor can
    /// be hidden, so a fixed index would land the operator on nothing.
    ///
    /// <para>The panel is DISCOVERED from the selected category rather than
    /// looked up by index (Sprint 32 Track G). It was a switch on
    /// <c>MainTabs.SelectedIndex</c> naming 0, 1 and 2 — correct for exactly
    /// the three categories that existed when it was written, and silently
    /// wrong for the fourth. Sprint 32 added several: every one of them would
    /// have fallen through to <c>_ => null</c>, and F6 would have done nothing
    /// at all on the new categories while continuing to work perfectly on the
    /// old ones, which is the hardest kind of gap to notice.</para>
    /// </remarks>
    private List<GroupBox> VisibleSections()
    {
        var found = new List<GroupBox>();
        if (SelectedCategoryPanel() is not Panel panel) return found;

        foreach (object child in panel.Children)
        {
            if (child is GroupBox g && g.Visibility == Visibility.Visible)
                found.Add(g);
        }
        return found;
    }

    /// <summary>
    /// The content panel of whichever category is showing, unwrapping the
    /// ScrollViewer every category is built inside.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow: it returns the category's own top-level panel, so
    /// a "section" stays what it has always been — a GroupBox sitting directly
    /// in that panel — rather than every nested GroupBox anywhere below. A
    /// deeper walk would quietly change what F6 counts as a section.
    /// </remarks>
    private Panel? SelectedCategoryPanel()
    {
        object? content = (MainTabs.SelectedItem as TabItem)?.Content;

        // Unwrap the containers a category is likely to be built inside. Every
        // category today is ScrollViewer > StackPanel, but three other tracks
        // added categories this sprint and none of them had to know that, so
        // this tolerates a Border or another ContentControl in the way rather
        // than returning null and leaving F6 silently dead on their tabs.
        for (int depth = 0; depth < 4 && content is not Panel; depth++)
        {
            content = content switch
            {
                ScrollViewer sv => sv.Content,
                Border b => b.Child,
                ContentControl cc => cc.Content,
                Decorator d => d.Child,
                _ => null,
            };
            if (content == null) break;
        }
        return content as Panel;
    }

    /// <summary>
    /// Move focus to the first focusable control of the next or previous
    /// section, wrapping at the ends, and say which section that is.
    /// </summary>
    /// <remarks>
    /// Announcing is not optional. Landing silently in a new group is the same
    /// failure the GroupBox work fixed this morning -- the operator would have
    /// moved somewhere and not been told where. A screen reader announces a
    /// group when focus ENTERS it by tabbing, but a programmatic focus change
    /// inside the same window is not reliably narrated, so this says it.
    ///
    /// <para>
    /// This is the legitimate case for app speech under
    /// feedback_speak_only_when_ui_does_not_convey: the operator asked to move,
    /// and where they landed is information no control on screen carries.
    /// </para>
    /// </remarks>
    private void MoveToSection(bool forward)
    {
        var sections = VisibleSections();
        if (sections.Count == 0) return;

        // Where are we now? The section containing focus, or -1 if focus is
        // somewhere else entirely (the preset toolbar, the tab strip).
        int current = -1;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].IsKeyboardFocusWithin) { current = i; break; }
        }

        int next;
        if (current < 0)
        {
            // Not in a section yet: forward starts at the first, backward at
            // the last, so both directions do something useful on first press.
            next = forward ? 0 : sections.Count - 1;
        }
        else
        {
            next = forward ? current + 1 : current - 1;
            if (next >= sections.Count) next = 0;
            else if (next < 0) next = sections.Count - 1;
        }

        GroupBox target = sections[next];
        string name = target.Header as string ?? Lexicon.Get("audio.workshop.unnamed_section");

        if (!target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            // A section with nothing focusable in it -- possible if every
            // control inside is collapsed. Say so rather than appearing to do
            // nothing, and leave focus where it was.
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.workshop.section_nothing_to_adjust", ("section", name)),
                VerbosityLevel.Terse, true);
            return;
        }

        ScreenReaderOutput.Speak(name, VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Show or bring to front the singleton Audio Workshop dialog.
    /// </summary>
    public static void ShowOrFocus(FlexBase? rig, int tabIndex = 0)
    {
        if (_instance == null || !_instance.IsLoaded)
        {
            _instance = new AudioWorkshopDialog();
            _instance.SetRig(rig);
            _instance.Show();
            // Non-modal WPF windows in a WinForms app don't receive keyboard input
            // without this — the WinForms message loop doesn't route keys to WPF.
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_instance);
        }
        _instance.FocusTab(tabIndex);
        _instance.Activate();
    }

    /// <summary>
    /// Show or bring to front the Workshop, opened at the named category.
    /// Falls back to the first category if the header does not match, so a
    /// renamed category costs the caller its landing spot and nothing more.
    /// </summary>
    public static void ShowOrFocus(FlexBase? rig, string header)
    {
        ShowOrFocus(rig, 0);
        _instance?.FocusTabByHeader(header);
    }

    /// <summary>
    /// Enable or disable the RADIO-side controls to match connection state.
    ///
    /// With no radio these toggles did nothing but say so unconvincingly:
    /// SetToggle returns at `_rig == null` BEFORE it plays the earcon, so the
    /// operator got the screen reader's confident "checked", no tone, and no
    /// action. Reported 2026-08-18. A control that cannot act costs a keyboard
    /// operator a tab stop to discover that, and tells a screen reader user
    /// something untrue.
    ///
    /// IsEnabled=false does both jobs at once: WPF drops the control from the
    /// tab order, and UIA reports it as unavailable, so a review cursor that
    /// lands on it still learns the feature exists and why it is out of reach.
    /// That preserves discoverability, which is why this is not simply hiding
    /// them.
    ///
    /// Only RADIO-side controls. The Workshop is deliberately usable offline -
    /// PC Neural NR, the noise profiles, the microphone check and the PC
    /// cleanup chain all process audio on this computer, and the mic check
    /// exists precisely so an operator can prove their input works WITHOUT
    /// involving a radio. (The TEST TONE is not in that set, despite an
    /// earlier reading of this comment: arming it writes to the radio's own
    /// transmit chain through rig.TxToneFrequency, so it is radio-side.)
    ///
    /// Sprint 30 Track A — this used to name five checkboxes and stop there,
    /// which fixed half the problem. The radio-side VALUE controls were left
    /// tabbable with handler-only guards, and ValueFieldControl cheerfully
    /// speaks a changing value on every arrow key with no rig attached: the
    /// same confident lie the checkbox fix killed, one control type over.
    /// It now works from a registry that whole SECTIONS enrol in
    /// (<see cref="AddRadioSection"/>) rather than an enumeration of controls,
    /// so a control added to a radio-side section later inherits the rule
    /// instead of quietly missing it. Disabling the GroupBox cascades to every
    /// child without touching each child's own IsEnabled, so state a section
    /// manages for its own reasons (the loopback button's model gate, the
    /// tone's passband gate) survives untouched and reappears on connect.
    /// </summary>
    private void UpdateRadioControlAvailability()
    {
        bool live = _rig != null;

        foreach (var element in _radioOnlyElements)
        {
            element.IsEnabled = live;
        }
    }

    /// <summary>
    /// Everything that cannot act without a radio: whole sections enrolled by
    /// <see cref="AddRadioSection"/>, plus the individual controls in sections
    /// that are genuinely mixed (the Microphone section holds the radio's mic
    /// source, gain, boost and bias alongside the Windows input level, which
    /// is the PC's own and valid offline).
    /// </summary>
    private readonly List<UIElement> _radioOnlyElements = new();

    public void SetRig(FlexBase? rig)
    {
        var oldRig = _rig;
        _rig = rig;
        UpdateRadioControlAvailability();
        if (rig != null)
        {
            // Ask for the TX equalizer early so a preset capture later in
            // this session has the answer in hand (Track F: presets were
            // shipping without the EQ because nothing ever asked for it).
            rig.RequestTxEqualizer();
            LoadPerRadioPrefs();
            // The store is operator-scoped, but a rig change is also when an
            // operator switch lands here — cheap to re-read either way.
            RefreshMicProfileOptions();
            LoadToneSettings();
            // Reflect the new rig's actual tone state (a fresh rig is never
            // armed — arming does not survive a radio switch by design).
            SetToneCheckSilently(rig.TxToneEngaged);
            UpdateToneStatus(speakIfNewlyOutside: false);
            UpdateLoopbackAvailability();
            PollTxAudio();
            // A different rig's last readings must not sit in the meter boxes
            // until the first poll of the new one — now that the boxes are
            // focusable, a review command could read the previous radio's
            // numbers as this one's.
            if (!ReferenceEquals(oldRig, rig))
                ResetMeterReadings("no reading yet");
            _meterTimer.Start();
        }
        else
        {
            // Radio gone: end any live session (nothing to restore on a dead
            // radio — the session skips rig writes when the rig is null).
            _session?.ForceEnd("Radio disconnected, audio check ended");
            _session = null;
            // And release the test tone (the generator died with the rig, but
            // the static key-down hook and local monitor are ours to clear).
            DisarmTone(speak: false, rig: oldRig);
            SetToneCheckSilently(false);
            // And the reference player, for the same reason: the player died
            // with the rig, but the arm box is ours and must not go on
            // claiming a recording is armed against a radio that is gone.
            DisarmReference(speak: false, rig: oldRig);
            // Clear a stale loopback arrangement flag on the departing rig so
            // a reconnect on the same FlexBase can arrange again. Writes are
            // internally guarded when the underlying radio is gone.
            if (oldRig != null && oldRig.LoopbackArranged)
                oldRig.EndLoopbackArrangement();
            _meterTimer.Stop();
            // The poll is dead now — leave the readings honest, not stale.
            UpdateMicReading();
            ResetMeterReadings("no radio connected");
            // A warning about a radio that is gone is a warning about nothing.
            UpdateSilentTxNote();
        }

        // Follow the new rig's meter inventory (or stop following the old one).
        // Both branches, because a departing radio's meter list is exactly as
        // wrong to leave on screen as a departing radio's readings.
        BindMeterInventory(rig);
    }

    /// <summary>
    /// Command Finder path for "check my transmit audio": open (or focus) the
    /// workshop and start an Audio Check session immediately.
    /// </summary>
    public static void ShowOrFocusAndStartCheck(FlexBase? rig)
    {
        // BY NAME, not index 0. Until 2026-08-25 the Audio Check lived on the
        // first category, so opening at index 0 and starting a check put the
        // operator where the check was. The three-way split moved it to Hear
        // Yourself, and index 0 is now This Computer — so this would have
        // started a check the operator could not see the controls for, which
        // for a keyed transmit is worse than merely wrong.
        ShowOrFocus(rig, "Hear Yourself");
        _instance?.ToggleAudioCheck();
    }

    /// <summary>
    /// Load per-radio Audio Check preferences (listen method, low power) from
    /// the serial-keyed RadioConfig store and reflect them in the controls.
    /// </summary>
    private void LoadPerRadioPrefs()
    {
        if (_rig == null) return;
        string serial = _rig.SelectedRadioSerial;
        if (string.IsNullOrEmpty(serial)) return;
        if (_radioCfg != null && _radioCfgSerial == serial) return;

        _radioCfg = RadioConfig.LoadForRadio(serial);
        _radioCfgSerial = serial;

        if (_listenMethodControl != null)
        {
            _listenMethodControl.SuppressEvents = true;
            _listenMethodControl.SelectedIndex = (int)_radioCfg.AudioCheckListenMethod;
            _listenMethodControl.SuppressEvents = false;
        }
        if (_checkPowerControl != null)
        {
            _checkPowerControl.SuppressEvents = true;
            _checkPowerControl.SelectedIndex = (int)_radioCfg.AudioCheckPowerMode;
            _checkPowerControl.SuppressEvents = false;
        }
        if (_checkWattsControl != null)
        {
            _checkWattsControl.SuppressEvents = true;
            _checkWattsControl.Value = Math.Clamp(_radioCfg.AudioCheckLowPowerWatts, 1, 100);
            _checkWattsControl.SuppressEvents = false;
        }
        UpdateCheckWattsVisibility();
    }

    private void SavePerRadioPrefs()
    {
        if (_radioCfg == null || string.IsNullOrEmpty(_radioCfgSerial)) return;
        if (_listenMethodControl != null)
            _radioCfg.AudioCheckListenMethod = (AudioCheckListenMethods)_listenMethodControl.SelectedIndex;
        if (_checkPowerControl != null)
            _radioCfg.AudioCheckPowerMode = (AudioCheckPowerModes)_checkPowerControl.SelectedIndex;
        if (_checkWattsControl != null)
            _radioCfg.AudioCheckLowPowerWatts = _checkWattsControl.Value;
        _radioCfg.SaveForRadio(_radioCfgSerial);
    }

    /// <summary>
    /// The watts field only exists while Low power is the selected check
    /// mode — a collapsed control is out of the tab order (house rule for
    /// controls that currently do nothing).
    /// </summary>
    private void UpdateCheckWattsVisibility()
    {
        if (_checkWattsControl == null) return;
        bool lowPower = _checkPowerControl?.SelectedIndex == (int)AudioCheckPowerModes.LowPower;
        _checkWattsControl.Visibility = lowPower ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Radio teardown notification. The workshop is a non-modal singleton that
    /// outlives the radio: before this hook, nothing stopped the 2 Hz poll
    /// timer when the rig died, and the tick raced Disconnect() nulling
    /// theRadio (crash zip JJFlexError-20260807-153513, NRE in get_MicGain
    /// during app close). MainWindow's power-off path calls this; safe from
    /// any thread, no-op when the dialog isn't open.
    /// </summary>
    public static void NotifyRigGone()
    {
        var inst = _instance;
        if (inst == null) return;
        if (inst.Dispatcher.CheckAccess())
            inst.SetRig(null);
        else
            inst.Dispatcher.BeginInvoke(() => inst.SetRig(null));
    }

    public void FocusTab(int tabIndex)
    {
        if (tabIndex >= 0 && tabIndex < MainTabs.Items.Count)
            MainTabs.SelectedIndex = tabIndex;
    }

    /// <summary>
    /// Select a category by its header. Returns false, quietly, if no category
    /// carries that header.
    /// </summary>
    /// <remarks>
    /// <b>The reason to prefer this over <see cref="FocusTab"/>.</b> Menu items
    /// opened this window at a hard-coded INDEX, which is correct exactly until
    /// somebody adds or removes a category — and then the menu silently opens
    /// the wrong one. The meter merge of 2026-08-25 removed a category and left
    /// "Earcon Explorer" pointing at index 2, which happened to still be the
    /// Earcon Explorer. Being right by luck is the state this whole audit keeps
    /// finding. SettingsDialog has had SelectTabByHeader for the same reason.
    /// </remarks>
    public bool FocusTabByHeader(string header)
    {
        foreach (object item in MainTabs.Items)
        {
            if (item is System.Windows.Controls.TabItem tab
                && string.Equals(tab.Header as string, header,
                                 StringComparison.OrdinalIgnoreCase))
            {
                MainTabs.SelectedItem = tab;
                return true;
            }
        }
        return false;
    }

}
