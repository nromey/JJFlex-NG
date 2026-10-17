using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace JJFlexWpf
{
    /// <summary>
    /// DeepFilterNet3 (Hendrik Schröter et al., MIT OR Apache-2.0) as a mono
    /// engine, through DeepFilterNet's own C API in <c>deepfilter.dll</c>.
    /// Full-band 48 kHz, 480-sample frames.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase one of #731: built, tested and benched, NOT selectable.</b>
    /// RNNoise is still the only engine the app runs. This class is reachable
    /// from code and from <c>tools/nrbench</c>; putting it in front of an
    /// operator waits on Noel's rulings.
    /// </para>
    /// <para>
    /// <b>The binary and the model are pinned</b>, and how they were built is in
    /// <c>runtimes/win-x64/native/README.md</c>: upstream tag <c>v0.5.6</c>. At
    /// that tag the C API needs a path to the model archive; there is no
    /// embedded default reachable from C, so the archive ships beside the DLL.
    /// </para>
    /// <para>
    /// <b>Why the model's hash is checked before anything is handed to the
    /// library.</b> The C API reports failure by panicking, and the DLL is built
    /// with panics set to abort, so a missing, truncated or wrong model file
    /// would not return an error: it would end the whole process. The hash
    /// check turns every one of those into <see cref="IsAvailable"/> = false
    /// with a reason, before the library is called at all.
    /// </para>
    /// <para>
    /// <b>Loaded by absolute path</b>, bound by name from the handle, never
    /// through the Windows search order. In the app the path comes from
    /// <c>NativeLoader.vb</c> (via <see cref="LibraryLocator"/>), which owns
    /// where native libraries live; tests and the bench pass a path.
    /// </para>
    /// </remarks>
    public sealed class DeepFilterNet3Engine : INeuralDenoiseEngine
    {
        /// <summary>The shipped file name (built as <c>df.dll</c> upstream, renamed).</summary>
        public const string LibraryFileName = "deepfilter.dll";

        /// <summary>Upstream's DeepFilterNet3 model archive, shipped beside the DLL unchanged.</summary>
        public const string ModelFileName = "DeepFilterNet3_onnx.tar.gz";

        /// <summary>
        /// SHA-256 of <see cref="ModelFileName"/> at upstream tag v0.5.6
        /// (<c>models/DeepFilterNet3_onnx.tar.gz</c>, 7,983,136 bytes). Bump with the pin.
        /// </summary>
        public const string ModelSha256 = "c94d91f70911001c946e0fabb4aa9adc37045f45a03b56008cb0c8244cb63616";

        /// <summary>Upstream's default attenuation limit: 100 dB, which means no limit.</summary>
        public const float DefaultAttenuationLimitDb = 100f;

        /// <summary>
        /// Where the app's copy of <see cref="LibraryFileName"/> lives. Set by
        /// <c>NativeLoader.Initialize</c>; when unset (tests, the bench) the
        /// default is <c>runtimes\win-{arch}\native</c> under the app base directory.
        /// </summary>
        public static Func<string?>? LibraryLocator { get; set; }

        private readonly string? _libraryPath;
        private readonly string? _modelPath;
        private Api? _api;
        private IntPtr _state;
        private float _attenLimitDb;
        private readonly float[] _in = new float[480];
        private readonly float[] _out = new float[480];

        /// <param name="attenuationLimitDb">Most the engine may take off, in dB. 100 = unlimited.</param>
        /// <param name="libraryPath">Absolute path to the DLL, or null for the app's copy.</param>
        /// <param name="modelPath">Absolute path to the model archive, or null for the one beside the DLL.</param>
        public DeepFilterNet3Engine(float attenuationLimitDb = DefaultAttenuationLimitDb,
                                    string? libraryPath = null, string? modelPath = null)
        {
            _attenLimitDb = attenuationLimitDb;
            _libraryPath = libraryPath;
            _modelPath = modelPath;
            Start();
        }

        public string Id => "deepfilternet3";

        public int SampleRate => 48000;

        /// <summary>Reported by the library (480 for this model); 480 if it never loaded.</summary>
        public int FrameLength { get; private set; } = 480;

        public bool IsAvailable => _state != IntPtr.Zero;

        /// <summary>Why <see cref="IsAvailable"/> is false, in plain words; empty when it is true.</summary>
        public string UnavailableReason { get; private set; } = "";

        /// <summary>The DLL actually loaded, or the path that was tried.</summary>
        public string LibraryPath { get; private set; } = "";

        /// <summary>Most the engine may take off, in dB (100 = unlimited). Applies live.</summary>
        public float AttenuationLimitDb
        {
            get => _attenLimitDb;
            set
            {
                _attenLimitDb = value;
                if (_state != IntPtr.Zero) _api!.SetAttenLim(_state, value);
            }
        }

        /// <summary>The local SNR, in dB, the model reported for the most recent frame.</summary>
        public float LastFrameSnrDb { get; private set; }

        /// <summary>Frames processed since creation or <see cref="Reset"/>.</summary>
        public long FramesProcessed { get; private set; }

        /// <summary>Sum of every reported per-frame SNR, for a mean.</summary>
        public double SnrSumDb { get; private set; }

        /// <summary>Post-filter strength; 0 disables it (upstream default). Upstream suggests 0 to 0.05.</summary>
        public void SetPostFilterBeta(float beta)
        {
            if (_state != IntPtr.Zero) _api!.SetPostFilterBeta(_state, beta);
        }

        public void ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (_state == IntPtr.Zero)
                throw new InvalidOperationException("DeepFilterNet3 is not available: " + UnavailableReason);
            if (input.Length != FrameLength || output.Length != FrameLength)
                throw new ArgumentException($"DeepFilterNet3 frames are exactly {FrameLength} samples.");

            // Copied in and out: the C signature takes a mutable input pointer,
            // and nothing outside this class should have to trust that it is
            // never written.
            input.CopyTo(_in);
            float snr = _api!.ProcessFrame(_state, _in, _out);
            _out.AsSpan(0, FrameLength).CopyTo(output);

            LastFrameSnrDb = snr;
            SnrSumDb += snr;
            FramesProcessed++;
        }

        public void Reset()
        {
            Stop();
            FramesProcessed = 0;
            SnrSumDb = 0;
            LastFrameSnrDb = 0;
            Start();
        }

        public void Dispose() => Stop();

        private void Stop()
        {
            if (_state != IntPtr.Zero)
            {
                _api!.Free(_state);
                _state = IntPtr.Zero;
            }
        }

        private void Start()
        {
            string lib = _libraryPath ?? LocateLibrary();
            LibraryPath = lib;

            if (!File.Exists(lib))
            {
                Unavailable($"{LibraryFileName} was not found at {lib}.");
                return;
            }

            string model = _modelPath ?? Path.Combine(Path.GetDirectoryName(lib) ?? "", ModelFileName);
            string? modelProblem = CheckModel(model);
            if (modelProblem != null)
            {
                Unavailable(modelProblem);
                return;
            }

            var api = Api.Load(lib, out string? loadProblem);
            if (api == null)
            {
                Unavailable(loadProblem ?? $"{LibraryFileName} would not load.");
                return;
            }
            _api = api;

            IntPtr pathUtf8 = Marshal.StringToCoTaskMemUTF8(model);
            try
            {
                _state = api.Create(pathUtf8, _attenLimitDb);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathUtf8);
            }

            if (_state == IntPtr.Zero)
            {
                Unavailable("DeepFilterNet3 did not initialise its model.");
                return;
            }

            int frame = checked((int)api.GetFrameLength(_state));
            if (frame != _in.Length)
            {
                // The buffers are sized for this model; a different one is a
                // different pin and must be reviewed, not absorbed.
                Stop();
                Unavailable($"DeepFilterNet3 reported a {frame}-sample frame; this build expects {_in.Length}.");
                return;
            }
            FrameLength = frame;
            UnavailableReason = "";
        }

        private void Unavailable(string reason)
        {
            UnavailableReason = reason;
            Trace.WriteLine("DeepFilterNet3Engine: unavailable: " + reason);
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
                Trace.WriteLine("DeepFilterNet3Engine: library locator failed: " + ex.Message);
            }
            return DefaultLibraryPath();
        }

        /// <summary><c>runtimes\win-{arch}\native\deepfilter.dll</c> under the app base directory.</summary>
        public static string DefaultLibraryPath()
        {
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            return Path.Combine(AppContext.BaseDirectory, "runtimes", $"win-{arch}", "native", LibraryFileName);
        }

        /// <summary>Null when the model file is the pinned one; otherwise what is wrong with it.</summary>
        private static string? CheckModel(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return $"The DeepFilterNet3 model was not found at {path}.";
                using var fs = File.OpenRead(path);
                string hash = Convert.ToHexString(SHA256.HashData(fs));
                if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
                    return $"The DeepFilterNet3 model at {path} is not the pinned file (SHA-256 {hash}).";
                return null;
            }
            catch (Exception ex)
            {
                return $"The DeepFilterNet3 model at {path} could not be read: {ex.Message}";
            }
        }

        /// <summary>
        /// The C API at upstream tag v0.5.6, bound by name from one loaded
        /// handle. Signatures from <c>libDF/src/capi.rs</c> at that tag;
        /// <c>extern "C"</c> is cdecl on x86. The float arrays are blittable,
        /// so the marshaller pins them and passes the pointer.
        /// </summary>
        private sealed class Api
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate IntPtr CreateFn(IntPtr pathUtf8, float attenLim);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate UIntPtr GetFrameLengthFn(IntPtr st);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void SetFloatFn(IntPtr st, float value);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate float ProcessFrameFn(IntPtr st, [In] float[] input, [Out] float[] output);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void FreeFn(IntPtr st);

            private CreateFn _create = null!;
            private GetFrameLengthFn _frame = null!;
            private SetFloatFn _atten = null!;
            private SetFloatFn _beta = null!;
            private ProcessFrameFn _process = null!;
            private FreeFn _free = null!;

            public IntPtr Create(IntPtr pathUtf8, float attenLim) => _create(pathUtf8, attenLim);
            public ulong GetFrameLength(IntPtr st) => _frame(st).ToUInt64();
            public void SetAttenLim(IntPtr st, float db) => _atten(st, db);
            public void SetPostFilterBeta(IntPtr st, float beta) => _beta(st, beta);
            public float ProcessFrame(IntPtr st, float[] input, float[] output) => _process(st, input, output);
            public void Free(IntPtr st) => _free(st);

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
                        problem = $"{path} exists but would not load (wrong architecture, or a missing dependency).";
                        return null;
                    }

                    var api = new Api();
                    if (!Bind(h, "df_create", out IntPtr create)
                        || !Bind(h, "df_get_frame_length", out IntPtr frame)
                        || !Bind(h, "df_set_atten_lim", out IntPtr atten)
                        || !Bind(h, "df_set_post_filter_beta", out IntPtr beta)
                        || !Bind(h, "df_process_frame", out IntPtr process)
                        || !Bind(h, "df_free", out IntPtr free))
                    {
                        problem = $"{path} loaded but does not export DeepFilterNet's C API.";
                        return null;
                    }
                    api._create = Marshal.GetDelegateForFunctionPointer<CreateFn>(create);
                    api._frame = Marshal.GetDelegateForFunctionPointer<GetFrameLengthFn>(frame);
                    api._atten = Marshal.GetDelegateForFunctionPointer<SetFloatFn>(atten);
                    api._beta = Marshal.GetDelegateForFunctionPointer<SetFloatFn>(beta);
                    api._process = Marshal.GetDelegateForFunctionPointer<ProcessFrameFn>(process);
                    api._free = Marshal.GetDelegateForFunctionPointer<FreeFn>(free);

                    _loadedPath = path;
                    _loaded = api;
                    return api;
                }
            }

            private static bool Bind(IntPtr h, string name, out IntPtr fn)
                => NativeLibrary.TryGetExport(h, name, out fn);
        }
    }
}
