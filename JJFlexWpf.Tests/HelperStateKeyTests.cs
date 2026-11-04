using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The pure function from a helper state change to the lexicon key Track E
/// will speak, and the events behind it: once per change, never on the audio
/// thread, never on the helper's reader or control thread.
/// </summary>
public sealed class HelperStateKeyTests
{
    public HelperStateKeyTests() => NoiseReductionPerChannelTests.AssertWindowless();

    [Theory]
    [InlineData(WdspHelperStateChange.CapReached, new[] { "nnr" }, "audio.denoise.helper.cap_reached.one")]
    [InlineData(WdspHelperStateChange.CapReached, new[] { "nr2" }, "audio.denoise.helper.cap_reached.one")]
    [InlineData(WdspHelperStateChange.CapReached, new[] { "nnr", "nr2" }, "audio.denoise.helper.cap_reached.several")]
    [InlineData(WdspHelperStateChange.BackAfterCap, new[] { "nnr" }, "audio.denoise.helper.back.one")]
    [InlineData(WdspHelperStateChange.BackAfterCap, new[] { "nnr", "nr2" }, "audio.denoise.helper.back.several")]
    public void Each_change_and_number_of_engines_has_its_key(WdspHelperStateChange change, string[] engines, string key)
    {
        Assert.Equal(key, WdspStateKeys.KeyFor(change, engines));
    }

    [Fact]
    public void With_no_helper_engine_selected_there_is_nothing_to_say()
    {
        foreach (var change in Enum.GetValues<WdspHelperStateChange>())
            Assert.Null(WdspStateKeys.KeyFor(change, Array.Empty<string>()));
    }

    [Fact]
    public void Every_change_has_a_key_and_the_keys_are_distinct_and_well_formed()
    {
        var keys = Enum.GetValues<WdspHelperStateChange>()
            .SelectMany(c => new[] { WdspStateKeys.KeyFor(c, new[] { "nnr" }), WdspStateKeys.KeyFor(c, new[] { "nnr", "nr2" }) })
            .ToArray();
        Assert.All(keys, k => Assert.True(k != null && WdspProtocol.IsKey(k), k));
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void The_cap_and_the_return_are_each_raised_once_off_the_audio_reader_and_control_threads()
    {
        using var rig = new HelperTestRig(n => n <= 4 ? new[] { "--crash-after", "1:9" } : Array.Empty<string>());
        var seen = new ConcurrentQueue<(WdspHelperStateChange Change, string? Thread, bool Pool, int Id)>();
        rig.Host.StateChanged += (_, e) => seen.Enqueue((e.Change, Thread.CurrentThread.Name,
            Thread.CurrentThread.IsThreadPoolThread, Environment.CurrentManagedThreadId));
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        int audioThread = 0;
        var stop = false;
        var audio = new Thread(() =>
        {
            audioThread = Environment.CurrentManagedThreadId;
            var input = new float[480]; var output = new float[480];
            while (!Volatile.Read(ref stop))
            {
                if (nnr.IsAvailable) nnr.ProcessFrame(input, output);
                if (nr2.IsAvailable) nr2.ProcessFrame(input, output);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        audio.Start();
        try
        {
            HelperTestRig.Until(() => seen.Count == 1, "the cap event");
            Thread.Sleep(500);
            rig.Host.Retry();
            HelperTestRig.Until(() => seen.Count == 2, "the back event");
            Thread.Sleep(500);
        }
        finally { Volatile.Write(ref stop, true); audio.Join(); }
        Assert.Equal(new[] { WdspHelperStateChange.CapReached, WdspHelperStateChange.BackAfterCap }, seen.Select(s => s.Change));
        Assert.All(seen, s =>
        {
            Assert.True(s.Pool, "raised off the thread pool");
            Assert.NotEqual(audioThread, s.Id);
            Assert.DoesNotContain("WDSP helper", s.Thread ?? "");
        });
        Assert.Equal("audio.denoise.helper.cap_reached.several", WdspStateKeys.KeyFor(seen.First().Change, new[] { "nnr", "nr2" }));
    }
}
