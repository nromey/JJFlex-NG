using System;
using System.Runtime.InteropServices;

namespace POpusCodec
{
    /// <summary>
    /// Public access to the Opus library's self-reported version string.
    ///
    /// This P/Invoke deliberately lives in the POpusCodec assembly: the app's
    /// NativeLoader registers its runtimes\win-{arch}\native resolver against
    /// THIS assembly, so a declaration anywhere else would resolve
    /// libopus.dll through a different (wrong) search path.
    ///
    /// The DLL ships without a Windows version resource, so
    /// opus_get_version_string() is the only honest answer to "which Opus is
    /// actually running?" — file properties show nothing.
    ///
    /// Note: the internal Wrapper class has its own opus_get_version(), which
    /// fills OpusEncoder.Version and OpusDecoder.Version. It read the string
    /// with PtrToStringAuto until #807, which on Windows reads the 8-bit
    /// string as UTF-16 and returns garbage; it now uses PtrToStringAnsi, the
    /// same as this accessor, so the two agree. This one needs no encoder or
    /// decoder, so it stays the one to call.
    /// </summary>
    public static class OpusInfo
    {
        [DllImport("libopus.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "opus_get_version_string")]
        private static extern IntPtr opus_get_version_string_native();

        /// <summary>
        /// The version string embedded in the loaded libopus.dll, e.g.
        /// "libopus 1.6.1". Throws (DllNotFoundException and friends) when the
        /// native library cannot load — callers report that honestly rather
        /// than substituting a guess.
        /// </summary>
        public static string VersionString()
        {
            return Marshal.PtrToStringAnsi(opus_get_version_string_native()) ?? "";
        }
    }
}
