using System;
using JJPortaudio;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// What the computer-microphone check says about what it hears, in one place
/// (#835, fix round 2026-10-28). Moved out of the Audio Devices window so that
/// Check your audio's first method, checking this computer's microphone, says
/// exactly what that window's check says: one probe (<see cref="MicProbe"/>),
/// one set of words (ruled by Noel 20:30).
/// </summary>
/// <remarks>
/// The words and their reasons are unchanged from the Audio Devices window;
/// the reasons that were on its private methods are kept here with them.
/// </remarks>
internal static class MicCheckWords
{
    /// <summary>
    /// Below this peak, in dBFS, what we are hearing is the interface's own
    /// electronics rather than a room. Chosen from a bench measurement, not
    /// from a table: an Audient interface with nothing plugged in read a
    /// steady -105 dBFS across a four-second check. A live microphone in a
    /// quiet room runs tens of decibels above that, so there is a wide gap
    /// to sit in and -75 sits in it.
    /// </summary>
    internal const float NoiseFloorDb = -75f;

    /// <summary>
    /// At or above this peak, in dBFS, the capture has clipped. Loudness
    /// figures are withheld from that reading: clipping raises RMS energy, so
    /// a loudness measured through it reads HIGHER than the true program
    /// loudness, and reporting a number the clipping corrupted would be worse
    /// than reporting none. -1 rather than exactly 0 because drivers hand back
    /// peaks a hair under full scale for a signal that already hit the rail.
    /// </summary>
    internal const float ClippedDb = -1f;

    /// <summary>
    /// The reading while the check runs. <paramref name="privacyBlocked"/> and
    /// its explanation are the caller's reading of the Windows privacy
    /// switches, which it takes once the device has given nothing for a second.
    /// </summary>
    internal static string Live(MicProbe.Reading r, bool privacyBlocked, string privacyExplanation)
    {
        if (!r.AnySound)
        {
            // Give the device a moment before passing judgement: some
            // interfaces hand over their first buffers late. Same key as the
            // line a check shows as it starts, because it is the same sentence.
            if (r.Seconds < 1.0) return Lexicon.Get("audio.device.mic_check_listening");
            if (privacyBlocked) return "No sound at all. " + privacyExplanation;
            // Not "quiet": exactly zero, every sample. A real microphone always
            // has a noise floor, so this is Windows handing us silence rather
            // than a quiet room, and saying so points at the right place to look.
            return "No sound at all — every sample is digital silence. The device is open, "
                + "but nothing is coming through it. Check that the microphone is not muted "
                + "in Windows, and that anything with its own mute button is unmuted.";
        }
        if (r.RecentPeakDb <= MicProbe.SilenceDb)
        {
            // The gated whole-check figure, not the 3 s window: the last three
            // seconds are the quiet this branch is reporting, while the
            // integrated figure still describes the speech before it.
            return Reading("Mic audio: quiet right now. Loudest so far:",
                r.HoldPeakDb, r.IntegratedLufs, withAdvice: false, advice: "");
        }
        // Same vocabulary the Audio Workshop and the Home fields use, reading
        // the same kind of number, so one level never gets two different
        // verdicts depending on where you asked. Loudness rides along: peak
        // answers "am I clipping", and loudness says when you have stopped
        // being too quiet.
        string text = Reading("Mic audio now:", r.RecentPeakDb, r.ShortTermLufs,
                withAdvice: false, advice: "")
            + $" Loudest so far {PeakText(r.HoldPeakDb)}.";
        // A fact, not a second verdict: an interface with nothing plugged into
        // it reads about -105 dBFS, real non-zero samples, so the
        // digital-silence test above does not fire.
        if (r.HoldPeakDb <= NoiseFloorDb)
        {
            text += " That is only the electrical noise floor — no sound is reaching the "
                + "microphone. Check that it is plugged in, and that any gain knob on the "
                + "interface is turned up.";
        }
        return text;
    }

