using System;
using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using System.Linq;
using System.Text.Json;
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
    /// <summary>
    /// Noel's +20 dB ruling (2026-10-20 06:21) is for the quiet, fixed-scale off-air render only.
    /// A take this computer recorded is at microphone level and must play exactly as recorded:
    /// lifting it would drive a -8 dBFS voice into the limiter. Pins both sides.
    /// </summary>
    [Fact]
    public void ListeningGainAppliesToTheOffAirRenderOnly()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        float[] voice = { 0.4f, -0.4f, 0.05f, -0.05f };
        var unity = new RecordingPlayer.FixedGainLimiterSampleProvider(new ArraySource(voice), gained: false);
        var unityOut = new float[voice.Length];
        unity.Read(unityOut);
        Assert.Equal(voice, unityOut);
        Assert.False(unity.Limited);

        var offAir = new RecordingPlayer.FixedGainLimiterSampleProvider(new ArraySource(voice), gained: true);
        var offAirOut = new float[voice.Length];
        offAir.Read(offAirOut);
        Assert.Equal(0.5f, offAirOut[2], 3);   // 0.05 x 10, below the ceiling
        Assert.True(offAir.Limited);           // 0.4 x 10 is far above it
    }

    private sealed class ArraySource : ISampleProvider
    {
        private readonly float[] _data;
        private int _at;
        public ArraySource(float[] data) => _data = data;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(Span<float> buffer)
        {
            int n = Math.Min(buffer.Length, _data.Length - _at);
            _data.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }
    }

    [Fact]
    public void OffAirPlaybackAnnouncementReachesOutputOnceWithFixedGain()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string path = Path.Combine(Path.GetTempPath(), "jjflex-playback-speech-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: path);
            RecordingPlayer.AnnounceStarted("Playing the received off-air take.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var speech = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line))
                .Where(d => d.RootElement.GetProperty("event").GetString() == "speech")
                .Select(d => d.RootElement.GetProperty("text").GetString()).ToArray();
            Assert.Single(speech);
            Assert.Equal("Playing the received off-air take. Played 20 dB louder.", speech[0]);
        }
        finally
        {
            OutputChannelRecorder.Configure(render: false, record: false);
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void PlaybackUsesOneFixedGainAndSoftLimitsStrongerTakes()
    {
        var source = new Samples(0.01f, -0.04f, 0.2f);
        var gain = new RecordingPlayer.FixedGainLimiterSampleProvider(source);
        var output = new float[3];
        Assert.Equal(3, gain.Read(output));
        Assert.Equal(0.1f, output[0], 4);
        Assert.Equal(-0.4f, output[1], 4);
        Assert.InRange(output[2], 0.8f, 0.99f);
        Assert.True(gain.Limited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPlaybackRestoresTheExactPriorComputerReceiveMute(bool priorMute)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string path = Path.Combine(Path.GetTempPath(), "jjflex-playback-failure-" + Guid.NewGuid().ToString("N") + ".wav");
        var rig = (FlexBase)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FlexBase));
        rig.LocalReceiveMutedForTakePlayback = priorMute;
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1)))
                writer.Write(new byte[882], 0, 882);
            // The permitted WPF test run has audio rendering gated off, so
            // the mixer refuses the file after the local mute was applied.
            Assert.False(RecordingPlayer.Play(path, "A test take.", rig));
            Assert.Equal(priorMute, rig.LocalReceiveMutedForTakePlayback);
            Assert.False(RecordingPlayer.IsPlaying);
        }
        finally
        {
            RecordingPlayer.Stop();
            try { File.Delete(path); } catch { }
        }
    }

    private sealed class Samples(params float[] samples) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        public int Read(Span<float> buffer)
        {
            samples.AsSpan().CopyTo(buffer);
            return samples.Length;
        }
    }
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
