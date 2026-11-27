using System.Windows.Automation;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The Network tab's port boxes say when a number in them was supplied by
    /// this program rather than reported by the radio (#1065, repair round 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boxes always hold a number, because OK and Apply validate them and
    /// the untouched-tab snapshot compares against them: blank boxes would make
    /// an untouched OK refuse. So when a connected radio reports no external
    /// port (forwarding's ports not known on this connection, where the boxes
    /// hold 4992; or the UDP port not known, where the UDP box copies the TCP
    /// number), the values stay, and the visible label, the accessible name and
    /// the screen-reader description (heard on focus) say the number is a
    /// suggestion. A number the radio reported keeps today's label and name,
    /// with no description.
    /// </para>
    /// <para>
    /// With no radio connected (the first load and the disconnected refill)
    /// the boxes keep today's plain words: the shared or TCP-only label, the
    /// reported names, and no description, whether or not separate ports is
    /// ticked (repair round 3, Noel's ruling). Nothing is offered to the radio
    /// in that state, so the suggestion words would describe nothing.
    /// </para>
    /// <para>
    /// The words follow each refill and the separate-ports checkbox, never a
    /// keystroke: renaming the focused box as the operator types would be read
    /// out mid-edit. A box counts as reported when it holds exactly the number
    /// the radio reported at the last refill.
    /// </para>
    /// </remarks>
    public partial class SettingsDialog
    {
        /// <summary>The external ports the radio reported at the last refill; -1 when it reported none.</summary>
        private int _reportedPortTcp = -1;
        private int _reportedPortUdp = -1;

        /// <summary>
        /// Whether the last refill was of a connected radio. False until the
        /// connected refill sets it, and set false again by the first load and
        /// the disconnected refill (repair round 3).
        /// </summary>
        private bool _portWordsRadioConnected;

        /// <summary>What a connected refill writes into the port boxes, and whether it shows the ports separately.</summary>
        internal readonly record struct PortBoxFillValues(string TcpText, string UdpText, bool Separate);

        /// <summary>
        /// The values a connected refill writes, unchanged by repair round 2:
        /// a TCP port that is not known shows 4992; a UDP port that is not
        /// known (-1) mirrors the TCP box, as the shared field does, rather
        /// than showing an unrelated 4992 (#1065). Separate only when
        /// forwarding is on and both ports are known and differ. Pure.
        /// </summary>
        /// <remarks>
        /// In the shared layout the UDP box can end up holding the TCP box's
        /// number instead of <see cref="PortBoxFillValues.UdpText"/>: the
        /// separate-ports checkbox mirrors it when the refill unticks it. The
        /// words are therefore worked out from what the boxes hold after the
        /// refill, never from these values.
        /// </remarks>
        internal static PortBoxFillValues PortBoxFill(bool enabled, int tcp, int udp) =>
            new PortBoxFillValues(
                (tcp > 0 ? tcp : 4992).ToString(),
                (udp > 0 ? udp : tcp > 0 ? tcp : 4992).ToString(),
                enabled && tcp > 0 && udp > 0 && tcp != udp);

        /// <summary>The label, accessible name and description of the two port boxes.</summary>
        internal readonly record struct PortFieldWords(
            string TcpLabel, string TcpName, string TcpHelp,
            string UdpLabel, string UdpName, string UdpHelp);

        /// <summary>
        /// Whether a port box holds the number the radio reported: a positive
        /// reported port, and the box holding exactly it. Pure.
        /// </summary>
        internal static bool PortBoxReported(int reportedPort, string? boxText) =>
            reportedPort > 0 && int.TryParse(boxText, out int shown) && shown == reportedPort;

        /// <summary>
        /// The words for the two port boxes, from whether each box holds a
        /// number the radio reported and whether the TCP and UDP ports are
        /// shown separately. Pure, so a test reaches it without a window.
        /// </summary>
        internal static PortFieldWords PortFieldPresentation(bool separate, bool tcpReported, bool udpReported)
        {
            string suggestedHelp = Lexicon.Get("settings.network.port_help_suggested");
            string copiedHelp = Lexicon.Get("settings.network.port_help_udp_copied");

            // The UDP box: its own words in both layouts. A UDP number that is
            // not reported beside a reported TCP number is the TCP number
            // copied across, and says so.
            string udpLabel = udpReported
                ? Lexicon.Get("settings.network.port_label_udp")
                : Lexicon.Get("settings.network.port_label_udp_suggested");
            string udpName = udpReported
                ? Lexicon.Get("settings.network.port_name_udp")
                : Lexicon.Get("settings.network.port_name_udp_suggested");
            string udpHelp = udpReported ? string.Empty : tcpReported ? copiedHelp : suggestedHelp;

            if (separate)
            {
                return tcpReported
                    ? new PortFieldWords(
                        Lexicon.Get("settings.network.port_label_tcp_only"),
                        Lexicon.Get("settings.network.port_name_shared"),
                        string.Empty,
                        udpLabel, udpName, udpHelp)
                    : new PortFieldWords(
                        Lexicon.Get("settings.network.port_label_tcp_only_suggested"),
                        Lexicon.Get("settings.network.port_name_tcp_only_suggested"),
                        suggestedHelp,
                        udpLabel, udpName, udpHelp);
            }

            // One box for both ports: reported only when both are.
            if (!tcpReported)
                return new PortFieldWords(
                    Lexicon.Get("settings.network.port_label_shared_suggested"),
                    Lexicon.Get("settings.network.port_name_shared_suggested"),
                    suggestedHelp,
                    udpLabel, udpName, udpHelp);
            if (!udpReported)
                return new PortFieldWords(
                    Lexicon.Get("settings.network.port_label_shared_udp_suggested"),
                    Lexicon.Get("settings.network.port_name_shared_udp_suggested"),
                    copiedHelp,
                    udpLabel, udpName, udpHelp);
            return new PortFieldWords(
                Lexicon.Get("settings.network.port_label_shared"),
                Lexicon.Get("settings.network.port_name_shared"),
                string.Empty,
                udpLabel, udpName, udpHelp);
        }

        /// <summary>
        /// The words the port boxes carry, given whether a radio is connected.
        /// With no radio connected they are today's plain words (the reported
        /// words for the layout shown), whatever the boxes hold; with a radio
        /// connected they are <see cref="PortFieldPresentation"/>'s. Pure, so a
        /// test reaches it without a window (repair round 3).
        /// </summary>
        internal static PortFieldWords ShownPortFieldWords(bool radioConnected, bool separate, bool tcpReported, bool udpReported) =>
            radioConnected
                ? PortFieldPresentation(separate, tcpReported, udpReported)
                : PortFieldPresentation(separate, tcpReported: true, udpReported: true);

        /// <summary>
        /// Puts the right words on the port boxes for what they hold now,
        /// against the ports recorded at the last refill and whether that
        /// refill was of a connected radio.
        /// </summary>
        private void ApplyPortFieldWords()
        {
            if (PortForwardTcpBox == null || PortForwardUdpBox == null
                || PortForwardTcpLabel == null || PortForwardUdpLabel == null
                || PortForwardSeparatePortsCheck == null) return;
            var words = ShownPortFieldWords(
                _portWordsRadioConnected,
                PortForwardSeparatePortsCheck.IsChecked == true,
                PortBoxReported(_reportedPortTcp, PortForwardTcpBox.Text),
                PortBoxReported(_reportedPortUdp, PortForwardUdpBox.Text));
            PortForwardTcpLabel.Text = words.TcpLabel;
            AutomationProperties.SetName(PortForwardTcpBox, words.TcpName);
            AutomationProperties.SetHelpText(PortForwardTcpBox, words.TcpHelp);
            PortForwardUdpLabel.Text = words.UdpLabel;
            AutomationProperties.SetName(PortForwardUdpBox, words.UdpName);
            AutomationProperties.SetHelpText(PortForwardUdpBox, words.UdpHelp);
        }

        /// <summary>
        /// The connected refill: records what the radio reported (-1 for none)
        /// and puts the words on the boxes.
        /// </summary>
        private void ShowPortFieldWords(int reportedTcp, int reportedUdp)
        {
            _portWordsRadioConnected = true;
            _reportedPortTcp = reportedTcp;
            _reportedPortUdp = reportedUdp;
            ApplyPortFieldWords();
        }

        /// <summary>
        /// The first load and the disconnected refill: no radio, so nothing
        /// reported, and the boxes keep today's plain words (repair round 3).
        /// </summary>
        private void ShowPortFieldWordsWithNoRadio()
        {
            _portWordsRadioConnected = false;
            _reportedPortTcp = -1;
            _reportedPortUdp = -1;
            ApplyPortFieldWords();
        }
    }
}
