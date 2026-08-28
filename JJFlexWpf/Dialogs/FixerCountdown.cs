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
/// </remarks>
internal static class FixerCountdown
{
    /// <summary>The count-in before a stage that listens: stage 1's
    /// microphone check.</summary>
    public static void RecordTone()
        => Guarded(EarconPlayer.CountdownRecordTone, "CountdownRecordTone");

    /// <summary>The count-in before a stage that keys the transmitter:
    /// stages 2, 3 and 4 — the same sound for all three until Noel rules on a
    /// distinct RF sound, which is different numbers, not different code.</summary>
    public static void TransmitTone()
        => Guarded(EarconPlayer.CountdownTransmitTone, "CountdownTransmitTone");

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
