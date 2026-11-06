using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio: every way of checking how you sound, in one non-modal
/// window, and nothing that only verifies the feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is its own window (#835, ruled by Noel 2026-10-27 13:57).</b> It
/// was the Audio Workshop's Hear Yourself page, and it was confusing to use on
/// a one-receiver radio over SmartLink: it talked about off-air takes that
/// radio cannot do, the Loopback Check was a button rather than a way of listening,
/// two slice-recorder buttons repeated the recorder that lives on Home, the
/// menus and the JJ key, and setup controls, bench tools and take results sat
/// in one long tab ring. A screen-reader user meets every one of those one Tab
/// at a time. Checking is a different moment from adjusting, so it got its own
/// window, and the Workshop keeps a button that opens this one: one surface,
/// several doors (the JJ key Alt+C, the Audio menu, that button), never two
/// copies.
/// </para>
/// <para>
/// <b>The layout, ruled 13:45.</b> Start sits in a group by itself, so Shift+Tab
/// from the method reaches it; focus lands on the method when the window opens;
/// Ctrl+Enter starts and stops from anywhere in it. Changing the method
/// reconfigures what is shown below it, and the methods offered depend on the
/// radio and the connection. <b>Opening this window never starts a check</b>
/// (13:57): <c>StartAudioCheck</c> stays globally unbound.
/// </para>
/// <para>
/// <b>Escape never leaves you transmitting.</b> While a check transmits, the
/// first Escape unkeys and stays; the second closes. Closing THIS window, by
/// any route, stops a running check and puts back whatever it changed, and so
/// does the radio going away. Closing the Audio Workshop does not: this is an
/// independent window, and a check running here is not the Workshop's.
/// </para>
/// <para>
/// The hooks it needs from the shell (the PTT controller, the app's audio
/// settings, the audio devices file) are the Audio Workshop's existing static
/// properties, read here rather than duplicated, so MainWindow wires them once.
/// </para>
/// </remarks>
public partial class CheckYourAudioDialog : JJFlexDialog
{
    private FlexBase? _rig;
    private bool _polling;
    private readonly DispatcherTimer _pollTimer;
    private static CheckYourAudioDialog? _instance;

    /// <summary>
    /// Where the current radio comes from when a door has none to hand (the
    /// Workshop opened from Settings has no radio of its own). Set by MainWindow.
    /// </summary>
    public static Func<FlexBase?>? RigSource { get; set; }

    /// <summary>Home's tune toggle, for the tune key pressed in this window (2026-11-02). Set by MainWindow.</summary>
    public static Action? ToggleTuneSource { get; set; }

    /// <summary>Turn a tune carrier of ours off; true when one was on (2026-11-02). Set by MainWindow.</summary>
    public static Func<bool>? TuneOffSource { get; set; }

    /// <summary>The window's one content panel. Every section is a GroupBox directly in it, which is what F6 counts.</summary>
    private StackPanel CheckContent { get; }

    /// <summary>The sections and the radio-only register: one implementation with the Audio Workshop's (<see cref="DialogSections"/>).</summary>
    private readonly DialogSections _sections = new();

    // Per-radio preferences (serial-keyed store). Loaded on SetRig.
    private RadioConfig? _radioCfg;
    private string _radioCfgSerial = "";

    /// <summary>Mic Gain and the Windows input level: the same settings, and the same implementation, as the Workshop's Microphone section.</summary>
    private readonly TransmitGainControls _gain;

    private static Func<PttSafetyController?>? PttControllerSource => AudioWorkshopDialog.PttControllerSource;
    private static Func<AudioOutputConfig?>? AudioConfigSource => AudioWorkshopDialog.AudioConfigSource;
    private static Action? AudioConfigSave => AudioWorkshopDialog.AudioConfigSave;

    private AudioCheckSession? _session;

    /// <summary>
    /// Is an Audio Check session active right now, in ANY phase, including the
    /// post-unkey wait for playback? While it is, the session owns the
    /// transmitter: the dialog transmit door (#778c, <see cref="DialogTransmitKeys"/>)
    /// refuses push to talk and the lock, and leaves Escape to this window's own
    /// two-stage handler. Read-only; the session itself is untouched. The Audio
    /// Workshop's property of the same name forwards here, so the door's wiring
    /// in MainWindow did not move.
    /// </summary>
    internal static bool AnAudioCheckIsActive => _instance?._session?.Active == true;

