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
            string tag = IsThisClient ? " (This client)" : "";
            string slices = !string.IsNullOrEmpty(OwnedSlices) ? $" — Slices: {OwnedSlices}" : "";
            string station = !string.IsNullOrEmpty(Station) ? $" on {Station}" : "";
            return $"{Program}{station}{slices}{tag}";
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
    }

    public partial class MultiFlexDialog : JJFlexDialog
    {
        private readonly MultiFlexCallbacks _callbacks;

        public MultiFlexDialog(MultiFlexCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            InitializeComponent();
            RefreshClientList();
        }

        private void RefreshClientList()
        {
            ClientList.Items.Clear();
            var clients = _callbacks.GetClients();
            foreach (var client in clients)
                ClientList.Items.Add(client);

            SummaryText.Text = clients.Count == 1
                ? "1 client connected:"
                : $"{clients.Count} clients connected:";

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

            string sliceInfo = !string.IsNullOrEmpty(selected.OwnedSlices)
                ? $"\n\nSlices {selected.OwnedSlices} will be released."
                : "";

            var result = MessageBox.Show(
                $"Disconnect {selected.Program} on {selected.Station}?{sliceInfo}",
                "Disconnect Client",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes) return;

            if (_callbacks.DisconnectClient(selected.Handle))
            {
                ScreenReaderOutput.Speak($"{selected.Program} disconnected", true);
                // Brief delay then refresh
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                    Dispatcher.BeginInvoke(RefreshClientList));
            }
            else
            {
                ScreenReaderOutput.Speak("Failed to disconnect client", true);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
