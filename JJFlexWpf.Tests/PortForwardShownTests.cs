using JJFlexWpf.Dialogs;
using Radios;
using Xunit;
using Shown = JJFlexWpf.Dialogs.SettingsDialog.PortForwardShown;

namespace JJFlexWpf.Tests;

/// <summary>
/// The forwarding lines and the Network tab's change test (#1065): a UDP port
/// that is not known is said to be not known, never shown as the TCP number;
/// and an untouched Network tab never offers to rewrite the radio's port
/// forwarding, whatever a fresh read of the radio now says.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> Static methods of <see cref="SettingsDialog"/> only: no
/// dialog, window or element is built, and every test asserts it runs on an
/// MTA thread, where WPF cannot build one.
/// </remarks>
public sealed class PortForwardShownTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    [Fact]
    public void An_untouched_tab_is_not_changed_when_the_radio_reads_a_udp_port_it_could_not_show()
    {
        AssertWindowless();
        // The tab filled with forwarding on, TCP 25678, and the UDP box
        // mirroring it because the UDP port is not known (-1).
        var shown = new Shown(true, 25678, 25678);
        Assert.False(SettingsDialog.PortForwardChanged(shown, wantEnabled: true, tcp: 25678, udp: 25678,
            curEnabled: true, curTcp: 25678, curUdp: -1));
    }

    [Fact]
    public void An_untouched_tab_is_not_changed_when_the_radios_number_moved_while_it_was_open()
    {
        AssertWindowless();
        // Filled before this fix's reader: the box showed 4991. The radio now
        // reads 25678 (a list arrived, or the reader changed). Untouched, it
        // must not offer to write 4991 back.
        var shown = new Shown(true, 25678, 4991);
        Assert.False(SettingsDialog.PortForwardChanged(shown, true, 25678, 4991,
            curEnabled: true, curTcp: 25678, curUdp: 25678));
    }

    [Fact]
    public void An_untouched_tab_filled_with_no_radio_does_not_offer_to_turn_forwarding_off()
    {
        AssertWindowless();
        var shown = new Shown(false, 4992, 4992);
        Assert.False(SettingsDialog.PortForwardChanged(shown, wantEnabled: false, tcp: 0, udp: 0,
            curEnabled: true, curTcp: 25678, curUdp: 25678));
    }

    [Fact]
    public void An_edited_port_is_a_change()
    {
        AssertWindowless();
        var shown = new Shown(true, 25678, 25678);
        Assert.True(SettingsDialog.PortForwardChanged(shown, true, 25680, 25680, true, 25678, 25678));
        Assert.True(SettingsDialog.PortForwardChanged(shown, true, 25678, 25680, true, 25678, 25678));
        Assert.True(SettingsDialog.PortForwardChanged(shown, false, 0, 0, true, 25678, 25678));
    }

    [Fact]
    public void With_nothing_recorded_the_radios_values_are_the_comparison()
    {
        AssertWindowless();
        Assert.False(SettingsDialog.PortForwardChanged(null, true, 25678, 25678, true, 25678, 25678));
        Assert.True(SettingsDialog.PortForwardChanged(null, true, 25678, 25678, true, 25678, -1));
    }

    [Fact]
    public void Radio_setup_never_puts_the_tcp_number_in_the_udp_rule()
    {
        AssertWindowless();
        string line = SettingsDialog.WayInLine(forwarding: true, tcp: 25678, udp: -1, modeText: "MODE.");
        Assert.Equal(Lexicon.Get("settings.radio.way_in.set_udp_unknown",
            ("ports", Lexicon.Get("settings.radio.way_in.ports_udp_unknown", ("tcp", 25678))),
            ("modeText", "MODE."), ("tcp", 25678),
            ("tlsPort", FlexBase.SmartLinkRadioTlsPort), ("udpPort", FlexBase.SmartLinkRadioUdpPort)), line);
        Assert.DoesNotContain("TCP and UDP", line);
        // 25678 appears for TCP only: once in the ports phrase, once in the TCP rule.
        Assert.Equal(2, CountOf(line, "25678"));
    }

    [Fact]
    public void Radio_setup_names_the_listed_udp_port()
    {
        AssertWindowless();
        string separate = SettingsDialog.WayInLine(true, 25678, 25679, "MODE.");
        Assert.Contains("25679", separate);
        string same = SettingsDialog.WayInLine(true, 25678, 25678, "MODE.");
        Assert.Equal(Lexicon.Get("settings.radio.way_in.set",
            ("ports", Lexicon.Get("settings.radio.way_in.ports_same", ("tcp", 25678))),
            ("modeText", "MODE."), ("tcp", 25678), ("tlsPort", FlexBase.SmartLinkRadioTlsPort),
            ("udpShown", 25678), ("udpPort", FlexBase.SmartLinkRadioUdpPort)), same);
        Assert.Equal(Lexicon.Get("settings.radio.way_in.not_set", ("modeText", "MODE.")),
            SettingsDialog.WayInLine(false, 25678, 25678, "MODE."));
    }

    [Fact]
    public void The_network_tab_says_the_udp_port_is_not_known()
    {
        AssertWindowless();
        string line = SettingsDialog.ForwardingStateLine(true, 25678, -1, "192.0.2.10");
        Assert.DoesNotContain("-1", line);
        Assert.DoesNotContain("TCP and UDP", line);
        Assert.StartsWith(Lexicon.Get("settings.network.advertises_tcp_udp_unknown", ("tcp", 25678)), line);
        Assert.DoesNotContain(" 0 ", SettingsDialog.ForwardingStateLine(true, 25678, 0, "192.0.2.10"));
    }

    private static int CountOf(string text, string part)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(part, i, System.StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
        return n;
    }
}
