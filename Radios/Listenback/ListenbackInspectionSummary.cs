using System;
using System.Globalization;
using System.Linq;

namespace Radios.Listenback;

/// <summary>Temporary inspection receipt, kept separate from automatic restoration.</summary>
public static class ListenbackInspectionSummary
{
    public static string Describe(RadioRecoverySnapshot offered, Func<SavedRadioSetting, bool?> present = null)
    {
        string changes = string.Join(Environment.NewLine, offered.Settings.Select(s => DescribeSetting(s, present?.Invoke(s))));
        if (offered.ListenerRequest is { } listener)
            changes += Environment.NewLine + Lexicon.Get("audio.iq.ack.listener_request",
                ("slice", listener.SliceId.ToString(CultureInfo.InvariantCulture)), ("pan", listener.PanId.ToString("X", CultureInfo.InvariantCulture)),
                ("frequency", listener.Frequency.ToString("R", CultureInfo.InvariantCulture)), ("mode", listener.Mode),
                ("antenna", listener.ReceiveAntenna ?? Lexicon.Get("audio.iq.ack.antenna_unchanged")));
        if (offered.ResourceCreationPending) changes += Environment.NewLine + Lexicon.Get("audio.iq.ack.creation_unknown");
        return Lexicon.Get("audio.iq.ack.offer", ("serial", offered.Serial), ("changes", changes));
    }
    private static string DescribeSetting(SavedRadioSetting setting, bool? present)
    {
        string resource = setting.ResourceKind switch
        {
            "slice" => Lexicon.Get("audio.iq.ack.slice", ("id", setting.ResourceId.ToString(CultureInfo.InvariantCulture))),
            "pan" => Lexicon.Get("audio.iq.ack.pan", ("id", setting.ResourceId.ToString("X", CultureInfo.InvariantCulture))),
            "stream" => Lexicon.Get("audio.iq.ack.stream", ("id", setting.ResourceId.ToString("X", CultureInfo.InvariantCulture))),
            _ => Lexicon.Get("audio.iq.ack.radio")
        };
        if (setting.Name.StartsWith("created-", StringComparison.Ordinal))
            return Lexicon.Get(present == true ? "audio.iq.ack.present" : present == false ? "audio.iq.ack.absent" : "audio.iq.ack.created", ("target", resource));
        string key = "audio.iq.setting." + setting.Name;
        return Lexicon.Get("audio.iq.ack.setting", ("setting", Lexicon.Get(key)), ("target", resource),
            ("prior", setting.Value), ("requested", setting.RequestedValue ?? Lexicon.Get("audio.iq.ack.not_recorded")));
    }
}
