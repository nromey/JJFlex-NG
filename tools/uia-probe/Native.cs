using System.Runtime.InteropServices;
using System.Text;

namespace JJFlex.UiaProbe;

/// <summary>
/// The Win32 layer. Two things live here and both are load-bearing.
///
/// <para><b>Foregrounding.</b> <see cref="Force"/> is the AttachThreadInput
/// dance, carried over verbatim from the scratchpad <c>uia.ps1</c>. Windows
/// refuses SetForegroundWindow from a process that does not own the foreground,
/// so a naive call silently flashes the taskbar button and returns false.
/// Attaching our input queue to both the current foreground thread AND the
/// target thread makes the call legal. This is the non-obvious part of the
/// original probe and the reason it worked when simpler attempts did not.</para>
///
/// <para><b>Synthetic keystrokes.</b> <see cref="SendKeyEvent"/> uses SendInput,
/// which injects at the same level as a real keyboard. That matters more here
/// than it usually would: WPF reads modifier state from the real keyboard
/// (<c>Keyboard.Modifiers</c>), so a chord delivered by PostMessage arrives with
/// no modifiers attached and a Ctrl+J test would silently pass as a bare J.
/// SendInput is the only method that presses the key the way an operator
/// does — which is the entire premise of this tool.</para>
/// </summary>
internal static class Native
{
    // ─────────────────────────── windows ───────────────────────────

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Bring a window to the foreground, for real. Returns false when Windows
    /// still refused — callers must treat that as "the keystroke would have
    /// gone somewhere else" and abort, never as a warning to press on through.
    /// </summary>
    internal static bool Force(IntPtr h)
    {
        ShowWindow(h, SW_RESTORE);
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        uint tgtThread = GetWindowThreadProcessId(h, out _);

        bool attachedFg = fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        bool attachedTgt = tgtThread != myThread && AttachThreadInput(myThread, tgtThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attachedTgt) AttachThreadInput(myThread, tgtThread, false);
            if (attachedFg) AttachThreadInput(myThread, fgThread, false);
        }

        // Trust the observation, not the return value: SetForegroundWindow
        // reports success in cases where focus did not actually move.
        for (int i = 0; i < 20; i++)
        {
            if (GetForegroundWindow() == h) return true;
            Thread.Sleep(25);
        }
        return GetForegroundWindow() == h;
    }

    internal static List<IntPtr> TopLevel(uint pid, bool visibleOnly)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid && (!visibleOnly || IsWindowVisible(h))) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    internal static string Text(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        var sb = new StringBuilder(n + 2);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // ─────────────────────────── keyboard ───────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MAPVK_VK_TO_VSC = 0;

    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12;      // Alt
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_RWIN = 0x5C;

    /// <summary>Keys whose scan code needs the extended-key flag.</summary>
    private static bool IsExtended(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,        // PageUp PageDown End Home
        0x25 or 0x26 or 0x27 or 0x28 => true,        // Left Up Right Down
        0x2D or 0x2E => true,                        // Insert Delete
        0x2C => true,                                // PrintScreen
        0x90 => true,                                // NumLock
        0x5B or 0x5C or 0x5D => true,                // LWin RWin Apps
        _ => false,
    };

    internal static void SendKeyEvent(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = GetMessageExtraInfo(),
                },
            },
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// True once a command that genuinely injects input has started.
    ///
    /// <para>This exists to keep a promise about permissions. Synthetic input
    /// is the one thing this tool does that can take the operator's keyboard,
    /// and it is gated accordingly — so the read-only commands must not touch
    /// SendInput even incidentally. Without this flag they did:
    /// <see cref="ReleaseAllModifiers"/> runs from Main's finally block on every
    /// invocation, and it would have sent a keyup for any modifier that happened
    /// to be down. On a clean desktop that sends nothing, but "usually sends
    /// nothing" is not the same claim as "cannot inject", and only the second
    /// one is worth making.</para>
    ///
    /// <para>So `jjprobe windows`, `tree`, `focus`, `watch`, `inventory`,
    /// `unbound`, `expand` and `altcheck` are now provably incapable of
    /// injecting anything, and only `press` and `sweep` arm this.</para>
    /// </summary>
    internal static bool InjectionArmed { get; set; }

    /// <summary>
    /// Force every modifier up, regardless of what we think we sent.
    ///
    /// Called before a sweep starts, after every chord, and from a
    /// ProcessExit / Ctrl+C handler. A stuck Ctrl or Alt after a crashed probe
    /// leaves the operator's whole desktop in a modified state, and an operator
    /// who cannot see the screen has no way to notice it happened — every
    /// subsequent keystroke just does the wrong thing. This is the single most
    /// important safety property in the tool.
    /// </summary>
    internal static void ReleaseAllModifiers()
    {
        if (!InjectionArmed) return;
        foreach (ushort vk in new ushort[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN })
        {
            // Only release what is actually down: sending a spurious keyup for
            // Alt on its own opens the menu bar of whatever is focused.
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) SendKeyEvent(vk, up: true);
        }
    }

    /// <summary>Names of any modifiers currently physically or synthetically held.</summary>
    internal static string[] HeldModifiers()
    {
        var held = new List<string>();
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) held.Add("Shift");
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) held.Add("Ctrl");
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) held.Add("Alt");
        if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) held.Add("Win");
        if ((GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) held.Add("Win");
        return held.ToArray();
    }
}
