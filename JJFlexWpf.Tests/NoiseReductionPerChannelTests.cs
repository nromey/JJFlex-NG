using System;
using System.Threading;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #732: each PC-side receive noise reducer must keep the two stereo channels
/// apart. A slice panned right has to come out of the right channel as ITSELF,
/// not as whatever happened to be on the left.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins.</b> Until Sprint 48 Track G both
/// <see cref="NoiseReductionProvider"/> and
/// <see cref="SpectralSubtractionProvider"/> read channel 0 only, cleaned it,
/// and wrote the result into every channel. Pan is per slice and set by the
/// operator, so with either reducer on, two slices split left and right would
/// both have played the left one, and a slice panned hard right would have
/// vanished. Nobody reported it; it was found by reading the code (#732), and
/// these tests were run red against that code before the fix went in.
/// </para>
/// <para>
/// <b>How "derives from the right input" is measured.</b> The left channel
/// carries one tone and the right channel a different one (or silence). The
/// right channel's output is then measured at both frequencies. Whatever the
/// reducer does to a tone, the energy at the LEFT tone's frequency in the
/// RIGHT output can only have come from the left input. The default strength
/// keeps 20 percent of the dry signal, so the right tone survives even if a
/// speech-trained model decides a steady tone is noise.
/// </para>
/// <para>
/// <b>Windowless.</b> Nothing here touches the harness's UI thread, a
/// dispatcher or a window. Every test asserts it is running on an MTA thread,
/// where WPF refuses to construct any element at all, so a window could not
/// appear even by accident.
/// </para>
/// </remarks>
public sealed class NoiseReductionPerChannelTests
{
    private const int Rate = 48000;
    private const double LeftHz = 440;
    private const double RightHz = 1250;

    /// <summary>The live receive path hands over 10 ms Opus packets: 480 frames, 960 floats.</summary>
    private const int PacketFloats = 960;

    [Fact]
    public void RnnoiseKeepsTheRightChannelsOwnTone()
    {
        AssertWindowless();
        using var nr = new NoiseReductionProvider(Rate, 2) { Enabled = true };
        AssertEngineRuns(nr);

        float[] stream = TwoTones(seconds: 1.0, LeftHz, RightHz);
        RunInPackets(stream, buf => nr.ProcessInPlace(buf, 0, buf.Length, 2));

        AssertChannelOwnsItsTone(stream, channel: 1, own: RightHz, other: LeftHz);
        AssertChannelOwnsItsTone(stream, channel: 0, own: LeftHz, other: RightHz);
    }

    [Fact]
    public void RnnoiseLeavesASilentRightChannelSilent()
    {
        AssertWindowless();
        using var nr = new NoiseReductionProvider(Rate, 2) { Enabled = true };
        AssertEngineRuns(nr);

        float[] stream = ToneLeftSilenceRight(seconds: 1.0, LeftHz);
        RunInPackets(stream, buf => nr.ProcessInPlace(buf, 0, buf.Length, 2));

        double rightRms = Rms(stream, channel: 1, fromFrame: Rate / 2);
        Assert.True(rightRms < 1e-3,
            $"Right channel was silent going in and came out at RMS {rightRms:G3}: " +
            "the left channel's audio has been written into it.");
    }

    [Fact]
    public void SpectralSubtractionKeepsTheRightChannelsOwnTone()
    {
        AssertWindowless();
        var ss = new SpectralSubtractionProvider(Rate, 2);
        CaptureNoiseProfile(ss);
        ss.Enabled = true;

        float[] stream = TwoTones(seconds: 1.0, LeftHz, RightHz);
        RunInPackets(stream, buf => ss.ProcessInPlace(buf, 0, buf.Length, 2));

        AssertChannelOwnsItsTone(stream, channel: 1, own: RightHz, other: LeftHz);
        AssertChannelOwnsItsTone(stream, channel: 0, own: LeftHz, other: RightHz);
    }

