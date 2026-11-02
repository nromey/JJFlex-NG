using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// The adaptive noise estimators libspecbleach offers. The numbers are the
    /// library's own (<c>SpecbleachNoiseEstimationMethod</c>), part of its
    /// public contract.
    /// </summary>
    public enum SpecbleachNoiseEstimator
    {
        /// <summary>Speech-presence-probability MMSE, the library's default.</summary>
        SppMmse = 0,
        /// <summary>Brandt's trimmed mean.</summary>
        Brandt = 1,
        /// <summary>Martin's minimum statistics.</summary>
        Martin = 2,
    }

    /// <summary>
    /// NR4: libspecbleach (Luciano Dato, LGPL 2.1) as a mono engine, through
    /// its C API in <c>specbleach.dll</c>. Adaptive spectral subtraction: it
    /// keeps estimating the noise continuously, so there is nothing to
    /// capture first. 48 kHz, fed 480 samples at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sprint 49 Track C: built, tested and benched, NOT selectable by an
    /// operator.</b> The catalog's <c>nr4</c> row builds it, so code and a
    /// stored selection can reach it, but no menu, key or control does.
    /// </para>
    /// <para>
    /// <b>The binary is pinned</b> (tag v0.4.1, built from the source vendored
    /// in <c>third_party/libspecbleach</c>), and how it was built is in
    /// <c>runtimes/win-x64/native/README.md</c>. The LGPL lets an operator
    /// replace it with their own build, so a different version is traced, not
    /// refused; the size check on the parameter block is what makes a changed
    /// layout fail safely.
    /// </para>
    /// <para>
    /// <b>Loaded by absolute path</b>, bound by name from the handle, never
    /// through the Windows search order. In the app the path comes from
    /// <c>NativeLoader.vb</c> (via <see cref="LibraryLocator"/>); tests and
    /// the bench pass a path.
    /// </para>
    /// <para>
    /// <b>The mode.</b> By default the instance is built in the library's
    /// low-latency mode with a 512-sample (10.7 ms) frame: causal, no
    /// look-ahead, temporal smoothing only. The library's default mode adds
    /// look-ahead, and its 2D smoothing blocks the calling thread on worker
    /// threads, which must never happen on the audio thread. Both are
    /// constructor parameters so the bench can render the default mode beside
    /// it; the live app uses the defaults.
    /// </para>
    /// <para>
    /// <b>Real time.</b> <see cref="TrySetSetting"/> only records the value
    /// (no allocation, nothing published when it is unchanged).
    /// <see cref="ProcessFrame"/> reloads the parameter block between frames,
    /// and only when a value changed; with the reduction curve off and the
    /// estimator fixed for the instance's life, that reload copies structs and
    /// allocates nothing (read in <c>load_reduction_parameters</c>,
    /// <c>src/processors/denoiser/spectral_denoiser.c</c>, at the tag).
    /// Creating and freeing the instance are setup-only, and happen in the
    /// constructor, <see cref="Reset"/> and <see cref="Dispose"/>, never on
    /// the live audio path.
    /// </para>
    /// </remarks>
    public sealed class SpecbleachEngine : IDenoiseEngine
    {
        /// <summary>The shipped file name (CMake's OUTPUT_NAME upstream; not renamed).</summary>
        public const string LibraryFileName = "specbleach.dll";

        /// <summary>The frame the live engine uses: 512 samples, about 10.7 ms at 48 kHz.</summary>
        public const int DefaultFrameSamples = 512;

        /// <summary><c>SPECBLEACH_INIT_LOW_LATENCY</c> in the header at the tag.</summary>
        private const uint LowLatencyFlag = 1u << 0;

        private const int Block = 480;

        /// <summary>
        /// Where the app's copy of <see cref="LibraryFileName"/> lives. Set by
        /// <c>NativeLoader.Initialize</c>; when unset (tests, the bench) the
        /// default is <c>runtimes\win-{arch}\native</c> under the app base directory.
        /// </summary>
        public static Func<string?>? LibraryLocator { get; set; }

        // The live settings, in this order. Index into _pending.
        private const int ReductionDb = 0, Smoothing = 1, Whitening = 2, MaskingDepth = 3, Suppression = 4;

        /// <summary>
        /// The settings an operator surface may offer. Names are stable keys,
        /// never renamed once shipped. Each default is the library's own
        /// default at the tag (<c>specbleach_denoiser_get_default_parameters</c>),
        /// which <c>SpecbleachEngineTests</c> checks against the loaded DLL.
        /// None changes latency: the header says smoothing changes never do.
        /// </summary>
        public static IReadOnlyList<EngineSettingDescriptor> SettingDescriptors { get; } = new[] {
            // How much noise is taken off. reduction_gain = 10^(-dB/20); 0 dB = no reduction.
            new EngineSettingDescriptor("reduction_db", "audio.denoise.reduction_db", EngineSettingKind.Number,
                new EngineSettingValue(20), 0, 40, 1, "audio.denoise.db"),
            new EngineSettingDescriptor("smoothing", "audio.denoise.smoothing", EngineSettingKind.Number,
                new EngineSettingValue(0.5), 0, 1, 0.05),
            new EngineSettingDescriptor("whitening", "audio.denoise.whitening", EngineSettingKind.Number,
                new EngineSettingValue(0), 0, 1, 0.05),
            new EngineSettingDescriptor("masking_depth", "audio.denoise.masking_depth", EngineSettingKind.Number,
                new EngineSettingValue(0.5), 0, 1, 0.05),
            new EngineSettingDescriptor("suppression", "audio.denoise.suppression", EngineSettingKind.Number,
                new EngineSettingValue(1), 0, 1, 0.05),
        };

        private readonly string? _libraryPath;
        private readonly uint _flags;
        private readonly int _frameSamples;
        private readonly SpecbleachNoiseEstimator _estimator;

        // Written by any thread through TrySetSetting; read by the audio
        // thread at a frame boundary when _dirty says something changed.
        private readonly double[] _pending = new double[5];
        private int _dirty;

        private Api? _api;
        private IntPtr _state;
        // The block handed to the library. Kept in a field and reused, so a
        // reload allocates nothing on the audio thread.
        private SpecbleachDenoiserParameters _parameters;

        /// <param name="initial">Starting values by setting name (the slot's factory hands every descriptor's value); null for the defaults.</param>
        /// <param name="libraryPath">Absolute path to the DLL, or null for the app's copy.</param>
        /// <param name="estimator">
        /// The adaptive noise estimator. Fixed for the instance's life on
        /// purpose: changing it makes the library free and re-create the
        /// estimator, which allocates and throws away the learned noise.
        /// </param>
        /// <param name="lowLatency">True for the library's low-latency mode (the live default); false for its default mode, for the bench.</param>
        /// <param name="frameSamples">The STFT frame, in samples at 48 kHz. 512 for the live engine.</param>
        public SpecbleachEngine(IReadOnlyDictionary<string, EngineSettingValue>? initial = null,
                                string? libraryPath = null,
                                SpecbleachNoiseEstimator estimator = SpecbleachNoiseEstimator.SppMmse,
                                bool lowLatency = true, int frameSamples = DefaultFrameSamples)
        {
            _libraryPath = libraryPath;
            _estimator = estimator;
            _flags = lowLatency ? LowLatencyFlag : 0u;
            _frameSamples = frameSamples;
            for (int i = 0; i < SettingDescriptors.Count; i++)
            {
                var d = SettingDescriptors[i];
                _pending[i] = initial != null && initial.TryGetValue(d.Name, out var v) && d.Accepts(v)
                    ? v.Number : d.Default.Number;
            }
            Start();
        }

        public string Id => "nr4";

        public int SampleRate => 48000;

        /// <summary>480: the library buffers internally and takes any block size, so the bank adds no carry.</summary>
        public int FrameLength => Block;

        public bool IsAvailable => _state != IntPtr.Zero;

        /// <summary>
        /// What the library reports (<c>specbleach_denoiser_get_latency</c>),
        /// read once after the instance is created; the header says it is
        /// stable for the instance's life. 512 in the live low-latency mode.
        /// Zero while the engine has never loaded.
        /// </summary>
        public int LatencySamples { get; private set; }

        public Unavailability Unavailability { get; private set; }
        public string UnavailabilityDetail { get; private set; } = "";

        public IReadOnlyList<EngineSettingDescriptor> Settings => SettingDescriptors;

        public EngineSettingValue GetSetting(string name)
        {
            int index = IndexOf(name);
            if (index < 0) throw new ArgumentException("Unknown setting", nameof(name));
            return new EngineSettingValue(Volatile.Read(ref _pending[index]));
        }

        /// <summary>
        /// Records a value to apply at the next frame boundary. Allocates
        /// nothing, and marks nothing when the value is unchanged (the slot
        /// calls this for every stored value on every buffer).
        /// </summary>
        public bool TrySetSetting(string name, EngineSettingValue value)
        {
            int index = IndexOf(name);
            if (index < 0 || !SettingDescriptors[index].Accepts(value)) return false;
            if (Volatile.Read(ref _pending[index]) == value.Number) return true;
            Volatile.Write(ref _pending[index], value.Number);
            Volatile.Write(ref _dirty, 1);
            return true;
        }

        /// <summary>The DLL actually loaded, or the path that was tried.</summary>
        public string LibraryPath { get; private set; } = "";

        /// <summary>What the loaded library says it is (<c>specbleach_get_version_string</c>). Traced, never enforced.</summary>
        public string LibraryVersion { get; private set; } = "";

        /// <summary>Frames processed since creation or <see cref="Reset"/>.</summary>
        public long FramesProcessed { get; private set; }

        /// <summary>The library's own defaults, as read through this binding's struct when the instance started.</summary>
        internal SpecbleachDenoiserParameters LibraryDefaults { get; private set; }

        /// <summary>The block most recently loaded into the library.</summary>
        internal SpecbleachDenoiserParameters AppliedParameters => _parameters;

        /// <summary>How many times the parameter block has been loaded (once at start, then once per changed frame).</summary>
        internal int ParameterLoads { get; private set; }

        /// <summary>The library's frame and FFT sizes, as it reported them.</summary>
        internal int LibraryFrameSize { get; private set; }
        internal int LibraryFftSize { get; private set; }

        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (_state == IntPtr.Zero)
                throw new InvalidOperationException("NR4 is not available: " + UnavailabilityDetail);
            if (input.Length != Block || output.Length != Block)
                throw new ArgumentException($"NR4 frames are exactly {Block} samples.");

            if (Interlocked.Exchange(ref _dirty, 0) != 0)
            {
                Compose();
                if (!LoadParameters())
                {
                    // Cannot happen with the size verified at start; if it
                    // does, try again next frame and let the bank pass this one through.
                    Volatile.Write(ref _dirty, 1);
                    throw new InvalidOperationException("libspecbleach refused a parameter block it accepted at start.");
                }
            }

            // Both spans are pinned for the call by passing their first
            // element by reference; nothing is copied and nothing allocated.
            if (_api!.Process(_state, Block, ref MemoryMarshal.GetReference(input), ref MemoryMarshal.GetReference(output)) == 0)
                throw new InvalidOperationException("libspecbleach refused a frame (invalid arguments).");
            FramesProcessed++;
            return true;
        }

        /// <summary>
        /// Forget everything, as if newly created. libspecbleach's own
        /// <c>specbleach_denoiser_reset_dsp_state</c> is setup-only (it tears
        /// down and rebuilds the engines inside), and the bank never calls
        /// Reset on the live path, so this simply frees the instance and makes
        /// a new one, the way the DeepFilterNet3 engine does. Settings carry over.
        /// </summary>
        public void Reset()
        {
            Stop();
            FramesProcessed = 0;
            Start();
        }

        /// <summary>
        /// Frees the instance. Setup-only in the library's terms: the receive
        /// slot only disposes a bank after the audio thread has let go of it,
        /// so this never runs while a frame is inside the library. Idempotent.
        /// </summary>
        public void Dispose() => Stop();

        /// <summary>
        /// Whether NR4 can be offered, WITHOUT loading the library: the file is
        /// present where the engine would look. A file that is present but will
        /// not load reads as available here and fails at build, which the slot
        /// reports.
        /// </summary>
        public static Unavailability Probe()
        {
            string? library;
            try { library = LibraryLocator?.Invoke(); }
            catch { return Unavailability.LibraryMissing; }
            if (string.IsNullOrEmpty(library)) library = DefaultLibraryPath();
            return File.Exists(library) ? Unavailability.None : Unavailability.LibraryMissing;
        }

        /// <summary><c>runtimes\win-{arch}\native\specbleach.dll</c> under the app base directory.</summary>
        public static string DefaultLibraryPath()
        {
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            return Path.Combine(AppContext.BaseDirectory, "runtimes", $"win-{arch}", "native", LibraryFileName);
        }

        /// <summary>The block's size as this binding lays it out: 88 bytes on x64, 76 on x86.</summary>
        internal static readonly uint ParametersSize = (uint)Marshal.SizeOf<SpecbleachDenoiserParameters>();

        /// <summary>
        /// Hand the library a block with a stated size, for the layout tests:
        /// the library refuses any size but its own before reading the block.
        /// </summary>
        internal bool LoadParametersForTest(SpecbleachDenoiserParameters block, uint size)
            => _state != IntPtr.Zero && _api!.Load(_state, ref block, size) != 0;

        private static int IndexOf(string name)
        {
            for (int i = 0; i < SettingDescriptors.Count; i++)
                if (SettingDescriptors[i].Name == name) return i;
            return -1;
        }

        /// <summary>Write the pending settings into the reused block. No allocation.</summary>
        private void Compose()
        {
            _parameters.ReductionGain = (float)Math.Pow(10, -Volatile.Read(ref _pending[ReductionDb]) / 20.0);
            _parameters.SmoothingFactor = (float)Volatile.Read(ref _pending[Smoothing]);
            _parameters.WhiteningFactor = (float)Volatile.Read(ref _pending[Whitening]);
            _parameters.MaskingDepth = (float)Volatile.Read(ref _pending[MaskingDepth]);
            _parameters.SuppressionStrength = (float)Volatile.Read(ref _pending[Suppression]);
        }

        private bool LoadParameters()
        {
            bool loaded = _api!.Load(_state, ref _parameters, ParametersSize) != 0;
            if (loaded) ParameterLoads++;
            return loaded;
        }

        private void Stop()
        {
            if (_state != IntPtr.Zero)
            {
                IntPtr state = _state;
                _state = IntPtr.Zero; // first, so a frame after Dispose throws instead of touching freed memory
                _api!.Free(state);
            }
        }

        private void Start()
        {
            string lib = _libraryPath ?? LocateLibrary();
            LibraryPath = lib;

            if (!File.Exists(lib))
            {
                Unavailable(Unavailability.LibraryMissing, $"{LibraryFileName} was not found at {lib}.");
                return;
            }

            var api = Api.Load(lib, out string? loadProblem);
            if (api == null)
            {
                Unavailable(Unavailability.InitFailed, loadProblem ?? $"{LibraryFileName} would not load.");
                return;
            }
            _api = api;
            LibraryVersion = api.Version;

            float frameMs = _frameSamples * 1000f / SampleRate;
            // The library truncates frame_ms back to samples; make sure the
            // float it is given cannot truncate to one sample short.
            if ((int)((double)frameMs / 1000.0 * SampleRate) < _frameSamples) frameMs = MathF.BitIncrement(frameMs);

            IntPtr state = api.Initialize((uint)SampleRate, frameMs, _flags);
            if (state == IntPtr.Zero)
            {
                Unavailable(Unavailability.InitFailed, $"libspecbleach would not create an instance ({_frameSamples}-sample frame, flags {_flags}).");
                return;
            }

            int frame = checked((int)api.GetFrameSize(state));
            int fft = checked((int)api.GetFftSize(state));
            LibraryFrameSize = frame;
            LibraryFftSize = fft;
            bool liveShape = _flags == LowLatencyFlag && _frameSamples == DefaultFrameSamples;
            if (frame != _frameSamples || fft < frame || (liveShape && fft != DefaultFrameSamples))
            {
                // The live engine's delay and cost are reviewed for exactly
                // this shape; a different one is a different build to review.
                api.Free(state);
                Unavailable(Unavailability.InitFailed, $"libspecbleach made a {frame}-sample frame with a {fft}-point FFT; this build expects {_frameSamples} and {(liveShape ? DefaultFrameSamples : frame)}.");
                return;
            }

            var defaults = api.GetDefaults();
            LibraryDefaults = defaults;
            _parameters = defaults;
            _parameters.LearnNoise = 0;                    // SPECBLEACH_LEARN_OFF: nothing to capture
            _parameters.ResidualListen = 0;
            _parameters.AdaptiveNoise = 1;                 // the point of NR4: continuous estimation
            _parameters.NoiseEstimationMethod = (int)_estimator;
            _parameters.SmoothingMode = 0;                 // SPECBLEACH_SMOOTHING_TEMPORAL; 2D modes block on workers
            _parameters.ReductionCurveEnabled = 0;
            _parameters.ReductionCurveBias = IntPtr.Zero;
            _parameters.ReductionCurveSize = 0;
            Compose();
            Volatile.Write(ref _dirty, 0);

            _state = state;
            if (!LoadParameters())
            {
                Stop();
                Unavailable(Unavailability.InitFailed, $"libspecbleach refused the {ParametersSize}-byte parameter block; this binding's layout does not match the library.");
                return;
            }

            LatencySamples = checked((int)api.GetLatency(state));
            Unavailability = Unavailability.None;
            UnavailabilityDetail = "";
        }

        private void Unavailable(Unavailability reasonCode, string reason)
        {
            Unavailability = reasonCode;
            UnavailabilityDetail = reason;
            Trace.WriteLine("SpecbleachEngine: unavailable: " + reason);
        }

        private static string LocateLibrary()
        {
            try
            {
                string? fromLoader = LibraryLocator?.Invoke();
                if (!string.IsNullOrEmpty(fromLoader)) return fromLoader;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("SpecbleachEngine: library locator failed: " + ex.Message);
            }
            return DefaultLibraryPath();
        }

        /// <summary>
        /// The C API at tag v0.4.1, bound by name from one loaded handle.
        /// Signatures from <c>include/specbleach_denoiser.h</c> and
        /// <c>include/specbleach_version.h</c> at that tag; plain C functions,
        /// which are cdecl on x86. Every argument is blittable. A C
        /// <c>bool</c> comes back as one byte, read as <see cref="byte"/>.
        /// </summary>
        private sealed class Api
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate SpecbleachDenoiserParameters GetDefaultsFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate IntPtr InitializeFn(uint sampleRate, float frameSizeMs, uint flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void FreeFn(IntPtr instance);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate byte LoadParametersFn(IntPtr instance, ref SpecbleachDenoiserParameters parameters, uint parametersSize);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate byte ProcessFn(IntPtr instance, uint numberOfSamples, ref float input, ref float output);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate uint QueryFn(IntPtr instance);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate IntPtr VersionFn();

            private GetDefaultsFn _defaults = null!;
            private InitializeFn _initialize = null!;
            private FreeFn _free = null!;
            private LoadParametersFn _load = null!;
            private ProcessFn _process = null!;
            private QueryFn _latency = null!;
            private QueryFn _frameSize = null!;
            private QueryFn _fftSize = null!;

            public string Version { get; private set; } = "";

            public SpecbleachDenoiserParameters GetDefaults() => _defaults();
            public IntPtr Initialize(uint rate, float frameMs, uint flags) => _initialize(rate, frameMs, flags);
            public void Free(IntPtr instance) => _free(instance);
            public byte Load(IntPtr instance, ref SpecbleachDenoiserParameters p, uint size) => _load(instance, ref p, size);
            public byte Process(IntPtr instance, uint n, ref float input, ref float output) => _process(instance, n, ref input, ref output);
            public uint GetLatency(IntPtr instance) => _latency(instance);
            public uint GetFrameSize(IntPtr instance) => _frameSize(instance);
            public uint GetFftSize(IntPtr instance) => _fftSize(instance);

            private static readonly object Gate = new();
            private static string? _loadedPath;
            private static Api? _loaded;

            /// <summary>One handle per process: the library is never unloaded.</summary>
            public static Api? Load(string path, out string? problem)
            {
                lock (Gate)
                {
                    problem = null;
                    if (_loaded != null && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
                        return _loaded;

                    if (!NativeLibrary.TryLoad(path, out IntPtr h))
                    {
                        problem = $"{path} exists but would not load (not a DLL, the wrong architecture, or a missing dependency).";
                        return null;
                    }

                    var api = new Api();
                    if (!Bind(h, "specbleach_denoiser_get_default_parameters", out IntPtr defaults)
                        || !Bind(h, "specbleach_denoiser_initialize", out IntPtr initialize)
                        || !Bind(h, "specbleach_denoiser_free", out IntPtr free)
                        || !Bind(h, "specbleach_denoiser_load_parameters", out IntPtr load)
                        || !Bind(h, "specbleach_denoiser_process", out IntPtr process)
                        || !Bind(h, "specbleach_denoiser_get_latency", out IntPtr latency)
                        || !Bind(h, "specbleach_denoiser_get_frame_size", out IntPtr frameSize)
                        || !Bind(h, "specbleach_denoiser_get_fft_size", out IntPtr fftSize)
                        || !Bind(h, "specbleach_get_version_string", out IntPtr version))
                    {
                        problem = $"{path} loaded but does not export libspecbleach's denoiser API (0.4.0 or later).";
                        return null;
                    }
                    api._defaults = Marshal.GetDelegateForFunctionPointer<GetDefaultsFn>(defaults);
                    api._initialize = Marshal.GetDelegateForFunctionPointer<InitializeFn>(initialize);
                    api._free = Marshal.GetDelegateForFunctionPointer<FreeFn>(free);
                    api._load = Marshal.GetDelegateForFunctionPointer<LoadParametersFn>(load);
                    api._process = Marshal.GetDelegateForFunctionPointer<ProcessFn>(process);
                    api._latency = Marshal.GetDelegateForFunctionPointer<QueryFn>(latency);
                    api._frameSize = Marshal.GetDelegateForFunctionPointer<QueryFn>(frameSize);
                    api._fftSize = Marshal.GetDelegateForFunctionPointer<QueryFn>(fftSize);
                    IntPtr text = Marshal.GetDelegateForFunctionPointer<VersionFn>(version)();
                    api.Version = text == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(text) ?? "";

                    // Traced, never refused: the LGPL entitles the operator to replace this library.
                    Trace.WriteLine($"SpecbleachEngine: loaded {path}, which reports \"{api.Version}\" (built against libspecbleach 0.4.1).");

                    _loadedPath = path;
                    _loaded = api;
                    return api;
                }
            }

            private static bool Bind(IntPtr h, string name, out IntPtr fn)
                => NativeLibrary.TryGetExport(h, name, out fn);
        }
    }

    /// <summary>
    /// <c>SpecbleachDenoiserParameters</c> from <c>include/specbleach_denoiser.h</c>
    /// at tag v0.4.1, field for field in the header's order. Enums are four
    /// bytes, a C <c>bool</c> is ONE byte (so <see cref="byte"/>, which also
    /// keeps the struct blittable), and the curve pointer is pointer-sized,
    /// so the layout differs by architecture: 88 bytes with the pointer at 64
    /// on x64, 76 with it at 60 on x86. <c>SpecbleachEngineTests</c> pins the
    /// offsets and every default by value on both.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpecbleachDenoiserParameters
    {
        public int LearnNoise;                  // SpecbleachLearnMode
        public byte ResidualListen;             // bool
        public float ReductionGain;
        public float SmoothingFactor;
        public int SmoothingMode;               // SpecbleachSmoothingMode
        public float DfttStrength;
        public float WhiteningFactor;
        public byte AdaptiveNoise;              // bool
        public int NoiseEstimationMethod;       // SpecbleachNoiseEstimationMethod
        public float MaskingDepth;
        public float SuppressionStrength;
        public float Aggressiveness;
        public float TonalReductionGain;
        public byte TransientProtectionEnable;  // bool
        public float NoiseProfileScale;
        public IntPtr ReductionCurveBias;       // const float*
        public byte ReductionCurveEnabled;      // bool
        public uint ReductionCurveSize;
        public float TonalNoiseProfileScale;
    }
}
