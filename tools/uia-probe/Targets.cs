using System.Diagnostics;
using System.Windows.Automation;

namespace JJFlex.UiaProbe;

internal sealed record WindowInfo(
    IntPtr Hwnd,
    string Title,
    string ClassName,
    bool Visible,
    bool Foreground,
    string UiaName,
    string AutomationId)
{
    public string HwndHex => "0x" + Hwnd.ToInt64().ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Finding the thing to probe: the process, and which of its windows.
///
/// Window selection matters more than it looks. jjflexible.exe keeps several
/// top-level HWNDs alive at once — the main window, any open dialog, and WPF's
/// invisible message-only windows — and a keystroke sent to the wrong one does
/// nothing while reporting success. So a target is resolved explicitly and the
/// choice is echoed in every result.
/// </summary>
internal static class Targets
{
    public const string DefaultProcessName = "jjflexible";

    public static int[] FindPids(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Select(p => p.Id).OrderBy(i => i).ToArray();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<int>();
        }
    }

    public static List<WindowInfo> Windows(int pid, bool visibleOnly = true)
    {
        var result = new List<WindowInfo>();
        IntPtr fg = Native.GetForegroundWindow();

        foreach (IntPtr h in Native.TopLevel((uint)pid, visibleOnly))
        {
            string uiaName = "", automationId = "";
            try
            {
                AutomationElement? el = AutomationElement.FromHandle(h);
                if (el != null)
                {
                    uiaName = el.Current.Name ?? "";
                    automationId = el.Current.AutomationId ?? "";
                }
            }
            catch (ElementNotAvailableException) { uiaName = "(element not available)"; }
            catch (System.Runtime.InteropServices.COMException ex) { uiaName = "(UIA failed: " + ex.GetType().Name + ")"; }
            catch (ArgumentException) { /* not a UIA-visible window */ }

            result.Add(new WindowInfo(h, Native.Text(h), Native.Cls(h),
                Native.IsWindowVisible(h), h == fg, uiaName, automationId));
        }
        return result;
    }

    /// <summary>
    /// Resolve a window from a user-supplied selector. Empty selector means
    /// "the best candidate": foreground first if it belongs to the process,
    /// otherwise the visible window with a title, preferring the largest
    /// automation subtree — which in practice is the dialog on top, not the
    /// main window behind it.
    /// </summary>
    /// <summary>
    /// The Win32 class of a standard dialog box, which is what
    /// <c>MessageBox</c> creates.
    ///
    /// <para>Worth naming explicitly because of what Track A hit on
    /// 2026-08-20: at least one JJFlex dialog puts up a MODAL message box
    /// during construction, and it blocked their UI thread for ten minutes. It
    /// presents as a hang, not as a failure — the app is alive, responds to
    /// nothing, and every keystroke disappears into a box nobody knew was
    /// there.</para>
    ///
    /// <para>A probe that cannot see this reports a run of dead keys and a
    /// mysterious stall. A probe that can see it says "there is a message box
    /// in front of you", which is the difference between a diagnosis and a
    /// shrug.</para>
    /// </summary>
    public const string MessageBoxClass = "#32770";

    /// <summary>Any modal message box the process currently has up.</summary>
    public static WindowInfo? FindMessageBox(int pid) =>
        Windows(pid).FirstOrDefault(w =>
            w.Visible && string.Equals(w.ClassName, MessageBoxClass, StringComparison.Ordinal));

    public static WindowInfo? Resolve(int pid, string? selector)
    {
        var windows = Windows(pid).Where(w => w.Visible).ToList();
        if (windows.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(selector))
        {
            string s = selector.Trim();
            if (int.TryParse(s, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index)
                && index >= 0 && index < windows.Count)
                return windows[index];

            return windows.FirstOrDefault(w =>
                       w.Title.Contains(s, StringComparison.OrdinalIgnoreCase))
                ?? windows.FirstOrDefault(w =>
                       w.UiaName.Contains(s, StringComparison.OrdinalIgnoreCase))
                ?? windows.FirstOrDefault(w =>
                       w.ClassName.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var foreground = windows.FirstOrDefault(w => w.Foreground);
        if (foreground != null) return foreground;

        return windows.FirstOrDefault(w => !string.IsNullOrEmpty(w.Title)) ?? windows[0];
    }
}
