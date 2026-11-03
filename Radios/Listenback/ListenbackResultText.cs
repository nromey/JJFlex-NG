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
    /// <remarks>
    /// Since Unified takes (design section 7): when the changes span more than
    /// one setting group, a DRAFT warning follows them, because the numbers
    /// can then be put down to no single change (Noel, 2026-11-02: "warn when
    /// more than one thing changed"). A switch and its level are one group;
    /// the mic input and the computer's microphone are one group.
    /// </remarks>
    public static string SetupChanges(AudioDifference difference)
    {
        var changes = (difference.SetupChanges ?? System.Array.Empty<SetupChange>())
            .Where(c => c.Key != "source" && c.Key != "kind").ToList();
        var sentences = changes.Select(Sentence).Where(s => s.Length > 0).ToList();
        if (changes.Select(c => TakeSetup.GroupOf(c.Key)).Distinct().Count() > 1)
            sentences.Add(Lexicon.Get("audio.takes.setup_several"));
        return string.Join(" ", sentences);
    }

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
            // Unified takes, design section 7. DRAFT PROSE: awaiting Noel.
            case "mic-gain":
                return Lexicon.Get("audio.takes.setup_mic_gain", ("previous", change.Previous), ("current", change.Current));
            case "mic-boost":
                return Lexicon.Get("audio.takes.setup_mic_boost", ("previous", OnOffWord(change.Previous)), ("current", OnOffWord(change.Current)));
            case "mic-bias":
                return Lexicon.Get("audio.takes.setup_mic_bias", ("previous", OnOffWord(change.Previous)), ("current", OnOffWord(change.Current)));
            case TakeSetup.GroupCompander:
            {
                var (previousState, currentState) = SwitchPhrases(change, level => level);
                return Lexicon.Get("audio.takes.setup_compander", ("previous", previousState), ("current", currentState));
            }
            case TakeSetup.GroupProcessor:
            {
                var (previousState, currentState) = SwitchPhrases(change, ProcessorLevelWord);
                return Lexicon.Get("audio.takes.setup_processor", ("previous", previousState), ("current", currentState));
            }
            case TakeSetup.GroupEqualizer:
                return EqualizerSentence(change);
            case "radio":
                return Lexicon.Get("audio.takes.setup_radio", ("previous", change.Previous), ("current", change.Current));
            default:
                return "";
        }
    }

    private static string OnOffWord(string value) =>
        Lexicon.Get(TakeSetup.SwitchOn(value) ? "audio.takes.state_on" : "audio.takes.state_off");

    /// <summary>The processor's level as the radio names it: NOR, DX or DXX.</summary>
    private static string ProcessorLevelWord(string level) => level switch
    {
        "0" => "NOR", "1" => "DX", "2" => "DXX", _ => level
    };

    /// <summary>
    /// "on at 50" or "off" for each side of a switch-and-level change; when
    /// both sides are off, the stored level is said ("off, set to 40"), since
    /// a stored setting that changed is still a change (design section 7).
    /// </summary>
    private static (string Previous, string Current) SwitchPhrases(SetupChange change, System.Func<string, string> levelWord)
    {
        string[] p = change.Previous.Split(' '), c = change.Current.Split(' ');
        bool bothOff = p[0] == "off" && c[0] == "off";
        string Phrase(string[] value) => value[0] == "on"
            ? Lexicon.Get("audio.takes.state_on_level", ("level", levelWord(value.Length > 1 ? value[1] : "")))
            : bothOff ? Lexicon.Get("audio.takes.state_off_level", ("level", levelWord(value.Length > 1 ? value[1] : "")))
            : Lexicon.Get("audio.takes.state_off");
        return (Phrase(p), Phrase(c));
    }

    private static string EqualizerSentence(SetupChange change)
    {
        if (TakeSetup.Equalizer(change.Previous) is not { } previous || TakeSetup.Equalizer(change.Current) is not { } current)
            return "";
        var sentences = new List<string>();
        if (previous.Enabled != current.Enabled)
            sentences.Add(Lexicon.Get("audio.takes.setup_eq_switched",
                ("previous", Lexicon.Get(previous.Enabled ? "audio.takes.state_on" : "audio.takes.state_off")),
                ("current", Lexicon.Get(current.Enabled ? "audio.takes.state_on" : "audio.takes.state_off"))));
        var bands = new List<string>();
        for (int i = 0; i < previous.Bands.Length; i++)
            if (previous.Bands[i] != current.Bands[i])
                bands.Add(Lexicon.Get("audio.takes.eq_band", ("hz", FlexBase.EqBandHz[i].ToString(CultureInfo.InvariantCulture)),
                    ("previous", previous.Bands[i].ToString("+0;-0;0", CultureInfo.InvariantCulture)),
                    ("current", current.Bands[i].ToString("+0;-0;0", CultureInfo.InvariantCulture))));
        if (bands.Count > 0)
            sentences.Add(Lexicon.Get(!previous.Enabled && !current.Enabled ? "audio.takes.setup_eq_bands_off" : "audio.takes.setup_eq_bands",
                ("bands", JoinList(bands))));
        return string.Join(" ", sentences);
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " " + Lexicon.Get("audio.takes.and") + " " + items[^1]
    };

    // ── Unified takes, the audio road's comparison sentences (design sections 2 and 3) ──
    // DRAFT PROSE: awaiting Noel, except the two mixed-kind sentences, whose
    // structure Noel RULED 2026-11-02 17:31 ("yep that's fine").

    /// <summary>
    /// The whole spoken result of a comparison on the audio road, assembled:
    /// what was compared and why anything was left out, then the setup
    /// differences, then the numbers. For a refusal, one sentence that says
    /// why and what to do.
    /// </summary>
    public static string AudioRoadSentence(AudioDifference difference)
    {
        string first = TakeKindWords.Phrase(difference.FirstKind ?? ""), second = TakeKindWords.Phrase(difference.SecondKind ?? "");
        bool mixed = difference.FirstKind != difference.SecondKind;
        if (!difference.Comparable)
        {
            if (difference.Reason == "unlike-takes:source") return UnlikeSource(difference);
            if (mixed && difference.Reason is "unlike-takes:kind" or "too-little-audio")
                return Lexicon.Get("audio.takes.mixed_nothing_fair", ("first", first), ("second", second));
            string refusal = difference.Reason switch
            {
                "unlike-takes:kind" => "audio.takes.refused_kind",
                "unlike-takes:source-unknown" => "audio.takes.refused_source_unknown",
                "unlike-takes:mode" => "audio.takes.refused_mode",
                "unlike-takes:tx-antenna" => "audio.takes.refused_tx_antenna",
                "unlike-takes:receive-antenna" => "audio.takes.refused_receive_antenna",
                "unlike-takes:serial" => "audio.takes.refused_radio",
                "too-little-audio" => "audio.takes.refused_too_little",
                _ => "audio.takes.refused_unreadable"
            };
            return Lexicon.Get(refusal);
        }
        string intro = mixed ? Lexicon.Get("audio.takes.mixed_tone_only", ("first", first), ("second", second))
            : difference.LevelCompared ? ""
            : Lexicon.Get(LevelWithheldKey(difference.LevelWithheld));
        bool toneChanged = System.Math.Abs(difference.TiltDb) >= 0.5;
        string numbers = difference.LevelCompared
            ? Lexicon.Get(toneChanged ? "audio.takes.level_and_tone" : "audio.takes.level_same_tone",
                ("level_word", Lexicon.Get(difference.LevelDb >= 0 ? "audio.iq.louder" : "audio.iq.quieter")),
                ("level", System.Math.Abs(difference.LevelDb).ToString("F1")),
                ("brightness_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.brighter" : "audio.iq.darker")),
                ("high_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.stronger" : "audio.iq.weaker")),
                ("tilt", System.Math.Abs(difference.TiltDb).ToString("F1")))
            : toneChanged
                ? Lexicon.Get("audio.takes.tone_changed",
                    ("brightness_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.brighter" : "audio.iq.darker")),
                    ("high_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.stronger" : "audio.iq.weaker")),
                    ("tilt", System.Math.Abs(difference.TiltDb).ToString("F1")))
                : Lexicon.Get("audio.takes.tone_same");
        string changes = SetupChanges(difference);
        return string.Join(" ", new[] { intro, changes, numbers }.Where(s => s.Length > 0));
    }

    private static string LevelWithheldKey(string reason) => reason switch
    {
        "transport" => "audio.takes.level_withheld_transport",
        "slice-volume" => "audio.takes.level_withheld_slice_volume",
        "receive-chain" => "audio.takes.level_withheld_receive_chain",
        _ => "audio.takes.level_withheld_boundary"
    };

    // "1 watt", never "1 watts" (Codex's round 14 check).
    private static string WattWord(string value) => Lexicon.Get(
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double watts) && watts == 1
            ? "audio.unit.watt" : "audio.unit.watts");

    private static string ProfileWord(string name) =>
        string.IsNullOrWhiteSpace(name) ? Lexicon.Get("audio.iq.no_mic_profile") : name;
}
