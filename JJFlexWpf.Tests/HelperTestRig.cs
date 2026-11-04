using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A host of our own over the MIT stub helper, for one test. Every process it
/// starts is hidden (the stub exits 40 at once if it ever has a console
/// window, so a missing CREATE_NO_WINDOW is a failed start, not a window), in
/// its own kill-on-close job, and checked gone when the rig is disposed; any
/// survivor is killed and fails the test.
/// </summary>
internal sealed class HelperTestRig : IDisposable
{
    internal static string StubPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "HelperStub", "JJFlexWpf.HelperStub.Tests.exe");
            Assert.True(File.Exists(path), "The stub helper was not built beside the tests: " + path);
            return path;
        }
    }

    /// <summary>Survivors from every rig in this run, reported by the tests that check for them.</summary>
    internal static readonly ConcurrentBag<int> Survivors = new();

    internal static readonly WdspHostPolicy Fast = new()
    {
        RestartDelays = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150) },
        Linger = TimeSpan.FromMilliseconds(400),
        HandshakeDeadline = TimeSpan.FromSeconds(10),
        // Longer than the app's 500 ms so a busy test machine's scheduling is
        // not mistaken for a stall; the stall tests hold the stub for 1.5 s.
        Stall = TimeSpan.FromSeconds(1),
        ConstructionWait = TimeSpan.FromSeconds(15),
    };

    public WdspHost Host { get; }
    private readonly List<WdspHelperEngine> _engines = new();
    private readonly List<int> _extraPids = new();

    public HelperTestRig(Func<int, IReadOnlyList<string>>? args = null, WdspHostPolicy? policy = null, string? path = null)
    {
        Host = new WdspHost(path ?? StubPath, policy ?? Fast, args);
        var log = Environment.GetEnvironmentVariable("JJFLEX_HELPER_TEST_TRACE");
        if (log != null && !Trace.Listeners.OfType<TextWriterTraceListener>().Any(l => l.Name == "helper"))
        {
            Trace.Listeners.Add(new TextWriterTraceListener(log, "helper"));
            Trace.AutoFlush = true;
        }
    }

    public WdspHelperEngine Engine(string id = "nnr", IReadOnlyDictionary<string, EngineSettingValue>? starting = null)
    {
        var e = new WdspHelperEngine(Host, id, DenoiseDirection.Receive, starting);
        lock (_engines) _engines.Add(e);
        return e;
    }

    public void Track(int pid) { lock (_extraPids) _extraPids.Add(pid); }

    public static void Until(Func<bool> predicate, string what, int ms = 10000, Action? step = null)
    {
        var t = Stopwatch.StartNew();
        while (!predicate() && t.ElapsedMilliseconds < ms) { step?.Invoke(); Thread.Sleep(2); }
        Assert.True(predicate(), "timed out waiting for: " + what);
    }

    /// <summary>
    /// Wait, outside the engine, until the answer the next ProcessFrame call
    /// will look for has arrived, so a live-path test is deterministic
    /// without the engine itself ever waiting.
    /// </summary>
    public static void Pace(params WdspHelperEngine[] engines)
    {
        foreach (var e in engines)
        {
            var ch = e.Channel;
            if (ch == null || !ch.Opened) continue;
            ulong n = ch.NextSeq;
            if (n < (ulong)e.PipelineDepth) continue;
            // Waits for discarded start-up answers too, so a test can tell the
            // discard (false although the answer is there) from mere lateness.
            ulong want = n - (ulong)e.PipelineDepth;
            var t = Stopwatch.StartNew();
            while (!ch.Has(want) && t.ElapsedMilliseconds < 3000 && ch.Opened) Thread.Sleep(0);
        }
    }

    public static bool Gone(int pid, int waitMs)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit(waitMs);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    public static void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); p.WaitForExit(2000); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        lock (_engines) foreach (var e in _engines) e.Dispose();
        Host.Dispose();
        var pids = Host.LaunchedProcessIds.Concat(_extraPids).ToArray();
        var alive = new List<int>();
        foreach (var pid in pids)
            if (!Gone(pid, 3000)) { alive.Add(pid); Survivors.Add(pid); Kill(pid); }
        Assert.True(alive.Count == 0, "stub processes outlived their rig and were killed: " + string.Join(", ", alive));
    }
}
