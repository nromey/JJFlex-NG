#nullable enable

using System;

namespace Radios.SmartLink
{
    /// <summary>
    /// Audio output primitive for a session (D2 discipline — session audio
    /// output goes through this sink, never a direct call to a global output).
    ///
    /// <para>
    /// Sprint 26 implementation (<c>DirectPassthroughSink</c>) ignores every
    /// property and writes samples directly to the default output. The
    /// properties on this interface carry enough primitives for all five
    /// Sprint 28+ audio behaviors — auto-pan on focus, manual pan override,
    /// per-session mute, smart-squelch-off-axis, multi-device routing — so
    /// none of them will require an interface change.
    /// </para>
    /// </summary>
    public interface ISessionAudioSink : IDisposable
    {
        /// <summary>
        /// Stereo position, −1.0 (hard left) through 0.0 (center) through
        /// +1.0 (hard right). Sprint 26: ignored. Sprint 28+: drives auto-pan
        /// on focus + manual pan override.
        /// </summary>
        double Pan { get; set; }

        /// <summary>
        /// Linear gain multiplier. 1.0 = unity, 0.0 = silent, &gt;1.0 = boost
        /// (subject to clipping protection in the implementation). Sprint 26:
        /// ignored. Sprint 28+: drives volume ducking for inactive sessions.
        /// </summary>
        double Gain { get; set; }

        /// <summary>
        /// Hard mute switch. When true, <see cref="Write(ReadOnlySpan{float})"/>
        /// discards samples rather than forwarding them. Sprint 26: ignored.
        /// Sprint 28+: per-session mute toggle in the tab strip.
        /// </summary>
        bool Muted { get; set; }

        /// <summary>
        /// True when the session's tab currently has UI focus. Sprint 26:
        /// ignored. Sprint 28+: drives auto-pan-to-center + unmute policy
        /// for the focused session while non-focused sessions ride their
        /// manual pan/gain settings.
        /// </summary>
        bool IsFocused { get; set; }

        /// <summary>
        /// Write a block of audio samples to the sink. Samples are interleaved
        /// stereo, 32-bit float, −1.0..+1.0 range. The sink applies pan/gain/mute
        /// policy (Sprint 28+) and forwards to its backing output device.
        /// </summary>
        /// <param name="samples">Interleaved stereo sample block.</param>
        void Write(ReadOnlySpan<float> samples);
    }
}
