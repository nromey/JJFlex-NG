using System.Runtime.InteropServices;
using System.Text;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>Who currently owns the keyboard, from the operating system's point of view.</summary>
public sealed record ForegroundWindow(int ProcessId, string Title)
{
    public override string ToString() => $"pid {ProcessId}, \"{Title}\"";
}

/// <summary>
/// The operator-facing guarantee, measured rather than asserted: running this
/// suite must not change which window has the keyboard. Every run checks it.
/// </summary>
public static class Foreground
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

    public static ForegroundWindow Current()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out var processId);
            var title = new StringBuilder(512);
            GetWindowTextW(hwnd, title, title.Capacity);
            return new ForegroundWindow(processId, title.ToString());
        }
        catch
        {
            return new ForegroundWindow(0, string.Empty);
        }
    }
}
