using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Dialogs;
using Radios;

namespace JJFlexWpf.Controls
{
    /// <summary>
    /// Radio network addressing: DHCP versus a fixed (static) address, with the
    /// gateway and subnet mask that go with it.
    ///
    /// Hosted in two places on purpose — Settings → Network, where someone who
    /// already knows what they want goes looking for it, and Settings → Radio
    /// Setup, where it appears as a numbered step for someone bringing a radio
    /// up for the first time. Both hosts get the same control, so the two
    /// screens cannot drift apart.
    ///
    /// This is the most dangerous setting JJ Flex exposes. Every other setting
    /// that goes wrong produces an error message; this one produces a radio that
    /// does not answer, and the only fix is someone standing in front of it. For
    /// a radio at a remote site that means a drive. So the flow is deliberately
    /// three-stage: validate arithmetic (<see cref="FlexBase.PreflightStaticIp"/>),
    /// surface warnings in a confirmation the user must actively accept, and only
    /// then send the command.
    /// </summary>
    public partial class StaticIpControl : UserControl
    {
        private FlexBase? _rig;

        /// <summary>
        /// The connected radio. Setting this refreshes the displayed state.
        /// Null or disconnected is a normal state, not an error — the control
        /// explains why it cannot act rather than disappearing.
        /// </summary>
        public FlexBase? Rig
        {
            get => _rig;
            set { _rig = value; Refresh(); }
        }

        /// <summary>
        /// Raised after the radio accepts a change, so a host can prompt for the
        /// restart that actually puts the new address into effect. Radio Setup
        /// uses this to point at its own restart step.
        /// </summary>
        public event EventHandler? AddressChanged;

        /// <summary>
        /// Suppresses the spoken announcement while <see cref="Refresh"/> sets
        /// the radio buttons programmatically. Without this, opening Settings
        /// speaks an address-mode announcement the user did not ask for.
        /// </summary>
        private bool _suppressAnnouncements;

        public StaticIpControl()
        {
            InitializeComponent();
            Refresh();
        }

        /// <summary>
        /// Re-read the radio's current addressing and update the display. Safe to
        /// call with no radio connected.
        /// </summary>
        public void Refresh()
        {
            if (CurrentStateText == null) return; // called before InitializeComponent completes

            _suppressAnnouncements = true;
            try
            {
                if (_rig == null || !_rig.IsConnected)
                {
                    CurrentStateText.Text =
                        "No radio is connected. Connect to the radio on your local network to change its address.";
                    SetFieldsEnabled(false);
                    DhcpRadio.IsChecked = true;
                    return;
                }

                var staticIp = _rig.CurrentStaticIP;
                bool isStatic = staticIp != null;

                var reachableAt = _rig.CurrentRadioIP?.ToString() ?? "an unknown address";
                CurrentStateText.Text = isStatic
                    ? $"The radio is set to the fixed address {staticIp}. JJ Flex is talking to it at {reachableAt}."
                    : $"The radio is using an automatic address from the router. JJ Flex is talking to it at {reachableAt}.";

                if (isStatic)
                {
                    IpBox.Text = staticIp!.ToString();
                    GatewayBox.Text = _rig.CurrentStaticGateway?.ToString() ?? string.Empty;
                    NetmaskBox.Text = _rig.CurrentStaticNetmask?.ToString() ?? string.Empty;
                    StaticRadio.IsChecked = true;
                }
                else
                {
                    DhcpRadio.IsChecked = true;
                }

                SetFieldsEnabled(true);
            }
            finally
            {
                _suppressAnnouncements = false;
                UpdateFieldEnablement();
            }
        }

        private void SetFieldsEnabled(bool connected)
        {
            UseCurrentButton.IsEnabled = connected;
            ApplyAddressButton.IsEnabled = connected;
            UpdateFieldEnablement();
        }

