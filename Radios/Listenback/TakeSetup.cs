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

    /// <summary>
    /// The setup facts that differed and are COMPARED anyway, with the
    /// difference named: the transmit filter and the RF power (RULED by Noel
    /// 2026-10-22 05:39), the microphone profile, the microphone input, and
    /// the computer microphone (Noel, 2026-10-21 20:18). A fact absent from
    /// either header (a take written before the field existed) is not named:
    /// what was not recorded cannot be said to have changed.
    /// </summary>
    public static IReadOnlyList<SetupChange> Changes(TakeInspection previous, TakeInspection current)
    {
        var changes = new List<SetupChange>();
        string pl = previous.Get("tx-filter-low"), ph = previous.Get("tx-filter-high");
        string cl = current.Get("tx-filter-low"), ch = current.Get("tx-filter-high");
        if (pl != null && ph != null && cl != null && ch != null &&
            (!SameNumber(pl, cl) || !SameNumber(ph, ch)))
            changes.Add(new("tx-filter", pl + " " + ph, cl + " " + ch));
        AddIfDifferent(changes, previous, current, "rf-power", numeric: true);
        AddIfDifferent(changes, previous, current, "mic-profile", numeric: false);
        bool inputChanged = AddIfDifferent(changes, previous, current, "mic-input", numeric: false);
        // Naming the computer microphone only when the input itself did not
        // change: moving from the jack to PC audio already explains why the
        // device went from "unavailable" to a name.
        if (!inputChanged) AddIfDifferent(changes, previous, current, "pc-microphone-device", numeric: false);
        return changes;
    }

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
