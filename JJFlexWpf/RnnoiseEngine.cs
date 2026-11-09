using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace JJFlexWpf
{
    /// <summary>RNNoise's input level. Historical is for offline comparisons only.</summary>
    /// <remarks>The app uses Corrected: Noel ruled to fix the double scaling on
    /// 2026-10-17. Historical reproduces the pre-Sprint-49 level, with today's
    /// aligned bank blend; it is not an exact render of the old provider.</remarks>
    public enum RnnoiseScaling
    {
        /// <summary>The former wrapper plus caller's double 32767 scaling.</summary>
        Historical,
        /// <summary>One conversion to the 16-bit float range RNNoise expects.</summary>
        Corrected,
    }

    /// <summary>
    /// The RNNoise models the engine can run (#1019; Noel 2026-11-08 17:42, "in
    /// advanced ... select tag and old as a model"). Each lives in its own library,
    /// because each library refuses the others' models (measured 2026-11-08), and
    /// no two of those libraries export a name in common.
    /// </summary>
    /// <remarks>
    /// The names are STORED (a setting, a preset, a favourite), so they name each
    /// model by identity and never by recency: a newer upstream model must not make
    /// a stored "latest" mean something else.
    /// </remarks>
    public static class RnnoiseModels
    {
        /// <summary>Upstream commit 70f1d256 (the 0.2 line, model of 2025-01-29), in <c>rnnoise.dll</c>. The default.</summary>
        public const string Newest = "main_70f1d256";

        /// <summary>The April 2024 <c>v0.2</c> tag, in <c>rnnoise_v02.dll</c>.</summary>
        public const string Tag = "tag_v02";

        /// <summary>The classic v0.1.1, run through <c>rnnoise_vad011.dll</c>'s whole-frame export.</summary>
        public const string V011 = "v011";

        /// <summary>The default model: the newest, which ships today.</summary>
        public const string Default = Newest;

        /// <summary>Every model, newest first, as the setting lists them.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Newest, Tag, V011 };

        /// <summary>The library a model runs in. An unknown name is the default's.</summary>
        public static string LibraryFileName(string model) => model switch
        {
            Tag => "rnnoise_v02.dll",
            V011 => "rnnoise_vad011.dll",   // the detector's library; the detector itself is not used here
            _ => RnnoiseEngine.LibraryFileName,
        };

        /// <summary>
        /// How far a model's output lags its input: v0.1.1 one frame (480 samples,
        /// its 20 ms window over a 10 ms hop); both 0.2 models two frames (960), the
        /// line's extra frame of look-ahead (upstream 84fe83b, before the tag).
        /// Measured on real speech by EngineLatencyTests.
        /// </summary>
        public static int LatencySamples(string model) => model == V011 ? RnnoiseEngine.Frame : 2 * RnnoiseEngine.Frame;

        /// <summary>Whether a model name is one of these.</summary>
        public static bool IsKnown(string? model) => model is Newest or Tag or V011;
    }

    /// <summary>
    /// Xiph RNNoise, a mono 48 kHz engine, loaded by absolute path through
    /// <see cref="NativeEngineLibrary"/>. Three models (<see cref="RnnoiseModels"/>),
    /// each in its own library and bound by that library's own export names. The
    /// newest (upstream 70f1d256, Sprint 49 Track R, #873) can also run a model
    /// FILE of its own network's shape, for a future ham-trained model (#1019, B4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A model file is read by our code, never by the library</b> (B4).
    /// Upstream's <c>rnnoise_model_from_filename</c> ends the process when the path
    /// does not exist (measured 2026-11-08: 0xC0000409, no return), and
    /// <c>rnnoise_model_from_file</c> takes a <c>FILE*</c> from a C runtime the DLL
    /// does not share (it links its own, statically). Neither is ever bound. The
    /// file is checked, read whole on the thread that builds the engine (the slot's
    /// build worker, never the audio thread), copied into unmanaged memory, and
    /// handed over with <c>rnnoise_model_from_buffer</c>.
    /// </para>
    /// <para>
    /// <b>Lifetimes</b>, from upstream's header: the buffer outlives the model, and
    /// the model outlives every state made from it. <see cref="Dispose"/> frees the
    /// state, then the model, then the buffer. <see cref="Reset"/> keeps the model
    /// and the buffer and remakes only the state, so it never touches the file.
    /// </para>
    /// </remarks>
    public sealed class RnnoiseEngine : IDenoiseEngine
    {
        public const int Frame = 480;
        public const string LibraryFileName = "rnnoise.dll";
        private const float Scale = 32767f;
        private const float InverseScale = 3.051851E-05f;
        /// <summary>The model-buffer alignment: cheap insurance (an inference from the SIMD code, not a measured need).</summary>
        private const int BufferAlignment = 64;

        /// <summary>
        /// Where a model's library is: given its file name, the absolute path, or
        /// null for the default folder. Wired from NativeLoader.vb, which decides
        /// where every native file of ours lives.
        /// </summary>
        public static Func<string, string?>? LibraryLocator { get; set; }

        private readonly string? _libraryPath;
        private readonly string? _modelFilePath;
        private readonly float[] _in = new float[Frame];
        private readonly float[] _out = new float[Frame];
        private Api? _api;
        private IntPtr _state;
        private IntPtr _model;
        private IntPtr _buffer;

        /// <param name="scaling">Corrected in the app; Historical for offline comparisons.</param>
        /// <param name="libraryPath">An absolute path to the model's library, or null for where the locator says.</param>
        /// <param name="model">One of <see cref="RnnoiseModels"/>. The default is the newest, which is what transmit runs: transmit does not follow the receive model choice.</param>
        /// <param name="modelFilePath">A model file of the newest network's shape, read by our code and handed over as a buffer. Newest model only.</param>
        public RnnoiseEngine(RnnoiseScaling scaling = RnnoiseScaling.Corrected, string? libraryPath = null,
            string model = RnnoiseModels.Default, string? modelFilePath = null)
        {
            Scaling = scaling;
            Model = RnnoiseModels.IsKnown(model) ? model : RnnoiseModels.Default;
            _libraryPath = libraryPath;
            _modelFilePath = modelFilePath;
            Start();
        }

        public string Id => "rnnoise";
        public int SampleRate => 48000;
        public int FrameLength => Frame;
        public bool IsAvailable => _state != IntPtr.Zero;

        /// <summary>The model this instance runs.</summary>
        public string Model { get; }

        /// <summary>
        /// The model's delay (<see cref="RnnoiseModels.LatencySamples"/>): 480 for
        /// v0.1.1, 960 for both 0.2 models. The bank aligns the original audio by
        /// this figure, so it follows the model: a model change builds a new
        /// instance, under the warm-up cross-fade, and the bank adopts its delay.
        /// </summary>
        public int LatencySamples => RnnoiseModels.LatencySamples(Model);
        public RnnoiseScaling Scaling { get; }
        public Unavailability Unavailability { get; private set; }
        public string UnavailabilityDetail { get; private set; } = "";
        public string LibraryPath { get; private set; } = "";

        /// <summary>
        /// The voice probability the last frame returned. Nothing in the app reads
        /// it; the tests hold each model to its library with it. The models' scales
        /// differ (measured mean 0.27 for the newest against 0.65 for the tag on the
        /// same recording), so anything that ever reads it must not assume one.
        /// </summary>
        internal float LastVoiceProbability { get; private set; }

        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (!IsAvailable) throw new InvalidOperationException("RNNoise is not available: " + UnavailabilityDetail);
            if (input.Length != Frame || output.Length != Frame)
                throw new ArgumentException($"RNNoise frames are exactly {Frame} samples.");
            for (int i = 0; i < Frame; i++)
            {
                float sample = input[i];
                if (Scaling == RnnoiseScaling.Historical) sample *= Scale;
                _in[i] = sample * Scale;
            }
            LastVoiceProbability = _api!.Process(_state, _out, _in); // C API: output BEFORE input; return is VAD probability.
            for (int i = 0; i < Frame; i++)
            {
                float sample = _out[i] * InverseScale;
                output[i] = Scaling == RnnoiseScaling.Historical ? sample / Scale : sample;
            }
            return true;
        }

        /// <summary>Forget what was learned: a new state on the same model. Never reads the model file again.</summary>
        public void Reset()
        {
            if (_api == null) { Start(); return; }   // never loaded: try again, as Reset always did
            if (_state != IntPtr.Zero) { _api.Destroy(_state); _state = IntPtr.Zero; }
            if (_modelFilePath != null && _model == IntPtr.Zero) return; // a refused file stays refused
            _state = _api.Create(_model);
            if (_state == IntPtr.Zero) Unavailable(Unavailability.InitFailed, $"RNNoise could not create its state at {LibraryPath}.");
        }

        /// <summary>The state, then the model, then the buffer: the order upstream's lifetimes require.</summary>
        public void Dispose()
        {
            if (_state != IntPtr.Zero) { _api!.Destroy(_state); _state = IntPtr.Zero; }
            FreeModel();
        }

        private void FreeModel()
        {
            if (_model != IntPtr.Zero) { _api!.ModelFree!(_model); _model = IntPtr.Zero; }
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
        }

        private void Start()
        {
            try
            {
                string path = _libraryPath ?? LocateLibrary(Model);
                LibraryPath = path;
                if (!File.Exists(path)) { Unavailable(Unavailability.LibraryMissing, $"RNNoise was not found at {path}."); return; }
                if (!Path.IsPathFullyQualified(path))
                    throw new ArgumentException("RNNoise requires an absolute library path: " + path);
                if (_modelFilePath != null && Model != RnnoiseModels.Newest)
                {
                    Unavailable(Unavailability.InitFailed, $"An RNNoise model file loads only into the newest model's library, not the {Model} one.");
                    return;
                }
                _api = Api.Load(path, Model);
                if (_modelFilePath != null && !LoadModelFile(_modelFilePath)) return;
                _state = _api.Create(_model);
                if (_state == IntPtr.Zero)
                {
                    FreeModel();
                    Unavailable(Unavailability.InitFailed, _modelFilePath != null
                        ? $"RNNoise refused the model file {_modelFilePath} (it is not a model of this network's shape, or it is damaged)."
                        : $"RNNoise could not create its state at {path}.");
                    return;
                }
                Unavailability = Unavailability.None;
                UnavailabilityDetail = "";
            }
            catch (Exception ex)
            {
                FreeModel();
                Unavailable(Unavailability.InitFailed,
                    $"RNNoise at {LibraryPath} could not initialise (it may be the wrong architecture or have a missing dependency): {ex.Message}");
            }
        }

        /// <summary>
        /// Check, read and hand over a model file (B4's order): it exists and its
        /// length is sane; read whole; copied into 64-byte-aligned unmanaged
        /// memory; <c>rnnoise_model_from_buffer</c>. False, with the reason, when
        /// it is refused; nothing is left allocated then.
        /// </summary>
        private bool LoadModelFile(string file)
        {
            if (!Path.IsPathFullyQualified(file) || !File.Exists(file))
            {
                Unavailable(Unavailability.ModelMissing, $"The RNNoise model file was not found at {file}.");
                return false;
            }
            long length = new FileInfo(file).Length;
            if (length <= 0 || length > int.MaxValue)
            {
                Unavailable(Unavailability.InitFailed, $"The RNNoise model file {file} is {length} bytes, which cannot be a model.");
                return false;
            }
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length != length)
            {
                Unavailable(Unavailability.InitFailed, $"The RNNoise model file {file} changed while it was read.");
                return false;
            }
            _buffer = Marshal.AllocHGlobal(bytes.Length + BufferAlignment);
            IntPtr aligned = new((_buffer.ToInt64() + BufferAlignment - 1) & ~(long)(BufferAlignment - 1));
            Marshal.Copy(bytes, 0, aligned, bytes.Length);
            _model = _api!.ModelFromBuffer!(aligned, bytes.Length);
            if (_model == IntPtr.Zero)
            {
                FreeModel();
                Unavailable(Unavailability.InitFailed, $"RNNoise refused the model file {file}.");
                return false;
            }
            if (!ClearUninitialisedFile(_model, aligned, bytes.Length))
            {
                // Not the layout the pin has: freeing it could close a garbage
                // FILE*, so the small struct is left allocated (never freed), and
                // the buffer it may point into stays alive with it.
                _model = IntPtr.Zero;
                _buffer = IntPtr.Zero;
                Unavailable(Unavailability.InitFailed, $"RNNoise's model structure is not the layout this build knows, so the model file {file} was not used.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Work around an upstream defect at the pin (70f1d256, <c>src/denoise.c</c>
        /// lines 227-242, found by Sprint 49 Track S): <c>rnnoise_model_from_buffer</c>
        /// mallocs an <c>RNNModel</c> and sets <c>const_blob</c>, <c>blob</c> and
        /// <c>blob_len</c> but never <c>file</c>, and <c>rnnoise_model_free</c> calls
        /// <c>fclose(model->file)</c> whenever it is not null. So freeing a model made
        /// from a buffer closes a garbage pointer, and the test host died of it (an
        /// internal CLR error) the first time a refused file was freed. The struct is
        /// <c>{ const void *const_blob; void *blob; int blob_len; FILE *file; }</c>;
        /// this checks the three fields it can know (our buffer, null, our length)
        /// before writing null into <c>file</c>, and returns false, touching nothing,
        /// if they do not read back as expected. Upstream source is not edited.
        /// </summary>
        private static bool ClearUninitialisedFile(IntPtr model, IntPtr buffer, int length)
        {
            int pointer = IntPtr.Size;
            int fileOffset = (2 * pointer + sizeof(int) + pointer - 1) / pointer * pointer;
            if (Marshal.ReadIntPtr(model, 0) != buffer || Marshal.ReadIntPtr(model, pointer) != IntPtr.Zero
                || Marshal.ReadInt32(model, 2 * pointer) != length) return false;
            Marshal.WriteIntPtr(model, fileOffset, IntPtr.Zero);
            return true;
        }

        internal static string? FrameSizeProblem(int frame) => frame == Frame ? null
            : $"RNNoise reported a {frame}-sample frame; this build expects {Frame}.";

        private void Unavailable(Unavailability reason, string detail)
        {
            Unavailability = reason;
            UnavailabilityDetail = detail;
            Trace.WriteLine("RnnoiseEngine: unavailable: " + detail);
        }

        /// <summary>The newest model's library, where the locator says.</summary>
        internal static string LocateLibrary() => LocateLibrary(RnnoiseModels.Newest);

        /// <summary>A model's library, where the locator says, else the default folder.</summary>
        internal static string LocateLibrary(string model)
        {
            string file = RnnoiseModels.LibraryFileName(model);
            return LibraryLocator?.Invoke(file) ?? DefaultLibraryPath(file);
        }

        public static string DefaultLibraryPath() => DefaultLibraryPath(LibraryFileName);
        public static string DefaultLibraryPath(string file) => Path.Combine(AppContext.BaseDirectory, "runtimes",
            Environment.Is64BitProcess ? "win-x64" : "win-x86", "native", file);

        /// <summary>
        /// One model's entry points, bound by that library's own names: plain C,
        /// cdecl even on x86, blittable arrays pinned by the marshaller. The three
        /// libraries share no export name (RnnoiseModelTests reads all of them).
        /// </summary>
        private sealed class Api
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate IntPtr CreateWithModelFn(IntPtr model);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate IntPtr CreateFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void DestroyFn(IntPtr state);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int FrameFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate float ProcessFn(IntPtr state, [Out] float[] output, [In] float[] input);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate IntPtr ModelFromBufferFn(IntPtr buffer, int length);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void ModelFreeFn(IntPtr model);

            /// <summary>Make a state; the model is zero for a library's built-in model.</summary>
            internal Func<IntPtr, IntPtr> Create = null!;
            internal DestroyFn Destroy = null!;
            internal ProcessFn Process = null!;
            /// <summary>The newest library only: <c>rnnoise_model_from_buffer</c>.</summary>
            internal ModelFromBufferFn? ModelFromBuffer;
            /// <summary>The newest library only: <c>rnnoise_model_free</c>.</summary>
            internal ModelFreeFn? ModelFree;

            /// <summary>The binding for one model's library; one per (path, model) for the process.</summary>
            internal static Api Load(string path, string model) =>
                NativeEngineLibrary.Load(path, "rnnoise." + model, exports => Bind(exports, model));

            private static Api Bind(NativeEngineLibrary.Exports exports, string model)
            {
                var api = new Api();
                FrameFn frame;
                switch (model)
                {
                    case RnnoiseModels.Tag:
                    {
                        var create = exports.Bind<CreateFn>("rnv02_create");
                        api.Create = m => m == IntPtr.Zero ? create() : IntPtr.Zero;
                        api.Destroy = exports.Bind<DestroyFn>("rnv02_destroy");
                        frame = exports.Bind<FrameFn>("rnv02_frame_size");
                        api.Process = exports.Bind<ProcessFn>("rnv02_process_frame");
                        break;
                    }
                    case RnnoiseModels.V011:
                    {
                        // The whole v0.1.1 frame, upstream's rnnoise_process_frame
                        // under our own name, from the voice detector's library.
                        var create = exports.Bind<CreateFn>("rnvad011_create");
                        api.Create = m => m == IntPtr.Zero ? create() : IntPtr.Zero;
                        api.Destroy = exports.Bind<DestroyFn>("rnvad011_destroy");
                        frame = exports.Bind<FrameFn>("rnvad011_frame_size");
                        api.Process = exports.Bind<ProcessFn>("rnvad011_reference_frame");
                        break;
                    }
                    default:
                    {
                        var create = exports.Bind<CreateWithModelFn>("rnnoise_create");
                        api.Create = m => create(m);
                        api.Destroy = exports.Bind<DestroyFn>("rnnoise_destroy");
                        frame = exports.Bind<FrameFn>("rnnoise_get_frame_size");
                        api.Process = exports.Bind<ProcessFn>("rnnoise_process_frame");
                        // The model-buffer pair, and ONLY that pair: never the
                        // filename or FILE* loaders (see the class remarks).
                        api.ModelFromBuffer = exports.Bind<ModelFromBufferFn>("rnnoise_model_from_buffer");
                        api.ModelFree = exports.Bind<ModelFreeFn>("rnnoise_model_free");
                        break;
                    }
                }
                // Reject an incompatible library before the handle is retained.
                if (FrameSizeProblem(frame()) is string problem)
                    throw new InvalidOperationException(problem);
                return api;
            }
        }
    }
}
