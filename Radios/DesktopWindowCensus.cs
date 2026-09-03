#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Radios
{
    /// <summary>
    /// One top-level window as the operator's screen holds it: what it is
    /// called, what it is, who owns it, and what state it is in.
    /// </summary>
    /// <param name="ProcessName">The owning executable's base name, empty
    /// when the process could not be identified.</param>
    /// <param name="ProcessAlive">False when the owning process is gone —
    /// a window outliving its process is the orphan-process family (#14,
    /// #21) showing itself, and it gets its own callout.</param>
    /// <param name="Responding">False when the owning thread did not answer
    /// a message within a short timeout — "not responding", in Task Manager's
    /// words.</param>
    /// <param name="OwnerHwnd">The window's Win32 owner, zero for none. A
    /// dialog is owned by the window it blocks.</param>
    /// <param name="OwnerEnabled">Whether that owner currently accepts input.
    /// An enabled window whose owner is disabled IS a modal dialog holding
    /// its program hostage — the exact picture measured on 2026-09-02, with
    /// Select Radio enabled and the main window disabled behind it.</param>
    public sealed record DesktopWindowRecord(
        nint Hwnd,
        string Title,
        string ClassName,
        int ProcessId,
        string ProcessName,
        bool ProcessAlive,
        bool Responding,
        bool IsOurs,
        bool IsForeground,
        bool IsEnabled,
        nint OwnerHwnd,
        string OwnerTitle,
        bool OwnerEnabled,
        bool IsToolWindow,
        bool IsCloaked)
    {
        /// <summary>This window is a modal dialog: it takes input while the
        /// window that owns it does not.</summary>
        public bool HoldsAModal => IsEnabled && OwnerHwnd != 0 && !OwnerEnabled;

        /// <summary>This window is waiting behind a modal of its own.</summary>
        public bool IsBehindAModal => !IsEnabled;

        /// <summary>The Windows desktop itself, which is what holds the
        /// foreground when everything is minimised.</summary>
        public bool IsDesktop => ClassName is "Progman" or "WorkerW";

        /// <summary>
        /// Whether this window belongs in a census the operator reads: not
        /// cloaked (another virtual desktop, or a store app parked in the
        /// background), and either titled and ordinary, or the foreground
        /// window whatever it looks like — the foreground is the one window
        /// that must never be filtered out, because "which window has my
        /// keyboard" is the question.
        /// </summary>
        public bool BelongsInCensus
            => !IsCloaked && (IsForeground || (Title.Length > 0 && !IsToolWindow));
    }

    /// <summary>
    /// A record of the foreground being taken from an idle operator while a
    /// modal of ours was up, kept so the census can name the thief later.
    /// </summary>
    public sealed record ForegroundTheft(DateTime When, DesktopWindowRecord Thief, string TakenFromTitle);

    /// <summary>What the screen held at one instant.</summary>
    public sealed class DesktopWindowSnapshot
    {
        public DesktopWindowSnapshot(DateTime takenAt, IReadOnlyList<DesktopWindowRecord> windows)
        {
            TakenAt = takenAt;
            Windows = windows;
        }

        public DateTime TakenAt { get; }

        /// <summary>Foreground first, this program's windows next, then the
        /// rest in stacking order.</summary>
        public IReadOnlyList<DesktopWindowRecord> Windows { get; }

        public DesktopWindowRecord? Foreground
        {
            get
            {
                foreach (var w in Windows) if (w.IsForeground) return w;
                return null;
            }
        }
    }

    /// <summary>
    /// "What is actually on my screen right now" (#154) — every visible
    /// top-level window with its title, class, owning process, whether that
    /// process is alive and answering, whether it is a modal, and which one
    /// holds the foreground.
    ///
    /// <para><b>Why this is a product feature and not a developer script.</b>
    /// A sighted operator answers "what is on my screen" by looking. A blind
    /// operator gets the narration of whatever holds focus, which describes a
    /// CONTROL and says nothing about which WINDOW it is in, which PROCESS
    /// owns it, or whether that process is even still running. On 2026-08-20
    /// that gap cost twenty minutes of guessing at an unidentified file
    /// dialog; on 2026-09-02 it left the operator stuck in a healthy,
    /// visible dialog that simply did not have the keyboard, while someone
    /// else enumerated windows from a terminal. <c>jjprobe windows</c> in
    /// <c>tools/uia-probe/</c> has answered the question for developers since
    /// Sprint 33; this is the same answer in the operator's hands.</para>
    ///
    /// <para><b>Read-only.</b> It reports; it never closes, focuses or acts on
    /// anything. The value is entirely in the answering.</para>
    ///
    /// <para><b>The pure parts are separate from the Win32 parts</b> so the
    /// ordering, the filter, the friendly names and the sentences are pinned
    /// by tests that construct no window and call no native function.</para>
    /// </summary>
    public static class DesktopWindowCensus
    {
        // ────────────────────────────────────────────────────────────────
        //  The snapshot
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Take the census now. Never throws: a window that cannot be
        /// described is described as far as it can be.
        /// </summary>
        public static DesktopWindowSnapshot Take()
        {
            var all = new List<DesktopWindowRecord>();
            try
            {
                nint fg = Native.GetForegroundWindow();
                var processes = new Dictionary<int, (string name, bool alive)>();
                Native.EnumWindows((h, _) =>
                {
                    try
                    {
                        if (!Native.IsWindowVisible(h)) return true;
                        all.Add(Describe(h, fg, processes, checkResponding: true));
                    }
                    catch
                    {
                        // One undescribable window must not lose the census.
                    }
                    return true;
                }, 0);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine(
                    $"DesktopWindowCensus: enumeration failed: {ex.Message}",
                    TraceLevel.Warning);
            }
            return new DesktopWindowSnapshot(DateTime.Now, Arrange(all));
        }

        /// <summary>
        /// Describe one window — for the trace line that names a foreground
        /// thief. Skips the responding check, which can wait on a timeout.
        /// </summary>
        public static DesktopWindowRecord Describe(nint hwnd)
        {
            try
            {
                return Describe(hwnd, Native.GetForegroundWindow(),
                    new Dictionary<int, (string, bool)>(), checkResponding: false);
            }
            catch
            {
                return new DesktopWindowRecord(hwnd, "", "", 0, "", false, true, false, false,
                    true, 0, "", true, false, false);
            }
        }

        private static DesktopWindowRecord Describe(
            nint h, nint fg, Dictionary<int, (string name, bool alive)> processes, bool checkResponding)
        {
            Native.GetWindowThreadProcessId(h, out uint pidRaw);
            int pid = (int)pidRaw;
            if (!processes.TryGetValue(pid, out var proc))
            {
                proc = IdentifyProcess(pid);
                processes[pid] = proc;
            }

            nint owner = Native.GetWindow(h, Native.GW_OWNER);
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            bool responding = !checkResponding || proc.alive && Native.IsResponding(h);

            return new DesktopWindowRecord(
                Hwnd: h,
                Title: Native.Text(h),
                ClassName: Native.Cls(h),
                ProcessId: pid,
                ProcessName: proc.name,
                ProcessAlive: proc.alive,
                Responding: responding,
                IsOurs: pid == Environment.ProcessId,
                IsForeground: h == fg,
                IsEnabled: Native.IsWindowEnabled(h),
                OwnerHwnd: owner,
                OwnerTitle: owner == 0 ? "" : Native.Text(owner),
                OwnerEnabled: owner == 0 || Native.IsWindowEnabled(owner),
                IsToolWindow: (ex & Native.WS_EX_TOOLWINDOW) != 0,
                IsCloaked: Native.IsCloaked(h));
        }

        private static (string name, bool alive) IdentifyProcess(int pid)
        {
            if (pid == 0) return ("", false);
            try
            {
                using var p = Process.GetProcessById(pid);
                bool exited;
                try { exited = p.HasExited; } catch { exited = false; }
                return (p.ProcessName ?? "", !exited);
            }
            catch (ArgumentException)
            {
                // No such process: the window has outlived its owner.
                return ("", false);
            }
            catch (Exception)
            {
                return ("", true);
            }
        }

        /// <summary>The class name of one window, empty for none.</summary>
        public static string ClassNameOf(nint hwnd)
        {
            if (hwnd == 0) return "";
            try { return Native.Cls(hwnd); } catch { return ""; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Pure: filter and order
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The census order: the foreground window first, because "is this
        /// mine?" is the first question and "which one has my keyboard" is
        /// the second; this program's own windows next; everything else in
        /// the order the desktop stacks it. Stable within each group.
        /// </summary>
        public static IReadOnlyList<DesktopWindowRecord> Arrange(IEnumerable<DesktopWindowRecord> all)
        {
            var foreground = new List<DesktopWindowRecord>();
            var ours = new List<DesktopWindowRecord>();
            var rest = new List<DesktopWindowRecord>();
            foreach (var w in all)
            {
                if (!w.BelongsInCensus) continue;
                if (w.IsForeground) foreground.Add(w);
                else if (w.IsOurs) ours.Add(w);
                else rest.Add(w);
            }
            var result = new List<DesktopWindowRecord>(foreground.Count + ours.Count + rest.Count);
            result.AddRange(foreground);
            result.AddRange(ours);
            result.AddRange(rest);
            return result;
        }

        // ────────────────────────────────────────────────────────────────
        //  Pure: what the sentinel must never take the foreground from
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Window classes of prompts the foreground watchdog must never take
        /// from: the modern permission and consent host, any standard dialog
        /// box (a message box from another program included), credential and
        /// PIN prompts, UAC, and the secure desktop. Stealing focus from a
        /// security prompt is the one case where a reclaim is actively
        /// harmful, and harmful specifically to an operator who cannot see
        /// what just vanished. The same list lives in
        /// <c>tools/uia-probe/Native.cs</c> for the same reason; the probe is
        /// a separate build and cannot reference this assembly.
        /// </summary>
        public static readonly IReadOnlyList<string> ProtectedForegroundClasses = new[]
        {
            "Shell_SystemDialogProxy",
            "#32770",
            "Credential Dialog Xaml Host",
            "ConsentUI",
            "$$$Secure UI$$$",
        };

        public static bool IsProtectedForegroundClass(string? className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            foreach (var c in ProtectedForegroundClasses)
                if (string.Equals(c, className, StringComparison.Ordinal)) return true;
            return false;
        }

        // ────────────────────────────────────────────────────────────────
        //  Pure: naming a program in plain language
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A plain-language name for a process the operator is likely to
        /// meet, keyed by executable base name (case-insensitive). Product
        /// names, not prose — they are data, and an exe name nobody here
        /// recognises is reported as itself, which is the truth. Deliberately
        /// short: every entry is a claim about somebody else's program.
        /// </summary>
        private static readonly Dictionary<string, string> FriendlyProgramNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["explorer"] = "Windows File Explorer",
                ["nvda"] = "NVDA, your screen reader",
                ["jfw"] = "JAWS, your screen reader",
                ["msedge"] = "Microsoft Edge",
                ["chrome"] = "Google Chrome",
                ["firefox"] = "Firefox",
                ["WindowsTerminal"] = "Windows Terminal",
                ["powershell"] = "PowerShell",
                ["pwsh"] = "PowerShell",
                ["cmd"] = "the command prompt",
                ["Teams"] = "Microsoft Teams",
                ["ms-teams"] = "Microsoft Teams",
                ["OUTLOOK"] = "Microsoft Outlook",
                ["olk"] = "Microsoft Outlook",
                ["WINWORD"] = "Microsoft Word",
                ["EXCEL"] = "Microsoft Excel",
                ["Code"] = "Visual Studio Code",
                ["devenv"] = "Visual Studio",
                ["Dropbox"] = "Dropbox",
                ["Zoom"] = "Zoom",
                ["Discord"] = "Discord",
                ["Notepad"] = "Notepad",
                ["LockApp"] = "the Windows lock screen",
                ["SearchHost"] = "Windows Search",
                ["StartMenuExperienceHost"] = "the Windows Start menu",
                ["ShellExperienceHost"] = "the Windows shell",
                ["TextInputHost"] = "Windows text input",
                ["ApplicationFrameHost"] = "a Windows Store app",
                ["Taskmgr"] = "Task Manager",
            };

        /// <summary>
        /// The plain-language name for a window's program, or null when there
        /// is nothing better than the executable name. Our own process is
        /// named by the caller from the lexicon, not here.
        /// </summary>
        public static string? FriendlyProgramName(DesktopWindowRecord w)
        {
            if (w.IsDesktop) return Lexicon.Get("leader.windows.desktop");
            if (w.ProcessName.Length > 0 && FriendlyProgramNames.TryGetValue(w.ProcessName, out var name))
                return name;
            return null;
        }

        // ────────────────────────────────────────────────────────────────
        //  The theft record, for the census to report
        // ────────────────────────────────────────────────────────────────

        private static ForegroundTheft? _lastTheft;

        /// <summary>The most recent foreground theft the watchdog repaired
        /// this session, or null.</summary>
        public static ForegroundTheft? LastTheft => System.Threading.Volatile.Read(ref _lastTheft);

        public static void NoteTheft(ForegroundTheft theft)
            => System.Threading.Volatile.Write(ref _lastTheft, theft);

        // ────────────────────────────────────────────────────────────────
        //  The operator's last input
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Milliseconds since the operator last pressed a key or moved the
        /// mouse anywhere in this session, or -1 when Windows could not say.
        /// Injected input counts, which is the right way round for this
        /// audience: a screen reader re-injecting the operator's keystroke is
        /// still the operator acting.
        /// </summary>
        public static int MillisecondsSinceLastInput()
        {
            try
            {
                var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
                if (!Native.GetLastInputInfo(ref info)) return -1;
                // Both are the 32-bit tick clock; unchecked subtraction is
                // correct across a wrap.
                int elapsed = unchecked(Environment.TickCount - (int)info.dwTime);
                return elapsed < 0 ? 0 : elapsed;
            }
            catch
            {
                return -1;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Win32
        // ────────────────────────────────────────────────────────────────

        private static class Native
        {
            internal const uint GW_OWNER = 4;
            internal const int GWL_EXSTYLE = -20;
            internal const long WS_EX_TOOLWINDOW = 0x00000080;
            private const int DWMWA_CLOAKED = 14;
            private const uint WM_NULL = 0x0000;
            private const uint SMTO_ABORTIFHUNG = 0x0002;
            private const uint SMTO_BLOCK = 0x0001;
            private const uint RespondingTimeoutMs = 250;

            internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);

            [DllImport("user32.dll")]
            internal static extern nint GetForegroundWindow();

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsWindowVisible(nint hWnd);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsWindowEnabled(nint hWnd);

            [DllImport("user32.dll")]
            internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

            [DllImport("user32.dll")]
            internal static extern nint GetWindow(nint hWnd, uint uCmd);

            // GetWindowLongPtrW is a real export only in 64-bit user32; on
            // x86 it is a macro over GetWindowLongW, and this app still ships
            // an x86 build. Pick at run time by pointer size.
            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
            private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
            private static extern int GetWindowLong32(nint hWnd, int nIndex);

            internal static nint GetWindowLongPtr(nint hWnd, int nIndex)
                => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetWindowTextLength(nint hWnd);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern nint SendMessageTimeout(
                nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

            [DllImport("dwmapi.dll")]
            private static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out int value, int size);

            [StructLayout(LayoutKind.Sequential)]
            internal struct LASTINPUTINFO
            {
                public uint cbSize;
                public uint dwTime;
            }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

            internal static string Text(nint h)
            {
                int n = GetWindowTextLength(h);
                if (n <= 0) return "";
                var sb = new StringBuilder(n + 2);
                GetWindowText(h, sb, sb.Capacity);
                return sb.ToString();
            }

            internal static string Cls(nint h)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                return sb.ToString();
            }

            internal static bool IsCloaked(nint h)
            {
                try
                {
                    return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                           && cloaked != 0;
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>
            /// Whether the window's thread answers a no-op message inside a
            /// short timeout. A hung program is reported rather than waited
            /// on: the census is something an operator asks for while
            /// confused, and a five-second stall per frozen window would be
            /// its own small outage.
            /// </summary>
            internal static bool IsResponding(nint h)
            {
                try
                {
                    nint ok = SendMessageTimeout(h, WM_NULL, 0, 0,
                        SMTO_ABORTIFHUNG | SMTO_BLOCK, RespondingTimeoutMs, out _);
                    return ok != 0;
                }
                catch
                {
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// The census in words: the sentences the list shows and the reader
    /// speaks, composed from the lexicon so the wording is the operator's to
    /// change. One row per window; each row is a complete sentence because
    /// a screen reader reads a row on its own.
    /// </summary>
    public static class DesktopWindowCensusSpeech
    {
        public static string Title(int count)
            => count == 1
                ? Lexicon.Get("leader.windows.title_one")
                : Lexicon.Get("leader.windows.title", ("count", count));

        /// <summary>
        /// "3. Select Radio. JJ Flexible Radio Access, this program. Has the
        /// keyboard, a dialog blocking the rest of its program."
        /// </summary>
        public static string Row(DesktopWindowRecord w, int index)
        {
            string title = w.Title.Length > 0 ? w.Title : Lexicon.Get("leader.windows.untitled");
            string program = ProgramPhrase(w);
            string status = StatusPhrase(w);
            return status.Length == 0
                ? Lexicon.Get("leader.windows.row", ("index", index), ("title", title), ("program", program))
                : Lexicon.Get("leader.windows.row_with_status",
                    ("index", index), ("title", title), ("program", program), ("status", status));
        }

        /// <summary>Who owns the window, in plain language where we know it.</summary>
        public static string ProgramPhrase(DesktopWindowRecord w)
        {
            if (w.IsOurs) return Lexicon.Get("leader.windows.program.ours");
            string? friendly = DesktopWindowCensus.FriendlyProgramName(w);
            if (friendly != null) return friendly;
            if (w.ProcessName.Length > 0) return w.ProcessName;
            return Lexicon.Get("leader.windows.program.unknown");
        }

        /// <summary>
        /// The window's state as a comma-joined list of short phrases, empty
        /// when there is nothing worth saying. "Its program has exited" is the
        /// orphan callout and comes first when it applies.
        /// </summary>
        public static string StatusPhrase(DesktopWindowRecord w)
        {
            var parts = new List<string>(4);
            if (!w.ProcessAlive) parts.Add(Lexicon.Get("leader.windows.status.program_gone"));
            else if (!w.Responding) parts.Add(Lexicon.Get("leader.windows.status.not_responding"));
            if (w.IsForeground) parts.Add(Lexicon.Get("leader.windows.status.keyboard"));
            if (w.HoldsAModal)
            {
                parts.Add(w.OwnerTitle.Length > 0
                    ? Lexicon.Get("leader.windows.status.modal_over", ("owner", w.OwnerTitle))
                    : Lexicon.Get("leader.windows.status.modal"));
            }
            else if (w.IsBehindAModal)
            {
                parts.Add(Lexicon.Get("leader.windows.status.behind_modal"));
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// The census's closing row when the watchdog has repaired a theft
        /// this session: who took the keyboard, from which dialog, and when.
        /// </summary>
        public static string LastTheftRow(ForegroundTheft theft)
            => Lexicon.Get("leader.windows.last_theft",
                ("time", theft.When.ToString("t", System.Globalization.CultureInfo.CurrentCulture)),
                ("program", ProgramPhrase(theft.Thief)),
                ("title", theft.TakenFromTitle));

        /// <summary>
        /// The explanation spoken after the watchdog takes the foreground
        /// back: names the program when we can, says where the operator is
        /// now either way.
        /// </summary>
        public static string ReclaimAnnouncement(DesktopWindowRecord thief, string dialogTitle)
        {
            string? friendly = DesktopWindowCensus.FriendlyProgramName(thief);
            string program = friendly ?? thief.ProcessName;
            return program.Length > 0
                ? Lexicon.Get("connect.dialog.keyboard_reclaimed_from",
                    ("program", program), ("title", dialogTitle))
                : Lexicon.Get("connect.dialog.keyboard_reclaimed", ("title", dialogTitle));
        }
    }
}
