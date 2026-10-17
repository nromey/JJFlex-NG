using System;
using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The DeepFilterNet3 engine (#731) against the DLL and model actually
/// committed under <c>runtimes\</c>: it loads, reports its frame, processes,
/// and fails cleanly, never by taking the process down, when its library or
/// model is missing or wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "cleanly" is the claim that matters.</b> DeepFilterNet's C API
/// reports a bad model path by panicking, and the DLL is built with panics set
/// to abort. If the engine ever handed it a missing or wrong file, this test
/// process would simply end. The two refusal tests passing, and the run going
/// on to report them, is the evidence that the hash check stands in front.
/// </para>
/// <para>
/// <b>Windowless and silent</b>: frames in, frames out, on an MTA thread
/// (asserted). Nothing is played.
/// </para>
/// </remarks>
public sealed class DeepFilterNet3EngineTests
{
    private static string NativeDir()
    {
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        return Path.Combine(RepoPaths.RequireRoot(), "runtimes", $"win-{arch}", "native");
    }

    private static string LibraryPath() => Path.Combine(NativeDir(), DeepFilterNet3Engine.LibraryFileName);

    [Fact]
    public void LoadsAndReportsItsFrameLength()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var e = new DeepFilterNet3Engine(libraryPath: LibraryPath());
        Assert.True(e.IsAvailable, "DeepFilterNet3 did not start: " + e.UnavailableReason);
        Assert.Equal(480, e.FrameLength);
        Assert.Equal(48000, e.SampleRate);
        Assert.Equal("", e.UnavailableReason);
    }

    [Fact]
    public void ProcessesFramesAndReportsAFiniteSnr()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var e = new DeepFilterNet3Engine(libraryPath: LibraryPath());
        Assert.True(e.IsAvailable, e.UnavailableReason);

        var rng = new Random(731);
        var input = new float[480];
        var output = new float[480];
        double outEnergy = 0;
        for (int frame = 0; frame < 100; frame++)
        {
            for (int i = 0; i < 480; i++)
            {
                int n = frame * 480 + i;
                input[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 300 * n / 48000.0)
                                   + (rng.NextDouble() - 0.5) * 0.1);
            }
            e.ProcessFrame(input, output);
            foreach (float s in output)
            {
                Assert.False(float.IsNaN(s) || float.IsInfinity(s));
                Assert.InRange(s, -2f, 2f);
                outEnergy += s * s;
            }
            Assert.False(float.IsNaN(e.LastFrameSnrDb) || float.IsInfinity(e.LastFrameSnrDb));
        }
        Assert.Equal(100, e.FramesProcessed);
        Assert.True(outEnergy > 0, "the engine returned silence for 100 frames of tone and noise");
    }

    [Fact]
    public void AnAbsentLibraryIsUnavailableWithAReason()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string nowhere = Path.Combine(Path.GetTempPath(), "jjflex-no-such-dir-" + Guid.NewGuid().ToString("N"), "deepfilter.dll");
        using var e = new DeepFilterNet3Engine(libraryPath: nowhere);
        Assert.False(e.IsAvailable);
        Assert.Contains("was not found", e.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() => e.ProcessFrame(new float[480], new float[480]));
    }

    [Fact]
    public void AModelThatIsNotThePinnedFileIsRefusedBeforeTheLibrarySeesIt()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string bogus = Path.Combine(Path.GetTempPath(), "jjflex-bogus-model-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        File.WriteAllBytes(bogus, new byte[] { 0x1f, 0x8b, 0x08, 0, 1, 2, 3, 4 });
        try
        {
            using var e = new DeepFilterNet3Engine(libraryPath: LibraryPath(), modelPath: bogus);
            Assert.False(e.IsAvailable);
            Assert.Contains("not the pinned file", e.UnavailableReason);
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    [Fact]
    public void AMissingModelIsRefusedBeforeTheLibrarySeesIt()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string missing = Path.Combine(Path.GetTempPath(), "jjflex-missing-model-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        using var e = new DeepFilterNet3Engine(libraryPath: LibraryPath(), modelPath: missing);
        Assert.False(e.IsAvailable);
        Assert.Contains("was not found", e.UnavailableReason);
    }

    [Fact]
    public void TheCommittedModelIsThePinnedFile()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        foreach (string arch in new[] { "x64", "x86" })
        {
            string model = Path.Combine(RepoPaths.RequireRoot(), "runtimes", $"win-{arch}", "native", DeepFilterNet3Engine.ModelFileName);
            using var fs = File.OpenRead(model);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
            Assert.Equal(DeepFilterNet3Engine.ModelSha256, hash, ignoreCase: true);
        }
    }

    [Fact]
    public void ResetStartsAgainFromNothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var e = new DeepFilterNet3Engine(libraryPath: LibraryPath());
        Assert.True(e.IsAvailable, e.UnavailableReason);
        var buf = new float[480];
        e.ProcessFrame(buf, buf.AsSpan().ToArray());
        e.Reset();
        Assert.True(e.IsAvailable, e.UnavailableReason);
        Assert.Equal(0, e.FramesProcessed);
    }

    /// <summary>
    /// The #732 claim again, for the new engine: through the same provider,
    /// with one DeepFilterNet3 instance per channel, the right channel's
    /// output is made from the right channel's input.
    /// </summary>
    [Fact]
    public void ThroughTheProviderEachChannelKeepsItsOwnSignal()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string lib = LibraryPath();
        using var nr = new NoiseReductionProvider(48000, 2, () => new DeepFilterNet3Engine(libraryPath: lib)) { Enabled = true };
        Assert.Equal("deepfilternet3", nr.EngineId);

        int frames = 48000;
        var s = new float[frames * 2];
        for (int n = 0; n < frames; n++)
            s[2 * n] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * n / 48000.0)); // right stays silent
        for (int pos = 0; pos < s.Length; pos += 960)
            nr.ProcessInPlace(s, pos, 960, 2);

        double right = NoiseReductionPerChannelTests.Rms(s, channel: 1, fromFrame: 24000);
        double left = NoiseReductionPerChannelTests.Rms(s, channel: 0, fromFrame: 24000);
        Assert.True(right < 1e-3, $"silent right channel came out at RMS {right:G3}");
        Assert.True(left > 0.01, $"left channel came out at RMS {left:G3}; the dry 20 percent alone should exceed this");
    }
}