        /// <summary>
        /// The address fields only mean something in fixed-address mode. Rather
        /// than disable them — which would leave them in the tab order announcing
        /// "unavailable" — they stay reachable but are cleared of the pretense of
        /// being editable when DHCP is selected.
        /// </summary>
        private void UpdateFieldEnablement()
        {
            bool useStatic = StaticRadio?.IsChecked == true;
            bool haveRadio = _rig != null && _rig.IsConnected;
            bool editable = useStatic && haveRadio;

            if (IpBox != null) IpBox.IsEnabled = editable;
            if (GatewayBox != null) GatewayBox.IsEnabled = editable;
            if (NetmaskBox != null) NetmaskBox.IsEnabled = editable;
            if (UseCurrentButton != null)
            {
                UseCurrentButton.IsEnabled = editable;

                // Over SmartLink this button can never work — JJ Flex sees the
                // address it reaches the radio THROUGH, not the radio's address
                // on its own network. Say so in the accessible name, so tabbing
                // onto the button announces the unavailability up front instead
                // of the press failing and then explaining (Noel, 2026-08-05;
                // Don's radio lives at Tony's, so this state is routine).
                bool remote = haveRadio && _rig!.IsWanConnection;
                System.Windows.Automation.AutomationProperties.SetName(UseCurrentButton, remote
                    ? "Fill in the fields using the address the radio is using right now. " +
                      "Not available over SmartLink — connect on the radio's own network to use this."
                    : "Fill in the fields using the address the radio is using right now");
            }
        }

        private void AddressModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            UpdateFieldEnablement();
            if (_suppressAnnouncements) return;

            ScreenReaderOutput.Speak(
                StaticRadio.IsChecked == true
                    ? "Fixed address. Enter the address, gateway and subnet mask, then apply."
                    : "Automatic address from the router.",
                VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Fill the fields from the address the radio is using right now. This is
        /// the safest route to a fixed address: you are pinning a value that
        /// demonstrably works on that network at this moment.
        /// </summary>
        private void UseCurrentButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                Report("No radio is connected.", speak: "No radio connected.");
                return;
            }

            var suggestion = _rig.SuggestStaticFromCurrent();

            // Even when the suggestion is incomplete it usually knows the IP, so
            // hand back whatever it found rather than making the user retype it.
            if (!string.IsNullOrEmpty(suggestion.Ip)) IpBox.Text = suggestion.Ip;
            if (!string.IsNullOrEmpty(suggestion.Gateway)) GatewayBox.Text = suggestion.Gateway;
            if (!string.IsNullOrEmpty(suggestion.Netmask)) NetmaskBox.Text = suggestion.Netmask;

            if (!suggestion.Available)
            {
                // Speak the reason ITSELF. "See the message" is a dead end for a
                // screen reader user — the information exists, so it goes to the
                // ear, not to a text box the user must go find (Noel, 2026-08-05).
                Report(suggestion.Reason, speak: suggestion.Reason);
                if (string.IsNullOrEmpty(GatewayBox.Text)) GatewayBox.Focus();
                else if (string.IsNullOrEmpty(NetmaskBox.Text)) NetmaskBox.Focus();
                return;
            }

            StaticRadio.IsChecked = true;

            string note = suggestion.Warnings.Count > 0
                ? " " + string.Join(" ", suggestion.Warnings)
                : string.Empty;
            Report($"Filled in {suggestion.Ip}, gateway {suggestion.Gateway}, subnet mask {suggestion.Netmask}. Nothing has been sent to the radio yet — choose Apply to radio." + note,
                   speak: $"Filled in {suggestion.Ip}. Choose apply to radio to send it.");
        }

