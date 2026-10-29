using System;
using System.Diagnostics;
using JJTrace;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The transmit checks' side of the Sprint 35 countdown contract (#261).
/// </summary>
/// <remarks>
/// <para>
/// <b>The SOUND belongs to Track G; the WIRING is this class's.</b> The two
/// methods in <c>EarconPlayer</c> — <c>CountdownRecordTone</c> and
/// <c>CountdownTransmitTone</c> — are three count tones and an octave-up "go",
/// the record variant landing on the octave and the transmit variant on the
/// stretched TX start. Those two names are the inter-track contract.
/// </para>
/// <para>
/// <b>These were reflection forwards until Sprint 37 Track N.</b> The two
/// tracks built in parallel worktrees from one base commit, so the methods did
/// not exist in this tree and a direct call would not compile; reflection let
/// both branches build alone and self-connect at the merge. The cost was that
/// a contract violation stopped being a build break, and the remark here
/// instructed whoever came after the merge to restore the direct calls — done,
/// so a renamed earcon is once again a compile error rather than a runtime
/// trace nobody reads.
/// </para>
/// <para>
/// A countdown that cannot sound must never cost the operator the check:
/// callers get the failure traced, never thrown, and the stage runs on its
/// spoken cues alone.
/// </para>
/// <para>
/// <b>WHICH STAGE GETS WHICH SOUND, written down here because it was written
/// down nowhere</b> (#396). The Fixer numbers its stages 0 to 4; the earcons
/// are named "record" and "transmit"; and nothing connected the two, so a
/// report of "stage two sounds different from stage one" could not be checked
/// against the code without reading three files. The mapping is:
/// </para>
/// <para>
/// Stage 0, audio setup and receive — NO countdown. Nothing is measured that a
/// person has to be ready for.
/// </para>
/// <para>
/// Stage 1, microphone test — <see cref="RecordTone"/>. Four events: three
/// counts, then the octave up. Nothing keys.
/// </para>
/// <para>
/// Stages 2, 3 and 4 — transmitter test, injected transmit, spoken transmit —
/// <see cref="TransmitTone"/>. FIVE events: three counts, then a rising PAIR
/// rather than a single note. All three key the radio.
/// </para>
/// <para>
/// <b>So one countdown genuinely has more tones than the other, and a pitch
/// the other never uses.</b> That difference is deliberate — the landing names
/// what is being counted down TO, and the transmit landing is the transmit
/// start figure drawn out slow, so an operator learns one shape and reads it
/// at two speeds. It is not a bug and it is not a doubled countdown: the field
/// trace of 2026-08-29 shows exactly one fire per stage run, never two.
/// </para>
/// </remarks>
internal static class FixerCountdown
{
    /// <summary>The count-in before a stage that listens: stage 1's
    /// microphone check. Four events, ending on the octave up.</summary>
    public static void RecordTone()
        => Guarded(EarconPlayer.CountdownRecordTone, "CountdownRecordTone");

    /// <summary>The count-in before a stage that keys the transmitter:
    /// stages 2, 3 and 4 — the same sound for all three until Noel rules on a
    /// distinct RF sound, which is different numbers, not different code.
    /// Four events, ending on the octave — which means the radio is ON.
    /// Ruled 2026-08-30; it was five, ending on a rising pair.</summary>
    public static void TransmitTone()
        => Guarded(EarconPlayer.CountdownTransmitTone, "CountdownTransmitTone");

    /// <summary>The end of ANY test — keying or not. Ruled 2026-08-30: the
    /// same sequence for all tests, and it ends on a fall. Guarded like the
    /// countdowns, because a sound that throws must never be what stops a
    /// stage from finishing.</summary>
    public static void StageDone()
        => Guarded(EarconPlayer.FixerStageDoneTone, "FixerStageDoneTone");

    /// <summary>
    /// One beat of the countdown, for a count played a beat at a time (#835):
    /// Check your audio's, whose last beat waits for the setup, and the
    /// Countdown settings' Play sample. The same sound as the figure above,
    /// through the same seam, so there is one countdown in the app.
    /// </summary>
    public static void Beat(Radios.CountdownBeat beat)
        => Guarded(() => EarconPlayer.CountdownBeatTone(beat.Hz, beat.Ms, beat.IsLanding),
                   beat.IsLanding ? "CountdownBeatTone landing" : "CountdownBeatTone count");

    /// <summary>
    /// Where a running <see cref="Radios.CountdownRun"/> plays and speaks:
    /// tones through <see cref="Beat"/>, words through the screen reader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Critical, because sounds-off makes the words the only warning.</b>
    /// With the transmit sounds switched off the spoken count is all that
    /// stands between an operator and a keying radio, so it is not left to the
    /// verbosity filter. Which pieces are spoken at all is the plan's decision
    /// (sounds off, or sounds on and Chatty), made before it starts.
    /// </para>
    /// <para>
    /// <b>Queued, not interrupting, and followed to delivery (Codex round 1).</b>
    /// The words go through <see cref="Radios.ScreenReaderOutput.CountdownSpeech"/>:
    /// additive on <see cref="Radios.Speech.SpeechSubject.CheckCountdown"/> so
    /// no number retires another, asked after so the count waits for them, and
    /// taken back out of the delivery queue when the count is called off.
    /// </para>
    /// </remarks>
    public sealed class Voice : Radios.ICountdownOutput
    {
        /// <summary>The one instance; it holds no state.</summary>
        public static Voice Instance { get; } = new();

        private Voice() { }

        /// <inheritdoc/>
        public void Tone(Radios.CountdownBeat beat) => Beat(beat);

        /// <inheritdoc/>
        public Radios.ICountdownWords Say(string text, bool onlyWarning) =>
            Radios.ScreenReaderOutput.CountdownSpeech.Say(text, onlyWarning);

        /// <inheritdoc/>
        public void WithdrawSpeech(string why) => Radios.ScreenReaderOutput.CountdownSpeech.Withdraw(why);
    }

    private static void Guarded(Action tone, string name)
    {
        try { tone(); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerCountdown: " + name + " threw and was ignored — "
                              + ex.Message, TraceLevel.Warning);
        }
    }
}
