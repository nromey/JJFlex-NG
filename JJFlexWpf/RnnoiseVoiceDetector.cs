using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace JJFlexWpf
{
    /// <summary>
    /// Xiph RNNoise v0.1.1's voice-activity branch, run on its own: the feature
    /// analysis and the three layers that make the voice probability, without the
    /// noise and denoise layers, the gains or the synthesis. Mono, 48 kHz, 480-sample
    /// frames; the probability for a frame describes that frame (v0.1.1 has no
    /// look-ahead, unlike the 0.2-line engine in <see cref="RnnoiseEngine"/>).
    /// </summary>
    /// <remarks>
    /// Loaded from its own library, <c>rnnoise_vad011.dll</c>, by absolute path, the way
    /// <see cref="RnnoiseEngine"/> loads <c>rnnoise.dll</c>. That library exports only
    /// <c>rnvad011_*</c> names, so it can never be confused with the engine's
    /// <c>rnnoise_*</c> exports in one process. Built from the v0.1.1 pin by the recipe in
    /// runtimes/win-x64/native/README.md, which also says what C file adds the entry
    /// points. Its probability equals the full v0.1.1 frame's return value bit for bit
    /// (RnnoiseVoiceDetectorTests).
    ///
    /// Ruled by Noel 2026-11-08 13:41 (#873): stored whether or not anything uses it.
    /// Nothing in the app calls it yet; whether the lag shifter does is his to decide.
    /// </remarks>
    public sealed class RnnoiseVoiceDetector : IDisposable
    {
        public const int Frame = 480;
        public const int SampleRate = 48000;
        public const string LibraryFileName = "rnnoise_vad011.dll";
        private const float Scale = 32767f;
        private const float InverseScale = 3.051851E-05f; // as RnnoiseEngine's
        private readonly string? _libraryPath;
        private readonly float[] _in = new float[Frame];
        private readonly float[] _out = new float[Frame];
        private Api? _api;
        private IntPtr _state;

        public RnnoiseVoiceDetector(string? libraryPath = null)
        {
            _libraryPath = libraryPath;
            Start();
        }

        public bool IsAvailable => _state != IntPtr.Zero;
        public Unavailability Unavailability { get; private set; }
        public string UnavailabilityDetail { get; private set; } = "";
        public string LibraryPath { get; private set; } = "";

        /// <summary>
        /// The voice probability, 0 to 1, for one 480-sample frame of audio in the
        /// -1 to +1 range (converted once to the 16-bit range RNNoise expects, as
        /// <see cref="RnnoiseScaling.Corrected"/> does). A frame below RNNoise's own
        /// silence threshold returns 0 and leaves the detector's memory untouched.
        /// </summary>
        public float Process(ReadOnlySpan<float> input)
        {
            Prepare(input);
            return _api!.Process(_state, _in);
        }

        /// <summary>
        /// The FULL v0.1.1 frame on this detector's state: the denoised frame into
        /// <paramref name="output"/> and the voice probability as the return. For the
        /// positive control and offline comparison only; never mix it with
        /// <see cref="Process"/> on one detector, since the two advance the state
        /// differently. Call <see cref="Reset"/> between them.
        /// </summary>
        internal float ReferenceFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            Prepare(input);
            if (output.Length != Frame) throw new ArgumentException($"RNNoise frames are exactly {Frame} samples.");
            float probability = _api!.Reference(_state, _out, _in);
            for (int i = 0; i < Frame; i++) output[i] = _out[i] * InverseScale;
            return probability;
        }

        public void Reset() { Stop(); Start(); }

        public void Dispose() => Stop();

        // Start's counterpart, the shape DeepFilterNet3Engine and SpecbleachEngine use.
        // It does what RnnoiseEngine.Dispose does; the two bind different libraries by
        // different export names, so they do not share a loader (Track R report).
        private void Stop()
        {
            if (_state == IntPtr.Zero) return;
            _api!.Destroy(_state);
            _state = IntPtr.Zero;
        }

        private void Prepare(ReadOnlySpan<float> input)
        {
            if (!IsAvailable) throw new InvalidOperationException("The voice detector is not available: " + UnavailabilityDetail);
            if (input.Length != Frame) throw new ArgumentException($"RNNoise frames are exactly {Frame} samples.");
            for (int i = 0; i < Frame; i++) _in[i] = input[i] * Scale;
        }

        private void Start()
        {
            try
            {
                string path = _libraryPath ?? LocateLibrary();
                LibraryPath = path;
                if (!File.Exists(path)) { Unavailable(Unavailability.LibraryMissing, $"The voice detector was not found at {path}."); return; }
                if (!Path.IsPathFullyQualified(path))
                    throw new ArgumentException("The voice detector requires an absolute library path: " + path);
                _api = Api.Load(path);
                _state = _api.Create();
                if (_state == IntPtr.Zero) { Unavailable(Unavailability.InitFailed, $"The voice detector could not create its state at {path}."); return; }
                Unavailability = Unavailability.None;
                UnavailabilityDetail = "";
            }
            catch (Exception ex)
            {
                Unavailable(Unavailability.InitFailed,
                    $"The voice detector at {LibraryPath} could not initialise (it may be the wrong architecture or have a missing dependency): {ex.Message}");
            }
        }

        internal static string? FrameSizeProblem(int frame) => frame == Frame ? null
            : $"The voice detector reported a {frame}-sample frame; this build expects {Frame}.";

        private void Unavailable(Unavailability reason, string detail)
        {
            Unavailability = reason;
            UnavailabilityDetail = detail;
            Trace.WriteLine("RnnoiseVoiceDetector: unavailable: " + detail);
        }

        // No locator hook yet: nothing in the app constructs a detector (#873). When
        // something does, wire one from NativeLoader the way RnnoiseEngine's is.
        internal static string LocateLibrary() => DefaultLibraryPath();
        public static string DefaultLibraryPath() => Path.Combine(AppContext.BaseDirectory, "runtimes",
            Environment.Is64BitProcess ? "win-x64" : "win-x86", "native", LibraryFileName);

        /// <summary>Our own entry points (native/rnvad011/rnvad011.c): plain C, cdecl even on x86.</summary>
        private sealed class Api
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate IntPtr CreateFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void DestroyFn(IntPtr state);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate int FrameFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate float ProcessFn(IntPtr state, [In] float[] input);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate float ReferenceFn(IntPtr state, [Out] float[] output, [In] float[] input);
            internal CreateFn Create = null!;
            internal DestroyFn Destroy = null!;
            internal FrameFn FrameSize = null!;
            internal ProcessFn Process = null!;
            internal ReferenceFn Reference = null!;
            private static readonly object Gate = new();
            private static readonly Dictionary<string, Api> Loaded = new(StringComparer.OrdinalIgnoreCase);

            // Handles stay loaded for process lifetime, like RnnoiseEngine's.
            internal static Api Load(string path)
            {
                lock (Gate)
                {
                    if (Loaded.TryGetValue(path, out var existing)) return existing;
                    IntPtr handle = NativeLibrary.Load(path);
                    try
                    {
                        var api = new Api {
                            Create = Bind<CreateFn>(handle, "rnvad011_create"),
                            Destroy = Bind<DestroyFn>(handle, "rnvad011_destroy"),
                            FrameSize = Bind<FrameFn>(handle, "rnvad011_frame_size"),
                            Process = Bind<ProcessFn>(handle, "rnvad011_process_frame"),
                            Reference = Bind<ReferenceFn>(handle, "rnvad011_reference_frame")
                        };
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
