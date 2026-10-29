using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;

namespace Radios.Listenback;

/// <summary>
/// Puts a restore the radio accepted into FlexLib's own cache (#835, fix round
/// 2026-10-29, item 1).
/// </summary>
/// <remarks>
/// <para>
/// A take puts the operator's settings back with raw commands, confirmed by the
/// radio's reply, because a FlexLib setter skips a value its cache already
/// holds and swallows the reply. But for several settings the radio never
/// echoes the change to the client that made it, so FlexLib's cache keeps the
/// value the take's arrangement set. On the bench (Noel, 2026-10-29 01:48) the
/// monitor was on, the take turned it off and back on by raw command, FlexLib
/// still said off, and the next send therefore "found" the monitor off, turned
/// it on, and at its end turned it OFF.
/// </para>
/// <para>
/// So once the radio has accepted a restore, its value goes into the cache
/// through FlexLib's own status parser, exactly as the echo would have: no
/// second command reaches the radio (a FlexLib setter would send one, unchecked,
/// after the restore owner's receive check), and nothing is added to the raw
/// status trace, so no tap can mistake it for the radio's own report.
/// </para>
/// <para>
/// The transmit and radio parsers are private, so they are reached by
/// reflection, the way <c>FlexBase.RadioReportsUpdating</c> reads FlexLib's
/// internal <c>Updating</c>. If a FlexLib upgrade renames one, the answer here is
/// <c>parser-missing</c> in the take's restore trace, and
/// <c>FlexLibRestoreCacheTests</c> goes red the same day.
/// </para>
/// </remarks>
internal static class FlexLibRestoreCache
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo TransmitParser =
        typeof(Radio).GetMethod("ParseTransmitStatus", Private, null, new[] { typeof(string) }, null);
    private static readonly MethodInfo RadioParser =
        typeof(Radio).GetMethod("ParseRadioStatus", Private, null, new[] { typeof(string) }, null);

    /// <summary>True when both private FlexLib parsers this relies on were found.</summary>
    internal static bool ParsersFound => TransmitParser != null && RadioParser != null;

    /// <summary>
    /// FlexLib's cached value for the setting, in the radio's own status form,
    /// or null when this class does not know how to read it. Taken just before
    /// the restore command goes out, so the apply can tell whether anything
    /// else (the operator, or the radio's own echo) changed it meanwhile.
    /// </summary>
    internal static string Read(Radio radio, SavedRadioSetting setting)
    {
        if (radio == null || setting == null || setting.StatusKey == "") return null;
        string key = setting.StatusKey.ToLowerInvariant();
        string[] prefix = setting.StatusPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (prefix.Length == 0) return null;
        static string Flag(bool value) => value ? "1" : "0";
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        switch (prefix[0])
        {
            case "transmit":
                return key switch
                {
                    "sb_monitor" => Flag(radio.TXMonitor),
                    "rfpower" => radio.RFPower.ToString(CultureInfo.InvariantCulture),
                    _ => null
                };
            case "radio":
                return key == "full_duplex_enabled" ? Flag(radio.FullDuplexEnabled) : null;
            case "slice":
                {
                    var slice = FindSlice(radio, prefix);
                    if (slice == null) return null;
                    return key switch
                    {
                        "txant" => slice.TXAnt,
                        "audio_mute" => Flag(slice.Mute),
                        "active" => Flag(slice.Active),
                        "rxant" => slice.RXAnt,
                        "mode" => slice.DemodMode,
                        "rfgain" => slice.RFGain.ToString(CultureInfo.InvariantCulture),
                        "rf_frequency" => Number(slice.Freq),
                        _ => null
                    };
                }
            case "display":
                {
                    var pan = FindPan(radio, prefix);
                    if (pan == null) return null;
                    return key switch
                    {
                        "center" => Number(pan.CenterFreq),
                        "daxiq_channel" => pan.DAXIQChannel.ToString(CultureInfo.InvariantCulture),
                        _ => null
                    };
                }
            case "xvtr":
                {
                    var xvtr = FindXvtr(radio, prefix);
                    return xvtr != null && key == "max_power" ? Number(xvtr.MaxPower) : null;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// After the radio's success reply: put the restored value into FlexLib's
    /// cache through its own status parser. <paramref name="cachedBefore"/> is
    /// <see cref="Read"/> taken just before the command went out; if the cache
    /// has moved since, someone else wrote it and it is left alone. Returns a
    /// short word for the restore trace.
    /// </summary>
    internal static string Accept(Radio radio, SavedRadioSetting setting, string cachedBefore)
    {
        if (radio == null || setting == null || setting.StatusKey == "") return "no-cached-value";
        string now = Read(radio, setting);
        if (now != null && Same(now, setting.Value)) return "cache-already-true";
        if (now != null && cachedBefore != null && !Same(now, cachedBefore)) return "cache-changed-meanwhile; left alone";
        string[] prefix = setting.StatusPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (prefix.Length == 0) return "no-cached-value";
        string status = setting.StatusKey + "=" + setting.Value;
        try
        {
            switch (prefix[0])
            {
                case "transmit":
                    if (TransmitParser == null) return "parser-missing";
                    TransmitParser.Invoke(radio, new object[] { status });
                    break;
                case "radio":
                    if (RadioParser == null) return "parser-missing";
                    RadioParser.Invoke(radio, new object[] { status });
                    break;
                case "slice":
                    {
                        var slice = FindSlice(radio, prefix);
                        if (slice == null) return "resource-gone";
                        slice.StatusUpdate(status);
                        break;
                    }
                case "display":
                    {
                        var pan = FindPan(radio, prefix);
                        if (pan == null) return "resource-gone";
                        pan.StatusUpdate(status);
                        break;
                    }
                case "xvtr":
                    {
                        var xvtr = FindXvtr(radio, prefix);
                        if (xvtr == null) return "resource-gone";
                        xvtr.StatusUpdate(status);
                        break;
                    }
                default:
                    return "no-cached-value";
            }
        }
        catch (Exception ex)
        {
            return "cache-update-failed: " + (ex.InnerException ?? ex).Message;
        }
        string after = Read(radio, setting);
        return after == null || Same(after, setting.Value) ? "cache-updated" : "cache-update-did-not-take: " + after;
    }

    private static bool Same(string a, string b) =>
        a == b || (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                   double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && x == y);

    private static Slice FindSlice(Radio radio, string[] prefix) =>
        prefix.Length >= 2 && int.TryParse(prefix[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            ? radio.SliceList.ToArray().FirstOrDefault(s => s.Index == index)
            : null;

    private static Panadapter FindPan(Radio radio, string[] prefix)
    {
        if (prefix.Length < 3 || prefix[1] != "pan" || !prefix[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(prefix[2][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)) return null;
        return radio.PanadapterList.ToArray().FirstOrDefault(p => p.StreamID == id);
    }

    private static Xvtr FindXvtr(Radio radio, string[] prefix) =>
        prefix.Length >= 2 && int.TryParse(prefix[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            ? radio.FindXvtrByIndex(index)
            : null;
}
