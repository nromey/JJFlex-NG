using System;
using Radios;
using Radios.Listenback;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio's playback takes (Unified takes, #864, round two): which
/// methods save their playback, and "start a new test or add to this one".
/// Kept in its own file so the check's own flow changes only where it must.
/// </summary>
public partial class CheckYourAudioDialog
{
    /// <summary>
    /// "Save each playback as a take", as this radio's setting holds it. Off
    /// by default, and until the setting is loaded nothing here is reachable.
    /// </summary>
    private bool _savePlaybackTakes;

    /// <summary>
    /// Whether a check of <paramref name="method"/> saves its playback as a
    /// take: Radio recording and Looped back, with the box ticked. Looped back
    /// stays hidden over SmartLink (D1); <see cref="AudioCheckMethods.Offered"/>
    /// already keeps it out of the cycle there.
    /// </summary>
    internal bool SavesPlaybackTake(AudioCheckListenMethods method) =>
        _savePlaybackTakes && method is AudioCheckListenMethods.RecordPlayback or AudioCheckListenMethods.Loopback;

    /// <summary>A radio connected: the same radio keeps the current test, another starts afresh (D3).</summary>
    private static void NoteTakeSeriesConnection(FlexBase? rig)
    {
        string serial = rig?.TakeAdmissionFactsNow().Serial ?? "";
        if (serial.Length > 0) TakeSession.Series.Connected(serial);
    }

    /// <summary>
    /// Ask "start a new test or add to this one" when the test's last take was
    /// made with another mic input, computer microphone, mic profile or radio
    /// (Q7, D5), and the operator has not already said "add to this one" for
    /// this test. Called BEFORE the countdown, by Start, never while keyed;
    /// asking changes nothing on the radio. False when the operator backed out
    /// with Escape, or something keyed while the question was open: then
    /// nothing starts.
    /// </summary>
    private bool AdmitTakeBeforeCountdown(AudioCheckListenMethods method, PttSafetyController ptt)
    {
        if (!SavesTakeOf(method) || _rig == null) return true;
        var rig = _rig;
        string root = ListenbackTakes.Root;
        TakeAdmission admission;
        try
        {
            NoteTakeSeriesConnection(rig);
            admission = TakeSession.Series.Admit(root, rig.TakeAdmissionFactsNow());
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException
                                      or UnauthorizedAccessException)
        {
            // The test registry could not be read: the take still saves, in
            // a new test (TakeSession.Series.Reserve makes one), so nothing is
            // asked and nothing is lost.
            JJTrace.Tracing.TraceLine("Check your audio take admission: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            return true;
        }
        if (!admission.Ask) return true;
        JJTrace.Tracing.TraceLine("Check your audio take admission: asking, changed=" + string.Join(",", admission.Changed),
            System.Diagnostics.TraceLevel.Info);
        // DRAFT PROSE: awaiting Noel. "Start a new test or add to this one".
        var question = new ConfirmActionDialog(TakeAdmissionText.Title,
            TakeAdmissionText.Message(admission.Test, admission.Changed),
            question: TakeAdmissionText.Question(admission.Test),
            yesLabel: TakeAdmissionText.NewTestLabel, noLabel: TakeAdmissionText.AddLabel) { Owner = this };
        bool? answer = question.ShowModalDialog();
        if (!question.Answered)
        {
            ScreenReaderOutput.Speak(TakeAdmissionText.BackedOut, Radios.Speech.SpeechIntent.Interrupt, VerbosityLevel.Terse,
                subject: Radios.Speech.SpeechSubject.PlaybackTake);
            return false;
        }
        // The question was modal: something may have keyed, or the radio gone, meanwhile.
        if (!ReferenceEquals(rig, _rig) || ptt.IsTransmitting || rig.Transmit)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.already_transmitting"), VerbosityLevel.Critical, interrupt: true);
            return false;
        }
        try { TakeSession.Series.Answer(root, startNewTest: answer == true); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            JJTrace.Tracing.TraceLine("Check your audio take admission answer: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
        }
        return true;
    }

    /// <summary>Whether a check of <paramref name="method"/> makes a take that joins a test.</summary>
    private bool SavesTakeOf(AudioCheckListenMethods method) => SavesPlaybackTake(method);
}
