using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The measurement behind <see cref="EngineKeepUpRule"/>'s defaults (Sprint 49
/// Track L): every receive engine run through the real
/// <see cref="RxAudioPipeline"/> at the receive loop's own pace, one 10 ms
/// stereo buffer at a time, first on this machine as it is, then with the
/// audio thread sharing one processor with three busy threads. Prints what it
/// saw; asserts nothing about speed, because speed is the machine's.
/// </summary>
/// <remarks>
/// Opt-in, because it takes about two minutes: set
/// <c>JJFLEX_KEEPUP_MEASURE=1</c> for the run. Windowless and silent: no
/// window, no audio device, and the helper (when its build is on this machine)
/// is a hidden console program in its own job. The load is on ONE processor
/// only, so the rest of the machine is untouched.
/// </remarks>
public sealed class EngineKeepUpMeasurementTests
{
    private readonly ITestOutputHelper _out;
    public EngineKeepUpMeasurementTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

    private const int Rate = 48000, Frame = 480, Channels = 2;
    private static readonly long Period = Stopwatch.Frequency / 100;

    [Fact]
    public void Measure_start_up_and_loaded_lateness_for_every_receive_engine()
    {
        string? which = Environment.GetEnvironmentVariable("JJFLEX_KEEPUP_MEASURE");
        if (which is not ("1" or "helper" or "local"))
        {
            _out.WriteLine("NOT RUN: set JJFLEX_KEEPUP_MEASURE=1 to measure (about three minutes), or to local or helper for half.");
            return;
        }
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string native = Path.Combine(RepoPaths.RequireRoot(), "runtimes", "win-" + arch, "native");
        string? helperPath = HelperLifecycleTests.RealHelper();
        _out.WriteLine($"Machine: {Environment.ProcessorCount} logical processors, {arch}. Helper: {helperPath ?? "not built here"}.");
        float[] noise = StereoNoise(20);
        if (which != "helper")
        {
            var catalog = Catalog(native, null);
            foreach (string id in new[] { "rnnoise", "deepfilternet3", "nr4", "spectral_capture" })
            {
                Run(catalog, id, noise, loaded: false, killHelperAt: null, null);
                Run(catalog, id, noise, loaded: true, killHelperAt: null, null);
            }
        }
        if (which == "local" || helperPath == null) return;
        // Each helper scenario on a helper of its own, so one cannot leave the
        // next a helper in a state it did not start in.
        foreach (string id in new[] { "nnr", "nr2" })
            foreach (var (loaded, burst, kill) in new[] { (false, false, (int?)null), (true, false, null), (false, true, null), (false, false, 300) })
            {
                using var rig = new HelperTestRig(path: helperPath, policy: WdspHostPolicy.Default);
                Run(Catalog(native, rig), id, noise, loaded, kill, rig, burst);
                var hello = rig.Host.CurrentConnection?.Hello;
                if (hello != null && !loaded && !burst && kill == null)
                    foreach (var e in hello.Engines.Where(e => e.Id == id))
                        _out.WriteLine($"    helper handshake, {e.Id}: median {e.MedianNanos / 1e6:F3} ms, 99th percentile {e.P99Nanos / 1e6:F3} ms per frame over {e.CalibrationFrames} frames.");
            }
    }

    private static DenoiseEngineCatalog Catalog(string native, HelperTestRig? rig) =>
        new(new DenoiseEngineCatalog().Entries.Select(e => e.Id switch
        {
            "deepfilternet3" => e with { Factory = (initial, _) => new DeepFilterNet3Engine((float)initial["attenuation_limit_db"].Number, Path.Combine(native, DeepFilterNet3Engine.LibraryFileName)) },
            "nr4" => e with { Factory = (initial, _) => new SpecbleachEngine(initial, Path.Combine(native, SpecbleachEngine.LibraryFileName), SpecbleachEngine.EstimatorFrom(initial)) },
            "nnr" or "nr2" when rig != null => e with { Factory = (initial, dir) => new WdspHelperEngine(rig.Host, e.Id, dir, initial) },
            _ => e,
        }));

