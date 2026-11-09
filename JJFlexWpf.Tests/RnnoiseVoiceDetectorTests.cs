using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The v0.1.1 voice detector kept on its own (#873, ruled 2026-11-08 13:41):
/// <c>rnnoise_vad011.dll</c> and <see cref="RnnoiseVoiceDetector"/>.
/// </summary>
/// <remarks>
/// Windowless: every test asserts an MTA thread and touches only native code and
/// files. The input is 20 s of the shipped reference voice with seeded noise under
/// it, read from disk and never played.
///
/// The positive control has two halves, because the detector and its in-library
/// reference share their weights:
/// - detector against the full v0.1.1 frame, bit for bit, frame by frame; and
/// - that full frame against the v0.1.1 <c>rnnoise.dll</c> this repository shipped
///   until Sprint 49 Track R (x64 SHA-256 97C0940D..., x86 48EF2397...), through a
///   digest of its output recorded from that DLL in a separate process.
/// Each half is shown able to fail: a one-step change to one input sample on the
/// detector's side breaks the first, and one patched weight breaks the second.
/// </remarks>
public sealed class RnnoiseVoiceDetectorTests
{
    // SHA-256 of the 2,000 voice probabilities (little-endian float32), then of the
    // 960,000 denoised samples, that the PREVIOUSLY SHIPPED v0.1.1 rnnoise.dll returned
    // for NoisySpeech() through the Corrected scaling. Recorded 2026-11-08 by Track R
    // from that DLL in a separate test run on each architecture; x64 (97C0940D...) and
    // x86 (48EF2397...) gave the same two digests, so one pair serves both.
    private const string OldProbabilities = "FDFD832DF0B9C0DA14C8D7C6A37DBC25CE0DEF25CA659EC4457D738B4644084D";
    private const string OldAudio = "330753EADA5F71AB51F4F6109482BBC6EFC521F71486C915C92827F4FCC76342";

    public RnnoiseVoiceDetectorTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static string RepoLibrary(bool otherArchitecture = false) => Path.Combine(RepoPaths.RequireRoot(), "runtimes",
        Environment.Is64BitProcess ^ otherArchitecture ? "win-x64" : "win-x86", "native", RnnoiseVoiceDetector.LibraryFileName);

    /// <summary>20 s of the reference voice from 12 s, with seeded white noise about 10 dB under its active level.</summary>
    internal static float[] NoisySpeech()
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");
        using var reader = new WaveFileReader(path);
        var source = reader.ToSampleProvider();
        Assert.Equal(48000, source.WaveFormat.SampleRate);
        Assert.Equal(1, source.WaveFormat.Channels);
        var all = new float[(int)reader.SampleCount];
        int at = 0, got;
        while (at < all.Length && (got = source.Read(all.AsSpan(at))) > 0) at += got;
        var speech = all.AsSpan(12 * 48000, 20 * 48000).ToArray();
        uint seed = 873;
        for (int i = 0; i < speech.Length; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            speech[i] += ((seed >> 8) / 16777216f - .5f) * .02f;
        }
        return speech;
    }

    private static (float[] Probabilities, float[] Audio) Reference(RnnoiseVoiceDetector detector, float[] input)
    {
        var probabilities = new float[input.Length / RnnoiseVoiceDetector.Frame];
        var audio = new float[input.Length];
        for (int f = 0; f < probabilities.Length; f++)
            probabilities[f] = detector.ReferenceFrame(input.AsSpan(f * 480, 480), audio.AsSpan(f * 480, 480));
        return (probabilities, audio);
    }

    private static float[] Detect(RnnoiseVoiceDetector detector, float[] input)
    {
        var probabilities = new float[input.Length / RnnoiseVoiceDetector.Frame];
        for (int f = 0; f < probabilities.Length; f++)
            probabilities[f] = detector.Process(input.AsSpan(f * 480, 480));
        return probabilities;
    }

    private static string Digest(float[] values) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));

    [Fact]
    public void Default_path_loads_our_architecture_and_the_repository_binary()
    {
        string path = RnnoiseVoiceDetector.DefaultLibraryPath();
        Assert.Equal(RnnoiseVoiceDetector.LibraryFileName, Path.GetFileName(path));
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
            Assert.Equal(Environment.Is64BitProcess ? Machine.Amd64 : Machine.I386, pe.PEHeaders.CoffHeader.Machine);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(RepoLibrary())), SHA256.HashData(File.ReadAllBytes(path)));
        using var detector = new RnnoiseVoiceDetector();
        Assert.True(detector.IsAvailable, detector.UnavailabilityDetail);
        Assert.Equal(path, detector.LibraryPath);
    }

    [Fact]
    public void Detector_exports_only_its_own_names_and_never_an_rnnoise_one()
    {
        // Read both shipped libraries' export tables side by side. Two RNNoise builds
        // in one process must never be bound by the same name.
        string[] detector = ExportNames(File.ReadAllBytes(RepoLibrary()));
        string[] engine = ExportNames(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(RepoLibrary())!, RnnoiseEngine.LibraryFileName)));
        Assert.Equal(new[] { "rnvad011_create", "rnvad011_destroy", "rnvad011_frame_size", "rnvad011_process_frame", "rnvad011_reference_frame" }, detector);
        Assert.DoesNotContain(detector, n => n.StartsWith("rnnoise_", StringComparison.Ordinal));
        Assert.Empty(detector.Intersect(engine));
        // Positive control on the reader itself: it does see the engine's names.
        Assert.Contains("rnnoise_process_frame", engine);
        Assert.Contains("rnnoise_create", engine);
    }

    [Fact]
    public void Detector_probability_equals_the_full_v011_frame_bit_for_bit()
    {
        float[] input = NoisySpeech();
        using var detector = new RnnoiseVoiceDetector();
        using var full = new RnnoiseVoiceDetector();
        Assert.True(detector.IsAvailable, detector.UnavailabilityDetail);
        float[] detected = Detect(detector, input);
        float[] expected = Reference(full, input).Probabilities;
        Assert.Equal(2000, detected.Length);
        for (int f = 0; f < detected.Length; f++)
            Assert.True(BitConverter.SingleToInt32Bits(expected[f]) == BitConverter.SingleToInt32Bits(detected[f]),
                $"frame {f}: full frame {expected[f]:R}, detector {detected[f]:R}");
        // The match is not trivial: plenty of speech and plenty of not-speech.
        int voiced = detected.Count(p => p > .5f), quiet = detected.Count(p => p < .1f);
        Assert.True(voiced > 400, $"only {voiced} frames above 0.5");
        Assert.True(quiet > 200, $"only {quiet} frames below 0.1");
    }

    [Fact]
    public void The_comparison_fails_when_the_detector_sees_one_sample_one_step_different()
    {
        // The detector's side alone gets one sample changed by one 16-bit step
        // in one frame. The frame-by-frame equality above must then be able to fail.
        float[] input = NoisySpeech();
        float[] nudged = (float[])input.Clone();
        nudged[600 * 480 + 100] += 1f / 32767f;
        using var detector = new RnnoiseVoiceDetector();
        using var full = new RnnoiseVoiceDetector();
        float[] detected = Detect(detector, nudged);
        float[] expected = Reference(full, input).Probabilities;
        Assert.True(Enumerable.Range(0, 600).All(f => detected[f] == expected[f]), "frames before the change must still agree");
        Assert.Contains(Enumerable.Range(600, 1400), f => BitConverter.SingleToInt32Bits(detected[f]) != BitConverter.SingleToInt32Bits(expected[f]));
    }

    [Fact]
    public void Full_frame_reproduces_the_v011_rnnoise_dll_this_repository_shipped()
    {
        float[] input = NoisySpeech();
        using var full = new RnnoiseVoiceDetector();
        var (probabilities, audio) = Reference(full, input);
        Assert.Equal(OldProbabilities, Digest(probabilities));
        Assert.Equal(OldAudio, Digest(audio));
    }

    [Fact]
    public void One_patched_weight_breaks_the_match_with_the_shipped_v011()
    {
        // vad_output_weights from rnn_data.c at v0.1.1, as signed bytes. Patch
        // weight 5 (20) to 21 in a private copy; the shipped file is untouched.
        sbyte[] weights = { 127, 127, 127, 127, 127, 20, 127, -126, -126, -54, 14, 125, -126, -126, 127, -125, -126, 127, -127, -127, -57, -30, 127, 80 };
        byte[] pattern = weights.Select(w => unchecked((byte)w)).ToArray();
        byte[] image = File.ReadAllBytes(RepoLibrary());
        int[] hits = Enumerable.Range(0, image.Length - pattern.Length).Where(i => image.AsSpan(i, pattern.Length).SequenceEqual(pattern)).ToArray();
        Assert.Single(hits);
        image[hits[0] + 5] = 21;
        string path = Path.Combine(Path.GetTempPath(), "rnvad011-weight-" + Guid.NewGuid() + ".dll");
        File.WriteAllBytes(path, image);
        // The handle stays loaded for the process's life (as RnnoiseEngine's do), so the
        // temp file is left for the OS to clear rather than deleted here.
        using var patched = new RnnoiseVoiceDetector(path);
        Assert.True(patched.IsAvailable, patched.UnavailabilityDetail);
        float[] input = NoisySpeech();
        var (probabilities, audio) = Reference(patched, input);
        Assert.NotEqual(OldProbabilities, Digest(probabilities));
        // The audio digest is NOT asserted: the voice output feeds nothing else in v0.1.1,
        // which is exactly why the branch can be run on its own.
        using var shipped = new RnnoiseVoiceDetector();
        var good = Reference(shipped, input);
        Assert.Contains(Enumerable.Range(0, good.Probabilities.Length), f => good.Probabilities[f] != probabilities[f]);
        Assert.Equal(Digest(good.Audio), Digest(audio));
    }

    [Fact]
    public void Frame_size_and_silence_follow_v011()
    {
        using var detector = new RnnoiseVoiceDetector();
        Assert.Equal(480, RnnoiseVoiceDetector.Frame);
        Assert.Null(RnnoiseVoiceDetector.FrameSizeProblem(480));
        Assert.Contains("42-sample", RnnoiseVoiceDetector.FrameSizeProblem(42));
        Assert.Equal(0f, detector.Process(new float[480]));
        Assert.Throws<ArgumentException>(() => detector.Process(new float[479]));
    }

    [Fact]
    public void Missing_library_is_unavailable_with_a_path_and_no_throw()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        using var detector = new RnnoiseVoiceDetector(missing);
        Assert.False(detector.IsAvailable);
        Assert.Equal(Unavailability.LibraryMissing, detector.Unavailability);
        Assert.Contains(missing, detector.UnavailabilityDetail);
        Assert.Throws<InvalidOperationException>(() => detector.Process(new float[480]));
    }

    [Fact]
    public void Wrong_architecture_gives_InitFailed_and_explains_the_load_failure()
    {
        string path = RepoLibrary(otherArchitecture: true);
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
            Assert.Equal(Environment.Is64BitProcess ? Machine.I386 : Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        using var detector = new RnnoiseVoiceDetector(path);
        Assert.False(detector.IsAvailable);
        Assert.Equal(Unavailability.InitFailed, detector.Unavailability);
        Assert.Contains(path, detector.UnavailabilityDetail);
        Assert.Contains("architecture", detector.UnavailabilityDetail);
    }

    [Fact]
    public void Detector_and_the_02_engine_run_side_by_side_in_one_process()
    {
        float[] input = NoisySpeech();
        using var engine = new RnnoiseEngine();
        using var detector = new RnnoiseVoiceDetector();
        using var alone = new RnnoiseVoiceDetector();
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        var output = new float[480];
        var together = new float[input.Length / 480];
        for (int f = 0; f < together.Length; f++)
        {
            engine.ProcessFrame(input.AsSpan(f * 480, 480), output);
            together[f] = detector.Process(input.AsSpan(f * 480, 480));
        }
        Assert.Equal(Detect(alone, input), together);
    }

    [Fact]
    public void Nothing_in_the_app_calls_the_detector_yet()
    {
        // Ruled 2026-11-08 (#873): stored, not wired. Its callers are its tests (and an
        // offline bench under tools/) until Noel rules otherwise. When he does, this
        // test is the one to retire, deliberately.
        string root = RepoPaths.RequireRoot();
        var mentions = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !p.Contains(Path.DirectorySeparatorChar + "build-native" + Path.DirectorySeparatorChar))
            .Where(p => File.ReadAllText(p).Contains("RnnoiseVoiceDetector", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        // Positive control: the scan does find the class's own file and this one.
        Assert.Contains("JJFlexWpf/RnnoiseVoiceDetector.cs", mentions);
        Assert.Contains("JJFlexWpf.Tests/RnnoiseVoiceDetectorTests.cs", mentions);
        var appCallers = mentions.Where(p => p != "JJFlexWpf/RnnoiseVoiceDetector.cs"
            && !p.StartsWith("JJFlexWpf.Tests/", StringComparison.Ordinal)
            && !p.StartsWith("tools/", StringComparison.Ordinal)).ToArray();
        Assert.Empty(appCallers);
    }

    private static string[] ExportNames(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        int Offset(int rva)
        {
            foreach (var section in pe.PEHeaders.SectionHeaders)
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData))
                    return rva - section.VirtualAddress + section.PointerToRawData;
            throw new InvalidDataException("RVA outside sections");
        }
        int table = Offset(pe.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress);
        int count = BitConverter.ToInt32(image, table + 24);
        int names = Offset(BitConverter.ToInt32(image, table + 32));
        var result = new string[count];
        for (int i = 0; i < count; i++)
        {
            int name = Offset(BitConverter.ToInt32(image, names + i * 4));
            int end = Array.IndexOf(image, (byte)0, name);
            result[i] = Encoding.ASCII.GetString(image, name, end - name);
        }
        return result.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }
}
