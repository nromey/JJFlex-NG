#nullable enable
using System;

namespace Radios.Speech
{
    /// <summary>
    /// A sentence built at the moment of hand-over, with the callback that
    /// wants to know what became of it (<see cref="SpeechArbiter.LatestComposed"/>,
    /// #747 round twelve of the 2026-11-06 junk box).
    /// </summary>
    /// <param name="Text">What to say.</param>
    /// <param name="Outcome">Told the reader's answer for exactly this text, once; never for an untracked or refused hand-over.</param>
    public readonly record struct SpeechComposition(string Text, Action<SpeechOutcome>? Outcome);
}
