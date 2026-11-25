using JJFlexWpf.Dialogs;
using Radios;
using Xunit;
using Shown = JJFlexWpf.Dialogs.SettingsDialog.PortForwardShown;
using Words = JJFlexWpf.Dialogs.SettingsDialog.PortFieldWords;
using Fill = JJFlexWpf.Dialogs.SettingsDialog.PortBoxFillValues;

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

    // Repair round 1: forwarding on and neither external port known (-1 when
    // no list and no SmartLink sighting describes the radio, 0 from the
    // connection cache). Both screens must say forwarding is on, name no
    // external number, and keep the radio's LAN-side ports.

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    public void The_network_tab_says_forwarding_is_on_and_names_no_port_when_neither_is_known(int tcp, int udp)
    {
        AssertWindowless();
        string line = SettingsDialog.ForwardingStateLine(true, tcp, udp, "192.0.2.10");
        Assert.Equal(Lexicon.Get("settings.network.forwarding_on_ports_unknown")
            + Lexicon.Get("settings.network.router_rules_ports_unknown", ("lanIp", "192.0.2.10"),
                ("tlsPort", FlexBase.SmartLinkRadioTlsPort), ("udpPort", FlexBase.SmartLinkRadioUdpPort)), line);
        AssertNamesNoUnknownPort(line);
        Assert.Contains(FlexBase.SmartLinkRadioTlsPort.ToString(), line);
        Assert.Contains(FlexBase.SmartLinkRadioUdpPort.ToString(), line);
        Assert.Contains("192.0.2.10", line);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    public void Radio_setup_says_forwarding_is_on_and_names_no_port_when_neither_is_known(int tcp, int udp)
    {
        AssertWindowless();
        string line = SettingsDialog.WayInLine(true, tcp, udp, "MODE.");
        Assert.Equal(Lexicon.Get("settings.radio.way_in.set_ports_unknown", ("modeText", "MODE."),
            ("tlsPort", FlexBase.SmartLinkRadioTlsPort), ("udpPort", FlexBase.SmartLinkRadioUdpPort)), line);
        Assert.NotEqual(Lexicon.Get("settings.radio.way_in.not_set", ("modeText", "MODE.")), line);
        AssertNamesNoUnknownPort(line);
        Assert.Contains("MODE.", line);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(25678, 25678)]
    public void Forwarding_off_still_says_so_on_both_screens(int tcp, int udp)
    {
        AssertWindowless();
        Assert.Equal(Lexicon.Get("settings.network.uses_upnp_or_hole_punch"),
            SettingsDialog.ForwardingStateLine(false, tcp, udp, "192.0.2.10"));
        Assert.Equal(Lexicon.Get("settings.radio.way_in.not_set", ("modeText", "MODE.")),
            SettingsDialog.WayInLine(false, tcp, udp, "MODE."));
    }

    [Fact]
    public void The_known_port_lines_are_unchanged()
    {
        AssertWindowless();
        Assert.Equal(Lexicon.Get("settings.network.advertises_tcp_udp_unknown", ("tcp", 25678))
            + Lexicon.Get("settings.network.router_rules_udp_unknown", ("tcp", 25678), ("lanIp", "192.0.2.10"),
                ("tlsPort", FlexBase.SmartLinkRadioTlsPort), ("udpPort", FlexBase.SmartLinkRadioUdpPort)),
            SettingsDialog.ForwardingStateLine(true, 25678, -1, "192.0.2.10"));
        Assert.Equal(Lexicon.Get("settings.network.advertises_one_port", ("tcp", 25678))
            + Lexicon.Get("settings.network.router_rules", ("tcp", 25678), ("udp", 25678), ("lanIp", "192.0.2.10"),
                ("tlsPort", FlexBase.SmartLinkRadioTlsPort), ("udpPort", FlexBase.SmartLinkRadioUdpPort)),
            SettingsDialog.ForwardingStateLine(true, 25678, 25678, "192.0.2.10"));
        Assert.Equal(Lexicon.Get("settings.network.advertises_separate_ports", ("tcp", 25678), ("udp", 25679))
            + Lexicon.Get("settings.network.router_rules", ("tcp", 25678), ("udp", 25679), ("lanIp", "192.0.2.10"),
                ("tlsPort", FlexBase.SmartLinkRadioTlsPort), ("udpPort", FlexBase.SmartLinkRadioUdpPort)),
            SettingsDialog.ForwardingStateLine(true, 25678, 25679, "192.0.2.10"));
        Assert.Equal(Lexicon.Get("settings.radio.way_in.set",
            ("ports", Lexicon.Get("settings.radio.way_in.ports_separate", ("tcp", 25678), ("udp", 25679))),
            ("modeText", "MODE."), ("tcp", 25678), ("tlsPort", FlexBase.SmartLinkRadioTlsPort),
            ("udpShown", 25679), ("udpPort", FlexBase.SmartLinkRadioUdpPort)),
            SettingsDialog.WayInLine(true, 25678, 25679, "MODE."));
    }

    [Fact]
    public void An_untouched_tab_filled_with_neither_port_known_does_not_offer_a_rewrite()
    {
        AssertWindowless();
        // FillNetworkTab with enabled, -1, -1: the checkbox on, and both
        // boxes fall back to 4992 (no TCP to mirror). An untouched OK parses
        // those boxes back, and the radio still reads -1/-1.
        var shown = new Shown(true, 4992, 4992);
        Assert.False(SettingsDialog.PortForwardChanged(shown, wantEnabled: true, tcp: 4992, udp: 4992,
            curEnabled: true, curTcp: -1, curUdp: -1));
        // And the comparison still sees a real edit in this state.
        Assert.True(SettingsDialog.PortForwardChanged(shown, true, 25678, 25678, true, -1, -1));
    }

    // Repair round 2: a number this program put in a port box (no radio;
    // 4992 when forwarding's ports are not known; the TCP number copied into
    // the UDP box when the UDP port is not known) is labelled and named as a
    // suggestion, with a description heard on focus. A number the radio
    // reported keeps today's label and name, with no description.

    private static Words WordsAfterFill(bool enabled, int tcp, int udp)
    {
        var fill = SettingsDialog.PortBoxFill(enabled, tcp, udp);
        return SettingsDialog.PortFieldPresentation(fill.Separate,
            SettingsDialog.PortBoxReported(tcp, fill.TcpText),
            SettingsDialog.PortBoxReported(udp, fill.UdpText));
    }

    private static string L(string key) => Lexicon.Get(key);

    private static void AssertBothSuggestedShared(Words w)
    {
        Assert.Equal(L("settings.network.port_label_shared_suggested"), w.TcpLabel);
        Assert.Equal(L("settings.network.port_name_shared_suggested"), w.TcpName);
        Assert.Equal(L("settings.network.port_help_suggested"), w.TcpHelp);
        Assert.Equal(L("settings.network.port_label_udp_suggested"), w.UdpLabel);
        Assert.Equal(L("settings.network.port_name_udp_suggested"), w.UdpName);
        Assert.Equal(L("settings.network.port_help_suggested"), w.UdpHelp);
        AssertNotTodaysReportedWords(w);
    }

    private static void AssertTodaysSharedWords(Words w)
    {
        Assert.Equal(L("settings.network.port_label_shared"), w.TcpLabel);
        Assert.Equal(L("settings.network.port_name_shared"), w.TcpName);
        Assert.Equal(string.Empty, w.TcpHelp);
        Assert.Equal(L("settings.network.port_label_udp"), w.UdpLabel);
        Assert.Equal(L("settings.network.port_name_udp"), w.UdpName);
        Assert.Equal(string.Empty, w.UdpHelp);
    }

    /// <summary>Neither box carries a reported number's label or name, and both describe themselves.</summary>
    private static void AssertNotTodaysReportedWords(Words w)
    {
        Assert.NotEqual(L("settings.network.port_label_shared"), w.TcpLabel);
        Assert.NotEqual(L("settings.network.port_label_tcp_only"), w.TcpLabel);
        Assert.NotEqual(L("settings.network.port_name_shared"), w.TcpName);
        Assert.NotEqual(L("settings.network.port_label_udp"), w.UdpLabel);
        Assert.NotEqual(L("settings.network.port_name_udp"), w.UdpName);
        Assert.False(string.IsNullOrWhiteSpace(w.TcpHelp));
        Assert.False(string.IsNullOrWhiteSpace(w.UdpHelp));
    }

    [Theory]
    [InlineData(true, -1, -1)]
    [InlineData(true, 0, 0)]
    [InlineData(false, -1, -1)]
    [InlineData(false, 0, 0)]
    public void A_4992_the_radio_did_not_report_is_labelled_and_named_as_a_suggestion(bool enabled, int tcp, int udp)
    {
        AssertWindowless();
        // The values are unchanged by this repair: 4992 in both, shared.
        Assert.Equal(new Fill("4992", "4992", false), SettingsDialog.PortBoxFill(enabled, tcp, udp));
        AssertBothSuggestedShared(WordsAfterFill(enabled, tcp, udp));
    }

    [Fact]
    public void With_no_radio_connected_the_4992s_are_suggestions()
    {
        AssertWindowless();
        // The first load and the disconnected refill show 4992 in both boxes
        // and record no reported port (-1, -1).
        AssertBothSuggestedShared(SettingsDialog.PortFieldPresentation(false,
            SettingsDialog.PortBoxReported(-1, "4992"), SettingsDialog.PortBoxReported(-1, "4992")));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void A_udp_box_copying_a_reported_tcp_number_says_the_udp_number_is_a_suggestion(int udp)
    {
        AssertWindowless();
        Assert.Equal(new Fill("25678", "25678", false), SettingsDialog.PortBoxFill(true, 25678, udp));
        var w = WordsAfterFill(true, 25678, udp);
        Assert.Equal(L("settings.network.port_label_shared_udp_suggested"), w.TcpLabel);
        Assert.Equal(L("settings.network.port_name_shared_udp_suggested"), w.TcpName);
        Assert.Equal(L("settings.network.port_help_udp_copied"), w.TcpHelp);
        Assert.Equal(L("settings.network.port_label_udp_suggested"), w.UdpLabel);
        Assert.Equal(L("settings.network.port_name_udp_suggested"), w.UdpName);
        Assert.Equal(L("settings.network.port_help_udp_copied"), w.UdpHelp);
        AssertNotTodaysReportedWords(w);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_reported_shared_port_keeps_todays_label_and_name(bool enabled)
    {
        AssertWindowless();
        AssertTodaysSharedWords(WordsAfterFill(enabled, 25678, 25678));
    }

    [Fact]
    public void Reported_separate_ports_keep_todays_labels_and_names()
    {
        AssertWindowless();
        Assert.Equal(new Fill("25678", "25679", true), SettingsDialog.PortBoxFill(true, 25678, 25679));
        var w = WordsAfterFill(true, 25678, 25679);
        Assert.Equal(L("settings.network.port_label_tcp_only"), w.TcpLabel);
        Assert.Equal(L("settings.network.port_name_shared"), w.TcpName);
        Assert.Equal(string.Empty, w.TcpHelp);
        Assert.Equal(L("settings.network.port_label_udp"), w.UdpLabel);
        Assert.Equal(L("settings.network.port_name_udp"), w.UdpName);
        Assert.Equal(string.Empty, w.UdpHelp);
    }

    [Fact]
    public void A_refill_that_reports_the_ports_puts_todays_words_back_and_a_later_unknown_takes_them_away()
    {
        AssertWindowless();
        // Each refill works the words out afresh from what it reports: nothing
        // from the previous fill sticks.
        AssertBothSuggestedShared(WordsAfterFill(true, -1, -1));
        AssertTodaysSharedWords(WordsAfterFill(true, 25678, 25678));
        AssertBothSuggestedShared(WordsAfterFill(true, 0, 0));
        AssertTodaysSharedWords(WordsAfterFill(true, 25678, 25678));
    }

    [Fact]
    public void Ticking_separate_ports_over_a_copied_udp_number_keeps_it_a_suggestion()
    {
        AssertWindowless();
        // Filled with TCP 25678 reported and UDP not known: the UDP box holds
        // 25678. The operator ticks "Use different TCP and UDP ports".
        var w = SettingsDialog.PortFieldPresentation(true,
            SettingsDialog.PortBoxReported(25678, "25678"), SettingsDialog.PortBoxReported(-1, "25678"));
        Assert.Equal(L("settings.network.port_label_tcp_only"), w.TcpLabel);
        Assert.Equal(string.Empty, w.TcpHelp);
        Assert.Equal(L("settings.network.port_label_udp_suggested"), w.UdpLabel);
        Assert.Equal(L("settings.network.port_help_udp_copied"), w.UdpHelp);
        // And a 4992 TCP box shown separately is a suggested TCP port.
        var unknown = SettingsDialog.PortFieldPresentation(true,
            SettingsDialog.PortBoxReported(-1, "4992"), SettingsDialog.PortBoxReported(-1, "4992"));
        Assert.Equal(L("settings.network.port_label_tcp_only_suggested"), unknown.TcpLabel);
        Assert.Equal(L("settings.network.port_name_tcp_only_suggested"), unknown.TcpName);
        Assert.Equal(L("settings.network.port_help_suggested"), unknown.TcpHelp);
    }

    [Fact]
    public void Unticking_separate_ports_copies_tcp_into_udp_and_the_udp_number_becomes_a_suggestion()
    {
        AssertWindowless();
        // Filled with 25678 and 25679 reported; unticking copies 25678 into
        // the UDP box, which is no longer the radio's UDP port.
        var w = SettingsDialog.PortFieldPresentation(false,
            SettingsDialog.PortBoxReported(25678, "25678"), SettingsDialog.PortBoxReported(25679, "25678"));
        Assert.Equal(L("settings.network.port_label_shared_udp_suggested"), w.TcpLabel);
        Assert.Equal(L("settings.network.port_label_udp_suggested"), w.UdpLabel);
    }

    [Theory]
    [InlineData(25678, "25678", true)]
    [InlineData(25678, "25679", false)]
    [InlineData(-1, "4992", false)]
    [InlineData(0, "0", false)]
    [InlineData(25678, "", false)]
    [InlineData(25678, null, false)]
    public void A_box_is_reported_only_when_it_holds_the_radios_own_positive_number(int reported, string? box, bool expected)
    {
        AssertWindowless();
        Assert.Equal(expected, SettingsDialog.PortBoxReported(reported, box));
    }

    [Fact]
    public void Every_fill_and_the_separate_ports_box_put_the_words_on_the_boxes()
    {
        AssertWindowless();
        // Source pin: the wiring these pure tests cannot reach.
        string dir = System.IO.Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs");
        string main = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "SettingsDialog.xaml.cs"));
        string words = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "SettingsDialog.PortFieldWords.cs"));
        string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "SettingsDialog.xaml"));
        // The first load and the disconnected refill record no reported port.
        Assert.Equal(2, CountOf(main, "ShowPortFieldWords(-1, -1);"));
        Assert.Contains("ShowPortFieldWords(tcp, udp);", main);
        int handler = main.IndexOf("private void PortForwardSeparatePortsCheck_Changed", System.StringComparison.Ordinal);
        Assert.True(handler >= 0);
        int end = main.IndexOf("private void PortForwardTcpBox_TextChanged", handler, System.StringComparison.Ordinal);
        Assert.True(end > handler);
        Assert.Contains("ApplyPortFieldWords();", main.Substring(handler, end - handler));
        // Nothing else writes the labels, so nothing can put a reported
        // number's words on a suggestion.
        Assert.DoesNotContain("PortForwardTcpLabel.Text =", main);
        Assert.DoesNotContain("PortForwardUdpLabel.Text =", main);
        Assert.Contains("PortForwardTcpLabel.Text = words.TcpLabel;", words);
        Assert.Contains("PortForwardUdpLabel.Text = words.UdpLabel;", words);
        Assert.Contains("AutomationProperties.SetName(PortForwardTcpBox, words.TcpName);", words);
        Assert.Contains("AutomationProperties.SetName(PortForwardUdpBox, words.UdpName);", words);
        Assert.Contains("AutomationProperties.SetHelpText(PortForwardTcpBox, words.TcpHelp);", words);
        Assert.Contains("AutomationProperties.SetHelpText(PortForwardUdpBox, words.UdpHelp);", words);
        Assert.Contains("x:Name=\"PortForwardUdpLabel\"", xaml);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// No non-positive number stands where a port would be, and nothing says
    /// forwarding is disabled.
    /// </summary>
    private static void AssertNamesNoUnknownPort(string line)
    {
        Assert.DoesNotContain("-1", line);
        Assert.DoesNotMatch(@"(?<![\d.])0(?![\d.])", line);
        Assert.DoesNotContain("disabled", line, System.StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOf(string text, string part)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(part, i, System.StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
        return n;
    }
}