    /// <summary>
    /// The summary when the check stops: nothing heard, only the noise floor,
    /// or the loudest reading with the advice that goes with its verdict.
    /// <paramref name="levelHere"/> is the Windows level control in the same
    /// window, which the advice then points at; null points at Windows Sound
    /// settings instead.
    /// </summary>
    internal static string Final(MicProbe.Reading final, WindowsMicLevel? levelHere)
    {
        if (!final.AnySound) return Lexicon.Get("audio.device.mic_check_nothing_heard");
        if (final.HoldPeakDb <= NoiseFloorDb)
            return Lexicon.Get("audio.device.mic_check_only_noise_floor", ("peak", PeakText(final.HoldPeakDb)));
        // The advice rides along whatever the output mode is: it is a
        // direction, and an operator who suppressed the coaching still needs to
        // be told which knob to move.
        return Reading(Lexicon.Get("audio.device.mic_check_loudest_heard"),
            final.HoldPeakDb, final.IntegratedLufs,
            withAdvice: true, advice: LevelAdvice(MicAudioReport.Verdict(final.HoldPeakDb), levelHere));
    }

    /// <summary>
    /// A reading, numbers first and coaching after, honouring the operator's
    /// verdict-output preference. Noel, 2026-08-13: "I'd read the level number
    /// first and then the coaching... it's easier if you're adjusting to hear
    /// a level and then coaching."
    /// </summary>
    internal static string Reading(string lead, float peakDb, float lufs, bool withAdvice, string advice)
    {
        var mode = MicAudioReport.VerdictMode;
        string numbers = PeakText(peakDb) + LoudnessPart(lufs, peakDb);
        string verdict = MicAudioReport.Verdict(peakDb);

        if (mode == MicVerdictOutputMode.Plain)
            return Lexicon.Get("audio.device.reading_plain",
                ("lead", lead), ("verdict", verdict)) + (withAdvice ? advice : "");

        if (mode == MicVerdictOutputMode.Numbers)
            return Lexicon.Get("audio.device.reading_numbers",
                ("lead", lead), ("numbers", numbers)) + (withAdvice ? advice : "");

        return Lexicon.Get("audio.device.reading_full",
            ("lead", lead), ("numbers", numbers), ("verdict", verdict))
            + (withAdvice ? advice : "");
    }

    /// <summary>
    /// A level, in words that cannot lie about which end of the scale it is
    /// at: at or under the floor there is no number worth saying; near the
    /// ceiling a decimal place is kept, because -0.3 and -0.0 are different
    /// situations there; elsewhere whole decibels (Noel, 2026-08-13, after
    /// both silence and full scale were spoken as zero).
    /// </summary>
    internal static string PeakText(float db)
    {
        if (db <= MicProbe.SilenceDb) return Lexicon.Get("audio.device.peak_nothing_at_all");
        if (db > -1f) return Lexicon.Get("audio.device.peak_dbfs", ("db", $"{db:F1}"));
        return Lexicon.Get("audio.device.peak_dbfs", ("db", $"{db:F0}"));
    }

    /// <summary>", loudness N LUFS", or empty when there is no honest figure: nothing gated in yet, or the capture clipped.</summary>
    internal static string LoudnessPart(float lufs, float peakDb)
    {
        if (lufs <= LufsMeter.Floor || peakDb >= ClippedDb) return "";
        return Lexicon.Get("audio.device.loudness_lufs", ("lufs", $"{lufs:F0}"));
    }

    /// <summary>
    /// What to do about an off-target verdict, and where. This check measures
    /// stage one, the level Windows captures at, so the remedy names the
    /// Windows input level, not the radio. Empty for "just right".
    /// </summary>
    internal static string LevelAdvice(string verdict, WindowsMicLevel? level)
    {
        // The poles come from the function that produced the verdict, so a
        // vocabulary change there cannot strand this branch on a stale string.
        string hot = MicAudioReport.Verdict(0f);
        string quiet = MicAudioReport.Verdict(-100f);
        if (verdict != hot && verdict != quiet) return "";

        if (verdict == hot)
        {
            // A boost left up is the likeliest culprit for a pinned reading,
            // and Windows Settings does not show it: name it first when it is up.
            float boost = 0f;
            try { boost = (level != null && level.HasBoost) ? level.BoostDb : 0f; }
            catch { /* device gone; the plain advice still stands */ }
            if (boost > 0f)
                return " " + Lexicon.Get("audio.device.advice_lower_boost", ("boost", $"{boost:F0}"));
            return " " + (level != null
                ? Lexicon.Get("audio.device.advice_lower_slider")
                : Lexicon.Get("audio.device.advice_lower_in_windows"));
        }

        return " " + (level != null
            ? Lexicon.Get("audio.device.advice_raise_slider")
            : Lexicon.Get("audio.device.advice_raise_in_windows"));
    }
}
