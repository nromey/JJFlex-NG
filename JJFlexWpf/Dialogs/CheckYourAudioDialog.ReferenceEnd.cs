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
/// <b>The app's key is let go through MainWindow's key edges</b>
/// (<see cref="EndAppKeyForReference"/>, which is
/// <c>MainWindow.HandleReferenceEndUnkey</c>): the controller's unkey, and
/// then the edges' input state. With no main window wired (a test), the
/// controller alone.
/// </para>
/// <para>
/// <b>A held Ctrl+Space stays ended until the operator lets go (round five,
/// 7a).</b> JAWS delivers a held key as repeated down/up pairs. Until round
/// five the edges forgot the hold here, so once the controller was idle the
/// next synthetic up only cleared the edge's guard and the next synthetic down
/// was a new press: the radio keyed again about a quarter of a second later,
/// and after receive that was the microphone. Now a hold the app ends is
/// latched, and the hold filter goes on judging the key until it confirms a
/// real release. The other ways a hold ends without a release (Escape, the
/// kill switch, a reflected-power cut, the radio turning transmit off on its
/// own) still forget it, and share the old behaviour (reported, not changed).
/// Under NVDA a held key repeats, and repeats never key.
/// </para>
/// <para>
/// <b>The tail.</b> <c>ReachedEnd</c> means the player handed over its last
/// samples, not that the radio has sent them: the encoder, the network and
/// the radio's own buffer lie between. So the unkey waits
/// <see cref="ReferenceEndTail"/> after the end is seen. The evening bench of
/// 2026-11-04 found the end beep whole in every take, the take running about
/// 0.55 s past the reference's end.
/// </para>
/// <para>
/// <b>Silence from the end until the radio receives (round four, part 6;
/// Noel 2026-11-05 01:37 and 01:38).</b> Until then the microphone came back
/// on the encoder at the end, so it went out for the tail, the poll and, under
/// a hardware push to talk, for as long as the operator held it. Now the
/// player holds silence by itself the moment the recording runs out
/// (<c>TxFilePlayer.StartHoldingSilenceAtEnd</c>), the tail runs over that
/// silence, and the microphone and the transmit input come back only when the
/// radio reports receive (<c>FlexBase.TxReferenceEndSilenceUntilReceive</c>),
/// whoever held the key. A hardware line held as it ends is told to let go;
/// that sentence is the only thing the holder changes about the silence.
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
    /// True when the operator must be told to let go of a push to talk: a
    /// hardware line keys the radio (the PTT source watch, held while the
    /// reference is armed, makes that reading honest) and something is still
    /// transmitting. With nothing transmitting, the last source the radio
    /// reported is history, not a held key.
    /// </summary>
    internal static bool PushToTalkStillHeld(ReferenceEndHolder holder, bool hardwareLine) =>
        hardwareLine && holder != ReferenceEndHolder.Nobody;

    /// <summary>
    /// What is said when the reference reaches its end, at the operator's
    /// verbosity. Ruled by Noel 2026-11-05 01:39 to 01:41, both forms. A
    /// hardware push to talk still held adds the release; nothing else does,
    /// because "release your push to talk" would be false for any other holder.
    /// </summary>
    internal static string ReferenceFinishedSentence(ReferenceEndHolder holder, bool hardwareLine, VerbosityLevel level) =>
        PushToTalkStillHeld(holder, hardwareLine)
            ? Lexicon.Get("audio.reference.finished_release_ptt", level)
            : Lexicon.Get("audio.reference.finished", level);

    /// <summary>
    /// The lowest verbosity the finished sentence is spoken at. Asking the
    /// operator to let go of a keyed radio is said at every verbosity; the
    /// plain end is said as it always was, from terse up.
    /// </summary>
    internal static VerbosityLevel ReferenceFinishedLevel(ReferenceEndHolder holder, bool hardwareLine) =>
        PushToTalkStillHeld(holder, hardwareLine) ? VerbosityLevel.Critical : VerbosityLevel.Terse;

    /// <summary>
    /// Stands in for <see cref="FlexBase.TxReferenceEndSilenceUntilReceive"/>
    /// in a test, which cannot build a connected radio here. Production never
    /// sets it.
    /// </summary>
    internal static Action<FlexBase, Action<ReferenceSilenceEnd>>? ReferenceSilenceWaitForTests;

    /// <summary>
    /// Let go of the app's own key at a reference's end, through MainWindow's
    /// key edges (<c>HandleReferenceEndUnkey</c>): the controller's unkey, and
    /// a held push to talk latched until the operator really lets go, so a
    /// screen reader's synthetic re-down cannot key the radio again (round
    /// five, 7a). Null with no main window wired (a test): the controller
    /// alone. True when it unkeyed.
    /// </summary>
    internal static Func<bool>? EndAppKeyForReference { get; set; }

    /// <summary>The PTT source watch, held from a successful arm until the silence after its pass ends, or a disarm.</summary>
    private IDisposable? _refPttWatch;

    /// <summary>Which silence the window is waiting on, so an older one ending cannot act for a newer pass.</summary>
    private int _refSilenceToken;

    /// <summary>
    /// The reference has played to its end. Say so at the operator's
    /// verbosity, stand the arm down, and leave the microphone and the transmit
    /// input where they are until the radio receives: the player is already
    /// holding silence. Ending the transmission itself, after the tail, is the
    /// caller's (<see cref="EndTransmissionAfterReferenceTail"/>). Returns the
    /// sentence spoken.
    /// </summary>
    internal string FinishReferencePass(FlexBase rig, ReferenceEndHolder holder, bool hardwareLine)
    {
        _refPassRunning = false;
        EarconPlayer.ConfirmTone();
        string finished = ReferenceFinishedSentence(holder, hardwareLine, ScreenReaderOutput.CurrentVerbosity);
        ScreenReaderOutput.Speak(finished, Radios.Speech.SpeechIntent.Queue,
            ReferenceFinishedLevel(holder, hardwareLine),
            subject: Radios.Speech.SpeechSubject.ReferenceAudio);
        SetReferenceArmSilently(false);
        UpdateReferenceStatus();
        BeginReferenceEndSilence(rig);
        return finished;
    }

    /// <summary>
    /// Wait for the radio to receive, then put the transmit input back and let
    /// the PTT source watch go. The microphone itself is handed back by the
    /// radio's own wait, before this runs.
    /// </summary>
    private void BeginReferenceEndSilence(FlexBase rig)
    {
        int token = ++_refSilenceToken;
        void Ended(ReferenceSilenceEnd how)
        {
            void OnUi() => ReferenceEndSilenceEnded(rig, token, how);
            var ui = Dispatcher;
            if (ui == null || ui.CheckAccess()) OnUi();
            else ui.BeginInvoke(new Action(OnUi));
        }
        if (ReferenceSilenceWaitForTests is { } wait) wait(rig, Ended);
        else rig.TxReferenceEndSilenceUntilReceive(Ended);
    }

    /// <summary>
    /// The silence after a pass has ended. Anything still to undo is undone
    /// only if this window still has this radio and nothing since has done it
    /// already: a disarm, a close or a teardown restores the input itself.
    /// </summary>
    private void ReferenceEndSilenceEnded(FlexBase rig, int token, ReferenceSilenceEnd how)
    {
        if (token != _refSilenceToken || !ReferenceEquals(rig, _rig)) return;
        ReleaseReferencePttWatch();
        if (how == ReferenceSilenceEnd.AlreadyReleased) return;
        // Not while transmitting: back on the Mic input, the radio would put
        // the hand microphone straight on the air. Only now is it receiving.
        if (RestoreInjectionPath(rig, out string pathNote) && !string.IsNullOrEmpty(pathNote))
            ScreenReaderOutput.Speak(pathNote, Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                subject: Radios.Speech.SpeechSubject.ReferenceAudio);
        UpdateReferenceStatus();
    }

    /// <summary>Let the PTT source watch go, if this window holds one. Safe to call twice.</summary>
    private void ReleaseReferencePttWatch()
    {
        var watch = _refPttWatch;
        _refPttWatch = null;
        watch?.Dispose();
    }

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
                // And through the key edges, which latch a hold the operator
                // may still be pressing (round five, 7a).
                if (EndAppKeyForReference is { } unkey) unkey();
                else PttControllerSource?.Invoke()?.EscapeUnlock();
                return ReferenceEndHolder.App;
            default:
                return ReferenceEndHolder.Nobody;
        }
    }
}
