using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using JJTrace;
using Radios;
using Radios.Speech;

namespace JJFlexWpf;

/// <summary>
/// The voice half of a message key: load the slot's recording, send it in
/// place of the microphone, say what happened (Sprint 48 Track A, #151).
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
/// pressed from the log form stops a send started from Home.
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

    /// <summary>True while a voice message is going out.</summary>
    public static bool InFlight => _send != null && !_send.Finished;

    /// <summary>The label of the message going out, or empty.</summary>
    public static string InFlightLabel => InFlight ? _send!.Label : "";

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
            bool same = string.Equals(_send!.Label, label, StringComparison.Ordinal);
            Stop();
            if (same) return;
        }

        // The licence-aware lockout PTT applies, applied here too: a key that
        // keys the radio must answer to the same rule as the key that keys it
        // by hand. Resolved per call — the controller is recreated on operator
        // switch and nulled on power-off.
        PttSafetyController? ptt = Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke();
        if (ptt?.CanTransmitHereCheck != null && !ptt.CanTransmitHereCheck())
        {
            Refuse(label, Lexicon.Get("audio.ptt.blocked_by_license"));
            return;
        }

        if (rig.TxToneEngaged)
        {
            Refuse(label, Lexicon.Get("audio.reference.tone_already_armed"));
            return;
        }

        string trouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(trouble))
        {
            Refuse(label, trouble);
            return;
        }

        string path = RecordingStore.PathForName(item.Audio);
        if (!File.Exists(path))
        {
            Refuse(label, Lexicon.Get("settings.cw.recording_missing", ("name", item.Audio)));
            return;
        }

        if (!TxAudioFile.TryLoadInto(rig, path, out TxAudioFile.Loaded? loaded, out string loadTrouble)
            || loaded == null)
        {
            Refuse(label, loadTrouble);
            return;
        }

        _send = VoiceMessageSend.Begin(new FlexBaseVoiceMessageRig(rig), label, Environment.TickCount64);
        if (_send.WeKeyed && ptt != null)
        {
            // The controller's live transmit monitoring runs over this
            // carrier exactly as it does over a transmit check's.
            ptt.BeginExternalTransmitWatch();
            _watchedBy = ptt;
        }

        Tracing.TraceLine("VoiceMessageSender: sending \"" + label + "\" from " + path
            + " (" + loaded.Describe() + "), " + (_send.WeKeyed ? "keying the radio" : "radio already transmitting"),
            TraceLevel.Info);

        ScreenReaderOutput.Speak(
            Lexicon.Get("settings.cw.voice_sending", ("label", label),
                ("length", RecordingStore.DescribeLength(loaded.Seconds))),
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

    /// <summary>Stop the message going out, if one is. Idempotent.</summary>
    public static void Stop()
    {
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

    /// <summary>The send has ended. Stop watching, say how it ended, forget it.</summary>
    private static void Settle()
    {
        StopTimer();
        VoiceMessageSend? send = _send;
        _send = null;

        if (_watchedBy != null)
        {
            _watchedBy.EndExternalTransmitWatch();
            _watchedBy = null;
        }
        if (send == null) return;

        string label = send.Label;
        string line = send.Outcome switch
        {
            VoiceMessageOutcome.Sent => Lexicon.Get("settings.cw.voice_sent", ("label", label)),
            VoiceMessageOutcome.Stopped => Lexicon.Get("settings.cw.voice_stopped", ("label", label)),
            VoiceMessageOutcome.Interrupted => Lexicon.Get("settings.cw.voice_interrupted", ("label", label)),
            VoiceMessageOutcome.RanLong => Lexicon.Get("settings.cw.voice_ran_long", ("label", label)),
            VoiceMessageOutcome.RadioDidNotKey => Lexicon.Get("settings.cw.voice_not_sent",
                ("label", label), ("reason", Lexicon.Get("settings.cw.radio_did_not_key"))),
            _ => "",
        };
        Tracing.TraceLine("VoiceMessageSender: \"" + label + "\" " + send.Outcome, TraceLevel.Info);

        bool trouble = send.Outcome == VoiceMessageOutcome.RadioDidNotKey
                    || send.Outcome == VoiceMessageOutcome.RanLong;
        if (trouble) EarconPlayer.Warning2Beep();
        ScreenReaderOutput.Speak(line, SpeechIntent.Queue,
            trouble ? VerbosityLevel.Critical : VerbosityLevel.Terse,
            subject: SpeechSubject.MessageKey);
    }

    private static void Refuse(string label, string reason)
    {
        Tracing.TraceLine("VoiceMessageSender: \"" + label + "\" not sent: " + reason, TraceLevel.Info);
        EarconPlayer.Warning2Beep();
        ScreenReaderOutput.Speak(
            Lexicon.Get("settings.cw.voice_not_sent", ("label", label), ("reason", reason)),
            SpeechIntent.Interrupt, VerbosityLevel.Critical,
            subject: SpeechSubject.MessageKey);
    }
}
