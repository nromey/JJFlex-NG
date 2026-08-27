using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The way back out of "Don't show this again" (task #267).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a named list and not a reset button.</b> "Reset all
    /// warnings" tells an operator nothing about what he silenced or when, and
    /// it takes back the messages he silenced ON PURPOSE along with the one he
    /// wants returned. Faced with that trade he does not press it, and the
    /// escape hatch may as well not exist. So the list names each message, says
    /// when it went quiet, and restores them one at a time. Restore-all is here
    /// too, because sometimes a clean slate genuinely is the answer — but it is
    /// the second option, not the only one.</para>
    ///
    /// <para><b>Order the list was one-way for a long time.</b> Until Sprint 36
    /// the store could only grow: <c>IsSuppressed</c> and <c>Suppress</c>, no
    /// unsuppress, no enumeration. It was already live on three surfaces, one
    /// of them a confirmation dialog. This window is the half of #267 that had
    /// to exist BEFORE the exit prompt was allowed its own checkbox — adding
    /// another door while nothing could reopen one would have made the problem
    /// bigger.</para>
    ///
    /// <para><b>Never opened cold from a chord.</b> It is reached from Settings
    /// → Notifications, which already says how many messages are silenced, so
    /// nobody arrives here to find an empty window. It still handles empty
    /// gracefully: an operator can restore the last one and stay.</para>
    /// </remarks>
    public partial class SilencedMessagesDialog : JJFlexDialog
    {
        /// <summary>
        /// One line of the list. The key and the words that describe it live on
        /// the SAME object, so the restore button can never act on a different
        /// row from the one that was read out. A parallel list of keys beside a
        /// list of strings is the same drift this whole task is about, one
        /// layer up.
        /// </summary>
        private sealed class Row
        {
            public Row(string key, string text) { Key = key; Text = text; }
            public string Key { get; }
            public string Text { get; }
            public override string ToString() => Text;
        }

        private readonly AdvisorySuppressionStore _store;

        public SilencedMessagesDialog(AdvisorySuppressionStore? store = null)
        {
            InitializeComponent();
            _store = store ?? AdvisorySuppression.Default;

            Loaded += (_, _) => Reload(selectIndex: 0, status: null);
        }

        /// <summary>
        /// Rebuild the list from the store and say where things stand.
        /// </summary>
        /// <remarks>
        /// Re-read rather than mutating a cached copy. The status line the
        /// retired trace dialog used to get wrong ("Start tracing" for a trace
        /// already running) was a cached copy of somebody else's state; a list
        /// that re-reads cannot disagree with the file it is describing.
        /// </remarks>
        private void Reload(int selectIndex, string? status)
        {
            IReadOnlyList<SuppressedAdvisory> entries = _store.Snapshot();
            List<Row> rows = entries.Select(e => new Row(e.Key, e.Sentence())).ToList();

            SilencedList.ItemsSource = rows;

            LeadText.Text = rows.Count == 0
                ? Lexicon.Get("settings.silenced.lead_empty")
                : Lexicon.Get("settings.silenced.lead");
            AutomationProperties.SetName(LeadText, LeadText.Text);

            StatusText.Text = status ?? string.Empty;
            AutomationProperties.SetName(StatusText, StatusText.Text);
            // An empty status line is not a thing to land on. Keep it out of
            // the tab order until it has something to say.
            StatusText.Focusable = StatusText.Text.Length > 0;

            // A disabled button is skipped by Tab in WPF, which is what we
            // want: nothing in this window should be reachable and inert.
            RestoreAllButton.IsEnabled = rows.Count > 0;

            if (rows.Count > 0)
                SilencedList.SelectedIndex = Math.Min(Math.Max(selectIndex, 0), rows.Count - 1);
            else
                SilencedList.SelectedIndex = -1;

            UpdateRestoreButton();
        }

        private void UpdateRestoreButton()
        {
            RestoreButton.IsEnabled = SilencedList.SelectedItem is Row;
        }

        private void SilencedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRestoreButton();
        }

        /// <summary>
        /// Focus the list, not a button. The operator came to read what is in
        /// it; focusing the item CONTAINER rather than the ListBox is what
        /// makes the screen reader announce the message rather than the list.
        /// Same reasoning, and same OnContentRendered caveat, as ProblemsDialog.
        /// </summary>
        protected override void FocusFirstControl()
        {
            try
            {
                SilencedList.UpdateLayout();
                if (SilencedList.SelectedIndex >= 0 &&
                    SilencedList.ItemContainerGenerator.ContainerFromIndex(SilencedList.SelectedIndex)
                        is ListBoxItem item &&
                    item.Focus())
                {
                    return;
                }
                if (SilencedList.Focus()) return;
            }
            catch { }
            base.FocusFirstControl();
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (SilencedList.SelectedItem is not Row row) return;

            int index = SilencedList.SelectedIndex;
            string label = row.Text;

            string status = _store.Unsuppress(row.Key)
                // The label, not "done": the operator may have arrowed past
                // several rows since he last heard one read out, and a receipt
                // that does not name its subject is a receipt he has to trust.
                ? Lexicon.Get("settings.silenced.restored_one", ("label", label))
                : Lexicon.Get("settings.silenced.restored_nothing");

            // Rebuild first, THEN speak. The list changing moves the selection,
            // and a screen reader announces the newly selected row; speaking
            // first would have the receipt cut off by that announcement.
            Reload(selectIndex: index, status: status);
            Say(status);

            // Restoring the last one empties the list and disables the button
            // that was just pressed. WPF puts focus somewhere arbitrary when
            // the focused element goes disabled; put it where the operator is
            // going next instead.
            if (SilencedList.Items.Count == 0)
            {
                try { CloseButton.Focus(); } catch { }
            }
        }

        private void RestoreAllButton_Click(object sender, RoutedEventArgs e)
        {
            int restored = _store.Clear();
            string status =
                restored == 0 ? Lexicon.Get("settings.silenced.restored_nothing") :
                restored == 1 ? Lexicon.Get("settings.silenced.restored_all_one") :
                Lexicon.Get("settings.silenced.restored_all_many", ("count", restored));

            Reload(selectIndex: 0, status: status);
            Say(status);

            // The list is empty now and the buttons that acted on it are gone
            // from the tab order, so focus has nowhere sensible to sit. Close
            // is where the operator is going next.
            if (restored > 0)
            {
                try { CloseButton.Focus(); } catch { }
            }
        }

        /// <summary>
        /// Speak a receipt for something the operator just did.
        /// </summary>
        /// <remarks>
        /// Terse rather than Critical: this is a confirmation of a deliberate
        /// act, which is exactly the tier Terse describes, and the same words
        /// are on the status line for anyone running with speech turned down.
        /// Interrupting is right here — whatever is in flight is the list
        /// announcing a row that has just stopped being true.
        /// </remarks>
        private static void Say(string text)
        {
            try { ScreenReaderOutput.Speak(text, VerbosityLevel.Terse, true); }
            catch { /* a receipt that cannot be spoken is still on the status line */ }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();
        }
    }
}
