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
/// under Don's box.
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
/// pressed from the log form stops a send started from Home. A Play press
/// while any send is going out stops it, as the same message key does.
/// </para>
/// <para>
/// <b>Don's box (#835).</b> With the box ticked and VOX on, every send
/// takes the same road (<see cref="SendRoute.Box"/>): pause VOX through the
/// radio's one VOX owner and wait for the radio to confirm it off; if VOX is
/// still holding the radio up on the operator's last words, let it drop;
/// then key, send the whole recording, unkey, and give the pause back, which
/// turns VOX on again only after the radio reports receive and the path has
/// drained (Noel, 2026-10-27 15:29). The short self-key the radio gives when
/// VOX is switched back on (#819) is accepted and not engineered around.
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

    private enum SendKind { VoiceMessage, ComputerRecording, SliceRecording }

    /// <summary>What a send is, beyond its state machine: what to call it, and what it holds.</summary>
    private sealed class SendInfo
    {
        public SendKind Kind;
        /// <summary>A voice message's slot label.</summary>
        public string Label = "";
        /// <summary>A recording's phrase for the sentences, "the computer recording X".</summary>
        public string What = "";
        /// <summary>The file to play on this computer while the send goes out, or null.</summary>
        public string? MonitorPath;
        public bool Monitoring;
        /// <summary>The box's VOX pause, given back when the send has settled.</summary>
        public FlexBase.LocalPlaybackVoxPause? VoxPause;
        public SliceSendRig? Slice;
    }

    private static SendInfo? _current;

    /// <summary>A box send waiting for the radio to confirm VOX off, or for VOX's tail to drop.</summary>
    private static SendInfo? _starting;

    /// <summary>True while a send is going out, or a box send is getting ready to.</summary>
    public static bool InFlight => _starting != null || (_send != null && !_send.Finished);

    /// <summary>
    /// True while the send going out, or getting ready, was started by a Play
    /// door: a computer recording or a slice recording. A Play press then stops
    /// it, as the same message key stops a message.
    /// </summary>
    public static bool InFlightFromPlay =>
        InFlight && (_starting ?? _current)?.Kind is SendKind.ComputerRecording or SendKind.SliceRecording;

    /// <summary>The label of the message going out, or empty.</summary>
    public static string InFlightLabel => _send != null && !_send.Finished ? _send.Label : "";

    // ── Routing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The operator is keying the radio themselves: push to talk or the lock
    /// through the controller, a hardware PTT line, or the tune carrier. A
    /// radio held up only by VOX is NOT this (Noel, 2026-10-27 15:28).
    /// </summary>
    private static bool OperatorHolds(FlexBase rig, PttSafetyController? ptt)
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
    /// stops it; a different slot stops it and starts the new one.
    /// </summary>
    public static void Send(FlexBase rig, CWMessageItem item)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (item == null) throw new ArgumentNullException(nameof(item));
        string label = item.Label ?? "";

        if (InFlight)
        {
            bool same = string.Equals((_starting ?? _current)?.Label, label, StringComparison.Ordinal)
                        && (_starting ?? _current)?.Kind == SendKind.VoiceMessage;
            Stop();
            if (same) return;
        }

        var info = new SendInfo { Kind = SendKind.VoiceMessage, Label = label };

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

        if (!TxAudioFile.TryLoadInto(rig, path, out TxAudioFile.Loaded? loaded, out string loadTrouble)
            || loaded == null)
        {
            Refuse(info, loadTrouble);
            return;
        }

        // Don's box (#835): with it ticked and VOX on, the message keys the
        // radio itself rather than letting VOX key it. Unticked, #151's rule
        // stands exactly as it was: VOX keys it, otherwise the key keys it.
        if (RouteFor(rig) == SendRoute.Box)
        {
            info.MonitorPath = path;
            _ = BeginBoxAsync(rig, info, new FlexBaseVoiceMessageRig(rig), startsOnKey: false, loaded.Seconds,
                joinInstead: () => Launch(rig, info,
                    VoiceMessageSend.Begin(new FlexBaseVoiceMessageRig(rig), label, Environment.TickCount64),
                    loaded.Seconds, path, loaded.Describe()));
            return;
        }

        Launch(rig, info, VoiceMessageSend.Begin(new FlexBaseVoiceMessageRig(rig), label, Environment.TickCount64),
            loaded.Seconds, path, loaded.Describe());
    }

    /// <summary>
    /// Send a computer recording to the air (#835, Tape Deck phase 2). The door
    /// decided the route: <see cref="SendRoute.JoinTransmission"/> joins a
    /// transmission the operator is holding and never touches the key (Noel,
    /// 2026-10-21 10:57: keyed, Play sends the file); <see cref="SendRoute.Box"/>
    /// keys the radio itself.
    /// </summary>
    public static void SendComputerRecording(FlexBase rig, string path, SendRoute route)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (InFlight) { Stop(); return; }

        // DRAFT PROSE (#835): awaiting Noel
        var info = new SendInfo
        {
            Kind = SendKind.ComputerRecording,
            What = Lexicon.Get("audio.send.what_computer", ("name", Path.GetFileNameWithoutExtension(path))),
            MonitorPath = path,
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
        if (!TxAudioFile.TryLoadInto(rig, path, out TxAudioFile.Loaded? loaded, out string loadTrouble)
            || loaded == null)
        {
            Refuse(info, loadTrouble);
            return;
        }

        if (route == SendRoute.Box)
        {
            _ = BeginBoxAsync(rig, info, new FlexBaseVoiceMessageRig(rig), startsOnKey: false, loaded.Seconds,
                joinInstead: () => JoinWithFile(rig, info, loaded));
            return;
        }
        JoinWithFile(rig, info, loaded);
    }

    /// <summary>
    /// Send the radio's own slice recording under Don's box (#835): key, then
    /// start the slice's playback, which the radio sends to the air because the
    /// radio is keyed, and which the operator already hears as it goes out
    /// (Noel, 15:24) — so no local copy is played for this one.
    /// </summary>
    public static void SendSliceRecording(FlexBase rig, SliceRecorderTarget target)
    {
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (InFlight) { Stop(); return; }

        string letter = rig.SliceRecorderLetter(target);
        // DRAFT PROSE (#835): awaiting Noel
        var info = new SendInfo
        {
            Kind = SendKind.SliceRecording,
            What = Lexicon.Get("audio.send.what_slice", ("letter", letter)),
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
        double seconds = rig.SliceRecordingSeconds(target) ?? FlexBase.SliceRecordBufferSeconds;
        _ = BeginBoxAsync(rig, info, slice, startsOnKey: true, seconds,
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
            Refuse(info, Lexicon.Get("audio.send.not_yet_transmitting"));
            return;
        }
        var send = VoiceMessageSend.Begin(new FlexBaseVoiceMessageRig(rig), info.What, Environment.TickCount64);
        Launch(rig, info, send, loaded.Seconds, loaded.Path, loaded.Describe());
        StartMonitor(info);
    }

    // ── Don's box ───────────────────────────────────────────────────────

    /// <summary>
    /// Pause VOX, let VOX's own tail drop, then key and send (#835). Every road
    /// out of here before the send begins gives the pause back.
    /// </summary>
    private static async Task BeginBoxAsync(FlexBase rig, SendInfo info, IVoiceMessageRig sendRig,
        bool startsOnKey, double seconds, Action joinInstead)
    {
        _starting = info;
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
            return;
        }
        if (pause == null)
        {
            _starting = null;
            Refuse(info, Lexicon.Get("audio.send.vox_unconfirmed"));
            return;
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
            return;
        }
        _starting = null;

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
            return;
        }
        if (ptt != null && ptt.RefuseIfHeldAfterCut("a send"))
        {
            pause.Dispose();
            return;
        }

        info.VoxPause = pause;
        var options = new KeyedSendOptions
        {
            // Push to talk and the lock, through the controller, which then
            // owns the unkey and every timeout on it. Deliberately NOT the
            // radio's hardware PTT source: nobody has read what the radio
            // reports as the source of a transmission this app keyed, and a
            // misread here would release the send's unkey with no owner left
            // to drop the carrier. A foot switch held through the send's own
            // unkey keeps the radio keyed by itself, so it loses nothing.
            OperatorKeyed = () => Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke()?.IsTransmitting == true,
            StartSourceWhenKeyed = startsOnKey,
            SourceFailed = info.Slice == null ? null : () => info.Slice.Failed,
            Keyed = () => StartMonitor(info),
        };
        var send = VoiceMessageSend.BeginKeyed(sendRig, Name(info), Environment.TickCount64, options);
        Launch(rig, info, send, seconds, info.MonitorPath ?? "", "box");
    }

    // ── Hearing the send ────────────────────────────────────────────────

    /// <summary>
    /// Hear what is being sent (Noel, 15:22). Our own file plays on this
    /// computer alongside the air; the radio's slice playback the radio
    /// already lets the operator hear, so it has no monitor path.
    /// </summary>
    private static void StartMonitor(SendInfo info)
    {
        if (info.MonitorPath == null || info.Monitoring) return;
        info.Monitoring = RecordingPlayer.PlayAlongsideSend(info.MonitorPath);
    }

    private static void StopMonitor(SendInfo? info)
    {
        if (info == null || !info.Monitoring) return;
        info.Monitoring = false;
        if (RecordingPlayer.PlayingAlongsideSend
            && string.Equals(RecordingPlayer.CurrentPath, info.MonitorPath, StringComparison.OrdinalIgnoreCase))
            RecordingPlayer.StopWithoutSpeaking();
    }

    // ── Running a send ──────────────────────────────────────────────────

    private static string Name(SendInfo info) => info.Kind == SendKind.VoiceMessage ? info.Label : info.What;

    private static void Launch(FlexBase rig, SendInfo info, VoiceMessageSend send, double seconds,
        string path, string describe)
    {
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        _send = send;
        _current = info;
        if (_send.WeKeyed && ptt != null)
        {
            // The controller's live transmit monitoring runs over this
            // carrier exactly as it does over a transmit check's. For a box
            // send that is the protection #804 says a VOX-keyed send lacks.
            ptt.BeginExternalTransmitWatch();
            _watchedBy = ptt;
        }

        Tracing.TraceLine("VoiceMessageSender: sending \"" + Name(info) + "\" from " + path
            + " (" + describe + "), "
            + (_send.Boxed ? "keying the radio with VOX paused (the box)"
               : _send.WeKeyed ? "keying the radio"
               : _send.ViaVox ? "letting VOX key the radio"
               : "radio already transmitting"),
            TraceLevel.Info);

        string length = RecordingStore.DescribeLength(seconds);
        // DRAFT PROSE (#835): awaiting Noel (the recording sentences; the message one is #151's)
        ScreenReaderOutput.Speak(
            info.Kind == SendKind.VoiceMessage
                ? Lexicon.Get("settings.cw.voice_sending", ("label", info.Label), ("length", length))
                : Lexicon.Get("audio.send.sending", ("what", info.What), ("length", length)),
            SpeechIntent.Interrupt, VerbosityLevel.Terse,
            subject: SpeechSubject.MessageKey);

        // Per dispatcher, not once: a DispatcherTimer belongs to the thread
        // that made it, and a message key can arrive from the WinForms full
        // log form, which FindLogEntry opens on an STA thread of its own.
        // Starting a timer made on another thread throws, so a send started
        // from a different dispatcher gets a timer of its own.
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

    /// <summary>Stop the send going out, or the box send getting ready. Idempotent.</summary>
    public static void Stop()
    {
        SendInfo? starting = _starting;
        if (starting != null)
        {
            // The box start owns its VOX pause until it sees this and gives it back.
            _starting = null;
            Tracing.TraceLine("VoiceMessageSender: " + Name(starting) + " stopped before it keyed", TraceLevel.Info);
            Speak(starting, VoiceMessageOutcome.Stopped, trouble: false);
            return;
        }
        VoiceMessageSend? send = _send;
        if (send == null || send.Finished) return;
        send.Stop();
        Settle();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        VoiceMessageSend? send = _send;
        if (send == null)
        {
            StopTimer();
            return;
        }
        if (!send.Finished) send.Tick(Environment.TickCount64);
        if (send.Finished) Settle();
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
        StopMonitor(info);
        // The box's VOX pause: VOX comes back on only once the radio reports
        // receive and the transmit path has drained, so the recording's tail
        // cannot key it again. A send the operator took over gives it back
        // when THEY unkey, by the same rule.
        if (info?.VoxPause != null)
        {
            info.VoxPause.Dispose();
            info.VoxPause = null;
        }
        if (send == null || info == null) return;

        Tracing.TraceLine("VoiceMessageSender: \"" + Name(info) + "\" " + send.Outcome, TraceLevel.Info);
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
