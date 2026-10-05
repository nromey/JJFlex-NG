using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using JJTrace;
using Radios;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf
{
    /// <summary>
    /// The keys that work while JJ Flexible does NOT have focus (#307): the
    /// JJ key, push to talk and the transmit lock, reached from a contest
    /// logger or any other program, and kept from that program.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism is a WH_KEYBOARD_LL hook, measured rather than
    /// chosen</b> (<c>tools/globalkeyprobe</c>, 2026-10-05): it fires with
    /// another process focused, it sees DOWN and UP and the ~30 ms repeat
    /// storm between them, and it can swallow — which <c>RegisterHotKey</c>
    /// cannot do and a key-state poll cannot do either. Swallowing is not
    /// optional: an unswallowed Space lands in the logger's entry field.
    /// </para>
    /// <para>
    /// <b>The hook lives on <see cref="KeyboardHookThread"/>, the third
    /// consumer after <see cref="HelpLauncher"/> and
    /// <see cref="CwCtrlInterrupt"/>, never on the UI thread (#402).</b>
    /// Windows delivers a low-level hook's callback through the pump of the
    /// thread that installed it, and a blocked installing thread stalls EVERY
    /// keystroke on the machine until LowLevelHooksTimeout, after which
    /// Windows silently removes the hook. For a blind operator whose screen
    /// reader is keyboard-driven that removes every route out; it happened
    /// three times on 2026-08-29. So the callback here decides in
    /// microseconds from a snapshot, posts its work to the UI dispatcher with
    /// <c>BeginInvoke</c>, and never waits on anything.
    /// </para>
    /// <para>
    /// <b>A missed key-up fails to UNKEYED.</b> The decision
    /// (<see cref="SystemWideKeyDecider"/>) matches a release by key code
    /// alone, because an operator lifts a modifier before the main key — the
    /// probe's second run logged 110 downs and ONE up for exactly that
    /// reason. Behind it, <see cref="SystemWidePttWatch"/> polls the
    /// operating system's own key state on a thread-pool timer while a hold
    /// is in flight, posts the release itself when no up edge ever came, and
    /// drops the carrier directly — the way <see cref="TransmitKillSwitch"/>
    /// does — when a posted release goes unhonoured because the UI thread is
    /// not processing it. #307 is explicit that this is the only binding in
    /// the product that puts RF on the air from another application.
    /// </para>
    /// <para>
    /// <b>The machine is released without a keystroke.</b> Teardown runs on
    /// the hook thread at shutdown and at process exit through
    /// <see cref="KeyboardHookThread"/>; an exception inside the callback
    /// marks the hook faulted, passes every later key straight through, posts
    /// an unkey if a hold was in flight, and asks the hook thread to unhook.
    /// The probe that trapped the operator offered no exit that did not run
    /// through the keyboard it was eating; this one needs none.
    /// </para>
    /// <para>
    /// <b>Screen readers hooking the same chord.</b> Windows calls low-level
    /// hooks newest-first, so whichever of us installed later sees the key
    /// first; if the reader swallows it we never see it, and if we swallow it
    /// the reader never does. Neither side can tell the other it did so. That
    /// is why the dialog's conflict report ends with "press it": it is the
    /// only test that sees a hook-based owner.
    /// </para>
    /// </remarks>
    public static class SystemWideKeys
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc? _hookProc;   // held so the unmanaged side keeps a live target
        private static IntPtr _hookHandle = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        // ── state ────────────────────────────────────────────────────────

        /// <summary>
        /// Guards the decider and the watch. Both are touched from the hook
        /// thread and from the watch timer's pool thread; every hold is a
        /// handful of comparisons.
        /// </summary>
        private static readonly object _gate = new object();
        private static readonly SystemWideKeyDecider _decider = new SystemWideKeyDecider();
        private static readonly SystemWidePttWatch _watch = new SystemWidePttWatch();
        private static System.Threading.Timer? _watchTimer;
        private static bool _watchRunning;

        private static volatile SystemWideKeySet _current = SystemWideKeySet.Off;
        private static Dispatcher? _ui;
        private static bool _installPosted;
        private static volatile bool _faulted;
        private static readonly int _ownPid = Environment.ProcessId;

        /// <summary>The chords in force right now.</summary>
        public static SystemWideKeySet Current => _current;

        /// <summary>True once the hook is actually installed (set on the hook thread).</summary>
        public static bool Installed => _hookHandle != IntPtr.Zero;

        // ── what the host wires. All run on the UI dispatcher. ───────────

        /// <summary>The live PTT safety controller, or null when no radio is powered.</summary>
        public static Func<PttSafetyController?>? PttControllerSource { get; set; }

        /// <summary>
        /// A push-to-talk edge, true for down and false for up, fed into the
        /// SAME pipeline the in-window Ctrl+Space uses — repeat guard, JAWS
        /// absorber, controller — so there is one PTT path with two doors.
        /// </summary>
        public static Action<bool>? PttEdge { get; set; }

        /// <summary>The transmit lock toggle, as Shift+Space does it inside the window.</summary>
        public static Action? ToggleLock { get; set; }

        /// <summary>The system-wide JJ key went down: play the tone and say the word, as Ctrl+J does.</summary>
        public static Action? LeaderArmed { get; set; }

        /// <summary>Escape while the layer waited: close it as the in-window Escape does.</summary>
        public static Action? LeaderCancelled { get; set; }

        /// <summary>
        /// The key after the JJ key, with its modifiers, and whether another
        /// program had the keyboard when it was pressed.
        /// </summary>
        public static Action<WinFormsKeys, bool>? LeaderKey { get; set; }

        /// <summary>The help-armed layer was let go by a key that was not H, slash or Escape.</summary>
        public static Action? LeaderHelpLetGo { get; set; }

        // ── install and configure ────────────────────────────────────────

        /// <summary>
        /// Install the hook, once, from anywhere. The actual
        /// <c>SetWindowsHookEx</c> runs on <see cref="KeyboardHookThread"/>'s
        /// pump; this only hands it over. The host's delegates may be set
        /// before or after.
        /// </summary>
        /// <param name="set">The chords to honour from the first keystroke.</param>
        /// <param name="ui">The dispatcher every action is posted to.</param>
        public static void Install(SystemWideKeySet set, Dispatcher ui)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            Apply(set);

            if (_installPosted)
                return;
            _installPosted = true;

            KeyboardHookThread.InstallHook(
                "SystemWideKeys (JJ key, push to talk and transmit lock from any program)",
                installOnHookThread: InstallOnHookThread,
                unhookOnHookThread: UnhookOnHookThread);
        }

        /// <summary>
        /// Put a new set of chords in force. Live, from the dialog's OK; a
        /// hold in flight keeps its key code so its release still matches.
        /// Also publishes the chords to the Hotkey Editor's reserved-key
        /// check, so an in-app command cannot be bound to a key the hook
        /// would eat before it arrived.
        /// </summary>
        public static void Apply(SystemWideKeySet set)
        {
            set ??= SystemWideKeySet.Off;
            _current = set;
            lock (_gate)
            {
                _decider.Set = set;
            }
            KeyInventory.SystemWideReservation = ReservationFor;
            Tracing.TraceLine("SystemWideKeys: " + (set.Enabled ? "on" : "off")
                + "; leader " + Describe(set.Leader)
                + ", push to talk " + Describe(set.PushToTalk)
                + ", transmit lock " + Describe(set.TransmitLock), TraceLevel.Info);
        }

        private static string Describe(WinFormsKeys k) =>
            k == WinFormsKeys.None ? "unassigned" : KeyManifest.FormatKey(k);

        private static string? ReservationFor(WinFormsKeys k)
        {
            var set = _current;
            if (!set.Enabled) return null;
            var role = set.RoleOf(k);
            if (role == null) return null;
            return Lexicon.Get("settings.systemwide_keys.reserved_in_app", ("role", RoleName(role.Value)));
        }

        /// <summary>The operator-facing name of a role, from the lexicon.</summary>
        public static string RoleName(SystemWideRole role) => role switch
        {
            SystemWideRole.Leader => Lexicon.Get("settings.systemwide_keys.role.leader"),
            SystemWideRole.PushToTalk => Lexicon.Get("settings.systemwide_keys.role.push_to_talk"),
            _ => Lexicon.Get("settings.systemwide_keys.role.transmit_lock"),
        };

        /// <summary>
        /// The in-app layer answered an unknown key and is waiting for H, the
        /// slash key or Escape (#303). Called from the UI thread after the
        /// second key has been dispatched, so the hook claims exactly those
        /// three next and lets anything else go — the in-window behaviour.
        /// </summary>
        public static void NoteHelpArmed(bool armed)
        {
            lock (_gate) { _decider.SetHelpArmed(armed); }
        }

        // ── the hook thread ──────────────────────────────────────────────

        /// <summary>Runs ONLY on the hook thread.</summary>
        private static void InstallOnHookThread()
        {
            if (_hookHandle != IntPtr.Zero)
                return;

            try
            {
                _hookProc = HookCallback;
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
                    GetModuleHandle(module?.ModuleName), 0);

                if (_hookHandle == IntPtr.Zero)
                {
                    Tracing.TraceLine(
                        $"SystemWideKeys: SetWindowsHookEx failed, error {Marshal.GetLastWin32Error()} — " +
                        "the JJ key, push to talk and transmit lock will work only inside the window.",
                        TraceLevel.Error);
                    _hookProc = null;
                }
                else
                {
                    Tracing.TraceLine("SystemWideKeys: hook installed on the dedicated hook thread", TraceLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SystemWideKeys.Install: {ex.Message}", TraceLevel.Error);
                _hookProc = null;
            }
        }

        /// <summary>
        /// Teardown, on the hook thread, at shutdown and process exit. A hold
        /// in flight is released on the way out: the hook that saw the down
        /// is going away, so nothing else would ever see the up.
        /// </summary>
        private static void UnhookOnHookThread()
        {
            bool releasePtt;
            lock (_gate)
            {
                releasePtt = _decider.PttHeld;
                _decider.Reset();
                _watch.Reset();
                StopWatchLocked();
            }
            if (releasePtt) Post(() => PttEdge?.Invoke(false));

            if (_hookHandle == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hookProc = null;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || _faulted)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            try
            {
                long msg = (long)wParam;
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (!isDown && !isUp)
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Live modifier state from Windows, read only to recognise a
                // DOWN. Injected input counts (JAWS re-injects), as it does
                // for CwCtrlInterrupt: filtering it would exempt our audience.
                var held = new SystemWideModifiers(
                    (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
                    (GetAsyncKeyState(VK_MENU) & 0x8000) != 0,
                    (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0);

                SystemWideDecision d;
                long now = Environment.TickCount64;
                lock (_gate)
                {
                    d = _decider.Decide(data.vkCode, isDown, held);
                    switch (d.Action)
                    {
                        case SystemWideAction.PttDown:
                            _watch.NoteDown(now);
                            StartWatchLocked();
                            break;
                        case SystemWideAction.PttUp:
                            _watch.NoteUp(now);
                            StartWatchLocked();   // stays up until the release is seen honoured
                            break;
                    }
                }

                if (d.HelpLetGo) Post(() => LeaderHelpLetGo?.Invoke());

                switch (d.Action)
                {
                    case SystemWideAction.PttDown:
                        Tracing.TraceLine("SystemWideKeys: push to talk DOWN from " + ForegroundName(), TraceLevel.Info);
                        Post(() => PttEdge?.Invoke(true));
                        break;
                    case SystemWideAction.PttUp:
                        Tracing.TraceLine("SystemWideKeys: push to talk UP", TraceLevel.Info);
                        Post(() => PttEdge?.Invoke(false));
                        break;
                    case SystemWideAction.LockToggle:
                        Tracing.TraceLine("SystemWideKeys: transmit lock toggled from " + ForegroundName(), TraceLevel.Info);
                        Post(() => ToggleLock?.Invoke());
                        break;
                    case SystemWideAction.LeaderArmed:
                        Tracing.TraceLine("SystemWideKeys: JJ key from " + ForegroundName(), TraceLevel.Info);
                        Post(() => LeaderArmed?.Invoke());
                        break;
                    case SystemWideAction.LeaderCancelled:
                        Post(() => LeaderCancelled?.Invoke());
                        break;
                    case SystemWideAction.LeaderKey:
                        {
                            bool fromOutside = !ForegroundIsOurs();
                            var key = d.Key;
                            Tracing.TraceLine("SystemWideKeys: second key " + key
                                + (fromOutside ? " from " + ForegroundName() : " with our own window focused"), TraceLevel.Info);
                            Post(() => LeaderKey?.Invoke(key, fromOutside));
                        }
                        break;
                }

                if (d.Swallow)
                    return (IntPtr)1;
            }
            catch (Exception ex)
            {
                Fault(ex);
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// An exception in the callback. The machine is released first — every
        /// later key passes straight through — a hold in flight is unkeyed,
        /// and the hook thread is asked to unhook once this callback has
        /// returned. Nothing here waits.
        /// </summary>
        private static void Fault(Exception ex)
        {
            _faulted = true;
            bool releasePtt;
            lock (_gate)
            {
                releasePtt = _decider.PttHeld;
                _decider.Reset();
            }
            Tracing.TraceLine("SystemWideKeys: exception in the hook callback; passing every key through from now on and unhooking. "
                + ex, TraceLevel.Error);
            if (releasePtt) Post(() => PttEdge?.Invoke(false));
            try
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(UnhookOnHookThread));
            }
            catch (Exception post)
            {
                Tracing.TraceLine("SystemWideKeys: could not post the unhook: " + post.Message, TraceLevel.Error);
            }
        }

        // ── the fail-safe poll (thread pool) ─────────────────────────────

        private static void StartWatchLocked()
        {
            if (_watchRunning) return;
            _watchRunning = true;
            if (_watchTimer == null)
                _watchTimer = new System.Threading.Timer(WatchTick, null,
                    SystemWidePttWatch.PollMs, SystemWidePttWatch.PollMs);
            else
                _watchTimer.Change(SystemWidePttWatch.PollMs, SystemWidePttWatch.PollMs);
        }

        private static void StopWatchLocked()
        {
            _watchRunning = false;
            _watchTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        private static void WatchTick(object? _)
        {
            try
            {
                PttSafetyController? controller = null;
                try { controller = PttControllerSource?.Invoke(); } catch { }
                bool holding = controller != null
                               && controller.State == PttSafetyController.PttState.PttHold;

                var set = _current;
                bool physicallyDown = set.PushToTalk != WinFormsKeys.None
                    && PhysicalKeyState.IsDown((int)SystemWideChord.VirtualKeyOf(set.PushToTalk));

                SystemWidePttWatch.Verdict verdict;
                bool stop;
                lock (_gate)
                {
                    verdict = _watch.Poll(Environment.TickCount64, physicallyDown, holding);
                    if (verdict == SystemWidePttWatch.Verdict.UnkeyMissedRelease)
                        _decider.ForceReleasePtt();
                    stop = !_watch.Busy;
                    if (stop) StopWatchLocked();
                }

                switch (verdict)
                {
                    case SystemWidePttWatch.Verdict.UnkeyMissedRelease:
                        Tracing.TraceLine("SystemWideKeys: the push to talk key has read physically UP for "
                            + SystemWidePttWatch.MissedReleaseMs + " ms with no release seen by the hook — "
                            + "posting the release ourselves (#307: a missed key-up fails to unkeyed).",
                            TraceLevel.Warning);
                        Post(() => PttEdge?.Invoke(false));
                        break;

                    case SystemWidePttWatch.Verdict.KillReleaseNotHonoured:
                        Tracing.TraceLine("SystemWideKeys: a push to talk release was posted "
                            + SystemWidePttWatch.ReleaseGraceMs + " ms ago and the controller still reports a held transmit — "
                            + "the UI thread is not processing it. Dropping the carrier from the watch thread.",
                            TraceLevel.Error);
                        controller?.KillTransmitNow("system-wide push to talk watchdog (release not honoured)",
                            Lexicon.Get("audio.ptt.systemwide_watchdog_unkeyed"),
                            Radios.Speech.SpeechSubject.SystemWidePttWatchdog);
                        break;

                    case SystemWidePttWatch.Verdict.KillHoldTooLong:
                        Tracing.TraceLine("SystemWideKeys: push to talk has been held for "
                            + SystemWidePttWatch.MaxHoldMs / 1000 + " s. Dropping the carrier.", TraceLevel.Error);
                        Post(() => PttEdge?.Invoke(false));
                        controller?.KillTransmitNow("system-wide push to talk watchdog (hold too long)",
                            Lexicon.Get("audio.ptt.systemwide_watchdog_unkeyed"),
                            Radios.Speech.SpeechSubject.SystemWidePttWatchdog);
                        break;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("SystemWideKeys: watch tick failed: " + ex.Message, TraceLevel.Error);
            }
        }

        // ── helpers ──────────────────────────────────────────────────────

        /// <summary>Hand work to the UI dispatcher and return at once. Never Invoke.</summary>
        private static void Post(Action action)
        {
            var ui = _ui;
            if (ui == null) return;
            try
            {
                ui.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("SystemWideKeys: posted action failed: " + ex.Message, TraceLevel.Error);
                    }
                }));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("SystemWideKeys: could not post to the UI dispatcher: " + ex.Message, TraceLevel.Error);
            }
        }

        private static bool ForegroundIsOurs()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                return pid == (uint)_ownPid;
            }
            catch { return false; }
        }

        /// <summary>The focused process's name, for the trace. Cheap and never throws.</summary>
        private static string ForegroundName()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return "no foreground window";
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == (uint)_ownPid) return "our own window";
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return "an unidentified program"; }
        }
    }
}
