using System.Diagnostics;

namespace JJFlexUpdaterHelper;

internal static class JjfProcessWaiter
{
    // 30s ceiling per TRACK-INSTRUCTIONS step 3. If JJF hasn't released its file
    // locks by then it's almost certainly hung — bail rather than block forever.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public enum WaitOutcome
    {
        Exited,
        AlreadyGone,
        TimedOut,
    }

    public static WaitOutcome WaitForExit(int pid, TimeSpan timeout, Action<string> log)
    {
        Process proc;
        try
        {
            proc = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // No process with this id — JJF exited before the helper could grab it,
            // which is the happy-path race when JJF closes very quickly after handoff.
            log($"JJF pid {pid} already exited (no such process); proceeding.");
            return WaitOutcome.AlreadyGone;
        }
        catch (InvalidOperationException)
        {
            log($"JJF pid {pid} already exited (process record disposed); proceeding.");
            return WaitOutcome.AlreadyGone;
        }

        using (proc)
        {
            // Process.HasExited can briefly lie if the handle is still being torn
            // down, so prefer WaitForExit (which is wired to the Win32 wait
            // primitive and handles already-exited correctly).
            log($"Waiting up to {timeout.TotalSeconds:F0}s for JJF pid {pid} to exit...");
            var sw = Stopwatch.StartNew();
            var exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
            sw.Stop();

            if (exited)
            {
                log($"JJF pid {pid} exited after {sw.Elapsed.TotalSeconds:F1}s.");
                return WaitOutcome.Exited;
            }

            log($"JJF pid {pid} did not exit within {timeout.TotalSeconds:F0}s; bailing without touching install.");
            return WaitOutcome.TimedOut;
        }
    }
}
