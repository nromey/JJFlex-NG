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

        private static readonly Dictionary<string, string> ContextMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "FreqDisplay", "pages/tuning-frequency.htm" },
            { "ScreenFieldsDSP", "pages/filters-dsp.htm" },
            { "ScreenFieldsTX", "pages/ptt-transmission.htm" },
            { "ScreenFieldsAudio", "pages/audio-workshop.htm" },
            { "ScreenFieldsReceiver", "pages/filters-dsp.htm" },
            { "ScreenFieldsAntenna", "pages/filters-dsp.htm" },
            { "AudioWorkshop", "pages/audio-workshop.htm" },
            { "EarconExplorer", "pages/earcon-explorer.htm" },
            { "MeterTones", "pages/meter-tones.htm" },
            { "LogPanel", "pages/logging.htm" },
            { "SettingsDialog", "pages/settings-profiles.htm" },
            { "CommandFinder", "pages/keyboard-reference.htm" },
            { "LeaderKey", "pages/leader-key.htm" },
            { "WelcomeDialog", "pages/getting-started.htm" },
            { "ConnectDialog", "pages/connection-troubleshooting.htm" },
            { "WhatsNew", "pages/whats-new.htm" },
        };

        public static void Initialize()
        {
            _helpFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JJFlexRadio.chm");
            InstallEscapeHook();
        }

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
                Radios.ScreenReaderOutput.Speak("Help file not found.", Radios.VerbosityLevel.Critical);
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
                Radios.ScreenReaderOutput.Speak("Could not open help file.", Radios.VerbosityLevel.Critical);
                System.Diagnostics.Trace.WriteLine($"HelpLauncher error: {ex.Message}");
            }
        }
    }
}
