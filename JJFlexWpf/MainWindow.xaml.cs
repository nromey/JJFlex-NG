using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Bridge interface for LogPanel commands — allows KeyCommands to call
/// LogPanel methods through WpfMainWindow without direct type dependency.
/// The main app implements this on its LogPanel and sets the LoggingLogPanel property.
/// </summary>
public interface ILogPanelCommands
{
    void FocusField(string fieldName);
    void NewEntry();
    bool WriteEntry();
}

/// <summary>
/// JJFlexRadio main window — WPF replacement for WinForms Form1.
/// Sprint 8: Full WPF conversion, code-behind pattern (not MVVM).
///
/// Layout:
///   DockPanel
///     ├─ Menu (Top)           — 3 mode-specific menu sets (Classic/Modern/Logging)
///     ├─ StatusBar (Bottom)   — Radio status fields (Power, Memories, Scan, Knob, LogFile)
///     └─ Grid (Fill)
///          Row 0: RadioControlsPanel  — Frequency, mode, tuner, TX/antenna buttons
///          Row 1: ContentArea         — Received/Sent text, rig fields display
///          Row 2: LoggingPanel        — Logging Mode overlay (collapsed by default)
/// </summary>
public partial class MainWindow : UserControl
{
    /// <summary>
    /// Flag to prevent re-entrant close attempts (mirrors Form1.Ending).
    /// </summary>
    private bool _isClosing;

    /// <summary>
    /// Callback to route UI mode changes to the WinForms MenuStripBuilder.
    /// Set by ShellForm constructor after building menus.
    /// </summary>
    public Action<UIMode>? MenuModeCallback { get; set; }

    /// <summary>
    /// Callback to persist UI mode changes to the operator profile.
    /// Set by ApplicationEvents.vb — routes to globals.ActiveUIMode setter.
    /// </summary>
    public Action<UIMode>? SaveUIModeCallback { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        Loaded += MainWindow_Loaded;

        // Ctrl+F1 on the Home fields (#184). Focus in the Home display always
        // sits on the same SilentTextBox whichever FIELD the cursor is in, so
        // a static help string cannot answer — and the Frequency field's
        // answer additionally depends on which tuning mode is live, where the
        // cursor stands, and the step values. A provider resolves all of that
        // at the moment the key is pressed.
        JJFlexHelp.SetProvider(FreqOut, ComposeFreqOutContextHelp);

        // Focus-return: when any JJFlexDialog closes, put keyboard focus back
        // inside the application and then speak compact status.
        //
        // The focus half is new on 2026-08-18. This callback only ever SPOKE,
        // and nothing restored focus - so closing Settings left the screen
        // reader announcing "pane" (the ElementHost, not a control), and
        // Insert+T still read a Settings window that no longer existed. The
        // operator had to Alt-Tab out and back to recover. Reported live.
        //
        // The cause is the hybrid shell: MainWindow is a WPF UserControl hosted
        // in a WinForms ElementHost, so WPF's ordinary "restore focus to the
        // element that opened the dialog" never spans the boundary. Nothing
        // owns putting it back, so it lands on the host pane.
        JJFlexDialog.FocusReturnCallback = () =>
        {
            // Sprint 42 Track D (#395): while a connect flow is running, the
            // dialogs closing are STAGES of the flow, not returns to the app.
            // The #348 foreground guard inside the landing catches most of
            // that, but it races the Connecting window's arrival — the rig
            // picker can close BEFORE that window takes the foreground, and
            // the guard then reads "nothing else is up" and drags focus, the
            // foreground and a landing announcement into mid-connect. The
            // scope has no race: it is raised before the picker opens and the
            // flow's finish runs this same landing if nothing powered on.
            //
            // BUT THE STAND-DOWN MUST NEVER BE TOTAL. This callback carries
            // two jobs — restore keyboard focus, then speak — and the scope
            // only has business suppressing the second. On 2026-08-30 this
            // branch was a bare return, and three times in one day the
            // discovering-window → picker hand-off lost the foreground with
            // nothing left to repair it: keyboard dead, screen reader silent,
            // a total lockout for a blind operator until the failsafe's
            // landing ran two minutes later. He killed the process rather
            // than wait. So the announcement stands down here, and a SILENT
            // focus check is scheduled instead — deferred long enough for the
            // flow's next window to arrive and take the foreground, so the
            // mid-connect race above stays closed.
            if (_connectQuiet.IsQuiet)
            {
                Tracing.TraceLine(
                    "FocusReturnCallback: connect quiet scope active - landing "
                    + "stands down; scheduling the silent focus check",
                    System.Diagnostics.TraceLevel.Info);
                ScheduleQuietScopeFocusRescue();
                return;
            }
            RunReturnToAppLanding();
        };

        // Sprint 42 Track D (#395): the frequency display asks before speaking
        // its focus-landing prefix. During a connect the activation churn
        // restores focus to it repeatedly, and each landing said "JJ Flexible
        // Home, slice" into the middle of the connect narration.
        FreqOut.SuppressFocusPrefix = () => _connectQuiet.IsQuiet;

        // Audio Workshop hooks that must NOT wait for a radio (2026-08-12).
        // The workshop's "This Computer" section and its preset toolbar both
        // work with no rig connected — choosing a sound card is a property of
        // the computer, and a saved preset is a file. Wiring these alongside
        // PttControllerSource in PowerNowOn would have left them dead until
        // the first connect. Every lambda resolves at call time, because both
        // of the values they read are set later, during startup.
        Dialogs.AudioWorkshopDialog.OpenAudioDevices = () => AudioSetupCallback?.Invoke();
        Dialogs.AudioWorkshopDialog.AudioDevicesPath = () => AudioDevicesFilePath;
        // Sprint 33 Track I: the recorder records the microphone the operator
        // already chose. Pointing it at the same setting the rest of the audio
        // path reads is what stops "which microphone am I recording" from
        // becoming a second thing to configure and a second thing to get wrong.
        RecordingNarrator.AudioDevicesPath = () => AudioDevicesFilePath;

        // Preset persistence — the operator-scoped store the presets model has
        // always assumed and nothing ever connected (see the note on
        // GetPresetsCallback). Falls back to the built-in defaults when there
        // is no operator yet, so Load offers Ragchew/Contest/DX on a fresh
        // install rather than claiming there are no presets.
        Dialogs.AudioWorkshopDialog.GetPresetsCallback = () =>
        {
            if (OpenParms == null)
                return Radios.AudioChainPresets.CreateDefaults();
            var loaded = Radios.AudioChainPresets.Load(
                OpenParms.ConfigDirectory, OpenParms.GetOperatorName(),
                out string? corruptPath);
            // #49 — a corrupt preset file must never silently become the
            // three defaults. That is the operator's tuning disappearing
            // with no notification. The store has already moved the
            // unreadable file aside so nothing overwrites it; this is where
            // the operator hears about it, once per occurrence.
            if (corruptPath != null)
            {
                Radios.ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("audio.presets.file_unreadable",
                        ("kept", System.IO.Path.GetFileName(corruptPath))),
                    Radios.VerbosityLevel.Critical);
            }
            return loaded;
        };
        // Returns whether the save actually landed. With no operator there is
        // no per-operator file to write, and a silent no-op here would put the
        // dialog straight back to announcing saves that did not happen. The
        // store's Save reports disk-level failure for the same reason.
        Dialogs.AudioWorkshopDialog.SavePresetsCallback = presets =>
        {
            if (OpenParms == null || presets == null) return false;
            return presets.Save(OpenParms.ConfigDirectory, OpenParms.GetOperatorName());
        };

