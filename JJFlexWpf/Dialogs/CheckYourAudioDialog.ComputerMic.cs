using System;
using System.Windows.Threading;
using JJPortaudio;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio's first method: check this computer's microphone through
/// Windows, with no radio involved and nothing keyed (ruled by Noel 2026-10-28
/// 20:30, and 20:34 for the no-radio case).
/// </summary>
/// <remarks>
/// <para>
/// <b>One probe, one set of words.</b> It runs the same <see cref="MicProbe"/>
/// the Audio Devices window's microphone check runs, on the microphone the
/// voice-note recorder and the Fix tool resolve (<c>RecordingNarrator.ResolveMicrophone</c>),
/// and says what it hears in <see cref="MicCheckWords"/>, the words that check
/// says. The Audio Workshop's "Check Microphone..." button, which opened that
/// check in the Audio Devices window, is gone: this is where it lives now.
/// </para>
/// <para>
/// <b>An escalation's first step.</b> Arrowing down from here goes one step
/// further each time: the radio's microphone at zero power, hearing yourself
/// live, recording and playing back, then the off-air take and Loopback.
/// </para>
/// <para>
/// Start starts it and starts it again stops it, the way Start runs every
/// method; Ctrl+Enter does the same; Escape while it runs stops it and stays,
/// so the verdict is heard before anything closes. Changing the method,
/// closing the window or a teardown stops it silently.
/// </para>
/// </remarks>
public partial class CheckYourAudioDialog
{
    private MicProbe? _computerMicProbe;
    private DispatcherTimer? _computerMicTimer;
    private bool _computerMicPrivacyChecked;
    private bool _computerMicPrivacyBlocked;
    private string _computerMicPrivacyWhy = "";

    /// <summary>The last thing the computer microphone check said, shown in the reading box while that method is chosen.</summary>
    private string _computerMicReading = "";

    /// <summary>True while the computer microphone check is listening.</summary>
    internal bool ComputerMicCheckRunning => _computerMicProbe != null;

    /// <summary>Start or stop the computer microphone check. Nothing is keyed and no radio is needed.</summary>
    private void ToggleComputerMicCheck()
    {
        if (_computerMicProbe != null) { StopComputerMicCheck(speak: true); return; }

        Devices.DeviceInfo? row = RecordingNarrator.ResolveMicrophone(out string trouble);
        if (row == null)
        {
            ShowComputerMicReading(trouble);
            ScreenReaderOutput.Speak(trouble, VerbosityLevel.Critical, interrupt: true);
            return;
        }

        // The privacy switches are context, never a gate, as in Audio Devices:
        // a registry read is evidence about a switch, not about a microphone.
        bool blocked = false;
        string why = "";
        try { blocked = MicrophonePrivacy.IsBlocked(MicrophonePrivacy.Check(out why)); }
        catch (Exception) { /* unknown stays unknown */ }

        var probe = new MicProbe();
        var outcome = probe.Start(row, out string failure);
        if (outcome != MicProbe.StartOutcome.Started)
        {
            probe.Dispose();
            string couldNotStart = Lexicon.Get("audio.device.mic_check_could_not_start",
                ("reason", blocked && why.Length > 0 ? why + " " + failure : failure));
            ShowComputerMicReading(couldNotStart);
            ScreenReaderOutput.Speak(couldNotStart, VerbosityLevel.Critical, interrupt: true);
            return;
        }

        _computerMicProbe = probe;
        _computerMicPrivacyChecked = false;
        _computerMicPrivacyBlocked = blocked;
        _computerMicPrivacyWhy = why;
        JJTrace.Tracing.TraceLine("Check your audio: computer microphone check started on \"" + row.Name + "\"",
            System.Diagnostics.TraceLevel.Info);
        SetStartButtonLabel("Stop Audio Check");
        ShowComputerMicReading(Lexicon.Get("audio.device.mic_check_listening"));
        _computerMicTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal,
            (s, e) => UpdateComputerMicReading(), Dispatcher);
        _computerMicTimer.Start();
        ScreenReaderOutput.Speak(blocked
            ? Lexicon.Get("audio.device.mic_check_started_but_blocked", ("reason", why))
            : Lexicon.Get("audio.device.mic_check_started", ("device", row.Name)),
            blocked ? VerbosityLevel.Critical : VerbosityLevel.Terse);
    }

    /// <summary>Stop the check and close the device; with <paramref name="speak"/>, say the verdict. Idempotent.</summary>
    private void StopComputerMicCheck(bool speak)
    {
        _computerMicTimer?.Stop();
        var probe = _computerMicProbe;
        _computerMicProbe = null;
        if (probe == null) return;
        MicProbe.Reading final = probe.Read();
        probe.Stop();
        probe.Dispose();
        // No Windows level control sits beside this reading for the advice to
        // point at, so it points at Windows Sound settings.
        string summary = MicCheckWords.Final(final, levelHere: null);
        JJTrace.Tracing.TraceLine("Check your audio: computer microphone check stopped: " + summary,
            System.Diagnostics.TraceLevel.Info);
        ShowComputerMicReading(summary);
        if (!_closing) SetStartButtonLabel("Start Audio Check");
        if (speak) ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, interrupt: true);
    }

    private void UpdateComputerMicReading()
    {
        var probe = _computerMicProbe;
        if (probe == null) { _computerMicTimer?.Stop(); return; }
        MicProbe.Reading r = probe.Read();
        if (r.Faulted)
        {
            string fault = r.FaultMessage;
            StopComputerMicCheck(speak: false);
            string stopped = Lexicon.Get("audio.device.mic_check_stopped_faulted", ("reason", fault));
            ShowComputerMicReading(stopped);
            ScreenReaderOutput.Speak(stopped, VerbosityLevel.Critical, interrupt: true);
            return;
        }
        // Privacy read once, and only after a second of nothing.
        if (!r.AnySound && r.Seconds >= 1.0 && !_computerMicPrivacyChecked)
        {
            _computerMicPrivacyChecked = true;
            try { _computerMicPrivacyBlocked = MicrophonePrivacy.IsBlocked(MicrophonePrivacy.Check(out _computerMicPrivacyWhy)); }
            catch (Exception) { /* unknown stays unknown */ }
        }
        ShowComputerMicReading(MicCheckWords.Live(r, _computerMicPrivacyBlocked, _computerMicPrivacyWhy));
    }

    /// <summary>Put the check's words in the reading box, where every method's verdict appears. Assigned only on change, so a review cursor is not reset twice a second.</summary>
    private void ShowComputerMicReading(string text)
    {
        _computerMicReading = text;
        if (_micReadingBox != null && SelectedMethod == AudioCheckListenMethods.ComputerMic && _micReadingBox.Text != text)
            _micReadingBox.Text = text;
    }

    /// <summary>The reading box while the computer microphone method is chosen: its own words, never the radio's mic meter.</summary>
    private bool ShowingComputerMicReading()
    {
        if (SelectedMethod != AudioCheckListenMethods.ComputerMic || _micReadingBox == null) return false;
        string text = _computerMicReading.Length > 0 ? _computerMicReading : Lexicon.Get("audio.check.computer_mic.idle");
        if (_micReadingBox.Text != text) _micReadingBox.Text = text;
        return true;
    }
}
