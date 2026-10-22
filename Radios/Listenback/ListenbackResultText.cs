using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.Listenback;

public static class ListenbackResultText
{
    public static string Ratio(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && double.IsFinite(ratio)
        ? ratio.ToString("F4", CultureInfo.CurrentCulture) : Lexicon.Get("audio.iq.unknown");

    // ── Round 14, #794: what a comparison says about the two takes' setup ──
    // DRAFT PROSE (#787): awaiting Noel. Every sentence below is assembled here
    // so a Radios test can read it the way NVDA will speak it.

    /// <summary>"the test tone", "your voice" or "the reference recording".</summary>
    public static string SourceWord(string source) => Lexicon.Get(source switch
    {
        TakeSetup.Tone => "audio.iq.source_tone",
        TakeSetup.Reference => "audio.iq.source_reference",
        _ => "audio.iq.source_voice"
    });

    /// <summary>
    /// The refusal for two takes of different kinds, in one full sentence that
    /// asks for another take (Noel, 2026-10-21 20:17: "if it's something that's
    /// not similar I'd ask for another take"). Falls back to the general
    /// "unlike or unaligned" sentence when the difference carries no source
    /// record, which cannot happen for a <c>unlike-takes:source</c> result.
    /// </summary>
    public static string UnlikeSource(AudioDifference difference)
    {
        var change = difference.SetupChanges?.FirstOrDefault(c => c.Key == "source");
        if (change == null) return Lexicon.Get("audio.iq.unlike_or_unaligned");
        return Lexicon.Get("audio.iq.unlike_source",
            ("previous", SourceWord(change.Previous)), ("current", SourceWord(change.Current)));
    }

    /// <summary>
    /// The setup differences a comparable result names in front of its numbers
    /// (Noel, 2026-10-21 20:18, "yep"), one sentence each, or "" when nothing
    /// changed. The source entry is never read here: a source difference is a
    /// refusal, spoken by <see cref="UnlikeSource"/>.
    /// </summary>
    public static string SetupChanges(AudioDifference difference) =>
        string.Join(" ", (difference.SetupChanges ?? System.Array.Empty<SetupChange>())
            .Where(c => c.Key != "source").Select(Sentence).Where(s => s.Length > 0));

    private static string Sentence(SetupChange change)
    {
        switch (change.Key)
        {
            case "tx-filter":
                string[] previous = change.Previous.Split(' '), current = change.Current.Split(' ');
                if (previous.Length != 2 || current.Length != 2) return "";
                return Lexicon.Get("audio.iq.setup_filter",
                    ("previous_low", previous[0]), ("previous_high", previous[1]),
                    ("current_low", current[0]), ("current_high", current[1]));
            case "rf-power":
                return Lexicon.Get("audio.iq.setup_power", ("previous", change.Previous), ("previous_unit", WattWord(change.Previous)),
                    ("current", change.Current), ("current_unit", WattWord(change.Current)));
            case "mic-profile":
                return Lexicon.Get("audio.iq.setup_profile",
                    ("previous", ProfileWord(change.Previous)), ("current", ProfileWord(change.Current)));
            case "mic-input":
                return Lexicon.Get("audio.iq.setup_mic_input", ("previous", change.Previous), ("current", change.Current));
            case "pc-microphone-device":
                return Lexicon.Get("audio.iq.setup_pc_microphone", ("previous", change.Previous), ("current", change.Current));
            default:
                return "";
        }
    }

    // "1 watt", never "1 watts" (Codex's round 14 check).
    private static string WattWord(string value) => Lexicon.Get(
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double watts) && watts == 1
            ? "audio.unit.watt" : "audio.unit.watts");

    private static string ProfileWord(string name) =>
        string.IsNullOrWhiteSpace(name) ? Lexicon.Get("audio.iq.no_mic_profile") : name;
}