        private void ApplyAddressButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                Report("No radio is connected.", speak: "No radio connected.");
                return;
            }

            if (DhcpRadio.IsChecked == true)
            {
                ApplyDhcp();
                return;
            }

            var check = _rig.PreflightStaticIp(IpBox.Text.Trim(), GatewayBox.Text.Trim(), NetmaskBox.Text.Trim());
            if (!check.CanProceed)
            {
                Report(check.BlockReason, speak: check.BlockReason);
                FocusFieldForBlockReason(check.BlockReason);
                return;
            }

            var confirm = new ConfirmActionDialog(
                "Confirm Fixed Address",
                $"JJ Flex will tell the radio to use the fixed address {IpBox.Text.Trim()}, gateway {GatewayBox.Text.Trim()}, subnet mask {NetmaskBox.Text.Trim()}. The radio takes the new address the next time it restarts.",
                check.Warnings,
                question: "If any of this is wrong, the radio may not come back on the network and will need someone at the radio to fix it. Continue?",
                yesLabel: "_Apply");

            if (confirm.ShowDialog() != true)
            {
                Report("Cancelled. Nothing was sent to the radio.", speak: "Cancelled.");
                return;
            }

            string ip = IpBox.Text.Trim();
            bool sent = _rig.ApplyStaticIp(
                ip, GatewayBox.Text.Trim(), NetmaskBox.Text.Trim(),
                onSuccess: () => Dispatcher.Invoke(() =>
                {
                    Report($"The radio accepted the fixed address {ip}. It takes effect the next time the radio restarts.",
                           speak: "Fixed address accepted. Restart the radio to use it.");
                    AddressChanged?.Invoke(this, EventArgs.Empty);
                    Refresh();
                }),
                onFailure: () => Dispatcher.Invoke(() =>
                    Report("The radio rejected the address. It is still using its previous settings.",
                           speak: "The radio rejected the address.")));

            if (!sent)
                Report("The address could not be sent. See the trace file for details.", speak: "Could not send the address.");
            else
                Report("Sent to the radio, waiting for it to answer...", speak: "Sent, waiting.");
        }

        private void ApplyDhcp()
        {
            if (_rig == null) return;

            // Nothing to do if the radio is already on DHCP — say so rather than
            // sending a command and reporting a success the user cannot tell from
            // a no-op.
            if (_rig.CurrentStaticIP == null)
            {
                Report("The radio is already getting its address automatically. Nothing to change.",
                       speak: "Already automatic.");
                return;
            }

            var confirm = new ConfirmActionDialog(
                "Confirm Automatic Address",
                "JJ Flex will tell the radio to go back to getting its address from the router. The change takes effect the next time the radio restarts.",
                new[]
                {
                    "The radio's address may change after it restarts, so any port forwarding rule on your router that points at its current address will need updating.",
                },
                question: "Continue?",
                yesLabel: "_Apply");

            if (confirm.ShowDialog() != true)
            {
                Report("Cancelled. Nothing was sent to the radio.", speak: "Cancelled.");
                return;
            }

            bool sent = _rig.RevertToDhcp(
                onSuccess: () => Dispatcher.Invoke(() =>
                {
                    Report("The radio accepted the change and will get its address from the router after it restarts.",
                           speak: "Automatic address accepted. Restart the radio to use it.");
                    AddressChanged?.Invoke(this, EventArgs.Empty);
                    Refresh();
                }),
                onFailure: () => Dispatcher.Invoke(() =>
                    Report("The radio rejected the change. It is still using its previous settings.",
                           speak: "The radio rejected the change.")));

            if (!sent)
                Report("The change could not be sent. See the trace file for details.", speak: "Could not send the change.");
            else
                Report("Sent to the radio, waiting for it to answer...", speak: "Sent, waiting.");
        }

        /// <summary>
        /// Put keyboard focus on whichever field the preflight complained about,
        /// so a screen reader user lands on the thing they need to fix rather than
        /// hunting for it after hearing the message.
        /// </summary>
        private void FocusFieldForBlockReason(string reason)
        {
            if (reason.Contains("gateway", StringComparison.OrdinalIgnoreCase)) GatewayBox.Focus();
            else if (reason.Contains("subnet mask", StringComparison.OrdinalIgnoreCase)) NetmaskBox.Focus();
            else if (reason.Contains("IP address", StringComparison.OrdinalIgnoreCase)) IpBox.Focus();
        }

        private void Report(string text, string speak)
        {
            ResultText.Text = text;
            ScreenReaderOutput.Speak(speak, VerbosityLevel.Terse, interrupt: true);
        }
    }
}
