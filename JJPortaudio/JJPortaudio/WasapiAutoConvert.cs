using System;
using System.Runtime.InteropServices;

namespace JJPortaudio
{
    /// <summary>
    /// The caller-side lever for WASAPI's shared-mode sample-rate refusal
    /// (Track B, 2026-08-18, #12).
    ///
    /// WASAPI shared mode accepts exactly one sample rate — the endpoint's
    /// own mix format — and refuses every other. That is the honesty the
    /// project chose it for (Devices.DefaultHostApiTypeId). But an Opus
    /// stream may only run at a rate Opus can encode, and a device whose
    /// Windows mix format is 44.1 kHz offers none of them, so with WASAPI as
    /// the DEFAULT host API such a device could not carry radio audio at
    /// all: the negotiation ladder legally cannot pick 44100 for an Opus
    /// stream, and everything else is refused. Verified against the pinned
    /// PortAudio source (a880212, pa_win_wasapi.c): without a stream info,
    /// GetClosestFormat answers a non-mix-format rate with
    /// paInvalidSampleRate via the sharedClosestMatch path.
    ///
    /// The escape hatch is PaWasapiStreamInfo with the paWinWasapiAutoConvert
    /// flag, passed through PaStreamParameters.hostApiSpecificStreamInfo.
    /// With it, the same source short-circuits Pa_IsFormatSupported to
    /// success for shared mode, and Pa_OpenStream adds
    /// AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | SRC_DEFAULT_QUALITY so Windows'
    /// own converter bridges the rates. The engine engages this as a LAST
    /// resort only — after native negotiation at the requested rate and the
    /// whole fallback ladder have failed — and says so in the trace, because
    /// a resampled stream reported as native would be the exact lie choosing
    /// WASAPI was meant to end.
    ///
    /// The struct layout mirrors include/pa_win_wasapi.h at the pinned
    /// commit exactly: five 32-bit fields, two pointers, three 32-bit enums,
    /// and the four-field PaWasapiStreamPassthrough added in 19.8.0. Natural
    /// alignment matches the C compiler on both x64 and x86, and the
    /// version-checked <c>size</c> field means a mismatch would be rejected
    /// by PortAudio rather than silently misread.
    /// </summary>
    internal static class WasapiAutoConvert
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PaWasapiStreamInfo
        {
            public uint size;               // sizeof(PaWasapiStreamInfo)
            public int hostApiType;         // PaHostApiTypeId: 13 = paWASAPI
            public uint version;            // 1
            public uint flags;              // PaWasapiFlags
            public uint channelMask;        // used only with paWinWasapiUseChannelMask
            public IntPtr hostProcessorOutput;
            public IntPtr hostProcessorInput;
            public int threadPriority;      // used only with paWinWasapiThreadPriority
            public int streamCategory;      // eAudioCategoryOther = 0
            public int streamOption;        // eStreamOptionNone = 0
            // PaWasapiStreamPassthrough (19.8.0), inert without the flag.
            public uint passthroughFormatId;
            public uint passthroughEncodedSamplesPerSec;
            public uint passthroughEncodedChannelCount;
            public uint passthroughAverageBytesPerSec;
        }

        /// <summary>paWinWasapiAutoConvert, 1 &lt;&lt; 6 in PaWasapiFlags.</summary>
        private const uint PaWinWasapiAutoConvertFlag = 1u << 6;

        private const int PaWasapiHostApiTypeId = 13; // PaHostApiTypeId.paWASAPI

        /// <summary>
        /// Allocate an unmanaged PaWasapiStreamInfo carrying only the
        /// AutoConvert flag. The pointer stays valid until
        /// <see cref="Release"/> — held for the stream's whole life rather
        /// than reasoning about how long PortAudio keeps it, because 72
        /// bytes is cheaper than being wrong about that.
        /// </summary>
        public static IntPtr Allocate()
        {
            var info = new PaWasapiStreamInfo
            {
                size = (uint)Marshal.SizeOf<PaWasapiStreamInfo>(),
                hostApiType = PaWasapiHostApiTypeId,
                version = 1,
                flags = PaWinWasapiAutoConvertFlag,
            };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<PaWasapiStreamInfo>());
            Marshal.StructureToPtr(info, p, false);
            return p;
        }

        /// <summary>Free a blob from <see cref="Allocate"/> and zero the
        /// caller's reference. Safe on an already-zero reference.</summary>
        public static void Release(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            Marshal.FreeHGlobal(p);
            p = IntPtr.Zero;
        }
    }
}
