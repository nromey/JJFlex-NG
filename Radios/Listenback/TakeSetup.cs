using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Radios.Listenback;

/// <summary>
/// One recorded setup fact that differed between the two takes of a
/// comparison, so the result can NAME the difference in front of its numbers
/// (Noel, 2026-10-21 20:18: a different mic or profile is compared, "and says
/// what changed"). <see cref="Previous"/> and <see cref="Current"/> are the
/// header's own values, untranslated; the lexicon sentence that reads them is
/// chosen by <see cref="Key"/>. The one composite is the transmit filter,
/// keyed <c>tx-filter</c>, whose two values are "low high" with a space
/// between, because one sentence names both edges of both takes.
/// </summary>
public sealed record SetupChange(string Key, string Previous, string Current);

/// <summary>
/// What a take's header says about the setup a comparison cares about
/// (#794's header-and-gate half, listen-back round 14).
/// </summary>
/// <remarks>
/// <para>There is no new "source" scheme here. What fed the transmitter is
/// read from the records FlexBase already writes: <c>tone-at-keying</c> at
/// every keying edge (older takes: <c>tone-engaged</c> in the provenance
/// block), and since round 14 <c>reference-at-keying</c> beside it (older
/// takes: nothing, which reads as "not the reference"). <see cref="Source"/>
/// is the one reader of those facts; <see cref="TakeAnalysis.CheckTone"/>
/// reads the tone through it too.</para>
///
/// <para>Found on the 2026-10-21 bench: the first voice take of an evening
/// was auto-compared with an hour-old tone take and reported "quieter by 7.3
/// dB ... brighter ... by 12.4 dB". That number means nothing, and
/// <see cref="AudioDifferential.Compatibility"/> had no way to know, because
/// it compared a fixed list of radio settings and never looked at the source.</para>
/// </remarks>
public static class TakeSetup
{
    public const string Tone = "tone";
    public const string Voice = "voice";
    public const string Reference = "reference";

    /// <summary>
    /// The tone state at the keying edge: the last <c>tone-at-keying</c>
    /// event, else the provenance block's <c>tone-engaged</c> and
    /// <c>tone-hz</c>, which were written at take start and are all an older
    /// take has.
    /// </summary>
    public static bool ToneAtKeying(TakeInspection take, out double hz)
    {
        bool engaged = string.Equals(take.Get("tone-engaged"), "True", StringComparison.OrdinalIgnoreCase);
        double.TryParse(take.Get("tone-hz"), NumberStyles.Float, CultureInfo.InvariantCulture, out hz);
        var keyedTone = take.Fields.LastOrDefault(p => p.Key == "tone-at-keying").Value;
        if (keyedTone != null)
        {
            using var json = JsonDocument.Parse(keyedTone);
            engaged = json.RootElement.GetProperty("Engaged").GetBoolean();
            hz = json.RootElement.GetProperty("Frequency").GetDouble();
        }
        return engaged;
    }

