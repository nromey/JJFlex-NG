using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio's countdown before it keys (#835, fix round 2026-10-28,
/// Track C). The window's one seam with it: each start path begins the count
/// as it begins setting up, and <c>AudioCheckSession.StartAsync</c> waits for
/// it at the one place it keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled by Noel 2026-10-28.</b> 21:32: a countdown before a method that
/// keys, then the normal transmit tone or speech; spoken with sounds off,
/// spoken and beeped with sounds on and Chatty. 21:35, for the off-air take
/// and Loopback: <i>"you set up the take as you're counting down. If the
/// countdown is too short, delay it a bit."</i> So "arrange, then key" became
/// "arrange while counting, then key": the key goes down only when BOTH the
/// count has ended and the setup is confirmed, and a slow setup holds the last
/// beat rather than keying early (<see cref="CountdownRun"/>).
/// </para>
/// <para>
/// <b>A bare Control skips the rest of the count</b> (Noel 2026-10-29 01:05:
/// <i>"it aborts the count and transmits"</i>). Escape, Start and Ctrl+Enter
/// still cancel without transmitting. The trigger is Control pressed and
/// released ALONE (<see cref="BareControlPress"/>), because Ctrl+Enter, the
/// cancel, begins with Control down. A skip still waits for the setup
/// (<see cref="CountdownRun.Skip"/>), so the off-air take and Loopback never
/// key onto a half-built arrangement.
/// </para>
/// <para>
/// <b>Every method counts down</b>, the zero-power microphone test included;
/// see <see cref="CheckCountdown"/> for why, and for the open question.
/// </para>
/// </remarks>
public partial class CheckYourAudioDialog
{
    /// <summary>The countdown of the start under way, or null when none is counting.</summary>
    private CountdownRun? _startCountdown;

    /// <summary>
    /// Begin the count for a start that is about to set up. Nothing when the
    /// operator has turned the countdown off. Called only after the start has
    /// been admitted, so two never overlap.
    /// </summary>
    private void BeginStartCountdown(AudioCheckListenMethods method)
    {
        HookCountdownSkipKeys();
        _startCountdown?.Cancel();
        _countdownStartedTick = Environment.TickCount64;
        var plan = CheckCountdown.PlanFor(method, CountdownSettings.Current,
            EarconPlayer.IsOn(EarconPlayer.EarconCategory.Transmit),
            ScreenReaderOutput.CurrentVerbosity);
        _startCountdown = plan == null
            ? null
            : CountdownRun.Start(plan, FixerCountdown.Voice.Instance, "Check your audio, " + method);
    }

    /// <summary>True while a start's countdown is playing.</summary>
    private bool StartCountdownRunning => _startCountdown?.IsRunning == true;

    /// <summary>
    /// The setup is confirmed: wait out what remains of the count, its spoken
    /// numbers included. True means key now; false means the count was called
    /// off, or its words could not be delivered, and nothing may key. True at
    /// once when the countdown is off.
    /// </summary>
    private async Task<bool> CountdownLetsKeyAsync()
    {
        var run = _startCountdown;
        if (run == null) return true;
        bool go = await run.ReadyToKeyAsync();
        if (!go && run.SpeechStalled)
        {
            // The count's words never got out (Codex round 1, blocker 1), so it
            // stopped rather than key behind a warning nobody heard. Say why,
            // with a sound as well, since speech is what just failed.
            // DRAFT PROSE (#835): awaiting Noel.
            EarconPlayer.LeaderInvalidTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.countdown.not_heard"), VerbosityLevel.Critical, interrupt: true);
        }
        return go;
    }

    // ── The bare Control that skips the count ─────────────────────────────
    //
    // Two sources, never both (Codex rounds 1 and 2, B1). While this app's
    // system-wide hook is installed it swallows a JJ chord's letter and
    // passes its Control, and it runs on its own thread, so this window can
    // neither see those letters nor know where they fell between its own
    // Control press and release. Then ONLY the hook's verdict counts: it sees
    // every edge in order (BareControlGestures). The window's own recogniser
    // decides only when no hook is installed, when nothing is swallowed. A
    // Control the deciding side did not see as bare never skips.

    /// <summary>When the count now playing began, on the hook's clock, so a Control held from before it skips nothing.</summary>
    private long _countdownStartedTick;

    /// <summary>Pairs a hook verdict with this window and this count, once per gesture.</summary>
    private readonly CountdownSkipGate _skipGate = new();

    /// <summary>
    /// When this window last became active, on the hook's clock; long.MaxValue
    /// while it is not. A gesture begun before it, elsewhere or before the
    /// window left and came back, is not this window's (Codex round 3, B2).
    /// </summary>
    private long _activeSinceTick = long.MaxValue;

