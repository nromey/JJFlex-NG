using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #855 step 1: the per-sound stage timing record, and the offline render the
/// timing tool and the synthesis measurement are built on.
/// </summary>
/// <remarks>
/// <para><b>Windowless and silent.</b> Every test asserts the MTA thread
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>), where WPF
/// cannot build a window, and nothing here opens an audio device: the alert
/// channel is never initialised, earcons are played only inside
/// <see cref="EarconPlayer.RenderOffline"/> (a private mixer on this thread),
/// and the timing probe is read by the test, not by a device.</para>
/// <para><b>What it cannot see:</b> the live device's played count and the
/// speaker. tools/soundtiming measures those against a running app.</para>
/// </remarks>
public sealed class SoundTimingTests
{
    private sealed class Capture : IDisposable
    {
        private readonly Action<string> _savedSink;
        private readonly Func<SoundTiming.DeviceSnapshot>? _savedDevice;
        private readonly bool _savedForced;
        public readonly List<string> Lines = new();

        public Capture(bool forced, Func<SoundTiming.DeviceSnapshot>? device = null)
        {
            _savedSink = SoundTiming.Sink;
            _savedDevice = SoundTiming.AlertDevice;
            _savedForced = SoundTiming.Forced;
            SoundTiming.Sink = l => { lock (Lines) Lines.Add(l); };
            SoundTiming.AlertDevice = device;
            SoundTiming.Forced = forced;
            SoundTiming.Cancel();
        }

        public void Dispose()
        {
            SoundTiming.Cancel();
            SoundTiming.Sink = _savedSink;
            SoundTiming.AlertDevice = _savedDevice;
            SoundTiming.Forced = _savedForced;
        }
    }

    private sealed class Constant : ISampleProvider
    {
        private int _left;
        public Constant(int frames) { _left = frames * 2; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public int Read(Span<float> buffer)
        {
            int n = Math.Min(_left, buffer.Length);
            for (int i = 0; i < n; i++) buffer[i] = 0.25f + i % 7 * 0.01f;
            _left -= n;
            return n;
        }
    }

    private static List<float> ReadAll(ISampleProvider p, int block)
    {
        var all = new List<float>();
        var buf = new float[block];
        int n;
        do { n = p.Read(buf); all.AddRange(buf.Take(n)); } while (n == block);
        return all;
    }

    [Fact]
    public void A_timed_sound_writes_one_line_with_every_stage_when_it_has_left_the_mixer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var device = new SoundTiming.DeviceSnapshot(producedFrames: 44100, playedFrames: 44100 - 4410 * 2,
            bufferMs: 100, bufferCount: 2, sampleRate: 44100);
        using var cap = new Capture(forced: true, () => device);

        SoundTiming.Begin("TxStartTone", "count=2 pitchHz=750");
        long t = SoundTiming.SynthStart();
        SoundTiming.SynthEnd(t);
        SoundTiming.Recipe("beat:hz=300,ms=100,sound=Hollow");
        var probe = SoundTiming.AtHandoff(new Constant(10000));
        Assert.IsType<SoundTiming.Probe>(probe);

        var buf = new float[8820];
        Assert.Equal(8820, probe.Read(buf));
        Assert.Empty(cap.Lines);                 // still sounding: not written yet
        ReadAll(probe, 8820);

        var line = Assert.Single(cap.Lines);
        Assert.StartsWith(SoundTiming.LinePrefix, line);
        Assert.True(SoundTimingLine.TryParse("123 [T5] " + line, out var p));
        Assert.Equal(123, p!.TraceMs);
        Assert.Equal(SoundTiming.FormatVersion, p.Version);
        Assert.Equal("TxStartTone", p.Sound);
        Assert.Equal("count=2 pitchHz=750", p.Detail);
        Assert.Equal("beat:hz=300,ms=100,sound=Hollow", p.Recipe);
        Assert.Equal("rendered", p.Source);
        Assert.Equal(200, p.QueuedMs, 3);         // two 100 ms buffers ahead of it
        Assert.True(p.AskedToOutMs >= 200);
        Assert.True(p.SynthMs >= 0 && p.AskedToHandoffMs >= 0 && p.HandoffToMixMs >= 0);
        Assert.Equal("100x2", p.Buffer);
        Assert.Equal(1, p.Inputs);
        Assert.Equal(20000, p.Samples);
        Assert.True(p.QpcMix >= p.QpcAsked && p.QpcFrequency > 0);
    }

