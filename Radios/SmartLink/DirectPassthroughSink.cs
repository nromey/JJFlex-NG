#nullable enable

using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>
    /// Sprint 26 implementation of <see cref="ISessionAudioSink"/>. Stores every
    /// property on the interface but acts on none of them — samples are forwarded
    /// to an injected downstream writer (or dropped silently if no writer is
    /// wired). This is intentional:
    ///
    /// <list type="bullet">
    /// <item><description>Sprint 26 preserves existing audio behavior: Phase 2
    /// wires <see cref="FlexBase"/>'s audio path to call <see cref="Write"/>,
    /// and this sink forwards to the previously-direct output. No behavior
    /// change to the user's ears.</description></item>
    /// <item><description>Sprint 28+ replaces this sink with a real policy
    /// implementation that honors Pan/Gain/Muted/IsFocused. The interface
    /// shape stays identical; only the concrete class at this seam changes.</description></item>
    /// </list>
    ///
    /// <para>
    /// The downstream <see cref="SinkWriter"/> delegate is an escape hatch for
    /// Phase 2 wiring: <c>FlexBase</c> sets it when wiring the active session's
    /// sink to the PortAudio output. If unset, <see cref="Write"/> is a no-op
    /// (useful during Phase 1 isolation testing where no audio output is
    /// actually wanted).
    /// </para>
    /// </summary>
    public sealed class DirectPassthroughSink : ISessionAudioSink
    {
        /// <summary>
        /// Downstream sample writer. Null = drop. Phase 2 wires this to the
        /// existing PortAudio path.
        /// </summary>
        public Action<ReadOnlySpan<float>>? SinkWriter { get; set; }

        public double Pan { get; set; }
        public double Gain { get; set; } = 1.0;
        public bool Muted { get; set; }
        public bool IsFocused { get; set; } = true;

        public void Write(ReadOnlySpan<float> samples)
        {
            // Sprint 26 policy: forward unchanged if a writer is wired; otherwise drop.
            // Sprint 28+ replaces this method with pan/gain/mute application.
            SinkWriter?.Invoke(samples);
        }

        public void Dispose()
        {
            SinkWriter = null;
            Tracing.TraceLine("DirectPassthroughSink disposed", TraceLevel.Verbose);
        }
    }
}
