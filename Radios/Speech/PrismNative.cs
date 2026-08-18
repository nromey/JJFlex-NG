using System;
using System.Runtime.InteropServices;

namespace Radios.Speech
{
    // P/Invoke layer over Prism's flat C ABI (ethindp/prism, include/prism.h).
    //
    // Adapted from the binding in Noel's CAMM (Civ VI Access), which is the
    // reference implementation and was read rather than reconstructed. The
    // braille entry point below does NOT exist there — Civ VI has no braille
    // surface, JJ Flexible does.
    //
    // Marshalling rules that are easy to get wrong and fail at runtime, not
    // compile time:
    //
    //  - Calling convention is __cdecl (PRISM_CALL). On x64 Windows there is
    //    one native convention so the attribute is cosmetic there, but JJ
    //    Flexible also ships x86, where cdecl vs stdcall is the difference
    //    between working and corrupting the stack.
    //  - `const char*` IN parameters marshal as UTF-8.
    //  - `const char*` RETURN values are owned by Prism and must NOT be freed,
    //    so they come back as IntPtr and are read with PtrToStringUTF8.
    //  - C `bool` is one byte (<stdbool.h>). Every bool is marshalled as U1;
    //    the default 4-byte Win32 BOOL corrupts the call.

    internal enum PrismError
    {
        Ok = 0,
        NotInitialized,
        InvalidParam,
        NotImplemented,
        NoVoices,
        VoiceNotFound,
        SpeakFailure,
        MemoryFailure,
        RangeOutOfBounds,
        Internal,
        NotSpeaking,
        NotPaused,
        AlreadyPaused,
        InvalidUtf8,
        InvalidOperation,
        AlreadyInitialized,
        BackendNotAvailable,
        Unknown,
        InvalidAudioFormat,
        InternalBackendLimitExceeded,
        BackendEnteredUndefinedState,
        Count,
    }

    [Flags]
    internal enum PrismBackendFeature : ulong
    {
        IsSupportedAtRuntime = 1UL << 0,
        SupportsSpeak = 1UL << 2,
        SupportsSpeakToMemory = 1UL << 3,
        SupportsBraille = 1UL << 4,
        SupportsOutput = 1UL << 5,
        SupportsIsSpeaking = 1UL << 6,
        SupportsStop = 1UL << 7,
        SupportsPause = 1UL << 8,
        SupportsResume = 1UL << 9,
    }

    /// <summary>
    /// Mirrors PrismConfig in prism.h. Returned BY VALUE from
    /// prism_config_init and passed by pointer to prism_init.
    ///
    /// **This must match the C layout exactly, field for field.** It was
    /// declared as a single `byte Version` with Pack = 1 — copied from CAMM,
    /// whose binding was written against an older Prism where that was true.
    /// The struct has since grown to roughly 48 bytes on x64, and a struct that
    /// size is returned through a hidden pointer rather than in a register. The
    /// runtime, believing it was one byte, wrote the return value into stack it
    /// did not own: access violation 0xC0000005 inside prism_config_init, on
    /// the very first call, before any managed code could catch it. A native
    /// crash is not catchable, so the app simply vanished on launch.
    ///
    /// NO Pack attribute: the C struct uses default alignment, so this one must
    /// too. Forcing Pack = 1 would silently shift every field after `version`.
    ///
    /// If Prism's config struct changes again, this crashes the same way. Check
    /// it against include/prism.h whenever the pinned commit moves.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PrismConfig
    {
        public byte Version;
        public IntPtr Registry;
        public IntPtr AvailabilityCallback;
        public IntPtr AvailabilityUserdata;
        public uint AvailabilityPollIntervalMs;
        public uint AvailabilityDebounceSamples;
        public uint AvailabilityBackoffMaxMs;
        [MarshalAs(UnmanagedType.U1)] public bool AvailabilityAutoPowerManage;
    }

    internal static class PrismNative
    {
        // Resolved by NativeLoader's DllImportResolver to the per-architecture
        // copy under runtimes/win-{x64,x86}/native/, the same way portaudio and
        // libopus are resolved.
        private const string Lib = "prism";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismConfig prism_config_init();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_init(ref PrismConfig cfg);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void prism_shutdown(IntPtr ctx);

        /// <summary>Auto-select the best backend, preferring a real screen
        /// reader over raw TTS. Returns an owned handle to free with
        /// prism_backend_free.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_registry_acquire_best(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void prism_backend_free(IntPtr backend);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_backend_name(IntPtr backend);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong prism_backend_get_features(IntPtr backend);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_initialize(IntPtr backend);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_output(
            IntPtr backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, [MarshalAs(UnmanagedType.U1)] bool interrupt);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_speak(
            IntPtr backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, [MarshalAs(UnmanagedType.U1)] bool interrupt);

        /// <summary>
        /// Braille only, no speech. Takes no interrupt flag — a braille display
        /// is a surface that gets overwritten, not a queue that gets cut off.
        /// Not bound in CAMM; added here because JJ Flexible pushes a status
        /// line to braille and the multi-braille work depends on this path.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_braille(
            IntPtr backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_stop(IntPtr backend);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_is_speaking(
            IntPtr backend, [MarshalAs(UnmanagedType.U1)] out bool outSpeaking);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_error_string(PrismError error);

        /// <summary>Read a null-terminated UTF-8 string Prism owns. Does NOT free it.</summary>
        internal static string? ReadUtf8(IntPtr p) =>
            p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        internal static string ErrorString(PrismError error)
        {
            try { return ReadUtf8(prism_error_string(error)) ?? error.ToString(); }
            catch { return error.ToString(); }
        }
    }
}
