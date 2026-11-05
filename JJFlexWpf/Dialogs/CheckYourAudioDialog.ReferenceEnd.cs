using System;
using System.Threading.Tasks;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio: when a reference recording reaches its end, the
/// transmission ends (Unified takes, round three, part 5b).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled by Noel 2026-11-04 11:36:</b> <i>"I don't see a point of giving
/// the mic back. If you're using reference audio, why would you want to talk
/// after that."</i> And at 18:45, for a key the operator holds through the
/// app: <i>"I'd unkey."</i> Until then the reference handed the microphone
/// back and the transmission went on, on the bench for about 80 seconds.
/// </para>
/// <para>
/// <b>Who holds the transmission decides the route, and there is one route for
/// each.</b> A check that keyed stops exactly as pressing Stop stops it,
/// through <c>AudioCheckSession.StopCheck</c>, the call the Stop button,
/// Ctrl+Enter and Escape in another window all make; never a second unkey. A
/// key the app holds (the lock, Ctrl+Space, the transmit toggle) is released
/// through the PTT controller, which owns all keying; writing the radio's
/// transmit flag directly would leave the controller believing it still holds
/// the lock. A transmission the app does not hold, a hardware push-to-talk
/// or footswitch above all, cannot be released by software and is left as it
/// was; that case is Noel's to rule.
/// </para>
/// <para>
/// <b>The app's key is let go the house way for a transmission a window ends
/// outside the key edges:</b> <c>DialogTransmitKeys.EscapeUnkey</c>, which is
/// <c>MainWindow.HandleDialogEscapeUnkey</c>, the controller's
/// <c>EscapeUnlock</c> and then the edges forgetting the hold, as Escape in a
/// dialog does. With no main window wired (a test), the controller alone.
/// </para>
/// <para>
/// <b>Not solved here, and reported:</b> an operator still holding Ctrl+Space
/// under JAWS, which delivers a held key as repeated down/up pairs. Once the
/// controller is idle, the next synthetic up only clears the edge's guard and
/// the next synthetic down is a new press, so the radio keys again on the
/// microphone about a quarter of a second later. The edges forgetting the hold
/// or not makes no difference to that. Escape and the transmit timeout share
/// it today. Under NVDA a held key repeats, and repeats never key.
/// </para>
/// <para>
/// <b>The tail.</b> <c>ReachedEnd</c> means the player handed over its last
/// samples, not that the radio has sent them: the encoder, the network and
/// the radio's own buffer lie between. So the unkey waits
/// <see cref="ReferenceEndTail"/> after the end is seen. Unmeasured; the
/// marker recording's closing 2 kHz beep makes it measurable at the bench.
/// The microphone is already back on the encoder during the tail, as it was
/// for the whole rest of the transmission before this ruling.
/// </para>
/// </remarks>
public partial class CheckYourAudioDialog
{
    /// <summary>Who holds the transmission at the moment a reference reaches its end.</summary>
    internal enum ReferenceEndHolder
    {
        /// <summary>Nothing is transmitting.</summary>
        Nobody,
        /// <summary>A check in this window keyed it; the check stops.</summary>
        Check,
        /// <summary>The app's PTT controller holds the key; it lets go.</summary>
        App,
        /// <summary>The radio is transmitting and the app does not hold it: a hardware line, most likely. Left alone.</summary>
        Elsewhere,
    }

    /// <summary>
    /// How long after the end is seen before the unkey, so the recording's last
    /// samples reach the air. Not measured; see the remarks.
    /// </summary>
    internal static readonly TimeSpan ReferenceEndTail = TimeSpan.FromMilliseconds(300);

    /// <summary>The pure decision. A check keys through the controller too, so the check is asked first.</summary>
    internal static ReferenceEndHolder WhoHoldsTheReference(bool checkKeyed, bool appKeyed, bool radioTransmitting) =>
        checkKeyed ? ReferenceEndHolder.Check
        : appKeyed ? ReferenceEndHolder.App
        : radioTransmitting ? ReferenceEndHolder.Elsewhere
        : ReferenceEndHolder.Nobody;

    /// <summary>
    /// What is said when the reference reaches its end. Only a transmission the
    /// app does not hold carries on, with the microphone, so only that one says so.
    /// </summary>
    internal static string ReferenceFinishedSentence(ReferenceEndHolder holder) =>
        holder == ReferenceEndHolder.Elsewhere
            // Today's wording, kept for the case Noel has not ruled on.
            ? Lexicon.Get("audio.check.reference_finished_mic_back")
            // DRAFT PROSE (#864, round three): awaiting Noel.
            : Lexicon.Get("audio.reference.finished");

    private ReferenceEndHolder ReferenceHolderNow(FlexBase rig)
    {
        var ptt = PttControllerSource?.Invoke();
        return WhoHoldsTheReference(_session?.Keyed == true, ptt?.IsTransmitting == true, rig.Transmit);
    }

    /// <summary>After the tail, end the transmission the reference rode, if it is still held the same way.</summary>
    private async void EndTransmissionAfterReferenceTail(FlexBase rig, ReferenceEndHolder holder)
    {
        object? session = _session;
        try { await Task.Delay(ReferenceEndTail); }
        catch (Exception) { return; }
        EndTransmissionForReference(rig, holder, session);
    }

    private sealed partial class AudioCheckSession
    {
        /// <summary>True while the check is keyed: the one phase in which Stop unkeys it.</summary>
        internal bool Keyed => Active && _phase == Phase.Keyed;
    }

    /// <summary>
    /// End the transmission a finished reference rode, by its holder's own
    /// route, rechecked now: the same radio, the same session, held the same
    /// way. Returns what it did.
    /// </summary>
    internal ReferenceEndHolder EndTransmissionForReference(FlexBase rig, ReferenceEndHolder planned, object? plannedSession)
    {
        if (!ReferenceEquals(rig, _rig)) return ReferenceEndHolder.Nobody;
        var now = ReferenceHolderNow(rig);
        if (now != planned) return ReferenceEndHolder.Nobody;
        switch (now)
        {
            case ReferenceEndHolder.Check when ReferenceEquals(_session, plannedSession) && _session != null:
                // Exactly what the Stop button does while keyed (ToggleAudioCheck).
                _session.StopCheck();
                return ReferenceEndHolder.Check;
            case ReferenceEndHolder.App:
                // Through the controller, never the radio's transmit flag: that
                // would leave the controller believing it still holds the key.
                if (DialogTransmitKeys.EscapeUnkey is { } unkey) unkey();
                else PttControllerSource?.Invoke()?.EscapeUnlock();
                return ReferenceEndHolder.App;
            default:
                return ReferenceEndHolder.Nobody;
        }
    }
}
