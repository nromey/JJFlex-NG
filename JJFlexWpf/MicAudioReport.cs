using Radios;

namespace JJFlexWpf;

/// <summary>
/// The one place that decides how a mic-audio reading reads. Four surfaces ask
/// this question — the JJ key's mic check (Ctrl+J, K), the transmit status key
/// (Alt+Shift+S), the Audio Workshop's reading field, and the Home audio
/// expander's — and every one of them used to compose its own sentence from
/// the same two ingredients. They agreed by inspection rather than by
/// construction, which is a thing that stays true right up until it doesn't.
///
/// Two figures, deliberately: dBFS is a peak measure and LUFS is a gated,
/// K-weighted loudness measure. They answer different questions and go wrong
/// in different directions — healthy peaks can hide a level that is too quiet
/// to hear, and a comfortable loudness can hide the odd consonant slamming
/// into the ceiling. help/md/audio-two-numbers.md is the authority on that
/// explanation and on the vocabulary here; this file must stay consistent with
/// it rather than paraphrase it.
/// </summary>
internal static class MicAudioReport
{
    /// <summary>
    /// The operator's verdict-output preference (Settings, Notifications).
    /// Pushed here by <see cref="AudioOutputConfig.Apply"/>, the same way that
    /// method pushes verbosity into ScreenReaderOutput and volume into
    /// FlexBase — so every surface reads one value with no plumbing, and a
    /// setting saved in the dialog is live everywhere at once.
    /// </summary>
    internal static MicVerdictOutputMode VerdictMode { get; set; } = MicVerdictOutputMode.Both;

    // --- Noise-floor reporting thresholds --------------------------------
    // Both must be met before the observation is worth an operator's time.
    //
    // The gap does the actual work: a voice standing less than 20 dB clear of
    // its own room is audibly sitting in that room, whatever the level says.
    // The floor is the guard that keeps a quiet shack quiet — an operator
    // running low mic gain in a silent room can show a narrow gap simply
    // because everything is small, and telling them about their noise floor
    // every transmit would be worse than saying nothing. Below -55 LUFS the
    // room is not what is wrong; the level verdict already has that covered.
    private const float NoiseFloorAudibleLufs = -55f;
    private const float NoiseGapTightLu = 20f;

    /// <summary>
    /// Plain-language mic-drive verdict from the SC_MIC peak-hold (dBFS).
    /// Thresholds are first-pass and tunable.
    /// </summary>
    internal static string Verdict(float scMicPeakDb)
    {
        if (scMicPeakDb < -30f) return "turn it up";
        if (scMicPeakDb > -6f) return "coming in hot";
        return "just right";
    }

    /// <summary>
    /// The body of a reading — verdict and figures, no lead-in. Callers supply
    /// their own lead ("Mic audio now:", "Your mic audio was") so the phrasing
    /// each surface earned is preserved while the numbers come from here.
    /// </summary>
    /// <param name="rig">radio, for the LUFS figures; null yields dBFS only</param>
    /// <param name="peakDb">the SC_MIC peak this reading is about, dBFS</param>
    /// <param name="live">true mid-transmit (short-term loudness), false for a
    /// finished transmit (the gated integrated figure)</param>
    internal static string Body(FlexBase? rig, float peakDb, bool live)
    {
        string verdict = Verdict(peakDb);
        string numbers = $"peak {peakDb:F0} dBFS";

        // LUFS exists only for the PC-audio path. An analog mic at the radio
        // produces no PC-side samples at all, so the figure is absent rather
        // than wrong, and the reading simply carries one number instead of two.
        float lufs = LoudnessFigure(rig, live);
        if (lufs > JJPortaudio.LufsMeter.Floor)
            numbers += $", loudness {lufs:F0} LUFS";

        return VerdictMode switch
        {
            MicVerdictOutputMode.Plain => verdict,
            MicVerdictOutputMode.Numbers => numbers,
            _ => $"{verdict}, {numbers}",
        };
    }

    /// <summary>
    /// The loudness figure for this reading, or <see cref="JJPortaudio.LufsMeter.Floor"/>
    /// when there isn't an honest one. Short-term (3 s) while transmitting so
    /// riding mic gain moves it; the gated whole-transmit figure afterwards,
    /// which is the one that ignores the gaps between words.
    /// </summary>
    private static float LoudnessFigure(FlexBase? rig, bool live)
    {
        if (rig == null) return JJPortaudio.LufsMeter.Floor;
        if (live)
            return rig.TxLufsAvailable ? rig.TxLufsShortTerm : JJPortaudio.LufsMeter.Floor;
        return rig.TxLufsSampleAvailable ? rig.TxLufsIntegrated : JJPortaudio.LufsMeter.Floor;
    }

    /// <summary>
    /// An observation about the room, or null when there is nothing worth
    /// saying — which is most of the time, and is the point.
    ///
    /// This is ADDED to a reading, never substituted for one. A high noise
    /// floor does not make a good level a bad level, and the wording must not
    /// invite the operator to fix it with gain: turning up a noisy signal
    /// raises the noise by exactly as much as the voice.
    /// </summary>
    internal static string? NoiseNote(FlexBase? rig)
    {
        if (rig == null) return null;

        // A test tone is continuous by design — it has no gaps for the same
        // reason a fan has none, and it would trip this on every check. The
        // operator armed it; they know what it is.
        if (rig.TxToneEngaged) return null;
        if (!rig.TxLufsSampleAvailable) return null;

        var profile = rig.TxLoudnessProfile;
        if (!profile.IsValid) return null;
        if (profile.NoiseFloorLufs <= NoiseFloorAudibleLufs) return null;
        if (profile.SpeechToNoiseLu >= NoiseGapTightLu) return null;

        return VerdictMode == MicVerdictOutputMode.Plain
            ? "Steady background noise, close behind your voice. Turning up would raise the room too."
            : $"Steady background noise, about {profile.SpeechToNoiseLu:F0} dB under your voice. "
              + "Turning up would raise the room too.";
    }

    /// <summary>
    /// A whole reading — lead, body, and the room observation when there is
    /// one. The shape every surface shows or speaks.
    /// </summary>
    internal static string Compose(FlexBase? rig, string lead, float peakDb, bool live)
    {
        string text = $"{lead} {Body(rig, peakDb, live)}";
        string? note = NoiseNote(rig);
        return note == null ? text : $"{text}. {note}";
    }
}
