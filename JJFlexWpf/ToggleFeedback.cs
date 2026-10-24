using System;
using Radios;

namespace JJFlexWpf;

/// <summary>One audible answer for an operator's on/off toggle.</summary>
internal static class ToggleFeedback
{
    internal static void Announce(string name, bool on, Action<string>? speak = null,
        string? sentence = null)
    {
        EarconPlayer.ToggleTone(on);
        string said = sentence ?? Lexicon.Get(on ? "audio.dsp.toggled_on" : "audio.dsp.toggled_off",
            ("label", name));
        if (speak != null) speak(said);
        else ScreenReaderOutput.Speak(said, VerbosityLevel.Terse, interrupt: true);
    }
}
