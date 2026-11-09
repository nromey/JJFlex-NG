namespace Radios.Listenback;

/// <summary>
/// The words for each kind of take: the name the Show selector and each row
/// use, and the phrase a comparison sentence uses ("One of these takes was
/// heard on the air and the other was looped back ...").
/// </summary>
/// <remarks>
/// Names RULED by Noel 2026-11-02 17:17-17:28 ("Heard on the air", "Radio
/// recording", "Looped back") and 19:50 (D6, "Diagnostic"); the phrases are
/// the 17:31 ruling's. The names for an older take of unrecorded kind and for
/// a damaged take are DRAFT.
/// </remarks>
public static class TakeKindWords
{
    public static string Name(string kind) => kind switch
    {
        TakeKind.OffAir => Lexicon.Get("audio.takes.kind.off_air"),
        TakeKind.RadioRecording => Lexicon.Get("audio.takes.kind.radio_recording"),
        TakeKind.LoopedBack => Lexicon.Get("audio.takes.kind.looped_back"),
        TakeKind.Diagnostic => Lexicon.Get("audio.takes.kind.diagnostic"),
        // PLACEHOLDER: Codex writes (#999).
        TakeKind.SliceRecording => Lexicon.Get("audio.takes.kind.slice_recording"),
        TakeKind.UnknownLegacy => Lexicon.Get("audio.takes.kind.unknown"),
        _ => Lexicon.Get("audio.takes.kind.damaged")
    };

    public static string Phrase(string kind) => kind switch
    {
        TakeKind.OffAir => Lexicon.Get("audio.takes.phrase.off_air"),
        TakeKind.RadioRecording => Lexicon.Get("audio.takes.phrase.radio_recording"),
        TakeKind.LoopedBack => Lexicon.Get("audio.takes.phrase.looped_back"),
        TakeKind.Diagnostic => Lexicon.Get("audio.takes.phrase.diagnostic"),
        // PLACEHOLDER: Codex writes (#999).
        TakeKind.SliceRecording => Lexicon.Get("audio.takes.phrase.slice_recording"),
        _ => Lexicon.Get("audio.takes.phrase.unknown")
    };
}
