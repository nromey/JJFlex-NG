using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;  // For Help.ShowHelp

namespace JJFlexWpf
{
    public static class HelpLauncher
    {
        private static string _helpFilePath;

        // Microsoft's CHM viewer (hh.exe) doesn't bind Escape — only Alt+F4 / X.
        // We install a low-level keyboard hook that posts WM_CLOSE when Escape
        // is pressed *and* the foreground window is the HtmlHelp viewer.
        // Honors the project dialog-escape rule for the F1 help path.
        //
        // The hook lives on KeyboardHookThread's dedicated pump, never the UI
        // thread (#402): Windows delivers a WH_KEYBOARD_LL callback via the
        // installing thread's message loop, so a hook installed from the UI
        // thread stalls every keystroke on the machine whenever that thread
        // blocks. The callback itself stays non-blocking — foreground-window
        // class check, then PostMessage, which posts and returns; the
        // synchronous send would couple this thread to the CHM viewer's pump.
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const uint WM_CLOSE = 0x0010;
        private const uint VK_ESCAPE = 0x1B;
        private const string HtmlHelpClassName = "HH Parent";

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc _hookProc; // keep delegate alive for the unmanaged callback
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
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Every value names a page in its outline folder (#826): the build writes
        // pages/<Folder>/<Page>.htm from docs/help/md/<Folder>/<Page>.md. A
        // Radios.Tests check reads this map from source and fails on a page that
        // does not exist or that docs/help/outline.md does not list as written.
        private static readonly Dictionary<string, string> ContextMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "FreqDisplay", "pages/JJFlexibleHome/TuningAndFrequency.htm" },
            { "ScreenFieldsDSP", "pages/Receiver/FiltersAndDsp.htm" },
            { "ScreenFieldsTX", "pages/Transmit/PttAndTransmission.htm" },
            { "ScreenFieldsAudio", "pages/AudioWorkshop/AudioWorkshop.htm" },
            { "ScreenFieldsReceiver", "pages/Receiver/FiltersAndDsp.htm" },
            { "ScreenFieldsAntenna", "pages/Receiver/FiltersAndDsp.htm" },
            { "AudioWorkshop", "pages/AudioWorkshop/AudioWorkshop.htm" },
            { "EarconExplorer", "pages/Sounds/EarconExplorer.htm" },
            { "MeterTones", "pages/Sounds/MeterTones.htm" },
            { "LogPanel", "pages/Logging/Logging.htm" },
            { "SettingsDialog", "pages/Settings/Settings.htm" },
            // "CommandFinder" used to point at the keyboard reference because
            // the Help > Keyboard Reference menu item borrowed the key — which
            // left the Command Finder dialog itself with no F1 page (#40).
            // Each has its own key now.
            { "CommandFinder", "pages/KeyboardReference/CommandFinder.htm" },
            { "KeyboardReference", "pages/KeyboardReference/KeyboardReference.htm" },
            { "LeaderKey", "pages/KeyboardReference/JJKey.htm" },
            { "WelcomeDialog", "pages/GettingStarted/GettingStarted.htm" },
            { "ConnectDialog", "pages/Troubleshooting/ConnectionTroubleshooting.htm" },
            { "WhatsNew", "pages/WhatsNew.htm" },
            { "KnowYourRadio", "pages/KnowYourRadio/KnowYourRadio.htm" },
            // Sprint 30 Track E: the no-radio Home page and the Diagnostics
            // tab each get a real F1 destination.
            { "RescueHome", "pages/JJFlexibleHome/HomeWithoutARadio.htm" },
            { "DiagnosticLog", "pages/Troubleshooting/DiagnosticLog.htm" },
            // Sprint 48 Track B (#307): the system-wide keys are documented
            // on the keyboard reference, in their own section, until the
            // reference is split and they get their own page (#826).
            { "SystemWideKeys", "pages/KeyboardReference/KeyboardReference.htm" },
            // Tape Deck track 4 (#787): the recordings dialog's own page.
            { "RecordedQsos", "pages/Audio/RecordedQsos.htm" },
            // The Settings Network tab's Help button (#826, open item 6, ruled
            // yes 2026-10-27): the three networking pages open in the help like
            // every other page, rather than as loose Markdown files beside the
            // exe. SessionStatusMessages.HelpDocFor returns one of these keys.
            { "NetworkDiagnostics", "pages/Networking/NetworkDiagnostics.htm" },
            { "NetworkManualPortForwarding", "pages/Networking/ManualPortForwarding.htm" },
            { "NetworkUpnp", "pages/Networking/Upnp.htm" },
        };

        /// <summary>True once the install has been handed to the hook thread.</summary>
        private static bool _installPosted;

        public static void Initialize()
        {
            _helpFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JJFlexRadio.chm");

            if (_installPosted)
                return;
            _installPosted = true;

            // The actual SetWindowsHookEx must run on the dedicated pumped
            // thread (#402) — see the class comment. Fire-and-forget: startup
            // never waits on the hook thread.
            KeyboardHookThread.InstallHook(
                "HelpLauncher (Escape closes the CHM viewer)",
                installOnHookThread: InstallEscapeHook,
                unhookOnHookThread: UnhookEscapeHook);
        }

        /// <summary>Runs ONLY on the hook thread.</summary>
        private static void InstallEscapeHook()
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
                    Trace.WriteLine(
                        $"HelpLauncher: SetWindowsHookEx failed, error {Marshal.GetLastWin32Error()} — " +
                        "Escape will not close the CHM viewer (Alt+F4 still works).");
                    _hookProc = null;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"HelpLauncher.InstallEscapeHook: {ex.Message}");
                _hookProc = null;
            }
        }

        /// <summary>Teardown, run on the hook thread during shutdown.</summary>
        private static void UnhookEscapeHook()
        {
            if (_hookHandle == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hookProc = null;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == VK_ESCAPE)
                {
                    var fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero && IsHtmlHelpWindow(fg))
                    {
                        PostMessage(fg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        return (IntPtr)1; // swallow Escape so CHM doesn't also process it
                    }
                }
            }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private static bool IsHtmlHelpWindow(IntPtr hWnd)
        {
            var sb = new StringBuilder(64);
            int len = GetClassName(hWnd, sb, sb.Capacity);
            return len > 0 && sb.ToString() == HtmlHelpClassName;
        }

        public static void ShowHelp(string context = null)
        {
            if (string.IsNullOrEmpty(_helpFilePath) || !File.Exists(_helpFilePath))
            {
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("help.launch.not_found"), Radios.VerbosityLevel.Critical);
                return;
            }

            string topic = null;
            if (context != null && ContextMap.TryGetValue(context, out var page))
                topic = page;

            try
            {
                if (topic != null)
                    System.Windows.Forms.Help.ShowHelp(null, _helpFilePath, HelpNavigator.Topic, topic);
                else
                    System.Windows.Forms.Help.ShowHelp(null, _helpFilePath);
            }
            catch (Exception ex)
            {
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("help.launch.open_failed"), Radios.VerbosityLevel.Critical);
                System.Diagnostics.Trace.WriteLine($"HelpLauncher error: {ex.Message}");
            }
        }
    }
}
