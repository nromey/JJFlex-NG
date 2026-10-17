using System;
using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Each real engine's <see cref="INeuralDenoiseEngine.LatencySamples"/> is
/// what the engine actually does, found by cross-correlating its output
/// against its input on real speech, not just what its documentation says.
/// </summary>
/// <remarks>
/// The speech is the shipped reference voice
/// (<c>JJFlexWpf/Resources/ReferenceVoice</c>), read from disk and never
/// played. Windowless: MTA thread, asserted.
/// </remarks>
public sealed class EngineLatencyTests
{
    [Fact]
    public void RnnoiseReportsTheDelayItHas()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var e = new RnnoiseEngine();
        Assert.True(e.IsAvailable, "RNNoise did not load, so this proves nothing");
        Assert.Equal(480, e.LatencySamples);
        AssertMeasuredDelay(e);
    }

    [Fact]
    public void DeepFilterNet3ReportsTheDelayItHas()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string lib = Path.Combine(RepoPaths.RequireRoot(), "runtimes", $"win-{arch}", "native", DeepFilterNet3Engine.LibraryFileName);
        using var e = new DeepFilterNet3Engine(libraryPath: lib);
        Assert.True(e.IsAvailable, e.UnavailableReason);
        Assert.Equal(1440, e.LatencySamples);
        AssertMeasuredDelay(e);
    }

    private static void AssertMeasuredDelay(INeuralDenoiseEngine e)
    {
        float[] speech = ReferenceSpeech(startSeconds: 12, seconds: 4);
        int f = e.FrameLength;
        var output = new float[speech.Length / f * f];
        for (int pos = 0; pos + f <= output.Length; pos += f)
            e.ProcessFrame(speech.AsSpan(pos, f), output.AsSpan(pos, f));

        int measured = PeakLag(speech, output, maxLag: 4800);
        // Half a millisecond of slack: a correlation peak on filtered speech
        // can sit a few samples off the true group delay.
        Assert.InRange(measured, e.LatencySamples - 24, e.LatencySamples + 24);
    }

    private static float[] ReferenceSpeech(double startSeconds, double seconds)
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");
        using var reader = new WaveFileReader(path);
        var sp = reader.ToSampleProvider();
        Assert.Equal(48000, sp.WaveFormat.SampleRate);
        Assert.Equal(1, sp.WaveFormat.Channels);
        var all = new float[(int)(reader.SampleCount)];
        int got = 0, n;
        while (got < all.Length && (n = sp.Read(all.AsSpan(got))) > 0) got += n;
        int start = (int)(startSeconds * 48000);
        int len = Math.Min((int)(seconds * 48000), got - start);
        return all.AsSpan(start, len).ToArray();
    }

    private static int PeakLag(float[] a, float[] b, int maxLag)
    {
        int n = Math.Min(a.Length, b.Length) - maxLag;
        int best = 0;
        double bestV = double.MinValue;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (double)a[i] * b[i + lag];
            if (sum > bestV) { bestV = sum; best = lag; }
        }
        return best;
    }
}
