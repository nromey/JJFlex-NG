using System;
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
/// into the ceiling. The short version, and the framing everything here hangs
/// off: <b>peak tells you whether you are safe; loudness tells you whether you
/// will be heard.</b> help/md/audio-two-numbers.md is the authority on that
/// explanation and on the vocabulary here; this file must stay consistent with
/// it rather than paraphrase it.
///
/// The verdicts talk like a person, on purpose. Setting mic levels is a
/// stressful loop — someone worried about sounding bad to strangers on the
/// air — so most verdicts end by inviting the next attempt ("...and try
/// again") rather than passing judgement. Two mechanical rules keep the warmth
/// from costing usability: every variant of a band opens with the same short
/// identifying token ("Hot." / "Good." / "Quiet."), so a fast screen-reader
/// listener gets the whole verdict from the first word and can bail out; and
/// the wording after the token rotates round-robin, so a tuning session never
/// hears one line eight times running. Round-robin, not random: random can
/// repeat itself, and nondeterministic speech makes a bug report
/// irreproducible.
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

    // --- Peak bands (dBFS) -----------------------------------------------
    // Seven bands where there used to be three. The old middle band ran from
    // -30 all the way to -6 — a twenty-four dB range that all reported the
    // same words, so an operator 20 dB shy of ideal was told they were fine —
    // and the old top band could not tell "hot" (peaks near the ceiling, ease
    // off) from "pegged" (samples being destroyed right now). Different
    // problems, different urgency, now different bands.
    //
    // Thresholds are first-pass and tunable, like everything here.
    private const float PeakPeggedDb = -1f;        // above: clipping, audio being lost NOW
    private const float PeakHotDb = -3f;           // above: peaks crowding the ceiling
    private const float PeakSweetFloorDb = -12f;   // down to here: the sweet spot
    private const float PeakQuietFloorDb = -20f;   // down to here: a nudge up wanted
    private const float PeakVeryQuietFloorDb = -30f; // down to here: a real boost wanted
    // At or below this there is no audio to judge — it is an electrical noise
    // floor or a meter sentinel, not a voice. (A dead interface with nothing
    // plugged in measures about -105 dBFS on the bench; a live microphone in
    // a quiet room sits far above that.) Saying "bring it up" about a number
    // like this would be true and useless; saying "I hear nothing" is the
    // honest sentence.
    private const float PeakNothingDb = -100f;

    // --- Loudness bands (LUFS) -------------------------------------------
    // HONESTY NOTE, do not let these numbers harden into fact: there is no
    // established LUFS standard for amateur SSB transmit audio. LUFS comes
    // from broadcast loudness normalisation — EBU R128 targets -23, streaming
    // sits around -16 to -14 — and nobody has written down what a ham's
    // transmit audio should read. The -14 to -20 pocket below is broadcast
    // practice adapted by reasoning: it leaves the compander and speech
    // processor downstream something to work with. First-pass and tunable.
    private const float LoudnessPlentyLufs = -14f;     // above: plenty loud
    private const float LoudnessPocketFloorLufs = -20f; // down to here: the pocket
    private const float LoudnessThinFloorLufs = -30f;   // down to here: a bit thin

    private enum PeakBand { Pegged, Hot, Sweet, Quiet, VeryQuiet, Faint, Nothing }
    private enum LoudnessBand { Plenty, Pocket, Thin, RealThin }

    private static PeakBand PeakBandOf(float db)
    {
        if (db <= PeakNothingDb) return PeakBand.Nothing;
        if (db > PeakPeggedDb) return PeakBand.Pegged;
        if (db > PeakHotDb) return PeakBand.Hot;
        if (db > PeakSweetFloorDb) return PeakBand.Sweet;
        if (db > PeakQuietFloorDb) return PeakBand.Quiet;
        if (db > PeakVeryQuietFloorDb) return PeakBand.VeryQuiet;
        return PeakBand.Faint;
    }

    private static LoudnessBand LoudnessBandOf(float lufs)
    {
        if (lufs > LoudnessPlentyLufs) return LoudnessBand.Plenty;
        if (lufs > LoudnessPocketFloorLufs) return LoudnessBand.Pocket;
        if (lufs > LoudnessThinFloorLufs) return LoudnessBand.Thin;
        return LoudnessBand.RealThin;
    }

    // --- Variant rotation ------------------------------------------------
    // One rotor per band. Each call serves the current variant; the index
    // advances only when the previous call for that band was more than a
    // moment ago. That single rule serves two callers with opposite needs:
    // the polled reading fields (Audio Workshop, Home expander — 2 Hz while
    // visible) re-compose constantly and must NOT churn, because both fields
    // deliberately assign text only on change so a steady reading never
    // resets a screen reader's review cursor; while the deliberate checks
    // (Ctrl+J K, Alt+Shift+S, the workshop's unkey report) arrive seconds
    // apart and SHOULD rotate, so a tuning session gets a fresh line each
    // pass. Polls land inside the hold window and see a stable sentence;
    // human-paced checks land outside it and advance the rotation.
    //
    // Known, accepted residue: while a polled surface is actively refreshing
    // a band, a spoken check of that same band lands inside the hold window
    // too and repeats the held variant. The frozen signatures leave no way
    // for a caller to say "this one is deliberate", and stable text under
    // the review cursor is the accessibility invariant that must win.
    //
    // The index does not persist across runs, and does not need to.
    private const double VariantHoldSeconds = 1.5;

    private sealed class Rotor
    {
        private readonly string[] _variants;
        private int _index = -1;
        private DateTime _lastUse = DateTime.MinValue;

        public Rotor(params string[] variants) => _variants = variants;

        public string Next()
        {
            lock (this)
            {
                DateTime now = DateTime.UtcNow;
                if (_index < 0 || (now - _lastUse).TotalSeconds >= VariantHoldSeconds)
                    _index = (_index + 1) % _variants.Length;
                _lastUse = now;
                return _variants[_index];
            }
        }
    }

    // Variant text rules, load-bearing:
    //  - No trailing period. Callers embed these mid-sentence and append
    //    their own punctuation (", peak -8 dBFS." and the like); internal
    //    sentence breaks are fine, a trailing one doubles up.
    //  - The band token is the first word(s) and is IDENTICAL across a
    //    band's variants. It is the verdict; everything after it is warmth.
    //  - "your levels" for the computer-side gain, per Noel — never
    //    "capture level", never jargon that makes the operator guess which
    //    knob is meant.
    //  - Bands that want another attempt end on the invitation ("...and try
    //    again"), because tuning IS a loop and the phrasing should make the
    //    next pass feel normal rather than like repeated failure. The sweet
    //    spot ends on the compliment — that is where the loop stops.
    private static readonly Rotor[] PeakRotors =
    {
        // Pegged: 0 to -1 dBFS. Samples are being destroyed right now — the
        // one verdict where the first word must carry the alarm.
        new Rotor(
            "Clipping. Whoa, you're pegging the meter — back it way off and try again",
            "Clipping. The top of your audio is getting sliced right off. Take it way down and give it another go",
            "Clipping. Every peak is slamming the ceiling. Way down on your levels, then try again",
            "Clipping. That's over the top, and the radio can't use what it can't hold. Back way off and have another run"),

        // Hot: -1 to -3 dBFS. Peaks crowding the ceiling — ease off, nothing
        // lost yet. Noel's own voice, kept: "whew, you're coming in hot".
        new Rotor(
            "Hot. Whew, you're coming in hot. Back it off a bit and try again",
            "Hot. Whew — coming in hot, buddy. Ease it back a touch and give it another go",
            "Hot. You're right up against the ceiling. A small step down and try it again",
            "Hot. Close to clipping, but nothing's broken yet. Back off a hair and take another pass"),

        // Sweet: -3 to -12 dBFS.
        new Rotor(
            "Good. That's the sweet spot, right there",
            "Good. Right in the sweet spot — leave that knob right where it is",
            "Good. That's the one. Nicely done",
            "Good. Your levels are sitting right where you want them"),

        // Quiet: -12 to -20 dBFS. First variant is Noel's, verbatim.
        new Rotor(
            "Quiet. Bring your levels up just a bit, you're still kind of quiet",
            "Quiet. A little more and you're there. Bring it up a touch and try again",
            "Quiet. You're close — just a nudge up and give it another go",
            "Quiet. Almost there. A bit more level and try it again"),

        // Very quiet: -20 to -30 dBFS.
        new Rotor(
            "Very quiet. You're pretty quiet — bring it up a fair bit and try again",
            "Very quiet. There's a lot of room above you. Bring your levels up a good bit and give it another go",
            "Very quiet. I can hear you, but not much of you. A solid boost and try again"),

        // Faint: -30 down to the nothing-heard floor.
        new Rotor(
            "Faint. I can barely hear you at all. Bring it way up",
            "Faint. There's barely anything coming through. Bring your levels way up and try again",
            "Faint. Just a whisper of you is arriving. Way up on the level, then give it another shot"),

        // Nothing: at or below the floor — no audio to judge, only an
        // electrical floor or a sentinel. All callers guard before asking,
        // so this mostly backstops the mic check's "loudest so far" path
        // when nothing real has arrived yet.
        new Rotor(
            "Nothing. I'm not hearing anything at all",
            "Nothing. I'm not hearing anything from this one",
            "Nothing. Dead silence on my end — no audio is arriving"),
    };

    // Loudness verdicts ride behind the peak verdict, never alone, and only
    // once peak is safe — so they answer the second question ("will you be
    // heard?") after the first one ("are you safe?") has a good answer. Their
    // tokens deliberately come from a different word-family than the peak
    // tokens ("thin", "in the pocket" — the pocket being the same phrase the
    // help page uses) so fast listeners never mistake which of the two
    // measures is talking.
    private static readonly Rotor[] LoudnessRotors =
    {
        // Plenty: above -14 LUFS.
        new Rotor(
            "Plenty loud. Maybe leave the processor a little room to work",
            "Plenty loud. You're filling the channel — nobody will be asking for repeats",
            "Plenty loud. If anything, you could spare the processor a little headroom"),

        // Pocket: -14 to -20 LUFS.
        new Rotor(
            "In the pocket. Loudness is right where you want it",
            "In the pocket. That's an easy, comfortable copy",
            "In the pocket. Loudness-wise, this is the spot"),

        // Thin: -20 to -30 LUFS. Descriptive on purpose: when peaks are
        // already healthy, "turn it up" can be exactly the wrong advice, so
        // these observe rather than prescribe a knob.
        new Rotor(
            "A bit thin. Readable, just not much weight behind it",
            "A bit thin. You're getting through, but you'll sound small next to the big signals",
            "A bit thin. Copyable, but there's not a lot of body in it"),

        // Real thin: below -30 LUFS.
        new Rotor(
            "Real thin. Real quiet overall — not much average power is getting out",
            "Real thin. Whatever the peaks say, the average is way down there",
            "Real thin. That'll be a tough copy in any noise at all"),
    };

    /// <summary>
    /// Plain-language mic-drive verdict from the SC_MIC peak-hold (dBFS).
    /// Seven bands; every variant of a band starts with the same identifying
    /// token, and the flavour after it rotates. Thresholds are first-pass and
    /// tunable. Returns no trailing period — callers embed this mid-sentence
    /// and supply their own punctuation.
    /// </summary>
    internal static string Verdict(float scMicPeakDb)
        => PeakRotors[(int)PeakBandOf(scMicPeakDb)].Next();

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
        PeakBand band = PeakBandOf(peakDb);

        // Nothing heard: the peak down here is a sentinel or an electrical
        // floor, not a measurement of audio, so there is no honest figure to
        // attach — every output mode gets the plain sentence.
        if (band == PeakBand.Nothing)
            return PeakRotors[(int)band].Next();

        string verdict = PeakRotors[(int)band].Next();

        // Never report loudness while the signal is clipping — and not merely
        // because it is redundant. Clipping converts peaks into harmonic
        // content and raises RMS energy, so K-weighted loudness measured at
        // full scale reads HIGHER than the program's true loudness; it would
        // be a figure the clipping itself corrupted. This is situational
        // logic and composes with the VerdictMode preference below — they are
        // not alternatives.
        bool clipping = band == PeakBand.Pegged;

        // LUFS exists only for the PC-audio path. An analog mic at the radio
        // produces no PC-side samples at all, so the figure is absent rather
        // than wrong, and the reading simply carries one number instead of two.
        float lufs = clipping ? JJPortaudio.LufsMeter.Floor : LoudnessFigure(rig, live);
        bool haveLoudness = lufs > JJPortaudio.LufsMeter.Floor;

        // The loudness verdict speaks only once peak is safe. While the
        // operator is being told to back off, the peak is the thing that
        // changes what they do next; once peaks are out of danger, whether
        // they will be HEARD becomes the open question and loudness answers
        // it.
        bool peakSafe = band != PeakBand.Pegged && band != PeakBand.Hot;
        if (haveLoudness && peakSafe)
            verdict += ". " + LoudnessRotors[(int)LoudnessBandOf(lufs)].Next();

        string numbers = $"peak {peakDb:F0} dBFS";
        if (haveLoudness)
            numbers += $", loudness {lufs:F0} LUFS";

        return VerdictMode switch
        {
            MicVerdictOutputMode.Plain => verdict,
            MicVerdictOutputMode.Numbers => numbers,
            _ => $"{verdict}. {char.ToUpperInvariant(numbers[0])}{numbers[1..]}",
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