    [Fact]
    public void The_probe_passes_every_sample_through_untouched()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var cap = new Capture(forced: true);
        SoundTiming.Begin("x");
        var timed = ReadAll(SoundTiming.AtHandoff(new Constant(5000)), 1000);
        var plain = ReadAll(new Constant(5000), 1000);
        Assert.Equal(plain, timed);
    }

    [Fact]
    public void A_second_input_handed_off_at_once_counts_on_the_same_sound()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var cap = new Capture(forced: true);
        SoundTiming.Begin("PlayExpand");
        var first = SoundTiming.AtHandoff(new Constant(100));
        var second = SoundTiming.AtHandoff(new Constant(100));
        Assert.IsType<Constant>(second);          // not wrapped: it is the same sound
        ReadAll(first, 1000);
        Assert.True(SoundTimingLine.TryParse(Assert.Single(cap.Lines), out var p));
        Assert.Equal("PlayExpand", p!.Sound);
        Assert.Equal(2, p.Inputs);
        Assert.Equal("unknown", p.Get("queued"));  // no device: says so rather than inventing one
    }

    [Fact]
    public void With_timing_off_nothing_is_wrapped_and_nothing_is_written()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var cap = new Capture(forced: false);
        var saved = JJTrace.Tracing.TheSwitch.Level;
        try
        {
            JJTrace.Tracing.TheSwitch.Level = System.Diagnostics.TraceLevel.Info;
            Assert.False(SoundTiming.Enabled);
            SoundTiming.Begin("TxStartTone");
            var source = new Constant(100);
            Assert.Same(source, SoundTiming.AtHandoff(source));
            Assert.Empty(cap.Lines);

            // The detailed capture turns it on without the harness's switch.
            JJTrace.Tracing.TheSwitch.Level = System.Diagnostics.TraceLevel.Verbose;
            Assert.True(SoundTiming.Enabled);
        }
        finally
        {
            JJTrace.Tracing.TheSwitch.Level = saved;
        }
    }

    [Fact]
    public void A_hand_off_long_after_the_last_with_no_gate_is_its_own_unnamed_sound()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var cap = new Capture(forced: true);
        SoundTiming.Begin("Named");
        ReadAll(SoundTiming.AtHandoff(new Constant(10)), 100);
        System.Threading.Thread.Sleep((int)SoundTiming.SameSoundWindowMs + 30);
        var later = SoundTiming.AtHandoff(new Constant(10));
        Assert.IsType<SoundTiming.Probe>(later);
        ReadAll(later, 100);
        Assert.Equal(new[] { "Named", "unnamed" },
            cap.Lines.Select(l => { SoundTimingLine.TryParse(l, out var p); return p!.Sound; }).ToArray());
    }

    [Fact]
    public void An_offline_render_plays_the_sound_into_a_private_mixer_and_times_nothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var cap = new Capture(forced: true);
        Assert.False(EarconPlayer.AlertChannelLive);   // no device anywhere in this test
        Assert.True(EarconPlayer.EarconsEnabled);

        var samples = EarconPlayer.RenderOffline(EarconPlayer.TxStartTone);

        // 50 + 20 + 50 ms and the 15 ms tail, stereo, at 44.1 kHz.
        Assert.Equal(44100 * (120 + 15) / 1000 + 1, samples.Length / 2);
        Assert.True(samples.Max(Math.Abs) > 0.2f);
        Assert.Empty(cap.Lines);
        Assert.False(EarconPlayer.RenderingOffline);
    }

    [Fact]
    public void An_offline_render_places_a_delayed_figure_at_its_delay_on_this_thread()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // The sampler's second and third figures are played later from a pool
        // thread live; offline they must land in the private mixer, never on
        // the live channel (which here would be the fallback beep).
        var samples = EarconPlayer.RenderOffline(EarconPlayer.VoiceSetSampler);
        int frames = samples.Length / 2;
        Assert.True(frames >= 44100 * (560 + 150) / 1000, $"only {frames} frames");
        float LateEnergy(int fromMs) =>
            samples.Skip(44100 * fromMs / 1000 * 2).Take(44100 / 20 * 2).Max(Math.Abs);
        Assert.True(LateEnergy(580) > 0.05f);
    }
}
