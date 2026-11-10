using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track S, Part B (#1019): RNNoise's three models, each in its own
/// library, chosen as an Advanced setting; the shared native loader; and loading
/// a model FILE safely. Windowless: every test asserts an MTA thread and touches
/// native code, files and the noise controller over fakes, never a window, never
/// a sound device.
/// </summary>
public sealed class RnnoiseModelTests
{
    public RnnoiseModelTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static string Arch(bool other = false) => Environment.Is64BitProcess ^ other ? "win-x64" : "win-x86";
    private static string NativeFolder(bool other = false) => Path.Combine(RepoPaths.RequireRoot(), "runtimes", Arch(other), "native");
    /// <summary>The repository's copy of a model's library, either architecture.</summary>
    private static string RepoLibrary(string model, bool other = false) => Path.Combine(NativeFolder(other), RnnoiseModels.LibraryFileName(model));

    /// <summary>The copy the engine loads (the test output's runtimes folder), which must be the repository's, byte for byte.</summary>
    private static string Library(string model)
    {
        string path = RnnoiseEngine.DefaultLibraryPath(RnnoiseModels.LibraryFileName(model));
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(RepoLibrary(model))), SHA256.HashData(File.ReadAllBytes(path)));
        return path;
    }
    private static string Digest(float[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));

    // ── Each model is its own library, called through the engine exactly as directly ──

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create0();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create1(IntPtr model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float Process(IntPtr state, [Out] float[] output, [In] float[] input);

    /// <summary>The model's audio and probabilities, bound here by name from the DLL, independently of the shared loader.</summary>
    private static (float[] Audio, float[] Probabilities) Direct(string model, float[] input)
    {
        IntPtr h = NativeLibrary.Load(Library(model));
        try
        {
            T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(h, name));
            (string create, string destroy, string process) = model switch
            {
                RnnoiseModels.Tag => ("rnv02_create", "rnv02_destroy", "rnv02_process_frame"),
                RnnoiseModels.V011 => ("rnvad011_create", "rnvad011_destroy", "rnvad011_reference_frame"),
                _ => ("rnnoise_create", "rnnoise_destroy", "rnnoise_process_frame"),
            };
            IntPtr st = model == RnnoiseModels.Newest ? Get<Create1>(create)(IntPtr.Zero) : Get<Create0>(create)();
            Assert.NotEqual(IntPtr.Zero, st);
            var proc = Get<Process>(process);
            var audio = new float[input.Length];
            var probabilities = new float[input.Length / 480];
            float[] i16 = new float[480], o16 = new float[480];
            for (int f = 0; f < probabilities.Length; f++)
            {
                for (int i = 0; i < 480; i++) i16[i] = input[f * 480 + i] * 32767f;
                probabilities[f] = proc(st, o16, i16);
                for (int i = 0; i < 480; i++) audio[f * 480 + i] = o16[i] * 3.051851E-05f;
            }
            Get<Destroy>(destroy)(st);
            return (audio, probabilities);
        }
        finally { NativeLibrary.Free(h); }
    }

    private static (float[] Audio, float[] Probabilities) ThroughEngine(RnnoiseEngine engine, float[] input)
    {
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        var audio = new float[input.Length];
        var probabilities = new float[input.Length / 480];
        for (int f = 0; f < probabilities.Length; f++)
        {
            engine.ProcessFrame(input.AsSpan(f * 480, 480), audio.AsSpan(f * 480, 480));
            probabilities[f] = engine.LastVoiceProbability;
        }
        return (audio, probabilities);
    }

    [Theory]
    [InlineData(RnnoiseModels.Newest)]
    [InlineData(RnnoiseModels.Tag)]
    [InlineData(RnnoiseModels.V011)]
    public void Each_model_through_the_engine_equals_its_library_called_directly(string model)
    {
        float[] input = RnnoiseVoiceDetectorTests.NoisySpeech();
        Assert.Equal(2000, input.Length / 480);
        using var engine = new RnnoiseEngine(model: model);
        Assert.Equal(model, engine.Model);
        Assert.Equal(Library(model), engine.LibraryPath);
        var viaEngine = ThroughEngine(engine, input);
        var direct = Direct(model, input);
        Assert.Equal(Digest(direct.Audio), Digest(viaEngine.Audio));
        Assert.Equal(Digest(direct.Probabilities), Digest(viaEngine.Probabilities));
    }

    [Fact]
    public void The_three_models_differ_and_the_old_one_is_the_v011_rnnoise_dll_this_repository_shipped()
    {
        float[] input = RnnoiseVoiceDetectorTests.NoisySpeech();
        var outputs = RnnoiseModels.All.ToDictionary(m => m, m => { using var e = new RnnoiseEngine(model: m); return ThroughEngine(e, input); });
        // A loader that always opened the same file would make these equal.
        foreach (var a in RnnoiseModels.All)
            foreach (var b in RnnoiseModels.All.Where(b => string.CompareOrdinal(a, b) < 0))
                Assert.NotEqual(Digest(outputs[a].Audio), Digest(outputs[b].Audio));
        // The digests Track R recorded from the v0.1.1 rnnoise.dll shipped until 2026-11-08.
        Assert.Equal(RnnoiseVoiceDetectorTests.OldAudio, Digest(outputs[RnnoiseModels.V011].Audio));
        Assert.Equal(RnnoiseVoiceDetectorTests.OldProbabilities, Digest(outputs[RnnoiseModels.V011].Probabilities));
    }

    [Theory]
    [InlineData(RnnoiseModels.Newest, 960)]
    [InlineData(RnnoiseModels.Tag, 960)]
    [InlineData(RnnoiseModels.V011, 480)]
    public void Each_model_reports_the_delay_it_has_measured_on_real_speech(string model, int samples)
    {
        using var e = new RnnoiseEngine(model: model);
        Assert.True(e.IsAvailable, e.UnavailabilityDetail);
        Assert.Equal(samples, e.LatencySamples);
        EngineLatencyTests.AssertMeasuredDelay(e);
    }

    [Fact]
    public void A_model_change_in_a_running_slot_moves_its_delay_after_the_warm_handover()
    {
        using var p = new RxAudioPipeline();
        p.NeuralSlot.Enabled = true;
        RunUntil(p, () => p.NeuralSlot.RunningEngineId == "rnnoise" && p.NeuralSlot.RunningLatencySamples > 0);
        Assert.Equal(960, p.NeuralSlot.RunningLatencySamples);
        Assert.True(p.NeuralSlot.TrySetSetting("rnnoise", "model", new(0, RnnoiseModels.V011)));
        RunUntil(p, () => p.NeuralSlot.RunningLatencySamples == 480);
        Assert.Equal(480, p.NeuralSlot.RunningLatencySamples);
        Assert.Equal(RnnoiseModels.V011, ((RnnoiseEngine)p.NeuralSlot.RunningBankForTest!.EngineFor(0)).Model);
        Assert.True(p.NeuralSlot.TrySetSetting("rnnoise", "model", new(0, RnnoiseModels.Tag)));
        RunUntil(p, () => p.NeuralSlot.RunningLatencySamples == 960);
        Assert.Equal(RnnoiseModels.Tag, ((RnnoiseEngine)p.NeuralSlot.RunningBankForTest!.EngineFor(0)).Model);
    }

    private static void RunUntil(RxAudioPipeline p, Func<bool> done)
    {
        var audio = new float[960];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        uint seed = 1019;
        while (!done() && clock.ElapsedMilliseconds < 10000)
        {
            for (int i = 0; i < audio.Length; i++) { seed = seed * 1664525u + 1013904223u; audio[i] = ((seed >> 8) / 16777216f - .5f) * .1f; }
            p.Process(audio);
            System.Threading.Thread.Sleep(1);
        }
    }

    // ── Exports and architecture ──

    private static (Machine Machine, string[] Exports) Read(string file)
    {
        byte[] image = File.ReadAllBytes(file);
        using var pe = new PEReader(new MemoryStream(image));
        int Offset(int rva)
        {
            foreach (var s in pe.PEHeaders.SectionHeaders)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                    return rva - s.VirtualAddress + s.PointerToRawData;
            throw new InvalidDataException("RVA outside sections");
        }
        int table = Offset(pe.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress);
        int count = BitConverter.ToInt32(image, table + 24);
        int names = Offset(BitConverter.ToInt32(image, table + 32));
        var list = new string[count];
        for (int i = 0; i < count; i++)
        {
            int name = Offset(BitConverter.ToInt32(image, names + i * 4));
            int end = Array.IndexOf(image, (byte)0, name);
            list[i] = Encoding.ASCII.GetString(image, name, end - name);
        }
        return (pe.PEHeaders.CoffHeader.Machine, list);
    }

    [Fact]
    public void No_rnnoise_export_outside_rnnoise_dll_and_every_file_is_its_folders_architecture()
    {
        foreach (bool other in new[] { false, true })
        {
            var expected = (Environment.Is64BitProcess ^ other) ? Machine.Amd64 : Machine.I386;
            var all = new List<string>();
            foreach (string model in RnnoiseModels.All)
            {
                var (machine, exports) = Read(RepoLibrary(model, other));
                Assert.Equal(expected, machine);
                Assert.NotEqual(expected == Machine.Amd64 ? Machine.I386 : Machine.Amd64, machine); // the wrong machine fails
                if (model != RnnoiseModels.Newest)
                    Assert.DoesNotContain(exports, e => e.StartsWith("rnnoise_", StringComparison.Ordinal));
                else
                    Assert.Contains("rnnoise_create", exports);   // positive control: the reader sees rnnoise_ names
                all.AddRange(exports);
            }
            Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());   // no name shared between libraries
            Assert.Contains("rnv02_process_frame", all);
        }
    }

    // ── The shared loader ──

    [Fact]
    public void The_old_model_and_the_voice_detector_share_one_handle_each_with_its_own_binding()
    {
        string path = Library(RnnoiseModels.V011);
        using var engine = new RnnoiseEngine(model: RnnoiseModels.V011);
        using var detector = new RnnoiseVoiceDetector();
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        Assert.True(detector.IsAvailable, detector.UnavailabilityDetail);
        Assert.Equal(path, detector.LibraryPath);
        Assert.NotEqual(IntPtr.Zero, NativeEngineLibrary.HandleOf(path));
        Assert.True(NativeEngineLibrary.BindingCount(path) >= 2);
        Assert.Contains("rnvad011_reference_frame", NativeEngineLibrary.BoundNames);
        Assert.Contains("rnvad011_process_frame", NativeEngineLibrary.BoundNames);
    }

    // ── Model files (B4) ──

    [Fact]
    public void A_missing_model_file_is_refused_and_the_process_lives()
    {
        string missing = Path.Combine(Path.GetTempPath(), "jjflex-no-model-" + Guid.NewGuid().ToString("N") + ".bin");
        using var engine = new RnnoiseEngine(modelFilePath: missing);
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.ModelMissing, engine.Unavailability);
        Assert.Contains(missing, engine.UnavailabilityDetail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(3_544_320)]
    public void An_empty_tiny_or_random_model_file_is_refused_and_leaves_the_engine_unavailable(int length)
    {
        string file = Path.Combine(Path.GetTempPath(), "jjflex-model-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[length];
        new Random(1019).NextBytes(bytes);
        File.WriteAllBytes(file, bytes);
        try
        {
            using var engine = new RnnoiseEngine(modelFilePath: file);
            Assert.False(engine.IsAvailable);
            Assert.Equal(Unavailability.InitFailed, engine.Unavailability);
            engine.Reset();                                   // a refused file stays refused, and Reset never reads it
            Assert.False(engine.IsAvailable);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_model_file_loads_only_into_the_newest_library()
    {
        string file = Path.Combine(Path.GetTempPath(), "jjflex-model-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        try
        {
            using var engine = new RnnoiseEngine(model: RnnoiseModels.Tag, modelFilePath: file);
            Assert.False(engine.IsAvailable);
            Assert.Equal(Unavailability.InitFailed, engine.Unavailability);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void The_filename_and_FILE_loaders_are_never_bound_and_the_buffer_loader_is()
    {
        using var engine = new RnnoiseEngine();
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        var bound = NativeEngineLibrary.BoundNames;
        Assert.Contains("rnnoise_model_from_buffer", bound);   // positive control
        Assert.Contains("rnnoise_model_free", bound);
        Assert.DoesNotContain("rnnoise_model_from_filename", bound);
        Assert.DoesNotContain("rnnoise_model_from_file", bound);
        // And no source in the app binds them, by any route (positive control: the
        // same pattern finds the buffer loader's binding in RnnoiseEngine.cs).
        var binding = new System.Text.RegularExpressions.Regex(@"Bind<\w+>\(\s*""(rnnoise_model_from_\w+)""");
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            foreach (System.Text.RegularExpressions.Match m in binding.Matches(File.ReadAllText(file))) found.Add(m.Groups[1].Value);
        }
        Assert.Contains("rnnoise_model_from_buffer", found);
        Assert.Equal(new[] { "rnnoise_model_from_buffer" }, found.Distinct());
    }

    // ── Transmit does not follow the receive model ──

    [Fact]
    public void After_a_receive_model_change_transmit_still_runs_the_newest_model_in_rnnoise_dll()
    {
        using var p = new RxAudioPipeline();
        Assert.True(p.NeuralSlot.TrySetSetting("rnnoise", "model", new(0, RnnoiseModels.V011)));
        using var tx = TxAudioConditioning.CreateNoiseReductionProvider();
        var engine = (RnnoiseEngine)tx.Bank!.EngineFor(0);
        Assert.Equal(RnnoiseModels.Newest, engine.Model);
        Assert.Equal(RnnoiseEngine.LibraryFileName, Path.GetFileName(engine.LibraryPath));
    }
}
