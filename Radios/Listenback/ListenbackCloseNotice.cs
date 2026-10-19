namespace Radios.Listenback;

/// <summary>Choose close-time wording from the record now, not a stale stop result.</summary>
public sealed record ListenbackCloseNotice(string Text, bool Failure)
{
    public static ListenbackCloseNotice ForCurrentRecord(RadioRecoverySnapshot pending, string fallback, bool failure) =>
        pending != null ? new(Lexicon.Get("audio.iq.recovery_pending"), true) :
        new(fallback == Lexicon.Get("audio.iq.recovery_pending") ? Lexicon.Get("audio.iq.no_recovery") : fallback, failure);
}
