using Radios;
using Radios.Speech;

namespace JJFlexWpf;

/// <summary>
/// The one way every door changes the VOX send box (#835): Home's Transmission group,
/// Settings, and the command the Command Finder runs. Sets the preference,
/// saves it with the operator's audio settings at once, and says what it is now.
/// </summary>
/// <remarks>
/// The saved copy is the operator's audio settings (<see cref="AudioOutputConfig"/>),
/// not the per-radio config: the box is how this operator likes to send, and
/// the brief rules it app-wide rather than per radio. Saved on change, the way
/// the Audio Workshop's tone settings are, so a crash does not lose it.
/// </remarks>
internal static class VoxSendBoxControl
{
    /// <summary>Tick or untick the box. Announces only when <paramref name="announce"/>.</summary>
    internal static void Set(bool on, bool announce)
    {
        bool changed = VoxSendBox.Ticked != on;
        VoxSendBox.Ticked = on;
        var config = Dialogs.AudioWorkshopDialog.AudioConfigSource?.Invoke();
        bool keptBeforeLoad = false;
        if (config != null && config.VoxSendKeysRadio != on)
        {
            config.VoxSendKeysRadio = on;
            Dialogs.AudioWorkshopDialog.AudioConfigSave?.Invoke();
        }
        else if (config == null)
        {
            // No radio has loaded the operator's settings yet: keep the choice
            // in the root file, which the first connect loads.
            keptBeforeLoad = AudioOutputConfig.KeepChoiceBeforeLoad(c => c.VoxSendKeysRadio = on);
        }
        JJTrace.Tracing.TraceLine("VoxSendBox: " + (on ? "ticked" : "unticked")
            + (changed ? "" : " (unchanged)")
            + (config == null ? (keptBeforeLoad ? ", kept for the first connect" : ", not saved: no operator settings loaded") : ""),
            System.Diagnostics.TraceLevel.Info);
        if (!announce) return;
        // DRAFT PROSE (#835): awaiting Noel
        ToggleFeedback.Announce(Lexicon.Get("audio.send.box_label"), on,
            sentence: Lexicon.Get(on ? "audio.send.box_on" : "audio.send.box_off"),
            speak: said => ScreenReaderOutput.Speak(said, SpeechIntent.Interrupt, VerbosityLevel.Terse,
                subject: SpeechSubject.VoxSendBox));
    }

    /// <summary>Flip the box, for the command.</summary>
    internal static void Toggle() => Set(!VoxSendBox.Ticked, announce: true);
}