        // Microphone profiles (Track F) — operator-scoped like the presets
        // (a microphone travels with the person; each profile's per-radio
        // bindings live inside it), same honest contracts: a corrupt file is
        // sidelined and spoken, a save reports whether it landed.
        Dialogs.AudioWorkshopDialog.GetMicProfilesCallback = () =>
        {
            if (OpenParms == null)
                return new Radios.MicrophoneProfileStore();
            var store = Radios.MicrophoneProfileStore.Load(
                OpenParms.ConfigDirectory, OpenParms.GetOperatorName(),
                out string? corruptPath);
            if (corruptPath != null)
            {
                Radios.ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("audio.mic_profiles.file_unreadable",
                        ("kept", System.IO.Path.GetFileName(corruptPath))),
                    Radios.VerbosityLevel.Critical);
            }
            return store;
        };
        Dialogs.AudioWorkshopDialog.SaveMicProfilesCallback = store =>
        {
            if (OpenParms == null || store == null) return false;
            return store.Save(OpenParms.ConfigDirectory, OpenParms.GetOperatorName());
        };

        // Wire braille display focus events
        FreqOut.GotKeyboardFocus += (s, e) => _brailleEngine.OnHomePositionFocused();
        FreqOut.LostKeyboardFocus += (s, e) => _brailleEngine.OnHomePositionBlurred();

        // Wire ScreenFieldsPanel Escape handler (Sprint 14) — once, not per-connect
        FieldsPanel.EscapePressed += (s, e) => FocusHome();
        FieldsPanel.ReturnFocusToFreqOut = () => FocusHome();

        // Wire MetersPanel Escape handler (Sprint 22 Phase 9)
        MetersPanel.EscapePressed += (s, e) => FocusHome();
        MetersPanel.ReturnFocusToFreqOut = () => FocusHome();

        // Wire CW notification delegates once at construction so they're available
        // during connect (AS fires on "Connecting to X", which happens BEFORE PowerOn).
        // Previously these were wired inside PowerOn, which raced with the connect path --
        // AS and BT delegates were null on first-connect and silently skipped.
        // The delegates only need _morseNotifier, which is field-initialized.
        Radios.ScreenReaderOutput.PlayCwAS = () => _morseNotifier.PlayAS();
        Radios.ScreenReaderOutput.PlayCwBT = () => _morseNotifier.PlayBT();
        // PlayCwSK signs off with proper ham etiquette at any speed: "73" + prosign SK
        // always; at speed >= 25 WPM, extend with "de JJF" app-callsign signature. Bare SK
        // is never sent -- feels abrupt, not how real operators sign off.
        //
        // Sprint 26 Phase 6 (BUG-061): single-utterance via bracket syntax so
        // "73 SK ee" renders as one continuous PARIS-spaced waveform. Previously
        // separate PlayString + PlaySK queue entries produced a small gap at
        // the queue boundary that didn't match standard word spacing.
        //
        // 2026-04-28 bug bundle: trailing " ee" (two dits) added so the no-radio
        // exit path also fires the friendly hand-wave close. This is the ONLY
        // wiring of these delegates — the vestigial PowerOn duplicate (which
        // re-introduced the BUG-061 inter-utterance gap on every connect) was
        // removed 2026-08-07 (QB Track A).
        Radios.ScreenReaderOutput.PlayCwSK = () =>
            _morseNotifier.PlayString(FarewellText(_morseNotifier.SpeedWpm));

        // #143 — how long the waiters should allow. Computed HERE, from the
        // same string PlayCwSK is about to send and at the speed it will send
        // it, because this is the only place that can see all three: the text,
        // the speed, and the output's own latency.
        //
        // The waiters (FlexBase.Disconnect and ApplicationEvents' shutdown)
        // both used a flat 5000 ms, chosen for the long farewell and short by
        // about a second for it. Two speed bands were being cut: roughly 10-15
        // WPM on the short string, and 25-31 on the long one, with everything
        // between and above fitting comfortably. Noel runs 20, which is why he
        // never saw it.
        Radios.ScreenReaderOutput.CwFarewellBudgetMs = () =>
        {
            int sending = _morseNotifier.DurationMsOf(FarewellText(_morseNotifier.SpeedWpm));

            // Past the sending duration the sound still has to get through the
            // device. EarconCwOutput bounds its own drain at roughly the output
            // latency twice over plus its grace; allow more than that, so the
            // OUTER wait never expires first and tears the device down while
            // the inner one is still legitimately waiting for the tail. That
            // failure destroys the final dit, which is exactly the symptom
            // Sprint 32 Track H fixed from the other end.
            return sending + (EarconPlayer.AlertOutputLatencyMs * 3) + 750;
        };
        Radios.ScreenReaderOutput.PlayCwMode = (mode) => _morseNotifier.PlayString(mode);
        // Sprint 32 Track H (#58): the slice vocabulary sends text, not mode
        // names — "3/4" for the census and "SL A USB" for a slice or mode
        // change — so it gets its own honestly-named delegate rather than
        // pushing sentences through PlayCwMode.
        Radios.ScreenReaderOutput.PlayCwText = (text) => _morseNotifier.PlayString(text);
        // Sprint 33 Track F (#153): the CW repeat cancels what is keying before
        // it re-sends, exactly as the speech repeat emits with interrupt. This
        // reaches EarconCwOutput and nothing else — a continuous earcon such as
        // the ATU progress tone is a separate input on the alert mixer and
        // keeps running, which was verified rather than assumed.
        Radios.ScreenReaderOutput.CancelCw = () => _morseNotifier.Cancel();
        // #182 (Noel's ruling): notifications CLOSE, they do not queue. Each
        // new SendCwText message supersedes the pending one — dropped from
        // the queue if unstarted, closed at the next CHARACTER boundary if
        // keying (#88: a half-sent character is a different character). The
        // session prosigns and the SK farewell are exempt and play out.
        Radios.ScreenReaderOutput.SupersedePendingCw = () => _morseNotifier.CloseForNewMessage();
        // #182's other half: Ctrl silences CW the way it already silences
        // speech. The hook OBSERVES the key — the reader still receives it
        // and still silences itself — and the busy check is one volatile
        // read, so the hook thread never does real work. The cancel is the
        // same one the repeat key uses: CW output only, continuous earcons
        // untouched. Since #402 the hook lives on KeyboardHookThread's own
        // pump, not this thread — this call only hands the install over, so
        // a blocked UI thread can no longer stall typing machine-wide.
        CwCtrlInterrupt.Install(() => _cwOutput.IsBusy, () => _morseNotifier.Cancel());
        // (#146) The radio announces its CW sidetone pitch on connect, on every
        // change, and as null on disconnect. Whether the notifier USES it is the
        // operator's setting; the notifier holds both numbers and picks.
        Radios.ScreenReaderOutput.RadioCwPitchChanged = (hz) =>
        {
            _lastRadioCwPitchHz = hz;
            _morseNotifier.RadioSidetoneHz = hz;
        };

        // Load user-scope CW settings from BaseConfigDir (root of %AppData%\JJFlexRadio\)
        // so CwNotificationsEnabled + speed + sidetone are set before any connect
        // triggers AS. Per-radio PowerOn and Settings OK also re-apply these, but this
        // is the earliest point where they must be correct for the first-connect AS
        // prosign to actually fire.
        try
        {
            string baseConfigDir = System.IO.Path.Combine(
                Radios.RadioConfig.AppDataRoot);
            if (System.IO.Directory.Exists(baseConfigDir))
            {
                var userConfig = AudioOutputConfig.Load(baseConfigDir);
                ApplyCwNotifierSettings(userConfig);
                Radios.ScreenReaderOutput.SpeakConnectionProgressEnabled = userConfig.SpeakConnectionProgress;
                // Sprint 33 Track K. Loaded here, from ROOT, for the same
                // reason the CW flags above are: it has to be in place before
                // the first disconnect, and the per-radio config that would
                // otherwise carry it is not read until PowerOn.
                Radios.FlexBase.OfferStationSaveOnDisconnect =
                    userConfig.OfferStationSaveOnDisconnect;
                // #147, and here for the same reason the CW settings are:
                // AudioOutputConfig.Apply() runs on CONNECT, so an operator who
                // chose the Simple set would hear the Rich one on every
                // dialog ding and JJ-key tone between launch and their first
                // connect — including the whole of a session that never
                // connects at all.
                EarconVoices.ActiveSet =
                    userConfig.EarconVoiceSet == (int)EarconVoiceSet.Simple
                        ? EarconVoiceSet.Simple
                        : EarconVoiceSet.Rich;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainWindow ctor: user-scope CW config load failed: {ex.Message}");
        }

        // Sprint 26 Phase 3: announce SmartLink session status transitions via screen
        // reader. The coordinator lives behind SmartLinkServices; we subscribe to its
        // ActiveSessionChanged event so that as sessions come and go (N=1 today, N>1
        // with tabs in Sprint 28+) we rebind the per-session StatusChanged handler.
        // All speech is marshalled to the UI thread because StatusChanged fires on
        // the session owner's monitor thread.
        _sessionStatusHandler = (s, status) =>
        {
            // Routine bring-up of the BACKGROUND SmartLink session is not news.
            // It happens on its own schedule - token refresh, account session,
            // radio list - and it happened even when Noel had deliberately
            // connected to his 8600 over the LAN, narrating "Connecting to
            // SmartLink... Connected" on top of the real connect announcements
            // (found 2026-08-17 in a verbose speech trace).
            //
            // Trouble still speaks, and so does RECOVERY from trouble: having
            // been told the session was reconnecting, silence about it coming
            // back would be the worse defect. So Connecting/Connected are
            // suppressed only when arriving from a healthy state.
            var prev = _lastSessionStatus;
            _lastSessionStatus = status;
            bool routineBringUp =
                (status == Radios.SmartLink.SessionStatus.Connecting
                 || status == Radios.SmartLink.SessionStatus.Connected)
                && prev != Radios.SmartLink.SessionStatus.Reconnecting
                && prev != Radios.SmartLink.SessionStatus.AuthorizationExpired
                && prev != Radios.SmartLink.SessionStatus.Disconnected;
            if (routineBringUp) return;

            // Routine tear-DOWN is not news either (#360, 2026-08-28). By
            // construction in WanSessionOwner, Disconnected is only ever
            // reached when the app itself asked (_userWantsConnected false —
            // the monitor turns network trouble into Reconnecting, never
            // straight Disconnected), and ShutDown only on a requested
            // shutdown. Every one of those deliberate paths narrates itself:
            // Radio ▸ Disconnect speaks "Disconnected from X", SelectRadio
            // hands its lead to the arriving picker, exit has the farewell,
            // and FlexBase's mid-connect WAN session cycling is plumbing
            // inside a connect that is being narrated by the Connecting
            // window. So "Disconnected" and "Session closed" from HERE were
            // always a second and third voice describing an event whose first
            // voice was already speaking — the #360 capture shows exactly
            // that: both were queued and then cancelled by the deliberate
            // path's own sentence 510 ms later, and the operator heard none
            // of the three. One event, one sentence: this channel defers to
            // the path that acted. Trouble (Reconnecting), the operator's
            // one actionable state (AuthorizationExpired), and recovery from
            // either still speak.
            bool routineTearDown =
                status == Radios.SmartLink.SessionStatus.Disconnected
                || status == Radios.SmartLink.SessionStatus.ShutDown;
            if (routineTearDown)
            {
                Tracing.TraceLine(
                    $"SessionStatus: {prev} -> {status} suppressed as routine tear-down (#360)",
                    System.Diagnostics.TraceLevel.Info);
                return;
            }

            var session = Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSession;
            int attempts = session?.ReconnectAttemptCount ?? 0;
            var lastErr = session?.LastError;
            // Sprint 27 Track D — pass NetworkTest context + account mode into
            // the richer message resolver so failure states get specific
            // overlays ("UPnP didn't take", "NAT symmetric", etc.) instead of
            // a generic "Reconnecting" line.
            var report = session?.MostRecentNetworkReport;
            var mode = RigControl?.CurrentAccountConnectionMode
                       ?? Radios.SmartLinkConnectionMode.ManualPortForwardOnly;
            bool verbose = Radios.SmartLink.DiagnosticVerbosityPreference.Verbose;
            string message = Radios.SmartLink.SessionStatusMessages.ForStatusRich(
                status, attempts, lastErr, report, mode, verbose);
            // StatusChanged runs on the session monitor thread; marshal to UI.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Radios.ScreenReaderOutput.Speak(message, Radios.VerbosityLevel.Terse);
            }));
        };
        Radios.SmartLink.SmartLinkServices.Coordinator.ActiveSessionChanged += (s, newSession) =>
        {
            if (_subscribedSession != null)
            {
                _subscribedSession.StatusChanged -= _sessionStatusHandler;
                _subscribedSession = null;
            }
            if (newSession != null)
            {
                _subscribedSession = newSession;
                newSession.StatusChanged += _sessionStatusHandler;
            }
        };
    }

    /// <summary>
    /// The exit farewell, at a given keying speed. ONE definition, because two
    /// callers need it: the delegate that sends it, and the one that says how
    /// long to wait for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Proper ham etiquette at any speed — "73" plus the SK prosign, never a
    /// bare SK, which feels abrupt and is not how operators sign off. At 25 WPM
    /// and above it extends with the "de JJF" app-callsign signature, which
    /// there is time for once the code is moving. The trailing "ee" is the
    /// friendly hand-wave close.
    /// </para>
    /// <para>
    /// <b>The 25 WPM step is a cliff, and it is why #143 could not be fixed
    /// with a bigger constant.</b> The string roughly DOUBLES in length at one
    /// word per minute faster, so the farewell gets longer exactly where the
    /// keying got quicker. Anything sizing a timeout has to ask this method
    /// rather than assume; a duration derived from speed alone is wrong on one
    /// side of the step whichever side it was measured on.
    /// </para>
    /// <para>
    /// One utterance, not two calls — bracket syntax renders it as a single
    /// continuous PARIS-spaced waveform. Sending "73" and then the prosign
    /// separately puts a queue-boundary gap in the middle that does not match
    /// standard word spacing (BUG-061).
    /// </para>
    /// </remarks>
    private static string FarewellText(int speedWpm)
        => (speedWpm >= 25 ? "73 de JJF" : "73") + " <SK> ee";

    // Sprint 26 Phase 3: tracks the session we're currently subscribed to so we can
    // unsubscribe cleanly when ActiveSession changes. Only one subscription at a time
    // (matches the N=1 coordinator reality for Sprint 26; Sprint 28+ tabbed UI will
    // need per-session subscriptions for each tab).
    private Radios.SmartLink.IWanSessionOwner? _subscribedSession;
    /// <summary>
    /// Previous SmartLink session status, so a transition can be judged by
    /// where it came FROM. Connected arriving after Reconnecting is news;
    /// Connected arriving after nothing is plumbing.
    /// </summary>
    /// Null until the first transition: "never seen" is NOT the same as "was
    /// disconnected". Initialising this to Disconnected would have made the
    /// very first bring-up look like a recovery and announce itself, which is
    /// the exact noise this suppression exists to remove.
    private Radios.SmartLink.SessionStatus? _lastSessionStatus;

    private readonly EventHandler<Radios.SmartLink.SessionStatus> _sessionStatusHandler;

    /// <summary>
    /// Main initialization sequence — replaces Form1_Load.
    /// Called once when the window first renders.
    ///
    /// Init order (matches Form1_Load):
    ///   1. Screen reader greeting
    ///   2. Status bar setup              — Phase 8.2
    ///   3. Config load (GetConfigInfo)   — Phase 8.1 (wired here)
    ///   4. UI Mode upgrade prompt        — Phase 8.5
    ///   5. Station name / window title   — Phase 8.1 (wired here)
    ///   6. Operator change handler       — Phase 8.1
    ///   7. Radio open                    — Phase 8.2+
    ///   8. Menu construction             — Phase 8.5
    ///   9. Logging panel build           — Phase 8.8
    ///  10. Apply UI mode                 — Phase 8.5
    /// </summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Tracing.TraceLine("MainWindow_Loaded: starting init", System.Diagnostics.TraceLevel.Info);

        // Welcome speech is now triggered by ShellForm.OnShown() calling SpeakWelcome(),
        // so it fires after the window is visible and the screen reader can hear it.

        // Update status
        StatusText.Text = Radios.Lexicon.Get("connect.home.status_ready_no_radio");

        // Sprint 29 Track D — auto-update launch + periodic check.
        StartUpdaterAutoCheck();

        Tracing.TraceLine("MainWindow_Loaded: init complete", System.Diagnostics.TraceLevel.Info);
    }

    /// <summary>
    /// Called by ShellForm.OnShown() after the window is visible on screen.
    /// Screen reader speech only works reliably after the window is visible.
    /// </summary>
    public void SpeakWelcome()
    {
        // First moment the application owns a visible top-level window, which
        // is the precondition the UI Automation speech channel cannot do
        // without. Speech itself came up long before this, during startup, so
        // this is the earliest point a Narrator user can be reached at all.
        //
        // No-op whenever we already have a real screen reader — this only
        // rescues the case where we settled for a raw synthesiser that would
        // otherwise talk over the reader that IS running.
        if (Radios.ScreenReaderOutput.TryUpgradeChannel())
        {
            JJTrace.Tracing.TraceLine(
                "Speech channel upgraded once the main window was shown.",
                System.Diagnostics.TraceLevel.Info);
        }

        // An ARRIVAL, not a greeting. By the time this is heard the operator
        // has navigated the connect dialog and connected a radio, so "welcome"
        // would be describing a moment well past. What is useful here is where
        // they landed and in which tuning mode. The greeting proper is spoken
        // at launch - see ScreenReaderOutput.SpeakGreeting.
        //
        // Fires whenever Home becomes visible, INCLUDING when the connect
        // dialog was cancelled and there is no radio at all, which is another
        // reason it cannot be a connect announcement.
        // Sprint 30 Track A — the first moment "no radio" is a fact rather than
        // "not yet": the connect flow ran synchronously during startup and has
        // returned by the time the shell is shown. If it produced no radio,
        // Home becomes the rescue page before anything below moves focus.
        EnterRescueModeIfNoRadio();

        string modeName = Radios.Lexicon.Get(ActiveUIMode == UIMode.Classic
            ? "connect.home.mode_classic" : "connect.home.mode_modern");
        string message = Radios.Lexicon.Get(
            _rescueMode ? "connect.home.arrival_no_radio" : "connect.home.arrival",
            ("modeName", modeName));

        // Ordering policy (live find 2026-08-04): with a startup advisory on
        // screen, this line used to talk over the dialog — and worse, the
        // FreqOut focus grab below yanked keyboard focus OUT of the modal and
        // back into the main window, which is how a Tab came to speak slice
        // state behind an open advisory. While the advisory chain is active,
        // BOTH the speech and the focus grab wait their turn; the chain
        // replays them when the last advisory closes.
        // #194, served by #253's register rather than by a second list of its
        // own: if instrumentation the operator cannot see is switched on, this
        // is the moment to say so. Null whenever nothing silent-and-costly is
        // running, which is the ordinary case, so an ordinary launch is
        // unchanged.
        //
        // HERE and not at launch proper, for the reason InitializeApplication
        // already records: a screen reader flushes its queue on every window
        // change, the whole connect flow is window changes, and an utterance
        // made before Home arrives would never survive to be heard. This is
        // the first announcement that does survive, so the notice rides
        // directly behind it — and inherits the advisory-parking policy for
        // free rather than inventing a second one.
        string? runningNotice = null;
        try { runningNotice = Radios.RunningCostRegister.DescribeNotableForSpeech(); }
        catch { /* never let a diagnostics read cost somebody their arrival announcement */ }

        if (_advisorySequenceActive)
        {
            _welcomeFocusPending = true;
            _deferredStartupSpeech.Add((message, null));
            if (runningNotice != null)
                _deferredStartupSpeech.Add((runningNotice, Radios.VerbosityLevel.Critical));
            return;
        }

        // Land on the frequency display at startup — or on the rescue page's
        // first button when there is no radio and the display is collapsed.
        FocusHome();
        Radios.ScreenReaderOutput.Speak(
            message, Radios.Speech.SpeechIntent.Queue, Radios.VerbosityLevel.Terse);
        if (runningNotice != null)
        {
            // Critical, and queued behind the arrival. Critical because the
            // operator did not ask and cannot see it; queued because it is the
            // second half of arriving, not an interruption of it.
            Radios.ScreenReaderOutput.Speak(
                runningNotice, Radios.Speech.SpeechIntent.Queue, Radios.VerbosityLevel.Critical);
        }
    }

    /// <summary>
    /// Sprint 22 Phase 8: Speak radio status after connect. Delayed 1.5s to let
    /// FlexLib populate slice data. Called at the end of PowerNowOn().
    /// </summary>
    private void SpeakConnectStatus()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            Dispatcher.Invoke(() =>
            {
                if (RigControl == null) return;

                string model = RigControl.RadioModel;
                string connType = Radios.Lexicon.Get(RigControl.RemoteRig
                    ? "connect.home.link_smartlink" : "connect.home.link_local");

                // Slices may not have populated yet even after the 1.5s delay;
                // when that's true, BuildFullSliceStatus falls back to a bare
                // "Connected to X", which through this path would come out
                // duplicate-prefixed. (Until #348 that fallback also said "no
                // active slice" — untrue within two seconds; the builder now
                // applies this same MyNumSlices test itself.) Speak the
                // connection portion alone in that case and trust subsequent
                // operations to reveal slice state as the user navigates.
                string message;
                if (RigControl.MyNumSlices > 0)
                {
                    string status = Radios.RadioStatusBuilder.BuildFullSliceStatus(RigControl);
                    message = Radios.Lexicon.Get("connect.home.connected_with_slices",
                        ("model", model), ("connType", connType), ("status", status));
                }
                else
                {
                    message = Radios.Lexicon.Get("connect.home.connected",
                        ("model", model), ("connType", connType));

                    // #359 — this sentence carries no slice information, so
                    // the census it would have covered becomes the only slice
                    // announcement there will be. Release it. The release
                    // always beats the settle it must un-suppress: reaching
                    // this branch means no slice had arrived by now, so no
                    // settle timer has started yet.
                    RigControl.ConnectSentenceFellBackToConnectionOnly();
                }

                // A startup advisory may be up (or about to be) — speaking the
                // slice rundown now would stomp the dialog the user is reading.
                // Park it; RunStartupAdvisories speaks it when the last
                // advisory closes.
                if (_advisorySequenceActive)
                {
                    _deferredStartupSpeech.Add((message, VerbosityLevel.Critical));
                    return;
                }
                Radios.ScreenReaderOutput.Speak(message, VerbosityLevel.Critical);
            });
        });
    }

    /// <summary>
    /// Called by the parent ShellForm before closing to run the VB-side exit sequence.
    /// Returns true to allow close, false to cancel (e.g., unsaved QSO).
    /// </summary>
    public bool RequestShutdown()
    {
        if (_isClosing)
            return true;

        Tracing.TraceLine("MainWindow.RequestShutdown: starting shutdown", System.Diagnostics.TraceLevel.Info);

        // Run VB-side exit sequence (prompts, cleanup, radio close)
        if (AppExitCallback != null && !AppExitCallback())
        {
            Tracing.TraceLine("MainWindow.RequestShutdown: exit cancelled by user", System.Diagnostics.TraceLevel.Info);
            return false;
        }

        _isClosing = true;

        try
        {
            UnwireRadioEvents();
            Tracing.TraceLine("MainWindow.RequestShutdown: shutdown complete", System.Diagnostics.TraceLevel.Info);
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine($"MainWindow.RequestShutdown error: {ex.Message}", System.Diagnostics.TraceLevel.Error);
        }

        return true;
    }

    /// <summary>
    /// Delegate to close the parent ShellForm. Set by ApplicationEvents.vb.
    /// </summary>
    public Action? CloseShellCallback { get; set; }

    /// <summary>
    /// Callback to wire FreqOutHandlers delegate properties from VB.NET globals.
    /// Set by ApplicationEvents.vb. Called when handlers are first created in SetupFreqout().
    /// </summary>
    public Action<FreqOutHandlers>? FreqOutHandlersWireCallback { get; set; }

    /// <summary>
    /// Callback to set filter presets on the NativeMenuBar.
    /// Set by ShellForm constructor. Called from FreqOutHandlersWireCallback
    /// so presets are available before the menu rebuild in PowerNowOn.
    /// </summary>
    public Action<Radios.FilterPresets>? SetNativeMenuFilterPresetsCallback { get; set; }

    #region PollTimer — Phase 8.4

    /// <summary>
    /// Poll interval in milliseconds. Matches Form1.pollTimerInterval (100ms = 10 FPS).
    /// </summary>
    private const int PollTimerIntervalMs = 100;

    /// <summary>
    /// WPF DispatcherTimer replacing System.Windows.Forms.Timer.
    /// Fires on UI thread — no InvokeRequired checks needed.
    /// </summary>
    private DispatcherTimer? _pollTimer;

    /// <summary>
    /// List of RadioComboBox controls to poll each tick.
    /// Matches Form1.combos list pattern.
    /// </summary>
    private readonly List<RadioComboBox> _comboControls = new();

    /// <summary>
    /// List of controls to enable/disable based on radio power state.
    /// Matches Form1.enableDisableControls pattern.
    /// </summary>
    private readonly List<UIElement> _enableDisableControls = new();

    /// <summary>
    /// Track whether radio power is on — gates the update cycle.
    /// </summary>
    private bool _radioPowerOn;

    /// <summary>
    /// PTT safety controller — manages TX hold, lock, timeout warnings, hard kill.
    /// Created when radio connects, disposed on disconnect.
    /// </summary>
    private PttSafetyController? _pttController;

    /// <summary>
    /// Guards against repeated PttDown calls from key-repeat.
    /// Set true on first Ctrl+Space down, cleared on key-up.
    /// </summary>
    private bool _pttKeyDown;

    /// <summary>
    /// Absorbs the synthetic key-release pairs a screen reader may substitute
    /// for a held Ctrl+Space, so a held PTT stays keyed (#216). Inert until it
    /// has seen the synthetic signature — under NVDA, which passes real holds
    /// through, every release is immediate exactly as before. See
    /// <see cref="Radios.PttHoldFilter"/> for the whole story.
    /// </summary>
    private readonly Radios.PttHoldFilter _pttHoldFilter = BuildPttHoldFilter();

    /// <summary>
    /// Hand the filter the two things it cannot work out for itself: this
    /// machine's keyboard repeat delay, and a way to ask Windows whether the
    /// space bar is physically down.
    ///
    /// The repeat delay is the load-bearing half. The first shipped version of
    /// the filter used one learned window for both the first gap of a press
    /// (the repeat delay) and the gaps inside a repeat stream (about half
    /// that), so it trained itself down to the smaller number and then chopped
    /// the start of every press — measured three times on one held key,
    /// 2026-08-26. Reading the operator's own setting is what makes the fix
    /// something other than a constant that happened to work on one desk.
    /// </summary>
    private static Radios.PttHoldFilter BuildPttHoldFilter()
    {
        var f = new Radios.PttHoldFilter();
        f.SetKeyRepeatDelay(Radios.KeyRepeatTiming.DelayMs());
        f.PhysicalKeyDown = () => Radios.PhysicalKeyState.IsDown(Radios.PhysicalKeyState.VkSpace);
        return f;
    }

    /// <summary>
    /// Runs a deferred PTT release to ground when no synthetic re-down
    /// arrived to cancel it. One timer, restarted per deferral.
    /// </summary>
    private DispatcherTimer? _pttDeferTimer;

    /// <summary>One Error-level trace line when the filter first arms.</summary>
    private bool _pttFilterArmTraced;

    /// <summary>
    /// One Error-level trace line the first time the physical key state
    /// contradicts the reader's release. It answers a question this design
    /// deliberately does not depend on — whether Windows' own key state
    /// survives a synthesising reader — so it is worth saying loudly once.
    /// </summary>
    private bool _pttProbeExtensionTraced;

    /// <summary>
    /// Current PTT configuration. Set during radio connect, used by Settings dialog.
    /// </summary>
    internal PttConfig? CurrentPttConfig { get; private set; }

    /// <summary>
    /// Audio output configuration (earcon device, meter tones). Loaded at radio connect.
    /// </summary>
    public AudioOutputConfig? CurrentAudioConfig { get; set; }

    /// <summary>
    /// Persist the current PC output volume (Audio Arc Track A). Called by the
    /// menu handler and the leader volume mode after an adjustment so the
    /// setting survives a crash; app close captures it too via
    /// CaptureFromEngine. Deliberately writes just this one value plus the
    /// existing config — no full engine capture from a hot path.
    /// </summary>
    public void PersistPcOutputVolume()
    {
        if (CurrentAudioConfig == null || OpenParms == null) return;
        CurrentAudioConfig.PcOutputVolumeDb = Radios.FlexBase.PcOutputVolumeDbSetting;
        CurrentAudioConfig.Save(OpenParms.ConfigDirectory);
    }

    /// <summary>
    /// DSP controls track (2026-08-11): capture the PC-side noise reduction
    /// state (toggles, strengths, floor, voice-only) from the live pipeline
    /// into the audio config and save it, so the settings survive a restart —
    /// the six config fields existed since Phase 20 but nothing ever wrote
    /// them. Called from every surface that changes a PC NR value (panel
    /// fields, leader toggles, menu items, Noise Profiles dialog). Same
    /// hot-path discipline as PersistPcOutputVolume: one small file, saved
    /// immediately so a crash doesn't lose the operator's dialed-in DSP.
    /// </summary>
    public void PersistDspSettings()
    {
        if (CurrentAudioConfig == null || OpenParms == null) return;
        var pipeline = FieldsPanel?.AudioPipeline;
        if (pipeline == null) return;
        CurrentAudioConfig.RNNoiseEnabled = pipeline.RnnEnabled;
        CurrentAudioConfig.RNNoiseStrength = pipeline.RnnStrength;
        CurrentAudioConfig.RNNoiseAutoDisableNonVoice = pipeline.RnnAutoDisableNonVoice;
        CurrentAudioConfig.SpectralSubEnabled = pipeline.SpectralEnabled;
        CurrentAudioConfig.SpectralSubStrength = pipeline.SpectralStrength;
        CurrentAudioConfig.SpectralSubFloor = pipeline.SpectralFloor;
        CurrentAudioConfig.Save(OpenParms.ConfigDirectory);
    }

    /// <summary>
    /// Returns PTT status text for the Speak Status hotkey, or null if PTT is idle.
    /// </summary>
    public string? GetPttStatusText() => _pttController?.GetSpokenStatus();
    public string? GetFilterEdgeStatus() => _freqOutHandlers?.FilterEdgeStatus;

    // Menu → handler bridge for the Slice > Tuning submenu. After tuning unity
    // (Sprint 29 Track F) the only remaining bridge is "Speak Current Step",
    // since coarse/fine no longer toggle and the step lists no longer cycle.
    // The Settings dialog is the canonical place to change step values.

    /// <summary>Menu: announce current coarse + fine step sizes.</summary>
    public void TuningMenuSpeakStep()
    {
        if (ActiveUIMode == UIMode.Classic)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.classic_tuning_mode"),
                Radios.VerbosityLevel.Terse, true);
            return;
        }
        _freqOutHandlers?.SpeakCurrentStepFromMenu();
    }

    /// <summary>
    /// Returns tuning mode status for Speak Status, e.g.
    /// "modern tuning, coarse 5 kilohertz, fine 100 hertz".
    /// </summary>
    public string? GetTuningModeStatus()
    {
        if (_freqOutHandlers == null) return null;
        if (ActiveUIMode == UIMode.Classic)
            return "classic tuning mode";
        return "modern tuning mode, coarse "
            + FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.CoarseTuneStep)
            + ", fine "
            + FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.FineTuneStep);
    }

    /// <summary>
    /// The Ctrl+F1 answer for the Home display (#184), resolved live for the
    /// field the cursor is standing in.
    ///
    /// The Frequency field leads with STATE — the live tuning mode, then the
    /// cursor digit (Classic) or the step values (Modern) — then reads only
    /// the live key map, then names the way to the other mode. The map rows
    /// come verbatim from KeyInventory, the same table behind the '?' key,
    /// the per-field help dialog, the Keys dialog, the Command Finder and the
    /// exported manifest, so this is not a new hand-maintained copy (#274 is
    /// what the last extra copy cost). Every other field answers with its
    /// '?' text for the same reason: one source, zero new prose.
    /// </summary>
    private string? ComposeFreqOutContextHelp()
    {
        var field = FreqOut.GetFocusedField();
        if (field == null) return null;
        bool modern = ActiveUIMode == UIMode.Modern;

        if (field.Key != "Freq")
            return KeyInventory.SpeakTextFor(field.Key, field.Label ?? field.Key, modern);

        // Binary CurrentVerbosity ternary, the ToggleUIMode pattern — Chatty
        // gets connective coaching, everyone gets the full live map. The
        // answer itself always speaks: the operator explicitly asked.
        bool chatty = Radios.ScreenReaderOutput.CurrentVerbosity == VerbosityLevel.Chatty;

        // The switch key as CURRENTLY bound, so a remapped key is never
        // misquoted. Null when unbound — the composer names the menu instead.
        var switchEntry = KeyCommandsRef?.Lookup(CommandValues.ToggleTuningMode);
        var switchKey = switchEntry?.KeyDef?.Key ?? System.Windows.Forms.Keys.None;
        string? switchDisplay = switchKey == System.Windows.Forms.Keys.None
            ? null : KeyManifest.FormatKey(switchKey);

        // Step values mirror FreqOutHandlers' own defaults if the handler is
        // somehow not built yet — the help must not invent numbers.
        int coarseHz = _freqOutHandlers?.CoarseTuneStep ?? 5000;
        int fineHz = _freqOutHandlers?.FineTuneStep ?? 100;

        return Radios.TuningContextHelp.ComposeFrequencyField(
            modern,
            chatty,
            switchDisplay,
            FreqOutHandlers.FormatStepForSpeech(coarseHz),
            FreqOutHandlers.FormatStepForSpeech(fineHz),
            modern ? null : FreqOut.CurrentStepName("Freq"),
            KeyInventory.FrequencyContextRows(modern));
    }

    /// <summary>
    /// Returns "frequency readout off" if readout is disabled, null otherwise.
    /// Only report when off — "on" is the default and not notable.
    /// </summary>
    public string? GetFreqReadoutStatus()
    {
        if (_freqOutHandlers == null) return null;
        return _freqOutHandlers.FreqReadoutEnabled ? null : "frequency readout off";
    }

    /// <summary>
    /// Returns meter tone status for Speak Status, e.g. "meter tones on, RX Monitor".
    /// </summary>
    public string? GetMeterStatus()
    {
        if (!MeterToneEngine.Enabled) return null;
        return $"meter tones {MeterToneEngine.CurrentPreset}";
    }

    /// <summary>
    /// Returns active filter preset name, or null if not on a preset.
    /// </summary>
    public string? GetFilterPresetStatus() => _freqOutHandlers?.ActiveFilterPresetStatus;

    /// <summary>
    /// Apply settings changes from the Settings dialog.
    /// Propagates PttConfig to controller, tuning steps to handler, and saves to disk.
    /// </summary>
    internal void ApplySettingsChanges(int coarseStep, int fineStep)
    {
        // Update PttSafetyController with modified config
        if (CurrentPttConfig != null)
            _pttController?.UpdateConfig(CurrentPttConfig);

        // Apply tuning steps and band memory setting
        if (_freqOutHandlers != null)
        {
            // Through ApplyStepSizes rather than assigning the two properties
            // and invoking the save callback here: #302 gave the steps a
            // picker and a pair of ladder keys, and three surfaces writing the
            // same two fields is how one of them quietly grows a different
            // idea of what "apply" means. Settings does not speak — the dialog
            // just closed and the operator knows what they chose.
            _freqOutHandlers.ApplyStepSizes(coarseStep, fineStep, speak: false);
            _freqOutHandlers.BandMemoryEnabled = CurrentPttConfig?.BandMemoryEnabled ?? true;
            _freqOutHandlers.FrequencyUnits = CurrentPttConfig?.FrequencyDisplayUnits ?? Radios.FrequencyUnits.Hz;
        }

        // Save PttConfig to disk
        if (CurrentPttConfig != null && OpenParms != null)
            CurrentPttConfig.Save(OpenParms.ConfigDirectory, OpenParms.GetOperatorName());

        // Save LicenseConfig to disk
        if (_freqOutHandlers?.License != null && OpenParms != null)
            _freqOutHandlers.License.Save(OpenParms.ConfigDirectory, OpenParms.GetOperatorName());

        // Save AudioOutputConfig to disk
        if (CurrentAudioConfig != null && OpenParms != null)
        {
            CurrentAudioConfig.CaptureFromEngine();
            CurrentAudioConfig.Save(OpenParms.ConfigDirectory);
        }

        // Re-apply braille config to the engine so Settings changes take effect
        // without requiring a reconnect. PowerOn also applies these at connect
        // time; this covers the Settings-while-running case.
        if (CurrentAudioConfig != null)
        {
            _brailleEngine.Enabled = CurrentAudioConfig.BrailleEnabled;
            _brailleEngine.CellCount = CurrentAudioConfig.BrailleCellCount;
            _brailleEngine.EnabledFields = (BrailleFields)CurrentAudioConfig.BrailleFields;
            _brailleEngine.UpdateTimerState();
        }

        // Re-apply CW notification config so Settings changes (speed WPM,
        // sidetone frequency, enable toggle, mode-announce toggle) take effect
        // at runtime. PowerOn applies these at connect; this covers the
        // Settings-while-running case, same as the braille pattern above.
        if (CurrentAudioConfig != null)
        {
            ApplyCwNotifierSettings(CurrentAudioConfig);
            Radios.ScreenReaderOutput.SpeakConnectionProgressEnabled = CurrentAudioConfig.SpeakConnectionProgress;
        }

        // Reflect any "Show panadapter" change immediately (toggle acts live)
        ApplyPanadapterVisibility();
    }

    /// <summary>
    /// Push the operator's CW notification preferences at the notifier and the
    /// screen-reader layer.
    ///
    /// Three call sites used to hold their own copy of these lines — the
    /// constructor, the Settings-while-running path and the connect path — and
    /// Sprint 33 Track F was about to make it nine. Copies of a settings
    /// application drift silently: the one that gets forgotten does not fail,
    /// it quietly keeps yesterday's value, which is the description-drift
    /// defect wearing a different hat. One method, three callers.
    ///
    /// The clamps are deliberately different from each other. Sidetone is
    /// 400–1200 because that is what the settings field promises. Speed is
    /// 10–60: Sprint 26 raised the cap from 30 because CW experts operate at
    /// 35–45 and the PARIS math handles anything decodable, and the floor stays
    /// at 10 because below that the dit lengths are distracting rather than
    /// slow. The RADIO's pitch is deliberately not clamped to the same band —
    /// see MorseNotifier.EffectiveSidetoneHz.
    /// </summary>
    private void ApplyCwNotifierSettings(AudioOutputConfig cfg)
    {
        if (cfg == null) return;
        _morseNotifier.SidetoneHz = Math.Clamp(cfg.CwSidetoneHz, 400, 1200);
        _morseNotifier.SpeedWpm = Math.Clamp(cfg.CwSpeedWpm, 10, 60);
        _morseNotifier.FollowRadioSidetone = cfg.CwPitchFollowsRadio;
        _morseNotifier.MarkVoice = EarconVoices.ResolveCwWaveform(cfg.CwWaveform).Voice;
        Radios.ScreenReaderOutput.CwNotificationsEnabled = cfg.CwNotificationsEnabled;
        Radios.ScreenReaderOutput.CwModeAnnounceEnabled = cfg.CwModeAnnounce;
    }

    /// <summary>
    /// Sync PanadapterPanel.Visibility to CurrentAudioConfig.ShowPanadapter.
    /// Collapsed removes the panel from layout AND the tab order so users
    /// who don't use the waterfall aren't forced to Tab through it. Called
    /// at startup and after Settings OK.
    /// </summary>
    private void ApplyPanadapterVisibility()
    {
        bool show = CurrentAudioConfig?.ShowPanadapter ?? true;
        PanadapterPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Previous SWR text for change detection.
    /// </summary>
    private string _oldSwr = "";

    /// <summary>
    /// ATU tune timeout timer — stops progress earcon after 15 seconds if no result.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _atuTuneTimer;

    /// <summary>
    /// Start or stop the poll timer.
    /// Matches Form1.PollTimer property pattern.
    /// </summary>
    public bool PollTimerEnabled
    {
        get => _pollTimer?.IsEnabled ?? false;
        set
        {
            if (value)
            {
                if (_pollTimer == null)
                {
                    _pollTimer = new DispatcherTimer(DispatcherPriority.Normal)
                    {
                        Interval = TimeSpan.FromMilliseconds(PollTimerIntervalMs)
                    };
                    _pollTimer.Tick += PollTimer_Tick;
                }
                _pollTimer.Start();
                Tracing.TraceLine("PollTimer: started", TraceLevel.Info);
            }
            else
            {
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Tick -= PollTimer_Tick;
                    _pollTimer = null;
                    Tracing.TraceLine("PollTimer: stopped", TraceLevel.Info);
                }
            }
        }
    }

    /// <summary>
    /// 100ms poll tick — calls UpdateStatus().
    /// Mirrors Form1.PollTimer_Tick.
    /// </summary>
    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatus();
    }

    /// <summary>
    /// Main status update — called every 100ms when radio is connected.
    /// Reads current rig state and updates all UI controls.
    /// Matches Form1.UpdateStatus() flow.
    /// </summary>
    public void UpdateStatus()
    {
        if (_isClosing)
            return;

        if (RigControl == null || !RigControl.IsConnected)
            return;

        try
        {
            if (_radioPowerOn)
            {
                // Update frequency display
                ShowFrequency();

                // Update all combo controls (Mode, Tuner, etc.)
                foreach (var combo in _comboControls)
                {
                    if (combo.IsEnabled)
                    {
                        combo.UpdateDisplay();
                    }
                }

                // Update rig-dependent fields (DSP controls via WpfFilterAdapter)
                if (RigControl.RigFields != null)
                {
                    RigControl.RigFields.RigUpdate?.Invoke();
                }

                // Update screen fields panel (Sprint 14)
                if (FieldsPanel.Visibility == Visibility.Visible)
                {
                    FieldsPanel.PollUpdate();
                }

                // SWR update during manual tuning
                if (OpenParms?.GetSWRText != null &&
                    RigControl.FlexTunerOn &&
                    RigControl.FlexTunerType == FlexBase.FlexTunerTypes.manual)
                {
                    string swrText = OpenParms.GetSWRText();
                    if (swrText != _oldSwr)
                    {
                        _oldSwr = swrText;
                        SetButtonText(AntennaTuneButton, _oldSwr);
                    }
                }
            }

            // Update status bar if FreqOut has changes
            if (FreqOut.Changed)
            {
                FreqOut.Display();
            }
        }
        catch (Exception ex)
        {
            if (!_radioPowerOn)
            {
                Tracing.TraceLine("UpdateStatus: power is off", TraceLevel.Error);
            }
            else
            {
                Tracing.TraceLine($"UpdateStatus error: {ex.Message}", TraceLevel.Error);
                PowerNowOffInternal();
            }
        }

    }

    /// <summary>
    /// Enable or disable radio-dependent controls based on power state.
    /// Matches Form1.enableDisableWindowControls().
    /// </summary>
    public void EnableDisableWindowControls(bool enabled)
    {
        // This method used to ALSO write _radioPowerOn, and that side effect
        // is why the connect earcon, the PC-audio policy and the REM ON queued
        // intent never ran once (#369, 2026-08-28): PowerNowOn calls this with
        // true BEFORE its own off→on transition guard, so the guard read a
        // flag this method had already raised and skipped its block on every
        // connect there has ever been. A method named "enable controls" must
        // not own the power lifecycle. The flag is now written only where the
        // power state actually changes: PowerNowOn (up), PowerNowOffInternal
        // and UnwireRadioEvents (down).
        foreach (var control in _enableDisableControls)
        {
            control.IsEnabled = enabled;
        }
        Tracing.TraceLine($"EnableDisableWindowControls: {enabled}", TraceLevel.Info);
    }

    #endregion

    #region Rescue Home — Sprint 30 Track A

    /// <summary>
    /// True while Home is the limited no-radio page.
    ///
    /// <para>The rescue page supersedes per-control gating on Home by
    /// construction: instead of a full Home whose every control has to remember
    /// to check for a radio, the page simply does not offer what cannot work.
    /// A control that is absent cannot lie, and — the part that matters over
    /// time — a control added to Home next year inherits the rule for free,
    /// because it is not on this page at all.</para>
    ///
    /// <para>SCOPE: startup only. A radio lost mid-session does NOT bring this
    /// page back. That case is a window transition during live operation, with
    /// every screen-reader flush lesson applying, and it wants its own design
    /// rather than a reuse of this one.</para>
    /// </summary>
    private bool _rescueMode;

    /// <summary>True while Home is showing the limited no-radio page.</summary>
    public bool InRescueMode => _rescueMode;

    /// <summary>
    /// Become the rescue page if the app finished starting up with no radio.
    /// Called from <see cref="SpeakWelcome"/>, which is the first moment the
    /// answer is knowable: the connect flow runs synchronously during startup,
    /// so before it returns, "no radio" only means "not yet".
    ///
    /// <para>Idempotent, and one-way within a session — once a radio has
    /// arrived, <see cref="ExitRescueMode"/> retires the page for good.</para>
    /// </summary>
    public void EnterRescueModeIfNoRadio()
    {
        if (_rescueMode || RigControl != null) return;
        _rescueMode = true;
        _rescueReason = "startup finished with no radio";
        Tracing.TraceLine(
            "Rescue Home: startup finished with no radio — showing the limited page",
            TraceLevel.Info);
        ApplyRescueVisibility();
    }

    /// <summary>
    /// Hide everything on Home that needs a radio and show the rescue page.
    /// Re-applied at the end of <see cref="ApplyUIMode"/> so a tuning-mode
    /// switch made while disconnected cannot quietly un-hide the radio
    /// controls behind the page.
    /// </summary>
    /// <summary>
    /// Why rescue mode engaged, for the trace. Set by whichever entry point
    /// raised <see cref="_rescueMode"/>.
    /// <para>#500 — the collapse had no reason attached to it anywhere, so even
    /// if a line had been written it could only have said THAT panels went, not
    /// why.</para>
    /// </summary>
    private string _rescueReason = "not recorded";

    /// <summary>
    /// Name the Home panels a screen-reader user can no longer Tab to, for the
    /// trace. Sprint 44 Track E (#500).
    /// <para>Reports the panels' ACTUAL state rather than what a method
    /// intended, so a line written after a collapse and a line written after a
    /// reconnect are directly comparable.</para>
    /// </summary>
    private string DescribeHomePanelVisibility()
    {
        static string One(string name, UIElement e)
            => name + "=" + (e.Visibility == Visibility.Visible ? "visible" : "collapsed");
        try
        {
            return One("RadioControls", RadioControlsPanel)
                + " " + One("Fields", FieldsPanel)
                + " " + One("Meters", MetersPanel)
                + " " + One("Panadapter", PanadapterPanel)
                + " " + One("Content", ContentArea)
                + " " + One("Logging", LoggingPanel);
        }
        catch (Exception ex)
        {
            // Never let the instrument be the thing that throws.
            return "unreadable: " + ex.Message;
        }
    }

    /// <summary>
    /// True when a radio is present but the operating surface is not. This is
    /// the #500 state exactly: a working radio and roughly a third of the
    /// interface missing, with nothing said and nothing logged.
    /// </summary>
    private bool HomePanelsCollapsedWithRadio =>
        RigControl != null
        && ActiveUIMode != UIMode.Logging
        && FieldsPanel.Visibility != Visibility.Visible
        && FieldsPanelUserVisible;

    /// <summary>
    /// A panel that vanishes without a word is indistinguishable from a crash
    /// to someone who cannot see the screen. Sprint 44 Track E (#500).
    /// </summary>
    /// <remarks>
    /// <para>Two instruments, because they answer different questions and one
    /// of them is not always available. The TRACE always runs and is what makes
    /// the state diagnosable after the fact; the SPEECH only runs when the
    /// operator is actually the one affected, and tells them the way back.</para>
    /// <para><b>Why the speech is deferred to Background priority.</b> This is
    /// reached from OnRadioStarted, in the middle of the connect flow, and the
    /// connect flow is a sequence of window changes — every one of which
    /// flushes a screen reader's queue. An utterance made on this line would be
    /// discarded before anyone heard it, which is the exact trap
    /// project_speech_flushes_on_window_change describes and the reason the
    /// rescue page carries its lead on a panel name instead of speaking it.
    /// Deferring lets the connect narration finish and the windows settle.</para>
    /// <para><b>And the condition is re-tested at that point</b>, so if
    /// anything did restore the panels in between, nothing is said. An
    /// announcement that is wrong by the time it is heard is worse than
    /// silence.</para>
    /// </remarks>
    private void ReportHomePanelsMissing()
    {
        // Wrapped because this sits on the connect path. An instrument that can
        // take down the thing it is watching is worse than no instrument, and
        // this one runs inside ExitRescueMode, which OnRadioStarted calls before
        // the rest of the radio is wired up.
        try
        {
            ReportHomePanelsMissingCore();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("ReportHomePanelsMissing: " + ex.Message, TraceLevel.Warning);
        }
    }

    private void ReportHomePanelsMissingCore()
    {
        if (!HomePanelsCollapsedWithRadio) return;

        Tracing.TraceLine(
            "Home panels missing with a radio present (#500/#410): ExitRescueMode returned early"
            + " because rescue mode was never entered, so ApplyUIMode never ran to restore them."
            + " | state: " + DescribeHomePanelVisibility()
            + " | FieldsPanelUserVisible=" + FieldsPanelUserVisible,
            TraceLevel.Error);

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!HomePanelsCollapsedWithRadio)
            {
                Tracing.TraceLine(
                    "Home panels missing: recovered before the announcement — saying nothing",
                    TraceLevel.Info);
                return;
            }
            string msg = Radios.Lexicon.Get("connect.home.fields_panel_missing");
            Tracing.TraceLine("Home panels missing: announcing — " + msg, TraceLevel.Info);
            // Critical so a quiet verbosity cannot filter out the one message
            // that explains a third of the interface being gone. Not an
            // interrupt: it must never cut the connect narration it follows.
            Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical);
        }));
    }

    private void ApplyRescueVisibility()
    {
        if (!_rescueMode) return;

        // Logging mode outranks the rescue page, and must. The log is a
        // logbook: entering QSOs, searching, importing and exporting all work
        // perfectly well with no radio attached, and an operator who pressed
        // Ctrl+Shift+L asked for that surface deliberately. Hiding it behind a
        // page whose whole claim is "only what works offline" would make the
        // page wrong about itself. Leaving Logging mode restores the page,
        // because ApplyUIMode runs this again on the way back.
        if (ActiveUIMode == UIMode.Logging)
        {
            RescuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        // #500 — SAY WHAT IS ABOUT TO GO, AND WHY.
        //
        // On 2026-09-01 this method removed all five expanders of the fields
        // panel from Noel's reach mid-session and the capture recorded
        // NOTHING: no exception, no line about the panel, nothing spoken. The
        // register's words are "the app lost roughly a third of its operating
        // surface and said nothing, and the diagnostic log cannot even confirm
        // it happened." Collapsing a panel is not an error, so nothing
        // reported it — which is exactly why it has to be reported on purpose.
        //
        // Before AND after, deliberately: the "before" state is the only record
        // of what the operator actually lost, and it is gone the moment the
        // next line runs.
        Tracing.TraceLine(
            "Rescue Home: collapsing the operating surface — reason: " + _rescueReason
            + " | before: " + DescribeHomePanelVisibility(),
            TraceLevel.Info);

        RescuePanel.Visibility = Visibility.Visible;
        RadioControlsPanel.Visibility = Visibility.Collapsed;
        FieldsPanel.Visibility = Visibility.Collapsed;
        MetersPanel.Visibility = Visibility.Collapsed;
        PanadapterPanel.Visibility = Visibility.Collapsed;
        ContentArea.Visibility = Visibility.Collapsed;
        LoggingPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = Radios.Lexicon.Get("connect.home.status_ready_no_radio");

        Tracing.TraceLine(
            "Rescue Home: collapsed — after: " + DescribeHomePanelVisibility()
            + " | route back: a radio connecting, which runs ExitRescueMode",
            TraceLevel.Info);
    }

    /// <summary>
    /// A radio arrived: retire the rescue page and hand Home back to the UI
    /// mode. Restoring through <see cref="ApplyUIMode"/> rather than by
    /// un-collapsing each panel means the connected layout is never rebuilt
    /// from this method's memory of what it used to look like — the mode
    /// builders stay the single source of truth for that.
    /// </summary>
    public void ExitRescueMode()
    {
        CancelRescueCountdown();
        if (!_rescueMode)
        {
            // #500 — THIS EARLY RETURN IS THE SILENT MOMENT, and it is the one
            // the evening of 2026-09-01 actually landed in.
            //
            // A disconnect runs RestoreNoRadioShell, which collapses Fields,
            // Meters and Panadapter WITHOUT entering rescue mode, and starts a
            // three-minute countdown. A radio arriving before that countdown
            // fires reaches here with _rescueMode still false, returns on this
            // line, and NEVER RUNS ApplyUIMode — which is the only thing that
            // un-collapses those three panels. ApplyUIMode is called on startup
            // and on an operator change; nothing on a reconnect calls it.
            //
            // So the operator gets a working radio with a third of the
            // interface gone, it survives further reconnects because
            // reconnecting is not what restores it, and a restart appears to
            // fix it. That is #410's mechanism seen from this side, and
            // repairing it is #410's job, not this track's.
            //
            // What this track owes it is that it stop being invisible.
            ReportHomePanelsMissing();
            return;
        }
        _rescueMode = false;
        Tracing.TraceLine("Rescue Home: radio arrived — restoring the full page", TraceLevel.Info);

        // Sprint 31 Track R — the reverse transition, and it has a real bug in
        // it. Read BEFORE anything collapses: if the operator's focus is sitting
        // on a rescue button and that button is about to disappear, WPF hands
        // focus to whatever happens to be next, which is the "keyboard stopped
        // working" failure FocusHome exists to prevent, arriving from the other
        // direction.
        bool pageHadFocus = RescuePanel.IsKeyboardFocusWithin;

        RescuePanel.Visibility = Visibility.Collapsed;
        // ContentArea is the one panel the mode builders never touch; its
        // children carry their own CW-mode visibility rules from there.
        ContentArea.Visibility = Visibility.Visible;
        ApplyUIMode(ActiveUIMode);

        // Reset the page's name so a lead that was never heard cannot outlive
        // the situation that produced it and greet the operator next time.
        RescuePanel.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, RescuePanelBaseName);

        if (!pageHadFocus) return;

        // The operator was standing ON the page when the radio came back. Put
        // them on the frequency display, which is where Home keeps focus once
        // there is a radio.
        FreqOut.FocusDisplay();

        // Speech is correct in THIS branch specifically, and the guard above is
        // what makes it correct. pageHadFocus can only be true when no window
        // took focus away — an auto-reconnect, not an operator-driven connect —
        // so there is no window change to flush it. On an operator-driven
        // connect the picker holds focus, this branch does not run, and the
        // connect flow's own "Connected to X" is the announcement.
        //
        // Deferred to Background so it lands after WPF has finished the focus
        // change and the screen reader has read where focus went, rather than
        // racing it.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.home.radio_back"), VerbosityLevel.Critical, interrupt: false));
    }

    #region Mid-session rescue — Sprint 31 Track R

    /// <summary>
    /// How long Home tolerates having no radio before it becomes the rescue
    /// page. Noel's number, and the delay is the design rather than a detail:
    /// a momentary drop that recovers on its own must not tear the operator's
    /// context away, and three minutes is long enough that the session is
    /// genuinely over. Radio ▸ Radio Rescue exists so nobody has to wait it out.
    /// </summary>
    private static readonly TimeSpan RescueGracePeriod = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The page's resting accessible name. A rescue arrival temporarily
    /// replaces it with itself plus a lead; the first focus into the page
    /// consumes the lead and puts this back.
    /// </summary>
    private const string RescuePanelBaseName = "Home, no radio connected";

    private DispatcherTimer? _rescueGraceTimer;

    /// <summary>
    /// Home has no radio: start the countdown to the rescue page.
    ///
    /// <para>Called from <see cref="RestoreNoRadioShell"/>, which is the single
    /// point where Home becomes a no-radio Home however the radio went away —
    /// operator disconnect, unexpected drop, or a failed reconnect. One rule
    /// for all three, because from Home's point of view they are the same
    /// state, and two descriptions of "no radio" is the whole reason this work
    /// exists.</para>
    /// </summary>
    private void BeginRescueCountdown()
    {
        if (_rescueMode) return;

        _rescueGraceTimer ??= new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = RescueGracePeriod
        };
        _rescueGraceTimer.Tick -= RescueGraceTimer_Tick;
        _rescueGraceTimer.Tick += RescueGraceTimer_Tick;
        _rescueGraceTimer.Stop();   // restart the full three minutes
        _rescueGraceTimer.Start();
        Tracing.TraceLine(
            $"Rescue Home: no radio on Home — rescue page in {RescueGracePeriod.TotalMinutes} minutes unless one arrives",
            TraceLevel.Info);
    }

    private void CancelRescueCountdown()
    {
        if (_rescueGraceTimer == null || !_rescueGraceTimer.IsEnabled) return;
        _rescueGraceTimer.Stop();
        Tracing.TraceLine("Rescue Home: radio arrived inside the grace period — countdown cancelled",
            TraceLevel.Info);
    }

    private void RescueGraceTimer_Tick(object? sender, EventArgs e)
    {
        _rescueGraceTimer?.Stop();
        // Re-check rather than trust the timer: a radio may have arrived
        // between the last tick scheduling and now.
        if (RigControl != null) return;
        EnterRescueMode("The radio has been gone for three minutes.");
    }

    /// <summary>
    /// Become the rescue page mid-session, carrying <paramref name="lead"/> to
    /// whoever arrives on the page.
    ///
    /// <para>THE CONSTRAINT, and why this does not simply speak: everything the
    /// operator must hear is carried BY the arriving surface, never spoken
    /// ahead of it. For a window that means the title. This is a panel inside a
    /// window that never changes, so the equivalent carrier is the panel's own
    /// accessible name — which a screen reader reads when focus enters the
    /// group, as part of the same announcement that names the button focus
    /// landed on. Nothing is queued, so nothing can be flushed.</para>
    ///
    /// <para>The lead is consumed once, on first focus into the page, exactly
    /// like globals.PendingDisconnectLead. That gives the behaviour that makes
    /// this correct rather than merely tidy: if the page arrives while a dialog
    /// is up, the lead is not lost and not spoken into a surface that cannot
    /// hold it — it WAITS for the operator to arrive, however long that
    /// takes.</para>
    /// </summary>
    public void EnterRescueMode(string lead)
    {
        if (_rescueMode || RigControl != null) return;
        _rescueMode = true;
        _rescueReason = "mid-session: " + lead;
        Tracing.TraceLine($"Rescue Home: entering mid-session — {lead}", TraceLevel.Info);

        RescuePanel.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, lead + " " + RescuePanelBaseName);
        RescuePanel.GotKeyboardFocus -= RescuePanel_GotKeyboardFocus;
        RescuePanel.GotKeyboardFocus += RescuePanel_GotKeyboardFocus;

        // The durable carrier, and the same line globals.vb writes when startup
        // finishes with no radio — so both routes into this page describe it
        // identically, which is the defect this task set out to close. A
        // successful connect replaces the whole title through UpdateTitleBar,
        // so it cannot go stale.
        UpdateTitleBar?.Invoke("JJ Flexible Radio Access — no radio connected");

        ApplyRescueVisibility();

        // Only take focus if Home already had it. A dialog, the radio picker or
        // the Audio Workshop may be in front, and yanking focus out of what the
        // operator is doing to announce that a radio is still missing would be
        // a worse offence than the silence it replaces. The lead keeps until
        // they come back.
        if (IsKeyboardFocusWithin) FocusRescuePage();
    }

    /// <summary>
    /// Consume the arrival lead. Read once, then the page goes back to its
    /// resting name so the operator does not hear the story of how they got
    /// here every time focus re-enters the page.
    /// </summary>
    private void RescuePanel_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RescuePanel.GotKeyboardFocus -= RescuePanel_GotKeyboardFocus;
        RescuePanel.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, RescuePanelBaseName);
    }

    #endregion

    /// <summary>
    /// Put keyboard focus on the rescue page. Returns false when the page is
    /// not up or refused focus, so callers can fall through to their normal
    /// landing spot rather than leaving focus nowhere.
    /// </summary>
    public bool FocusRescuePage()
    {
        if (!_rescueMode || RescuePanel.Visibility != Visibility.Visible) return false;
        return RescueConnectButton.Focus();
    }

    /// <summary>
    /// True when a window of THIS process other than our own shell currently
    /// holds the foreground — a chained dialog, or the Connecting window on
    /// its own thread. Used by the focus-return callback (#348): a dialog
    /// closing back into a flow that still owns the operator is not a return
    /// to Home, and treating it as one dragged focus (and an interrupting
    /// landing announcement) into the middle of every connect.
    /// </summary>
    internal bool AnotherOwnWindowHasForeground()
    {
        var fg = ForegroundProbe.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        ForegroundProbe.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != (uint)Environment.ProcessId) return false;

        // Our shell is the root window above the HwndSource hosting this
        // control. Not Process.MainWindowHandle: that property guesses from
        // z-order and can return whichever of our windows happens to be on
        // top — including the very window being tested.
        if (PresentationSource.FromVisual(this)
            is not System.Windows.Interop.HwndSource source) return true;
        var shell = ForegroundProbe.GetAncestor(source.Handle, ForegroundProbe.GA_ROOT);
        return fg != shell;
    }

    private static class ForegroundProbe
    {
        internal const uint GA_ROOT = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }

    #region Connect-flow quiet scope — Sprint 42 Track D (#395)

    // The connect path tears down and rebuilds the Home surface, swaps a
    // 228-item menu bar, and closes two windows over the shell, all in the one
    // second the operator is listening hardest — right after we asked them to
    // pay attention with "Connecting to K5NER". Each activation change and
    // focus restore made the reader announce the window and the landing spot:
    // five window announcements wedged between our own two sentences, measured
    // from the operator's own capture on 2026-08-29.
    //
    // Nothing in that churn is a wrong line — the menus must change, the
    // fields must rebuild. The defect is WHEN the byproducts are allowed to
    // speak. This scope marks "a connect flow owns the operator's ears" so the
    // byproducts stay quiet, and exactly one deliberate landing happens at the
    // end:
    //
    //  - the frequency display's focus-landing prefix stays silent (the
    //    operator did not move; being re-told "JJ Flexible Home, slice" twice
    //    mid-connect is noise, not orientation);
    //  - the dialog-close focus-return landing stands down (the #348
    //    foreground guard already catches most of this, but it races the
    //    Connecting window's arrival — the picker can close BEFORE that window
    //    takes the foreground, and the guard then read "nothing else is up"
    //    and dragged focus, foreground and an announcement into mid-connect);
    //  - when the flow finishes, ONE normalization runs: on success, focus is
    //    quietly ensured to be somewhere real and the connect narration is the
    //    announcement; on cancel or failure, the standard return-to-app
    //    landing runs exactly as it always did, prefix included.
    //
    // The scope is entered by whichever door opens first (the menu command,
    // the rescue button, or WireRadioEvents for auto-connect and retry legs)
    // and finished from a Background-priority dispatcher post, so every
    // pending focus event and queued rebuild drains inside the scope. A
    // generation counter keeps a finish posted by an abandoned leg (the retry
    // ladder unwires and rewires mid-flow) from ending the scope of the leg
    // that superseded it.

    // THE STATE MACHINE LIVES IN Radios.ConnectQuietScope, NOT HERE. Track
    // D's original implementation kept it as loose fields in this file with
    // zero assertions over it, and the first stuck scope reached the
    // operator as a two-minute total lockout (2026-08-30, three times in one
    // day: keyboard dead, screen reader silent, the failsafe's landing the
    // only thing that ever brought either back). Every decision is now in
    // the pure class where Radios.Tests pins every exit; this file keeps
    // only the dispatcher, the timers, and the landing. The doors' pump
    // hazard — a finish posted from INSIDE the flow running while the
    // Connecting window is still up — is documented on the class.

    /// <summary>All quiet-scope decisions. See <see cref="Radios.ConnectQuietScope"/>.</summary>
    private readonly Radios.ConnectQuietScope _connectQuiet = new();

    /// <summary>
    /// Last line of defence, and a short one: a stuck scope silences the
    /// focus-landing prefix and every dialog-close landing, and the
    /// 2026-08-30 lockouts proved a stuck scope can also be what keeps a
    /// stranded keyboard stranded. The interval and its reasoning live at
    /// <see cref="Radios.ConnectQuietScope.FailsafeMs"/> — ten seconds, not
    /// the original 120, because two minutes of silence reads as a crash to
    /// a blind operator and twice he killed the process rather than wait.
    /// Firing is routine (the doors bracket the whole modal picker, so any
    /// unhurried browse outlives the deadline); the finish's landing stands
    /// down when a window of ours holds the foreground, so a healthy expiry
    /// only lifts the suppressions, silently.
    /// </summary>
    private DispatcherTimer? _connectQuietFailsafe;

    /// <summary>
    /// Enter the quiet scope. Idempotent — the first entry wins, and a
    /// re-entry from a retry leg just extends the same scope. Pass
    /// <paramref name="door"/> true from the call sites that BRACKET the
    /// whole flow (menu command, rescue button); their matching end request
    /// is the only one honored while they are open.
    /// </summary>
    internal void BeginConnectFlowQuiet(string reason, bool door = false)
    {
        var kind = _connectQuiet.Begin(door);
        if (kind == Radios.ConnectQuietScope.BeginKind.Fresh)
        {
            Tracing.TraceLine($"ConnectQuiet: begin ({reason})", TraceLevel.Info);
        }
        else
        {
            Tracing.TraceLine($"ConnectQuiet: extended ({reason})", TraceLevel.Verbose);
        }

        if (_connectQuietFailsafe == null)
        {
            _connectQuietFailsafe = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Radios.ConnectQuietScope.FailsafeMs)
            };
            _connectQuietFailsafe.Tick += (s, e) =>
            {
                _connectQuietFailsafe!.Stop();
                if (!_connectQuiet.IsQuiet) return;
                Tracing.TraceLine(
                    "ConnectQuiet: failsafe expired after "
                    + $"{Radios.ConnectQuietScope.FailsafeMs / 1000}s - the flow is still "
                    + "open, closing the scope so announcements come back (#395)",
                    TraceLevel.Warning);
                FinishConnectFlowQuiet(fromFailsafe: true);
            };
        }
        _connectQuietFailsafe.Stop();
        _connectQuietFailsafe.Start();
    }

    /// <summary>
    /// Request the end of the quiet scope. The actual finish is posted at
    /// Background priority so the activation changes, focus restores and
    /// queued rebuilds still in flight land INSIDE the scope, not after it.
    /// Safe to call from every exit of every door — a finish from a
    /// superseded leg no-ops via the generation counter, and a finish with no
    /// scope open no-ops entirely.
    /// </summary>
    internal void EndConnectFlowQuiet(string reason, bool door = false)
    {
        switch (_connectQuiet.RequestEnd(door))
        {
            case Radios.ConnectQuietScope.EndDecision.NotOpen:
                return;

            case Radios.ConnectQuietScope.EndDecision.DeferredToDoor:
                // A door still holds the flow open — its finally will end the
                // scope after the flow truly returns. Honoring an inner end
                // here would let a message pump run the finish while the
                // Connecting window is still up.
                Tracing.TraceLine($"ConnectQuiet: end deferred to open door ({reason})",
                    TraceLevel.Verbose);
                return;
        }

        Tracing.TraceLine($"ConnectQuiet: end requested ({reason})", TraceLevel.Verbose);
        int gen = _connectQuiet.Generation;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_connectQuiet.ShouldRunPostedFinish(gen)) return; // a newer leg owns the scope
            FinishConnectFlowQuiet();
        }));
    }

    /// <summary>
    /// The one deliberate landing at the end of a connect flow.
    /// <paramref name="fromFailsafe"/> marks a finish the failsafe forced: if
    /// the landing actually has to intervene then, it announces that
    /// announcements are back — a rescued scope is a real event the operator
    /// must hear about, not a silent recovery leaving minutes unexplained.
    /// </summary>
    private void FinishConnectFlowQuiet(bool fromFailsafe = false)
    {
        var kind = _connectQuiet.DecideFinish();
        if (kind == Radios.ConnectQuietScope.FinishKind.NotOpen) return;
        _connectQuietFailsafe?.Stop();

        if (kind == Radios.ConnectQuietScope.FinishKind.PowerOnQuietNormalize)
        {
            // Success. The connect narration ("PC audio on…", "Connected to
            // FLEX-8600…") is the announcement; this landing only makes sure
            // focus is somewhere real. Done while the scope is still marked
            // quiet so the landing itself is silent — the hybrid shell can
            // strand focus on the unnamed host pane across window churn, and
            // a silent FocusHome here is the repair with none of the noise.
            try
            {
                if (!AnotherOwnWindowHasForeground() && !IsKeyboardFocusWithin)
                    FocusHome();
            }
            catch (System.Exception ex)
            {
                Tracing.TraceLine(
                    $"ConnectQuiet: silent focus normalization failed: {ex.Message}",
                    TraceLevel.Warning);
            }
            _connectQuiet.Close();
            Tracing.TraceLine("ConnectQuiet: finish - radio powered on, narration owns the announcement",
                TraceLevel.Info);
            return;
        }

        // Cancel or failure. Nothing powered on, so nothing else will tell
        // the operator where they are — run the standard return-to-app
        // landing exactly as a dialog close outside a connect would have,
        // prefix announcement included (the scope is cleared first so the
        // landing speaks).
        _connectQuiet.Close();
        Tracing.TraceLine("ConnectQuiet: finish - no power-on, running the return-to-app landing",
            TraceLevel.Info);
        RunReturnToAppLanding(fromFailsafe ? QuietScopeRescueLead : null);
    }

    /// <summary>
    /// Spoken ahead of the landing when the FAILSAFE closed the scope and the
    /// landing actually intervened — the operator has just lived through a
    /// stretch where announcements (and possibly the keyboard) were dead, and
    /// this is the one sentence that says the outage is over. A healthy
    /// failsafe expiry (the picker browsed at leisure, foreground still ours)
    /// stands down at the landing's guard and stays silent, so this never
    /// fires as a false alarm. A literal, not a Lexicon key, only because the
    /// connect partition is owned by parallel work right now; it belongs in
    /// connect.json when that file is free.
    /// </summary>
    private const string QuietScopeRescueLead = "Announcements are back on.";

    /// <summary>One-shot timer behind <see cref="ScheduleQuietScopeFocusRescue"/>.</summary>
    private DispatcherTimer? _quietFocusRescue;

    /// <summary>
    /// The quiet scope's replacement for the focus half of the dialog-close
    /// landing it stands down. Waits long enough for the flow's next window
    /// to arrive (see <see cref="Radios.ConnectQuietScope.StrandedFocusRescueDelayMs"/>),
    /// then repairs keyboard focus SILENTLY if nothing else did.
    ///
    /// This is the fix for the 2026-08-30 lockouts. The traces from all
    /// three agree: the discovering window closed into the opening picker,
    /// the foreground escaped, the stand-down skipped the one repair that
    /// ever ran on that edge, and the operator sat with a dead keyboard and
    /// a silent reader — over a pumping, healthy UI thread the whole time —
    /// until the failsafe's landing did at 120 seconds exactly what this
    /// does at three quarters of one.
    /// </summary>
    private void ScheduleQuietScopeFocusRescue()
    {
        if (_quietFocusRescue == null)
        {
            _quietFocusRescue = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(
                    Radios.ConnectQuietScope.StrandedFocusRescueDelayMs)
            };
            _quietFocusRescue.Tick += (s, e) =>
            {
                _quietFocusRescue!.Stop();
                RescueStrandedFocusQuietly();
            };
        }
        _quietFocusRescue.Stop();
        _quietFocusRescue.Start();
    }

    /// <summary>
    /// The deferred check itself: same guards as the landing, none of the
    /// speech. Every early return is a healthy outcome; only a genuinely
    /// stranded keyboard is repaired, and silently — the scope is still
    /// open, so the flow's own narration keeps the microphone.
    /// </summary>
    private void RescueStrandedFocusQuietly()
    {
        try
        {
            // The scope closed in the meantime — its finish ran the full
            // landing, repair included.
            if (!_connectQuiet.IsQuiet) return;

            // A window of the flow (the picker, the Connecting window) holds
            // the foreground: the operator is where the flow put them.
            if (AnotherOwnWindowHasForeground()) return;

            // Focus is alive inside the shell.
            if (IsKeyboardFocusWithin) return;

            Tracing.TraceLine(
                "ConnectQuiet: focus was stranded with no window of ours in the "
                + "foreground - repairing silently (#395 lockout guard)",
                TraceLevel.Info);
            Radios.WindowActivation.EnsureForeground(
                System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
            FocusHome();
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine(
                $"ConnectQuiet: silent focus rescue failed: {ex.Message}",
                TraceLevel.Warning);
        }
    }

    /// <summary>
    /// Focus repair after a native menu command — replaces the unconditional
    /// <c>_window.Focus()</c> the WM_COMMAND epilogue used to run, which
    /// yanked keyboard focus off whatever field the operator was on and
    /// parked it on this UserControl's ROOT. A screen reader read the root's
    /// automation name — "JJ Flexible Radio Access Main Window" — after every
    /// menu action, and the operator was left somewhere arrow keys mean
    /// nothing. Now: if focus survived inside the content, leave it alone; if
    /// another of our windows owns the operator (a workshop the command
    /// opened, the Connecting window), stand down; only when focus genuinely
    /// escaped, land it on Home through the one funnel that knows where Home
    /// keeps it.
    /// </summary>
    internal void ReclaimFocusAfterMenuCommand()
    {
        try
        {
            if (IsKeyboardFocusWithin) return;
            if (AnotherOwnWindowHasForeground()) return;
            FocusHome();
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine(
                $"ReclaimFocusAfterMenuCommand failed: {ex.Message}", TraceLevel.Warning);
        }
    }

    /// <summary>
    /// The standard "a dialog closed back into the app" landing — focus
    /// repair plus compact orientation speech. Extracted verbatim from the
    /// <see cref="JJFlexDialog.FocusReturnCallback"/> lambda (Sprint 42
    /// Track D) so the connect flow's finish can run the identical landing
    /// when a flow ends without a radio: same guards, same speech, same #348
    /// and #349 reasoning.
    /// </summary>
    private void RunReturnToAppLanding(string? rescueLead = null)
    {
        try
        {
            // A dialog can close in the MIDDLE of a flow that still owns
            // the operator: the rig selector closes while the Connecting
            // window — its own thread, its own narration — is still up, or
            // a nested dialog closes back into its parent. Grabbing focus
            // at those moments dragged the operator to Home between every
            // window of a connect; each landing announced "JJ Flexible
            // Home…" with an interrupt, and every interrupt re-queued
            // whatever the speech arbiter still held unspoken (#348: three
            // such landings in one connect, two of them 129 ms apart).
            // When any other window of ours holds the foreground, the
            // return is not ours to handle yet — and the status narration
            // below would talk over that window too, so leave entirely.
            if (AnotherOwnWindowHasForeground())
            {
                Tracing.TraceLine(
                    "FocusReturnCallback: another window of ours holds the foreground - standing down",
                    System.Diagnostics.TraceLevel.Info);
                return;
            }

            // The failsafe's rescue announcement, spoken only once the guard
            // above has passed: a landing that proceeds after a forced scope
            // close means announcements really were stuck, and this one
            // sentence is what tells the operator the outage is over before
            // the landing describes where they are. Queued, so it lands in
            // order ahead of the status below; Critical, because "speech is
            // working again" must be heard at any verbosity.
            if (rescueLead != null)
            {
                Radios.ScreenReaderOutput.Speak(rescueLead,
                    Radios.Speech.SpeechIntent.Queue,
                    Radios.VerbosityLevel.Critical);
            }

            // Only intervene when focus actually escaped. A dialog opened
            // from a field should return to that field, and WPF manages
            // that correctly whenever it can - overriding it every time
            // would move the operator somewhere they did not ask to be.
            if (!IsKeyboardFocusWithin)
            {
                Radios.WindowActivation.EnsureForeground(
                    System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
                FocusHome();
            }
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine(
                $"FocusReturnCallback: restore failed: {ex.Message}",
                System.Diagnostics.TraceLevel.Warning);
        }

        var rig = RigControl;
        if (rig != null && rig.MyNumSlices > 0)
        {
            // Compact status on return — but only once the radio's slice
            // census has arrived. This is the same MyNumSlices test
            // SpeakConnectStatus applies, and it was missing here: the
            // connecting flow's dialogs close before slices populate, so
            // this path spoke "Connected to FLEX-8600, no active slice" —
            // false within two seconds — 77 ms after the Connecting window
            // had already said "Connected… waiting for slice" (#348).
            // While slices are absent the connect flow owns the narration;
            // once they exist, this is real information about where the
            // operator returned to.
            string status = Radios.RadioStatusBuilder.BuildSpokenStatus(rig);
            Radios.ScreenReaderOutput.Speak(status, Radios.VerbosityLevel.Chatty);
        }
        else if (rig == null)
        {
            // #349: with no radio, nothing announces the return. The Home
            // display carries no populated fields on a cold start, so the
            // landing announcement that rides GotKeyboardFocus can stay
            // silent, and the interop layer may restore focus without
            // raising anything a screen reader voices — the only thing
            // Noel heard was the unnamed host pane, as "Pane". Say where
            // they came back to. Queued, not interrupting, so it lands
            // behind whatever the close itself announced; Critical, since
            // connection state is the always-spoken class.
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.no_radio.plain",
                    Radios.ScreenReaderOutput.CurrentVerbosity),
                Radios.Speech.SpeechIntent.Queue,
                Radios.VerbosityLevel.Critical);
        }
    }

    #endregion

    /// <summary>
    /// Land keyboard focus wherever Home currently keeps it: the rescue page's
    /// first button when the page is up, the frequency display otherwise.
    ///
    /// <para>Every "put the user back on Home" path goes through here. With no
    /// radio the frequency display is COLLAPSED, and WPF's Focus() on a
    /// collapsed element simply fails — focus then stays on a dismissed dialog
    /// or falls to the ElementHost pane, which a screen reader reads as "pane"
    /// and an operator experiences as the keyboard having stopped working.
    /// That failure is invisible in a diff, which is exactly why it gets one
    /// funnel instead of a check at each call site.</para>
    /// </summary>
    public void FocusHome()
    {
        if (FocusRescuePage()) return;
        FreqOut.FocusDisplay();
    }

    private void RescueConnectButton_Click(object sender, RoutedEventArgs e)
    {
        // No pre-announcement here, on purpose. The connect flow opens its own
        // windows, and a screen reader flushes its queue at every window
        // change — anything said now is destroyed before it is heard. The
        // arriving window carries its own state (globals.PendingDisconnectLead
        // is the same pattern from the other direction).
        if (SelectRadioCallback == null)
        {
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.home.not_ready_to_connect"),
                VerbosityLevel.Critical, true);
            return;
        }

        // Sprint 42 Track D (#395): the whole flow — picker, Connecting
        // window, Start — runs inside this call, and its window churn must
        // not narrate itself over the connect narration. The end request is
        // in a finally so a cancelled picker or a thrown connect can never
        // leave the scope stuck; the finish decides between "narration owns
        // the announcement" and "run the return-to-app landing".
        BeginConnectFlowQuiet("rescue connect button", door: true);
        try
        {
            SelectRadioCallback.Invoke();
        }
        finally
        {
            EndConnectFlowQuiet("rescue connect flow returned", door: true);
        }
    }

    /// <summary>
    /// Sprint 31 Track R — the one addition Noel asked for on this page, in his
    /// own words: "if you can't connect, perhaps one needs to setup a radio /
    /// enroll it."
    ///
    /// <para>A deep link rather than a plain Settings open, and the distinction
    /// is the whole point: a page that exists because the operator is stuck
    /// must not answer a stuck operator with directions. The tab name is the
    /// SAME string the firmware advisory has used since Sprint 29
    /// (MainWindow.FirmwareAdvisory.cs), so this reuses a route already proven
    /// to land, rather than inventing a second one.</para>
    ///
    /// <para>Kept as its own button rather than repointing the Settings button:
    /// Settings is the general door, and an operator with no radio may equally
    /// want Audio, Updates or Diagnostics. Repointing it would buy back one tab
    /// stop by removing the only general route off the page.</para>
    /// </summary>
    private void RescueRadioSetupButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsCallback?.Invoke("Radio Setup");
    }

    private void RescueSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Empty string, not a tab name: Settings opens on its default tab.
        // SelectTabByHeader simply reports false for an unmatched header.
        OpenSettingsCallback?.Invoke(string.Empty);
    }

    private void RescueWorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        // Deliberately on this page. The Workshop's "This Computer" section and
        // its microphone check exist precisely so an operator can prove their
        // input works with no radio in the picture; its radio-side sections
        // disable themselves — see AudioWorkshopDialog.UpdateRadioControlAvailability.
        Dialogs.AudioWorkshopDialog.ShowOrFocus(RigControl, 0);
    }

    private void RescueHelpButton_Click(object sender, RoutedEventArgs e)
    {
        // Open at the page that describes THIS page, not the help file's
        // front door — the operator pressing Help here is asking about the
        // room they are standing in.
        HelpLauncher.ShowHelp("RescueHome");
    }

    private void RescueExitButton_Click(object sender, RoutedEventArgs e)
    {
        CloseShellCallback?.Invoke();
    }

    #endregion

    #region UI Mode Management — Phase 8.5

    /// <summary>
    /// UI mode enum — mirrors globals.vb UIMode.
    /// </summary>
    public enum UIMode
    {
        Classic,
        Modern,
        Logging
    }

    /// <summary>
    /// Current active UI mode.
    /// </summary>
    public UIMode ActiveUIMode { get; private set; } = UIMode.Modern;

    /// <summary>
    /// The operator's field-panel preference, remembered across mode switches
    /// so that going Classic → Modern → Classic does not silently re-show a
    /// panel they hid. Sprint 15 Track D.
    /// </summary>
    /// <remarks>
    /// <para><b>SESSION-SCOPED. It is not persisted, and this doc comment said
    /// it was until 2026-09-02</b> — "Persisted to operator profile via
    /// SaveFieldsPanelVisibleCallback". That callback and its Load counterpart
    /// were declared here in Sprint 15, invoked from three places, and
    /// <b>never assigned anywhere in the solution</b>, so both were permanently
    /// null and the load branch that read the profile never once executed.
    /// They are deleted rather than wired; see the Track E report and the
    /// register note on #500 for why. In short: nothing has depended on them
    /// for eighteen sprints, and persisting this particular preference would
    /// make the Alt+N trap permanent instead of survivable.</para>
    /// <para>The practical consequence, and it is the one the #500 entry got
    /// backwards: a restart brings the panel back because this defaults to
    /// true, NOT because a saved value was reloaded.</para>
    /// </remarks>
    public bool FieldsPanelUserVisible { get; set; } = true;

    /// <summary>
    /// Last non-logging mode (Classic or Modern). Restored when exiting Logging Mode.
    /// Matches globals.vb LastNonLogMode.
    /// </summary>
    public UIMode LastNonLogMode { get; set; } = UIMode.Modern;

    /// <summary>
    /// Apply the specified UI mode — show/hide menus and panels.
    /// Central dispatcher matching Form1.ApplyUIMode().
    /// </summary>
    public void ApplyUIMode(UIMode mode)
    {
        ActiveUIMode = mode;
        Tracing.TraceLine($"ApplyUIMode: {mode}", TraceLevel.Info);

        // Route menu mode change to WinForms MenuStripBuilder
        MenuModeCallback?.Invoke(mode);

        switch (mode)
        {
            case UIMode.Classic:
                ShowClassicUI();
                break;
            case UIMode.Modern:
                ShowModernUI();
                break;
            case UIMode.Logging:
                ShowLoggingUI();
                break;
        }

        // Rescue Home outranks the mode builders while it is up: every builder
        // above un-collapses radio controls unconditionally, so without this a
        // tuning-mode switch made with no radio would put the frequency display
        // and the CW text boxes back into the tab order behind the page.
        ApplyRescueVisibility();
    }

    /// <summary>
    /// Show Classic mode: radio controls visible, logging hidden.
    /// Rebuilds FreqOut with full field set if radio is connected.
    /// </summary>
    private void ShowClassicUI()
    {
        RadioControlsPanel.Visibility = Visibility.Visible;

        // Restore user's field panel preference (Sprint 15 Track D). The
        // LoadFieldsPanelVisibleCallback branch that used to stand here was
        // dead — the callback was never assigned — so this line is what has
        // always decided the panel's visibility.
        FieldsPanel.Visibility = FieldsPanelUserVisible ? Visibility.Visible : Visibility.Collapsed;

        SetTextAreasVisible(true);
        LoggingPanel.Visibility = Visibility.Collapsed;

        // Rebuild FreqOut with Classic field set if radio is connected
        if (RigControl != null && _radioPowerOn)
            SetupFreqoutClassic();
    }

    /// <summary>
    /// Show Modern mode: radio controls visible, logging hidden.
    /// Rebuilds FreqOut with simplified field set if radio is connected.
    /// </summary>
    private void ShowModernUI()
    {
        RadioControlsPanel.Visibility = Visibility.Visible;

        // Respect user's field panel preference (same as Classic; the dead
        // LoadFieldsPanelVisibleCallback branch removed 2026-09-02).
        FieldsPanel.Visibility = FieldsPanelUserVisible ? Visibility.Visible : Visibility.Collapsed;

        SetTextAreasVisible(true);
        LoggingPanel.Visibility = Visibility.Collapsed;

        // Rebuild FreqOut with Modern field set if radio is connected
        if (RigControl != null && _radioPowerOn)
            SetupFreqoutModern();
    }

    /// <summary>
    /// Show Logging mode: radio controls hidden, log panel visible.
    /// </summary>
    private void ShowLoggingUI()
    {
        RadioControlsPanel.Visibility = Visibility.Collapsed;
        FieldsPanel.Visibility = Visibility.Collapsed;
        SetTextAreasVisible(false);
        LoggingPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Toggle Logging Mode on/off.
    /// Matches Form1.ToggleLoggingMode().
    /// </summary>
    public void ToggleLoggingMode()
    {
        if (ActiveUIMode == UIMode.Logging)
            ExitLoggingMode();
        else
            EnterLoggingMode();
    }

    /// <summary>
    /// Toggle between Classic and Modern tuning modes.
    /// Menus are unified — this only changes tuning behavior (FreqOut field set).
    /// </summary>
    public void ToggleUIMode()
    {
        if (ActiveUIMode == UIMode.Logging)
        {
            return;
        }

        var newMode = ActiveUIMode == UIMode.Classic ? UIMode.Modern : UIMode.Classic;
        LastNonLogMode = newMode;
        ApplyUIMode(newMode);
        SaveUIModeCallback?.Invoke(newMode);

        // Sprint 32 Track E, #128. All three roads to tuning mode -- the
        // Ctrl+Shift+M chord, the Slice menu and the Tools menu -- come through
        // here, so one tone covers them all. Modern is the "on" end, matching
        // the checked state the menu items show. This one earns a tone more
        // than most: it changes what the arrow keys do, and the sentence that
        // follows takes several seconds to say.
        EarconPlayer.ToggleTone(newMode == UIMode.Modern);

        // Sprint 26 Phase 8: mode-change announcement includes the brief tuning-key
        // summary for the new mode. Addresses Don's discoverability gap — without
        // this, operators toggling modes have no cue that the keys have changed.
        // Verbose on first-focus-after-load would annoy regular users; once-per-
        // mode-switch is the right cadence.
        //
        // Chatty users get a fuller coaching paragraph; Terse users get the brief
        // hint. Branched on CurrentVerbosity so we never speak both back-to-back
        // (both Terse and Chatty messages pass the filter for a Chatty user).
        bool chatty = Radios.ScreenReaderOutput.CurrentVerbosity == VerbosityLevel.Chatty;
        // Four plain keys, NOT a two-tier ladder. This is one of the binary
        // CurrentVerbosity ternaries the contract lists separately from the
        // three real ladders, and the store's shipped-ladder test requires all
        // three tiers, critical included. There is no critical wording here to
        // extract and inventing one would be editing. Reported as a ladder
        // candidate instead of built.
        string hint = Radios.Lexicon.Get(
            newMode == UIMode.Classic
                ? (chatty ? "connect.home.tuning_hint_classic_chatty"
                          : "connect.home.tuning_hint_classic_terse")
                : (chatty ? "connect.home.tuning_hint_modern_chatty"
                          : "connect.home.tuning_hint_modern_terse"));
        Radios.ScreenReaderOutput.Speak(hint, VerbosityLevel.Terse);
    }

    /// <summary>
    /// Enter Logging Mode from either Classic or Modern.
    /// Matches Form1.EnterLoggingMode().
    ///
    /// In pure WPF, this is beautifully simple:
    /// - Set Visibility on the Grid rows (no ElementHost container issues)
    /// - Focus moves naturally to LogEntryControl.CallSignBox
    /// - No "unknown" announcement (no intermediate containers)
    /// - No focus trapping (Visibility.Collapsed removes from tab order)
    /// </summary>
    public void EnterLoggingMode()
    {
        if (ActiveUIMode == UIMode.Logging)
            return;

        LastNonLogMode = ActiveUIMode;
        ApplyUIMode(UIMode.Logging);

        // Update RadioPane with current radio state
        LoggingRadioPane.UpdateFromRadio();

        // Focus the CallSign field in LogEntryControl.
        // In pure WPF, this just works — no BeginInvoke, no ElementHost dance.
        LoggingLogEntry.FocusCallSign();

        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.entering_logging"), VerbosityLevel.Terse);
    }

    /// <summary>
    /// Exit Logging Mode, returning to last non-logging mode.
    /// Matches Form1.ExitLoggingMode().
    ///
    /// The ElementHost bugs that Sprint 7 fought are GONE:
    /// - No Keyboard.ClearFocus() needed (no WPF focus retained in hidden host)
    /// - No two-step focus dance (move WinForms first, then hide)
    /// - Just Visibility.Collapsed → focus moves naturally to visible content
    /// </summary>
    public void ExitLoggingMode()
    {
        if (ActiveUIMode != UIMode.Logging)
            return;

        // Phase 8.8+: Check for unsaved QSO, save LogPanel state

        ApplyUIMode(LastNonLogMode);

        // Focus FreqOut display (the primary control in Classic/Modern modes),
        // or the rescue page when that is what Home currently is.
        FocusHome();

        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("connect.home.returning_to_mode", ("mode", LastNonLogMode)),
            VerbosityLevel.Terse);
    }

    #endregion

    #region Keyboard Routing — Phase 8.6

    /// <summary>
    /// Delegate for routing keyboard commands to the VB.NET KeyCommands system.
    /// Set by ApplicationEvents.vb after creating the KeyCommands instance.
    /// Takes a WinForms Keys value, returns true if the key was consumed.
    /// </summary>
    public Func<System.Windows.Forms.Keys, bool>? DoCommandHandler { get; set; }

    /// <summary>
    /// The live KeyCommands registry instance. Set by ApplicationEvents.vb
    /// right after the DoCommandHandler wiring. The Keys dialog (Tools →
    /// Hotkey Editor / Help → Key Assignments) edits bindings through this.
    /// </summary>
    public KeyCommands? KeyCommandsRef { get; set; }

    /// <summary>
    /// Registry handler target for CommandValues.ToggleFreqReadout — toggles
    /// the frequency speech readout. Radio scope guards the no-radio case in
    /// dispatch, but keep a spoken fallback for safety.
    /// </summary>
    public void ToggleFreqReadoutCommand()
    {
        if (_freqOutHandlers != null)
            _freqOutHandlers.ToggleFreqReadout();
        else
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.no_radio"),
                VerbosityLevel.Critical, true);
    }

    /// <summary>
    /// Window-level PreviewKeyDown — intercepts ALL keys before child controls.
    /// Replaces Form1.ProcessCmdKey override.
    ///
    /// Priority order:
    /// 1. Hard-wired meta-commands (Ctrl+Shift+M/L for mode switching)
    /// 2. Scope-aware KeyCommands.DoCommand() via DoCommandHandler delegate
    /// 3. Pass through to focused control (WPF default behavior)
    ///
    /// This is THE fix for the ElementHost keyboard forwarding hack.
    /// In pure WPF, PreviewKeyDown on the Window sees every key, period.
    /// No more PreviewKeyDown→BeginInvoke→Form1 chain.
    ///
    /// Alt and F10 are NOT handled here — they activate the native Win32 HMENU
    /// menu bar automatically via DefWindowProc in the WinForms message loop.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Sprint 28 Phase 3.5 (2026-04-21) — the Ctrl+Tab short-circuit that
        // opened the action toolbar has been removed. Ctrl+Tab / Ctrl+Shift+Tab
        // now flow through to ScreenFieldsPanel.FocusNextExpander, restoring
        // the industry-standard pane-navigation pattern. The action toolbar's
        // popup-menu form is disabled pending redesign (see memory
        // project_action_toolbar_redesign.md). ShowActionToolbar /
        // ExecuteActionToolbarItem code retained in this file for the future
        // redesign to build on.
        var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;
        // Let regular Tab (and Ctrl+Tab / Ctrl+Shift+Tab) pass through to
        // WPF default handling / child-panel PreviewKeyDown handlers.
        if (rawKey == Key.Tab)
            return;

        // 1. Former hard-wired meta-commands (Ctrl+Shift+M tuning mode,
        // Ctrl+Shift+L logging mode, Ctrl+Shift+F frequency readout, and
        // Ctrl+Alt+F read filter values) now live in the KeyCommands registry
        // as ToggleTuningMode / ToggleLoggingMode / ToggleFreqReadout /
        // SpeakRXFilter — dispatched at step 4 below. QB Track H (2026-08-07):
        // the hard-wired versions silently shadowed registry bindings on the
        // same chords (MemoryScan, SpeakFrequency, SearchLog-in-Logging) and
        // were invisible to the Keys surface and Command Finder.

        // 1c. Universal Home keys (M/V/R/X/Q/=) no-radio guard.
        //
        // These keys are field-handler-bound — they only fire when focus is on a
        // Home field. With no radio, FrequencyDisplay's _fieldDict is empty so
        // focus can't be on a Home field, and the keys silently do nothing —
        // a violation of the no-silent-keystrokes rule. Speak no-radio guidance
        // and consume so the user gets audible feedback at window scope.
        //
        // Gated on FreqOut.IsKeyboardFocusWithin so we only fire where a Home
        // field WOULD have focus if a radio were connected; outside Home (menu
        // bar, settings dialog, command finder) the keys pass through to be
        // typed normally. With a radio connected, the guard skips and the
        // normal field-handler routing wins.
        //
        // Shift+M and Shift+, (multi-slice variants) are intentionally NOT in
        // this list — they're bound in KeyCommands (Radio scope) and the
        // DoCommand-layer guard at f8c64d57 already covers them.
        //
        // Sprint 30 Track A: the rescue page joins the gate. With no radio the
        // frequency display is collapsed, so FreqOut can never hold focus and
        // this guard would never fire again — these keys would go back to being
        // silent in exactly the situation they were written for.
        if (RigControl == null
            && Keyboard.Modifiers == ModifierKeys.None
            && (FreqOut.IsKeyboardFocusWithin
                || (_rescueMode && RescuePanel.IsKeyboardFocusWithin))
            && IsUniversalHomeKey(rawKey))
        {
            Radios.ScreenReaderOutput.SpeakNoRadioConnected();
            e.Handled = true;
            return;
        }

        // 2. Filter hotkeys (bracket keys) — Modern and Classic modes (not Logging)
        if (ActiveUIMode != UIMode.Logging && _freqOutHandlers != null && _radioPowerOn)
        {
            // Escape cancels filter edge selection mode
            if (rawKey == Key.Escape && _freqOutHandlers.InFilterEdgeMode)
            {
                _freqOutHandlers.CancelFilterEdgeMode();
                e.Handled = true;
                return;
            }
            // Escape also cancels RIT/XIT scale-adjust mode. The field handler
            // catches Escape when focus is still on RIT/XIT, but if focus has
            // drifted (or the user simply hits Escape from elsewhere) we want
            // the mode-bail to work from anywhere.
            if (rawKey == Key.Escape && _freqOutHandlers.InRitXitScaleAdjustMode)
            {
                _freqOutHandlers.CancelRitXitScaleAdjust();
                e.Handled = true;
                return;
            }
            if (rawKey == Key.OemOpenBrackets || rawKey == Key.OemCloseBrackets)
            {
                _freqOutHandlers.HandleFilterHotkey(e);
                if (e.Handled) return;
            }
        }

        // 3. PTT keys — Ctrl+Space (hold), Shift+Space (lock toggle), Escape (unlock)
        //    Active when focus is in the Home fields OR the Home field groups
        //    (ScreenFields expanders), radio powered on. Requires modifier
        //    keys to prevent accidental transmit.
        //
        //    Audio Arc Keys Track (2026-08-11): the gate was FreqOut-only,
        //    which made Ctrl+Space silently dead while riding a value in the
        //    expanders — precisely where an operator adjusting Mic Level
        //    wants to key up (research-queue field report). The expander
        //    panel uses no Space chords, so nothing is shadowed. Escape
        //    keeps its layering: while transmitting it unkeys FIRST (Track
        //    A's PTT-safety rule); when not transmitting it falls through
        //    to the expander's collapse-group behavior as before. Logging
        //    text fields remain outside the gate on purpose.
        if (_pttController != null && _radioPowerOn &&
            (FreqOut.IsKeyboardFocusWithin || FieldsPanel.IsKeyboardFocusWithin))
        {
            if (rawKey == Key.Space && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                _pttController.ToggleLock();
                e.Handled = true;
                return;
            }

            if (rawKey == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat && !_pttKeyDown) // ignore key-repeat and redundant down events
                {
                    _pttKeyDown = true;
                    if (_pttHoldFilter.NoteDown(Environment.TickCount64) ==
                        Radios.PttHoldFilter.DownAction.ContinueHold)
                    {
                        // The synthetic re-down of a hold being absorbed
                        // (#216): the transmitter never dropped, so there is
                        // nothing to key — just stand down the pending
                        // deferred release.
                        _pttDeferTimer?.Stop();
                    }
                    else
                    {
                        _pttController.PttDown();
                    }
                }
                e.Handled = true;
                return;
            }

            if (rawKey == Key.Escape && _pttController.IsTransmitting)
            {
                _pttController.EscapeUnlock();
                e.Handled = true;
                return;
            }
        }

        // 4. Route through scope-aware KeyCommands registry
        if (DoCommandHandler != null)
        {
            var winFormsKey = WpfKeyConverter.ToWinFormsKeys(e);
            if (winFormsKey != System.Windows.Forms.Keys.None && DoCommandHandler(winFormsKey))
            {
                e.Handled = true;
                return;
            }
        }

        // 5. Fall through to focused control (default WPF behavior)
    }

    private static bool IsUniversalHomeKey(Key key) =>
        key == Key.M || key == Key.V || key == Key.R || key == Key.X
        || key == Key.Q || key == Key.OemPlus;

    /// <summary>
    /// PreviewKeyUp — handles Ctrl+Space release for PTT hold mode.
    /// Catches Space release regardless of Ctrl state (user may release Ctrl first).
    /// </summary>
    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;

        if (rawKey == Key.Space && _pttController != null && _pttController.State == PttSafetyController.PttState.PttHold)
        {
            _pttKeyDown = false;
            if (_pttHoldFilter.NoteUp(Environment.TickCount64) ==
                Radios.PttHoldFilter.UpAction.DeferRelease)
            {
                // #216: this release may be a screen reader's synthetic pair
                // rather than the operator letting go. Keep transmitting; if
                // no re-down claims it within the learned window, the timer
                // performs the real release. Costs a fraction of a second of
                // carrier tail on a synthesising reader; costs nothing under
                // one that delivers real holds, because the filter never
                // arms there.
                if (!_pttFilterArmTraced && _pttHoldFilter.SynthesisDetected)
                {
                    _pttFilterArmTraced = true;
                    Tracing.TraceLine("PTT: screen reader is synthesising key-release pairs for a"
                        + " held Ctrl+Space (release " + _pttHoldFilter.SyntheticReleaseCount
                        + " arrived too fast to be human). Absorbing releases so transmit stays"
                        + " keyed — see task #216. Windows repeat delay on this machine is "
                        + _pttHoldFilter.KeyRepeatDelayMs + " ms, so the first gap of a press is"
                        + " bridged with " + _pttHoldFilter.FirstGapDeferMs + " ms and gaps inside"
                        + " the repeat stream with " + _pttHoldFilter.RepeatGapDeferMs + " ms."
                        + " Two windows, because they measure two different things: sharing one"
                        + " is what chopped the start of every press before 2026-08-26.",
                        TraceLevel.Error);
                }
                StartPttDeferTimer(_pttHoldFilter.DeferMs);
            }
            else
            {
                _pttController.PttUp();
            }
            e.Handled = true;
        }
        else if (rawKey == Key.Space)
        {
            _pttKeyDown = false; // Clear flag even if PTT state changed (e.g., locked)
        }
    }

    /// <summary>
    /// Arm (or re-arm) the deferred-release timer for the PTT hold filter.
    /// When it fires and the filter still has a release pending — no synthetic
    /// re-down arrived — the release really happens.
    /// </summary>
    private void StartPttDeferTimer(int ms)
    {
        if (_pttDeferTimer == null)
        {
            _pttDeferTimer = new DispatcherTimer();
            _pttDeferTimer.Tick += (s, args) =>
            {
                _pttDeferTimer!.Stop();
                if (_pttHoldFilter.DeferralElapsed(Environment.TickCount64))
                {
                    _pttController?.PttUp();
                    return;
                }

                // Still pending means Windows says the key is physically down
                // even though the reader sent a release. Keep transmitting and
                // ask again shortly — the corroboration is bounded inside the
                // filter, so a probe that is wrong cannot hold the transmitter
                // open (#216).
                if (_pttHoldFilter.ReleasePending)
                {
                    if (!_pttProbeExtensionTraced)
                    {
                        _pttProbeExtensionTraced = true;
                        Tracing.TraceLine("PTT: a deferred release ran out while Windows still"
                            + " reported the space bar physically down, so the hold was extended"
                            + " rather than unkeyed. This is the operating system's key state"
                            + " disagreeing with the screen reader's event stream, which is"
                            + " exactly what #216 predicted — and it is the first evidence that"
                            + " the physical key state survives a synthesising reader. Further"
                            + " extensions counted, not logged.",
                            TraceLevel.Error);
                    }
                    StartPttDeferTimer(_pttHoldFilter.NextRecheckMs);
                }
            };
        }
        _pttDeferTimer.Stop();
        _pttDeferTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _pttDeferTimer.Start();
    }

    #endregion

    #region Radio Wiring — Phase 11.6

    /// <summary>
    /// The active radio control instance. Set by VB-side openTheRadio().
    /// </summary>
    public FlexBase? RigControl { get; set; }

    /// <summary>
    /// Current radio's open parameters. Set by VB-side, used for SWR text and frequency formatting.
    /// </summary>
    public FlexBase.OpenParms? OpenParms { get; set; }

    /// <summary>
    /// Callback to close the radio from VB-side (CloseTheRadio).
    /// Set by globals module since it involves VB module state.
    /// </summary>
    public Action? CloseRadioCallback { get; set; }

    /// <summary>
    /// Save a SmartLink account email as the default for Remote connections.
    /// Wired by globals.vb since it needs BaseConfigDir + operator name.
    /// </summary>
    public Action<string>? SaveDefaultSmartLinkAccount { get; set; }

    /// <summary>
    /// Get the current default SmartLink account email.
    /// Wired by globals.vb since it needs BaseConfigDir + operator name.
    /// </summary>
    public Func<string>? GetDefaultSmartLinkEmail { get; set; }

    /// <summary>
    /// Use a SmartLink account for THIS CONNECTION without changing the saved
    /// default (the "Use Now" button). Wired by globals.vb, which keeps the
    /// override and honors it in ShowAccountSelector and
    /// ResolveSmartLinkAccount ahead of the saved default.
    ///
    /// <para>Cleared by globals.CloseTheRadio, so disconnecting returns the
    /// borrowed account (#342). This line used to say the override "clears
    /// itself by existing only in memory — an app restart is back to the
    /// default", which was true and was the whole bug: nothing shorter than an
    /// app restart ended it, so an account borrowed for one radio judged every
    /// radio that followed.</para>
    /// </summary>
    public Action<string>? SetSessionSmartLinkAccount { get; set; }

    /// <summary>
    /// Update the shell form title bar with radio status.
    /// Wired by globals.vb to AppShellForm.Text.
    /// </summary>
    public Action<string>? UpdateTitleBar { get; set; }

    /// <summary>
    /// Callback for VB-side exit sequence. Returns true to proceed, false to cancel.
    /// Set by ApplicationEvents.vb, called from MainWindow_Closing.
    /// </summary>
    public Func<bool>? AppExitCallback { get; set; }

    /// <summary>
    /// Callback to select/connect to a radio. Used by menu "Connect to Radio" item.
    /// Set by ApplicationEvents.vb, routes to globals.SelectRadio().
    /// </summary>
    public Action? SelectRadioCallback { get; set; }

    /// <summary>
    /// Callback for VB-side power-on tasks (knob setup, tracing).
    /// </summary>
    public Action? PowerOnCallback { get; set; }

    /// <summary>
    /// Callback to show past Connection Test results.
    /// </summary>
    public Action? ShowTestResultsCallback { get; set; }

    /// <summary>
    /// Callback to open the CW message manager (#329).
    /// </summary>
    /// <remarks>
    /// A callout rather than a direct call because the messages belong to the
    /// operator record, which lives on the VB side; JJFlexWpf cannot reference
    /// back. The dialogs it opens are ours and take delegates, which is what
    /// keeps them free of any radio or operator type.
    /// </remarks>
    public Action? ManageCWMessagesCallback { get; set; }

    /// <summary>
    /// Callback to show a WinForms error dialog parented to ShellForm.
    /// Parameters: message, title. Falls back to unparented WPF MessageBox if not set.
    /// </summary>
    public Action<string, string>? ShowErrorCallback { get; set; }

    // QB Track H (2026-08-07): GetKeyActionsCallback / GetAvailableActionsCallback /
    // SaveKeyActionsCallback are gone with the legacy ShowKeysDialog/SetupKeysDialog
    // pair. SaveKeyActionsCallback was never wired — the legacy Update button
    // saved into a null callback, which is why Noel's live test couldn't
    // change a key. The Keys surface talks to KeyCommandsRef directly.

    /// <summary>
    /// Callback to build the list of command finder items for CommandFinderDialog.
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Func<List<Dialogs.CommandFinderItem>>? GetCommandFinderItemsCallback { get; set; }

    /// <summary>
    /// Callback to execute a command by its tag (CommandValues enum value).
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Action<object>? ExecuteCommandCallback { get; set; }

    /// <summary>Callback to speak the current radio status summary. Set by ApplicationEvents.vb.</summary>
    public Action? SpeakStatusCallback { get; set; }

    /// <summary>Callback to show the status dialog. Set by ApplicationEvents.vb.</summary>
    public Action? ShowStatusDialogCallback { get; set; }

    /// <summary>Callback to open the PortAudio device picker. Set by globals.vb.</summary>
    public Action? AudioSetupCallback { get; set; }

    /// <summary>
    /// Full path to audioDevices.xml. Set by ApplicationEvents.vb at startup,
    /// so Settings' Audio tab can open the device picker whether or not a radio
    /// is connected. globals owns the path; this is a handoff, not a second
    /// place that knows how to build it.
    /// </summary>
    public string? AudioDevicesFilePath { get; set; }

    /// <summary>
    /// Callback to open the operator management dialog (the VB Lister form over
    /// the PersonalData operators list — the same surface the app shows at
    /// first run when no operator exists). Set by ApplicationEvents.vb.
    /// QB Track A stub audit, 2026-08-07.
    /// </summary>
    public Action? ShowOperatorsCallback { get; set; }

    /// <summary>
    /// Antenna tune button base text, matching Form1 pattern.
    /// </summary>
    private const string AntennaTuneButtonBaseText = "Ant Tune";

    private string AntennaTuneButtonText
    {
        get
        {
            var text = AntennaTuneButtonBaseText;
            if (RigControl != null && RigControl.FlexTunerUsingMemoryNow)
                text += " mem";
            return text;
        }
    }

    /// <summary>
    /// Wire MainWindow event handlers to RigControl.
    /// Called by VB-side openTheRadio() BEFORE RigControl.Start().
    /// </summary>
    public void WireRadioEvents()
    {
        if (RigControl == null) return;

        // Sprint 42 Track D (#395): a connect is genuinely starting — this is
        // the one call every path makes before Start(), including auto-connect
        // and the retry ladder's fresh legs, which never pass through the menu
        // or the rescue button. Idempotent when a door already opened it.
        BeginConnectFlowQuiet("radio events wired");

        RigControl.PowerStatus += PowerStatusHandler;
        RigControl.NoSliceError += NoSliceErrorHandler;
        RigControl.FeatureLicenseChanged += FeatureLicenseChangedHandler;
    }

    /// <summary>
    /// Post-Start radio setup. Called by VB-side after RigControl.Start() succeeds.
    /// Replaces Form1's post-start wiring (setupBoxes, menus, poll timer).
    /// </summary>
    public void OnRadioStarted()
    {
        Tracing.TraceLine("MainWindow.OnRadioStarted", TraceLevel.Info);

        // A radio is genuinely here now, which retires the rescue page. Done at
        // Started rather than at power-on because everything below assumes the
        // full Home layout exists, and the two are only milliseconds apart.
        ExitRescueMode();

        SetupBoxes();

        // Wire memory dialog delegate
        if (RigControl != null)
        {
            RigControl.ShowMemoriesDialog = ShowMemoriesDialog;
            // QB Track L: RadioInfoDialog (General + Feature Availability tabs)
            // — never assigned since Sprint 11, leaving the menu door dead.
            RigControl.ShowRadioInfoDialog = ShowRadioInfoDialog;
            // Sprint 33 Track J (#109): TXControlsDialog — same bug again,
            // declared and invoked since Sprint 11, never assigned.
            RigControl.ShowTXControlsDialog = ShowTXControlsDialog;
        }

        // Disable controls initially — PowerNowOn enables them when radio powers on
        EnableDisableWindowControls(false);

        // Start polling
        PollTimerEnabled = true;

        StatusText.Text = Radios.Lexicon.Get("connect.home.status_waiting_power_on");

        RunStartupAdvisories();
    }

    /// <summary>
    /// The connect-time advisories, in virgin-radio order: SmartLink first
    /// (set it up / register), then firmware. Run one after the other so a
    /// brand-new radio never stacks two message boxes on top of each other.
    /// Each advisory swallows its own failures; none may disturb the connection.
    /// </summary>
    private async void RunStartupAdvisories()
    {
        _advisorySequenceActive = true;
        try
        {
            await SuggestRegistrationIfUnregisteredAsync();
            await SuggestFirmwareUpdateIfAvailableAsync();
        }
        finally
        {
            _advisorySequenceActive = false;

            // Bring-up speech that arrived while the chain was active gets its
            // turn now that the user is done reading — focus first (the parked
            // welcome focus grab), then each parked announcement in arrival
            // order. One policy for every bring-up path, not per-path patches.
            if (_welcomeFocusPending)
            {
                _welcomeFocusPending = false;
                try { FocusHome(); } catch { /* window may be closing */ }
            }
            foreach (var (message, level) in _deferredStartupSpeech)
            {
                if (level.HasValue)
                    Radios.ScreenReaderOutput.Speak(message, level.Value);
                else
                    Radios.ScreenReaderOutput.Speak(message);
            }
            _deferredStartupSpeech.Clear();
        }
    }

    /// <summary>
    /// True while the startup-advisory chain is running. Every main-window
    /// bring-up speech path checks it — the welcome line and the connect-time
    /// slice rundown both queue into <see cref="_deferredStartupSpeech"/>
    /// instead of talking over an open advisory (ordering policy, 2026-08-07;
    /// the connect rundown got this treatment first and the welcome line was
    /// a separate un-parked path). All on the dispatcher thread, no locking.
    /// </summary>
    private bool _advisorySequenceActive;

    /// <summary>
    /// Parked bring-up announcements, in arrival order. Null level means
    /// speak at the default verbosity.
    /// </summary>
    private readonly List<(string message, VerbosityLevel? level)> _deferredStartupSpeech = new();

    /// <summary>
    /// SpeakWelcome's FreqOut focus grab was deferred because an advisory was
    /// open — replay it when the chain ends, BEFORE the parked speech, so the
    /// caret lands where a cold start would put it.
    /// </summary>
    private bool _welcomeFocusPending;

    /// <summary>
    /// Serials already offered the registration suggestion this app run, so a
    /// user who declines is not re-asked on every reconnect.
    /// </summary>
    private static readonly HashSet<string> _registrationSuggestedSerials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SmartLink-setup suggestion already made this app run. Not per-serial:
    /// having no SmartLink account is a property of this computer, not of
    /// whichever radio happens to be connected.
    /// </summary>
    private static bool _smartLinkSetupSuggested;

    /// <summary>
    /// After a local connect, quietly find out where this radio stands with
    /// SmartLink and speak up only when the user has something to gain:
    /// NoAccount means SmartLink has never been set up on this computer (the
    /// virgin-radio case — suggest starting it), NotRegistered means the
    /// account exists but this radio is not in it (suggest registering).
    /// Unknown means stay silent — an unreachable server is not evidence the
    /// radio needs anything. The answer comes from the SmartLink server and
    /// can take seconds; a connect never waits on it.
    /// </summary>
    private async Task SuggestRegistrationIfUnregisteredAsync()
    {
        var rig = RigControl;
        if (rig == null) return;

        // Sprint 30 Track A — an operator who has already said "I only use
        // this radio here" is asked nothing further about SmartLink for it,
        // on any connect, on any run. Not registering is a real answer, and
        // an app that keeps raising it is treating a decision as an
        // unfinished task. Checked before the server query so a local-only
        // radio does not even generate the round trip.
        if (SmartLinkIntentFor(rig) == Radios.SmartLinkIntents.LocalOnly)
        {
            Tracing.TraceLine(
                "SuggestRegistration: radio is marked local-only — nothing to suggest",
                TraceLevel.Info);
            return;
        }

        try
        {
            var result = await rig.QuerySmartLinkRegistrationAsync();

            if (result == FlexBase.SmartLinkRegistrationQuery.NoAccount)
            {
                if (_smartLinkSetupSuggested) return;
                _smartLinkSetupSuggested = true;
                if (!rig.IsConnected) return;

                Tracing.TraceLine("SuggestRegistration: no SmartLink account on this computer", TraceLevel.Info);

                await Dispatcher.BeginInvoke(() =>
                {
                    string msg = Radios.Lexicon.Get("connect.smartlink.setup_body");
                    Dialogs.AdvisoryDialog.Show(
                        Radios.Lexicon.Get("connect.smartlink.setup_title"), msg,
                        suppressKey: Radios.AdvisoryKeys.SmartLinkSetup,
                        new Dialogs.AdvisoryDialog.AdvisoryAction(
                            Radios.Lexicon.Get("connect.smartlink.action_open_radio_setup"), () => OpenSettingsCallback?.Invoke("Radio Setup")));
                });
                return;
            }

            if (result != FlexBase.SmartLinkRegistrationQuery.NotRegistered) return;

            string serial = rig.SelectedRadioSerial ?? string.Empty;
            if (serial.Length == 0) return;

            string account = rig.CurrentSmartLinkAccountEmail;

            // Sprint 38 Track D (#342) — DO NOT ANSWER A QUESTION NOBODY ASKED.
            //
            // The server was asked about whichever account happened to be in
            // play. On a LOCAL connect that account can be one the operator
            // borrowed for a DIFFERENT radio: connect to Don's 6300 over
            // SmartLink, disconnect, connect to your own 8600 across the room,
            // and the honest answer "the 8600 is not registered to Don" comes
            // back and gets rendered as advice about your own radio.
            //
            // globals.CloseTheRadio now returns the borrowed account at
            // disconnect, which fixes the reported sequence at its source. This
            // is the second guard and it is the cheap one: whatever route left
            // a non-default account in play — a "Use Now" that is still
            // standing, a picker switch, a future writer nobody has thought of
            // yet — a local connect judged against an account the operator did
            // not choose as theirs is not evidence about this radio, so it says
            // nothing at all.
            //
            // Deliberately narrow. It only fires when a default account exists
            // AND the account asked is a different one; a single-account
            // install, or one that has never nominated a default, resolves
            // exactly as before. And it runs BEFORE the once-per-run serial
            // guard below, so a suppressed question does not spend the one
            // chance the real question gets later.
            //
            // Remote connects are exempt by construction: over SmartLink the
            // query returns Registered, because arriving that way IS proof.
            bool localConnect = !rig.RemoteRig;
            string defaultAccount = GetDefaultSmartLinkEmail?.Invoke() ?? string.Empty;
            if (localConnect
                && account.Length > 0
                && defaultAccount.Length > 0
                && !account.Equals(defaultAccount, StringComparison.OrdinalIgnoreCase))
            {
                Tracing.TraceLine(
                    $"SuggestRegistration: local connect to {serial} was judged against '{account}', "
                    + $"which is not this operator's account '{defaultAccount}' — saying nothing, "
                    + "the answer is about the wrong account (#342)",
                    TraceLevel.Info);
                return;
            }

            if (!_registrationSuggestedSerials.Add(serial)) return;
            if (!rig.IsConnected) return;

            Tracing.TraceLine($"SuggestRegistration: {serial} not registered to {account}", TraceLevel.Info);

            // Registered-elsewhere awareness (live incident 2026-08-05: Noel was
            // signed in as Don, and the advisory insisted a radio he KNEW was
            // registered wasn't — true for that account, misleading as stated).
            // The server was only asked about the signed-in account, so when
            // other saved accounts exist, say so and offer the switch instead of
            // presenting registration as the one explanation.
            int otherAccounts = 0;
            try
            {
                otherAccounts = Radios.FlexBase.SharedAccountManager.Accounts
                    .Count(a => !a.Email.Equals(account, StringComparison.OrdinalIgnoreCase));
            }
            catch { /* count stays 0; the simple advisory is still correct */ }

            // Sprint 30 Track A — a LOCAL connect with no answer on record gets
            // an OFFER, not a complaint. The old wording opened by telling an
            // operator sitting three feet from their radio that it was "not
            // registered", which frames a valid arrangement as a fault. The
            // offer states the same fact, says plainly that not registering is
            // a fine answer, and gives that answer a button so it can be
            // recorded once and never raised again.
            //
            // A REMOTE connect keeps the original advisory: the operator is
            // already using SmartLink, so registration is not hypothetical and
            // "I only use this radio here" is not on the table.
            // (localConnect is resolved above, where the borrowed-account guard
            // needs it.)
            bool undecided = SmartLinkIntentFor(rig) == Radios.SmartLinkIntents.Undecided;

            await Dispatcher.BeginInvoke(() =>
            {
                if (localConnect && undecided)
                {
                    ShowLocalOnlyOffer(serial, account, otherAccounts);
                    return;
                }

                string msg = Radios.Lexicon.Get("connect.smartlink.not_registered_body",
                    ("account", account),
                    ("otherAccountsNote", otherAccounts > 0
                        ? Radios.Lexicon.Get("connect.smartlink.other_accounts_note")
                        : ""));

                var actions = new List<Dialogs.AdvisoryDialog.AdvisoryAction>
                {
                    new(Radios.Lexicon.Get("connect.smartlink.action_open_radio_setup"), () => OpenSettingsCallback?.Invoke("Radio Setup")),
                };
                if (otherAccounts > 0)
                    actions.Add(new(Radios.Lexicon.Get("connect.smartlink.action_manage_accounts"), ShowSmartLinkAccountManager));

                Dialogs.AdvisoryDialog.Show(
                    Radios.Lexicon.Get("connect.smartlink.not_registered_title"), msg,
                    suppressKey: Radios.AdvisoryKeys.RegisterRadio(serial),
                    actions.ToArray());
            });
        }
        catch (Exception ex)
        {
            // A failed suggestion must never disturb a working connection.
            Tracing.TraceLine($"SuggestRegistration: {ex.Message}", TraceLevel.Error);
        }
    }

    /// <summary>
    /// This radio's recorded SmartLink intent, or Undecided when there is no
    /// serial to key it by. Reads the per-radio config each time rather than
    /// caching: the operator can change it in Settings while connected, and a
    /// cached copy would keep asking about a radio they just settled.
    /// </summary>
    private static Radios.SmartLinkIntents SmartLinkIntentFor(FlexBase rig)
    {
        try
        {
            string serial = rig.SelectedRadioSerial ?? string.Empty;
            if (serial.Length == 0) return Radios.SmartLinkIntents.Undecided;
            return Radios.RadioConfig.LoadForRadio(serial).SmartLinkIntent;
        }
        catch (Exception ex)
        {
            // An unreadable config must not silence an advisory that might
            // genuinely help; Undecided is the state that still offers.
            Tracing.TraceLine(
                $"SmartLinkIntentFor: {ex.Message} — treating as undecided",
                TraceLevel.Warning);
            return Radios.SmartLinkIntents.Undecided;
        }
    }

    /// <summary>
    /// The local-only offer: this radio is not registered with SmartLink, here
    /// is what registering would buy, and here is the button that says you do
    /// not want it — once, permanently, per radio.
    /// </summary>
    private void ShowLocalOnlyOffer(string serial, string account, int otherAccounts)
    {
        // Wording note, and it is a constraint rather than a preference:
        // nothing here may imply that registration says whose radio this is.
        // Track B established the counter-example on 2026-08-18 - an operator
        // connected to somebody else's radio using that person's account, and
        // a registration test would have called him the owner. Registration
        // answers who has ACCESS. So this asks about the operator's USE ("do
        // you operate it from away") and never about the radio's ownership,
        // and what it stores is a local prompt preference on this machine,
        // not a claim about the radio.
        string msg = Radios.Lexicon.Get("connect.smartlink.reach_from_away_body",
            ("account", account),
            ("otherAccountsNote", otherAccounts > 0
                ? Radios.Lexicon.Get("connect.smartlink.other_accounts_note_switch")
                : ""));

        var actions = new List<Dialogs.AdvisoryDialog.AdvisoryAction>
        {
            new(Radios.Lexicon.Get("connect.smartlink.action_local_only"), () => RecordSmartLinkIntent(
                serial, Radios.SmartLinkIntents.LocalOnly)),
            new(Radios.Lexicon.Get("connect.smartlink.action_open_radio_setup"), () =>
            {
                // Opening setup IS the answer to the question, so record it.
                // Without this the offer would return on the next run for an
                // operator who has plainly said they want SmartLink — and the
                // registration reminder, which is help once the intent is
                // known, would never take over from the offer.
                RecordSmartLinkIntent(serial, Radios.SmartLinkIntents.WantsSmartLink, quiet: true);
                OpenSettingsCallback?.Invoke("Radio Setup");
            }),
        };
        if (otherAccounts > 0)
            actions.Add(new(Radios.Lexicon.Get("connect.smartlink.action_manage_accounts"), ShowSmartLinkAccountManager));

        // No suppressKey: the "don't show this again" checkbox would be a
        // third, vaguer way to answer the same question the buttons answer
        // precisely, and its answer would be stored somewhere else entirely.
        // Close is the "not now" path and costs one more sighting.
        Dialogs.AdvisoryDialog.Show(Radios.Lexicon.Get("connect.smartlink.reach_from_away_title"), msg, null, actions.ToArray());
    }

    /// <summary>
    /// Write the operator's SmartLink intent for one radio and, unless quiet,
    /// hand them a receipt.
    ///
    /// <para>The receipt is its own window on purpose. The choice is made
    /// inside a dialog that is closing, and a screen reader flushes its queue
    /// as that window goes away — so an utterance spoken here would be
    /// destroyed before it was heard. A window that ARRIVES carries its own
    /// title, which is why the outcome is IN the title.</para>
    ///
    /// <para>It also has to be able to say the write failed. RadioConfig's own
    /// contract is that a false return means the value did not reach disk but
    /// the operator's choice still stands for the session — telling them
    /// otherwise, or telling them nothing, is how a setting silently
    /// evaporates overnight.</para>
    /// </summary>
    private void RecordSmartLinkIntent(string serial, Radios.SmartLinkIntents intent, bool quiet = false)
    {
        bool saved = false;
        try
        {
            var cfg = Radios.RadioConfig.LoadForRadio(serial);
            cfg.SmartLinkIntent = intent;
            saved = cfg.SaveForRadio(serial);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"RecordSmartLinkIntent({serial}): {ex.Message}", TraceLevel.Error);
        }

        Tracing.TraceLine(
            $"RecordSmartLinkIntent: {serial} -> {intent}, saved={saved}", TraceLevel.Info);

        if (quiet) return;

        string where = Radios.Lexicon.Get("connect.smartlink.where_to_change");
        if (intent == Radios.SmartLinkIntents.LocalOnly)
        {
            Dialogs.AdvisoryDialog.Show(
                saved ? Radios.Lexicon.Get("connect.smartlink.local_only_saved_title") : Radios.Lexicon.Get("connect.smartlink.local_only_unsaved_title"),
                Radios.Lexicon.Get(saved
                    ? "connect.smartlink.local_only_saved_body"
                    : "connect.smartlink.local_only_unsaved_body", ("where", where)));
        }
    }

    /// <summary>
    /// Unwire event handlers when closing the radio.
    /// Called by VB-side CloseTheRadio().
    /// </summary>
    public void UnwireRadioEvents()
    {
        PollTimerEnabled = false;
        _radioPowerOn = false;

        // Sprint 42 Track D (#395): a teardown ends any quiet scope a failed
        // connect left open. On an ordinary disconnect there is no scope and
        // this no-ops; on the retry ladder's mid-flow unwire, the very next
        // WireRadioEvents re-raises the scope and its generation counter
        // makes this end request a no-op too.
        EndConnectFlowQuiet("radio events unwired");

        if (RigControl != null)
        {
            RigControl.PowerStatus -= PowerStatusHandler;
            RigControl.NoSliceError -= NoSliceErrorHandler;
            RigControl.FeatureLicenseChanged -= FeatureLicenseChangedHandler;
            RigControl.TransmitChange -= TransmitChangeHandler;
            RigControl.FlexAntTunerStartStop -= FlexAntTuneStartStopHandler;
            RigControl.ConnectedEvent -= ConnectedEventHandler;
        }

        FreqOut.Clear();
        _comboControls.Clear();
        _enableDisableControls.Clear();

        // Forget the licence-gating verdict with the radio it described. The
        // next radio may be a different model on a different subscription, and
        // a stale signature would suppress the rebuild that tells the menus so.
        _featureGateSignature = null;

        StatusText.Text = Radios.Lexicon.Get("connect.home.status_ready_no_radio");

        // Restore the cold-start no-radio visual shell. Without this the
        // connect-time ShowClassicUI / ShowModernUI calls leave
        // FieldsPanel / MetersPanel / PanadapterPanel sitting Visible after
        // disconnect, so users see (and tab through, and focus into) Home
        // controls that have no underlying radio data. Cold-start XAML
        // defaults are all Collapsed; we mirror that here.
        RestoreNoRadioShell();
    }

    /// <summary>
    /// Reset MainWindow's visual state to match cold-start (no radio yet).
    /// Mirrors the XAML default Visibility for each panel:
    /// RadioControlsPanel Visible (so FreqOut remains the focus anchor),
    /// FieldsPanel / MetersPanel / PanadapterPanel Collapsed (so they
    /// drop out of layout AND tab order), focus returned to FreqOut so
    /// the next Tab press lands somewhere meaningful.
    /// Called from UnwireRadioEvents on disconnect.
    /// </summary>
    private void RestoreNoRadioShell()
    {
        try
        {
            // #500, and this is the SECOND collapse route — the one the
            // register did not name. ApplyRescueVisibility collapses six panels
            // and only runs in rescue mode; this collapses three of the same
            // six on every disconnect, rescue mode or not, and is therefore the
            // route that most often takes the fields panel away. It traced
            // nothing at all until 2026-09-02.
            Tracing.TraceLine(
                "RestoreNoRadioShell: collapsing Fields, Meters and Panadapter on disconnect"
                + " | before: " + DescribeHomePanelVisibility(),
                TraceLevel.Info);

            RadioControlsPanel.Visibility = Visibility.Visible;
            FieldsPanel.Visibility = Visibility.Collapsed;
            MetersPanel.Visibility = Visibility.Collapsed;
            PanadapterPanel.Visibility = Visibility.Collapsed;
            // FreqOut is the cold-start focus anchor (SpeakWelcome focuses it
            // after MainWindow loads). Returning focus here means the user
            // doesn't lose a meaningful focus location during the visibility
            // changes — without this, focus that was inside FieldsPanel when
            // it Collapses gets routed by WPF to whatever the next visible
            // focusable element happens to be, which is screen-reader hostile.
            //
            // Through FocusHome so this stays correct if the mid-session
            // radio-lost case ever adopts the rescue page (Sprint 30 Track A
            // deliberately scoped that OUT; today this is always FreqOut).
            FocusHome();

            // Sprint 31 Track R — it adopted it. This shell is the interim
            // state now, not the destination: three minutes from here, with no
            // radio back, Home becomes the rescue page and the two routes to
            // "no radio" stop describing it differently. A reconnect inside the
            // grace period cancels the countdown through ExitRescueMode, so a
            // momentary drop never costs the operator their context.
            BeginRescueCountdown();
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine($"RestoreNoRadioShell: exception {ex.Message}", TraceLevel.Error);
        }
    }

    /// <summary>
    /// Set up combo controls and enable/disable lists after radio Start().
    /// Replaces Form1.setupBoxes().
    /// </summary>
    private void SetupBoxes()
    {
        Tracing.TraceLine("SetupBoxes", TraceLevel.Info);
        if (RigControl == null)
        {
            Tracing.TraceLine("SetupBoxes: no rig", TraceLevel.Error);
            return;
        }

        FreqOut.Clear();

        _comboControls.Clear();
        _enableDisableControls.Clear();

        // Mode control
        ModeControl.IsEnabled = true;
        ModeControl.ClearCache();
        ModeControl.TheList = null;
        var modeList = new ArrayList();
        foreach (string val in RigCaps.ModeTable)
        {
            modeList.Add(val);
        }
        ModeControl.TheList = modeList;
        ModeControl.UpdateDisplayFunction = () => RigControl.Mode;
        ModeControl.UpdateRigFunction = (v) =>
        {
            if (!_radioPowerOn)
            {
                Tracing.TraceLine("mode: no power", TraceLevel.Error);
                return;
            }
            RigControl.Mode = v?.ToString();
        };
        _comboControls.Add(ModeControl);
        _enableDisableControls.Add(ModeControl);

        // SentTextBox in enable/disable collection
        _enableDisableControls.Add(SentTextBox);
    }

    /// <summary>
    /// FreqOut field handler instance — manages all interactive tuning.
    /// </summary>
    private FreqOutHandlers? _freqOutHandlers;

    /// <summary>Braille display status line engine.</summary>
    private readonly BrailleStatusEngine _brailleEngine = new();

    /// <summary>
    /// Sprint 28 Phase 3.10 — diagnostic toggle (Ctrl+Shift+B) for the braille
    /// status line. When off, BrailleStatusEngine stops pushing status to the
    /// braille display, letting the screen reader's default (show focused
    /// control content = FreqOut) take over. Used to test whether cursor routing
    /// on the braille display reaches our FreqOut TextBox's SelectionChanged
    /// event. Pending verification that routing works, this may evolve into the
    /// permanent three-mode braille display model (idle/navigating/tuning).
    /// </summary>
    public void ToggleBrailleStatus()
    {
        _brailleEngine.Enabled = !_brailleEngine.Enabled;
        _brailleEngine.UpdateTimerState();
        // Sprint 32 Track E, #128.
        EarconPlayer.ToggleTone(_brailleEngine.Enabled);
        Radios.ScreenReaderOutput.Speak(
            _brailleEngine.Enabled ? Radios.Lexicon.Get("connect.home.braille_on") : Radios.Lexicon.Get("connect.home.braille_off"),
            Radios.VerbosityLevel.Terse,
            interrupt: true);
    }

    /// <summary>CW Morse code notification engine for connection/status events.</summary>
    // Static so the field initializer below can reference it: C# forbids an
    // instance field initializer touching another instance field.
    private static readonly EarconCwOutput _cwOutput = new();
    private readonly MorseNotifier _morseNotifier = new(_cwOutput);

    /// <summary>
    /// Let any in-flight CW finish before teardown, up to a bounded wait.
    /// Called from the VB exit sequence, which owns the shutdown order.
    /// </summary>
    public bool WaitForCwIdle(int maxWaitMs) => _cwOutput.WaitForIdle(maxWaitMs);

    /// <summary>
    /// The last CW sidetone pitch a radio reported, or null when none has.
    /// Static so the Settings preview can be honest about the disconnected
    /// case without needing a MainWindow instance: previewing "follow the
    /// radio" with no radio has to sound exactly like the real thing does,
    /// which is the configured tone and no complaint about it.
    /// </summary>
    private static int? _lastRadioCwPitchHz;

    /// <summary>
    /// Play a short CW sample with the settings currently showing in the
    /// Settings dialog, without disturbing the live notifier (#145, #146).
    ///
    /// A throwaway notifier over the SHARED output, so the sample queues behind
    /// anything real that is already keying rather than cutting it off. An
    /// operator auditioning tone shapes mid-connect should not be able to
    /// silence the connect prosigns by doing so.
    ///
    /// "V" is the sample because it is the character every CW operator has
    /// heard ten thousand times as a test — di-di-di-dah — so nothing about the
    /// pattern distracts from the timbre, which is the thing being judged.
    /// </summary>
    public static void PreviewCwTone(string? waveformId, bool followRadioPitch,
        int configuredHz, int speedWpm)
    {
        try
        {
            var preview = new MorseNotifier(_cwOutput)
            {
                SidetoneHz = Math.Clamp(configuredHz, 400, 1200),
                SpeedWpm = Math.Clamp(speedWpm, 10, 60),
                FollowRadioSidetone = followRadioPitch,
                RadioSidetoneHz = _lastRadioCwPitchHz,
                MarkVoice = EarconVoices.ResolveCwWaveform(waveformId).Voice,
            };
            _ = preview.PlayString("V");
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"PreviewCwTone failed: {ex.Message}", TraceLevel.Warning);
        }
    }

    /// <summary>
    /// Expose FreqOutHandlers for Settings dialog tuning step access.
    /// </summary>
    public FreqOutHandlers? FreqHandlers => _freqOutHandlers;

    /// <summary>
    /// Set up the frequency display fields with interactive handlers.
    /// Dispatches to Classic or Modern field set based on ActiveUIMode.
    /// </summary>
    private void SetupFreqout()
    {
        if (RigControl == null) return;

        if (ActiveUIMode == UIMode.Modern)
            SetupFreqoutModern();
        else
            SetupFreqoutClassic();
    }

    /// <summary>
    /// Classic mode: full field set — Slice, Mute, Volume, SMeter, Split, VOX, Freq, Offset, RIT, XIT.
    /// Position-based tuning via cursor placement within each field.
    /// </summary>
    private void SetupFreqoutClassic()
    {
        if (RigControl == null) return;
        Tracing.TraceLine("SetupFreqoutClassic", TraceLevel.Info);

        EnsureFreqOutHandlers();

        var fields = new List<FrequencyDisplay.DisplayField>();

        // Field order (Sprint 28 bug bundle 2026-04-28 — parity with Modern):
        //   Slice → SliceOps → Freq → SMeter → Squelch → SquelchLevel
        //   → Split → VOX → Offset → RIT → XIT → Mute → Volume
        //
        // Modern (post-Sprint-26-Phase-8) is the canonical sequence; Classic
        // mirrors it for the shared fields so muscle memory transfers between
        // modes. Mute and Volume are Classic-only (Modern handles them via the
        // M universal key and the Audio expander) and now sit at the end of
        // the order so they don't interrupt the high-traffic Freq → SMeter
        // path. Customize Home (Sprint 30+) will let users override.
        // HelpItems come from the KeyInventory table — the single source that
        // also drives the '?' handler, the Keys surface, and the key manifest.
        // QB Track H (2026-08-07): the previous inline lists had drifted from
        // the real handlers (e.g. the removed 'C' coarse/fine toggle).
        fields.Add(new FrequencyDisplay.DisplayField("Slice", 1, "", "") { Label = "Slice",
            HelpItems = KeyInventory.HelpItemsFor("Slice", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("SliceOps", 3, "", "") { Label = "Slice operations",
            HelpItems = KeyInventory.HelpItemsFor("SliceOps", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("Freq", 12, "", "") { Label = "Frequency", DefaultCursorOffset = 8,
            HelpItems = KeyInventory.HelpItemsFor("Freq", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("SMeter", 4, "", "") { Label = "S Meter",
            HelpItems = KeyInventory.HelpItemsFor("SMeter", modern: false) });
        // Sprint 28 Phase 3.9 — Squelch + Squelch Level fields. Squelch state
        // always visible (toggle with Space or Q); level field always present
        // and adjustable (pre-loads threshold when squelch is off, takes effect
        // when squelch is on). Positioned after S Meter since they're the
        // "signal threshold" response to what S Meter shows.
        fields.Add(new FrequencyDisplay.DisplayField("Squelch", 1, "", "") { Label = "Squelch",
            HelpItems = KeyInventory.HelpItemsFor("Squelch", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("SquelchLevel", 3, "", "") { Label = "Squelch Level",
            HelpItems = KeyInventory.HelpItemsFor("SquelchLevel", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("Split", 1, "", "") { Label = "Split",
            HelpItems = KeyInventory.HelpItemsFor("Split", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("VOX", 1, "", "") { Label = "VOX",
            HelpItems = KeyInventory.HelpItemsFor("VOX", modern: false) });
        // QB Track I — Transmit slice field: shows which slice keys the radio
        // ("-" when none does). Sits by VOX/RIT/XIT where an operator looks
        // for transmit state. The discoverable door for what was previously
        // only the hidden T keypress on the Slice field. HelpItems come from
        // the KeyInventory table like every other field (QB Track L).
        fields.Add(new FrequencyDisplay.DisplayField("TXSlice", 1, "", "") { Label = "Transmit slice",
            HelpItems = KeyInventory.HelpItemsFor("TXSlice", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("Offset", 1, "", "") { Label = "Offset",
            HelpItems = KeyInventory.HelpItemsFor("Offset", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("RIT", 5, "", "") { Label = "RIT", DefaultCursorOffset = 2,
            HelpItems = KeyInventory.HelpItemsFor("RIT", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("XIT", 5, " ", "") { Label = "XIT", DefaultCursorOffset = 2,
            HelpItems = KeyInventory.HelpItemsFor("XIT", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("Mute", 1, "", "") { Label = "Mute",
            HelpItems = KeyInventory.HelpItemsFor("Mute", modern: false) });
        fields.Add(new FrequencyDisplay.DisplayField("Volume", 3, "", "") { Label = "Volume",
            HelpItems = KeyInventory.HelpItemsFor("Volume", modern: false) });

        // Classic mode uses position-based step names (no override)
        FreqOut.StepNameOverride = null;
        FreqOut.IsModernMode = false;
        FreqOut.Populate(fields.ToArray());
        _firstFreqDisplay = true;
    }

    /// <summary>
    /// Modern tuning mode: full field set minus Mute and Volume. Sprint 26
    /// Phase 8 added the checkbox-field mirroring (Split, VOX, Offset, RIT,
    /// XIT) so operators can arrow-right to toggle them without leaving Modern
    /// tuning. Mute is universal-key territory ('M' from any field). Volume
    /// goes through the Slice menu / Audio expander.
    ///
    /// Field order: Slice → SliceOps → Freq → SMeter → Squelch → SquelchLevel
    ///   → Split → VOX → Offset → RIT → XIT
    ///
    /// Classic mirrors this sequence for the shared fields (Sprint 28 bug
    /// bundle 2026-04-28) and appends Mute → Volume at the end. Until
    /// Customize Home (Sprint 30+) ships, this is the canonical order — keep
    /// the two setup methods in sync when adding/removing fields.
    ///
    /// Tuning: simplified Freq handler with coarse/fine via Up/Down +
    /// Shift+Up/Down.
    /// </summary>
    private void SetupFreqoutModern()
    {
        if (RigControl == null) return;
        Tracing.TraceLine("SetupFreqoutModern", TraceLevel.Info);

        EnsureFreqOutHandlers();

        var fields = new List<FrequencyDisplay.DisplayField>();

        // Sprint 26 Phase 8: modern mode now mirrors classic's checkbox fields
        // (Split, VOX, Offset, RIT, XIT) to the right of SMeter so operators
        // can arrow-right to toggle them without leaving modern tuning mode.
        // Don's workflow: modern for frequency tuning, arrow-right to RIT, use
        // classic digit-position editing inside the RIT field to "tune in
        // teensies." Modern's Freq-field handler owns its own tuning model;
        // these fields reuse the classic-mode handlers unchanged.
        //
        // Field order: Slice → SliceOps → Freq → SMeter → Split → VOX → Offset → RIT → XIT
        // HelpItems come from the KeyInventory table (see Classic setup note).
        fields.Add(new FrequencyDisplay.DisplayField("Slice", 1, "", "") { Label = "Slice",
            HelpItems = KeyInventory.HelpItemsFor("Slice", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("SliceOps", 3, "", "") { Label = "Slice operations",
            HelpItems = KeyInventory.HelpItemsFor("SliceOps", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("Freq", 12, "", "") { Label = "Frequency", DefaultCursorOffset = 8,
            HelpItems = KeyInventory.HelpItemsFor("Freq", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("SMeter", 4, " ", "") { Label = "S Meter",
            HelpItems = KeyInventory.HelpItemsFor("SMeter", modern: true) });
        // Sprint 28 Phase 3.9 — Squelch + Squelch Level fields (see Classic setup for rationale)
        fields.Add(new FrequencyDisplay.DisplayField("Squelch", 1, "", "") { Label = "Squelch",
            HelpItems = KeyInventory.HelpItemsFor("Squelch", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("SquelchLevel", 3, "", "") { Label = "Squelch Level",
            HelpItems = KeyInventory.HelpItemsFor("SquelchLevel", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("Split", 1, "", "") { Label = "Split",
            HelpItems = KeyInventory.HelpItemsFor("Split", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("VOX", 1, "", "") { Label = "VOX",
            HelpItems = KeyInventory.HelpItemsFor("VOX", modern: true) });
        // QB Track I — Transmit slice field (see Classic setup for rationale).
        // HelpItems from the KeyInventory table (QB Track L).
        fields.Add(new FrequencyDisplay.DisplayField("TXSlice", 1, "", "") { Label = "Transmit slice",
            HelpItems = KeyInventory.HelpItemsFor("TXSlice", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("Offset", 1, "", "") { Label = "Offset",
            HelpItems = KeyInventory.HelpItemsFor("Offset", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("RIT", 5, "", "") { Label = "RIT", DefaultCursorOffset = 2,
            HelpItems = KeyInventory.HelpItemsFor("RIT", modern: true) });
        fields.Add(new FrequencyDisplay.DisplayField("XIT", 5, " ", "") { Label = "XIT", DefaultCursorOffset = 2,
            HelpItems = KeyInventory.HelpItemsFor("XIT", modern: true) });

        // Modern mode: Freq field uses modifier keys, not cursor position
        FreqOut.IsModernMode = true;
        // Modern mode: override step name to report both coarse and fine steps
        // (Sprint 29 Track F — there's no "current mode" any more, so the
        // help text describes the unified Up/Down + Shift+Up/Down setup).
        FreqOut.StepNameOverride = (field) =>
        {
            if (field.Key == "Freq" && _freqOutHandlers != null)
            {
                return "coarse " + FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.CoarseTuneStep)
                    + ", fine " + FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.FineTuneStep);
            }
            return null;
        };
        FreqOut.Populate(fields.ToArray());
        _firstFreqDisplay = true;
    }

    /// <summary>
    /// Create FreqOutHandlers if not already initialized.
    /// </summary>
    private void EnsureFreqOutHandlers()
    {
        if (_freqOutHandlers == null)
        {
            _freqOutHandlers = new FreqOutHandlers(this);
            // Wire VB.NET globals delegates
            FreqOutHandlersWireCallback?.Invoke(_freqOutHandlers);
            // Wire FieldKeyDown event to route keys to the handler for the field under cursor
            FreqOut.FieldKeyDown += FreqOut_FieldKeyDown;
            // Typing sound and keyboard sounds applied later in PowerOn
            // after CurrentAudioConfig is loaded from disk.
        }
    }

    /// <summary>
    /// Route FieldKeyDown events to the appropriate FreqOutHandler method
    /// based on the field key under the cursor.
    /// Modern mode uses simplified Freq handler with coarse/fine tuning.
    /// </summary>
    private void FreqOut_FieldKeyDown(FrequencyDisplay.DisplayField field, System.Windows.Input.KeyEventArgs e)
    {
        if (_freqOutHandlers == null) return;

        // '?' on any Home field speaks the keys that work right here —
        // field-specific keys first, then the universal Home keys. Generated
        // from the same KeyInventory table as the help dialog, the Keys
        // surface, and the manifest, so speech and docs can't drift.
        // QB Track H (2026-08-07). Accepts the /? key with or without Shift.
        var qKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (qKey == Key.OemQuestion &&
            (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            Radios.ScreenReaderOutput.Speak(
                KeyInventory.SpeakTextFor(field.Key, field.Label ?? field.Key,
                    ActiveUIMode == UIMode.Modern),
                VerbosityLevel.Terse, true);
            e.Handled = true;
            return;
        }

        // Focus-bound exit for RIT/XIT scale-adjust mode: any keypress on a
        // non-RIT/XIT field counts as leaving, so we cancel before routing.
        // The mode's own handler treats RIT↔XIT navigation as a leave too,
        // so this catches everything else.
        if (_freqOutHandlers.InRitXitScaleAdjustMode
            && field.Key != "RIT" && field.Key != "XIT")
        {
            _freqOutHandlers.CancelRitXitScaleAdjust();
        }

        // Modern mode: simplified routing for reduced field set
        if (ActiveUIMode == UIMode.Modern)
        {
            switch (field.Key)
            {
                case "Freq":
                    _freqOutHandlers.AdjustFreqModern(field, e);
                    break;
                case "Slice":
                    _freqOutHandlers.AdjustSlice(field, e);
                    break;
                case "SliceOps":
                    _freqOutHandlers.AdjustSliceOps(field, e);
                    break;
                case "SMeter":
                    _freqOutHandlers.AdjustSMeter(field, e);
                    break;
                // Sprint 26 Phase 8: modern-mode checkbox fields share the
                // classic handlers — same behavior, Don can arrow-right to
                // them without dropping into classic mode.
                case "Split":
                    _freqOutHandlers.AdjustSplit(field, e);
                    break;
                case "VOX":
                    _freqOutHandlers.AdjustVox(field, e);
                    break;
                case "TXSlice":
                    _freqOutHandlers.AdjustTxSlice(field, e);
                    break;
                case "Offset":
                    _freqOutHandlers.AdjustOffset(field, e);
                    break;
                case "RIT":
                    _freqOutHandlers.AdjustRit(field, e);
                    break;
                case "XIT":
                    _freqOutHandlers.AdjustXit(field, e);
                    break;
                case "Squelch":
                    _freqOutHandlers.AdjustSquelch(field, e);
                    break;
                case "SquelchLevel":
                    _freqOutHandlers.AdjustSquelchLevel(field, e);
                    break;
            }
            return;
        }

        // Classic mode: full field routing
        switch (field.Key)
        {
            case "Freq":
                _freqOutHandlers.AdjustFreq(field, e);
                break;
            case "Split":
                _freqOutHandlers.AdjustSplit(field, e);
                break;
            case "RIT":
                _freqOutHandlers.AdjustRit(field, e);
                break;
            case "XIT":
                _freqOutHandlers.AdjustXit(field, e);
                break;
            case "VOX":
                _freqOutHandlers.AdjustVox(field, e);
                break;
            case "TXSlice":
                _freqOutHandlers.AdjustTxSlice(field, e);
                break;
            case "SMeter":
                _freqOutHandlers.AdjustSMeter(field, e);
                break;
            case "Offset":
                _freqOutHandlers.AdjustOffset(field, e);
                break;
            case "Slice":
                _freqOutHandlers.AdjustSlice(field, e);
                break;
            case "SliceOps":
                _freqOutHandlers.AdjustSliceOps(field, e);
                break;
            case "Mute":
                _freqOutHandlers.AdjustMute(field, e);
                break;
            case "Volume":
                _freqOutHandlers.AdjustVolume(field, e);
                break;
            case "Squelch":
                _freqOutHandlers.AdjustSquelch(field, e);
                break;
            case "SquelchLevel":
                _freqOutHandlers.AdjustSquelchLevel(field, e);
                break;
        }
    }

    private bool _firstFreqDisplay = true;

    /// <summary>
    /// Update the frequency display from current rig state.
    /// Simplified version of Form1.showFrequency() that reads directly from the rig.
    /// </summary>
    private void ShowFrequency()
    {
        if (RigControl == null || OpenParms?.FormatFreq == null) return;

        try
        {
            // Frequency — skip writing during tuning speech suppression window to avoid
            // double-speaking (ShowFrequency and SpeakTuningDebounced both announce freq)
            ulong freq = RigControl.Transmit
                ? RigControl.TXFrequency
                : RigControl.VirtualRXFrequency;
            bool suppressFreqWrite = _freqOutHandlers != null &&
                DateTime.UtcNow < _freqOutHandlers.TuningSpeechUntil;
            if (freq > 0 && !suppressFreqWrite)
            {
                FreqOut.Write("Freq", OpenParms.FormatFreq(freq));
            }

            // S-meter (raw value)
            int smeter = (int)RigControl.SMeter;
            if (RigControl.Transmit)
            {
                // Forward power, NOT SMeter's integer watts: that truncates,
                // so 174 mW of real RF displayed as "0" — identical to not
                // transmitting (measured on an 8600, 2026-08-16).
                FreqOut.Write("SMeter",
                    FlexBase.FormatForwardPowerCompact(RigControl.ForwardPowerWatts));
            }
            else
            {
                // S-units
                if (RigControl.SmeterInDBM)
                {
                    FreqOut.Write("SMeter", smeter.ToString());
                }
                else if (SMeterReading.IsOverS9(smeter))
                {
                    // A third rendering — this one goes to an external display
                    // that wants a bare signed number — built from the same
                    // arithmetic as speech and braille.
                    FreqOut.Write("SMeter",
                        "+" + SMeterReading.ExcessOverS9(smeter).ToString());
                }
                else
                {
                    FreqOut.Write("SMeter", smeter.ToString());
                }
            }

            // Slice indicator — shows current active slice number
            FreqOut.Write("Slice", RigControl.ActiveSliceLetter);

            // Dynamic label for SliceOps — purpose-named per Sprint 28 bug bundle
            // (2026-04-28). Carries the active slice letter; the volume value is
            // announced when arrow up/down adjusts it, not on focus landing.
            FreqOut.SetFieldLabel("SliceOps", $"Slice operations: slice {RigControl.ActiveSliceLetter} controls");

            // Mute — current active slice mute state (GetVFOAudio true = audio on = not muted)
            FreqOut.Write("Mute", RigControl.GetVFOAudio(RigControl.RXVFO) ? " " : "M");

            // Volume — current active slice gain (0-100)
            int vol = RigControl.GetVFOGain(RigControl.RXVFO);
            FreqOut.Write("Volume", vol.ToString());

            // SliceOps — shows volume level for the slice audio field
            FreqOut.Write("SliceOps", vol.ToString());

            // Split
            bool isSplit = _freqOutHandlers?.GetSplitVFOs?.Invoke() == true;
            FreqOut.Write("Split", isSplit ? "S" : " ");

            // VOX
            FreqOut.Write("VOX", RigControl.Vox == FlexBase.OffOnValues.on ? "V" : " ");

            // QB Track I — Transmit slice letter; "-" when no slice keys the
            // radio (speech maps it to "none" in FrequencyDisplay).
            string txSliceLetter = RigControl.TXSliceLetter;
            FreqOut.Write("TXSlice", string.IsNullOrEmpty(txSliceLetter) ? "-" : txSliceLetter);

            // Sprint 28 Phase 3.9 — Squelch state + level.
            // Squelch field: "Q" when on, " " (space) when off — the active-state signal.
            // SquelchLevel field: always shows the stored numeric level. When squelch
            // is off the level is inactive but remembered, and showing the number
            // keeps the adjustment-announcement and the screen-reader re-read
            // consistent ("Squelch level 45" stays "45" whether squelch is on or off).
            // The adjacent Squelch field carries the active/inactive signal on its
            // own. Earlier design used "---" placeholder when off; dropped 2026-04-24
            // because it caused announce-vs-display mismatch for screen-reader users.
            bool squelchOn = RigControl.Squelch == FlexBase.OffOnValues.on;
            FreqOut.Write("Squelch", squelchOn ? "Q" : " ");
            FreqOut.Write("SquelchLevel", RigControl.SquelchLevel.ToString());

            // Offset
            FreqOut.Write("Offset", RigControl.OffsetDirection switch
            {
                FlexBase.OffsetDirections.plus => "+",
                FlexBase.OffsetDirections.minus => "-",
                _ => " "
            });

            // RIT
            var rit = RigControl.RIT;
            if (rit.Active)
            {
                string ritText = (rit.Value < 0 ? "-" : "+") + Math.Abs(rit.Value).ToString("d4");
                FreqOut.Write("RIT", ritText);
            }
            else
            {
                FreqOut.Write("RIT", " rrrr");
            }

            // XIT
            var xit = RigControl.XIT;
            if (xit.Active)
            {
                string xitText = (xit.Value < 0 ? "-" : "+") + Math.Abs(xit.Value).ToString("d4");
                FreqOut.Write("XIT", xitText);
            }
            else
            {
                FreqOut.Write("XIT", " xxxx");
            }

            if (FreqOut.Changed)
            {
                FreqOut.Display();
            }

            // Update title bar with compact status for Insert+T (screen reader reads window title)
            if (UpdateTitleBar != null)
            {
                string sliceLetter = RigControl.ActiveSliceLetter;
                string mode = RigControl.Mode ?? "";
                double freqMhz = (RigControl.Transmit ? RigControl.TXFrequency : RigControl.VirtualRXFrequency) / 1_000_000.0;
                UpdateTitleBar($"JJ Flexible Radio Access — Slice {sliceLetter}, {freqMhz:F3}, {mode}");
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"ShowFrequency error: {ex.Message}", TraceLevel.Error);
        }
    }

    /// <summary>
    /// Configure variable controls based on rig capabilities.
    /// Replaces Form1.configVariableControls().
    /// </summary>
    private void ConfigureVariableControls()
    {
        if (RigControl == null) return;

        _enableDisableControls.Remove(TransmitButton);
        _enableDisableControls.Remove(TuneToggleButton);
        _enableDisableControls.Remove(AntennaTuneButton);
        if (RigControl.MyCaps.HasCap(RigCaps.Caps.ManualTransmit))
        {
            _enableDisableControls.Add(TransmitButton);
            _enableDisableControls.Add(TuneToggleButton);
            _enableDisableControls.Add(AntennaTuneButton);
            TransmitButton.Visibility = Visibility.Visible;
            TuneToggleButton.Visibility = Visibility.Visible;
        }
        else
        {
            TransmitButton.Visibility = Visibility.Collapsed;
            TuneToggleButton.Visibility = Visibility.Collapsed;
        }
    }

    // ── Event Handlers ──────────────────────────────────────

    private void PowerStatusHandler(object sender, bool powerOn)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => PowerStatusHandler(sender, powerOn));
            return;
        }

        if (powerOn) PowerNowOn();
        else PowerNowOffInternal();
    }

    private void NoSliceErrorHandler(object sender, string msg)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => NoSliceErrorHandler(sender, msg));
            return;
        }

        // No dialog here, on purpose (2026-08-06). This fires from inside
        // Start() while the TopMost Connecting window is still up, so a modal
        // parked behind it is a question nobody can perceive — with all of
        // Don's slices in use, the app sat at "Connecting" until Escape
        // finally unwound the hidden dialog chain. There is also no actual
        // question to ask: the only move is to back out and let the user
        // coordinate with whoever has the slices. Speak the reason and step
        // aside; Start() returns false and the normal failure path
        // (OpenTheRadio → Abort → CloseTheRadio) tears everything down
        // promptly. Same reasoning as the round-27 save-prompt removal.
        //
        // The speak must OUTLIVE the teardown (2026-08-06, traces 202128 and
        // 202229): fired inline it dies in the focus churn — the Connecting
        // window closes and the shell reactivates within milliseconds, and
        // the screen reader's own focus announcements cut off anything in
        // flight. Noel heard the earlier phase "connected" and then nothing,
        // twice. Same swallow documented at the "Default account set" speak;
        // longer delay here because this churn includes a cross-thread form
        // close plus shell activation. Past tense — by the time this speaks,
        // the teardown is done.
        string speech = Radios.Lexicon.Get("connect.home.disconnected_after", ("msg", msg));
        System.Threading.Tasks.Task.Delay(750).ContinueWith(_ =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                Radios.ScreenReaderOutput.Speak(speech, VerbosityLevel.Critical, true);
            });
        });
    }

    /// <summary>
    /// The last licence-gating verdict this window acted on, so an event that
    /// changes nothing an operator can perceive costs nothing.
    ///
    /// <para>FlexLib raises FeatureLicenseChanged for EVERY property change on
    /// the licence object, and the whole object is re-hooked on each radio
    /// connect — so the event is far chattier than the handful of verdicts it
    /// can actually move. Rebuilding the native menu bar on each one would
    /// churn a Win32 HMENU that a screen reader may be reading at the time.
    /// Null means no verdict has been recorded yet.</para>
    /// </summary>
    private string? _featureGateSignature;

    /// <summary>
    /// The licence-gated features changed their minds, so the surfaces that
    /// gate on them get rebuilt.
    ///
    /// <para>This was a stub that only traced, with a comment promising a
    /// "full implementation when WPF menus support advanced feature gating".
    /// The menus have gated on licence state for a long time; what was missing
    /// was anything to tell them the state had MOVED. So a subscription that
    /// arrived seconds after connect — the ordinary case, since the radio
    /// sends its feature list asynchronously — left the menu frozen at
    /// whatever it decided during connect, and the operator's diversity or
    /// advanced-NR entry stayed "unavailable" for a feature they had paid
    /// for until the next reconnect.</para>
    ///
    /// <para>Deliberately silent. A licence verdict moving is a change to what
    /// the menus offer, not an event the operator asked about, and it lands
    /// during the connect storm where announcements are most damaging. The
    /// Feature Availability tab is where the reasons are read on demand.</para>
    /// </summary>
    private void FeatureLicenseChangedHandler(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            // BeginInvoke, NEVER Invoke (#402). This event is raised from
            // FlexLib's TCP read loop (status parse → FeatureLicense
            // PropertyChanged), and that loop is strictly sequential: it does
            // not read the next line until this handler returns. A synchronous
            // Invoke therefore parks the radio's ENTIRE receive channel on the
            // UI thread's message pump. On 2026-08-30 the pump was blocked in
            // the station-name wait, this Invoke wedged the read loop about
            // 1.2 s into the connect, and every reply and status after it —
            // including the station-name echo the wait was polling for — sat
            // unread for 45 seconds, three attempts in a row, on a healthy
            // radio a few feet away. Queued execution is all this handler
            // needs: it is signature-gated and last-writer-wins.
            Dispatcher.BeginInvoke(() => FeatureLicenseChangedHandler(sender, e));
            return;
        }

        var rig = RigControl;
        if (rig == null)
        {
            _featureGateSignature = null;
            return;
        }

        string signature;
        try
        {
            // Every input a licence-gated menu decision reads. Hardware facts
            // are in it too: they cannot change under a live radio, but they
            // CAN differ from the last radio this window was connected to, and
            // the menus must not inherit the previous rig's verdict.
            signature = string.Join("|",
                rig.DiversityHardwareSupported,
                rig.DiversityReady,
                rig.DiversityGateMessage,
                rig.NeuralNRHardwareSupported,
                rig.NoiseReductionLicenseReported,
                rig.NoiseReductionLicensed);
        }
        catch (Exception ex)
        {
            // Reading gate state must never break the connection. Treat an
            // unreadable verdict as "unchanged" — the menus keep what they
            // have, which is the last state we could actually vouch for.
            Tracing.TraceLine(
                $"FeatureLicenseChanged: could not read gate state: {ex.Message}",
                TraceLevel.Warning);
            return;
        }

        if (signature == _featureGateSignature)
        {
            Tracing.TraceLine("FeatureLicenseChanged: no gating change", TraceLevel.Verbose);
            return;
        }

        if (_featureGateSignature == null)
        {
            // First verdict for this radio. Traced in full and named as the
            // open question it answers: does FeatureLicense populate at all on
            // a purely LOCAL connect with no SmartLink account? Nothing in the
            // code can settle that — the radio decides — so the trace from one
            // local session does. Read it as: reported=False through an entire
            // local session means it never populated, and every "unsubscribed"
            // verdict in this app must therefore stay a "we were not told".
            Tracing.TraceLine(
                "FeatureLicenseChanged: first licence verdict for this radio — "
                + $"connection={(RigControl?.RemoteRig == true ? "SmartLink" : "local")}, "
                + $"nrLicenceReported={rig.NoiseReductionLicenseReported}, "
                + $"nrLicenceEnabled={rig.NoiseReductionLicensed}, "
                + $"diversityGate='{rig.DiversityGateMessage}'",
                TraceLevel.Info);
        }

        Tracing.TraceLine(
            $"FeatureLicenseChanged: gating moved to {signature} — rebuilding menus",
            TraceLevel.Info);
        _featureGateSignature = signature;
        SetupOperationsMenu();
    }

    private void TransmitChangeHandler(object sender, bool transmit)
    {
        if (!Dispatcher.CheckAccess())
        {
            // BeginInvoke, NEVER Invoke — raised from FlexLib's sequential
            // TCP read loop (interlock status parse); a synchronous marshal
            // here stalls every subsequent reply and status whenever the UI
            // thread is busy. Same wedge class as FeatureLicenseChanged
            // (#402). Queued is safe: button text is last-writer-wins and
            // EscapeUnlock is idempotent.
            Dispatcher.BeginInvoke(() => TransmitChangeHandler(sender, transmit));
            return;
        }

        Tracing.TraceLine($"TransmitChange: {transmit}", TraceLevel.Info);

        // Update Transmit button visual
        TransmitButton.Content = transmit ? "TX On" : "Transmit";

        // If TX turned off externally (CAT, SmartSDR), sync PTT controller to Idle
        if (!transmit && _pttController != null && _pttController.IsTransmitting)
        {
            _pttController.EscapeUnlock();
        }
    }

    private void FlexAntTuneStartStopHandler(FlexBase.FlexAntTunerArg e)
    {
        if (!Dispatcher.CheckAccess())
        {
            // BeginInvoke, NEVER Invoke — raised from FlexLib's sequential
            // TCP read loop (ATU status parse); a synchronous marshal here
            // stalls the radio's whole receive channel whenever the UI
            // thread is busy. Same wedge class as FeatureLicenseChanged
            // (#402). Queued is safe: tune progress display is
            // last-writer-wins.
            Dispatcher.BeginInvoke(() => FlexAntTuneStartStopHandler(e));
            return;
        }

        if (RigControl == null) return;

        if (RigControl.FlexTunerType == FlexBase.FlexTunerTypes.manual)
        {
            if (e.Status == "OK")
            {
                SetButtonText(AntennaTuneButton, e.SWR);
            }
        }
        else
        {
            SetButtonText(AntennaTuneButton, e.Status);
        }

        // Audio narrative for ATU tune cycle.
        // Progress earcons only for automatic ATU operations (the radio is doing its
        // own thing and the operator needs to know when it finishes).
        // Manual tune (Ctrl+Shift+T) never gets progress beeps — the operator controls
        // the carrier and uses meter tones/speech to monitor SWR, power, etc.
        bool isAutoATU = e.Type == "auto" && RigControl.HasATU;

        switch (e.Status)
        {
            case "InProgress":
                if (isAutoATU)
                {
                    EarconPlayer.StartATUProgressEarcon();
                    StartATUTimeout();
                }
                break;
            case "OK":
            case "Successful":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                if (isAutoATU)
                    EarconPlayer.ATUSuccessTone();
                AnnounceSettledSwrAfterTune(isFailure: false);
                break;
            case "Fail":
            case "FailBypass":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                if (isAutoATU)
                {
                    EarconPlayer.ATUFailTone();
                    AnnounceSettledSwrAfterTune(isFailure: true);
                }
                break;
            case "Bypass":
            case "ManualBypass":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                if (isAutoATU)
                    Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tune.atu_bypassed"), VerbosityLevel.Terse);
                break;
            case "NotStarted":
            case "Aborted":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                break;
        }
    }

    /// <summary>
    /// Speak the settled SWR after an auto-ATU tune cycle. 200 ms delay
    /// lets mid-sweep transients drop off before the value is read. This
    /// overload is for the auto-ATU path where TX is still active briefly
    /// after the OK/Fail status hits. For manual carrier (Ctrl+Shift+T),
    /// TX stops the instant the operator releases — use the float overload
    /// with the pre-captured value, because reading SWR post-TX gives the
    /// meter's idle rest value (~1.0), not the final match.
    /// Gated by <see cref="AudioOutputConfig.AnnounceSwrAfterTune"/>.
    /// </summary>
    private async void AnnounceSettledSwrAfterTune(bool isFailure)
    {
        // Both early exits trace. A tune that announces nothing and records
        // nothing is indistinguishable from a tune whose announcement was lost
        // downstream, and that is the #409 inference all over again.
        if (CurrentAudioConfig?.AnnounceSwrAfterTune == false)
        {
            Tracing.TraceLine("tuneSpoken: suppressed, AnnounceSwrAfterTune is off",
                TraceLevel.Info);
            return;
        }
        if (RigControl == null)
        {
            Tracing.TraceLine("tuneSpoken: suppressed, no rig", TraceLevel.Info);
            return;
        }

        try
        {
            await Task.Delay(200).ConfigureAwait(true);
            if (RigControl == null) return;
            SpeakSwrAfterTune(isFailure, RigControl.SWRValue);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"AnnounceSettledSwrAfterTune: {ex.Message}", TraceLevel.Warning);
        }
    }

    /// <summary>
    /// Manual-carrier variant: caller has already captured the SWR while
    /// TX was active (before setting TxTune = false). No delay, no re-read —
    /// reading post-TX would give ~1.0 because the meter snaps to its idle
    /// rest value when forward power drops to zero.
    /// Gated by <see cref="AudioOutputConfig.AnnounceSwrAfterTune"/>.
    /// </summary>
    private void AnnounceCapturedSwrAfterTune(bool isFailure, float capturedSwr)
    {
        if (CurrentAudioConfig?.AnnounceSwrAfterTune == false)
        {
            Tracing.TraceLine(
                $"tuneSpoken: suppressed, AnnounceSwrAfterTune is off (captured {capturedSwr:F2})",
                TraceLevel.Info);
            return;
        }
        SpeakSwrAfterTune(isFailure, capturedSwr);
    }

    private static void SpeakSwrAfterTune(bool isFailure, float swr)
    {
        // "SWR is X to 1" is technically accurate but verbose for a status
        // readout. Hams say the leading number and the ratio is implicit.
        string text = Radios.Lexicon.Get(
            isFailure ? "audio.tune.swr_failed" : "audio.tune.swr",
            ("swr", $"{swr:F1}"));
        VerbosityLevel level = isFailure ? VerbosityLevel.Critical : VerbosityLevel.Terse;

        // Sprint 44 Track E — WRITE DOWN THE NUMBER WE SAY OUT LOUD.
        //
        // This method is the single funnel for the settled SWR: the auto-ATU
        // path arrives through AnnounceSettledSwrAfterTune and the manual
        // carrier through AnnounceCapturedSwrAfterTune, and both end here. Until
        // 2026-09-02 the value was spoken and never recorded, so a tester's "my
        // tuner said 1.7" could not be corroborated from a diagnostic bundle —
        // which was the entirety of Don's report, and a day went into a number
        // the application had already computed and discarded.
        //
        // Deliberately separate from FlexBase's tuneResult line rather than
        // folded into it. That one records what the RADIO settled at; this one
        // records what the OPERATOR was told. They are supposed to agree, and
        // the day they do not, having both is the whole diagnosis.
        Tracing.TraceLine(
            $"tuneSpoken: swr={swr:F2} spokenAs={swr:F1} failure={isFailure} level={level}",
            TraceLevel.Info);

        Radios.ScreenReaderOutput.Speak(text, level);
    }

    private void ConnectedEventHandler(object sender, FlexBase.ConnectedArg e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ConnectedEventHandler(sender, e));
            return;
        }

        Tracing.TraceLine($"ConnectedEvent: Power={_radioPowerOn} Connected={e.Connected}", TraceLevel.Info);
        if (_radioPowerOn && !e.Connected)
        {
            PowerNowOffInternal();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.radio_disconnected"), VerbosityLevel.Critical, true);

            if (ShowErrorCallback != null)
                ShowErrorCallback(Radios.Lexicon.Get("connect.home.radio_disconnected"), "Error");
            else
                Dialogs.AdvisoryDialog.Show(Radios.Lexicon.Get("connect.home.radio_disconnected_title"), Radios.Lexicon.Get("connect.home.radio_disconnected_sentence"));
        }
    }

    // ── Power On/Off ──────────────────────────────────────

    private void PowerNowOn()
    {
        // Capture the off→on transition FIRST, before anything in this body
        // can touch the flag, and raise the flag here so everything the body
        // calls sees a powered radio (#369, 2026-08-28). The transition used
        // to be tested 36 lines further down, after EnableDisableWindowControls
        // had already — as a side effect it no longer has — set the flag true,
        // which made the test constant-false and silently skipped the connect
        // earcon, the PC-audio policy and the REM ON queued intent on EVERY
        // connect since each was added. wasOff is the one honest reading of
        // the transition; the trace records it so a re-raised power event
        // (wasOff=False) is visible in a capture instead of indistinguishable
        // from a first connect.
        bool wasOff = !_radioPowerOn;
        _radioPowerOn = true;
        Tracing.TraceLine($"MainWindow PowerNowOn wasOff={wasOff}", TraceLevel.Info);

        // Sprint 42 Track D (#395): tell the quiet scope a radio really
        // arrived, so its finish knows the connect narration owns the
        // announcement and the return-to-app landing must NOT run. If no
        // scope is open (a re-raised power event mid-session) this records
        // nothing.
        _connectQuiet.NotePowerOn();

        // Setup frequency display
        SetupFreqout();

        // Setup operations menu
        SetupOperationsMenu();

        // Configure controls based on rig caps
        ConfigureVariableControls();

        // Enable all controls
        EnableDisableWindowControls(true);

        // Wire additional event handlers
        if (RigControl != null)
        {
            RigControl.TransmitChange += TransmitChangeHandler;
            RigControl.FlexAntTunerStartStop += FlexAntTuneStartStopHandler;
            RigControl.ConnectedEvent += ConnectedEventHandler;
        }

        // Status updates
        WriteStatus("Power", "On");
        if (RigControl != null)
            WriteStatus("Memories", RigControl.NumberOfMemories.ToString());

        // Wire panadapter braille display
        WirePanDisplay();

        // Initialize screen fields panel (Sprint 14)
        if (RigControl != null)
        {
            FieldsPanel.Initialize(RigControl);
            _brailleEngine.SetRig(RigControl);

            // Sprint 28 Phase 3.6 — subscribe to mode changes so CW-only text
            // areas hide/show appropriately. Initial evaluation below covers the
            // case where the radio comes up in a non-CW mode.
            RigControl.ModeChanged += OnRadioModeChanged;
            UpdateTextAreasVisibility();
        }

        // The signature connect double-beep and the per-radio connect
        // policies used to fire HERE, before the audio config below was
        // loaded and applied. Sprint 42 Track D (#395) moved both after that
        // block — see the comments at their new site for why the tone could
        // never reliably survive from this position.

        StatusText.Text = Radios.Lexicon.Get("connect.home.status_power_on");

        // Initialize PTT safety controller (Sprint 15)
        if (RigControl != null && OpenParms != null)
        {
            CurrentPttConfig = PttConfig.Load(
                OpenParms.ConfigDirectory,
                OpenParms.GetOperatorName());
            _pttController = new PttSafetyController(
                () => RigControl,
                () => _radioPowerOn,
                CurrentPttConfig,
                text => StatusTx.Text = text);

            // Wire license-aware TX lockout (Sprint 17 Track C)
            _pttController.CanTransmitHereCheck = () =>
                _freqOutHandlers?.CanTransmitHere() ?? true;

            // Give the Audio Workshop's Audio Check session a live path to
            // the controller (QB Track G). Resolved per call — the controller
            // is recreated on operator switch and nulled on power-off, so the
            // workshop must never cache it.
            Dialogs.AudioWorkshopDialog.PttControllerSource = () => _pttController;

            // Apply band memory and frequency units settings from config
            if (_freqOutHandlers != null)
            {
                _freqOutHandlers.BandMemoryEnabled = CurrentPttConfig.BandMemoryEnabled;
                _freqOutHandlers.FrequencyUnits = CurrentPttConfig.FrequencyDisplayUnits;
            }
        }

        // Load audio config and initialize meter tones
        if (OpenParms != null)
        {
            CurrentAudioConfig = AudioOutputConfig.Load(OpenParms.ConfigDirectory);
            MeterToneEngine.Initialize();
            CurrentAudioConfig.Apply();
            if (RigControl != null)
                MeterToneEngine.AttachToRadio(RigControl);

            // Give the Audio Workshop the per-operator app settings store
            // (Audio Track C: test-tone frequency/level/monitor live here, NOT
            // in the serial-keyed per-radio config — hearing doesn't change
            // when you switch rigs). Save is immediate on change so a crash
            // doesn't lose the operator's dialed-in tone.
            Dialogs.AudioWorkshopDialog.AudioConfigSource = () => CurrentAudioConfig;
            Dialogs.AudioWorkshopDialog.AudioConfigSave = () =>
            {
                if (CurrentAudioConfig != null && OpenParms != null)
                    CurrentAudioConfig.Save(OpenParms.ConfigDirectory);
            };

            // DSP controls track (2026-08-11): the noise-capture narrator
            // remembers each completed capture in the config (same store,
            // same immediate-save discipline as the workshop hooks above)...
            NoiseCaptureNarrator.AudioConfigSource = () => CurrentAudioConfig;
            NoiseCaptureNarrator.AudioConfigSave = () =>
            {
                if (CurrentAudioConfig != null && OpenParms != null)
                    CurrentAudioConfig.Save(OpenParms.ConfigDirectory);
            };
            // ...and the pipeline (created in FieldsPanel.Initialize, before
            // this config existed) now gets its persisted PC NR settings and
            // the last noise profile back. This is what makes "PC Spectral
            // NR on, no noise profile loaded" stop greeting every session.
            FieldsPanel.ApplyDspConfig(CurrentAudioConfig);

            // Apply braille config
            _brailleEngine.Enabled = CurrentAudioConfig.BrailleEnabled;
            _brailleEngine.CellCount = CurrentAudioConfig.BrailleCellCount;
            _brailleEngine.EnabledFields = (BrailleFields)CurrentAudioConfig.BrailleFields;
            _brailleEngine.UpdateTimerState();

            // Apply panadapter visibility — Collapsed removes the control from layout
            // AND the tab order, so users who don't use the waterfall aren't forced to
            // Tab through it. Pan callback suppresses the braille push when hidden too.
            ApplyPanadapterVisibility();

            // Apply CW notification config
            ApplyCwNotifierSettings(CurrentAudioConfig);

            // Migrate CW settings to root on every connect. CW is user-scope (not per-radio)
            // but historically lived only in per-radio config. The MainWindow constructor
            // loads root at app startup to set CwNotificationsEnabled BEFORE any connect
            // fires AS. Without this migration, users with CW enabled in per-radio config
            // would still have false in root after app restart and AS would silently skip.
            // Defense-in-depth vs the NativeMenuBar save-to-root which only fires if the
            // user explicitly opens Settings → OK.
            try
            {
                string baseConfigDir = System.IO.Path.Combine(
                    Radios.RadioConfig.AppDataRoot);
                if (System.IO.Directory.Exists(baseConfigDir))
                {
                    var rootConfig = AudioOutputConfig.Load(baseConfigDir);
                    rootConfig.CwNotificationsEnabled = CurrentAudioConfig.CwNotificationsEnabled;
                    rootConfig.CwModeAnnounce = CurrentAudioConfig.CwModeAnnounce;
                    rootConfig.CwSidetoneHz = CurrentAudioConfig.CwSidetoneHz;
                    rootConfig.CwSpeedWpm = CurrentAudioConfig.CwSpeedWpm;
                    // Sprint 33 Track F: the pitch source, the keying
                    // waveform and the alert voice set are user-scope for
                    // the same reason the four above are — they describe
                    // this operator's ears, not this radio. Left out of
                    // this list they would apply until the next restart
                    // and then quietly revert, which is worse than not
                    // offering them.
                    rootConfig.CwPitchFollowsRadio = CurrentAudioConfig.CwPitchFollowsRadio;
                    rootConfig.CwWaveform = CurrentAudioConfig.CwWaveform;
                    rootConfig.EarconVoiceSet = CurrentAudioConfig.EarconVoiceSet;
                    rootConfig.Save(baseConfigDir);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerOn: CW migrate-to-root failed: {ex.Message}");
            }

            // The CW prosign delegates (PlayCwAS/BT/SK/Mode) are wired ONCE in
            // the constructor and deliberately NOT re-wired here. A PowerOn
            // duplicate used to live at this spot; its PlayCwSK copy was the
            // pre-BUG-061 multi-call version (separate PlayString + PlaySK
            // queue entries), so every connect silently swapped the clean
            // single-utterance "73 <SK> ee" waveform for the gappy one.
            // Removed 2026-08-07 (QB Track A).

            // Fire BT (connected) prosign now that delegates are wired and CwNotificationsEnabled
            // is loaded. This used to fire in FlexBase at connect success, but that location
            // raced with MainWindow init -- PlayCwBT was null on first connect. PowerOn is the
            // semantically correct moment: radio is up, delegates are live, CW config is applied.
            //
            // 2026-04-24: 1-second delay added before the BT plays. Without
            // the delay, the prosign fires while the radio audio pipeline is
            // still initialising, and a brief device-contention stall can
            // split BT (dah-di-di-di-dah) at a mid-character boundary so it
            // perceptually parses as "N U" (dah-dit then dit-dit-dah —
            // identical elements, audible gap). Letting the audio pipeline
            // settle first eliminates the split. Fire-and-forget — no need
            // to block PowerOn waiting for the prosign to finish.
            if (Radios.ScreenReaderOutput.CwNotificationsEnabled)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1000);
                    var bt = Radios.ScreenReaderOutput.PlayCwBT;
                    if (bt != null) await bt.Invoke();
                });
            }

            // MultiFlex client connect/disconnect earcons. These bypass the
            // EarconCategory gates (direct chirps), so the #171 transcript
            // record happens here, where the earcon's identity is known.
            Radios.ScreenReaderOutput.PlayClientConnectedEarcon = () =>
            {
                if (Radios.OutputChannelRecorder.RecordEnabled)
                    Radios.OutputChannelRecorder.RecordEarcon(
                        "ClientConnectedEarcon", "ungated", true,
                        EarconPlayer.EarconsEnabled && EarconPlayer.AlertChannelLive
                            && Radios.OutputChannelRecorder.RenderEnabled,
                        600, 120, detail: "chirp to 900Hz");
                EarconPlayer.PlayChirp(600, 900, 120, 0.2f);
            };
            Radios.ScreenReaderOutput.PlayClientDisconnectedEarcon = () =>
            {
                if (Radios.OutputChannelRecorder.RecordEnabled)
                    Radios.OutputChannelRecorder.RecordEarcon(
                        "ClientDisconnectedEarcon", "ungated", true,
                        EarconPlayer.EarconsEnabled && EarconPlayer.AlertChannelLive
                            && Radios.OutputChannelRecorder.RenderEnabled,
                        900, 120, detail: "chirp to 600Hz");
                EarconPlayer.PlayChirp(900, 600, 120, 0.2f);
            };

            // Warning alarm, for the radio layer's unprompted warnings (#111)
            Radios.ScreenReaderOutput.PlayWarningAlarmEarcon = () => EarconPlayer.WarningAlarmTone();

            // Apply typing sound to FreqOutHandlers
            if (_freqOutHandlers != null)
            {
                _freqOutHandlers.TypingSound = CurrentAudioConfig.TypingSound;
                // Pre-load keyboard sounds if mechanical mode is unlocked
                if (!EarconPlayer.HasKeyboardSounds &&
                    FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref2, CurrentAudioConfig.TuningHash))
                {
                    CalibrationEngine.LoadKeyboardSounds();
                }
            }
        }

        // Signature connect double-beep — every successful connect path
        // (picker local, picker remote, auto-connect, reconnect) converges on
        // this power transition, so this is the one hook that covers them all
        // (QB Track A stretch, 2026-08-07). Guarded on the off→on transition
        // so a re-raised power event can't double-fire it — wasOff, captured
        // at the top of this method before anything could disturb the flag.
        //
        // MOVED below the audio-config block on purpose (Sprint 42 Track D,
        // #395). It used to fire ~65 lines EARLIER, and a few milliseconds
        // after it started, CurrentAudioConfig.Apply() above called
        // EarconPlayer.SetAlertDevice — which stops, disposes and recreates
        // the alert channel's output device even when the device number is
        // unchanged. The 200 ms tone was being torn down mid-play by our own
        // connect path, and how much of it survived depended on how much the
        // doomed device had prefetched — which is exactly why Noel heard a
        // different fragment every connect (#395: "a double beep, then one
        // high beep"). From here the tone starts on a device nothing later in
        // this method touches, and with the operator's per-radio earcon
        // config already applied instead of the previous session's.
        //
        // The tone gets its OWN transition test, deliberately separate from
        // the policy block below (#369, Noel's question at the bench: the tone
        // is feedback about a CONNECTION EVENT and plays through the earcon
        // device; the policies are per-radio state application). The condition
        // is the same today, and that is fine — what must never happen again
        // is the cheap, audible, harmless sound being hostage to the guard of
        // the expensive silent ones, so that when a guard goes wrong the only
        // audible symptom disappears with it.
        if (wasOff)
        {
            try { EarconPlayer.ConnectSuccessTone(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PowerNowOn: connect earcon failed: {ex.Message}", TraceLevel.Warning);
            }
        }

        // Per-radio connect POLICIES, on the same off→on transition but under
        // their own test: double-applying these on a re-raised power event
        // would be a real fault, where a doubled tone is an annoyance.
        // Moved with the tone (#395) so the heard order is unchanged: tone
        // first, then "PC audio on, as you left it" — and the announcement now
        // speaks at the verbosity the config block above just loaded.
        if (wasOff)
        {
            // Per-radio PC audio on connect (Threads Track, 2026-08-12).
            // Runs AFTER FlexBase's open sequence has done its historical
            // remote auto-on, so this is policy on top, never a race.
            try { ApplyPcAudioOnConnect(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PowerNowOn: PC audio on-connect policy failed: {ex.Message}", TraceLevel.Error);
            }

            // Per-radio REM ON queued intent (Track C, settings that stick).
            // A setting made while disconnected survives and applies here.
            try { ApplyRemOnOnConnect(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PowerNowOn: REM ON on-connect policy failed: {ex.Message}", TraceLevel.Error);
            }
        }

        // Initialize band tracking from current frequency so first tune doesn't
        // trigger a false "Entering extra phone" boundary notification
        if (_freqOutHandlers != null && RigControl != null)
        {
            ulong freq = RigControl.VirtualRXFrequency;
            if (freq > 0) _freqOutHandlers.InitializeBandTracking(freq);
        }

        // VB-side tasks (knob setup, tracing)
        PowerOnCallback?.Invoke();

        // Sprint 22 Phase 8: Announce radio status after connect.
        //
        // #359 — the sentence this schedules and the slice census were saying
        // one thing twice, 60 to 94 ms apart, in two vocabularies. The full
        // sentence is the one the operator should hear, so the census's
        // spoken half defers to it: armed here, immediately before the
        // sentence is scheduled, consumed by the first slice-settle. If the
        // sentence falls back to connection-only (slices not yet populated at
        // its 1.5 s mark), SpeakConnectStatus releases the census — it is
        // then the only slice announcement there will be.
        RigControl?.SuppressFirstSpokenCensusForConnect();
        SpeakConnectStatus();

        // Sprint 42 Track D (#395): the connect churn is done being ISSUED —
        // request the quiet scope's end. The finish is posted at Background
        // priority, so the coalesced menu rebuild, the activation restores
        // and every focus event this method caused all land inside the scope,
        // and only then does the one deliberate landing run. If the flow came
        // through a door (menu, rescue button), that door's own end request
        // simply no-ops after this one wins — or vice versa; the finish runs
        // once either way.
        EndConnectFlowQuiet("power-on complete");
    }

    /// <summary>
    /// Per-radio PC audio on connect (Threads Track, 2026-08-12). Reads the
    /// serial-keyed RadioConfig and applies its PcAudioOnConnect policy:
    /// always on, always off, or the operator's remembered last choice —
    /// remember-last being the default, with the explicit modes there
    /// because remember-last faithfully carries an accident forward (a
    /// hiccup-off would otherwise poison every later session, which for a
    /// remote-only operator means a silent radio every night).
    ///
    /// Runs AFTER FlexBase's open sequence, which already turns PC audio on
    /// for remote (WAN) connects; with no recorded choice this method
    /// changes nothing, so pre-existing configs behave exactly as before.
    /// Whatever happens is announced — a switch is never flipped silently.
    /// </summary>
    private void ApplyPcAudioOnConnect()
    {
        var rig = RigControl;
        if (rig == null) return;
        string serial = rig.SelectedRadioSerial;
        if (string.IsNullOrEmpty(serial)) return;

        var cfg = RadioConfig.LoadForRadio(serial);
        bool before = rig.PCAudio;
        bool? desired = cfg.DesiredPcAudioOnConnect;
        Tracing.TraceLine(
            $"ApplyPcAudioOnConnect: mode={cfg.PcAudioOnConnect} lastKnown={cfg.PcAudioLastStateKnown} lastOn={cfg.PcAudioLastOn} before={before}",
            TraceLevel.Info);

        if (desired == null)
        {
            // No opinion recorded: historical behaviour stands (remote
            // connects arrive here already on). Still say so when it's on —
            // audio should now be flowing, and hearing nothing after this
            // announcement is itself a useful signal.
            if (before)
                ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.pc_audio.on_home"), VerbosityLevel.Terse);
            return;
        }

        if (desired.Value != before) rig.PCAudio = desired.Value;
        bool actual = rig.PCAudio;

        string reason = Radios.Lexicon.Get(cfg.PcAudioOnConnect switch
        {
            PcAudioOnConnectModes.AlwaysOn => "audio.pc_audio.reason_always_on",
            PcAudioOnConnectModes.AlwaysOff => "audio.pc_audio.reason_always_off",
            _ => "audio.pc_audio.reason_as_you_left_it",
        });

        if (desired.Value && !actual)
        {
            // Wanted on, could not start (no usable sound device is the
            // usual cause). The audio path speaks its own failure detail;
            // this names the consequence.
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.pc_audio.could_not_start_home"),
                VerbosityLevel.Critical);
        }
        else if (actual)
        {
            ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("audio.pc_audio.on_because", ("reason", reason)),
                VerbosityLevel.Terse);
        }
        else if (before)
        {
            // The policy just turned it off. Over remote that costs
            // everything, and that must never be a silent surprise.
            ScreenReaderOutput.Speak(
                Radios.Lexicon.Get(rig.RemoteRig
                    ? "audio.pc_audio.off_because_remote"
                    : "audio.pc_audio.off_because",
                    ("reason", reason)),
                VerbosityLevel.Terse);
        }
        // Off and already off: nothing flipped, nothing to hear — stay quiet.
    }

    /// <summary>
    /// Per-radio REM ON queued intent (Track C, settings that stick). REM ON
    /// is radio-persistent state that can only be written with the radio
    /// connected — which is exactly when a remote-base owner is NOT thinking
    /// about it. The Radios tab stores the intent with no radio present;
    /// this applies it on the same off→on transition as the PC audio policy.
    /// Idempotent: re-asserting the persisted value on every connect is
    /// harmless and self-healing. Announces only when it actually changed
    /// something — a no-op every session would be noise.
    /// </summary>
    private void ApplyRemOnOnConnect()
    {
        var rig = RigControl;
        if (rig == null) return;
        string serial = rig.SelectedRadioSerial;
        if (string.IsNullOrEmpty(serial)) return;

        var cfg = RadioConfig.LoadForRadio(serial);
        if (cfg.RemOnOnConnect == RemOnOnConnectModes.LeaveAlone) return;

        // The change-nothing hold outranks the queued intent (#403): both are
        // the operator's own per-radio answers, and the hold is the later,
        // sharper one. Checked here so the automatic path skips quietly — the
        // connect announcement has already said the radio is being left alone
        // — instead of reaching the rig's setter and hearing a refusal shaped
        // for a person who just pressed something.
        if (cfg.ChangeNothingOnThisRadio)
        {
            Tracing.TraceLine(
                "ApplyRemOnOnConnect: skipped — change nothing is on for this radio",
                TraceLevel.Info);
            return;
        }

        bool desired = cfg.RemOnOnConnect == RemOnOnConnectModes.TurnOn;
        bool before = rig.RemoteOnEnabled;
        Tracing.TraceLine(
            $"ApplyRemOnOnConnect: mode={cfg.RemOnOnConnect} before={before}",
            TraceLevel.Info);
        if (before == desired) return;

        rig.RemoteOnEnabled = desired;
        ScreenReaderOutput.Speak(
            Radios.Lexicon.Get(desired
                ? "connect.home.rem_on_enabled"
                : "connect.home.rem_on_disabled"),
            VerbosityLevel.Terse);
    }

    /// <summary>
    /// Internal power-off handler. Implements the full power-off sequence.
    /// </summary>
    private void PowerNowOffInternal()
    {
        Tracing.TraceLine("MainWindow PowerNowOff", TraceLevel.Info);

        if (RigControl != null)
        {
            RigControl.TransmitChange -= TransmitChangeHandler;
            RigControl.FlexAntTunerStartStop -= FlexAntTuneStartStopHandler;
            RigControl.ConnectedEvent -= ConnectedEventHandler;
        }

        _radioPowerOn = false;

        // Dispose PTT safety controller (Sprint 15) — stops TX if active
        _pttController?.Dispose();
        _pttController = null;

        // A deferred PTT release (#216) has nothing left to release; the hold
        // filter keeps its learned state — that describes the screen reader,
        // which survives the radio.
        _pttDeferTimer?.Stop();
        _pttHoldFilter.Reset();

        // Stop the Audio Workshop's poll timer (and any Audio Check session) —
        // the workshop singleton outlives the radio, and its 2 Hz tick raced
        // this teardown nulling theRadio (2026-08-07 app-close crash).
        Dialogs.AudioWorkshopDialog.NotifyRigGone();

        // Detach screen fields panel (Sprint 14)
        FieldsPanel.Detach();

        // Sprint 28 Phase 3.6 — unsubscribe from mode changes to avoid leaking
        // handler references across radio reconnects.
        if (RigControl != null)
        {
            RigControl.ModeChanged -= OnRadioModeChanged;
        }

        if (!_isClosing)
        {
            FreqOut.Clear();
            WriteStatus("Power", "Off");
            EnableDisableWindowControls(false);
            StatusText.Text = Radios.Lexicon.Get("connect.home.status_power_off");
        }
    }

    // ── Panadapter Braille Display — Sprint 12 Phase 12.10 ──────

    /// <summary>
    /// Wire the panadapter braille display callback from WpfFilterAdapter.
    /// Called during PowerNowOn after the radio and filter adapter are set up.
    /// </summary>
    private void WirePanDisplay()
    {
        if (RigControl?.FilterControl is not WpfFilterAdapter adapter) return;

        // Wire pan display callback — updates PanDisplayBox with braille text
        adapter.PanDisplayCallback = (line, pos) =>
        {
            if (_isClosing) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing) return;
                PanDisplayBox.Text = line;
                // Only snap the caret to current-freq position when the user
                // is NOT focused here — while focused, the user owns the caret
                // and pan refreshes must not move it out from under them.
                // Combined with the focus-transition guard in PanNavTimer_Tick,
                // this keeps the radio from drifting on passive pan updates.
                if (!PanDisplayBox.IsKeyboardFocused && pos >= 0 && pos < line.Length)
                    PanDisplayBox.SelectionStart = pos;

                // Respect user's Show-panadapter preference. When hidden, the callback
                // still refreshes the backing Text (harmless on a Collapsed control) so
                // re-enabling is instant — but we skip auto-showing the panel and
                // suppress the braille push so we don't spam a braille display the user
                // isn't looking at.
                bool showPan = CurrentAudioConfig?.ShowPanadapter ?? true;
                if (showPan && PanadapterPanel.Visibility != Visibility.Visible)
                    PanadapterPanel.Visibility = Visibility.Visible;

                // Send to braille display if available and panadapter is shown
                if (showPan && Radios.ScreenReaderOutput.HasBraille)
                    Radios.ScreenReaderOutput.Braille(line);
            });
        };

        // Wire segment display callback for low/high frequency labels
        if (adapter.PanManager != null)
        {
            adapter.PanManager.SegmentDisplayCallback = (lowText, highText) =>
            {
                if (_isClosing) return;
                Dispatcher.BeginInvoke(() =>
                {
                    PanLowFreq.Text = lowText;
                    PanHighFreq.Text = highText;
                });
            };
        }

        Tracing.TraceLine("WirePanDisplay: callbacks connected", TraceLevel.Info);
    }

    /// <summary>
    /// Timer for pan navigation — tunes the radio to the frequency under the
    /// cursor when the user moves the caret with Left/Right. Only fires a
    /// tune when the cursor has actually moved since focus entered — prevents
    /// Tab/Shift+Tab focus transitions from mutating radio state (the caret
    /// is sticky across focus changes and without this guard a focus event
    /// alone would make the radio jump to wherever the caret happened to be).
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _panNavTimer;
    private int _panNavLastCursorPos = -1;

    private void PanDisplayBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (RigControl?.FilterControl is not WpfFilterAdapter adapter) return;
        if (adapter.PanManager == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.PageUp:
            case Key.PageDown:
                adapter.PanManager.checkForRangeJump(
                    key == Key.PageUp ? (int)System.Windows.Forms.Keys.PageUp
                                      : (int)System.Windows.Forms.Keys.PageDown);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Right:
                // Allow normal cursor movement, then tune after a brief pause
                // The pan nav timer handles tuning to frequency under cursor
                break;
        }
    }

    private void PanDisplayBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Seed the move-detection baseline with the current caret position so
        // the first timer tick doesn't interpret "focus entered" as "user
        // moved cursor" — without this, Tab-in would fire gotoFreq on the
        // stale SelectionStart and jerk the radio to wherever the caret was
        // last left (sometimes hundreds of kHz from the actual slice freq).
        _panNavLastCursorPos = PanDisplayBox.SelectionStart;

        if (_panNavTimer == null)
        {
            _panNavTimer = new System.Windows.Threading.DispatcherTimer();
            _panNavTimer.Interval = TimeSpan.FromMilliseconds(200);
            _panNavTimer.Tick += PanNavTimer_Tick;
        }
        _panNavTimer.Start();
    }

    private void PanDisplayBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _panNavTimer?.Stop();
        _panNavLastCursorPos = -1;
    }

    private void PanNavTimer_Tick(object? sender, EventArgs e)
    {
        if (RigControl?.FilterControl is not WpfFilterAdapter adapter) return;
        if (adapter.PanManager?.CurrentPanData == null) return;

        int cursorPos = PanDisplayBox.SelectionStart;
        // Only tune when the cursor has actually moved. Focus alone should
        // never cause a frequency change — see field doc comment above.
        if (cursorPos == _panNavLastCursorPos) return;
        _panNavLastCursorPos = cursorPos;

        var panData = adapter.PanManager.CurrentPanData;
        if (cursorPos >= 0 && cursorPos < panData.frequencies.Length)
        {
            double freq = panData.frequencies[cursorPos];
            if (freq > 0)
                adapter.PanManager.gotoFreq(freq);
        }
    }

    // ── Helpers ──────────────────────────────────────

    /// <summary>
    /// Thread-safe button text update. Replaces Form1.setButtonText().
    /// </summary>
    private void SetButtonText(Button button, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetButtonText(button, text));
            return;
        }

        button.Content = text;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
    }

    /// <summary>
    /// Toggle transmit state. Routes through PTT safety controller when available.
    /// Replaces Form1.toggleTransmit().
    /// </summary>
    private void ToggleTransmit()
    {
        if (!_radioPowerOn || RigControl == null)
        {
            Tracing.TraceLine("toggleTransmit: no power", TraceLevel.Error);
            return;
        }

        if (_pttController != null)
        {
            // Route through PTT safety controller for timeout/warning tracking
            _pttController.ToggleLock();
        }
        else
        {
            // Fallback — direct TX toggle (no safety controller)
            Tracing.TraceLine($"toggling transmit: {RigControl.Transmit}", TraceLevel.Info);
            RigControl.Transmit = !RigControl.Transmit;
        }
    }

    /// <summary>
    /// Show the WPF MemoriesDialog with delegates wired to the current radio.
    /// Sprint 11 Phase 11.7: Replaces FlexMemories.ShowDialog() call in KeyCommands.
    /// </summary>
    private void ShowMemoriesDialog()
    {
        if (RigControl?.RigFields?.Memories == null) return;

        var memories = RigControl.RigFields.Memories;
        var dialog = new Dialogs.MemoriesDialog();

        // Wire sorted memory list
        dialog.GetSortedMemories = () =>
        {
            var items = new List<Dialogs.MemoriesDialog.MemoryDisplayItem>();
            foreach (var mem in memories.SortedMemories)
            {
                var m = mem.Value;
                string name = string.IsNullOrEmpty(m.Name) ? m.Freq.ToString("F6") : m.Name;
                string group = string.IsNullOrEmpty(m.Group) ? "" : m.Group + '.';
                items.Add(new Dialogs.MemoriesDialog.MemoryDisplayItem
                {
                    FullName = group + name,
                    MemoryRef = m
                });
            }
            return items;
        };

        // Wire select memory (tune to it)
        dialog.SelectMemory = (memRef) =>
        {
            if (memRef is Flex.Smoothlake.FlexLib.Memory mem)
            {
                mem.Select();
            }
        };

        // Wire per-memory property getters
        dialog.FormatFrequency = (memRef) =>
        {
            if (memRef is Flex.Smoothlake.FlexLib.Memory mem && OpenParms?.FormatFreq != null)
                return OpenParms.FormatFreq((ulong)(mem.Freq * 1e6));
            return "";
        };

        dialog.GetMode = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Mode ?? "" : "";

        dialog.GetName = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Name ?? "" : "";

        dialog.GetOwner = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Owner ?? "" : "";

        dialog.GetGroup = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Group ?? "" : "";

        dialog.GetFilterLow = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.RXFilterLow : 0;

        dialog.GetFilterHigh = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.RXFilterHigh : 0;

        // Mode list from rig caps
        dialog.ModeList = new List<string>(RigCaps.ModeTable);

        // Show the dialog (owner set by JJFlexDialog base class)
        dialog.ShowDialog();

        // If user selected a memory via Enter key, go home
        if (dialog.ShowFreq)
        {
            gotoHome();
        }
    }

    /// <summary>
    /// Show the WPF TXControlsDialog with delegates wired to the current radio.
    /// Sprint 33 Track J (#109): the Sprint 9 Track B dialog was complete, and
    /// FlexBase.ShowTXControlsDialog was declared and invoked from globals.vb,
    /// but nothing ever assigned it. The null-conditional call meant the TX
    /// Controls door did nothing at all — no window, no speech, nothing for a
    /// screen reader to report. Exactly the ShowRadioInfoDialog bug below.
    /// The WinForms original (Radios\TXControls.cs) was deleted when this
    /// replaced it, so this was the only remaining route to these settings.
    /// Wired in OnRadioStarted alongside the other two.
    /// </summary>
    private void ShowTXControlsDialog()
    {
        var rig = RigControl;
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.no_radio_sentence"),
                Radios.VerbosityLevel.Critical, true);
            return;
        }

        var dialog = new Dialogs.TXControlsDialog();

        // TX request inputs
        dialog.GetTXReqRCAEnabled = () => rig.TXReqRCAEnabled;
        dialog.SetTXReqRCAEnabled = v => rig.TXReqRCAEnabled = v;
        dialog.GetTXReqRCAPolarity = () => rig.TXReqRCAPolarity;
        dialog.SetTXReqRCAPolarity = v => rig.TXReqRCAPolarity = v;
        dialog.GetTXReqACCEnabled = () => rig.TXReqACCEnabled;
        dialog.SetTXReqACCEnabled = v => rig.TXReqACCEnabled = v;
        dialog.GetTXReqACCPolarity = () => rig.TXReqACCPolarity;
        dialog.SetTXReqACCPolarity = v => rig.TXReqACCPolarity = v;

        // TX outputs, enable plus delay
        dialog.GetTX1Enabled = () => rig.TX1Enabled;
        dialog.SetTX1Enabled = v => rig.TX1Enabled = v;
        dialog.GetTX1Delay = () => rig.TX1Delay;
        dialog.SetTX1Delay = v => rig.TX1Delay = v;

        dialog.GetTX2Enabled = () => rig.TX2Enabled;
        dialog.SetTX2Enabled = v => rig.TX2Enabled = v;
        dialog.GetTX2Delay = () => rig.TX2Delay;
        dialog.SetTX2Delay = v => rig.TX2Delay = v;

        dialog.GetTX3Enabled = () => rig.TX3Enabled;
        dialog.SetTX3Enabled = v => rig.TX3Enabled = v;
        dialog.GetTX3Delay = () => rig.TX3Delay;
        dialog.SetTX3Delay = v => rig.TX3Delay = v;

        dialog.GetTXACCEnabled = () => rig.TXACCEnabled;
        dialog.SetTXACCEnabled = v => rig.TXACCEnabled = v;
        dialog.GetTXACCDelay = () => rig.TXACCDelay;
        dialog.SetTXACCDelay = v => rig.TXACCDelay = v;

        // Hardware ALC and remote-on
        dialog.GetHWAlcEnabled = () => rig.HWAlcEnabled;
        dialog.SetHWAlcEnabled = v => rig.HWAlcEnabled = v;
        dialog.GetRemoteOnEnabled = () => rig.RemoteOnEnabled;
        dialog.SetRemoteOnEnabled = v => rig.RemoteOnEnabled = v;

        dialog.ShowDialog();
    }

    /// <summary>
    /// Show the WPF RadioInfoDialog with callbacks wired to the current radio.
    /// QB Track L (2026-08-07): the Sprint 11 dialog existed but
    /// FlexBase.ShowRadioInfoDialog was never assigned app-side, so the
    /// Operations menu's Feature Availability door silently did nothing.
    /// Wired in OnRadioStarted alongside ShowMemoriesDialog.
    /// </summary>
    private void ShowRadioInfoDialog(int tabIndex)
    {
        var rig = RigControl;
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.no_radio_sentence"),
                Radios.VerbosityLevel.Critical, true);
            return;
        }

        var callbacks = new Dialogs.RadioInfoCallbacks
        {
            GetModel = () => rig.RadioModel,
            GetVersion = () => rig.RadioFirmwareVersion,
            GetSerial = () => rig.ConnectedSerial ?? "",
            GetCallsign = () => rig.RadioCallsign,
            SetCallsign = call => rig.RadioCallsign = call,
            GetNickname = () => rig.RadioNickname,
            // RenameRadio is the full rename path — persists radio-side and
            // refreshes the roster/auto-connect display names.
            SetNickname = name => rig.RenameRadio(name),
            GetIPAddress = () => rig.CurrentRadioIP?.ToString() ?? "",
            GetDisplayModes = () =>
            {
                var items = new List<Dialogs.DisplayModeItem>();
                foreach (var name in rig.FrontPanelDisplayModes)
                    items.Add(new Dialogs.DisplayModeItem { DisplayText = name, Value = name });
                return items;
            },
            GetCurrentDisplayMode = () => rig.FrontPanelDisplayMode,
            SetDisplayMode = mode => rig.FrontPanelDisplayMode = mode?.ToString() ?? "",
            GetFeatureAvailabilityText = () => rig.BuildFeatureAvailabilityText(),
            RefreshLicense = () => rig.RefreshLicenseState(),
        };

        var tab = tabIndex == (int)Dialogs.RadioInfoTab.FeatureAvailability
            ? Dialogs.RadioInfoTab.FeatureAvailability
            : Dialogs.RadioInfoTab.General;
        var dialog = new Dialogs.RadioInfoDialog(callbacks, tab);
        dialog.ShowDialog();
    }

    /// <summary>
    /// The coarse-and-fine step picker (#302), opened with S from the
    /// Frequency field in Modern tuning.
    /// </summary>
    /// <remarks>
    /// Applies through <c>FreqOutHandlers.ApplyStepSizes</c>, the one place
    /// the two step sizes change, and speaks both on the way out so the
    /// operator hears the result without pressing Shift+S to check.
    /// </remarks>
    public void ShowTuningStepsDialog()
    {
        if (_freqOutHandlers == null) return;

        var dialog = new Dialogs.TuningStepsDialog(
            _freqOutHandlers.CoarseTuneStep, _freqOutHandlers.FineTuneStep);

        if (dialog.ShowDialog() == true)
            _freqOutHandlers.ApplyStepSizes(dialog.CoarseStepHz, dialog.FineStepHz, speak: true);
    }

    #endregion

    #region Radio Control Button Handlers — Phase 8.2

    /// <summary>
    /// Antenna Tune button — toggles FlexTunerOn.
    /// </summary>
    private void AntennaTuneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_radioPowerOn || RigControl == null)
        {
            Tracing.TraceLine("antennaTune: no power", TraceLevel.Error);
            return;
        }
        _oldSwr = "";
        RigControl.FlexTunerOn = !RigControl.FlexTunerOn;
    }

    /// <summary>
    /// Show SWR/tuner status on hover.
    /// </summary>
    private void AntennaTuneButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_radioPowerOn)
        {
            Tracing.TraceLine("antennaTune: no power", TraceLevel.Error);
            return;
        }
        SetButtonText(AntennaTuneButton, AntennaTuneButtonText);
    }

    private void AntennaTuneButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_radioPowerOn) return;
        SetButtonText(AntennaTuneButton, AntennaTuneButtonText);
    }

    /// <summary>
    /// Tune toggle button — toggles TX tune carrier (Ctrl+Shift+T).
    /// </summary>
    private void TuneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_radioPowerOn || RigControl == null) return;
        ToggleTuneCarrier();
    }

    /// <summary>
    /// Toggle the tune carrier on/off with audio feedback.
    /// Called from both the UI button and the Ctrl+Shift+T hotkey.
    /// Guards against key auto-repeat producing rapid on/off/on/off chirps.
    /// </summary>
    private long _lastTuneToggleTicks;
    public void ToggleTuneCarrier()
    {
        if (RigControl == null) return;

        // Debounce: ignore calls within 500ms (key auto-repeat protection)
        long now = Environment.TickCount64;
        if (now - _lastTuneToggleTicks < 500) return;
        _lastTuneToggleTicks = now;

        bool newState = !RigControl.TxTune;
        // Capture SWR while TX is still active. Must happen BEFORE TxTune = false,
        // because the meter snaps to ~1.0 the instant forward power drops to zero —
        // reading any time after this line gives the idle rest value, not the
        // final measured SWR (Don's "SWR 1.0 to 1 every time" bug).
        float capturedSwr = newState ? 0f : RigControl.SWRValue;
        RigControl.TxTune = newState;
        TuneToggleButton.IsChecked = newState;
        if (newState)
        {
            EarconPlayer.TuneOnTone();
            MeterToneEngine.OnTuneStarted();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tune.tune_on"), VerbosityLevel.Terse, true);
        }
        else
        {
            // Stop ATU progress earcon in case auto-ATU started a tune cycle
            EarconPlayer.StopATUProgressEarcon();
            StopATUTimeout();
            EarconPlayer.TuneOffTone();
            MeterToneEngine.OnTuneStopped();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tune.tune_off"), VerbosityLevel.Terse, true);
            // TXTune falling edge raises no FlexAntTuneStartStop event, so the
            // ATU-status-driven announce path never fires for manual carrier.
            // Call directly here with the captured SWR so external/manual tuners
            // (Don's rooftop unit) get the real final match, not the idle 1.0.
            AnnounceCapturedSwrAfterTune(isFailure: false, capturedSwr);
        }
    }

    /// <summary>
    /// Show or hide the meters panel. Called from Ctrl+M.
    /// </summary>
    /// <remarks>
    /// This used to show the panel AND switch meter tones on or off in one
    /// action (#126), which meant an operator who wanted to look at the
    /// settings started a noise, and an operator who wanted the noise off had
    /// to open a panel. They are separate now: this key is the panel, and the
    /// tone switch is Ctrl+J then T. Nothing in here changes audio state.
    /// </remarks>
    public void ToggleMetersPanel()
    {
        MetersPanel.TogglePanelVisibility();
    }

    /// <summary>
    /// Toggle a ScreenFields expander category by index.
    /// Called from KeyCommands.vb after Sprint 23 hotkey unification.
    /// 0=DSP, 1=Audio, 2=Receiver, 3=Transmission, 4=Antenna
    /// </summary>
    public void ToggleScreenFieldsCategory(int categoryIndex)
    {
        FieldsPanel.ToggleCategory(categoryIndex);
    }

    /// <summary>
    /// Start ATU tune cycle with audio feedback.
    /// Called from both the menu and the Ctrl+T hotkey.
    /// </summary>
    public void StartATUTuneCycle()
    {
        if (RigControl == null) return;
        if (!RigControl.MyCaps.HasCap(Radios.RigCaps.Caps.ATGet))
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tune.no_tuner"), VerbosityLevel.Terse);
            return;
        }
        // ATU tune uses FlexTunerOn which handles auto/manual tuner logic
        _oldSwr = "";
        RigControl.FlexTunerOn = true;
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tune.atu_tuning"), VerbosityLevel.Terse, true);
    }

    private void StartATUTimeout()
    {
        StopATUTimeout();
        _atuTuneTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _atuTuneTimer.Tick += (s, e) =>
        {
            StopATUTimeout();
            EarconPlayer.StopATUProgressEarcon();
            EarconPlayer.ATUFailTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tune.atu_timed_out"), VerbosityLevel.Critical, true);
        };
        _atuTuneTimer.Start();
    }

    private void StopATUTimeout()
    {
        _atuTuneTimer?.Stop();
        _atuTuneTimer = null;
    }

    /// <summary>
    /// Transmit button — toggles PTT.
    /// </summary>
    private void TransmitButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTransmit();
    }

    /// <summary>
    /// Show the action toolbar popup (Ctrl+Tab). Lightweight ListBox with common TX actions.
    /// Arrow keys navigate, Enter activates, Escape closes.
    /// </summary>
    private void ShowActionToolbar()
    {
        if (RigControl == null || !_radioPowerOn)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.no_radio"),
                VerbosityLevel.Critical, true);
            return;
        }

        var dlg = new JJFlexDialog
        {
            Title = Radios.Lexicon.Get("connect.home.actions_title"),
            Width = 250,
            Height = 200,
            ShowInTaskbar = false,
            ResizeMode = System.Windows.ResizeMode.NoResize
        };

        var list = new System.Windows.Controls.ListBox
        {
            Margin = new System.Windows.Thickness(8),
        };
        System.Windows.Automation.AutomationProperties.SetName(
            list, Radios.Lexicon.Get("connect.home.actions_list_name"));

        // The action ITEMS below stay literal on purpose. ExecuteActionToolbarItem
        // switches on the very same strings, so extracting them would let an
        // operator's overlay silently disconnect every entry from its action.
        // Build action items based on radio state
        bool hasATU = RigControl.HasATU;
        bool canTx = RigControl.CanTransmit;
        bool isTx = RigControl.Transmit;

        if (hasATU)
            list.Items.Add("ATU Tune");
        if (canTx)
        {
            bool tuning = RigControl.TxTune;
            list.Items.Add(tuning ? "Stop Tune Carrier" : "Start Tune Carrier");
            list.Items.Add(isTx ? "Stop Transmit" : "Start Transmit");
        }
        list.Items.Add("Speak Status");
        list.Items.Add("Cancel");

        list.SelectedIndex = 0;
        dlg.Content = list;

        list.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is string item)
            {
                dlg.Close();
                ExecuteActionToolbarItem(item);
                e.Handled = true;
            }
        };

        dlg.Loaded += (s, e) => list.Focus();
        dlg.ShowDialog();
    }

    private void ExecuteActionToolbarItem(string item)
    {
        if (RigControl == null) return;

        switch (item)
        {
            case "ATU Tune":
                RigControl.FlexTunerOn = !RigControl.FlexTunerOn;
                break;
            case "Start Tune Carrier":
            case "Stop Tune Carrier":
                ToggleTuneCarrier();
                break;
            case "Start Transmit":
            case "Stop Transmit":
                ToggleTransmit();
                break;
            case "Speak Status":
                SpeakStatusCallback?.Invoke();
                break;
        }
    }

    #endregion

    #region Text Area Support — Phase 8.3

    /// <summary>
    /// Window IDs matching Form1.WindowIDs enum for text routing.
    /// </summary>
    public enum WindowIDs
    {
        ReceiveDataOut,
        SendDataOut
    }

    /// <summary>
    /// Map a WindowID to its TextBox control.
    /// Matches Form1.TBIDToTB().
    /// </summary>
    private TextBox WindowIdToTextBox(WindowIDs id)
    {
        return id switch
        {
            WindowIDs.ReceiveDataOut => ReceivedTextBox,
            WindowIDs.SendDataOut => SentTextBox,
            _ => ReceivedTextBox
        };
    }

    /// <summary>
    /// Write text to a text area. Thread-safe via Dispatcher.
    /// Matches Form1.WriteText/WriteTextX pattern.
    /// </summary>
    /// <param name="id">Which text area to write to</param>
    /// <param name="text">Text to write or append</param>
    /// <param name="cursor">Cursor position:
    ///   -1 = preserve current position,
    ///    0 = move to end,
    ///   &gt;0 = set to specific position,
    ///   &lt;-1 = buffer limit (negative of max length, trims from start)</param>
    /// <param name="clearFirst">True to replace all text, false to append</param>
    public void WriteText(WindowIDs id, string text, int cursor = 0, bool clearFirst = false)
    {
        if (_isClosing)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => WriteText(id, text, cursor, clearFirst));
            return;
        }

        try
        {
            var tb = WindowIdToTextBox(id);
            WriteToTextBox(tb, text, cursor, clearFirst);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"WriteText error: {ex.Message}", System.Diagnostics.TraceLevel.Error);
        }
    }

    /// <summary>
    /// Core text writing logic — matches Form1.toTextbox().
    /// Handles append, replace, cursor positioning, and buffer limiting.
    /// </summary>
    private static void WriteToTextBox(TextBox tb, string text, int cursor, bool clearFirst)
    {
        string finalText;
        if (clearFirst)
        {
            finalText = text;
        }
        else
        {
            finalText = tb.Text + text; // Append
        }

        // Handle cursor positioning
        if (cursor == -1)
        {
            // Preserve current position
            cursor = tb.SelectionStart;
        }
        else if (cursor < -1)
        {
            // Buffer limit: negative value = max length
            int maxLen = -cursor;
            if (finalText.Length > maxLen)
            {
                finalText = finalText.Substring(finalText.Length - maxLen);
            }
            cursor = finalText.Length;
        }
        else if (cursor == 0)
        {
            // Move to end
            cursor = finalText.Length;
        }

        tb.Text = finalText;

        // Set cursor and scroll into view
        if (cursor <= tb.Text.Length)
        {
            tb.SelectionStart = cursor;
            tb.SelectionLength = 0;
        }

        // Scroll to cursor (WPF equivalent of ScrollToCaret)
        tb.ScrollToLine(tb.GetLineIndexFromCharacterIndex(
            Math.Min(tb.SelectionStart, Math.Max(0, tb.Text.Length - 1))));
    }

    // Sprint 28 Phase 3.6 (2026-04-21) — text-area visibility combines two
    // independent constraints: (1) the UI mode (hidden in Logging mode, visible
    // in Classic/Modern), and (2) the radio mode (only useful in CW). The CW
    // boxes aren't used for anything else currently — no CAT commands, no
    // digital-mode text, just CW send/receive. Showing them outside CW adds
    // tab-order clutter without user benefit. Sprint 27 intended this fix but
    // the implementation was missed; Sprint 28 adds it.
    private bool _uiModeWantsTextAreas = true;

    /// <summary>
    /// Set the UI-mode intent for text-area visibility. Classic/Modern set true,
    /// Logging sets false. Actual visibility is the AND of this intent AND the
    /// current radio mode being CW.
    /// </summary>
    public void SetTextAreasVisible(bool visible)
    {
        _uiModeWantsTextAreas = visible;
        UpdateTextAreasVisibility();
    }

    /// <summary>
    /// Sprint 28 Phase 3.6 — apply combined visibility: UI-mode intent AND
    /// radio-mode-is-CW. Called on SetTextAreasVisible (UI mode change) and on
    /// the rig's ModeChanged event (radio mode change).
    /// </summary>
    private void UpdateTextAreasVisibility()
    {
        bool isCwMode = IsCurrentModeCw();
        bool actuallyVisible = _uiModeWantsTextAreas && isCwMode;

        var vis = actuallyVisible ? Visibility.Visible : Visibility.Collapsed;
        ReceiveLabel.Visibility = vis;
        ReceivedTextBox.Visibility = vis;
        SendLabel.Visibility = vis;
        SentTextBox.Visibility = vis;
        // Tab stop matches visibility so hidden boxes are excluded from tab order.
        ReceivedTextBox.IsTabStop = actuallyVisible;
        SentTextBox.IsTabStop = actuallyVisible;
    }

    /// <summary>Sprint 28 Phase 3.6 — true if the current radio mode is any CW
    /// variant. Matches ScreenFieldsPanel's existing isCW check for APF
    /// visibility (line 809) for consistency.</summary>
    private bool IsCurrentModeCw()
    {
        var mode = RigControl?.Mode?.ToUpperInvariant() ?? "";
        return mode == "CW" || mode == "CWL" || mode == "CWU";
    }

    /// <summary>Sprint 28 Phase 3.6 — handler for rig mode changes. Re-evaluates
    /// text-area visibility so the CW boxes show only when the radio is actually
    /// in CW. Marshals to the UI thread since ModeChanged may fire from any
    /// thread.</summary>
    private void OnRadioModeChanged(string newMode)
    {
        Dispatcher.BeginInvoke(new Action(UpdateTextAreasVisibility));
    }

    #endregion

    #region Text Area Event Handlers — Phase 8.3

    /// <summary>
    /// ReceivedTextBox keyboard handler — forwards function keys and modified keys.
    /// Clipboard operations (Ctrl+C, Ctrl+X) handled naturally by WPF TextBox.
    /// </summary>
    private void ReceivedTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys to keyboard routing (Phase 8.6)
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            // Phase 8.6: Commands.DoCommand(e)
            e.Handled = true;
        }
    }

    /// <summary>
    /// SentTextBox keyboard handler — handles CW shortcuts and function keys.
    /// Matches Form1.SentTextBox_KeyDown.
    /// </summary>
    private void SentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys to keyboard routing (Phase 8.6)
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            // Phase 8.6: Commands.DoCommand(e)
            e.Handled = true;
        }

        // Phase 8.4+: Ctrl+Enter sends CW, other shortcuts
    }

    /// <summary>
    /// SentTextBox text input handler — for CW character transmission.
    /// Matches Form1.SentTextBox_KeyPress for direct CW send.
    /// </summary>
    private void SentTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Phase 8.4+: Route typed characters to CW transmit
    }

    #endregion

    #region Frequency Readback — Ctrl+Shift+F

    /// <summary>
    /// Speak the current frequency and active slice.
    /// Global hotkey — works in Classic, Modern, and Logging modes.
    /// </summary>
    public void SpeakFrequency()
    {
        if (RigControl == null || !_radioPowerOn || OpenParms?.FormatFreq == null)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.no_radio"),
                VerbosityLevel.Critical, true);
            return;
        }

        try
        {
            ulong freq = RigControl.Transmit
                ? RigControl.TXFrequency
                : RigControl.VirtualRXFrequency;
            string freqText = OpenParms.FormatFreq(freq);
            int slice = RigControl.RXVFO;
            string speech = Radios.Lexicon.Get("connect.home.frequency",
                ("freqText", freqText), ("slice", RigControl.VFOToLetter(slice)));

            // In Modern mode, append both step sizes — tuning unity removed
            // the "current mode" concept, so we report what Up and Shift+Up
            // each do.
            if (ActiveUIMode == UIMode.Modern && _freqOutHandlers != null)
            {
                speech += Radios.Lexicon.Get("connect.home.frequency_steps",
                    ("coarse", FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.CoarseTuneStep)),
                    ("fine", FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.FineTuneStep)));
            }

            Radios.ScreenReaderOutput.Speak(speech, VerbosityLevel.Terse, true);
        }
        catch
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.frequency_unavailable"), VerbosityLevel.Critical, true);
        }
    }

    /// <summary>
    /// Jump to a specific band. Delegates to FreqOutHandlers.BandJump().
    /// Called from KeyCommands band F-key handlers.
    /// </summary>
    public void BandJump(HamBands.Bands.BandNames band)
    {
        _freqOutHandlers?.BandJump(band);
    }

    /// <summary>
    /// Navigate to next (+1) or previous (-1) band.
    /// Called from KeyCommands BandUp/BandDown handlers.
    /// </summary>
    public void BandNavigate(int direction)
    {
        _freqOutHandlers?.BandNavigate(direction);
    }

    #region 60m Channel Navigation — Sprint 22 Phase 10

    private int _sixtyMeterChannelIndex;

    /// <summary>
    /// Navigate 60m channels: cycles through Channel 1-5 + Digi Segment.
    /// Alt+Shift+Up/Down parallels Alt+Up/Down for band navigation.
    /// </summary>
    public void SixtyMeterChannelNavigate(int direction)
    {
        if (RigControl == null || !_radioPowerOn) return;

        string country = _freqOutHandlers?.License?.Country ?? "US";
        var alloc = SixtyMeterChannels.GetAllocation(country);
        if (alloc == null)
        {
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.no_60m_channels"), VerbosityLevel.Terse);
            return;
        }

        int stopCount = alloc.Value.Channels.Length + (alloc.Value.Digi != null ? 1 : 0);
        if (stopCount == 0) return;

        _sixtyMeterChannelIndex = (_sixtyMeterChannelIndex + direction + stopCount) % stopCount;

        if (_sixtyMeterChannelIndex < alloc.Value.Channels.Length)
        {
            // Channelized frequency
            var ch = alloc.Value.Channels[_sixtyMeterChannelIndex];
            ulong freqHz = (ulong)(ch.FrequencyMHz * 1_000_000.0 + 0.5);
            RigControl.Frequency = freqHz;
            RigControl.Mode = ch.Mode;
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.sixty_meter_channel",
                ("label", ch.Label), ("freq", $"{ch.FrequencyMHz:F4}"), ("mode", ch.Mode)),
                VerbosityLevel.Terse);
        }
        else if (alloc.Value.Digi is { } digi)
        {
            // Digital segment — tune to start
            ulong freqHz = (ulong)(digi.StartMHz * 1_000_000.0 + 0.5);
            RigControl.Frequency = freqHz;
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.sixty_meter_digital",
                ("freq", $"{digi.StartMHz:F4}")), VerbosityLevel.Terse);
        }
    }

    #endregion

    /// <summary>
    /// Common mode cycle list for F10/F11 hotkeys.
    /// Subset of RigCaps.ModeTable — just the frequently used modes.
    /// </summary>
    private static readonly string[] CommonModes = { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM" };

    /// <summary>
    /// Cycle to the next (+1) or previous (-1) mode in the common mode list.
    /// Called from KeyCommands mode handlers (F10/F11).
    /// </summary>
    public void CycleMode(int direction)
    {
        if (RigControl == null || !_radioPowerOn) return;

        string currentMode = RigControl.Mode ?? "USB";
        int idx = Array.IndexOf(CommonModes, currentMode);

        if (idx < 0)
        {
            // Current mode not in common list — jump to first or last
            idx = direction > 0 ? CommonModes.Length - 1 : 0;
        }

        int next = (idx + direction + CommonModes.Length) % CommonModes.Length;
        string newMode = CommonModes[next];
        RigControl.Mode = newMode;
        Radios.ScreenReaderOutput.Speak(newMode, VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Jump directly to a specific mode.
    /// Called from KeyCommands direct mode handlers (Alt+U, Alt+L, Alt+C).
    /// </summary>
    public void SetMode(string mode)
    {
        if (RigControl == null || !_radioPowerOn) return;

        string currentMode = RigControl.Mode ?? "";
        if (string.Equals(currentMode, mode, StringComparison.OrdinalIgnoreCase))
        {
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.home.already_in_mode", ("mode", mode)),
                VerbosityLevel.Terse, true);
            return;
        }
        RigControl.Mode = mode;
        Radios.ScreenReaderOutput.Speak(mode, VerbosityLevel.Terse, true);
    }

    #endregion

    #region SmartLink & Auto-Connect Management

    /// <summary>
    /// Show the SmartLink Account Manager dialog.
    /// Works without a radio connection — manages saved accounts (view, rename, delete).
    /// </summary>
    public void ShowSmartLinkAccountManager()
    {
        // The SHARED manager, never a fresh instance: a private copy here is
        // how Reset Sign-In got silently undone on 2026-08-06 — the rig's
        // in-memory tokens survived the on-disk clear and re-saved themselves.
        var mgr = Radios.FlexBase.SharedAccountManager;

        while (true)
        {
            var defaultEmail = GetDefaultSmartLinkEmail?.Invoke() ?? "";
            var callbacks = new Dialogs.SmartLinkAccountCallbacks
            {
                // Default first, then friendly name — an order the operator
                // chose. Ordering by LastUsed put another operator's account
                // on top of the very screen showing Noel's as default
                // (2026-08-10): two answers, inches apart. LastUsed is still
                // displayed per account; it just no longer drives position.
                GetAccounts = () => mgr.Accounts
                    .OrderByDescending(a => a.Email.Equals(defaultEmail, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(a => a.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(a => new Dialogs.SmartLinkAccountInfo
                    {
                        FriendlyName = a.FriendlyName,
                        Email = a.Email,
                        LastUsed = a.LastUsed,
                        AccountData = a,
                        IsDefault = a.Email.Equals(defaultEmail, StringComparison.OrdinalIgnoreCase),
                        AutoStartRemote = a.AutoStartRemote
                    }).ToList(),
                RenameAccount = (oldName, newName) => mgr.RenameAccount(oldName, newName),
                DeleteAccount = (name) => { mgr.DeleteAccount(name); },
                ResetAccountSignIn = (name) => mgr.ResetAccountSignIn(name),
                // Start Fresh goes through the SAME shared manager as Reset
                // Sign-In — a private manager instance is how Reset Sign-In
                // got silently undone on 2026-08-06 (the rig's in-memory
                // tokens re-saved themselves over the on-disk clear).
                StartFreshAllAccounts = () => mgr.ResetAllSignIns(),
                SetAutoStartRemote = (name, enabled) =>
                {
                    var acct = mgr.Accounts.FirstOrDefault(a =>
                        a.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (acct == null) return;
                    acct.AutoStartRemote = enabled;
                    mgr.SaveAccounts();
                },
                ScreenReaderSpeak = (msg, interrupt) => Radios.ScreenReaderOutput.Speak(msg, interrupt)
            };

            var dialog = new Dialogs.SmartLinkAccountDialog(callbacks);
            var result = dialog.ShowDialog();

            if (result != true)
                break;

            if (dialog.NewLoginRequested)
            {
                var signedIn = RunNativeSignInFlow(mgr, prefillEmail: "");
                if (signedIn != null)
                    PropagateMidSessionSignIn(signedIn);
                continue;
            }

            if (dialog.CreateAccountRequested)
            {
                // Native signup — the hosted page's signup link half-works
                // (creates the account, then fails its redirect and reports
                // failure), so JJ Flex owns the whole journey: create, then
                // flow straight into sign-in with the new email prefilled.
                var signup = new Dialogs.SmartLinkSignUpDialog(mgr);
                if (signup.ShowDialog() == true && !string.IsNullOrEmpty(signup.SignedUpEmail))
                {
                    var signedIn = RunNativeSignInFlow(mgr, signup.SignedUpEmail);
                    if (signedIn != null)
                        PropagateMidSessionSignIn(signedIn);
                }
                continue;
            }

            // Use Now: session-only override, saved default untouched.
            if (dialog.UseOnceRequested && dialog.SelectedAccountData is Radios.SmartLinkAccount useAcct)
            {
                Tracing.TraceLine($"ShowSmartLinkAccountManager: use-now for {useAcct.Email} (default unchanged)", TraceLevel.Info);
                SetSessionSmartLinkAccount?.Invoke(useAcct.Email);
                System.Threading.Tasks.Task.Delay(200).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        Radios.ScreenReaderOutput.Speak(
                            Radios.Lexicon.Get("connect.smartlink.using_for_session",
                                ("account", useAcct.FriendlyName)),
                            VerbosityLevel.Critical, true);
                    });
                });
                break;
            }

            // User selected an existing account — save as default
            Tracing.TraceLine($"ShowSmartLinkAccountManager: result={result}, SelectedAccountData={dialog.SelectedAccountData?.GetType()?.Name ?? "null"}, NewLogin={dialog.NewLoginRequested}", TraceLevel.Info);
            if (dialog.SelectedAccountData is Radios.SmartLinkAccount selectedAcct)
            {
                SaveDefaultSmartLinkAccount?.Invoke(selectedAcct.Email);
                // Speech gets swallowed by the focus changes that follow the
                // dialog closing, so this is deliberately delayed rather than
                // spoken inline. (It never "used Tolk directly" as the previous
                // comment claimed — it has always gone through
                // ScreenReaderOutput; the delay is the whole mechanism.)
                System.Threading.Tasks.Task.Delay(200).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        Radios.ScreenReaderOutput.Speak(
                            Radios.Lexicon.Get("connect.smartlink.default_account_set",
                                ("account", selectedAcct.FriendlyName)),
                            VerbosityLevel.Critical, true);
                    });
                });
            }
            break;
        }
    }

    /// <summary>
    /// The native-first sign-in flow shared by New Login and Create Account
    /// (2026-08-06): the native password form leads; the WebView2 browser page
    /// survives only as the MFA / Use-Browser-Instead fallback. Returns the
    /// signed-in account (saved when Remember was checked, unsaved otherwise),
    /// or null when the user backed out.
    /// </summary>
    private Radios.SmartLinkAccount? RunNativeSignInFlow(Radios.SmartLinkAccountManager mgr, string prefillEmail)
    {
        using (var native = new Radios.SmartLinkLoginForm(mgr, prefillEmail))
        {
            var nativeResult = native.ShowDialog();
            if (nativeResult == System.Windows.Forms.DialogResult.OK
                && !string.IsNullOrEmpty(native.IdToken))
            {
                var nativeFriendly =
                    !string.IsNullOrEmpty(native.FriendlyName) ? native.FriendlyName :
                    !string.IsNullOrEmpty(native.Email) ? native.Email :
                    "SmartLink Account";
                var account = new Radios.SmartLinkAccount
                {
                    FriendlyName = nativeFriendly,
                    Email = native.Email,
                    IdToken = native.IdToken,
                    RefreshToken = native.RefreshToken,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(native.ExpiresIn),
                    LastUsed = DateTime.UtcNow
                };

                if (!native.RememberSignIn)
                {
                    // Adding an account to the SAVED list while asking not to
                    // remember it is a contradiction — honor the checkbox and
                    // explain. The sign-in itself still counts for this session.
                    Radios.ScreenReaderOutput.Speak(
                        Radios.Lexicon.Get("connect.smartlink.signed_in_not_remembered"),
                        VerbosityLevel.Terse, true);
                    return account;
                }

                mgr.SaveAccount(account);
                Radios.ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("connect.smartlink.account_saved", ("account", nativeFriendly)),
                    VerbosityLevel.Terse, true);
                return account;
            }
            if (nativeResult != System.Windows.Forms.DialogResult.Retry)
            {
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.smartlink.sign_in_cancelled"), VerbosityLevel.Terse, true);
                return null;
            }
        }

        // Fallback: Auth0 PKCE flow via WPF AuthDialog (browser)
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.smartlink.opening_login"), VerbosityLevel.Terse, true);
        var authDialog = new Dialogs.AuthDialog(
            trace: (msg, level) => JJTrace.Tracing.TraceLine(msg, (System.Diagnostics.TraceLevel)level),
            screenReaderSpeak: (msg, interrupt) => Radios.ScreenReaderOutput.Speak(msg, interrupt));
        authDialog.ForceNewLogin = true;

        if (authDialog.ShowDialog() == true && !string.IsNullOrEmpty(authDialog.IdToken))
        {
            var friendlyName = !string.IsNullOrEmpty(authDialog.Email)
                ? authDialog.Email
                : Radios.Lexicon.Get("connect.smartlink.default_friendly_name");

            var newAccount = new Radios.SmartLinkAccount
            {
                FriendlyName = friendlyName,
                Email = authDialog.Email,
                IdToken = authDialog.IdToken,
                RefreshToken = authDialog.RefreshToken,
                ExpiresAt = DateTime.UtcNow.AddSeconds(authDialog.ExpiresIn),
                LastUsed = DateTime.UtcNow
            };

            mgr.SaveAccount(newAccount);
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.smartlink.account_saved", ("account", friendlyName)),
                VerbosityLevel.Terse, true);
            return newAccount;
        }

        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.smartlink.login_cancelled"), VerbosityLevel.Terse, true);
        return null;
    }

    /// <summary>
    /// Mid-session sign-in propagation (found live 2026-08-04): a New Login
    /// while a radio is connected used to change nothing — FlexBase only loads
    /// its account during connect, so the Register button stayed grayed with
    /// "no account signed in" and the only recourse was restarting the app.
    /// Load the fresh account into the live rig, clear the per-run
    /// already-suggested guard for this radio, and re-run the registration
    /// suggestion so the advisory chain reflects the new reality.
    /// </summary>
    private void PropagateMidSessionSignIn(Radios.SmartLinkAccount account)
    {
        var rig = RigControl;
        if (rig == null || !rig.IsConnected) return;

        if (!rig.AdoptSignedInAccount(account))
        {
            Tracing.TraceLine($"PropagateMidSessionSignIn: adopt failed for {account.Email}", TraceLevel.Warning);
            return;
        }
        Tracing.TraceLine($"PropagateMidSessionSignIn: live rig now using {account.Email}", TraceLevel.Info);

        // Signing in changes the answer to "is this radio registered to the
        // signed-in account" — let both advisory guards re-ask.
        string serial = rig.SelectedRadioSerial ?? string.Empty;
        if (serial.Length > 0) _registrationSuggestedSerials.Remove(serial);
        _smartLinkSetupSuggested = false;

        _ = SuggestRegistrationIfUnregisteredAsync();
    }

    /// <summary>Show the MultiFlex client management dialog.</summary>
    public void ShowMultiFlexDialog()
    {
        if (RigControl == null || !_radioPowerOn)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.multiflex_needs_radio"), VerbosityLevel.Critical, true);
            return;
        }

        var rig = RigControl;

        var callbacks = new Dialogs.MultiFlexCallbacks
        {
            GetClients = () =>
            {
                return rig.GetGuiClients().Select(gc => new Dialogs.MultiFlexClientInfo
                {
                    Program = gc.program,
                    Station = gc.station,
                    Handle = gc.handle,
                    IsThisClient = gc.isThisClient,
                    OwnedSlices = gc.slices
                }).ToList();
            },
            DisconnectClient = (handle) => rig.DisconnectGuiClient(handle),
            SubscribeClientListChanged = h => rig.GuiClientChanged += h,
            UnsubscribeClientListChanged = h => rig.GuiClientChanged -= h
        };

        var dialog = new Dialogs.MultiFlexDialog(callbacks);
        dialog.ShowDialog();
    }

    // --- Auto-Connect callbacks (wired from ApplicationEvents.vb) ---

    /// <summary>Returns whether auto-connect is globally enabled.</summary>
    public Func<bool>? IsAutoConnectEnabled { get; set; }

    /// <summary>Returns the configured auto-connect radio name, or null if none.</summary>
    public Func<string?>? GetAutoConnectRadioName { get; set; }

    /// <summary>Sets the global auto-connect enabled flag and saves.</summary>
    public Action<bool>? SetAutoConnectEnabled { get; set; }

    /// <summary>Clears the auto-connect radio config and saves.</summary>
    public Action? ClearAutoConnectRadio { get; set; }

    /// <summary>
    /// Toggle the global auto-connect enabled flag.
    /// Returns speech message for caller to announce after menu closes.
    /// </summary>
    public string? ToggleAutoConnect()
    {
        if (IsAutoConnectEnabled == null || SetAutoConnectEnabled == null) return null;

        bool newState = !IsAutoConnectEnabled();
        SetAutoConnectEnabled(newState);
        // Sprint 32 Track E, #128. Both roads (menu item and the Radio menu
        // entry) return through here, so the tone lands whichever was used.
        EarconPlayer.ToggleTone(newState);
        return newState ? "Auto-connect enabled" : "Auto-connect disabled";
    }

    /// <summary>
    /// Clear the auto-connect radio configuration.
    /// Returns speech message for caller to announce after menu closes.
    /// </summary>
    public string? ClearAutoConnect()
    {
        if (ClearAutoConnectRadio == null) return null;

        string? radioName = GetAutoConnectRadioName?.Invoke();
        if (string.IsNullOrEmpty(radioName))
        {
            return "No auto-connect radio configured";
        }

        ClearAutoConnectRadio();
        return $"Auto-connect to {radioName} cleared";
    }

    #endregion

    #region Form1 Compatibility — Phase 9.1

    /// <summary>
    /// Display the key commands help dialog.
    /// Matches Form1.DisplayHelp().
    /// </summary>
    public void DisplayHelp()
    {
        // Context-sensitive help: check what has focus and show help dialog
        if (FreqOut.IsKeyboardFocusWithin)
        {
            var field = FreqOut.GetFocusedField();
            if (field?.HelpItems != null && field.HelpItems.Count > 0)
            {
                var dialog = new Dialogs.ShowHelpDialog
                {
                    Title = Radios.Lexicon.Get("help.field.window_title",
                        ("label", field.Label ?? field.Key)),
                    HelpTitle = Radios.Lexicon.Get("help.field.heading",
                        ("label", field.Label ?? field.Key)),
                    HelpItems = field.HelpItems
                };
                dialog.ShowDialog();
                return;
            }
        }

        if (FieldsPanel.IsKeyboardFocusWithin)
        {
            var dialog = new Dialogs.ShowHelpDialog
            {
                Title = Radios.Lexicon.Get("help.value_field.window_title"),
                HelpText = Radios.Lexicon.Get("help.value_field.body")
            };
            dialog.ShowDialog();
            return;
        }

        // Fall back to Command Finder
        ShowCommandFinder();
    }

    public void ShowCommandFinder()
    {
        var items = GetCommandFinderItemsCallback?.Invoke() ?? new List<Dialogs.CommandFinderItem>();
        var dialog = new Dialogs.CommandFinderDialog
        {
            GetCommands = () => items,
            ExecuteCommand = (tag) => ExecuteCommandCallback?.Invoke(tag),
            SpeakText = (msg) => Radios.ScreenReaderOutput.Speak(msg),
            CurrentMode = ActiveUIMode.ToString()
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Handle calibration reference entered via JJ Ctrl+F frequency input.
    /// Delegates to FreqOutHandlers if available, otherwise handles directly.
    /// </summary>
    public void HandleCalibrationFromFreqInput(string calibRef)
    {
        if (_freqOutHandlers != null)
        {
            // Use the existing handler which manages config, sounds, and speech
            _freqOutHandlers.HandleCalibrationPublic(calibRef);
        }
        else
        {
            // No handler — just play confirmation and speak
            CalibrationEngine.PlayVerificationTone(calibRef);
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.home.calibration_accepted"), Radios.VerbosityLevel.Critical, true);
        }
    }

    /// <summary>
    /// Show the Earcon Scratchpad — the audio bench. Also reachable from the
    /// Audio menu, and by typing "cqtest" into Ctrl+F.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This deliberately does NOT mute the radio (#138).</b> It used to,
    /// muting the slice on the way in and restoring on the way out, on the
    /// reasoning that you cannot hear a quiet earcon over band noise. That was
    /// right when the scratchpad only played a sound so you could confirm it
    /// existed. It stopped being right when the scratchpad became the bench for
    /// deciding whether an earcon is AUDIBLE ENOUGH — because the thing it has
    /// to be audible over is exactly the receive audio the mute was removing.
    /// Judging an alert against silence and then shipping it into a pileup is
    /// how an alert that tests fine turns out to be inaudible in use.
    /// </para>
    /// <para>
    /// The operator's own mute is theirs either way: the universal <c>M</c>
    /// toggles the active slice and <c>Shift+M</c> every slice, and unlike this
    /// method they leave the radio in the state the operator chose rather than
    /// in the state a dialog chose for them.
    /// The Audio menu route never muted, so the two ways in now behave the
    /// same, which they did not before.
    /// </para>
    /// </remarks>
    public void ShowEarconScratchpad()
    {
        var dialog = new Dialogs.EarconScratchpadDialog();
        dialog.ShowDialog();
    }

    /// <summary>
    /// Bring the main window to front and focus the frequency display.
    /// Matches Form1.gotoHome().
    /// </summary>
    public void gotoHome()
    {
        FocusHome();
    }

    /// <summary>
    /// Callback to rebuild the native menu bar after radio connects.
    /// Set by ShellForm after creating NativeMenuBar.
    /// </summary>
    public Action? RebuildMenuCallback { get; set; }

    /// <summary>
    /// Open the Settings dialog on a named tab ("Radio Setup", ...).
    /// Set by BridgeForm; advisory dialogs use it for their action buttons.
    /// </summary>
    public Action<string>? OpenSettingsCallback { get; set; }

    /// <summary>
    /// Rebuild the ScreenFields/Operations menus with live DSP controls.
    /// Called from PowerNowOn after the radio is ready.
    /// </summary>
    public void SetupOperationsMenu()
    {
        // Wording matters: since Sprint 42 Track D the callback QUEUES a
        // coalesced rebuild rather than running one inline, so this line must
        // not claim the rebuild happened here — NativeMenuBar.ApplyUIMode's
        // own trace marks the actual rebuild.
        Tracing.TraceLine("MainWindow.SetupOperationsMenu: requesting menu rebuild", TraceLevel.Info);
        RebuildMenuCallback?.Invoke();
    }

    #region StatusBar + ScanTimer — Sprint 10 Phase 10.1

    /// <summary>
    /// Write a named field to the WPF StatusBar.
    /// Replaces Form1.StatusBox.Write(key, value) — the RadioBoxes.MainBox API.
    /// Supported keys: "Power", "Memory", "Scan", "LogFile" (matching StatusBar TextBlocks).
    /// </summary>
    public void WriteStatus(string key, string value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => WriteStatus(key, value));
            return;
        }

        switch (key)
        {
            case "Power":
                StatusPower.Text = value;
                break;
            case "Memories":
                StatusMemory.Text = value;
                break;
            case "Scan":
                StatusScan.Text = value;
                break;
            case "LogFile":
                StatusLogFile.Text = value;
                break;
            case "Knob":
                // Knob status not in current StatusBar layout — ignore for now
                break;
            default:
                Tracing.TraceLine($"MainWindow.WriteStatus: unknown key '{key}'", TraceLevel.Warning);
                break;
        }
    }

    /// <summary>
    /// Scan timer — DispatcherTimer replacing Form1.ScanTmr (WinForms Timer).
    /// Used by scan.vb and MemoryScan.vb via globals.scanTimer property.
    /// </summary>
    private DispatcherTimer? _scanTimer;

    /// <summary>
    /// Gets the scan timer, creating it on first access.
    /// Tick event fires ScanTimerTick which the main app wires to
    /// scan.ScanTimer_Tick / MemoryScan.ScanTimer_Tick (replaces Form1 Handles clause).
    /// </summary>
    public DispatcherTimer ScanTimer
    {
        get
        {
            if (_scanTimer == null)
            {
                _scanTimer = new DispatcherTimer();
                _scanTimer.Interval = TimeSpan.FromMilliseconds(500); // default, overridden by scan code
                _scanTimer.Tick += (s, e) => ScanTimerTick?.Invoke(s, e);
            }
            return _scanTimer;
        }
    }

    /// <summary>
    /// Event raised on each scan timer tick. Wire this in ApplicationEvents.vb to dispatch
    /// to scan.ScanTimer_Tick or MemoryScan.ScanTimer_Tick based on scanstate.
    /// </summary>
    public event EventHandler? ScanTimerTick;

    /// <summary>
    /// Whether the scan timer is currently running.
    /// Replaces Form1.ScanTmr.Enabled check in globals.ScanInProcess.
    /// </summary>
    public bool ScanTimerEnabled
    {
        get => _scanTimer?.IsEnabled ?? false;
        set
        {
            if (value)
                ScanTimer.Start();
            else
                _scanTimer?.Stop();
        }
    }

    #endregion

    /// <summary>
    /// Enter Logging Mode with the log entry pre-filled from a station lookup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#310.</b> This replaces <c>HandleLogContactResult</c>, whose whole
    /// body was a trace line saying "wiring in Phase 9.5". The Log Contact
    /// button in Station Lookup works and always did — it sets
    /// <c>WantsLogContact</c> and fills <c>LookupData</c> — and nothing read
    /// either, so pressing it did nothing at all.
    /// </para>
    /// <para>
    /// It takes the values rather than reaching for the lookup window because
    /// that window is owned by the VB side, which is also where the old call
    /// site lived. Form1's version went through
    /// <c>LoggingLogPanel.PreFillFromLookup</c>; that interface has no
    /// implementation any more, so this uses <c>SetFieldText</c> on the live
    /// control, whose field names are the same set.
    /// </para>
    /// <para>
    /// Deliberately silent about the values. Focus lands on the Call Sign box
    /// and the screen reader reads it with the callsign already in it —
    /// speaking them as well would say everything twice, and Form1 carried the
    /// same note.
    /// </para>
    /// </remarks>
    public void PreFillLogEntryFromLookup(string callSign, string name, string qth,
                                          string state, string grid)
    {
        if (ActiveUIMode != UIMode.Logging) EnterLoggingMode();

        LoggingLogEntry.SetFieldText("CALL", callSign ?? "");
        LoggingLogEntry.SetFieldText("NAME", name ?? "");
        LoggingLogEntry.SetFieldText("QTH", qth ?? "");
        LoggingLogEntry.SetFieldText("STATE", state ?? "");
        LoggingLogEntry.SetFieldText("GRID", grid ?? "");

        LoggingLogEntry.FocusCallSign();
        Tracing.TraceLine("MainWindow.PreFillLogEntryFromLookup: filled from station lookup",
            TraceLevel.Info);
    }

    /// <summary>
    /// Public power-off entry point — callable from VB side.
    /// Matches Form1.powerNowOff().
    /// </summary>
    public void powerNowOff()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(powerNowOff);
            return;
        }
        PowerNowOffInternal();
    }

    /// <summary>
    /// Toggle focus between the log pane and the radio pane in Logging Mode.
    /// F6, the primary navigation key of the mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#310.</b> This was a stub whose whole body traced "wiring in Phase
    /// 9.5" — a phase the sprint numbering walked past long ago — so F6
    /// dispatched correctly, reached the handler, and did nothing. That is the
    /// no-silent-keystrokes rule in its worst form: an unbound key tells you
    /// so, while a key bound to a stub is indistinguishable from a key that
    /// worked and had no effect, which is indistinguishable from a radio that
    /// ignored you. The keyboard reference has documented F6 the whole time.
    /// </para>
    /// <para>
    /// Both targets exist and always did. <c>Form1</c> and <c>RadioPane.vb</c>
    /// were deleted in Sprint 11 but they were REPLACED, not dropped —
    /// <c>LoggingRadioPane</c> and <c>LoggingLogEntry</c> are named controls in
    /// MainWindow.xaml with accessible names, and both expose a public focus
    /// entry point. <c>RadioPaneControl.FocusFirst</c>'s own doc comment says
    /// "Called by F6 pane-switching logic", which nothing had ever done.
    /// </para>
    /// <para>
    /// Only the destination is announced, not both panes: the arrival is what
    /// the operator does not otherwise know. FreqBox announces itself on
    /// focus, so the radio side would be said twice — Form1 carried the same
    /// note.
    /// </para>
    /// </remarks>
    public void ToggleLoggingPaneFocusForHotkey()
    {
        if (ActiveUIMode != UIMode.Logging)
        {
            // Reachable: the command is also in the Command Finder and the
            // Hotkey Editor, both of which will happily run it from anywhere.
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("logging.pane.only_in_logging_mode"),
                VerbosityLevel.Critical, true);
            return;
        }

        if (LoggingRadioPane.IsKeyboardFocusWithin)
        {
            LoggingLogEntry.FocusCallSign();
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("logging.pane.log_entry"), VerbosityLevel.Terse, true);
        }
        else
        {
            LoggingRadioPane.FocusFirst();
        }
    }

    // LogCharacteristicsForHotkey and OpenFullLogEntryForHotkey WERE HERE AND
    // ARE DELETED (#310). Both were bodies of one trace line reading "stub —
    // wiring in Phase 9.5", and both had a working implementation on the VB
    // side the entire time: ShowLogCharacteristicsDialog and
    // OpenFullLogEntryForm in globals.vb, which the two callouts now call
    // directly. Nothing here to hold an implementation, so nothing here.
    //
    // They were left pointing at a "Phase 9.5" the sprint numbering walked
    // past long ago, and NativeMenuBar had already worked around them: the
    // Logging menu's Log Characteristics item is deliberately routed to
    // CommandValues.LogFileName with a comment saying the other command
    // "dead-ends in the same kind of MainWindow stub — don't wire it". The
    // workaround can go now; the comment is corrected with it.

    /// <summary>
    /// LogPanel bridge for KeyCommands access.
    /// Set by the main app when the logging panel is created.
    /// Phase 9.5: Move LogPanel creation to MainWindow.
    /// </summary>
    public ILogPanelCommands? LoggingLogPanel { get; set; }

    /// <summary>
    /// WinForms-compatible Visible property.
    /// Maps to WPF Visibility for KeyCommands.vb compatibility.
    /// </summary>
    public bool Visible
    {
        get => Visibility == Visibility.Visible;
        set => Visibility = value ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>
    /// WinForms-compatible BringToFront.
    /// Focuses this control; the parent ShellForm handles window activation.
    /// </summary>
    public new void BringToFront()
    {
        Focus();
    }

    #endregion
}
