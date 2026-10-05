// Global key probe — Sprint 48 preparation, 2026-10-05.
//
// Answers three questions BEFORE any track is briefed, because the answers
// decide whether the global JJ key and a global push-to-talk are achievable at
// all. Ruled by Noel: "probing is right and slow implementation."
//
//   1. Does a RegisterHotKey chord fire while ANOTHER application has focus?
//      (the Ctrl+Shift+J leader case)
//   2. Can the NEXT keystroke be read without taking focus away from that
//      application? (RegisterHotKey gives one chord, not the key after it —
//      so Ctrl+Shift+J then "1" depends on this)
//   3. Can a HELD chord's down AND up be seen, and SWALLOWED, while unfocused?
//      (the Ctrl+Alt+Space push-to-talk case. RegisterHotKey fires on key-down
//      only and never reports key-up, so a held PTT cannot be built on it.
//      This is the one that might not be cleanly achievable.)
//
// DELIBERATELY STANDALONE. It is not referenced by the application, shares no
// code with it, and writes nothing into %AppData%\JJFlexRadio — so a probe that
// misbehaves cannot disturb the operator's settings or a live radio session.
//
// IT NEVER TOUCHES A RADIO. No FlexLib, no network, no transmit. It observes
// keystrokes and writes a log.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GlobalKeyProbe
{
    internal static class Program
    {
        // ── the three chords under test, per Noel's 2026-10-05 ruling ──
        // Alt is what distinguishes a global chord from its in-app twin.
        private const int HK_LEADER = 1;    // Ctrl+Shift+J   (in-app: Ctrl+J)
        private const int HK_PTT = 2;       // Ctrl+Alt+Space (in-app: Ctrl+Space)
        private const int HK_LOCK = 3;      // Alt+Shift+Space(in-app: Shift+Space)

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        private const int WM_HOTKEY = 0x0312;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_SPACE = 0x20;
        private const int VK_J = 0x4A;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static StreamWriter? _log;
        private static IntPtr _hook = IntPtr.Zero;
        private static LowLevelKeyboardProc? _hookProc;   // held so it is not collected
        private static ProbeWindow? _window;

        /// <summary>
        /// While true the hook SWALLOWS the chords under test, so the focused
        /// application never sees them. Question 3 is whether this works.
        /// Toggled with Ctrl+Shift+J so both halves can be measured in one
        /// sitting without restarting.
        /// </summary>
        private static bool _swallow = true;

        /// <summary>
        /// THE FINDING THAT RESHAPED THIS PROBE, 2026-10-05 first run: a
        /// low-level hook that SWALLOWS a chord runs BEFORE Windows' hotkey
        /// dispatch, so RegisterHotKey never fires for it. Thirty-one presses
        /// of a successfully registered Alt+Shift+Space produced zero WM_HOTKEY
        /// messages. The two mechanisms are mutually exclusive the moment you
        /// swallow, so swallowing is now a COMMAND-LINE switch rather than a
        /// key toggle: run once with it and once without, and the two logs
        /// answer different questions instead of one log answering neither.
        /// </summary>
        private static bool _swallowFromCommandLine = true;

        /// <summary>
        /// How long the probe lives before releasing the hook and exiting on
        /// its own. See the comment at the timer in Main: this is a safety
        /// requirement rather than a convenience.
        /// </summary>
        private const int LifetimeSeconds = 120;

        /// <summary>
        /// Set when the leader fires; the next keystroke the hook sees is
        /// reported as the SECOND KEY and this clears. That is question 2.
        /// </summary>
        private static bool _awaitingSecondKey;

        [STAThread]
        private static void Main(string[] args)
        {
            foreach (string a in args)
            {
                if (a.Equals("--no-swallow", StringComparison.OrdinalIgnoreCase))
                    _swallowFromCommandLine = false;
            }
            _swallow = _swallowFromCommandLine;

            string dir = Path.Combine(Path.GetTempPath(), "jjflex-globalkeyprobe");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            _log = new StreamWriter(path, append: false) { AutoFlush = true };

            Say("Global key probe. Sprint 48 preparation.");
            Say("Log: " + path);
            Say("");
            Say("WHAT TO DO: leave this window alone, click into Notepad (or N3FJP),");
            Say("and press the chords below. Everything is recorded here and in the log.");
            Say("");
            Say("  Ctrl+Shift+J    the leader. Then press a DIGIT to test the second key.");
            Say("                  Also toggles swallowing on and off.");
            Say("  Ctrl+Alt+Space  push to talk. HOLD it, then release.");
            Say("  Alt+Shift+Space transmit lock toggle. Watch for a system menu.");
            Say("");
            Say(_swallowFromCommandLine
                ? "SWALLOWING ON. The focused app should NOT see these chords - and"
                  + " RegisterHotKey will NOT fire, because the hook runs first."
                : "SWALLOWING OFF (--no-swallow). The focused app WILL also see these"
                  + " chords, and RegisterHotKey SHOULD fire. Watch for a system menu.");
            Say("Run it both ways; the two logs answer different questions.");
            Say("Starting minimized so the chords are measured globally.");
            Say("");

            _window = new ProbeWindow();
            _window.CreateControl();

            // ── SELF-TERMINATION, AND IT IS A SAFETY REQUIREMENT ──
            //
            // Added 2026-10-05 after this probe trapped the operator. It
            // installs a system-wide hook that SWALLOWS keys, it starts
            // minimized, and the only way out was a keyboard it was eating.
            // Noel: "Now I can't stop the application". He also hit
            // NVDA+Shift+Space by reflex and landed in focus mode, because the
            // Space-with-modifiers family is screen-reader territory.
            //
            // A TOOL THAT INTERFERES WITH INPUT MUST RELEASE THE MACHINE BY
            // ITSELF. Not on a keystroke, because keystrokes are the thing it
            // is interfering with; not on a click, because it is minimized.
            // On a clock, unconditionally.
            var life = new System.Windows.Forms.Timer { Interval = 1000 };
            int left = LifetimeSeconds;
            life.Tick += (s, e) =>
            {
                left--;
                if (left == 30 || left == 10 || left == 5)
                    Say("probe exits in " + left + " s (hook released then)");
                if (left <= 0)
                {
                    life.Stop();
                    Say("LIFETIME REACHED - releasing the hook and exiting");
                    _window!.Close();
                }
            };
            life.Start();
            Say("SELF-EXIT: this probe quits on its own in " + LifetimeSeconds
                + " s and releases the keyboard. You do not have to close it.");

            RegisterOne(HK_LEADER, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_J, "Ctrl+Shift+J");
            RegisterOne(HK_PTT, MOD_CONTROL | MOD_ALT, VK_SPACE, "Ctrl+Alt+Space");
            RegisterOne(HK_LOCK, MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE, "Alt+Shift+Space");

            _hookProc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
            Say(_hook == IntPtr.Zero
                ? "HOOK: FAILED to install, error " + Marshal.GetLastWin32Error()
                : "HOOK: installed (sees key-down AND key-up, and can swallow)");
            Say("");

            Application.Run(_window);

            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            UnregisterHotKey(_window.Handle, HK_LEADER);
            UnregisterHotKey(_window.Handle, HK_PTT);
            UnregisterHotKey(_window.Handle, HK_LOCK);
            Say("probe ended");
            _log?.Dispose();
        }

        private static void RegisterOne(int id, uint mods, uint vk, string name)
        {
            bool ok = RegisterHotKey(_window!.Handle, id, mods, vk);
            if (ok)
            {
                Say("HOTKEY: " + name + " registered");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED. That is a RESULT,
                // not a probe failure: it means another running program already
                // owns the chord, which is exactly what we are trying to find
                // out before choosing one.
                Say("HOTKEY: " + name + " REFUSED, error " + err
                    + (err == 1409 ? " (already registered by another program - this is a finding)" : ""));
            }
        }

        /// <summary>The foreground window, so the log says what had focus.</summary>
        private static string Foreground()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "none";
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out uint pid);
            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; }
            catch { proc = "pid" + pid; }
            string title = sb.ToString();
            if (title.Length > 40) title = title.Substring(0, 40) + "...";
            return proc + " \"" + title + "\"";
        }

        /// <summary>
        /// The key code of a chord we have seen go DOWN and not yet seen come
        /// UP, or zero.
        /// </summary>
        /// <remarks>
        /// <b>THE BUG THIS FIXES WOULD HAVE LEFT A TRANSMITTER KEYED, and it
        /// was found by a reviewer reading this file rather than by running
        /// it.</b> The original <c>IsOurs</c> decided whether a keystroke was
        /// ours by reading the LIVE modifier state at that instant. On release
        /// an operator usually lifts a modifier first, so by the time Space
        /// goes up the modifiers are already gone, the test returns false, and
        /// <b>the UP edge is never seen.</b>
        ///
        /// The second probe run proves it: <b>110 key-down events and exactly
        /// ONE key-up.</b> The first run's single clean UP is what made the
        /// mechanism look sound, and two greps of the log counted what was
        /// expected rather than what was there.
        ///
        /// A push-to-talk built the same way keys the radio on the down edge
        /// and never hears the release. <b>So a release is matched by KEY CODE
        /// ALONE</b> - whatever the modifiers are doing by then - which is also
        /// how a real PTT must work.
        /// </remarks>
        private static uint _chordDown;

        private static bool IsOurs(uint vk, bool ctrl, bool alt, bool shift)
        {
            if (vk == VK_J && ctrl && shift) return true;
            if (vk == VK_SPACE && ctrl && alt) return true;
            if (vk == VK_SPACE && alt && shift) return true;
            return false;
        }

        /// <summary>
        /// True when this edge belongs to a chord under test. A DOWN must match
        /// the modifiers; an UP matches the key code of whatever went down,
        /// because the modifiers are routinely released first.
        /// </summary>
        private static bool IsOursEdge(uint vk, bool down, bool ctrl, bool alt, bool shift)
        {
            if (down)
            {
                if (!IsOurs(vk, ctrl, alt, shift)) return false;
                _chordDown = vk;
                return true;
            }

            if (_chordDown != 0 && vk == _chordDown)
            {
                _chordDown = 0;
                return true;
            }
            return IsOurs(vk, ctrl, alt, shift);
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;

            // QUESTION 2: the keystroke after the leader, read with no focus change.
            if (_awaitingSecondKey && down && info.vkCode != 0x11 && info.vkCode != 0x12 && info.vkCode != 0x10)
            {
                _awaitingSecondKey = false;
                Say("  Q2 SECOND KEY: vk=0x" + info.vkCode.ToString("X2")
                    + " (" + Describe(info.vkCode) + ") read with focus still on " + Foreground()
                    + (_swallow ? " - SWALLOWED" : " - passed through"));
                if (_swallow) return (IntPtr)1;
            }

            if (IsOursEdge(info.vkCode, down, ctrl, alt, shift))
            {
                // THE HOOK IS THE PRIMARY MECHANISM, not a second opinion. It
                // sees both edges, sees auto-repeat, works unfocused and can
                // swallow - and when it swallows, RegisterHotKey is dead. So
                // the leader is armed from HERE as well, so Q2 can be measured
                // in a swallowing run where no WM_HOTKEY will ever arrive.
                if (down && info.vkCode == VK_J && ctrl && shift)
                {
                    _awaitingSecondKey = true;
                    Say("  HOOK: leader seen, armed for the next key"
                        + (_swallow ? " (hotkey will NOT fire - we swallowed it)" : ""));
                }

                // QUESTION 3: both edges of a held chord, seen while unfocused.
                string edge = down ? "DOWN" : up ? "UP" : "msg" + msg;
                Say("  HOOK " + edge + ": " + Describe(info.vkCode)
                    + " ctrl=" + ctrl + " alt=" + alt + " shift=" + shift
                    + " focus=" + Foreground()
                    + (_swallow ? " - SWALLOWED" : " - passed through"));
                if (_swallow) return (IntPtr)1;
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static string Describe(uint vk) => vk switch
        {
            VK_SPACE => "Space",
            VK_J => "J",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x1B => "Escape",
            _ => "vk0x" + vk.ToString("X2"),
        };

        private sealed class ProbeWindow : Form
        {
            private readonly TextBox _out;

            public ProbeWindow()
            {
                Text = "JJ Flexible global key probe";
                Width = 900;
                Height = 560;
                StartPosition = FormStartPosition.CenterScreen;
                // Minimized on purpose, Noel 2026-10-05: "you might want to
                // start this minimized to test global itself". A probe window
                // holding focus is not measuring a global key at all - the
                // first run logged focus=globalkeyprobe for its own presses.
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = true;
                _out = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Dock = DockStyle.Fill,
                    Font = new System.Drawing.Font("Consolas", 10f),
                    WordWrap = false,
                };
                AccessibleName = "Global key probe log";
                _out.AccessibleName = "Probe log";
                Controls.Add(_out);
                Lines = new List<string>();
            }

            public List<string> Lines { get; }

            public void Append(string s)
            {
                Lines.Add(s);
                if (IsHandleCreated)
                    BeginInvoke(new Action(() => { _out.AppendText(s + Environment.NewLine); }));
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    string which = id switch
                    {
                        HK_LEADER => "Ctrl+Shift+J (leader)",
                        HK_PTT => "Ctrl+Alt+Space (push to talk)",
                        HK_LOCK => "Alt+Shift+Space (transmit lock)",
                        _ => "id " + id,
                    };
                    Say("Q1 HOTKEY FIRED: " + which + " while focus was on " + Foreground());

                    if (id == HK_LEADER)
                    {
                        _awaitingSecondKey = true;
                        Say("  leader armed - press a digit now.");
                    }
                    if (id == HK_PTT)
                        Say("  NOTE: RegisterHotKey reports DOWN only. Key-up for a held PTT"
                            + " has to come from the hook lines above.");
                }
                base.WndProc(ref m);
            }
        }

        private static void Say(string s)
        {
            string line = Clock.Elapsed.TotalMilliseconds.ToString("F0").PadLeft(7) + "  " + s;
            _log?.WriteLine(line);
            _window?.Append(line);
        }
    }
}
