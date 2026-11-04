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

    /// <summary>Xiph RNNoise v0.1.1, a mono 48 kHz engine, loaded by absolute path.</summary>
    public sealed class RnnoiseEngine : IDenoiseEngine
    {
        public const int Frame = 480;
        public const string LibraryFileName = "rnnoise.dll";
        private const float Scale = 32767f;
        private const float InverseScale = 3.051851E-05f;
        public static Func<string?>? LibraryLocator { get; set; }
        private readonly string? _libraryPath;
        private readonly float[] _in = new float[Frame];
        private readonly float[] _out = new float[Frame];
        private Api? _api;
        private IntPtr _state;

        public RnnoiseEngine(RnnoiseScaling scaling = RnnoiseScaling.Corrected, string? libraryPath = null)
        {
            Scaling = scaling;
            _libraryPath = libraryPath;
            Start();
        }

        public string Id => "rnnoise";
        public int SampleRate => 48000;
        public int FrameLength => Frame;
        public bool IsAvailable => _state != IntPtr.Zero;
        /// <summary>The 20 ms analysis window overhangs its hop by 480 samples.</summary>
        public int LatencySamples => Frame;
        public RnnoiseScaling Scaling { get; }
        public Unavailability Unavailability { get; private set; }
        public string UnavailabilityDetail { get; private set; } = "";
        public string LibraryPath { get; private set; } = "";

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
            _api!.Process(_state, _out, _in); // C API: output BEFORE input; return is VAD probability.
            for (int i = 0; i < Frame; i++)
            {
                float sample = _out[i] * InverseScale;
                output[i] = Scaling == RnnoiseScaling.Historical ? sample / Scale : sample;
            }
            return true;
        }

        public void Reset() { Dispose(); Start(); }
        public void Dispose()
        {
            if (_state == IntPtr.Zero) return;
            _api!.Destroy(_state);
            _state = IntPtr.Zero;
        }

        private void Start()
        {
            try
            {
                string path = _libraryPath ?? LocateLibrary();
                LibraryPath = path;
                if (!File.Exists(path)) { Unavailable(Unavailability.LibraryMissing, $"RNNoise was not found at {path}."); return; }
                if (!Path.IsPathFullyQualified(path))
                    throw new ArgumentException("RNNoise requires an absolute library path: " + path);
                _api = Api.Load(path);
                _state = _api.Create(IntPtr.Zero);
                if (_state == IntPtr.Zero) { Unavailable(Unavailability.InitFailed, $"RNNoise could not create its state at {path}."); return; }
                Unavailability = Unavailability.None;
                UnavailabilityDetail = "";
            }
            catch (Exception ex)
            {
                Unavailable(Unavailability.InitFailed,
                    $"RNNoise at {LibraryPath} could not initialise (it may be the wrong architecture or have a missing dependency): {ex.Message}");
            }
        }

        internal static string? FrameSizeProblem(int frame) => frame == Frame ? null
            : $"RNNoise reported a {frame}-sample frame; this build expects {Frame}.";

        private void Unavailable(Unavailability reason, string detail)
        {
            Unavailability = reason;
            UnavailabilityDetail = detail;
            Trace.WriteLine("RnnoiseEngine: unavailable: " + detail);
        }

        internal static string LocateLibrary() => LibraryLocator?.Invoke() ?? DefaultLibraryPath();
        public static string DefaultLibraryPath() => Path.Combine(AppContext.BaseDirectory, "runtimes",
            Environment.Is64BitProcess ? "win-x64" : "win-x86", "native", LibraryFileName);

        /// <summary>Header at v0.1.1: plain C, cdecl even on x86. Blittable arrays are pinned.</summary>
        private sealed class Api
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate IntPtr CreateFn(IntPtr model);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void DestroyFn(IntPtr state);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate int FrameFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate float ProcessFn(IntPtr state, [Out] float[] output, [In] float[] input);
            internal CreateFn Create = null!;
            internal DestroyFn Destroy = null!;
            internal FrameFn FrameSize = null!;
            internal ProcessFn Process = null!;
            private static readonly object Gate = new();
            private static readonly Dictionary<string, Api> Loaded = new(StringComparer.OrdinalIgnoreCase);

            // Handles stay loaded for process lifetime, like DeepFilterNet3.
            internal static Api Load(string path)
            {
                lock (Gate)
                {
                    if (Loaded.TryGetValue(path, out var existing)) return existing;
                    IntPtr handle = NativeLibrary.Load(path);
                    try
                    {
                        var api = new Api {
                            Create = Bind<CreateFn>(handle, "rnnoise_create"),
                            Destroy = Bind<DestroyFn>(handle, "rnnoise_destroy"),
                            FrameSize = Bind<FrameFn>(handle, "rnnoise_get_frame_size"),
                            Process = Bind<ProcessFn>(handle, "rnnoise_process_frame")
                        };
                        // Reject incompatible APIs before retaining a process-lifetime
                        // handle. Failed validation must release its mapped DLL.
                        if (FrameSizeProblem(api.FrameSize()) is string problem)
                            throw new InvalidOperationException(problem);
                        Loaded.Add(path, api);
                        return api;
                    }
                    catch { NativeLibrary.Free(handle); throw; }
                }
            }
            private static T Bind<T>(IntPtr handle, string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
        }
    }
}