    /// <summary>This window's handle, as the hook reports the foreground window.</summary>
    private long OwnWindowHandle => (long)new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <summary>The window's own recogniser, used only when no hook is installed.</summary>
    private readonly BareControlPress _bareControl = new();

    /// <summary>The count that was running when the Control now held went down; only that count is skipped by it.</summary>
    private CountdownRun? _controlPressedDuring;

    /// <summary>The Windows stamp on the press of the Control now held, to ask the hook whether it saw that press.</summary>
    private int _controlDownTime;

    private bool _countdownSkipKeysHooked;

    /// <summary>
    /// Listen for the hook's verdicts, and watch every key the window sees,
    /// handled or not, so a Control held as part of Ctrl+Enter (which the
    /// window handles itself) is known to be a chord. Hooked at the first
    /// count rather than in the constructor so this seam stays in this file; a
    /// Control already held then is simply not seen, which cannot fire a skip.
    /// </summary>
    private void HookCountdownSkipKeys()
    {
        if (_countdownSkipKeysHooked) return;
        _countdownSkipKeysHooked = true;
        SystemWideKeys.BareControlReleased += OnHookBareControl;
        // Ownership is dated from the latest activation and ends at deactivation.
        // Hooked at the first count, when the window is already active, so the
        // first activation is taken from that moment instead.
        Activated += (s, e) => _activeSinceTick = Environment.TickCount64;
        Deactivated += (s, e) => _activeSinceTick = long.MaxValue;
        if (IsActive) _activeSinceTick = Environment.TickCount64;
        Closed += (s, e) => SystemWideKeys.BareControlReleased -= OnHookBareControl;
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(CountdownSkipKeyDown), handledEventsToo: true);
        AddHandler(PreviewKeyUpEvent, new KeyEventHandler(CountdownSkipKeyUp), handledEventsToo: true);
        Deactivated += (s, e) => { _bareControl.Reset(); _controlPressedDuring = null; };
    }

    /// <summary>
    /// The hook saw a bare Control. It skips this count only if this window is
    /// the active one, the count is running, and Control went down after the
    /// count began.
    /// </summary>
    private void OnHookBareControl(BareControlRelease verdict)
    {
        var run = _startCountdown;
        if (run == null || !_skipGate.HookVerdictSkips(verdict, SystemWideKeys.Installed, IsActive, OwnWindowHandle,
                _activeSinceTick, run.IsRunning, _countdownStartedTick)) return;
        run.Skip();
    }

    private static bool IsControlKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == Key.LeftCtrl || key == Key.RightCtrl;
    }

    /// <summary>The key itself, the same on its press and its release, with Alt's System wrapper taken off.</summary>
    private static int KeyIdentity(KeyEventArgs e) => (int)(e.Key == Key.System ? e.SystemKey : e.Key);

    private void CountdownSkipKeyDown(object sender, KeyEventArgs e)
    {
        bool control = IsControlKey(e);
        if (control && !e.IsRepeat && _controlPressedDuring == null)
        {
            _controlPressedDuring = StartCountdownRunning ? _startCountdown : null;
            _controlDownTime = e.Timestamp;
        }
        bool otherModifier = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        _bareControl.KeyDown(KeyIdentity(e), control, e.IsRepeat, otherModifier);
    }

    private void CountdownSkipKeyUp(object sender, KeyEventArgs e)
    {
        bool control = IsControlKey(e);
        bool bare = _bareControl.KeyUp(KeyIdentity(e), control);
        if (!control) return;
        // Every Control release ends that press, a chord's included.
        var pressedDuring = _controlPressedDuring;
        _controlPressedDuring = null;
        if (!bare) return;
        // If the hook saw any key of this press, its verdict decides, not
        // this window; only a hook that saw nothing (none, or a dead one)
        // leaves the decision here.
        if (!CountdownSkipGate.WindowMayDecide(SystemWideKeys.EverSawAKey, SystemWideKeys.LastKeyTime, _controlDownTime,
            SystemWideKeys.EverSawControlDown, SystemWideKeys.LastControlDownTime)) return;
        // Only the count that was running when Control went down, and only
        // while it is still running: a Control held from before the count
        // began, or across its end, skips nothing.
        if (pressedDuring == null || !ReferenceEquals(pressedDuring, _startCountdown) || !pressedDuring.IsRunning) return;
        pressedDuring.Skip();
    }

    /// <summary>
    /// The start has finished, keyed or not. A count still playing (the start
    /// failed or was refused) stops on the beat it had reached; one that ended
    /// is left alone.
    /// </summary>
    private void EndStartCountdown()
    {
        _startCountdown?.Cancel();
        _startCountdown = null;
    }
}
