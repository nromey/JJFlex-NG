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

    // ── Shell hooks (QB Track G, 2026-08-07) ──

    /// <summary>
    /// Live path to the PTT safety controller. Set by MainWindow when the
    /// controller is created; resolved per use because the controller is
    /// recreated on operator switch and nulled at power-off. EVERY keying
    /// path rides the controller — the warning ladder, the license lockout,
    /// and the 15-minute hard kill all apply unchanged. Never set
    /// rig.Transmit directly. Read by Check your audio, the Fixer and the
    /// voice-message sender as well as here, which is why it stayed put when
    /// the check moved out (#835).
    /// </summary>
    public static Func<PttSafetyController?>? PttControllerSource { get; set; }

    /// <summary>
    /// Is an Audio Check active? The check lives in Check your audio since
    /// #835; this forwards there, so the dialog transmit door's wiring in
    /// MainWindow (<c>DialogTransmitKeys.SessionOwnsTransmitter</c>) did not
    /// have to move with it.
    /// </summary>
    internal static bool AnAudioCheckIsActive => CheckYourAudioDialog.AnAudioCheckIsActive;

    private CycleFieldControl? _micSourceControl;
    private GroupBox? _monitorHeader;


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

    /// <summary>
    /// Mic Gain and the Windows input level, in the Microphone section. The same
    /// implementation Check your audio hosts for its adjust-while-listening loop,
    /// bound to the same two settings (#835).
    /// </summary>
    private readonly TransmitGainControls _gain;


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

        _gain = new TransmitGainControls(Dispatcher, () => _rig, () => _polling,
            () => AudioDevicesPath?.Invoke(), host: "Audio Workshop");
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
            // The check, the test tone, the reference recording and take
            // playback lived here until #835 and each had its teardown in this
            // handler; they live in Check your audio now, whose own Closed
            // handler carries them. What is left here is the Workshop's own.
            CloseBenchTools();
            _meterTimer.Stop();
            // The Core Audio endpoint and its volume callback must not
            // outlive the dialog that subscribed them.
            _gain.Dispose();
            // Nor must the meter-inventory subscription: the inventory lives as
            // long as the rig, so a closed dialog left subscribed is a closed
            // dialog kept alive by the radio.
            BindMeterInventory(null);
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    /// <summary>
    /// Workshop-local document keys (Noel, 2026-08-11): Ctrl+S saves a
    /// preset, Ctrl+O loads one — standard document verbs, learnable
    /// because universal. Ctrl+S also fixes a live defect: Save Preset
    /// used to answer to its Alt+S button mnemonic, which (WPF access keys
    /// match with Shift held) shadowed the GLOBAL Alt+Shift+S Speak
    /// Transmit Status chord in the one dialog where an operator most
    /// needs to query their audio. Both are LOCAL to this dialog — neither
    /// is a global binding — and each requires exactly the Ctrl modifier so
    /// chords like Ctrl+Shift+S pass through untouched.
    ///
    /// <para>Ctrl+Enter started and stopped the Audio Check here, and Escape
    /// had a first stage that unkeyed it, until #835 moved the check into its
    /// own window. Both went with it: Check your audio has them now, and
    /// nothing in this window transmits any more.</para>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // A detuned render playing from Diagnostics stops with Escape before
        // the window closes, as take playback always has.
        if (e.Key == Key.Escape && RecordingPlayer.IsPlaying) RecordingPlayer.Stop();
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
    /// Explicit tab order across the two transmit categories, in the order an
    /// operator walks them: what this computer does, then what the radio does.
    /// </summary>
    /// <remarks>
    /// This carried an "express lane" until #835 — Start Audio Check, the live
    /// mic reading and Mic Gain numbered first, so a running check was three
    /// adjacent stops. The check and its reading moved to Check your audio,
    /// which keeps that loop in its own layout, and Mic Gain came back to the
    /// Microphone section here, where it lived before 2026-08-25.
    ///
    /// <para>Sections move on F6 / Shift+F6, never on Ctrl+Tab, which moves
    /// between CATEGORIES in this window (CategoryNavigator).</para>
    /// </remarks>
    private void ApplyTxAudioTabOrder()
    {
        int idx = 1;
        ApplyTabOrderWithin(ThisComputerContent, ref idx);
        ApplyTabOrderWithin(TransmitSettingsContent, ref idx);
    }

    /// <summary>
    /// Number every control in a panel, descending into sections.
    /// </summary>
    /// <remarks>
    /// This walked one level deep until 2026-08-13, which was correct while
    /// sections were flat headers in a single panel. Now that a section is a
    /// GroupBox holding its own panel, a one-level walk would hand a single
    /// index to an entire section and leave every control inside it
    /// unnumbered.
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
    // The section list, the panel unwrap and the move now live in
    // SectionNavigator, shared with Settings since #822 (2026-10-25). The
    // remarks above still describe what they do.
    private List<GroupBox> VisibleSections() => SectionNavigator.Sections(MainTabs);

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
    private Panel? SelectedCategoryPanel() => SectionNavigator.TabPanel(MainTabs);

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
    private void MoveToSection(bool forward) => SectionNavigator.Move(MainTabs, forward);

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
        else if (!ReferenceEquals(_instance._rig, rig)) _instance.SetRig(rig);
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
    /// The Workshop's door to Check your audio, which was its Hear Yourself
    /// category until #835 (ruled by Noel 2026-10-27 13:57: "one surface with
    /// several doors"). It opens the window; it never starts a check.
    /// </summary>
    private void CheckYourAudio_Click(object sender, RoutedEventArgs e) =>
        CheckYourAudioDialog.ShowOrFocus(_rig);

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
    /// manages for its own reasons survives untouched and reappears on connect.
    /// </summary>
    private void UpdateRadioControlAvailability() => _sections.SetRadioAvailable(_rig != null);

    public void SetRig(FlexBase? rig)
    {
        var oldRig = _rig;
        if (!ReferenceEquals(oldRig, rig)) BenchToolsRigChanging(rig);
        _rig = rig;
        UpdateRadioControlAvailability();
        if (rig != null)
        {
            // Ask for the TX equalizer early so a preset capture later in
            // this session has the answer in hand (Track F: presets were
            // shipping without the EQ because nothing ever asked for it).
            rig.RequestTxEqualizer();
            // The store is operator-scoped, but a rig change is also when an
            // operator switch lands here — cheap to re-read either way.
            RefreshMicProfileOptions();
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
            // The check, the tone and the reference were torn down here until
            // #835; Check your audio follows the radio itself now.
            _meterTimer.Stop();
            // The poll is dead now — leave the readings honest, not stale.
            ResetMeterReadings("no radio connected");
            // A warning about a radio that is gone is a warning about nothing.
            UpdateSilentTxNote();
        }

        // Follow the new rig's meter inventory (or stop following the old one).
        // Both branches, because a departing radio's meter list is exactly as
        // wrong to leave on screen as a departing radio's readings.
        BindMeterInventory(rig);
        UpdateBenchToolsAvailability();
    }

    public static void NotifyRigReady(FlexBase rig) =>
        DialogSections.PostRigChange(_instance, w => w.SetRig(rig), onlyIfLoaded: true);

    /// <summary>
    /// Radio teardown notification. The workshop is a non-modal singleton that
    /// outlives the radio: before this hook, nothing stopped the 2 Hz poll
    /// timer when the rig died, and the tick raced Disconnect() nulling
    /// theRadio (crash zip JJFlexError-20260807-153513, NRE in get_MicGain
    /// during app close). MainWindow's power-off path calls this; safe from
    /// any thread, no-op when the dialog isn't open.
    /// </summary>
    public static void NotifyRigGone() =>
        DialogSections.PostRigChange(_instance, w => w.SetRig(null), onlyIfLoaded: false);

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