    /// <summary>
    /// Escape was pressed in another window while a check runs (#835, fix
    /// round 2). If the check is transmitting, stop it, which unkeys and
    /// restores through the session, and return true so that window stays
    /// open. Once the check has unkeyed, Escape there is that window's own.
    /// Closing the Workshop by its close button or Alt+F4 still leaves a
    /// check here running: this window is independent.
    /// </summary>
    internal static bool StopTransmittingCheckFromElsewhere()
    {
        var session = _instance?._session;
        if (session?.Active != true || !session.EscapeStopsTransmit) return false;
        session.StopCheck();
        return true;
    }

    /// <summary>True while a check is starting or running, so another window's take (the Workshop's diagnostic take) can refuse rather than race it.</summary>
    internal static bool ACheckIsStartingOrActive =>
        _instance != null && (_instance._checkStarts.IsBusy || _instance._session?.Active == true);

    public CheckYourAudioDialog()
    {
        // DRAFT PROSE (#835): awaiting Noel. The name is Noel's (13:41).
        Title = "Check your audio";
        Width = 560;
        Height = 620;

        // Non-modal, like the Workshop: show in the taskbar, resizable, and no
        // owner, so Alt+Tab works and it does not steal focus from Home.
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        new System.Windows.Interop.WindowInteropHelper(this).Owner = IntPtr.Zero;

        CheckContent = new StackPanel { Margin = new Thickness(8) };
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Content = CheckContent
        };

        _gain = new TransmitGainControls(Dispatcher, () => _rig, () => _polling,
            () => AudioWorkshopDialog.AudioDevicesPath?.Invoke(), host: "Check your audio");

        BuildCheckDialog();
        UpdateRadioControlAvailability();

        // ~2 Hz, as the Workshop polled when this was its page.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += PollTimer_Tick;

        Closed += (s, e) =>
        {
            _closing = true;
            // A session must never outlive its window: unkey (through the
            // controller), restore every changed state, stop playback.
            CancelCheckStart();
            _session?.ForceEnd("Audio check ended");
            _session = null;
            // The computer microphone probe holds a capture device open.
            StopComputerMicCheck(speak: false);
            // The test tone and the reference recording must never outlive
            // their window either: arming is never persisted, and an armed
            // source that outlived it would replace the microphone at the
            // next key-down with nothing on screen saying so.
            DisarmTone(speak: false);
            // A reference's held end silence is handed to the radio's own
            // receive wait rather than ended here (round five, 7b; Noel
            // 2026-11-06 03:12): under a held hand mic, ending it would put
            // the desk microphone on the air. Anything else disarms as before.
            ReleaseReferenceAtClose();
            DetachReferenceAudio();
            // Local take playback: the player is static and would otherwise keep
            // sounding, and keep calling back into a button that is gone.
            DetachTakePlayback();
            _pollTimer.Stop();
            _gain.Dispose();
            // Guarded: only THIS window clears the pointer (the Workshop learned
            // this the hard way with a second instance from Settings).
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    /// <summary>
    /// Show or bring to front the one Check your audio window. Opening it never
    /// starts a check.
    /// </summary>
    public static void ShowOrFocus(FlexBase? rig)
    {
        rig ??= RigSource?.Invoke();
        if (_instance == null || !_instance.IsLoaded)
        {
            _instance = new CheckYourAudioDialog();
            _instance.SetRig(rig);
            _instance.Show();
            // Non-modal WPF windows in a WinForms app don't receive keyboard
            // input without this — the WinForms loop doesn't route keys to WPF.
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_instance);
        }
        else
        {
            if (rig != null && !ReferenceEquals(_instance._rig, rig)) _instance.SetRig(rig);
            _instance.FocusFirstControl();
        }
        _instance.Activate();
    }

    /// <summary>
    /// The Command Finder's "Start Audio Check": open this window and start a
    /// check with the method shown. Never bound to a key (see
    /// <c>KeyCommands._unboundNotes</c>): a global chord that keys the
    /// transmitter from anywhere is not something to hand out by default.
    /// </summary>
    public static void ShowOrFocusAndStartCheck(FlexBase? rig)
    {
        ShowOrFocus(rig);
        _instance?.ToggleAudioCheck();
    }

