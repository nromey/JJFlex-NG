using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Info about a MultiFlex GUI client for display: who, and how we know.
    /// </summary>
    /// <remarks>
    /// Two rows can name the same client and mean different things (#634,
    /// ruled 2026-09-26): one the radio itself reported connected on this
    /// connection, and one only SmartLink's list or a discovery broadcast has
    /// mentioned. The second is shown, labelled as reported, and cannot be
    /// disconnected from here until the radio identifies it. A third state
    /// is a client the radio once confirmed that a list has since stopped
    /// mentioning — kept, because a list can omit a live client, and shown as
    /// possibly gone rather than gone.
    ///
    /// The client sentences are Noel's wording, APPROVED 2026-09-30 with
    /// only false facts corrected on his authority (Track L10). The two
    /// no-station companions in ClientRowPhrase followed, approved
    /// 2026-10-01 exactly as written (Track L13).
    /// </remarks>
    public class MultiFlexClientInfo
    {
        public MultiFlexClientInfo(ClientRow row) { Row = row; }

        /// <summary>The roster's row, with how we know about it.</summary>
        public ClientRow Row { get; }

        public string Program => Row.Program;
        public string Station => Row.Station;
        public uint Handle => Row.Handle;
        public bool IsThisClient => Row.IsThisClient;
        public string OwnedSlices => Row.OwnedSlices;
        public bool ConfirmedByRadio => Row.ConfirmedByRadio;
        public bool MayHaveLeft => Row.MayHaveLeft;

        /// <summary>The list row's words live in <see cref="ClientRowPhrase"/>,
        /// in Radios, where the suite reads them assembled without a window.
        /// The not-yet-confirmed rows are terse and chatty pairs, read at
        /// the operator's verbosity as the picker's last-seen row is.</summary>
        public override string ToString() => ClientRowPhrase.Line(Row, ScreenReaderOutput.CurrentVerbosity);
    }

    /// <summary>
    /// Callbacks for the MultiFlex dialog.
    /// </summary>
    public class MultiFlexCallbacks
    {
        /// <summary>Returns the list of clients the roster holds.</summary>
        public required Func<List<MultiFlexClientInfo>> GetClients { get; init; }

        /// <summary>Disconnect a client by handle. Returns true if the request was sent.</summary>
        public required Func<uint, bool> DisconnectClient { get; init; }

        /// <summary>
        /// True while the rig cannot say who is on the radio at all — its own
        /// handle is not established, so an empty list would mean "unknown",
        /// not "empty". Optional; null reads as false.
        /// </summary>
        public Func<bool>? ClientInformationUnavailable { get; init; }

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

        /// <summary>The outcome of the operator's last disconnect request,
        /// kept on the readable line until they move to another row or the
        /// radio reports the client gone (#643, ruled in Track L7).</summary>
        private readonly DisconnectOutcomeLine _outcome = new DisconnectOutcomeLine();

        /// <summary>True while the list is being rebuilt. Clearing and
        /// re-selecting the rows raises SelectionChanged, and those are not
        /// the operator moving to another row, so they must not end an
        /// outcome the line is holding.</summary>
        private bool _refreshing;

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
            _refreshing = true;
            try
            {
                var selectedHandle = (ClientList.SelectedItem as MultiFlexClientInfo)?.Handle;
                ClientList.Items.Clear();
                var clients = _callbacks.GetClients();
                foreach (var client in clients)
                    ClientList.Items.Add(client);

                bool unavailable = _callbacks.ClientInformationUnavailable?.Invoke() == true;
                SummaryText.Text = ClientRowPhrase.Summary(clients.Select(c => c.Row).ToList(), unavailable);

                if (ClientList.Items.Count > 0)
                {
                    // Keep the operator's place across a refresh when the row is
                    // still there; otherwise start at the top.
                    int keep = -1;
                    if (selectedHandle.HasValue)
                    {
                        for (int i = 0; i < ClientList.Items.Count; i++)
                            if (((MultiFlexClientInfo)ClientList.Items[i]).Handle == selectedHandle.Value) { keep = i; break; }
                    }
                    ClientList.SelectedIndex = keep >= 0 ? keep : 0;
                }
            }
            finally
            {
                _refreshing = false;
            }

            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            var selected = (ClientList.SelectedItem as MultiFlexClientInfo)?.Row;
            // Can't disconnect yourself, and can't disconnect a client only a
            // list has reported: the radio has to identify it first (#634).
            DisconnectButton.IsEnabled = ClientRowPhrase.MayDisconnect(selected);
            // The line holds the last request's outcome while it still
            // describes the selected row, so no refresh can erase it (#643).
            ShowDisconnectReason(_outcome.TextFor(selected));
        }

        /// <summary>
        /// A sentence the operator can READ under the list, or nothing. The
        /// disabled button says nothing about why, and speech does not survive
        /// a focus change (#643), so the reason lives in a control.
        /// </summary>
        private void ShowDisconnectReason(string? text)
        {
            DisconnectReasonText.Text = text ?? "";
            DisconnectReasonText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ClientList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // A rebuild's own selection changes are not the operator moving.
            if (_refreshing) return;
            UpdateButtonStates();
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ClientList.SelectedItem as MultiFlexClientInfo;
            if (selected == null || !ClientRowPhrase.MayDisconnect(selected.Row)) return;

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

            // The roster can change while the confirmation is open. Re-read
            // it: the handle must still be there and still the radio's own
            // word, or the request would go to a client the operator did not
            // look at (#634).
            var now = _callbacks.GetClients().FirstOrDefault(c => c.Handle == selected.Handle);
            if (now == null || !ClientRowPhrase.MayDisconnect(now.Row))
            {
                string changed = Lexicon.Get("connect.multiflex.changed_while_confirming"); // Sentence 7, approved 2026-09-30.
                RefreshClientList();
                ShowDisconnectReason(changed);
                ScreenReaderOutput.Speak(changed, true);
                return;
            }

            if (_callbacks.DisconnectClient(selected.Handle))
            {
                // The request went to the radio; whether the client left is
                // the radio's to report. Sentence 8 (approved 2026-09-30)
                // replaces a line that claimed the disconnect had happened.
                // It stays on the line until the operator moves to another
                // row or the radio reports the client gone; the refresh below
                // reads it back rather than overwriting it (#643, Sol's
                // review of L6).
                string sent = Lexicon.Get("connect.multiflex.disconnect_requested", ("station", selected.Row.NameForSentence));
                _outcome.Record(selected.Handle, sent);
                ShowDisconnectReason(sent);
                ScreenReaderOutput.Speak(sent, true);
                // Brief delay then refresh
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                    Dispatcher.BeginInvoke(RefreshClientList));
            }
            else
            {
                // Readable too, on the same line and on the same terms: a
                // failure that lived only in speech was lost with it (#643).
                string failed = Lexicon.Get("connect.multiflex.disconnect_failed");
                _outcome.Record(selected.Handle, failed);
                ShowDisconnectReason(failed);
                ScreenReaderOutput.Speak(failed, true);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
