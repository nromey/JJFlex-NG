using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Info about a connected MultiFlex GUI client for display.
    /// </summary>
    public class MultiFlexClientInfo
    {
        public string Program { get; set; } = "";
        public string Station { get; set; } = "";
        public uint Handle { get; set; }
        public bool IsThisClient { get; set; }
        public string OwnedSlices { get; set; } = "";

        public override string ToString()
        {
            string tag = IsThisClient ? Lexicon.Get("connect.multiflex.this_client_tag") : "";
            string slices = !string.IsNullOrEmpty(OwnedSlices)
                ? Lexicon.Get("connect.multiflex.slices_suffix", ("ownedSlices", OwnedSlices))
                : "";
            string station = !string.IsNullOrEmpty(Station)
                ? Lexicon.Get("connect.multiflex.station_suffix", ("station", Station))
                : "";
            return Lexicon.Get("connect.multiflex.client_line",
                ("program", Program), ("station", station), ("slices", slices), ("tag", tag));
        }
    }

    /// <summary>
    /// Callbacks for the MultiFlex dialog.
    /// </summary>
    public class MultiFlexCallbacks
    {
        /// <summary>Returns the list of connected clients.</summary>
        public required Func<List<MultiFlexClientInfo>> GetClients { get; init; }

        /// <summary>Disconnect a client by handle. Returns true if successful.</summary>
        public required Func<uint, bool> DisconnectClient { get; init; }

        /// <summary>
        /// Subscribe a handler that runs whenever a MultiFlex client is added,
        /// removed, or updated. Optional — if null, the dialog falls back to
        /// refresh-on-open-only behavior.
        /// </summary>
        public Action<Action>? SubscribeClientListChanged { get; init; }

        /// <summary>Unsubscribe a handler previously passed to SubscribeClientListChanged.</summary>
        public Action<Action>? UnsubscribeClientListChanged { get; init; }
    }

    public partial class MultiFlexDialog : JJFlexDialog
    {
        private readonly MultiFlexCallbacks _callbacks;

        public MultiFlexDialog(MultiFlexCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            InitializeComponent();
            RefreshClientList();

            if (_callbacks.SubscribeClientListChanged != null)
            {
                _callbacks.SubscribeClientListChanged(OnClientListChanged);
                Closed += OnDialogClosed;
            }
        }

        private void OnDialogClosed(object? sender, EventArgs e)
        {
            Closed -= OnDialogClosed;
            _callbacks.UnsubscribeClientListChanged?.Invoke(OnClientListChanged);
        }

        private void OnClientListChanged()
        {
            // Event fires on FlexLib's receive thread; marshal to the UI thread
            // before touching WPF controls.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnClientListChanged));
                return;
            }
            RefreshClientList();
        }

        private void RefreshClientList()
        {
            ClientList.Items.Clear();
            var clients = _callbacks.GetClients();
            foreach (var client in clients)
                ClientList.Items.Add(client);

            SummaryText.Text = clients.Count == 1
                ? Lexicon.Get("connect.multiflex.one_client")
                : Lexicon.Get("connect.multiflex.many_clients", ("count", clients.Count));

            if (ClientList.Items.Count > 0)
                ClientList.SelectedIndex = 0;

            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            var selected = ClientList.SelectedItem as MultiFlexClientInfo;
            // Can't disconnect yourself
            DisconnectButton.IsEnabled = selected != null && !selected.IsThisClient;
        }

        private void ClientList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtonStates();
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ClientList.SelectedItem as MultiFlexClientInfo;
            if (selected == null || selected.IsThisClient) return;

            var confirm = new ConfirmActionDialog(
                Lexicon.Get("connect.multiflex.disconnect_title"),
                Lexicon.Get("connect.multiflex.disconnect_body",
                    ("program", selected.Program), ("station", selected.Station)),
                warnings: string.IsNullOrEmpty(selected.OwnedSlices)
                    ? null
                    : new[] { Lexicon.Get("connect.multiflex.disconnect_warning",
                        ("ownedSlices", selected.OwnedSlices)) },
                question: Lexicon.Get("connect.multiflex.disconnect_question"),
                yesLabel: Lexicon.Get("connect.multiflex.disconnect_yes"));

            if (confirm.ShowDialog() != true) return;

            if (_callbacks.DisconnectClient(selected.Handle))
            {
                ScreenReaderOutput.Speak(
                    Lexicon.Get("connect.multiflex.disconnected", ("program", selected.Program)), true);
                // Brief delay then refresh
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                    Dispatcher.BeginInvoke(RefreshClientList));
            }
            else
            {
                ScreenReaderOutput.Speak(Lexicon.Get("connect.multiflex.disconnect_failed"), true);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
