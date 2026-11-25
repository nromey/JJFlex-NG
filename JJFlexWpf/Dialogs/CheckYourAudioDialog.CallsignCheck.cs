using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;
using Radios.Speech;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The way out of a callsign refusal, in Check your audio (#1306).
/// </summary>
/// <remarks>
/// <para><b>Ruled by Noel 2026-11-24 13:33</b>, and kept when the check's
/// default became off at 13:43: "If there's a refusal, I'd offer a button to
/// toggle it off for folks that are annoyed with it." The refusal at a key
/// press is spoken while a key may be held, so no button can appear there
/// without taking focus mid-press; that sentence names where the check is
/// turned off instead. This window is already open and focused when its start
/// is refused, so it can carry the button itself.</para>
/// <para><b>The button lives only while the refusal does.</b> Collapsed, and so
/// out of the tab order, until a start is refused for a missing callsign; it
/// goes again when a start passes the callsign question, when the poll finds
/// the refusal no longer applies (a callsign entered, or the check turned off
/// in Settings), and with the window.</para>
/// <para><b>No confirmation box</b> (the orchestrator's decision, not a ruling):
/// the check is voluntary and off by default, and turning it back on is one
/// checkbox in Settings. The press turns it off, focus goes back to Start, and
/// the answer says what changed, so the operator can start again at once.</para>
/// </remarks>
public partial class CheckYourAudioDialog
{
    private Button? _callsignCheckOffButton;

    /// <summary>The door name the start's refusal is traced and throttled under.</summary>
    private const string StartDoor = "Check your audio's start";

    /// <summary>Built right after Start, in the Start section, hidden.</summary>
    private void BuildCallsignCheckOffButton()
    {
        string label = Lexicon.Get("settings.operators.callsign_check_off_button");
        _callsignCheckOffButton = new Button
        {
            Content = label,
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_callsignCheckOffButton, label);
        JJFlexHelp.SetText(_callsignCheckOffButton, Lexicon.Get("settings.operators.callsign_check_off_help"));
        _callsignCheckOffButton.Click += (s, e) => TurnCallsignCheckOff();
        _sections.Add(CheckContent, _callsignCheckOffButton);
    }

    /// <summary>
    /// The start's callsign question, asked before any countdown (#335, J1).
    /// Refused, it says this window's own sentence, which names the button, and
    /// shows the button. Passed, the button goes.
    /// </summary>
    /// <returns>True when the start is refused: the caller must not go on.</returns>
    private bool RefuseStartForNoCallsign()
    {
        if (ZeroPowerDoors.RefuseNoCallsign(_rig, StartDoor,
                sentence: Lexicon.Get("settings.operators.no_callsign_check_your_audio")))
        {
            ShowCallsignCheckOffButton(true);
            return true;
        }
        ShowCallsignCheckOffButton(false);
        return false;
    }

    /// <summary>
    /// Every poll: once the refusal no longer applies, the button goes, so it
    /// never offers to turn off a check that is not stopping anything.
    /// </summary>
    private void SyncCallsignCheckOffButton(FlexBase rig)
    {
        if (_callsignCheckOffButton?.Visibility != Visibility.Visible) return;
        if (!ZeroPowerDoors.NoCallsign(rig)) ShowCallsignCheckOffButton(false);
    }

    private void ShowCallsignCheckOffButton(bool show)
    {
        var button = _callsignCheckOffButton;
        if (button == null) return;
        if (show)
        {
            button.Visibility = Visibility.Visible;
            return;
        }
        if (button.Visibility != Visibility.Visible) return;
        // A focused control that disappears strands focus; Start is where the
        // operator goes next anyway.
        bool hadFocus = button.IsKeyboardFocusWithin;
        button.Visibility = Visibility.Collapsed;
        if (hadFocus) _startCheckButton?.Focus();
    }

    /// <summary>
    /// The button: the check off everywhere at once (the live value, the
    /// computer's settings file and the connected radio's settings in memory,
    /// so Settings does not reopen ticked and write it back), focus back to
    /// Start, and one sentence saying so.
    /// </summary>
    private void TurnCallsignCheckOff()
    {
        bool kept = CallsignCheckSwitch.Set(false, AudioConfigSource?.Invoke(), RadioConfig.AppDataRoot);
        ShowCallsignCheckOffButton(false);
        _startCheckButton?.Focus();
        ScreenReaderOutput.Speak(
            Lexicon.Get(kept ? "settings.operators.callsign_check_turned_off" : "settings.operators.callsign_check_off_not_saved"),
            SpeechIntent.Queue, VerbosityLevel.Terse, subject: SpeechSubject.CallsignCheckTurnedOff);
    }
}
