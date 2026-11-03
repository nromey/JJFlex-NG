using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The helper engine over the MIT stub: alignment to the sample, lateness,
/// an audio thread that is never held, no allocation, and settings that
/// cost nothing when unchanged. Every test runs on an MTA thread and
/// constructs no window; every stub process is hidden, in a job, and gone.
/// </summary>
public sealed class HelperEngineTests
{
    public HelperEngineTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static float[] Interleave(float[] left, float[] right, int frame)
    {
        var b = new float[960];
        for (int i = 0; i < 480; i++) { b[2 * i] = left[frame * 480 + i]; b[2 * i + 1] = right[frame * 480 + i]; }
        return b;
    }

    private static (float[] Left, float[] Right) Signals(int frames)
    {
        var rng = new Random(737);
        var left = new float[frames * 480];
        var right = new float[frames * 480];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = 0.5f * MathF.Sin(i * 0.031f);
            right[i] = (float)(rng.NextDouble() - 0.5);
        }
        return (left, right);
    }

    [Fact]
    public void A_factory_built_engine_never_waits_offline_and_never_starts_without_its_file()
    {
        var row = new DenoiseEngineCatalog().Find("nnr")!;
        Assert.NotNull(row.Factory);
        using var engine = Assert.IsType<WdspHelperEngine>(row.Factory!(new Dictionary<string, EngineSettingValue>()));
        Assert.False(engine.OfflineWait);
        Assert.Equal(WdspHelperEngine.DefaultPipelineDepth, engine.PipelineDepth);
        Assert.Equal("nnr", engine.Id);
        Assert.Equal((48000, 480), (engine.SampleRate, engine.FrameLength));
        // No helper is built into the test output, so the app's own host finds nothing.
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.HelperMissing, engine.Unavailability);
        Assert.Equal(0, engine.LatencySamples);
        Assert.Empty(WdspHost.Default.LaunchedProcessIds);
        var nr2 = new DenoiseEngineCatalog().Find("nr2")!;
        Assert.Equal((DenoiseFamily.Spectral, Unavailability.HelperMissing), (nr2.Family!.Value, nr2.Availability()));
    }

    [Fact]
    public void The_default_command_line_is_exactly_the_pipe_name_and_the_start_is_hidden_suspended_and_jobbed()
    {
        Assert.Equal(WdspNative.CREATE_SUSPENDED | WdspNative.CREATE_NO_WINDOW, WdspNative.StartFlags);
        Assert.Equal(WdspNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | WdspNative.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION, WdspNative.JobLimitFlags);
        using var rig = new HelperTestRig();
        using var engine = rig.Engine();
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        string pipe = rig.Host.LastPipeName!;
        Assert.Matches(new Regex(@"^\\\\\.\\pipe\\jjflex-wdsp-" + Environment.ProcessId + "-[0-9a-f]{32}$"), pipe);
        Assert.Equal("\"" + HelperTestRig.StubPath + "\" --pipe " + pipe, rig.Host.LastCommandLine);
        Assert.True(rig.Host.CurrentHelperIsInItsJob());
        // A new random part on every start.
        Assert.NotEqual(WdspNative.NewPipeName(1), WdspNative.NewPipeName(1));
    }

    [Fact]
    public void A_second_client_cannot_connect_to_the_helper_pipe()
    {
        using var rig = new HelperTestRig();
        using var engine = rig.Engine();
        Assert.True(engine.IsAvailable);
        string name = rig.Host.LastPipeName!.Substring(@"\\.\pipe\".Length);
        using var intruder = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut);
        Assert.ThrowsAny<Exception>(() => intruder.Connect(300));
    }

    [Fact]
    public void Stereo_through_the_real_bank_is_the_input_delayed_by_exactly_the_reported_latency_on_both_channels()
    {
        using var rig = new HelperTestRig();
        var made = new List<WdspHelperEngine>();
        using var bank = new PerChannelDenoiserBank(() => { var e = rig.Engine(); made.Add(e); return e; }, 2);
        Assert.Equal(2448 + 960, made[0].LatencySamples);
        Assert.Equal(2448 + 960, bank.LatencySamples);
        const int frames = 60;
        var (left, right) = Signals(frames);
        int lag = bank.LatencySamples;
        for (int f = 0; f < frames; f++)
        {
            HelperTestRig.Pace(made.ToArray());
            var buffer = Interleave(left, right, f);
            bank.Process(buffer, 1f);
            if (f < 8) continue; // two frames of pipeline, then six of discarded start-up history
            for (int i = 0; i < 480; i++)
            {
                int t = f * 480 + i;
                Assert.Equal(left[t - lag], buffer[2 * i]);
                Assert.Equal(right[t - lag], buffer[2 * i + 1]);
            }
        }
        Assert.Equal(16, bank.LateFrames);
        Assert.Equal(0, bank.FallbackFrames);
    }

    [Fact]
    public void A_late_answer_is_a_false_counted_as_late_and_is_never_used_for_another_frame()
    {
        using var rig = new HelperTestRig(n => new[] { "--late", "20:120" });
        var made = new List<WdspHelperEngine>();
        using var bank = new PerChannelDenoiserBank(() => { var e = rig.Engine(); made.Add(e); return e; }, 1);
        var engine = made[0];
        var (signal, _) = Signals(40);
        long lateBefore = 0;
        var lateAt = new List<int>();
        for (int f = 0; f < 40; f++)
        {
            if (f != 22) HelperTestRig.Pace(engine);
            var buffer = signal.AsSpan(f * 480, 480).ToArray();
            if (f == 22) lateBefore = bank.LateFrames;
            long late = bank.LateFrames;
            bank.Process(buffer, 1f);
            if (bank.LateFrames != late) lateAt.Add(f);
            if (f == 22)
            {
                // Seq 20's answer is held back by the stub: late, not a failure.
                Assert.Equal(lateBefore + 1, bank.LateFrames);
                Assert.Equal(0, bank.FallbackFrames);
                Assert.True(engine.IsAvailable);
                Assert.Equal(signal.AsSpan(f * 480, 480).ToArray(), buffer); // passed through, today undelayed
                Thread.Sleep(250); // seq 20's answer now arrives, after its deadline
            }
            else if (f > 22)
            {
                // Seq 20 has arrived but nothing asks for it again; each frame gets its own seq's answer.
                for (int i = 0; i < 480; i++) Assert.Equal(signal[f * 480 + i - 3408], buffer[i]);
            }
        }
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 22 }, lateAt);
        Assert.Equal(0, bank.FallbackFrames);
    }

    private static TimeSpan Longest(WdspHelperEngine engine, int calls, Func<bool>? keepGoing = null)
    {
        var input = new float[480];
        var output = new float[480];
        long worst = 0;
        var t = new Stopwatch();
        for (int i = 0; i < calls && (keepGoing?.Invoke() ?? true); i++)
        {
            t.Restart();
            if (engine.IsAvailable) engine.ProcessFrame(input, output);
            worst = Math.Max(worst, t.ElapsedTicks);
        }
        return TimeSpan.FromSeconds((double)worst / Stopwatch.Frequency);
    }

    [Fact]
    public void The_audio_thread_is_never_held_by_a_stalled_helper_a_full_queue_or_a_broken_pipe()
    {
        // Stalled: the stub processes nothing for 400 ms (under the 500 ms stall rule, so no restart).
        using (var rig = new HelperTestRig(n => new[] { "--stall-after", "4:300" }))
        {
            using var engine = rig.Engine();
            var input = new float[480]; var output = new float[480];
            for (int i = 0; i < 6; i++) { engine.ProcessFrame(input, output); Thread.Sleep(10); }
            // A tight loop with the helper not answering: the writer's queue and the pipe fill,
            // and every call still returns at once.
            var worst = Longest(engine, 5000);
            Assert.True(worst < TimeSpan.FromMilliseconds(50), $"a ProcessFrame call took {worst.TotalMilliseconds} ms");
            Assert.True(Interlocked.Read(ref rig.Host.FramesSent) < 5000, "every frame was sent, so the queue never filled");
        }
        // Broken pipe: the helper closes it; calls stay bounded while the engine goes unavailable.
        using (var rig = new HelperTestRig(n => n == 1 ? new[] { "--break-after", "3" } : Array.Empty<string>()))
        {
            using var engine = rig.Engine();
            var input = new float[480]; var output = new float[480];
            for (int i = 0; i < 6; i++) { engine.ProcessFrame(input, output); Thread.Sleep(10); }
            var deadline = Stopwatch.StartNew();
            var worst = Longest(engine, int.MaxValue, () => engine.IsAvailable && deadline.ElapsedMilliseconds < 5000);
            Assert.False(engine.IsAvailable);
            Assert.Equal(Unavailability.HelperNotRunning, engine.Unavailability);
            Assert.True(worst < TimeSpan.FromMilliseconds(50), $"a ProcessFrame call took {worst.TotalMilliseconds} ms");
        }
    }

    [Fact]
    public void Steady_state_frames_and_unchanged_settings_allocate_nothing_on_the_audio_thread()
    {
        using var rig = new HelperTestRig();
        using var engine = rig.Engine("nr2");
        var input = new float[480]; var output = new float[480];
        for (int i = 0; i < 50; i++) { engine.ProcessFrame(input, output); engine.TrySetSetting("trained_t2", new EngineSettingValue(0.2)); }
        var choice = new EngineSettingValue(0, "trained");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 3000; i++)
        {
            engine.ProcessFrame(input, output);
            engine.TrySetSetting("trained_t2", new EngineSettingValue(0.2));
            engine.TrySetSetting("gain_method", choice);
            _ = engine.IsAvailable;
            _ = engine.LatencySamples;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Unchanged_settings_send_nothing_a_change_sends_once_and_a_refusal_is_not_asked_again()
    {
        using var rig = new HelperTestRig(n => new[] { "--refuse-setting", "gain_method" });
        using var engine = rig.Engine("nr2");
        Assert.Equal(0.2, engine.GetSetting("trained_t2").Number, 9);
        for (int i = 0; i < 200; i++) Assert.True(engine.TrySetSetting("trained_t2", new EngineSettingValue(0.2)));
        Thread.Sleep(300);
        Assert.Equal(0, Interlocked.Read(ref rig.Host.SetSettingsSent));

        Assert.True(engine.TrySetSetting("trained_t2", new EngineSettingValue(0.25)));
        for (int i = 0; i < 200; i++) engine.TrySetSetting("trained_t2", new EngineSettingValue(0.25));
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.SettingAcksReceived) == 1, "the ack");
        Thread.Sleep(300);
        Assert.Equal(1, Interlocked.Read(ref rig.Host.SetSettingsSent));
        Assert.Equal(0.25, engine.GetSetting("trained_t2").Number, 9);

        // Off-range, unknown and wrong-kind values change nothing and send nothing.
        Assert.False(engine.TrySetSetting("trained_t2", new EngineSettingValue(0.31)));
        Assert.False(engine.TrySetSetting("no_such", new EngineSettingValue(1)));
        Assert.False(engine.TrySetSetting("gain_method", new EngineSettingValue(1)));

        // The stub refuses gain_method: the value in effect stays, and asking again sends nothing.
        Assert.True(engine.TrySetSetting("gain_method", new EngineSettingValue(0, "log")));
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.SettingAcksReceived) == 2, "the refusal");
        Assert.Equal("trained", engine.GetSetting("gain_method").Choice);
        for (int i = 0; i < 50; i++) Assert.True(engine.TrySetSetting("gain_method", new EngineSettingValue(0, "log")));
        Thread.Sleep(300);
        Assert.Equal(2, Interlocked.Read(ref rig.Host.SetSettingsSent));
    }

    [Fact]
    public void A_latency_setting_is_only_ever_a_starting_value_and_fixes_the_delay()
    {
        using var rig = new HelperTestRig();
        using var standard = rig.Engine("nnr");
        Assert.Equal(2448 + 960, standard.LatencySamples);
        Assert.False(standard.TrySetSetting("model", new EngineSettingValue(0, "premium")));
        Assert.True(standard.TrySetSetting("model", new EngineSettingValue(0, "standard")));
        using var premium = rig.Engine("nnr", new Dictionary<string, EngineSettingValue> {
            ["model"] = new(0, "premium"), ["mask_floor_db"] = new(-30) });
        Assert.Equal(2448 + 480 + 960, premium.LatencySamples);
        Assert.Equal("premium", premium.GetSetting("model").Choice);
        Assert.Equal(-30, premium.GetSetting("mask_floor_db").Number);
        Assert.True(premium.TrySetSetting("model", new EngineSettingValue(0, "premium")));
        Thread.Sleep(300);
        Assert.Equal(0, Interlocked.Read(ref rig.Host.SetSettingsSent));
        // One stable list, the same object on every read.
        Assert.Same(premium.Settings, premium.Settings);
        Assert.Equal(new[] { "model", "mask_floor_db" }, premium.Settings.Select(s => s.Name));
    }

    [Fact]
    public void Reset_opens_a_new_channel_under_a_new_id_and_frames_are_late_meanwhile()
    {
        using var rig = new HelperTestRig();
        using var engine = rig.Engine();
        var input = new float[480]; var output = new float[480];
        uint first = engine.Channel!.Id;
        engine.Reset();
        Assert.True(engine.IsAvailable);
        Assert.False(engine.ProcessFrame(input, output));
        HelperTestRig.Until(() => engine.Channel is { Opened: true } ch && ch.Id != first, "the new channel");
        Assert.True(engine.Channel!.Id > first);
        Assert.Equal(0ul, engine.Channel.NextSeq);
        Assert.Equal(2448 + 960, engine.LatencySamples);
    }

    [Fact]
    public void The_offline_wait_gives_the_live_delay_without_pacing()
    {
        using var rig = new HelperTestRig();
        using var engine = new WdspHelperEngine(rig.Host, "nnr", DenoiseDirection.Receive, null, offlineWait: true);
        var (signal, _) = Signals(30);
        var output = new float[480];
        for (int f = 0; f < 30; f++)
        {
            bool processed = engine.ProcessFrame(signal.AsSpan(f * 480, 480), output);
            Assert.Equal(f >= 8, processed);
            if (processed) for (int i = 0; i < 480; i++) Assert.Equal(signal[f * 480 + i - 3408], output[i]);
        }
    }
    /// <summary>
    /// An Ok OpenChannelAck whose applied values break the offer (one missing,
    /// out of order, listed twice, or a value its own descriptor refuses) never
    /// makes the engine available: it is a helper this JJ Flexible cannot read,
    /// so a version mismatch, final, with the channel closed and its delay
    /// never taken from the broken ack.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("order")]
    [InlineData("duplicate")]
    [InlineData("refused")]
    public void A_malformed_open_ack_never_makes_the_engine_available(string how)
    {
        using var rig = new HelperTestRig(_ => new[] { "--bad-applied", how });
        var engine = rig.Engine("nnr");
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.HelperVersionMismatch, engine.Unavailability);
        Assert.Contains("applied", engine.UnavailabilityDetail);
        Assert.Equal(0, engine.LatencySamples);
        Assert.Null(engine.Channel);
        Thread.Sleep(300);
        Assert.False(engine.IsAvailable);
        Assert.Single(rig.Host.LaunchedProcessIds); // not a crash: nothing restarts
    }

    /// <summary>
    /// The delay is known before the bank reads it, and never changes after.
    /// An instance whose first open misses the construction wait reports 0
    /// and must stay unavailable with 0 for good, even when the helper's
    /// acknowledgement arrives late: otherwise the bank, which set its delay
    /// line from 0 at construction, would meet a changed delay at its first
    /// produced frame and silence the untouched side for one delay length.
    /// </summary>
    [Fact]
    public void An_open_that_misses_the_construction_wait_never_becomes_available_or_changes_its_delay()
    {
        var policy = HelperTestRig.Fast with { ConstructionWait = TimeSpan.FromMilliseconds(300) };
        using var rig = new HelperTestRig(_ => new[] { "--slow-start", "1200" }, policy);
        var engine = rig.Engine("nnr");
        Assert.False(engine.IsAvailable);
        Assert.Equal(0, engine.LatencySamples);
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Running, "the late handshake");
        Thread.Sleep(800); // room for the late OpenChannelAck
        Assert.False(engine.IsAvailable);
        Assert.Equal(0, engine.LatencySamples);
        // An engine that did open in time, on the same helper, is unaffected.
        var prompt = rig.Engine("nr2");
        Assert.True(prompt.IsAvailable);
        Assert.Equal(3408, prompt.LatencySamples);
        // Not even a retry revives the late one: its delay is 0 for good.
        rig.Host.Retry();
        Thread.Sleep(500);
        Assert.False(engine.IsAvailable);
        Assert.Equal(0, engine.LatencySamples);
        Assert.Null(engine.Channel);
        Assert.True(prompt.IsAvailable);
    }
}
