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

    /// <summary>
    /// The window is closing: let the reference go. Called from the
    /// <c>Closed</c> handler in place of <see cref="DisarmReference"/>.
    /// </summary>
    /// <remarks>
    /// The window first looks once more for an end its poll has not seen yet,
    /// exactly as its poll would (<see cref="SyncReferenceUi"/>): the finished
    /// sentence, and the app's own key let go after the tail. Without that a
    /// close inside the last half second of a pass would leave the lock keyed
    /// over silence with nothing on screen to say so.
    /// </remarks>
    private void ReleaseReferenceAtClose()
    {
        var rig = _rig;
        if (rig != null && _refPassRunning) SyncReferenceUi();
        ReleaseReferenceLeavingWindow(rig);
    }

    /// <summary>
    /// The window is closing, or is switching away from <paramref name="rig"/>
    /// to another radio: let the reference go (round five, 7b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-11-06 03:12 (C3):</b> <i>"if you ever did, silence
    /// is fine."</i> Until round five a close called <c>TxFileStop</c>, so with a
    /// hand mic still keying the radio the desk microphone went on the air (the
    /// transmit input is still this computer's). Now a reference past its end
    /// keeps its silence and is handed to the radio's own receive wait, which
    /// is pinned to the connection and ends it at receive, at a lost or
    /// replaced connection, or when a new pass starts.
    /// </para>
    /// <para>
    /// <b>Only the held end silence survives.</b> A reference still playing, an
    /// armed reference that has not started, and a silence already let go all
    /// disarm exactly as before. The held silence survives whoever holds the
    /// key, not only under a hand mic: round four's rule is silence until the
    /// radio receives, whoever holds the key, and the rule that the silence must
    /// not outlive the window, which a close used to obey, was overruled.
    /// </para>
    /// <para>
    /// A radio teardown (<c>EndCheckBeforeTeardown</c>) and a radio leaving with
    /// none arriving still disarm at once: the connection itself is ending, and
    /// the disconnect's VOX release would end the silence a moment later anyway.
    /// </para>
    /// </remarks>
    private void ReleaseReferenceLeavingWindow(FlexBase? rig)
    {
        if (rig != null && rig.TxReferenceEndSilenceHeld) HandReferenceEndSilenceToRadio(rig);
        else DisarmReference(speak: false, rig: rig);
    }

    /// <summary>
    /// The window is switching straight from <paramref name="oldRig"/> to
    /// another radio (<c>SetRig</c>, after <c>_rig</c> has moved): let the
    /// departing radio's reference go (round five repair, 7h-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>First, an end the poll has not seen yet is finished</b>, for the
    /// departing radio, exactly as the poll would: the finished sentence, and
    /// the app's own key let go after the tail. Without that, a switch inside a
    /// pass's last half second left the departing radio keyed over silence, the
    /// lock's timeout the only thing to end it. A tail planned before the switch
    /// ends that key too, because the app's key no longer asks whether the
    /// window still has the radio (<see cref="EndTransmissionForReference"/>).
    /// </para>
    /// <para>
    /// <b>It cannot end the arriving radio's key.</b> The app holds a radio's
    /// reference only while the controller keys THAT radio, and the tail's plan
    /// is bound to the controller, the radio's connection and its keying count.
    /// </para>
    /// <para>
    /// Then the silence, if held, goes to the departing radio's own receive
    /// wait, as at a close; anything else disarms. The close does not come
    /// here: its poll-like look is <see cref="ReleaseReferenceAtClose"/>'s.
    /// </para>
    /// </remarks>
    private void ReleaseReferenceAtSwitch(FlexBase oldRig)
    {
        FinishPassIfItEnded(oldRig);
        ReleaseReferenceLeavingWindow(oldRig);
    }

    /// <summary>
    /// Everything a disarm does except ending the silence, which goes to the
    /// radio's own wait instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The window's own wait, if it asked for one, is made inert</b> (the
    /// token moves), and a fresh radio-side wait is asked for: the window may
    /// be closing before its poll ever saw the end, and then nothing else would
    /// end the silence. A newer wait makes any older one stand aside (the
    /// generation), so asking twice is harmless.
    /// </para>
    /// <para>
    /// <b>The transmit input</b> goes back through the radio's restore owner,
    /// which writes only while the radio reports receive
    /// (<c>RestoreTransmitInputAfterInjection</c>), so it cannot put the hand
    /// mic on the air either.
    /// </para>
    /// <para>
    /// <b>The PTT source watch ends at the close, not at receive.</b> Its one
    /// reader on this path is this window's finished sentence, which has been
    /// said by now or never will be; the radio's wait asks our key and the
    /// radio's interlock, never the source. And the watch keeps a FlexLib trace
    /// topic selected, which costs every space-less command a vendor exception
    /// path, so it is held no longer than it is read.
    /// </para>
    /// </remarks>
    private void HandReferenceEndSilenceToRadio(FlexBase rig)
    {
        _refPassRunning = false;
        _refSilenceToken++;
        ReleaseReferencePttWatch();
        SetReferenceArmSilently(false);
        RestoreInjectionPath(rig, out _);
        JJTrace.Tracing.TraceLine("Reference end: the window let go with the silence still held; the radio's own"
            + " receive wait ends it", System.Diagnostics.TraceLevel.Info);
        void Ended(ReferenceSilenceEnd how) =>
            JJTrace.Tracing.TraceLine("Reference end: the silence a closed or departed window handed over ended (" + how + ")",
                System.Diagnostics.TraceLevel.Info);
        if (ReferenceSilenceWaitForTests is { } wait) wait(rig, Ended);
        else rig.TxReferenceEndSilenceUntilReceive(Ended);
        UpdateReferenceStatus();
    }

    /// <summary>Let the PTT source watch go, if this window holds one. Safe to call twice.</summary>
    private void ReleaseReferencePttWatch()
    {
        var watch = _refPttWatch;
        _refPttWatch = null;
        watch?.Dispose();
    }

    /// <summary>
    /// Who holds the transmission on <paramref name="rig"/>. The app holds it
    /// only when the controller is transmitting AND keys this radio (round five
    /// repair, 7h-2): the controller keys whichever radio the main window has,
    /// so after a window switches between radios, a transmission on the other
    /// one is not this radio's reference's to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>While a check runs, the only key a reference's end may end is the
    /// check's own</b> (part 7j). The check holds it only while its own key is
    /// up (<c>AudioCheckSession.OwnKeyUp</c>), not merely while its one-second
    /// watcher still reads it keyed. Until 7j the holder was the sampled phase
    /// alone, so an operator who unlocked the check's key and locked again
    /// before the 500 ms end poll had the new key planned as the check's, and
    /// the tail's stop ended it (Codex, round five repair 2, B4).
    /// </para>
    /// <para>
    /// <b>Nor is a key the operator makes while a check runs the app's to end
    /// for the reference.</b> The window refuses its own push to talk and lock
    /// while a check runs, so a key then is one made deliberately elsewhere
    /// (the system-wide lock), against the check. Asked as "the app's key",
    /// it would be ended once the watcher had seen the check's own key go,
    /// one tick later: the same transmission, ended or kept by the watcher's
    /// timing. It is left as the radio's (<see cref="ReferenceEndHolder.Elsewhere"/>),
    /// as a key begun inside the tail already is (7i-1); the operator lets it
    /// go, and the silence holds until the radio receives.
    /// </para>
    /// </remarks>
    private ReferenceEndHolder ReferenceHolderNow(FlexBase rig)
    {
        var session = _session;
        if (session?.Active == true)
            return WhoHoldsTheReference(session.OwnKeyUp, appKeyed: false, rig.Transmit);
        var ptt = PttControllerSource?.Invoke();
        return WhoHoldsTheReference(false, ptt?.IsTransmitting == true && ptt.KeysRadio(rig), rig.Transmit);
    }

    /// <summary>
    /// Which of the app's transmissions a reference rode, as its tail planned
    /// it (round five, 7c; round five repair, 7h-1): the controller, the
    /// count of keyings on that controller, and the radio's connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count alone is not an identity.</b> <see cref="PttSafetyController.Keyings"/>
    /// starts at zero on every controller, and the main window builds a new
    /// controller at every power-on (a reconnect, a re-raised power event, an
    /// operator change) on the same radio. So a tail pending across a
    /// replacement saw 1 against 1 and quietly ended the NEW controller's
    /// first transmission, its "Receiving" with it (Codex, round five B1).
    /// </para>
    /// <para>
    /// <b>The controller is compared by instance, and the connection by its
    /// binding</b> (<see cref="FlexBase.CurrentConnectionBinding"/>, a new
    /// object for every transport attempt), so a reconnect on the same radio
    /// that somehow kept its controller ends the plan too. A tail whose plan
    /// is no longer current does nothing: it ends nobody's key.
    /// </para>
    /// <para>
    /// <b>A close does not end the plan</b> (round five, 7b): the controller,
    /// its count and the connection all survive a window closing, so the tail
    /// still lets go of the app's own key after a close.
    /// </para>
    /// <para>
    /// <b>A check holds one for its own key too</b> (round five repair 2,
    /// 7i-2): its watcher asks it, so a key the operator made between two of
    /// its ticks is not taken for the check's.
    /// </para>
    /// </remarks>
    internal sealed record ReferenceKeying(PttSafetyController? Controller, int Keyings, RadioConnectionBinding? Connection)
    {
        /// <summary>The transmission on <paramref name="rig"/> as it stands now.</summary>
        internal static ReferenceKeying Now(FlexBase rig, PttSafetyController? ptt) =>
            new(ptt, ptt?.Keyings ?? 0, rig.CurrentConnectionBinding);

        /// <summary>
        /// True when <paramref name="ptt"/> is still the transmission this plan
        /// was made for: the same controller, with no keying since, on the same
        /// connection. Whether that controller keys this radio at all is the
        /// holder's question, asked again just before this one
        /// (<see cref="ReferenceHolderNow"/>), and asked only there.
        /// </summary>
        internal bool StillCurrent(FlexBase rig, PttSafetyController? ptt) =>
            ptt != null && ReferenceEquals(ptt, Controller)
            && ptt.Keyings == Keyings && ReferenceEquals(rig.CurrentConnectionBinding, Connection);
    }

    /// <summary>After the tail, end the transmission the reference rode, if it is still held the same way.</summary>
    private async void EndTransmissionAfterReferenceTail(FlexBase rig, ReferenceEndHolder holder)
    {
        object? session = _session;
        // Which keying the reference rode (round five, 7c; repair 7h-1): an
        // operator who lets go and keys again inside the tail has started a
        // transmission of their own, and so has a controller built since; it
        // is not the reference's to end, or to end quietly. The app's key is
        // planned here, on MainWindow's controller. A check's is not: its plan
        // is the session's own key, recorded the moment it keyed and asked
        // when the tail comes due (part 7j), never a fresh reading taken now.
        var keying = holder == ReferenceEndHolder.App ? ReferenceKeying.Now(rig, PttControllerSource?.Invoke()) : null;
        try { await Task.Delay(ReferenceEndTail); }
        catch (Exception) { return; }
        EndTransmissionForReference(rig, holder, session, keying);
    }

    private sealed partial class AudioCheckSession
    {
        /// <summary>
        /// The session's bookkeeping reads keyed: its watcher has not yet seen
        /// the check's key end. Not the question "is the check's key up";
        /// that is <see cref="OwnKeyUp"/>.
        /// </summary>
        internal bool Keyed => Active && _phase == Phase.Keyed;

        /// <summary>
        /// The check's own key is up and the session knows it: keyed, and the
        /// key on the air is the one it made (<see cref="OwnKeyHeld"/>). The
        /// reference's holder asks this (part 7j).
        /// </summary>
        internal bool OwnKeyUp => Keyed && OwnKeyHeld;

        /// <summary>The controller this check keyed through, and the one its stop unkeys.</summary>
        internal PttSafetyController Controller => _ptt;
    }

    /// <summary>
    /// End the transmission a finished reference rode, by its holder's own
    /// route, rechecked now: for a check, the same window, radio and session,
    /// and the check's own key still up; for the app's key, held the same way
    /// and the same transmission (<see cref="ReferenceKeying"/>). Returns what
    /// it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The app's key does not ask whether this window still has the
    /// radio</b> (round five repair, 7h-2). A window that switches straight to
    /// another radio leaves the departing radio's key with the main window's
    /// controller. Until 7h-2 an already planned tail returned here, so
    /// nothing but the lock's timeout ended a transmission carrying silence.
    /// The plan asks the stronger question instead (<see cref="ReferenceKeying"/>):
    /// the same controller, keying this radio, with no keying since, on the
    /// same connection. A check still asks for this window's radio and
    /// session; a switch has ended that session already.
    /// </para>
    /// <para>
    /// <b>A check is bound to its transmission too</b> (round five repair 2,
    /// 7i-1). Until then a check's tail asked whether the session still read
    /// Keyed, and that is bookkeeping its one-second watcher samples. So an
    /// operator who let go of the check's lock and locked again inside the
    /// 300 ms tail had the new transmission ended by the old tail's stop
    /// (Codex, round five repair B2).
    /// </para>
    /// <para>
    /// <b>Since part 7j a check asks its session, the one owner of "is the
    /// check's key up"</b> (<c>AudioCheckSession.StopOwnKey</c>): the key it
    /// made the moment it keyed, still up, on the same controller and
    /// connection. That is the plan, recorded once, not a second reading taken
    /// at the poll; so a key made before the poll (B4) and a key made inside
    /// the tail (B2) are both the operator's. If the check's key has ended,
    /// the session is told so and nothing is unkeyed. The same call the Stop
    /// button makes while the check's key is up.
    /// </para>
    /// <para>With no plan given (a test), the app's plan is made now.</para>
    /// </remarks>
    internal ReferenceEndHolder EndTransmissionForReference(FlexBase rig, ReferenceEndHolder planned, object? plannedSession,
        ReferenceKeying? plannedKeying = null)
    {
        if (planned == ReferenceEndHolder.Check)
            return ReferenceEquals(rig, _rig) && _session != null && ReferenceEquals(_session, plannedSession)
                && _session.StopOwnKey()
                ? ReferenceEndHolder.Check : ReferenceEndHolder.Nobody;
        var now = ReferenceHolderNow(rig);
        if (now != planned) return ReferenceEndHolder.Nobody;
        // Is the transmission on the air still the one the reference rode? A
        // transmission begun since the end is the operator's own (7c), and so
        // is one on a controller or a connection built since (7h-1). Asked of
        // MainWindow's controller, the one its unkey acts on.
        var route = PttControllerSource?.Invoke();
        bool stillTheReferences = (plannedKeying ?? ReferenceKeying.Now(rig, route)).StillCurrent(rig, route);
        switch (now)
        {
            case ReferenceEndHolder.App:
                if (!stillTheReferences) return ReferenceEndHolder.Nobody;
                // MainWindow's unkey acts on its own controller, which is the one
                // just checked: both are the same field (_pttController).
                // Through the controller, never the radio's transmit flag: that
                // would leave the controller believing it still holds the key.
                // And through the key edges, which latch a hold the operator
                // may still be pressing (round five, 7a). With no "Receiving":
                // the finished sentence is the only thing said (7c).
                if (EndAppKeyForReference is { } unkey) unkey();
                else route?.EndAfterReference();
                return ReferenceEndHolder.App;
            default:
                return ReferenceEndHolder.Nobody;
        }
    }
}
