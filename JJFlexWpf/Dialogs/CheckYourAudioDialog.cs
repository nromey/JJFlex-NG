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
/// was the Audio Workshop's Hear Yourself page. Don, on a one-receiver radio
/// over SmartLink, found it confusing: it talked about off-air takes his radio
/// cannot do, the Loopback Check was a button rather than a way of listening,
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
/// first Escape unkeys and stays; the second closes. Closing by any route stops
/// a running check and puts back whatever it changed.
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
            () => AudioWorkshopDialog.AudioDevicesPath?.Invoke());

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
            // The test tone and the reference recording must never outlive
            // their window either: arming is never persisted, and an armed
            // source that outlived it would replace the microphone at the
            // next key-down with nothing on screen saying so.
            DisarmTone(speak: false);
            DisarmReference(speak: false);
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

    public static void NotifyRigReady(FlexBase rig) =>
        DialogSections.PostRigChange(_instance, w => w.SetRig(rig), onlyIfLoaded: true);

    /// <summary>
    /// Radio teardown. The window outlives the radio, so the poll must stop
    /// before Disconnect nulls the radio (the 2026-08-07 app-close crash the
    /// Workshop met). Safe from any thread; a no-op when the window is closed.
    /// </summary>
    /// <summary>
    /// The shell is about to tear the radio down on purpose (a disconnect, or
    /// the app closing): end any check NOW, while the rig and its transport are
    /// still up, so its restoration is sent to a radio that can still hear it.
    /// Called from <c>MainWindow.UnwireRadioEvents</c>, which <c>CloseTheRadio</c>
    /// runs before it disposes the rig. Also disarms the tone and the
    /// reference, whose arm may have moved the radio's transmit input.
    /// </summary>
    /// <remarks>
    /// An UNEXPECTED loss never comes through here: by the time
    /// <see cref="NotifyRigGone"/> runs the connection is dead, the session's
    /// restore writes reach nothing, and nothing records them for the next
    /// connect. Only an off-air take keeps a recovery record (the listen-back
    /// recovery store), offered back at the next connect.
    /// </remarks>
    public static void EndCheckBeforeTeardown()
    {
        var inst = _instance;
        if (inst == null) return;
        void End()
        {
            inst.CancelCheckStart();
            inst._session?.ForceEnd("Radio disconnected, audio check ended");
            inst._session = null;
            inst.DisarmTone(speak: false);
            inst.DisarmReference(speak: false);
        }
        if (inst.Dispatcher.CheckAccess()) End();
        // Synchronously, so it is done before the caller disposes the rig;
        // bounded, so a busy UI thread cannot hold the teardown forever.
        else inst.Dispatcher.Invoke(End, System.Windows.Threading.DispatcherPriority.Send,
            System.Threading.CancellationToken.None, TimeSpan.FromSeconds(2));
    }

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
        if (e.Key == Key.Escape)
        {
            switch (AudioCheckEscape.Decide(_checkStarts.IsBusy, _checkStarts.IsCancelled,
                _session?.Active == true, _session?.EscapeStopsTransmit == true))
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
                    _session?.StopCheck();
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
            HelpLauncher.ShowHelp("CheckYourAudio");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F6 && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            SectionNavigator.Move(CheckContent, forward: Keyboard.Modifiers == ModifierKeys.None);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            if (_session?.Active == true)
            {
                ToggleAudioCheck();
                StopCheckPlayback();
            }
            else if (!StopCheckPlayback()) ToggleAudioCheck();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
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
                _session?.ForceEnd(rig == null ? "Radio disconnected, audio check ended" : "Radio changed, audio check ended");
                _session = null;
            }
            _iqRecoveryReader = null;
            _lastIqDirectory = null;
            _offAirStatus = "";
            _offAirReading = "";
            _radioCfg = null;
            _radioCfgSerial = "";
        }
        if (oldRig != null && !ReferenceEquals(oldRig, rig))
            _ = oldRig.StopSliceRecorderOwnedByAsync(this);
        _rig = rig;
        UpdateRadioControlAvailability();
        if (rig != null)
        {
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
