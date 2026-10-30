using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using JJTrace;
using Radios;
using Radios.Speech;

namespace JJFlexWpf;

/// <summary>
/// The voice half of a message key: load the slot's recording, send it in
/// place of the microphone, say what happened (Sprint 48 Track A, #151). And,
/// since #835, the one host for every send to the air: a voice message, a
/// computer recording (Tape Deck phase 2), or the radio's slice recording
/// under the VOX send box.
/// </summary>
/// <remarks>
/// <para>
/// This is the dispatcher-side shell around <see cref="VoiceMessageSend"/>,
/// which holds the state machine and is tested without a radio. What lives
/// here is everything that needs the UI assembly: the file loader
/// (<see cref="TxAudioFile"/>, NAudio), the recordings folder, the lexicon
/// sentences, the earcons, and the timer that drives the send.
/// </para>
/// <para>
/// <b>One send at a time, process-wide.</b> There is one radio and one
/// transmitter, and the message keys can arrive from more than one key
/// table — the main window's and the WinForms log form's each have a
/// <c>KeyCommands</c>. So the in-flight send is static: a message key
/// pressed from the log form stops a send started from Home. Play from
/// computer pressed while a computer recording is going out pauses it, and
/// pressed again carries it on (#835); a Play press on any other send stops
/// it, as the same message key stops a message.
/// </para>
/// <para>
/// <b>The VOX send box (#835).</b> With the box ticked and VOX on, every send
/// takes the same road (<see cref="SendRoute.Box"/>): pause VOX through the
/// radio's one VOX owner and wait for the radio to confirm it off; if VOX is
/// still holding the radio up on the operator's last words, let it drop;
/// then key, send the whole recording, unkey, and give the pause back, which
/// turns VOX on again only after the radio reports receive and the path has
/// drained (Noel, 2026-10-27 15:29). The short self-key the radio gives when
/// VOX is switched back on (#819) is accepted and not engineered around.
/// </para>
/// <para>
/// <b>How long a send may key the radio (#835, round 2).</b> A send longer
/// than three minutes asks first, before anything keys (Noel, 2026-10-27
/// 20:48). At ten minutes of recording sent, the send PAUSES: it unkeys the
/// radio if it keyed it, gives the microphone back, and holds its place, so
/// the operator can identify; Play carries on from there and Escape ends it
/// (20:50). Play pauses a running recording send and carries it on the same
/// way (20:51). One send is a <see cref="SendSession"/> across pieces, each
/// piece one <see cref="VoiceMessageSend"/> loaded from the held place
/// (<see cref="TxAudioFile.TryLoadPieceInto"/>), so a recording of any length
/// goes out whole, and none is shortened.
/// </para>
/// <para>
/// <b>The refusals are the radio's own words.</b> The path check is
/// <see cref="FlexBase.TxTonePathTrouble"/>, the single rig-level answer to
/// "can this computer's audio reach the transmitter", shared with the test
/// tone and the reference recording; the licence lockout is the same check
/// PTT applies. This surface refuses out loud in those words rather than
/// growing a second opinion about either, the way <c>Ctrl+J, G</c> already
/// does for the tone.
/// </para>
/// </remarks>
internal static class VoiceMessageSender
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    private static VoiceMessageSend? _send;
    private static DispatcherTimer? _timer;
    private static PttSafetyController? _watchedBy;
    /// <summary>The controller whose lock timeout stands still while this send rides it (#835, 2026-10-30 00:00).</summary>
    private static PttSafetyController? _riddenBy;

    private enum SendKind { VoiceMessage, ComputerRecording, SliceRecording }

    /// <summary>What a send is, beyond its state machine: what to call it, and what it holds.</summary>
    private sealed class SendInfo
    {
        public SendKind Kind;
        /// <summary>A voice message's slot label.</summary>
        public string Label = "";
        /// <summary>A recording's phrase for the sentences, "the computer recording X".</summary>
        public string What = "";
        /// <summary>The box's VOX pause, given back when the send has settled.</summary>
        public FlexBase.LocalPlaybackVoxPause? VoxPause;
        public SliceSendRig? Slice;
        /// <summary>The box send's cut owner (the transmit kill switch's arm), disposed when it settles.</summary>
        public BoxSendOwnedRig? Owned;
        /// <summary>The radio this send is for; a paused send ends if it disconnects.</summary>
        public FlexBase? Rig;
        /// <summary>The file the pieces are loaded from; null for the radio's slice recording.</summary>
        public string? Path;
        /// <summary>The send across its pieces and pauses (#835).</summary>
        public SendSession Session = new(0);
        /// <summary>The piece about to start carries on after a pause, so it says so rather than "Sending".</summary>
        public bool Resuming;
        /// <summary>What the running piece holds on the radio, given back when it settles.</summary>
        public PieceHolds? Holds;
        /// <summary>Push to talk's first sentence already said this send stopped, so Settle says nothing.</summary>
        public bool TakeoverSpoken;
    }

    /// <summary>
    /// What a send holds on the radio for exactly the life of one piece, from
    /// before it keys until it settles (#835, fix round 2026-10-28). A paused
    /// send holds none of it. Disposing twice is harmless.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transmit monitor</b> (<see cref="FlexBase.HoldMonitorForSend"/>):
    /// every send is heard through the radio's monitor, switched on silently if
    /// it was off and put back afterwards, at the operator's own monitor level
    /// (Noel, 2026-10-28 16:36, 16:47, 16:48). It replaces the local copy a box
    /// send used to play on this computer, which was far louder than the air
    /// and doubled what the monitor already gave. Taken before the key so the
    /// first syllable is heard; the monitor sounds only while transmitting.
    /// </para>
    /// <para>
    /// <b>The radio's PTT source</b> read as the radio sends it
    /// (<see cref="FlexBase.WatchPttSource"/>), so a hardware PTT pressed under
    /// the send's own key is seen: the radio reports <c>RCA,SW</c>, which
    /// FlexLib cannot parse.
    /// </para>
    /// </remarks>
    private sealed class PieceHolds : IDisposable
    {
        private IDisposable? _pttWatch;
        private IDisposable? _monitor;

        private PieceHolds(IDisposable? pttWatch, IDisposable? monitor) { _pttWatch = pttWatch; _monitor = monitor; }

        public static PieceHolds Take(FlexBase rig, string who)
        {
            IDisposable? watch = null, monitor = null;
            try { watch = rig.WatchPttSource(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("VoiceMessageSender: the PTT source watch could not start: " + ex.Message,
                    TraceLevel.Warning);
            }
            try { monitor = rig.HoldMonitorForSend(who); }
            catch (Exception ex)
            {
                // Hearing the send is a courtesy; it must never stop the send.
                Tracing.TraceLine("VoiceMessageSender: the transmit monitor could not be held: " + ex.Message,
                    TraceLevel.Warning);
            }
            return new PieceHolds(watch, monitor);
        }

        public void Dispose()
        {
            System.Threading.Interlocked.Exchange(ref _monitor, null)?.Dispose();
            System.Threading.Interlocked.Exchange(ref _pttWatch, null)?.Dispose();
        }

        /// <summary>
        /// The send was taken over by the operator's own key (#835, fix round
        /// 2026-10-30; Noel, 2026-10-29 23:57): the PTT source watch ends now,
        /// and the monitor stays as the send left it until the operator
        /// unkeys (<see cref="FlexBase.ReleaseMonitorHoldAtUnkey"/>).
        /// </summary>
        public void ReleaseAtUnkey(FlexBase rig, string who)
        {
            System.Threading.Interlocked.Exchange(ref _pttWatch, null)?.Dispose();
            IDisposable? monitor = System.Threading.Interlocked.Exchange(ref _monitor, null);
            try { rig.ReleaseMonitorHoldAtUnkey(monitor, who); }
            catch (Exception ex)
            {
                Tracing.TraceLine("VoiceMessageSender: the monitor could not wait for the unkey: " + ex.Message,
                    TraceLevel.Warning);
                monitor?.Dispose();
            }
        }
    }

    /// <summary>
    /// Give back what a piece held. Safe when it held nothing. A send the
    /// operator took over keeps the monitor until they unkey
    /// (<paramref name="takenOver"/>; Noel, 2026-10-29 23:57).
    /// </summary>
    private static void ReleaseHolds(SendInfo? info, bool takenOver = false)
    {
        if (info == null) return;
        PieceHolds? holds = info.Holds;
        info.Holds = null;
        if (holds == null) return;
        if (takenOver && info.Rig != null) holds.ReleaseAtUnkey(info.Rig, Name(info));
        else holds.Dispose();
    }

    private static SendInfo? _current;

    /// <summary>
    /// A send paused at a held place (#835): nothing keyed, nothing engaged,
    /// waiting for Play, Escape, or the radio disconnecting.
    /// </summary>
    private static SendInfo? _paused;

    /// <summary>
    /// True while the long-send warning is on screen. A send door pressed in
    /// that moment (the JJ key works from other programs) is ignored rather
    /// than starting a second send under the question.
    /// </summary>
    private static bool _confirming;

    /// <summary>A box send waiting for the radio to confirm VOX off, or for VOX's tail to drop.</summary>
    private static SendInfo? _starting;

    /// <summary>
    /// True while a send is going out, a box send is getting ready to, or a
    /// send is paused (#835): Escape ends any of them.
    /// </summary>
    public static bool InFlight => _starting != null || (_send != null && !_send.Finished) || _paused != null;

    /// <summary>True while a send is paused at a held place, waiting for Play.</summary>
    public static bool Paused => _paused != null;

    /// <summary>
    /// True while the send going out, getting ready, or paused, was started by
    /// a Play door: a computer recording or a slice recording.
    /// </summary>
    public static bool InFlightFromPlay =>
        InFlight && Active?.Kind is SendKind.ComputerRecording or SendKind.SliceRecording;

    /// <summary>
    /// True while the send in hand (going out, getting ready, or paused) is a
    /// computer recording: the one Play from computer and Enter in Recorded
    /// QSOs pause and carry on (#835, fix round 2026-10-29).
    /// </summary>
    public static bool ComputerRecordingInHand => InFlight && Active?.Kind == SendKind.ComputerRecording;

    /// <summary>The send in hand, whichever state it is in.</summary>
    private static SendInfo? Active => _starting ?? (_send != null && !_send.Finished ? _current : null) ?? _paused;

    private static SendSessionState? StateOf(SendInfo? info) =>
        info == null ? null : ReferenceEquals(info, _paused) ? SendSessionState.Paused : SendSessionState.Running;

    /// <summary>The label of the message going out, or empty.</summary>
    public static string InFlightLabel => _send != null && !_send.Finished ? _send.Label : "";

    // ── Routing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The operator is keying the radio themselves: push to talk or the lock
    /// through the controller, a hardware PTT line, or the tune carrier. A
    /// radio held up only by VOX is NOT this (Noel, 2026-10-27 15:28). Also
    /// asked by a paused speaker playback carrying on (<see cref="LocalPlaybackVox.CarryOnAsync"/>).
    /// </summary>
    internal static bool OperatorHolds(FlexBase rig, PttSafetyController? ptt)
    {
        if (ptt?.IsTransmitting == true) return true;
        try
        {
            if (rig.Transmit && rig.PttSourceIsHardware) return true;
            if (rig.TxTune) return true;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("VoiceMessageSender.OperatorHolds: " + ex.Message, TraceLevel.Warning);
        }
        return false;
    }

    /// <summary>Which way a send pressed now goes. Read live, once per press.</summary>
    internal static SendRoute RouteFor(FlexBase rig)
    {
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        bool voiceVoxOn = rig.CurrentVoxSettingIfOn() == FlexBase.VoxSetting.SimpleVox;
        return VoxSendBox.Choose(VoxSendBox.Ticked, voiceVoxOn, rig.Transmit, OperatorHolds(rig, ptt));
    }

    // ── The doors ───────────────────────────────────────────────────────

    /// <summary>
    /// Send the slot's recording. The same slot pressed while it is going out
    /// pauses it, and pressed again carries it on; a different slot stops it
    /// and starts the new one; Escape stops it.
    /// </summary>
    public static void Send(FlexBase rig, CWMessageItem item)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (item == null) throw new ArgumentNullException(nameof(item));
        string label = item.Label ?? "";
        if (_confirming)
        {
            Tracing.TraceLine("VoiceMessageSender: \"" + label + "\" ignored: the long-send question is open",
                TraceLevel.Info);
            return;
        }

        // A message's own key PAUSES it and carries it on, the same as Play
        // does for a recording; Escape stops it (Noel, 2026-10-28 21:20, which
        // replaced #151's "the same key stops it"). Another message key still
        // stops it and starts its own. Carrying on does not ask the long-send
        // question again: the send was agreed once (21:25).
        SendInfo? active = Active;
        bool same = active != null && active.Kind == SendKind.VoiceMessage
                    && string.Equals(active.Label, label, StringComparison.Ordinal);
        switch (SendSession.Answer(StateOf(active), same, pressPauses: true, canHoldPlace: true))
        {
            case SendPress.Pause:
                PauseRunning();
                return;
            case SendPress.Resume:
                ResumePaused();
                return;
            case SendPress.Stop:
                Stop();
                if (same) return;
                break;
        }

        var info = new SendInfo { Kind = SendKind.VoiceMessage, Label = label, Rig = rig };

        // The licence-aware lockout PTT applies, applied here too: a key that
        // keys the radio must answer to the same rule as the key that keys it
        // by hand. Resolved per call — the controller is recreated on operator
        // switch and nulled on power-off.
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        if (ptt?.CanTransmitHereCheck != null && !ptt.CanTransmitHereCheck())
        {
            Refuse(info, Lexicon.Get("audio.ptt.blocked_by_license"));
            return;
        }

        // The hold after a reflected-power cut (#571): a voice message keys the
        // radio itself, or hands the recording to VOX, and either way it is RF
        // into the same fault. The controller speaks the refusal — the same
        // sentence a refused lock key gets — so nothing is said here.
        if (ptt != null && ptt.RefuseIfHeldAfterCut("a message key"))
        {
            Tracing.TraceLine("VoiceMessageSender: \"" + label + "\" not sent: keying is held after a reflected-power cut",
                TraceLevel.Info);
            return;
        }

        if (rig.TxToneEngaged)
        {
            Refuse(info, Lexicon.Get("audio.reference.tone_already_armed"));
            return;
        }

        string trouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(trouble))
        {
            Refuse(info, trouble);
            return;
        }

        string path = RecordingStore.PathForName(item.Audio);
        if (!File.Exists(path))
        {
            Refuse(info, Lexicon.Get("settings.cw.recording_missing", ("name", item.Audio)));
            return;
        }

        info.Path = path;
        SendRoute route = RouteFor(rig);
        if (!LengthAgreed(rig, info, route, out TxAudioFile.Loaded? loaded, out route)) return;
        if (loaded == null)
        {
            if (!TxAudioFile.TryLoadPieceInto(rig, path, 0, out loaded, out string loadTrouble) || loaded == null)
            {
                Refuse(info, loadTrouble);
                return;
            }
        }
        info.Session = new SendSession(loaded.LengthSeconds);
        StartVoiceMessagePiece(rig, info, loaded, route);
    }

    /// <summary>
    /// Start a voice message's piece: the first, or one carrying on after a
    /// pause. The VOX send box (#835): with it ticked and VOX on, the message keys
    /// the radio itself rather than letting VOX key it. Unticked, #151's rule
    /// stands exactly as it was: VOX keys it, otherwise the key keys it.
    /// </summary>
    private static void StartVoiceMessagePiece(FlexBase rig, SendInfo info, TxAudioFile.Loaded loaded, SendRoute route)
    {
        if (route == SendRoute.Box)
        {
            _ = BeginBoxAsync(rig, info, new FlexBaseVoiceMessageRig(rig), startsOnKey: false,
                joinInstead: () => BeginAndLaunch(rig, info, info.Label, loaded));
            return;
        }

        BeginAndLaunch(rig, info, info.Label, loaded);
    }

    /// <summary>
    /// Take the piece's holds, THEN begin the send, which may key the radio at
    /// once, then launch it. In that order so the transmit monitor's turn-on is
    /// queued to the radio ahead of the key and the first syllable is heard.
    /// </summary>
    private static void BeginAndLaunch(FlexBase rig, SendInfo info, string label, TxAudioFile.Loaded loaded)
    {
        PieceHolds holds = PieceHolds.Take(rig, Name(info));
        VoiceMessageSend send;
        try { send = VoiceMessageSend.Begin(new FlexBaseVoiceMessageRig(rig), label, Environment.TickCount64); }
        catch
        {
            holds.Dispose();
            throw;
        }
        Launch(rig, info, send, loaded.Path, loaded.Describe(), holds);
    }

    /// <summary>
    /// Send a computer recording to the air (#835, Tape Deck phase 2). The door
    /// decided the route: <see cref="SendRoute.JoinTransmission"/> joins a
    /// transmission the operator is holding and never touches the key (Noel,
    /// 2026-10-21 10:57: keyed, Play sends the file); <see cref="SendRoute.Box"/>
    /// keys the radio itself.
    /// </summary>
    public static void SendComputerRecording(FlexBase rig, string path, SendRoute route)
        => SendComputerRecording(rig, path, route, 0);

    /// <summary>
    /// A recording paused on the speakers, when a press now would send (#835;
    /// Noel, 2026-10-30 05:56: <i>"If you've paused and you turn send on, say
    /// you didn't realize you did, it should send"</i>). The route is chosen as
    /// a fresh press would choose it: the box ticked with VOX on, or the
    /// operator keyed. The speakers' playback ends without a word and the
    /// recording goes to the air from where it was paused, saying so as any
    /// send does. False, touching nothing, when the press would not send or
    /// there is nothing paused, so the caller carries on on the speakers.
    /// </summary>
    internal static bool SendPausedSpeakerPlayback(FlexBase rig, SendRoute route)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (route == SendRoute.AsBefore || _confirming || InFlight || !RecordingPlayer.IsPaused) return false;
        string path = RecordingPlayer.CurrentPath;
        // Only a recorded QSO, the thing Play from computer sends. The same
        // player plays Check your audio's takes, and a paused take is never
        // something to put on the air.
        if (!IsRecordedQso(path)) return false;
        double at = RecordingPlayer.PositionSeconds ?? 0;
        Tracing.TraceLine("VoiceMessageSender: the recording paused on the speakers at " + at.ToString("F1")
            + " s goes to the air instead, " + route + ": a press now sends", TraceLevel.Info);
        RecordingPlayer.StopWithoutSpeaking();
        SendComputerRecording(rig, path, route, at);
        return true;
    }

    /// <summary>True when the file is in the Recorded QSOs folder.</summary>
    internal static bool IsRecordedQso(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string folder = Path.GetFullPath(RecordedQsoCatalog.FolderPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try { return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    private static void SendComputerRecording(FlexBase rig, string path, SendRoute route, double fromSeconds)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (_confirming) return;
        if (InFlight) { Stop(); return; }

        // DRAFT PROSE (#835): awaiting Noel
        var info = new SendInfo
        {
            Kind = SendKind.ComputerRecording,
            What = Lexicon.Get("audio.send.what_computer", ("name", System.IO.Path.GetFileNameWithoutExtension(path))),
            Path = path,
            Rig = rig,
        };
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        if (!Admitted(rig, ptt, info, route)) return;

        if (rig.TxToneEngaged)
        {
            Refuse(info, Lexicon.Get("audio.reference.tone_already_armed"));
            return;
        }
        string trouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(trouble))
        {
            Refuse(info, trouble);
            return;
        }
        if (!LengthAgreed(rig, info, route, out TxAudioFile.Loaded? loaded, out route, fromSeconds)) return;
        if (route == SendRoute.AsBefore)
        {
            // The radio changed while the question was open: nobody is keying
            // it and the box does not apply. Play never keys on its own.
            Refuse(info, Lexicon.Get("audio.send.nothing_keys_now"));
            return;
        }
        if (loaded == null)
        {
            if (!TxAudioFile.TryLoadPieceInto(rig, path, fromSeconds, out loaded, out string loadTrouble) || loaded == null)
            {
                Refuse(info, loadTrouble);
                return;
            }
        }
        info.Session = new SendSession(loaded.LengthSeconds, fromSeconds);
        StartComputerPiece(rig, info, loaded, route);
    }

    /// <summary>A computer recording's piece: the first, or one carrying on after a pause.</summary>
    private static void StartComputerPiece(FlexBase rig, SendInfo info, TxAudioFile.Loaded loaded, SendRoute route)
    {
        if (route == SendRoute.Box)
        {
            _ = BeginBoxAsync(rig, info, new FlexBaseVoiceMessageRig(rig), startsOnKey: false,
                joinInstead: () => JoinWithFile(rig, info, loaded));
            return;
        }
        JoinWithFile(rig, info, loaded);
    }

    /// <summary>
    /// The long-send warning (#835; Noel, 2026-10-27 20:48: <i>"I would warn the
    /// user if it's longer than 180 seconds and if they [click] ok, tell them
    /// how to pause the transmission"</i>). Asked before anything keys, every
    /// time a send over three minutes is pressed, and never for a send that
    /// joins a transmission the operator is already holding. False when the
    /// operator declined, or when the send could not go ahead; either way it
    /// has said so, and nothing has keyed.
    /// </summary>
    /// <remarks>
    /// The length comes from the file's header when it says, so a declined
    /// send never touches the transmit player; a file that does not say is
    /// loaded first, and <paramref name="loaded"/> carries it out. After a
    /// "Send", the route is read again, because the radio may have changed
    /// while the question was open, and the licence and the hold after a
    /// reflected-power cut are asked again for the same reason.
    /// </remarks>
    private static bool LengthAgreed(FlexBase rig, SendInfo info, SendRoute route,
        out TxAudioFile.Loaded? loaded, out SendRoute routeAfter, double fromSeconds = 0)
    {
        loaded = null;
        routeAfter = route;
        string path = info.Path ?? "";
        if (!TxAudioFile.TryMeasureSeconds(path, out double length))
        {
            if (!TxAudioFile.TryLoadPieceInto(rig, path, fromSeconds, out loaded, out string loadTrouble) || loaded == null)
            {
                Refuse(info, loadTrouble);
                return false;
            }
            length = loaded.LengthSeconds;
        }
        // A send from a paused place is as long as what is left of it.
        length = Math.Max(0, length - fromSeconds);
        if (!VoiceMessageSend.NeedsLengthWarning(length, route)) return true;

        if (!ConfirmLongSend(info, length))
        {
            Tracing.TraceLine("VoiceMessageSender: " + Name(info) + " not sent: the operator declined the long-send warning",
                TraceLevel.Info);
            // DRAFT PROSE (#835): awaiting Noel
            ScreenReaderOutput.Speak(Lexicon.Get("audio.send.cancelled", ("what", Name(info))),
                SpeechIntent.Interrupt, VerbosityLevel.Terse, subject: SpeechSubject.MessageKey);
            return false;
        }
        if (InFlight)
        {
            // Something else started while the question was open. One send at a time.
            Tracing.TraceLine("VoiceMessageSender: " + Name(info) + " not sent: another send began during the question",
                TraceLevel.Info);
            return false;
        }
        routeAfter = RouteFor(rig);
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        if (!Admitted(rig, ptt, info, routeAfter == SendRoute.JoinTransmission ? routeAfter : SendRoute.Box))
            return false;
        return true;
    }

    /// <summary>
    /// The question itself, on the existing confirmation dialog: its body is
    /// one read-only text the operator can arrow through, Send is not the
    /// default button, and Escape and Cancel both decline.
    /// </summary>
    private static bool ConfirmLongSend(SendInfo info, double length)
    {
        bool message = info.Kind == SendKind.VoiceMessage;
        // DRAFT PROSE (#835): awaiting Noel
        var lines = new System.Collections.Generic.List<string>
        {
            Lexicon.Get(message ? "audio.send.long_pause_message" : "audio.send.long_pause_play"),
        };
        if (length > VoiceMessageSend.PauseAfterSeconds + VoiceMessageSend.PauseEndMarginSeconds)
            lines.Add(Lexicon.Get("audio.send.long_id", ("resume", ResumeWord(info))));
        _confirming = true;
        try
        {
            var dialog = new Dialogs.ConfirmActionDialog(
                Lexicon.Get("audio.send.long_title"),
                Lexicon.Get("audio.send.long_message", ("what", Name(info)),
                    ("length", RecordingStore.DescribeLength(length))),
                lines,
                question: Lexicon.Get("audio.send.long_question"),
                yesLabel: Lexicon.Get("audio.send.long_yes"),
                noLabel: Lexicon.Get("audio.send.long_no"));
            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            // A question that cannot be asked is a send that does not go.
            Tracing.TraceLine("VoiceMessageSender: the long-send warning could not be shown: " + ex.Message,
                TraceLevel.Warning);
            return false;
        }
        finally
        {
            _confirming = false;
        }
    }

    /// <summary>The key that carries a paused send on, in the operator's words.</summary>
    private static string ResumeWord(SendInfo info) =>
        Lexicon.Get(info.Kind == SendKind.VoiceMessage ? "audio.send.resume_message" : "audio.send.resume_play");

    /// <summary>
    /// Send the radio's own slice recording under the VOX send box (#835): key, then
    /// start the slice's playback, which the radio sends to the air because the
    /// radio is keyed. It is heard through the transmit monitor like every
    /// other send (2026-10-28 16:47), held for the piece by the send host and
    /// for the playback itself by the slice recorder.
    /// </summary>
    public static void SendSliceRecording(FlexBase rig, SliceRecorderTarget target)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (_confirming) return;
        if (InFlight) { Stop(); return; }

        string letter = rig.SliceRecorderLetter(target);
        // DRAFT PROSE (#835): awaiting Noel
        var info = new SendInfo
        {
            Kind = SendKind.SliceRecording,
            What = Lexicon.Get("audio.send.what_slice", ("letter", letter)),
            Rig = rig,
        };
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        if (!Admitted(rig, ptt, info, SendRoute.Box)) return;
        if (!rig.SliceHasRecording(target))
        {
            RefuseLine(Lexicon.Get("audio.recorder.nothing_to_play", ("letter", letter)), info);
            return;
        }

        var slice = new SliceSendRig(rig, target);
        info.Slice = slice;
        // The radio keeps at most two minutes (FlexBase.SliceRecordBufferSeconds,
        // and SliceRecordingSeconds clamps what the radio reports to it), so a
        // slice send never reaches the three-minute warning or the ten-minute
        // pause, and its playback cannot hold a place: Play stops it.
        double seconds = rig.SliceRecordingSeconds(target) ?? FlexBase.SliceRecordBufferSeconds;
        info.Session = new SendSession(seconds);
        _ = BeginBoxAsync(rig, info, slice, startsOnKey: true,
            joinInstead: () => _ = RecorderControls.StartSliceOnAirAsync(rig, target));
    }

    /// <summary>The licence lockout and the hold after a reflected-power cut, for a send that may key.</summary>
    private static bool Admitted(FlexBase rig, PttSafetyController? ptt, SendInfo info, SendRoute route)
    {
        if (ptt?.CanTransmitHereCheck != null && !ptt.CanTransmitHereCheck())
        {
            Refuse(info, Lexicon.Get("audio.ptt.blocked_by_license"));
            return false;
        }
        // Joining a transmission the operator already holds keys nothing; only
        // a send that keys answers to the hold (#571), which speaks for itself.
        if (route == SendRoute.Box && ptt != null && ptt.RefuseIfHeldAfterCut("a recording send"))
        {
            Tracing.TraceLine("VoiceMessageSender: " + info.What + " not sent: keying is held after a reflected-power cut",
                TraceLevel.Info);
            return false;
        }
        return true;
    }

    /// <summary>
    /// A computer recording joins a transmission the operator is holding. The
    /// recording replaces the microphone for its length, the microphone comes
    /// back at its end, and the key is never touched.
    /// </summary>
    private static void JoinWithFile(FlexBase rig, SendInfo info, TxAudioFile.Loaded loaded)
    {
        // Begin keys a receiving radio. The operator's press was read keyed,
        // but if the radio has not echoed it yet, Begin would key on its own
        // account and later unkey under the operator's thumb. So: refuse, and
        // let the press be made again a moment later. Only within the first
        // tenth of a second or so of a key-down.
        if (!rig.Transmit)
        {
            RefuseOrHold(info, Lexicon.Get("audio.send.not_yet_transmitting"));
            return;
        }
        BeginAndLaunch(rig, info, info.What, loaded);
    }

    // ── the VOX send box ───────────────────────────────────────────────────────

    /// <summary>
    /// Pause VOX, let VOX's own tail drop, then key and send (#835). Every road
    /// out of here before the send begins gives the pause back.
    /// </summary>
    private static async Task BeginBoxAsync(FlexBase rig, SendInfo info, IVoiceMessageRig sendRig,
        bool startsOnKey, Action joinInstead)
    {
        // This attempt's own holds, taken before anything can key: handed to
        // the piece if it launches, given back on every other road out.
        PieceHolds? holds = PieceHolds.Take(rig, Name(info));
        try
        {
            if (await BeginBoxCoreAsync(rig, info, sendRig, startsOnKey, joinInstead, holds)) holds = null;
        }
        finally
        {
            holds?.Dispose();
        }
    }

    /// <summary>The box start itself; true when the piece launched and took <paramref name="holds"/>.</summary>
    private static async Task<bool> BeginBoxCoreAsync(FlexBase rig, SendInfo info, IVoiceMessageRig sendRig,
        bool startsOnKey, Action joinInstead, PieceHolds holds)
    {
        _starting = info;
        // A kill from here on cancels this send's key, whatever route it takes
        // (fix round 1, Codex's follow-up 4). Escape invalidates _starting;
        // a kill moves this generation, and BoxSendOwnedRig.Own refuses.
        long disarmsAtPress = TransmitKillSwitch.DisarmGeneration;
        Tracing.TraceLine("VoiceMessageSender: box send of " + Name(info) + ": pausing VOX", TraceLevel.Info);
        FlexBase.LocalPlaybackVoxPause? pause;
        try { pause = await rig.PauseVoxForLocalPlaybackAsync(); }
        catch (Exception ex)
        {
            Tracing.TraceLine("VoiceMessageSender: VOX pause threw: " + ex.Message, TraceLevel.Warning);
            pause = null;
        }

        if (!ReferenceEquals(_starting, info))
        {
            // Stopped, or replaced by another press, while the radio answered.
            pause?.Dispose();
            return false;
        }
        if (pause == null)
        {
            _starting = null;
            RefuseOrHold(info, Lexicon.Get("audio.send.vox_unconfirmed"));
            return false;
        }

        // The tail of VOX's own hold on the operator's last words. It counts
        // as unkeyed for the box (15:28): with VOX now off it drops, and the
        // send keys from a quiet radio, so VOX cannot drop the carrier in the
        // recording's first pause.
        long until = Environment.TickCount64 + (long)VoxSendBox.VoxTailDropWait.TotalMilliseconds;
        while (rig.Transmit && Environment.TickCount64 < until && ReferenceEquals(_starting, info)
               && !OperatorHolds(rig, Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke()))
            await Task.Delay(50);
        if (!ReferenceEquals(_starting, info))
        {
            pause.Dispose();
            return false;
        }
        _starting = null;
        if (TransmitKillSwitch.DisarmGeneration != disarmsAtPress)
        {
            // A kill came while this send was getting ready: no key, and no
            // joining whatever is on the air either.
            Tracing.TraceLine("VoiceMessageSender: box send of " + Name(info) + " cancelled by a kill before it keyed",
                TraceLevel.Warning);
            pause.Dispose();
            return false;
        }

        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        if (OperatorHolds(rig, ptt) || rig.Transmit)
        {
            // The operator keyed while we waited, or something other than VOX
            // holds the radio up. Theirs, so the send joins it and never
            // touches the key — #151's already-transmitting rule.
            Tracing.TraceLine("VoiceMessageSender: box send of " + Name(info)
                + ": the radio is held by the operator or another source, so the send joins it", TraceLevel.Info);
            pause.Dispose();
            joinInstead();
            return false;
        }
        if (ptt != null && ptt.RefuseIfHeldAfterCut("a send"))
        {
            pause.Dispose();
            return false;
        }

        info.VoxPause = pause;
        var options = new KeyedSendOptions
        {
            // Any push to talk takes over (Noel, 15:26): the keyboard's and
            // the lock through the controller, which then owns the unkey, and
            // a hardware line, read from the radio's interlock source only
            // after the send's own software key has been seen, so the send's
            // own MOX can never read as the operator (fix round 1, Codex's
            // blocker 2). After a hardware takeover the send withdraws its
            // own software key; the line holds the carrier.
            TakenBy = BoxSendTakeover.For(rig,
                () => Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke()?.IsTransmitting == true).Check,
            StartSourceWhenKeyed = startsOnKey,
            SourceFailed = info.Slice == null ? null : () => info.Slice.Failed,
        };
        // The carrier this send keys is under the reflected-power CUT for its
        // whole life (fix round 1, Codex's blocker 1): armed on the transmit
        // kill switch before the key, keyed through it, disarmed in Settle.
        info.Owned = BoxSendOwnedRig.Own(rig, sendRig, "the box send of " + Name(info), disarmsAtPress);
        if (info.Owned == null)
        {
            // A kill came while this send was getting ready. Nothing keys.
            pause.Dispose();
            info.VoxPause = null;
            return false;
        }
        var send = VoiceMessageSend.BeginKeyed(info.Owned, Name(info), Environment.TickCount64, options);
        Launch(rig, info, send, info.Path ?? "", "box", holds);
        return true;
    }

    // ── Running a send ──────────────────────────────────────────────────

    private static string Name(SendInfo info) => info.Kind == SendKind.VoiceMessage ? info.Label : info.What;

    private static void Launch(FlexBase rig, SendInfo info, VoiceMessageSend send, string path, string describe,
        PieceHolds? holds = null)
    {
        // The piece's holds: handed over by a box start, taken here otherwise.
        // An earlier piece's are already given back by Settle; any left are a
        // defect, and go now rather than leak.
        ReleaseHolds(info);
        info.Holds = holds ?? PieceHolds.Take(rig, Name(info));
        info.TakeoverSpoken = false;
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        _send = send;
        _current = info;
        _paused = null;
        info.Session.Begin(send);
        // A box send's carrier is owned by the transmit kill switch, which
        // warns AND cuts (BoxSendOwnedRig); asking the controller to watch it
        // too would only say the reflected-power warning twice. An unboxed
        // keyed voice message keeps the controller's watch as before. NOTE:
        // that watch WARNS but cannot CUT — the controller judges its cut only
        // for a transmission it owns, and here it is Idle. That gap is
        // pre-existing for unboxed voice messages and is not this track's.
        if (_send.WeKeyed && !_send.Boxed && ptt != null)
        {
            ptt.BeginExternalTransmitWatch();
            _watchedBy = ptt;
        }
        // A recording riding the operator's transmission: under the lock, the
        // lock's timeout stands still while it goes out, and the send's own
        // ten-minute limit governs it (Noel, 2026-10-30 00:00). The ten-minute
        // pause ends the ride, so the lock's clock runs while the operator
        // identifies, and the 15-minute hard kill is never touched.
        if (!_send.WeKeyed && !_send.ViaVox && !_send.Boxed && ptt != null)
        {
            VoiceMessageSend riding = _send;
            ptt.BeginRecordingSendRide(Name(info), () => ReferenceEquals(_send, riding) && !riding.Finished);
            _riddenBy = ptt;
        }

        Tracing.TraceLine("VoiceMessageSender: sending \"" + Name(info) + "\" from " + path
            + " at " + info.Session.HeldAtSeconds.ToString("F1") + " s of " + info.Session.TotalSeconds.ToString("F1")
            + " s, piece " + info.Session.Pieces + " (" + describe + "), "
            + (_send.Boxed ? "keying the radio with VOX paused (the box)"
               : _send.WeKeyed ? "keying the radio"
               : _send.ViaVox ? "letting VOX key the radio"
               : "radio already transmitting"),
            TraceLevel.Info);

        string length = RecordingStore.DescribeLength(info.Session.TotalSeconds);
        // DRAFT PROSE (#835): awaiting Noel (the recording sentences; the message one is #151's)
        string line = info.Resuming
            ? Lexicon.Get("audio.send.resuming", ("what", Name(info)),
                ("position", RecordingStore.DescribeLength(info.Session.HeldAtSeconds)),
                ("remaining", RecordingStore.DescribeLength(info.Session.RemainingSeconds)))
            : info.Kind == SendKind.VoiceMessage
                ? Lexicon.Get("settings.cw.voice_sending", ("label", info.Label), ("length", length))
                : Lexicon.Get("audio.send.sending", ("what", info.What), ("length", length));
        info.Resuming = false;
        ScreenReaderOutput.Speak(line, SpeechIntent.Interrupt, VerbosityLevel.Terse,
            subject: SpeechSubject.MessageKey);

        EnsureTimer();
    }

    /// <summary>
    /// Start the send's timer on this thread. Per dispatcher, not once: a
    /// DispatcherTimer belongs to the thread that made it, and a message key
    /// can arrive from the WinForms full log form, which FindLogEntry opens on
    /// an STA thread of its own. Starting a timer made on another thread
    /// throws, so a send started from a different dispatcher gets a timer of
    /// its own. It also runs while a send is paused, to end it if the radio
    /// disconnects.
    /// </summary>
    private static void EnsureTimer()
    {
        if (_timer == null || _timer.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            StopTimer();
            _timer = new DispatcherTimer { Interval = TickInterval };
            _timer.Tick += OnTick;
        }
        _timer.Start();
    }

    /// <summary>Stop the timer on the thread that owns it.</summary>
    private static void StopTimer()
    {
        DispatcherTimer? t = _timer;
        if (t == null) return;
        if (t.Dispatcher.CheckAccess()) t.Stop();
        else t.Dispatcher.BeginInvoke(new Action(t.Stop));
    }

    /// <summary>
    /// Stop the send going out, the box send getting ready, or the send that
    /// is paused (Escape, and every door that ends a send). Idempotent.
    /// </summary>
    public static void Stop()
    {
        SendInfo? starting = _starting;
        if (starting != null)
        {
            // The box start owns its VOX pause until it sees this and gives it back.
            _starting = null;
            starting.Session.Stop();
            Tracing.TraceLine("VoiceMessageSender: " + Name(starting) + " stopped before it keyed", TraceLevel.Info);
            Speak(starting, VoiceMessageOutcome.Stopped, trouble: false);
            return;
        }
        SendInfo? paused = _paused;
        if (paused != null)
        {
            // Nothing of a paused send is keyed or engaged: its VOX pause, its
            // cut owner, its transmit monitor and its PTT source watch were all
            // given back at the pause.
            _paused = null;
            paused.Session.Stop();
            StopTimer();
            Tracing.TraceLine("VoiceMessageSender: " + Name(paused) + " stopped while paused at "
                + paused.Session.HeldAtSeconds.ToString("F1") + " s", TraceLevel.Info);
            // DRAFT PROSE (#835): awaiting Noel
            ScreenReaderOutput.Speak(Lexicon.Get("audio.send.stopped_paused", ("what", Name(paused))),
                SpeechIntent.Interrupt, VerbosityLevel.Terse, subject: SpeechSubject.MessageKey);
            return;
        }
        VoiceMessageSend? send = _send;
        if (send == null || send.Finished) return;
        send.Stop();
        Settle();
    }

    /// <summary>
    /// Play from computer was pressed while a send is in hand (#835; Noel,
    /// 2026-10-27 20:51: <i>"you stop play and pause the same way"</i>). A
    /// computer recording going out pauses; a paused one carries on; any other
    /// send stops, as before. True when the press was answered here; false
    /// when no send is in hand and the door should do what it always did.
    /// </summary>
    internal static bool AnswerPlayFromComputer()
    {
        if (_confirming) return true;
        SendInfo? active = Active;
        bool same = active?.Kind == SendKind.ComputerRecording;
        switch (SendSession.Answer(StateOf(active), same, pressPauses: true, canHoldPlace: true))
        {
            case SendPress.Pause: PauseRunning(); return true;
            case SendPress.Resume: ResumePaused(); return true;
            case SendPress.Stop: Stop(); return true;
            default: return false;
        }
    }

    /// <summary>
    /// The operator pressed Play on the send going out: hold the place. A box
    /// send still waiting for VOX to confirm off has sent nothing, and pauses
    /// at the place it would have started from.
    /// </summary>
    private static void PauseRunning()
    {
        SendInfo? starting = _starting;
        if (starting != null)
        {
            _starting = null;   // the box start sees this and gives its VOX pause back
            starting.Session.PauseBeforeKeying();
            _paused = starting;
            EnsureTimer();
            Tracing.TraceLine("VoiceMessageSender: " + Name(starting) + " paused before it keyed", TraceLevel.Info);
            SpeakPaused(starting);
            return;
        }
        VoiceMessageSend? send = _send;
        if (send == null || send.Finished) return;
        send.Pause();
        Settle();
    }

    /// <summary>
    /// Carry a paused send on from its held place (#835; Noel, 2026-10-27
    /// 20:50: <i>"play keys it"</i>). The way is chosen as a fresh press
    /// would choose it now: under the box, VOX is paused again and the send
    /// keys exactly as a fresh box send does; with the operator holding the
    /// transmitter it rides that; a voice message otherwise goes as #151 sends
    /// one. A recording with nobody keying and the box not applying stays
    /// paused and says to key first, because Play never keys on its own. Every
    /// check a fresh press makes is made again.
    /// </summary>
    private static void ResumePaused()
    {
        SendInfo? info = _paused;
        if (info == null) return;
        FlexBase? rig = info.Rig;
        if (rig == null || !rig.IsConnected)
        {
            EndPausedOnDisconnect(info);
            return;
        }

        SendRoute route = RouteFor(rig);
        ResumeWay way = SendSession.ResumeAs(info.Kind == SendKind.VoiceMessage, route);
        if (way == ResumeWay.NeedsKey)
        {
            // DRAFT PROSE (#835): awaiting Noel
            ScreenReaderOutput.Speak(Lexicon.Get("audio.send.resume_needs_key", ("what", Name(info))),
                SpeechIntent.Interrupt, VerbosityLevel.Critical, subject: SpeechSubject.MessageKey);
            return;
        }

        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        if (ptt?.CanTransmitHereCheck != null && !ptt.CanTransmitHereCheck())
        {
            RefuseOrHold(info, Lexicon.Get("audio.ptt.blocked_by_license"));
            return;
        }
        // The hold after a reflected-power cut (#571), for a carrying-on that
        // keys or lets VOX key. The controller speaks the refusal; the send
        // stays paused.
        if (way != ResumeWay.Join && ptt != null && ptt.RefuseIfHeldAfterCut("a send carrying on"))
            return;
        if (rig.TxToneEngaged)
        {
            RefuseOrHold(info, Lexicon.Get("audio.reference.tone_already_armed"));
            return;
        }
        string trouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(trouble))
        {
            RefuseOrHold(info, trouble);
            return;
        }

        if (!TxAudioFile.TryLoadPieceInto(rig, info.Path ?? "", info.Session.HeldAtSeconds,
                out TxAudioFile.Loaded? loaded, out string loadTrouble) || loaded == null)
        {
            // The file went away while paused: there is nothing to carry on with.
            _paused = null;
            info.Session.Stop();
            StopTimer();
            // DRAFT PROSE (#835): awaiting Noel
            RefuseLine(Lexicon.Get("audio.send.resume_refused_ended", ("what", Name(info)), ("reason", loadTrouble)), info);
            return;
        }

        _paused = null;
        info.Resuming = true;
        Tracing.TraceLine("VoiceMessageSender: " + Name(info) + " carrying on from "
            + info.Session.HeldAtSeconds.ToString("F1") + " s, " + way, TraceLevel.Info);
        if (info.Kind == SendKind.VoiceMessage)
            StartVoiceMessagePiece(rig, info, loaded, route);
        else
            StartComputerPiece(rig, info, loaded, route);
    }

    /// <summary>
    /// A send that could not go ahead. A send carrying on after a pause stays
    /// paused, so the operator can try again; any other send is refused.
    /// </summary>
    private static void RefuseOrHold(SendInfo info, string reason)
    {
        if (info.Session.State == SendSessionState.Paused)
        {
            info.Resuming = false;
            _paused = info;
            EnsureTimer();
            Tracing.TraceLine("VoiceMessageSender: " + Name(info) + " did not carry on, still paused: " + reason,
                TraceLevel.Info);
            EarconPlayer.Warning2Beep();
            // DRAFT PROSE (#835): awaiting Noel
            ScreenReaderOutput.Speak(Lexicon.Get("audio.send.resume_refused", ("what", Name(info)), ("reason", reason)),
                SpeechIntent.Interrupt, VerbosityLevel.Critical, subject: SpeechSubject.MessageKey);
            return;
        }
        Refuse(info, reason);
    }

    /// <summary>The radio went away while a send was paused: the send ends (#835).</summary>
    private static void EndPausedOnDisconnect(SendInfo info)
    {
        if (ReferenceEquals(_paused, info)) _paused = null;
        info.Session.Stop();
        StopTimer();
        Tracing.TraceLine("VoiceMessageSender: " + Name(info) + " ended while paused: the radio disconnected",
            TraceLevel.Info);
        // DRAFT PROSE (#835): awaiting Noel
        ScreenReaderOutput.Speak(Lexicon.Get("audio.send.paused_disconnected", ("what", Name(info))),
            SpeechIntent.Queue, VerbosityLevel.Critical, subject: SpeechSubject.MessageKey);
    }

    /// <summary>
    /// One sentence at a pause (#835): that it paused, whether the radio was
    /// unkeyed or the microphone given back, and which keys carry on and stop.
    /// The ten-minute pause comes unasked, so it is heard at every verbosity.
    /// </summary>
    private static void SpeakPaused(SendInfo info)
    {
        SendSession s = info.Session;
        string key = s.PausedForId
            ? (s.PausedPieceKeyed ? "audio.send.paused_id_unkeyed" : "audio.send.paused_id_mic")
            : (s.PausedPieceKeyed ? "audio.send.paused_unkeyed" : "audio.send.paused_mic");
        if (s.PausedForId && EarconPlayer.RecorderBeepsEnabled) EarconPlayer.ConfirmTone();
        // DRAFT PROSE (#835): awaiting Noel
        ScreenReaderOutput.Speak(Lexicon.Get(key, ("what", Name(info)), ("resume", ResumeWord(info))),
            SpeechIntent.Interrupt, s.PausedForId ? VerbosityLevel.Critical : VerbosityLevel.Terse,
            subject: SpeechSubject.MessageKey);
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        VoiceMessageSend? send = _send;
        if (send == null)
        {
            SendInfo? paused = _paused;
            if (paused == null)
            {
                StopTimer();
                return;
            }
            // A paused send never keys by itself; this only ends it if the
            // radio it was going to has gone (#835: "Disconnect ends it").
            if (paused.Rig == null || !paused.Rig.IsConnected) EndPausedOnDisconnect(paused);
            return;
        }
        if (!send.Finished) send.Tick(Environment.TickCount64);
        if (send.Finished) Settle();
    }

    /// <summary>
    /// Push to talk or the lock is about to start a transmission (#835 fix
    /// round; Noel, 2026-10-28 16:55, <i>"yes, that's how it should be"</i>). A
    /// box send going out is taken over NOW, exactly as the next tick would
    /// take it (the recording stops, the microphone comes back, the key is the
    /// operator's), and the sentence saying so is returned for push to talk to
    /// speak as the FIRST part of its own line. Null when no box send is going
    /// out, and then nothing changes.
    /// </summary>
    /// <remarks>
    /// At the bench the takeover sentence was queued about half a second after
    /// "Transmitting", and the held Ctrl+Space's auto-repeat cut it at every
    /// repeat until the key was released: the operator heard that the send had
    /// stopped only after he had finished talking. Only push to talk's first
    /// line is spoken before the repeat begins, so the news has to ride in it.
    /// A hardware line taking over is still told by Settle: there is no
    /// keyboard repeat to cut it.
    /// </remarks>
    internal static string? TakeOverAtKeyDown()
    {
        VoiceMessageSend? send = _send;
        SendInfo? info = _current;
        if (send == null || info == null || send.Finished || !send.Boxed) return null;
        if (!send.TakeOverByController()) return null;
        // DRAFT PROSE (#835): awaiting Noel
        string line = info.Kind == SendKind.VoiceMessage
            ? Lexicon.Get("audio.send.message_taken_over_ptt", ("label", info.Label))
            : Lexicon.Get("audio.send.taken_over_ptt", ("what", info.What));
        info.TakeoverSpoken = true;
        Tracing.TraceLine("VoiceMessageSender: " + Name(info) + " taken over at push to talk's key-down", TraceLevel.Info);
        Settle();
        return line;
    }

    /// <summary>The send has ended. Stop watching, give VOX back, say how it ended, forget it.</summary>
    private static void Settle()
    {
        StopTimer();
        VoiceMessageSend? send = _send;
        SendInfo? info = _current;
        _send = null;
        _current = null;

        if (_watchedBy != null)
        {
            _watchedBy.EndExternalTransmitWatch();
            _watchedBy = null;
        }
        if (_riddenBy != null)
        {
            _riddenBy.EndRecordingSendRide(info != null ? Name(info) : "a send");
            _riddenBy = null;
        }
        // Taken over by the operator's key: the monitor goes back when THEY
        // unkey, not now (Noel, 2026-10-29 23:57), by the same rule the VOX
        // pause below follows.
        ReleaseHolds(info, takenOver: send?.Outcome == VoiceMessageOutcome.TakenOver);
        // The box's VOX pause: VOX comes back on only once the radio reports
        // receive and the transmit path has drained, so the recording's tail
        // cannot key it again. A send the operator took over gives it back
        // when THEY unkey, by the same rule.
        // Disarm the box send's cut owner. The send has already unkeyed.
        info?.Owned?.Dispose();
        if (info != null) info.Owned = null;   // a piece carrying on arms a fresh one (#835)
        if (info?.VoxPause != null)
        {
            info.VoxPause.Dispose();
            info.VoxPause = null;
        }
        if (send == null || info == null) return;

        Tracing.TraceLine("VoiceMessageSender: \"" + Name(info) + "\" " + send.Outcome
            + (send.Outcome == VoiceMessageOutcome.Paused
                ? " at " + send.PausedAtSeconds.ToString("F1") + " s of the piece" + (send.PausedForId ? ", ten minutes sent" : "")
                : ""), TraceLevel.Info);
        if (info.Session.Settle() == SendSessionState.Paused)
        {
            // Everything the piece held has been given back above: the VOX
            // pause (VOX comes on once the radio reports receive and the path
            // has drained, so the operator can identify by voice), the cut
            // owner, the controller's watch, the transmit monitor and the PTT
            // source watch (ReleaseHolds). A paused send
            // holds only its place.
            _paused = info;
            EnsureTimer();
            SpeakPaused(info);
            return;
        }
        if (send.Outcome == VoiceMessageOutcome.TakenOver && info.TakeoverSpoken) return;
        bool trouble = send.Outcome == VoiceMessageOutcome.RadioDidNotKey
                    || send.Outcome == VoiceMessageOutcome.VoxDidNotKey
                    || send.Outcome == VoiceMessageOutcome.RanLong
                    || send.Outcome == VoiceMessageOutcome.SourceStopped;
        Speak(info, send.Outcome, trouble);
    }

    /// <summary>One sentence for how a send ended, under the message key's subject.</summary>
    private static void Speak(SendInfo info, VoiceMessageOutcome outcome, bool trouble)
    {
        string line;
        if (info.Kind == SendKind.VoiceMessage)
        {
            string label = info.Label;
            line = outcome switch
            {
                VoiceMessageOutcome.Sent => Lexicon.Get("settings.cw.voice_sent", ("label", label)),
                VoiceMessageOutcome.Stopped => Lexicon.Get("settings.cw.voice_stopped", ("label", label)),
                VoiceMessageOutcome.Interrupted => Lexicon.Get("settings.cw.voice_interrupted", ("label", label)),
                VoiceMessageOutcome.RanLong => Lexicon.Get("settings.cw.voice_ran_long", ("label", label)),
                VoiceMessageOutcome.RadioDidNotKey => Lexicon.Get("settings.cw.voice_not_sent",
                    ("label", label), ("reason", Lexicon.Get("settings.cw.radio_did_not_key"))),
                VoiceMessageOutcome.VoxDidNotKey => Lexicon.Get("settings.cw.voice_not_sent",
                    ("label", label), ("reason", Lexicon.Get("settings.cw.vox_did_not_key"))),
                // DRAFT PROSE (#835): awaiting Noel
                VoiceMessageOutcome.TakenOver => Lexicon.Get("audio.send.message_taken_over", ("label", label)),
                VoiceMessageOutcome.SourceStopped => Lexicon.Get("audio.send.message_source_stopped", ("label", label)),
                _ => "",
            };
        }
        else
        {
            string what = info.What;
            RecorderOutcome? failure = info.Slice?.Failure;
            // DRAFT PROSE (#835): awaiting Noel
            line = outcome switch
            {
                VoiceMessageOutcome.Sent => Lexicon.Get("audio.send.sent", ("what", what)),
                VoiceMessageOutcome.Stopped => Lexicon.Get("audio.send.stopped", ("what", what)),
                VoiceMessageOutcome.Interrupted => Lexicon.Get("audio.send.interrupted", ("what", what)),
                VoiceMessageOutcome.RanLong => Lexicon.Get("audio.send.ran_long", ("what", what)),
                VoiceMessageOutcome.TakenOver => Lexicon.Get("audio.send.taken_over", ("what", what)),
                VoiceMessageOutcome.RadioDidNotKey => Lexicon.Get("audio.send.not_sent",
                    ("what", what), ("reason", Lexicon.Get("settings.cw.radio_did_not_key"))),
                VoiceMessageOutcome.SourceStopped when failure != null && !string.IsNullOrEmpty(failure.Message)
                    => Lexicon.Get("audio.send.not_sent", ("what", what), ("reason", failure.Message)),
                VoiceMessageOutcome.SourceStopped => Lexicon.Get("audio.send.source_stopped", ("what", what)),
                _ => "",
            };
        }
        if (trouble) EarconPlayer.Warning2Beep();
        ScreenReaderOutput.Speak(line, SpeechIntent.Queue,
            trouble ? VerbosityLevel.Critical : VerbosityLevel.Terse,
            subject: SpeechSubject.MessageKey);
    }

    private static void Refuse(SendInfo info, string reason)
    {
        // DRAFT PROSE (#835): awaiting Noel (the recording form; the message form is #151's)
        RefuseLine(info.Kind == SendKind.VoiceMessage
            ? Lexicon.Get("settings.cw.voice_not_sent", ("label", info.Label), ("reason", reason))
            : Lexicon.Get("audio.send.not_sent", ("what", info.What), ("reason", reason)), info);
    }

    private static void RefuseLine(string line, SendInfo info)
    {
        Tracing.TraceLine("VoiceMessageSender: \"" + Name(info) + "\" not sent: " + line, TraceLevel.Info);
        EarconPlayer.Warning2Beep();
        ScreenReaderOutput.Speak(line, SpeechIntent.Interrupt, VerbosityLevel.Critical,
            subject: SpeechSubject.MessageKey);
    }

    // ── The radio's slice playback as a send's source ───────────────────

    /// <summary>
    /// The radio's slice playback seen as the thing a box send sends. Started
    /// only once the radio is keyed (<see cref="KeyedSendOptions.StartSourceWhenKeyed"/>),
    /// so the slice recorder's own intent, fixed at the press, is "on the air"
    /// (<c>FlexBase.StartSlicePlayAsync</c>); every safety the recorder applies
    /// to on-air playback applies here unchanged.
    /// </summary>
    internal sealed class SliceSendRig : IVoiceMessageRig
    {
        private readonly FlexBase _rig;
        private readonly SliceRecorderTarget _target;
        private bool _started, _stopAsked;
        private volatile bool _confirmed, _failed;

        internal SliceSendRig(FlexBase rig, SliceRecorderTarget target) { _rig = rig; _target = target; }

        /// <summary>The slice recorder refused or failed the start; its own outcome says why.</summary>
        internal bool Failed => _failed;
        internal RecorderOutcome? Failure { get; private set; }

        public bool Transmit { get => _rig.Transmit; set => _rig.Transmit = value; }

        // Playing only once asked and not failed: the recorder marks a slice
        // playing BEFORE the radio confirms, so a refused start reads as
        // playing for a moment and then not, which must not read as "ended".
        public bool FilePlaying => _started && !_failed && _rig.IsSlicePlaying(_target);

        // Ended only after a CONFIRMED start, for the same reason.
        public bool FileReachedEnd => _confirmed && !_failed && !_rig.IsSlicePlaying(_target);

        public double FileSeconds => _rig.SliceRecordingSeconds(_target) ?? FlexBase.SliceRecordBufferSeconds;

        public void FileStart()
        {
            if (_started) return;
            _started = true;
            _ = StartAsync();
        }

        private async Task StartAsync()
        {
            try
            {
                var outcome = await _rig.StartSlicePlayAsync(_target);
                if (outcome.Success) _confirmed = true;
                else { Failure = outcome; _failed = true; }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("SliceSendRig: start threw: " + ex.Message, TraceLevel.Warning);
                _failed = true;
            }
        }

        public void FileStartForVox() => FileStart();

        public void FileStop()
        {
            if (!_started || _stopAsked) return;
            _stopAsked = true;
            // Serialised behind the start on the slice's own gate, so a stop
            // asked while the start is still being confirmed stops it. The
            // unkey that follows stops it too (FlexBase.Transmit cancels slice
            // playback on the way down); this one says so if it fails.
            _ = RecorderControls.ReportStopFailureAsync(_rig.StopSlicePlayAsync(_target));
        }

        public bool VoxOn => _rig.Vox == FlexBase.OffOnValues.on;
    }
}