    /// <summary>
    /// Whether Reference Audio was feeding the transmitter at the keying edge:
    /// the last <c>reference-at-keying</c> event, else the provenance block's
    /// <c>reference-audio</c>. A take with neither (made before round 14) is
    /// read as NOT the reference, so an old voice take still compares with a
    /// new one.
    /// </summary>
    public static bool ReferenceAtKeying(TakeInspection take)
    {
        var keyed = take.Fields.LastOrDefault(p => p.Key == "reference-at-keying").Value;
        if (keyed != null)
        {
            using var json = JsonDocument.Parse(keyed);
            return json.RootElement.GetProperty("Engaged").GetBoolean();
        }
        return string.Equals(take.Get("reference-audio"), "True", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="Tone"/>, <see cref="Reference"/> or <see cref="Voice"/>.
    /// The tone wins when both read engaged, because arming the reference is
    /// refused while the tone is armed, so that state cannot arise from the
    /// dialog and a header claiming it is read the conservative way.
    /// </summary>
    public static string Source(TakeInspection take)
    {
        if (ToneAtKeying(take, out _)) return Tone;
        return ReferenceAtKeying(take) ? Reference : Voice;
    }

    // ── The header contract (Unified takes, design section 7) ──
    //
    // ONE list of the transmit-chain facts a take records and a comparison
    // names. The playback take's snapshot (Snapshot) writes exactly these keys,
    // Changes reads exactly these keys, and ListenbackResultText names every
    // group, so a setting cannot be recorded and never named, or named and
    // never recorded. The spellings are the ones the off-air header has always
    // written (FlexBase.StartListenbackAsync), so nothing old is reinterpreted
    // and AudioDifferential.Compatibility is not loosened.
    //
    // A NEW TRANSMIT-CHAIN SETTING (transmit noise reduction when it ships, any
    // new processing) MUST BE ADDED HERE, with its sentence, in the same
    // change. Until it is, a comparison cannot say "only X changed", because
    // the setting's effect would be silently attributed to something else.
    // UnifiedTakesContractTests fails for a registered field without stored
    // evidence and an assembled sentence.

    /// <summary>
    /// One registered setup fact: its header key, the group it is named in
    /// (a group is one sentence: the compander's switch and level are one
    /// group), and whether its values compare as numbers.
    /// </summary>
    public sealed record SetupField(string Key, string Group, bool Numeric);

    public const string GroupMicrophone = "microphone";
    public const string GroupProfile = "mic-profile";
    public const string GroupMicGain = "mic-gain";
    public const string GroupMicBoost = "mic-boost";
    public const string GroupMicBias = "mic-bias";
    public const string GroupCompander = "compander";
    public const string GroupProcessor = "processor";
    public const string GroupEqualizer = "tx-equalizer";
    public const string GroupFilter = "tx-filter";
    public const string GroupPower = "rf-power";

    /// <summary>
    /// The registered fields, in the order a comparison names them: the
    /// microphone first (Noel, 2026-11-02, Q2: "microphone is more important
    /// than radio"), then the profile, then processing and EQ, then the
    /// filter and the power. Until microphones are named, the radio's mic
    /// input and the computer's microphone device stand in for the microphone
    /// (D5), and they are named as what they are, never as a microphone.
    /// </summary>
    public static IReadOnlyList<SetupField> Registered { get; } = new[]
    {
        new SetupField("mic-input", GroupMicrophone, false),
        new SetupField("pc-microphone-device", GroupMicrophone, false),
        new SetupField("mic-profile", GroupProfile, false),
        new SetupField("mic-gain", GroupMicGain, true),
        new SetupField("mic-boost", GroupMicBoost, false),
        new SetupField("mic-bias", GroupMicBias, false),
        new SetupField("compander", GroupCompander, false),
        new SetupField("compander-level", GroupCompander, true),
        new SetupField("processor", GroupProcessor, false),
        new SetupField("processor-level", GroupProcessor, true),
        new SetupField("tx-equalizer", GroupEqualizer, false),
        new SetupField("tx-filter-low", GroupFilter, true),
        new SetupField("tx-filter-high", GroupFilter, true),
        new SetupField("rf-power", GroupPower, true),
    };

    /// <summary>What a playback take's snapshot is taken from.</summary>
    /// <param name="Chain">The transmit chain, captured by <see cref="AudioChainPreset.CaptureFrom"/>, which never changes the radio.</param>
    /// <param name="MicProfile">The radio's selected mic profile, "" when none.</param>
    /// <param name="RfPower">The RF power setting.</param>
    /// <param name="PcMicrophoneDevice">The computer's microphone device feeding PC audio, or "unavailable".</param>
    public sealed record SetupSource(AudioChainPreset Chain, string MicProfile, int RfPower, string PcMicrophoneDevice);

    /// <summary>
    /// The registered facts for a NEW take, in the off-air header's own
    /// spellings. Reads a captured chain; never calls <c>ApplyTo</c> and never
    /// touches the radio. An EQ the radio has not reported is written as
    /// <c>null</c>, which every reader treats as unknown, never as flat.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Snapshot(SetupSource source)
    {
        var chain = source.Chain;
        static string OnOff(bool on) => on ? "on" : "off";
        var facts = new Dictionary<string, string>
        {
            ["mic-input"] = chain.RadioMicInput ?? "",
            ["pc-microphone-device"] = string.IsNullOrEmpty(source.PcMicrophoneDevice) ? "unavailable" : source.PcMicrophoneDevice,
            ["mic-profile"] = source.MicProfile ?? "",
            ["mic-gain"] = chain.MicGain.ToString(CultureInfo.InvariantCulture),
            ["mic-boost"] = OnOff(chain.MicBoost),
            ["mic-bias"] = OnOff(chain.MicBias),
            // The off-air header writes the compander as FlexBase.OffOnValues
            // and the processor as the radio's bool; kept so the two compare.
            ["compander"] = OnOff(chain.CompanderOn),
            ["compander-level"] = chain.CompanderLevel.ToString(CultureInfo.InvariantCulture),
            ["processor"] = chain.SpeechProcessorOn.ToString(),
            ["processor-level"] = chain.SpeechProcessorLevel.ToString(CultureInfo.InvariantCulture),
            ["tx-equalizer"] = JsonSerializer.Serialize(chain.TxEqCaptured ? TxEqualizerRecord(chain.TxEqEnabled,
                chain.TxEq32, chain.TxEq63, chain.TxEq125, chain.TxEq250, chain.TxEq500, chain.TxEq1000,
                chain.TxEq2000, chain.TxEq4000, chain.TxEq8000) : null),
            ["tx-filter-low"] = chain.TxFilterLow.ToString(CultureInfo.InvariantCulture),
            ["tx-filter-high"] = chain.TxFilterHigh.ToString(CultureInfo.InvariantCulture),
            ["rf-power"] = source.RfPower.ToString(CultureInfo.InvariantCulture),
        };
        return Registered.Select(f => new KeyValuePair<string, string>(f.Key, facts[f.Key])).ToList();
    }

    /// <summary>
    /// The one shape of the <c>tx-equalizer</c> record, shared by the off-air
    /// take and the playback take: whether it is on, and all nine bands,
    /// 32 Hz included.
    /// </summary>
    public static object TxEqualizerRecord(bool enabled, int hz32, int hz63, int hz125, int hz250, int hz500,
        int hz1000, int hz2000, int hz4000, int hz8000) =>
        new { Enabled = enabled, Hz32 = hz32, Hz63 = hz63, Hz125 = hz125, Hz250 = hz250, Hz500 = hz500,
            Hz1000 = hz1000, Hz2000 = hz2000, Hz4000 = hz4000, Hz8000 = hz8000 };

    /// <summary>The nine band names, in the record's order.</summary>
    public static readonly string[] EqualizerBands = { "Hz32", "Hz63", "Hz125", "Hz250", "Hz500", "Hz1000", "Hz2000", "Hz4000", "Hz8000" };

    /// <summary>
    /// A take's transmit EQ as the header recorded it: on or off and the nine
    /// bands, or null when it was not recorded or the radio had not reported it.
    /// Compared as numbers, never by the JSON's property order.
    /// </summary>
    public static (bool Enabled, int[] Bands)? Equalizer(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var bands = new int[EqualizerBands.Length];
            for (int i = 0; i < bands.Length; i++)
                if (!root.TryGetProperty(EqualizerBands[i], out var band) || !band.TryGetInt32(out bands[i])) return null;
            return root.TryGetProperty("Enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (enabled.GetBoolean(), bands) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The setup facts that differed and are COMPARED anyway, with the
    /// difference named: the transmit filter and the RF power (RULED by Noel
    /// 2026-10-22 05:39), the microphone profile, the microphone input, and
    /// the computer microphone (Noel, 2026-10-21 20:18), and since Unified
    /// takes every other registered field: mic gain, boost and bias, the
    /// compander, the speech processor and the transmit EQ. A fact absent
    /// from either header (a take written before the field existed) is not
    /// named: what was not recorded cannot be said to have changed.
    /// </summary>
    /// <remarks>
    /// The order is <see cref="Registered"/>'s, microphone first. It was
    /// filter, power, profile, input, device until Unified takes.
    /// </remarks>
    public static IReadOnlyList<SetupChange> Changes(TakeInspection previous, TakeInspection current)
    {
        var changes = new List<SetupChange>();
        bool inputChanged = AddIfDifferent(changes, previous, current, "mic-input", numeric: false);
        // Naming the computer microphone only when the input itself did not
        // change: moving from the jack to PC audio already explains why the
        // device went from "unavailable" to a name.
        if (!inputChanged) AddIfDifferent(changes, previous, current, "pc-microphone-device", numeric: false);
        AddIfDifferent(changes, previous, current, "mic-profile", numeric: false);
        AddIfDifferent(changes, previous, current, "mic-gain", numeric: true);
        AddIfDifferent(changes, previous, current, "mic-boost", numeric: false);
        AddIfDifferent(changes, previous, current, "mic-bias", numeric: false);
        AddPair(changes, previous, current, GroupCompander, "compander", "compander-level");
        AddPair(changes, previous, current, GroupProcessor, "processor", "processor-level");
        var pe = Equalizer(previous.Get("tx-equalizer"));
        var ce = Equalizer(current.Get("tx-equalizer"));
        if (pe is { } a && ce is { } b && (a.Enabled != b.Enabled || !a.Bands.SequenceEqual(b.Bands)))
            changes.Add(new(GroupEqualizer, previous.Get("tx-equalizer"), current.Get("tx-equalizer")));
        string pl = previous.Get("tx-filter-low"), ph = previous.Get("tx-filter-high");
        string cl = current.Get("tx-filter-low"), ch = current.Get("tx-filter-high");
        if (pl != null && ph != null && cl != null && ch != null &&
            (!SameNumber(pl, cl) || !SameNumber(ph, ch)))
            changes.Add(new("tx-filter", pl + " " + ph, cl + " " + ch));
        AddIfDifferent(changes, previous, current, "rf-power", numeric: true);
        return changes;
    }

    /// <summary>
    /// The group a change belongs to: one sentence each, and what the
    /// several-settings warning counts.
    /// </summary>
    public static string GroupOf(string changeKey) => changeKey switch
    {
        "mic-input" or "pc-microphone-device" => GroupMicrophone,
        _ => changeKey
    };

    /// <summary>
    /// A switch and its level, named as one group, with values "on 50" or
    /// "off 50" ("state level") so one sentence can say both.
    /// </summary>
    private static void AddPair(List<SetupChange> changes, TakeInspection previous, TakeInspection current,
        string group, string switchKey, string levelKey)
    {
        string ps = previous.Get(switchKey), cs = current.Get(switchKey);
        string pl = previous.Get(levelKey), cl = current.Get(levelKey);
        if (ps == null || cs == null || pl == null || cl == null) return;
        if (SwitchOn(ps) == SwitchOn(cs) && SameNumber(pl, cl)) return;
        changes.Add(new(group, (SwitchOn(ps) ? "on " : "off ") + pl, (SwitchOn(cs) ? "on " : "off ") + cl));
    }

    /// <summary>"on" or "True" (either spelling the headers use) reads as on.</summary>
    public static bool SwitchOn(string value) =>
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

    private static bool AddIfDifferent(List<SetupChange> changes, TakeInspection previous, TakeInspection current,
        string key, bool numeric)
    {
        string a = previous.Get(key), b = current.Get(key);
        if (a == null || b == null) return false;
        if (numeric ? SameNumber(a, b) : a == b) return false;
        changes.Add(new(key, a, b));
        return true;
    }

    private static bool SameNumber(string a, string b) =>
        double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
        double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ? x == y : a == b;
}