    [Fact]
    public void SpectralSubtractionLeavesASilentRightChannelSilent()
    {
        AssertWindowless();
        var ss = new SpectralSubtractionProvider(Rate, 2);
        CaptureNoiseProfile(ss);
        ss.Enabled = true;

        float[] stream = ToneLeftSilenceRight(seconds: 1.0, LeftHz);
        RunInPackets(stream, buf => ss.ProcessInPlace(buf, 0, buf.Length, 2));

        double rightRms = Rms(stream, channel: 1, fromFrame: Rate / 2);
        Assert.True(rightRms < 1e-3,
            $"Right channel was silent going in and came out at RMS {rightRms:G3}: " +
            "the left channel's audio has been written into it.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// A provider whose engine failed to load passes audio straight through,
    /// which would satisfy every per-channel assertion here without testing
    /// anything. So the engine must be shown to be running first.
    /// </summary>
    private static void AssertEngineRuns(NoiseReductionProvider nr)
    {
        Assert.True(nr.Bank?.IsAvailable == true,
            $"{nr.EngineId} is not available in this process, so a pass here would be vacuous.");
    }

    internal static void AssertWindowless()
    {
        // WPF throws on constructing any element off an STA thread, so an MTA
        // test body cannot put a window on the operator's desktop.
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
    }

    /// <summary>Feed a quiet, independent white noise on both channels until the profile is captured.</summary>
    private static void CaptureNoiseProfile(SpectralSubtractionProvider ss)
    {
        var rng = new Random(732);
        ss.StartSampling(1);
        var buf = new float[PacketFloats];
        for (int guard = 0; ss.IsSampling && guard < 1000; guard++)
        {
            for (int i = 0; i < buf.Length; i++)
                buf[i] = (float)((rng.NextDouble() * 2 - 1) * 0.005);
            ss.ProcessInPlace(buf, 0, buf.Length, 2);
        }
        Assert.True(ss.HasProfile, "the noise profile was never captured, so the test would prove nothing");
    }

    private static void RunInPackets(float[] stream, Action<float[]> process)
    {
        var buf = new float[PacketFloats];
        for (int pos = 0; pos + PacketFloats <= stream.Length; pos += PacketFloats)
        {
            Array.Copy(stream, pos, buf, 0, PacketFloats);
            process(buf);
            Array.Copy(buf, 0, stream, pos, PacketFloats);
        }
    }

    private static float[] TwoTones(double seconds, double leftHz, double rightHz)
    {
        int frames = (int)(Rate * seconds);
        var s = new float[frames * 2];
        for (int n = 0; n < frames; n++)
        {
            s[2 * n] = (float)(0.25 * Math.Sin(2 * Math.PI * leftHz * n / Rate));
            s[2 * n + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * rightHz * n / Rate));
        }
        return s;
    }

    private static float[] ToneLeftSilenceRight(double seconds, double leftHz)
    {
        int frames = (int)(Rate * seconds);
        var s = new float[frames * 2];
        for (int n = 0; n < frames; n++)
            s[2 * n] = (float)(0.25 * Math.Sin(2 * Math.PI * leftHz * n / Rate));
        return s;
    }

    /// <summary>
    /// Assert the channel's energy at its own tone dominates its energy at the
    /// other channel's tone, over the second half of the stream (past any
    /// start-up transient in either reducer).
    /// </summary>
    private static void AssertChannelOwnsItsTone(float[] stream, int channel, double own, double other)
    {
        int from = Rate / 2;
        double eOwn = Goertzel(stream, channel, from, own);
        double eOther = Goertzel(stream, channel, from, other);
        Assert.True(eOwn > 10 * eOther,
            $"Channel {channel}: energy at its own tone ({own} Hz) is {eOwn:G3}, at the other " +
            $"channel's tone ({other} Hz) {eOther:G3}. Its output is not derived from its own input.");
    }

    internal static double Goertzel(float[] interleaved, int channel, int fromFrame, double hz, int channels = 2)
    {
        int frames = interleaved.Length / channels;
        double w = 2 * Math.PI * hz / Rate;
        double coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (int n = fromFrame; n < frames; n++)
        {
            double s0 = interleaved[n * channels + channel] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    internal static double Rms(float[] interleaved, int channel, int fromFrame, int channels = 2)
    {
        int frames = interleaved.Length / channels;
        double sum = 0;
        int n = 0;
        for (int f = fromFrame; f < frames; f++, n++)
        {
            double v = interleaved[f * channels + channel];
            sum += v * v;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}