    public static void NotifyRigReady(FlexBase rig)
    {
        // Whether or not this window is open: a test carries on only on the
        // same radio (Unified takes, D3), so every connection is counted.
        NoteTakeSeriesConnection(rig);
        DialogSections.PostRigChange(_instance, w => w.SetRig(rig), onlyIfLoaded: true);
    }

    /// <summary>
    /// Radio teardown. The window outlives the radio, so the poll must stop
    /// before Disconnect nulls the radio (the 2026-08-07 app-close crash the
    /// Workshop met). Safe from any thread; a no-op when the window is closed.
    /// </summary>
    /// <summary>
    /// The shell is about to tear <paramref name="rig"/> down on purpose (a
    /// disconnect, the app closing, a radio switch): shut admission, end any
    /// check, and wait, bounded, for everything any check changed on that
    /// radio to be put back while it reports receive and for the command queue
    /// to run past it. Called from <c>MainWindow.UnwireRadioEvents</c>, which
    /// <c>CloseTheRadio</c> runs before it disposes the rig. Also disarms the
    /// tone and the reference, whose arm may have moved the transmit input.
    /// </summary>
    /// <remarks>
    /// An UNEXPECTED loss never comes through here: by the time
    /// <see cref="NotifyRigGone"/> runs the connection is dead, the session's
    /// restore writes reach nothing, and nothing records them for the next
    /// connect. Only an off-air take keeps a recovery record (the listen-back
    /// recovery store), offered back at the next connect.
    /// </remarks>
    public static void EndCheckBeforeTeardown(FlexBase? rig)
    {
        if (rig == null) return;
        // 1. Admission shut FIRST (fix round 3), before anything ends, waits or
        //    pumps: no check may start and the app keys nothing on this radio
        //    until this teardown has finished.
        rig.BeginCheckTeardown();
        try
        {
            // 2. End whatever this window is running on this radio. Its changes,
            //    and the Loopback arrangement's, land in the radio's own restore
            //    owner synchronously, so the settle below sees them all.
            var inst = _instance;
            if (inst != null && ReferenceEquals(inst._rig, rig))
            {
                void End()
                {
                    inst._tearingDown = true;
                    inst.CancelCheckStart();
                    // A saved playback ends before its mutes are put back (Unified takes).
                    inst._session?.EndTake("connection-ending");
                    inst._session?.ForceEnd("Radio disconnected, audio check ended");
                    inst._session = null;
                    inst.DisarmTone(speak: false);
                    inst.DisarmReference(speak: false);
                }
                if (inst.Dispatcher.CheckAccess()) End();
                else inst.Dispatcher.Invoke(End, System.Windows.Threading.DispatcherPriority.Send,
                    System.Threading.CancellationToken.None, TimeSpan.FromSeconds(2));
            }

            // 3. Wait for the RADIO's owner, not for the window or its session:
            //    a check that already ended, or whose window already closed,
            //    is still in there and is still waited for. A second teardown
            //    joins the first. The frame keeps this dispatcher pumping while
            //    it waits (the radio's events and other windows need it), and
            //    admission is already shut, so what it pumps cannot start a
            //    check or key the radio.
            var work = rig.SettleChecksForTeardownAsync(TeardownBound);
            if (!work.IsCompleted)
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var frame = new System.Windows.Threading.DispatcherFrame();
                work.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                    TaskScheduler.Default);
                // A second, independent stop, the bound plus a margin.
                var guard = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
                    { Interval = TeardownBound + TimeSpan.FromSeconds(1) };
                guard.Tick += (_, _) => { guard.Stop(); frame.Continue = false; };
                guard.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                guard.Stop();
            }

