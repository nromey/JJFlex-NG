#nullable enable
using System.Globalization;

namespace Flex.Smoothlake.FlexLib;

// JJFlex patch: both of the radio's advertised port pairs, as the SmartLink
// radio list reported them. Reapply with MIGRATION.md item 29.
//
// Stock ParseRadioListMessage reads the manual pair and the UPnP pair and keeps
// ONE in PublicTlsPort/PublicUdpPort, so a dead manual pair hides a working
// UPnP pair from everything downstream. This keeps both, each by source, plus
// the radio's own hole-punch report, in a record nothing on the connect path
// writes. Stock's own selection is untouched: PublicTlsPort, PublicUdpPort,
// IsPortForwardOn and RequiresHolePunch are computed exactly as before.

/// <summary>
/// What the SmartLink radio list said about the radio's ports. A port the list
/// did not carry, or carried as something that is not a whole number, is -1.
/// A value is kept as reported otherwise (zero included); deciding whether a
/// pair is usable (both ports from 1 to 65535) is the caller's job.
/// </summary>
public sealed class AdvertisedPorts
{
    public AdvertisedPorts(int manualTlsPort, int manualUdpPort, bool upnpSupported,
        int upnpTlsPort, int upnpUdpPort, bool requiresHolePunch)
    {
        ManualTlsPort = manualTlsPort;
        ManualUdpPort = manualUdpPort;
        UpnpSupported = upnpSupported;
        UpnpTlsPort = upnpTlsPort;
        UpnpUdpPort = upnpUdpPort;
        RequiresHolePunch = requiresHolePunch;
    }

    /// <summary>public_tls_port: the manually forwarded TCP port.</summary>
    public int ManualTlsPort { get; }
    /// <summary>public_udp_port: the manually forwarded UDP port.</summary>
    public int ManualUdpPort { get; }
    /// <summary>upnp_supported=1: the radio's own UPnP mapping is in play.</summary>
    public bool UpnpSupported { get; }
    /// <summary>public_upnp_tls_port, read whether or not UPnP is supported.</summary>
    public int UpnpTlsPort { get; }
    /// <summary>public_upnp_udp_port, read whether or not UPnP is supported.</summary>
    public int UpnpUdpPort { get; }
    /// <summary>The hole-punch flag exactly as the list parse computed it, before anyone changed it.</summary>
    public bool RequiresHolePunch { get; }

    /// <summary>The parse rule for one port field: absent or not a whole number reads as -1.</summary>
    public static int ParsePort(string? text) =>
        text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : -1;

    public override string ToString() =>
        $"manual={ManualTlsPort}/{ManualUdpPort} upnp={(UpnpSupported ? "on" : "off")} {UpnpTlsPort}/{UpnpUdpPort} punch={RequiresHolePunch}";
}
