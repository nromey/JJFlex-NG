using System;
using System.Collections.Generic;
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

public sealed class RnnoiseBindingTests
{
    public RnnoiseBindingTests() => NoiseReductionPerChannelTests.AssertWindowless();
    private static string RepoLibrary() => Path.Combine(RepoPaths.RequireRoot(), "runtimes",
        Environment.Is64BitProcess ? "win-x64" : "win-x86", "native", "rnnoise.dll");

    [Fact]
    public void Default_path_loads_our_architecture_and_exact_repository_binary()
    {
        string path = RnnoiseEngine.DefaultLibraryPath();
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        Assert.Equal(Environment.Is64BitProcess ? Machine.Amd64 : Machine.I386, pe.PEHeaders.CoffHeader.Machine);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(RepoLibrary())), SHA256.HashData(File.ReadAllBytes(path)));
        using var engine = new RnnoiseEngine();
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        Assert.Equal(path, engine.LibraryPath);
        Assert.Equal(480, engine.FrameLength);
    }

    [Fact]
    public void Missing_library_is_unavailable_with_a_path_and_no_throw()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        using var engine = new RnnoiseEngine(libraryPath: missing);
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.LibraryMissing, engine.Unavailability);
        Assert.Contains(missing, engine.UnavailabilityDetail);
    }

    [Fact]
    public void Wrong_architecture_gives_InitFailed_and_explains_the_load_failure()
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "runtimes",
            Environment.Is64BitProcess ? "win-x86" : "win-x64", "native", "rnnoise.dll");
        using var engine = new RnnoiseEngine(libraryPath: path);
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.InitFailed, engine.Unavailability);
        Assert.Contains(path, engine.UnavailabilityDetail);
        Assert.Contains("architecture", engine.UnavailabilityDetail);
    }

    [Fact]
    public void Catalog_probe_checks_machine_without_loading_or_creating_an_engine()
    {
        var previous = RnnoiseEngine.LibraryLocator;
        string path = Path.Combine(Path.GetTempPath(), "rnnoise-probe-" + Guid.NewGuid() + ".dll");
        try {
            // PE header is valid, but NO RNNoise exports: loading/constructing
            // would fail, whereas this intentionally cheap probe must pass.
            File.Copy(typeof(RnnoiseEngine).Assembly.Location, path);
            RnnoiseEngine.LibraryLocator = () => path;
            var entry = new DenoiseEngineCatalog().Find("rnnoise")!;
            using (var stream = File.OpenRead(path)) using (var pe = new PEReader(stream)) {
                Assert.Equal(Environment.Is64BitProcess ? Machine.Amd64 : Machine.I386, pe.PEHeaders.CoffHeader.Machine);
            }
            Assert.Equal(Unavailability.None, entry.Availability());
            File.Delete(path); // probe kept no loaded handle
            Assert.Equal(Unavailability.LibraryMissing, entry.Availability());
            string wrong = Path.Combine(RepoPaths.RequireRoot(), "runtimes",
                Environment.Is64BitProcess ? "win-x86" : "win-x64", "native", "rnnoise.dll");
            RnnoiseEngine.LibraryLocator = () => wrong;
            Assert.Equal(Unavailability.InitFailed, entry.Availability());
        }
        finally { RnnoiseEngine.LibraryLocator = previous; if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Native_frame_size_is_checked_before_state_creation()
    {
        // Patch only the exported frame-size function in a private test copy.
        // This exercises the real load and call rather than testing a copy of
        // the guard. The checked opcode belongs to our pinned native build.
        byte[] image = File.ReadAllBytes(RepoLibrary());
        using var pe = new PEReader(new MemoryStream(image));
        int Offset(int rva) {
            foreach (var section in pe.PEHeaders.SectionHeaders)
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData))
                    return rva - section.VirtualAddress + section.PointerToRawData;
            throw new InvalidDataException("RVA outside sections");
        }
        int table = Offset(pe.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress);
        int count = BitConverter.ToInt32(image, table + 24);
        int functions = Offset(BitConverter.ToInt32(image, table + 28));
        int names = Offset(BitConverter.ToInt32(image, table + 32));
        int ordinals = Offset(BitConverter.ToInt32(image, table + 36));
        int code = -1;
        for (int i = 0; i < count; i++) {
            int name = Offset(BitConverter.ToInt32(image, names + i * 4));
            int end = Array.IndexOf(image, (byte)0, name);
            if (Encoding.ASCII.GetString(image, name, end - name) == "rnnoise_get_frame_size")
                code = Offset(BitConverter.ToInt32(image, functions + BitConverter.ToUInt16(image, ordinals + i * 2) * 4));
        }
        Assert.True(code >= 0);
        Assert.Equal((byte)0xB8, image[code]); // mov eax,480
        Assert.Equal(480, BitConverter.ToInt32(image, code + 1));
        BitConverter.GetBytes(42).CopyTo(image, code + 1);
        string path = Path.Combine(Path.GetTempPath(), "rnnoise-frame-" + Guid.NewGuid() + ".dll");
        File.WriteAllBytes(path, image);
        bool rejected = false;
        try {
            using var engine = new RnnoiseEngine(libraryPath: path);
            rejected = !engine.IsAvailable;
            Assert.False(engine.IsAvailable);
            Assert.Equal(Unavailability.InitFailed, engine.Unavailability);
            Assert.Contains("42-sample", engine.UnavailabilityDetail);
        }
        finally {
            // Rejected handles must be released. If the guard itself regresses,
            // preserve the assertion failure rather than masking it with a
            // mapped-file deletion error from the erroneously accepted API.
            if (rejected) File.Delete(path);
        }
    }

    [Fact]
    public void App_catalog_and_transmit_factory_pin_corrected_scaling()
    {
        using var engine = new DenoiseEngineCatalog().Find("rnnoise")!.Factory!(new Dictionary<string, EngineSettingValue>());
        Assert.Equal(RnnoiseScaling.Corrected, Assert.IsType<RnnoiseEngine>(engine).Scaling);
        using var provider = TxAudioConditioning.CreateNoiseReductionProvider();
        for (int ch = 0; ch < 2; ch++)
            Assert.Equal(RnnoiseScaling.Corrected, Assert.IsType<RnnoiseEngine>(provider.Bank!.EngineFor(ch)).Scaling);
        Assert.False(provider.Enabled);
        Assert.Equal(.8f, provider.Strength);
    }

    [Fact]
    public void Transmit_stage_reports_only_the_delay_actually_applied_to_this_buffer()
    {
        // Exercise the actual coordinator without Attach, player or a device.
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var owner = typeof(TxAudioConditioning);
        var providerField = owner.GetField("_nr", flags)!;
        var appliedField = owner.GetField("_nrAppliedLatency", flags)!;
        object? previousProvider = providerField.GetValue(null);
        object? previousApplied = appliedField.GetValue(null);
        var stage = owner.GetMethod("NrStage", flags)!.CreateDelegate<Action<float[], int, uint>>();
        var readDelay = owner.GetMethod("AppliedNrLatency", flags)!.CreateDelegate<Func<int>>();
        using var provider = TxAudioConditioning.CreateNoiseReductionProvider();
        Assert.True(provider.Bank!.IsAvailable);
        var buffer = new float[960];
        try {
            providerField.SetValue(null, provider);
            stage(buffer, buffer.Length, 48000);
            Assert.Equal(0, readDelay()); // disabled by default
            provider.Enabled = true;
            stage(buffer, buffer.Length, 48000);
            Assert.Equal(480, readDelay());
            provider.Enabled = false;
            stage(buffer, buffer.Length, 48000);
            Assert.Equal(0, readDelay());
            provider.Enabled = true;
            stage(buffer, buffer.Length, 48000);
            Assert.Equal(480, readDelay());
            stage(buffer, buffer.Length, 44100);
            Assert.Equal(0, readDelay());
        }
        finally {
            providerField.SetValue(null, previousProvider);
            appliedField.SetValue(null, previousApplied);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateFn(IntPtr model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyFn(IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float ProcessFn(IntPtr state, [Out] float[] output, [In] float[] input);

    private static float[] Speech()
    {
        string path = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Resources", "ReferenceVoice", "jjflex-reference-voice.wav");
        using var reader = new WaveFileReader(path);
        var source = reader.ToSampleProvider();
        Assert.Equal(48000, source.WaveFormat.SampleRate);
        Assert.Equal(1, source.WaveFormat.Channels);
        var all = new float[(int)reader.SampleCount];
        int at = 0, got;
        while (at < all.Length && (got = source.Read(all.AsSpan(at))) > 0) at += got;
        return all.AsSpan(12 * 48000, 2 * 48000).ToArray();
    }

    [Fact]
    public void Real_speech_gets_exactly_one_16_bit_range_conversion()
    {
        float[] speech = Speech();
        using var engine = new RnnoiseEngine();
        using var historical = new RnnoiseEngine(RnnoiseScaling.Historical);
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        Assert.True(historical.IsAvailable, historical.UnavailabilityDetail);
        IntPtr handle = NativeLibrary.Load(RepoLibrary());
        var create = Marshal.GetDelegateForFunctionPointer<CreateFn>(NativeLibrary.GetExport(handle, "rnnoise_create"));
        var destroy = Marshal.GetDelegateForFunctionPointer<DestroyFn>(NativeLibrary.GetExport(handle, "rnnoise_destroy"));
        var process = Marshal.GetDelegateForFunctionPointer<ProcessFn>(NativeLibrary.GetExport(handle, "rnnoise_process_frame"));
        IntPtr state = create(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, state);
        double difference = 0, expectedEnergy = 0;
        var input = new float[480]; var expected = new float[480];
        var actual = new float[480]; var old = new float[480];
        try {
            for (int at = 0; at < speech.Length; at += 480) {
                for (int i = 0; i < 480; i++) input[i] = speech[at + i] * 32767f;
                process(state, expected, input);
                engine.ProcessFrame(speech.AsSpan(at, 480), actual);
                historical.ProcessFrame(speech.AsSpan(at, 480), old);
                for (int i = 0; i < 480; i++) {
                    float once = expected[i] * 3.051851E-05f;
                    Assert.Equal(once, actual[i]);
                    difference += Math.Abs(once - old[i]); expectedEnergy += once * once;
                }
            }
        }
        finally { destroy(state); NativeLibrary.Free(handle); }
        Assert.True(expectedEnergy > .01, "reference speech must produce audio");
        Assert.True(difference > .1, "real speech must separate single and double scaling");
    }

    [Fact]
    public void Strength_one_bank_output_is_bit_identical_to_real_engine_frames()
    {
        float[] speech = Speech();
        var output = (float[])speech.Clone();
        using var bank = new PerChannelDenoiserBank(() => new RnnoiseEngine(), 1);
        using var engine = new RnnoiseEngine();
        Assert.True(bank.IsAvailable); Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        var expected = new float[480];
        for (int at = 0; at < output.Length; at += 480) {
            engine.ProcessFrame(speech.AsSpan(at, 480), expected);
            bank.Process(output.AsSpan(at, 480), 1);
            Assert.Equal(expected, output.AsSpan(at, 480).ToArray());
        }
        Assert.Equal(0, bank.FallbackFrames);
        Assert.Equal(0, bank.LateFrames);
    }
}
