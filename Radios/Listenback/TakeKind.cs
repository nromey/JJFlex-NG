using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Listenback;

/// <summary>
/// The kinds of take, and the two header formats that carry them (Unified
/// takes, design section 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two formats, four kinds.</b> <see cref="IqFormat"/> is the original
/// off-air recorder's format: the radio's raw IQ in <c>rf.iq</c>, with
/// <c>heard.wav</c> a render of it. It carries <see cref="OffAir"/> ("Heard on
/// the air") and <see cref="Diagnostic"/> (the receive-only diagnostic take).
/// <see cref="AudioFormat"/> is the playback capture: what the radio played
/// back, as it came down the wire, stored in <c>heard.wav</c> with no IQ at
/// all. It carries <see cref="RadioRecording"/> and <see cref="LoopedBack"/>.
/// </para>
/// <para>
/// <b>The format is read first, then the kind.</b> A take is never inferred to
/// be audio because <c>rf.iq</c> is missing: a missing <c>rf.iq</c> on an IQ
/// header is a damaged IQ take, exactly as it was before this file existed.
/// An IQ header with no kind, or a kind this build does not know, is
/// <see cref="UnknownLegacy"/>: such takes were made before the kind was
/// written, and they are listed honestly rather than guessed at.
/// </para>
/// <para>
/// These strings are schema tokens written into <c>take.txt</c>. They are
/// never spoken; <see cref="TakeKindWords"/> holds the words.
/// </para>
/// </remarks>
public static class TakeKind
{
    public const string IqFormat = "jjflex-iq-take-1";
    public const string AudioFormat = "jjflex-audio-take-1";

    /// <summary>Heard on the air: the transmitted signal, received as raw IQ and demodulated in software.</summary>
    public const string OffAir = "off-air";
    /// <summary>Diagnostic: a receive-only IQ capture, no transmission of the operator's own.</summary>
    public const string Diagnostic = "receive-only-diagnostic";
    /// <summary>Radio recording: what the radio's recorder saved before transmitting, as the radio played it back.</summary>
    public const string RadioRecording = "radio-recording";
    /// <summary>Looped back: the signal out the transverter port and back through the radio's receiver and recorder, as the radio played it back.</summary>
    public const string LoopedBack = "looped-back";
    /// <summary>An IQ take whose header names no kind this build knows.</summary>
    public const string UnknownLegacy = "unknown-legacy";
    /// <summary>A folder whose header cannot be read, or whose format or kind is invalid.</summary>
    public const string Damaged = "damaged";

    /// <summary>The kinds a header may name, in the order the Show selector lists them.</summary>
    public static IReadOnlyList<string> Known { get; } = new[] { OffAir, RadioRecording, LoopedBack, Diagnostic };

    public static bool IsAudio(string kind) => kind is RadioRecording or LoopedBack;
    public static bool IsIq(string kind) => kind is OffAir or Diagnostic or UnknownLegacy;

    /// <summary>
    /// The kind a header describes, from its format and kind lines. Never
    /// reads the take's files: a damaged audio file is still a Radio recording,
    /// one that cannot be played.
    /// </summary>
    public static string Of(IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        string format = fields.LastOrDefault(f => f.Key == "format").Value;
        string kind = fields.LastOrDefault(f => f.Key == "kind").Value;
        if (format == IqFormat)
            return kind is OffAir or Diagnostic ? kind
                : IsAudio(kind ?? "") ? Damaged
                : UnknownLegacy;
        if (format == AudioFormat)
            return IsAudio(kind ?? "") ? kind : Damaged;
        return Damaged;
    }
}
