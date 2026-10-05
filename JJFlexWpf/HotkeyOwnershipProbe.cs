using System;
using System.Runtime.InteropServices;
using Radios;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf
{
    /// <summary>
    /// Asks Windows whether another program has already claimed a chord
    /// through <c>RegisterHotKey</c> — the one good use left for that API
    /// after the 2026-10-05 probe discarded it as a mechanism (#307, #689).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Detector only, never the mechanism.</b> The probe measured that a
    /// low-level hook which swallows a key runs BEFORE Windows' hotkey
    /// dispatch, so <c>WM_HOTKEY</c> never arrives for a chord the hook
    /// eats, and <c>RegisterHotKey</c> reports key-down only, so a held push
    /// to talk could never have been built on it. What it still does well is
    /// REFUSE: error 1409, <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>, is Windows
    /// saying another program owns the chord. This registers, reads the
    /// answer, and unregisters at once, so nothing is kept and nothing can
    /// collide with our own hook.
    /// </para>
    /// <para>
    /// <b>The blind spot, which the dialog states rather than hides.</b> It
    /// sees only programs that ALSO used <c>RegisterHotKey</c>. A program
    /// using a keyboard hook is invisible to it — NVDA, JAWS and this
    /// application among them — so it would not have caught
    /// <c>NVDA+Shift+Space</c>, which Noel hit by reflex mid-probe. A clear
    /// result means "no RegisterHotKey owner", never "free". Saying otherwise
    /// is a negative result with no positive control, in a dialog.
    /// </para>
    /// </remarks>
    public static class HotkeyOwnershipProbe
    {
        public enum Verdict
        {
            /// <summary>Registration succeeded and was undone: nobody holds it that way.</summary>
            NoRegisterHotKeyOwner,

            /// <summary>Error 1409: another program has this chord registered.</summary>
            OwnedByAnotherProgram,

            /// <summary>Registration failed for some other reason; the answer is unknown.</summary>
            CouldNotCheck,
        }

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        // An id nobody else in this process uses: we register nothing else.
        private const int ProbeId = 0x4A4A;   // "JJ"

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// Try the chord. Call from any thread; with a null window the hotkey
        /// is tied to the calling thread and is released before this returns.
        /// </summary>
        public static Verdict Probe(WinFormsKeys chord, out int win32Error)
        {
            win32Error = 0;
            if (chord == WinFormsKeys.None) return Verdict.CouldNotCheck;

            uint mods = MOD_NOREPEAT;
            if ((chord & WinFormsKeys.Control) != 0) mods |= MOD_CONTROL;
            if ((chord & WinFormsKeys.Alt) != 0) mods |= MOD_ALT;
            if ((chord & WinFormsKeys.Shift) != 0) mods |= MOD_SHIFT;
            uint vk = SystemWideChord.VirtualKeyOf(chord);
            if (vk == 0) return Verdict.CouldNotCheck;

            try
            {
                if (RegisterHotKey(IntPtr.Zero, ProbeId, mods, vk))
                {
                    UnregisterHotKey(IntPtr.Zero, ProbeId);
                    return Verdict.NoRegisterHotKeyOwner;
                }
                win32Error = Marshal.GetLastWin32Error();
                return win32Error == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? Verdict.OwnedByAnotherProgram
                    : Verdict.CouldNotCheck;
            }
            catch (Exception)
            {
                return Verdict.CouldNotCheck;
            }
        }
    }
}
