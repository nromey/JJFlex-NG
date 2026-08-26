using System;
using System.Diagnostics;
using System.Reflection;
using JJTrace;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The transmit checks' side of the Sprint 35 countdown contract (#261).
/// </summary>
/// <remarks>
/// <para>
/// <b>The SOUND belongs to Track G; the WIRING is this track's.</b> Track G
/// builds two methods in <c>EarconPlayer</c>, named exactly
/// <c>CountdownRecordTone</c> and <c>CountdownTransmitTone</c> — three count
/// tones and an octave-up "go", the record variant landing on the octave and
/// the transmit variant on the stretched TX start. Those two names are the
/// inter-track contract: neither track may rename them unilaterally.
/// </para>
/// <para>
/// <b>Why this forwards by reflection instead of calling directly, for now.</b>
/// The two tracks build in parallel worktrees from the same base commit, so
/// the methods do not exist in this tree yet and a direct call would not
/// compile here. Reflection lets both branches build alone and self-connect
/// at the sprint merge with no integration line. The cost is that a contract
/// violation stops being a build break — so a missing method is traced as an
/// ERROR on every attempt, never swallowed, and the integration pass (#256)
/// should replace these bodies with direct calls once both tracks are merged,
/// restoring compile-time enforcement. That replacement is two lines.
/// </para>
/// <para>
/// A countdown that cannot sound must never cost the operator the check:
/// callers get a no-op plus a loud trace, and the stage runs on its spoken
/// cues alone.
/// </para>
/// </remarks>
internal static class FixerCountdown
{
    private const string RecordName = "CountdownRecordTone";
    private const string TransmitName = "CountdownTransmitTone";

    private static readonly MethodInfo? _record = Find(RecordName);
    private static readonly MethodInfo? _transmit = Find(TransmitName);

    /// <summary>The count-in before a stage that listens: stage 1's
    /// microphone check.</summary>
    public static void RecordTone() => Play(_record, RecordName);

    /// <summary>The count-in before a stage that keys the transmitter:
    /// stages 3 and 4 — the same sound for both until Noel rules on a
    /// distinct RF sound, which is different numbers, not different code.</summary>
    public static void TransmitTone() => Play(_transmit, TransmitName);

    private static MethodInfo? Find(string name)
    {
        try
        {
            return typeof(EarconPlayer).GetMethod(
                name, BindingFlags.Public | BindingFlags.Static,
                binder: null, types: Type.EmptyTypes, modifiers: null);
        }
        catch { return null; }
    }

    private static void Play(MethodInfo? method, string name)
    {
        if (method == null)
        {
            // Loud on purpose. A silent no-op here is the dead-Alt+L shape:
            // a cue that compiles, reviews clean, and never sounds.
            Tracing.TraceLine("FixerCountdown: EarconPlayer." + name + " is missing — the "
                + "countdown did not sound. Track G's earcon has not landed, or the "
                + "contract name changed (#261).", TraceLevel.Error);
            return;
        }

        try { method.Invoke(null, null); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerCountdown: " + name + " threw and was ignored — "
                              + ex.Message, TraceLevel.Warning);
        }
    }
}