            var result = work.IsCompletedSuccessfully ? work.Result : new AudioCheckTeardownResult(false, "did-not-finish");
            JJTrace.Tracing.TraceLine("Check your audio teardown: " + (result.Completed ? "completed" : "NOT confirmed at " + result.Stage),
                result.Completed ? System.Diagnostics.TraceLevel.Info : System.Diagnostics.TraceLevel.Warning);
            if (!result.Completed && !rig.CheckRestoreStillOwed())
            {
                // Round 1030 repair TD (D2): the words come from what is owed, not
                // from the stage. Every restore that is not done is known to owe
                // nothing (on Noel's bench 2026-10-30 the withheld entry was an
                // off-air take the radio had already confirmed restored), so
                // nothing is said now and nothing is kept for the next connect.
                JJTrace.Tracing.TraceLine("Check your audio teardown: not confirmed at " + result.Stage
                    + ", but nothing is owed; nothing is said or kept", System.Diagnostics.TraceLevel.Info);
            }
            else if (!result.Completed)
            {
                // Withheld because the radio kept transmitting is the correct
                // outcome, and is said as itself, not as a restore.
                // DRAFT PROSE (#835): awaiting Noel.
                bool withheld = result.Stage == "still-transmitting";
                string key = withheld ? "audio.check.teardown_withheld" : "audio.check.teardown_unconfirmed";
                // #835, 2026-10-29 item 2: on the bench this was said and never
                // heard; the disconnect's window changes flush the reader. It is
                // queued under its own subject now, and kept for the next
                // connect to this radio, which says it again.
                rig.NoteCheckRestoreReminder(withheld ? FlexBase.CheckRestoreWithheld : FlexBase.CheckRestoreUnconfirmed);
                ScreenReaderOutput.Speak(Lexicon.Get(key), Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Critical,
                    subject: Radios.Speech.SpeechSubject.CheckSettingsNotRestored);
            }
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Check your audio teardown failed: " + ex, System.Diagnostics.TraceLevel.Error);
            try { rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreUnconfirmed); } catch { /* never block the disconnect */ }
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.teardown_unconfirmed"), Radios.Speech.SpeechIntent.Queue,
                VerbosityLevel.Critical, subject: Radios.Speech.SpeechSubject.CheckSettingsNotRestored);
        }
        finally
        {
            if (_instance != null) _instance._tearingDown = false;
            rig.EndCheckTeardown();
        }
    }

    /// <summary>Said, and nothing started, when a check is asked for while the radio is being torn down.</summary>
    private static bool RefusedForTeardown(FlexBase? rig)
    {
        if (rig?.TeardownInProgress != true) return false;
        // DRAFT PROSE (#835): awaiting Noel.
        ScreenReaderOutput.Speak(Lexicon.Get("audio.check.teardown_refuses"), VerbosityLevel.Critical, interrupt: true);
        return true;
    }

    /// <summary>How long a teardown before a disconnect may hold the close, end to end.</summary>
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(5);

    /// <summary>True while the window is putting a check back before the radio goes.</summary>
    private bool _tearingDown;

    public static void NotifyRigGone() =>
        DialogSections.PostRigChange(_instance, w => w.SetRig(null), onlyIfLoaded: false);

    private bool _closing;

    /// <summary>
    /// Focus lands on the method (ruled 13:45): Shift+Tab reaches Start, and
    /// Tab walks the method's own options.
    /// </summary>
    protected override void FocusFirstControl()
    {
        if (_listenMethodControl != null && _listenMethodControl.IsVisible && _listenMethodControl.IsEnabled
            && _listenMethodControl.Focus()) return;
        base.FocusFirstControl();
    }

    /// <summary>
    /// Two-stage Escape while a check transmits (the first unkeys and stays,
    /// the second closes), F6 between sections, and Ctrl+Enter to start or stop
    /// from anywhere in the window. The class handler runs before
    /// JJFlexDialog's instance handler, so the first Escape can be consumed and
    /// the closing one simply let through.
    /// </summary>
    /// <remarks>
    /// <b>What decides is whether anything is still transmitting, not whether
    /// a session object is alive (fix round 1).</b> After the unkey a session
    /// stays Active through its post-unkey grace, its recorder's stop-then-play
    /// startup and the whole Loopback playback. Asking only "is a session
    /// active?" made a prompt second Escape stop the check a second time and a
    /// third press necessary to close. <see cref="AudioCheckEscape.Decide"/> is
    /// the rule; a radio still transmitting (a hardware PTT line, or a radio
    /// slow to report its unkey) keeps the window open however often Escape is
    /// pressed.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ComputerMicCheckRunning && _session?.Active != true)
        {
            // No check is transmitting: Escape stops the listening and stays, so
            // the verdict is heard before anything closes. The next one closes.
            // The tune key works during the listening (2026-11-02), so a carrier
            // of ours goes off on this same press: Escape never leaves one up.
            EscapeOurTune();
            StopComputerMicCheck(speak: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            // The radio's own transmit report, not only the session's (fix round
            // 2): a hardware PTT line can hold the radio keyed with no check
            // running, or while one is still preparing.
            // And a tune carrier of ours, up or still in the command queue
            // (2026-11-02): Escape turns it off rather than closing on it.
            bool transmitting = _session?.EscapeStopsTransmit == true || _rig?.Transmit == true
                || _rig?.TuneAskedOrUp == true
                || PttControllerSource?.Invoke()?.IsTransmitting == true;
            switch (AudioCheckEscape.Decide(_checkStarts.IsBusy, _checkStarts.IsCancelled,
                _session?.Active == true, transmitting))
            {
                case AudioCheckEscapeAction.CancelPendingStart:
                    CancelCheckStart();
                    // DRAFT PROSE (#787): awaiting Noel.
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.check.start_cancelled_escape"), VerbosityLevel.Terse);
                    e.Handled = true;
                    return;
                case AudioCheckEscapeAction.CloseCancelledStart:
                    // A cancelled start keeps admission closed during rollback,
                    // but its rollback no longer holds this window open.
                    Close();
                    e.Handled = true;
                    return;
                case AudioCheckEscapeAction.UnkeyAndStay:
                    if (_checkStarts.IsBusy && !_checkStarts.IsCancelled) CancelCheckStart();
                    if (_session?.Active == true) _session.StopCheck(escape: true);
                    else UnkeyOutsideACheck();
                    StopCheckPlayback();
                    e.Handled = true;
                    return;
                case AudioCheckEscapeAction.EndAndClose:
                    // Unkeyed already: end what remains (a playback, the
                    // Loopback arrangement) and restore, then let the press
                    // through so the dialog closes on this press.
                    _session?.ForceEnd(null);
                    StopCheckPlayback();
                    break;
                default:
                    StopCheckPlayback();
                    break;
            }
        }
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            // The help opens at the section of the method shown, wherever focus is.
            HelpLauncher.ShowHelp(AudioCheckMethods.HelpContext(SelectedMethod));
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F6 && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            SectionNavigator.Move(CheckContent, forward: Keyboard.Modifiers == ModifierKeys.None);
            e.Handled = true;
            return;
        }
        if (TakesTheTuneKey(e))
        {
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            // This press can key the radio (#859). The gate decides starts only:
            // a fresh key-down always stops or cancels what is live. This window
            // takes the key before the field does, so a number still being typed
            // has to be finished here or the check runs at the old value, and
            // anything but the number as typed stops a start: the operator hears
            // what it is now.
            var typing = Keyboard.FocusedElement as Controls.ValueFieldControl;
            bool checkLive = _checkStarts.IsBusy || _iqBusy || _session?.Active == true || ComputerMicCheckRunning;
            switch (_ctrlEnterPress.Decide(Environment.TickCount64, e.IsRepeat, checkLive || CheckPlaybackRunning,
                () => typing?.CommitPendingEntry() ?? true))
            {
                case PressAct.Stop:
                    if (checkLive) ToggleAudioCheck();
                    StopCheckPlayback();
                    break;
                case PressAct.Start:
                    ToggleAudioCheck();
                    break;
            }
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private readonly FreshPressGate _ctrlEnterPress = new();

    /// <summary>A chord that was refused or changed is over when Control goes up.</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) _ctrlEnterPress.NoteControlUp();
        // The tune key's chord carries Control or Alt; either going up ends it.
        Key raw = e.Key == Key.System ? e.SystemKey : e.Key;
        if (raw is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt) _tuneKeyPress.NoteControlUp();
        base.OnPreviewKeyUp(e);
    }

    /// <summary>
    /// Escape with the radio transmitting and no check to stop (fix round 2):
    /// ask the controller to unkey if it is the one keying, and if the radio is
    /// still keyed by something else, a hardware line or VOX, say so plainly
    /// rather than closing the window on it.
    /// </summary>
    private void UnkeyOutsideACheck()
    {
        bool ourTune = EscapeOurTune();
        var ptt = PttControllerSource?.Invoke();
        if (ptt?.IsTransmitting == true) ptt.EscapeUnlock();
        else if (!ourTune && _rig?.Transmit == true)
            // DRAFT PROSE (#835): awaiting Noel.
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.escape_radio_still_transmitting"),
                VerbosityLevel.Critical, interrupt: true);
    }

    /// <summary>
    /// Escape with a tune of ours holding the radio (2026-11-02). Until then
    /// Escape here asked only the push-to-talk controller, which a tune carrier
    /// does not go through, so it blamed a hardware line or VOX and kept the
    /// window open on our own carrier. Now the carrier goes off through Home's
    /// toggle, tones and SWR sentence and all, and the window stays open: the
    /// next Escape closes it, as after any unkey. An ATU cycle cannot be
    /// stopped from here; it is named, so the hardware sentence is never said
    /// about our own tune. True when a tune of ours answered for the transmit.
    /// </summary>
    internal bool EscapeOurTune()
    {
        if (TuneOffSource?.Invoke() == true) return true;
        if (!TuneDoors.Up(_rig)) return false;
        // DRAFT PROSE: awaiting Noel.
        ScreenReaderOutput.Speak(Lexicon.Get("audio.tune.tuner_still_tuning"),
            VerbosityLevel.Critical, interrupt: true);
        return true;
    }

    /// <summary>
    /// A tune of ours holds the radio: a carrier up or on its way, or an ATU
    /// cycle. For the Stop button, which names it rather than blaming a
    /// hardware line; only Escape turns it off.
    /// </summary>
    internal bool OurTuneHolds() => _rig?.TuneAskedOrUp == true || TuneDoors.Up(_rig);

    /// <summary>One answer per press of the tune key, whatever the reader does with a held one.</summary>
    private readonly FreshPressGate _tuneKeyPress = new();

    /// <summary>
    /// The operator's tune key in this window (2026-11-02): Ctrl+Shift+T by
    /// default, read from the live key table so a remap is honoured. Tune is a
    /// Radio-scope command and reaches no dialog by itself, while the refusal
    /// here says "Tune is on. Turn it off first." So the window takes it. A
    /// chord with no Control or Alt is not taken, so a tune key remapped to a
    /// plain key still types in this window's fields. True when taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A stop is explicit and never swallowed; a start needs a new press</b>
    /// (Codex, 2026-11-02). With a carrier of ours up or on its way, any fresh
    /// key-down turns it off through <see cref="TuneOffSource"/>, which writes
    /// off and nothing else, with no debounce. Otherwise a start goes through
    /// Home's toggle only for a new press, by <see cref="FreshPressGate"/>, the
    /// rule Ctrl+Enter here already follows: so a held key delivered as a
    /// reader's down/up pairs starts once, the first pair stops it, and the
    /// pairs after that start nothing. That is the safe direction; nobody has
    /// measured how this chord arrives.
    /// </para>
    /// <para>
    /// While a check owns the transmitter, or is starting, or an off-air take
    /// is running, a start is refused: the same rule as push to talk here.
    /// The Windows microphone check does not own the radio, so a carrier may
    /// start during it; Escape then turns that carrier off with the listening.
    /// </para>
    /// </remarks>
    private bool TakesTheTuneKey(KeyEventArgs e)
    {
        var chord = WpfKeyConverter.ToWinFormsKeys(e);
        if ((chord & (System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt)) == 0) return false;
        if (!KeyCommands.IsTuneKey(chord)) return false;
        if (_rig == null) return true;
        bool carrierOurs = _rig.TuneAskedOrUp;
        switch (_tuneKeyPress.Decide(Environment.TickCount64, e.IsRepeat, carrierOurs, () => true))
        {
            case PressAct.Stop:
                TuneOffSource?.Invoke();
                break;
            case PressAct.Start:
                bool checkOwnsTransmitter = _session?.Active == true || _checkStarts.IsBusy || _iqBusy;
                if (checkOwnsTransmitter) TuneDoors.RefuseTuneDuringCheck();
                else ToggleTuneSource?.Invoke();
                break;
        }
        return true;
    }

    /// <summary>
    /// Enable or disable the radio-side controls to match connection state.
    /// IsEnabled=false drops a control from the tab order and tells UIA it is
    /// unavailable, so a review cursor still learns it exists. Disabling a
    /// GroupBox cascades to its children without touching their own IsEnabled,
    /// so state a section manages for its own reasons survives.
    /// </summary>
    private void UpdateRadioControlAvailability() => _sections.SetRadioAvailable(_rig != null);

    public void SetRig(FlexBase? rig)
    {
        var oldRig = _rig;
        if (!ReferenceEquals(oldRig, rig))
        {
            CancelCheckStart();
            if (_iqRun != null) _ = FinishIqAsync("connection-changed");
            // BEFORE _rig changes, on a disconnect as well as a switch (fix
            // round 1): the session's End reaches back through this window to
            // take a Loopback arrangement down, and that needs the old radio.
            // This required an arriving radio until then, so a disconnect ended
            // the session only after _rig was null.
            if (oldRig != null)
            {
                // A saved playback ends incomplete, and is not waited for (Unified takes).
                _session?.EndTake(rig == null ? "connection-ended" : "radio-changed");
                _skipTakeDrain = true;
                try { _session?.ForceEnd(rig == null ? "Radio disconnected, audio check ended" : "Radio changed, audio check ended"); }
                finally { _skipTakeDrain = false; }
                _session = null;
            }
            _savedTakes.Clear();
            _iqRecoveryReader = null;
            _lastIqDirectory = null;
            _offAirStatus = "";
            _offAirReading = "";
            _radioCfg = null;
            _radioCfgSerial = "";
            // A per-radio setting: the next radio's is read when it arrives.
            _savePlaybackTakes = false;
        }
        if (oldRig != null && !ReferenceEquals(oldRig, rig))
            _ = oldRig.StopSliceRecorderOwnedByAsync(this);
        _rig = rig;
        UpdateRadioControlAvailability();
        if (rig != null)
        {
            // A switch straight to another radio lets the departing radio's
            // tone and reference go, as the branch below does for a radio that
            // leaves with none arriving (round four repair, part 6e-3). The
            // shell's own switch tears the old radio down first
            // (EndCheckBeforeTeardown, from UnwireRadioEvents), which makes this
            // a second, harmless release there; but a surface holding an older
            // radio can call ShowOrFocus with it and reach this switch with no
            // teardown at all, and a window must never leave a departing radio
            // armed, whoever called it.
            if (oldRig != null && !ReferenceEquals(oldRig, rig))
            {
                DisarmTone(speak: false, rig: oldRig);
                // An end its poll had not seen yet is finished for that radio,
                // the app's key let go after the tail (round five repair,
                // 7h-2); its held end silence, if any, goes to that radio's own
                // receive wait, as at a close (round five, 7b).
                ReleaseReferenceAtSwitch(oldRig);
            }
            LoadPerRadioPrefs();
            LoadToneSettings();
            // A fresh rig is never armed — arming does not survive a radio
            // switch by design.
            SetToneCheckSilently(rig.TxToneEngaged);
            UpdateToneStatus(speakIfNewlyOutside: false);
            PollOnce();
            _pollTimer.Start();
        }
        else
        {
            // Radio gone. Any session was ended above, while _rig was still
            // the radio it changed.
            CancelCheckStart();
            DisarmTone(speak: false, rig: oldRig);
            SetToneCheckSilently(false);
            DisarmReference(speak: false, rig: oldRig);
            // Clear a stale loopback arrangement on the departing rig so a
            // reconnect on the same FlexBase can arrange again.
            if (oldRig != null && oldRig.LoopbackArranged)
                _ = oldRig.EndLoopbackArrangementAsync();
            _pollTimer.Stop();
            UpdateMicReading();
        }
        RefreshOfferedMethods();
    }

    /// <summary>One pass of everything the 2 Hz poll keeps honest.</summary>
    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_rig == null) return;
        PollOnce();
    }

    private void PollOnce()
    {
        var rig = _rig;
        if (rig == null) return;

        // The test tone and the reference follow the ENGINE every tick: the
        // Ctrl+J, Ctrl+G chord arms the tone from outside this window, a
        // reference pass ends when the recording runs out, and the passband
        // warning must fire if the filter moves out from under an armed tone.
        SyncToneArmUi();
        SyncToneMonitor();
        UpdateToneStatus(speakIfNewlyOutside: rig.TxToneEngaged);
        SyncReferenceUi();

        _polling = true;
        try { _gain.Poll(rig, () => _startCheckButton?.Focus()); }
        finally { _polling = false; }

        // The methods on offer can change under an open window: a SmartLink
        // session replaced by a local one, or the radio's antenna list arriving
        // after connect. Cheap; it rebuilds the cycle only when the set moved.
        RefreshOfferedMethods();
        SyncPlayTakeLabel();
        UpdateMicReading();
    }

    private static CheckBox MakeToggle(string label) => AudioWorkshopDialog.MakeToggle(label);
    private static CycleFieldControl MakeCycle(string label, string[] options) => AudioWorkshopDialog.MakeCycle(label, options);
}