    private void Run(DenoiseEngineCatalog catalog, string id, float[] noise, bool loaded, int? killHelperAt, HelperTestRig? rig, bool burst = false)
    {
        var events = new ConcurrentQueue<string>();
        var t = new Thread(() => RunOn(catalog, id, noise, loaded, killHelperAt, rig, events, burst)) { IsBackground = true };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(90)), id + " measurement did not finish");
        foreach (var e in events) _out.WriteLine("    event: " + e);
    }

    private void RunOn(DenoiseEngineCatalog catalog, string id, float[] noise, bool loaded, int? killHelperAt, HelperTestRig? rig,
        ConcurrentQueue<string> events, bool burst)
    {
        int core = Environment.ProcessorCount - 1;
        var mask = (UIntPtr)(1UL << core);
        var stop = new ManualResetEventSlim();
        var burners = new List<Thread>();
        int? helperPid = rig?.Host.CurrentProcessId;
        if (loaded)
        {
            SetThreadAffinityMask(GetCurrentThread(), mask);
            for (int b = 0; b < 3; b++)
            {
                var burner = new Thread(() => { SetThreadAffinityMask(GetCurrentThread(), mask); while (!stop.IsSet) { } }) { IsBackground = true };
                burner.Start(); burners.Add(burner);
            }
        }
        try
        {
            using var writer = new SettledOffAudioWriter();
            using var p = new RxAudioPipeline(48000, 2, catalog, 0, writer);
            p.KeepUpResult += (_, r) => events.Enqueue(r.Describe());
            var row = catalog.Find(id)!;
            var slot = row.Family == DenoiseFamily.Spectral ? p.SpectralSlot : p.NeuralSlot;
            var buffer = new float[Frame * Channels];
            int at = 0;
            void Next() { Array.Copy(noise, at, buffer, 0, buffer.Length); at = (at + buffer.Length) % (noise.Length - buffer.Length); }
            if (id == "spectral_capture")
            {
                p.StartNoiseSampling(3);
                for (int i = 0; i < 320; i++) { Next(); p.Process(buffer); }
                Assert.True(p.HasNoiseProfile);
            }
            Assert.True(p.SelectPcEngine(id, out var refusal), refusal);
            if (row.Family == DenoiseFamily.Spectral) p.SpectralEnabled = true; else p.RnnEnabled = true;
            if (loaded && rig != null && IsHelper(id) && rig.Host.CurrentProcessId is int pid)
            {
                using var helper = Process.GetProcessById(pid);
                helper.ProcessorAffinity = (IntPtr)(long)(1UL << core);
                helperPid = pid;
            }

            const int Total = 900;
            var elapsed = new double[Total];
            var late = new bool[Total];
            int runningAt = -1;
            long started = Stopwatch.GetTimestamp();
            int i2;
            for (i2 = 0; i2 < Total; i2++)
            {
                // Spun, not slept, so the pace is exact: a coarse sleep would
                // deliver buffers in pairs and make a helper's answers late for
                // a reason that is not the helper. The burst run does that on
                // purpose, two buffers together every 20 ms, as network jitter can.
                long due = started + (burst ? (i2 / 2) * 2 : i2) * Period;
                while (Stopwatch.GetTimestamp() < due) Thread.SpinWait(20);
                if (killHelperAt == i2 && rig?.Host.CurrentProcessId is int victim) HelperTestRig.Kill(victim);
                Next();
                var bank = slot.RunningBank;
                long lateBefore = bank?.LateFrames ?? 0;
                long t0 = Stopwatch.GetTimestamp();
                p.Process(buffer);
                elapsed[i2] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                bool running = slot.ProcessingEngineId == id;
                if (running && runningAt < 0) runningAt = i2;
                late[i2] = running && (IsHelper(id) ? (slot.RunningBank?.LateFrames ?? 0) != lateBefore && ReferenceEquals(bank, slot.RunningBank) : elapsed[i2] > 10);
            }
            writer.Settle(); // the last change raised: everything published so far (repair 2, no fixed wait)
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"{id}, {(loaded ? "LOADED (one processor shared with three busy threads)" : "idle machine")}{(burst ? ", buffers delivered in pairs every 20 ms" : "")}{(killHelperAt != null ? $", helper killed at buffer {killHelperAt}" : "")}: ");
            if (runningAt < 0) { sb.Append("never became the running engine in 9 seconds."); _out.WriteLine(sb.ToString()); return; }
            sb.Append(CultureInfo.InvariantCulture, $"running from buffer {runningAt} ({runningAt * 10} ms). ");
            var run = Enumerable.Range(runningAt, Total - runningAt).ToArray();
            var times = run.Select(i => elapsed[i]).Order().ToArray();
            sb.Append(CultureInfo.InvariantCulture, $"Per buffer once running: mean {run.Average(i => elapsed[i]):F3} ms, 99th percentile {times[(int)Math.Ceiling(times.Length * .99) - 1]:F3} ms, worst {times[^1]:F3} ms. ");
            int First(int from, int n) => Enumerable.Range(runningAt + from, Math.Max(0, Math.Min(n, Total - runningAt - from))).Count(i => late[i]);
            sb.Append(CultureInfo.InvariantCulture, $"Late buffers after it started running: first 10 {First(0, 10)}, 10 to 50 {First(10, 40)}, 50 to 100 {First(50, 50)}. ");
            int worstWindow = 0, worstAt = -1;
            for (int s = runningAt + 50; s + 100 <= Total; s++)
            {
                int c = 0; for (int k = s; k < s + 100; k++) if (late[k]) c++;
                if (c > worstWindow) { worstWindow = c; worstAt = s; }
            }
            sb.Append(CultureInfo.InvariantCulture, $"Most late buffers in any one-second window after the first 50: {worstWindow}{(worstAt >= 0 ? $" (from buffer {worstAt})" : "")}. ");
            sb.Append(CultureInfo.InvariantCulture, $"All late buffers once running: {run.Count(i => late[i])} of {run.Length}.");
            if (killHelperAt != null)
            {
                var after = Enumerable.Range(killHelperAt.Value, Total - killHelperAt.Value).ToArray();
                sb.Append(CultureInfo.InvariantCulture, $" After the kill: {after.Count(i => late[i])} late buffers in the following {after.Length}; late run lengths: {string.Join(",", Runs(late, killHelperAt.Value))}.");
            }
            _out.WriteLine(sb.ToString());
        }
        finally
        {
            stop.Set();
            foreach (var b in burners) b.Join();
            if (loaded && helperPid is int pid2)
            {
                try { using var helper = Process.GetProcessById(pid2); helper.ProcessorAffinity = (IntPtr)(long)((1UL << Environment.ProcessorCount) - 1); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static IEnumerable<int> Runs(bool[] late, int from)
    {
        int n = 0;
        for (int i = from; i < late.Length; i++)
        {
            if (late[i]) n++;
            else if (n > 0) { yield return n; n = 0; }
        }
        if (n > 0) yield return n;
    }

    private static bool IsHelper(string id) => id is "nnr" or "nr2";

    private static float[] StereoNoise(int seconds)
    {
        var mono = EngineSpeedCore.GeneratedNoise(seconds);
        var stereo = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) { stereo[2 * i] = mono[i]; stereo[2 * i + 1] = mono[i]; }
        return stereo;
    }
}
