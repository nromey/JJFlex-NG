#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;
using Radios.Facts;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Where the operator reads what the radio told him that did not land, and
    /// the history of the things that could not wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the store, not the radio.</b> That is the whole reason this
    /// is a separate window rather than a panel in Status: the status surface
    /// returns early both when there is no radio and when the radio is
    /// disconnected, so putting the backlog there would hide it in exactly the
    /// two situations where somebody goes looking. Nothing in this class
    /// touches a <c>Rig</c>, and it must keep working with none, with a
    /// disconnected one, with the picker open, and after the screen reader
    /// binding has been replaced.
    /// </para>
    /// <para>
    /// <b>Nothing here acknowledges anything by accident.</b> Opening the
    /// window, moving focus, pressing Ctrl and asking for a replay all
    /// acknowledge nothing. Only the explicit buttons do, and each one is
    /// scoped to the selected record and to the material revision that was on
    /// screen when it was pressed.
    /// </para>
    /// <para>
    /// <b>Reading never transmits</b> and never re-runs whatever the record is
    /// about. It renders words.
    /// </para>
    /// </remarks>
    public partial class UndeliveredDetailsDialog : JJFlexDialog
    {
        private readonly FactStore _store;
        private readonly List<Fact> _shown = new();

        /// <summary>
        /// Open the surface against a store. Defaults to the application's,
        /// which exists for the whole run and outlives every reader binding.
        /// </summary>
        public UndeliveredDetailsDialog(FactStore? store = null)
        {
            _store = store ?? ApplicationFacts.Store;

            InitializeComponent();

            Title = Lexicon.Get("facts.window.title");
            PendingView.Content = Lexicon.Get("facts.window.pending_label");
            HistoryView.Content = Lexicon.Get("facts.window.history_label");
            ReadButton.Content = Lexicon.Get("facts.action.read");
            ReviewedButton.Content = Lexicon.Get("facts.action.reviewed");
            ResumeButton.Content = Lexicon.Get("facts.action.resume");
            CloseButton.Content = Lexicon.Get("facts.window.close");

            AutomationProperties.SetName(this, Title);
            AutomationProperties.SetName(Items, Lexicon.Get("facts.window.pending_label"));
            AutomationProperties.SetName(DetailText, Lexicon.Get("facts.window.detail_label"));
            AutomationProperties.SetName(ReadButton, ReadButton.Content?.ToString() ?? string.Empty);
            AutomationProperties.SetName(ReviewedButton, ReviewedButton.Content?.ToString() ?? string.Empty);
            AutomationProperties.SetName(ResumeButton, ResumeButton.Content?.ToString() ?? string.Empty);
            AutomationProperties.SetName(CloseButton, CloseButton.Content?.ToString() ?? string.Empty);

            Rebuild();
        }

        /// <summary>The record the operator has selected, or null.</summary>
        public Fact? Selected =>
            Items.SelectedIndex >= 0 && Items.SelectedIndex < _shown.Count
                ? _shown[Items.SelectedIndex]
                : null;

        /// <summary>
        /// Rebuild the list from the store.
        /// </summary>
        /// <remarks>
        /// <b>Preserves the selection across a rebuild</b> by episode rather
        /// than by index, because a row arriving above the selected one would
        /// otherwise move the reading position out from under somebody who was
        /// part way through reading it.
        /// </remarks>
        public void Rebuild()
        {
            string? keep = Selected?.Identity.EpisodeId;

            _shown.Clear();
            _shown.AddRange(HistoryView.IsChecked == true ? _store.History() : _store.Pending());

            var rows = new List<string>(_shown.Count);
            foreach (Fact fact in _shown) rows.Add(Describe(fact));

            Items.ItemsSource = rows;
            AutomationProperties.SetName(Items,
                Lexicon.Get(HistoryView.IsChecked == true
                    ? "facts.window.history_label"
                    : "facts.window.pending_label"));

            int at = keep == null ? -1 : _shown.FindIndex(f => f.Identity.EpisodeId == keep);
            if (at < 0 && _shown.Count > 0) at = 0;
            Items.SelectedIndex = at;

            if (_shown.Count == 0)
            {
                DetailText.Text = Lexicon.Get(HistoryView.IsChecked == true
                    ? "facts.window.nothing_historical"
                    : "facts.window.nothing_pending");
            }

            UpdateButtons();
        }

        /// <summary>
        /// One row: which radio, when it was observed, and where it stands.
        /// </summary>
        /// <remarks>
        /// The station is named even when there is only one, because the list
        /// defaults to every station's records and a row that names none reads
        /// as belonging to whichever radio happens to be selected.
        /// </remarks>
        private static string Describe(Fact fact)
        {
            string station = fact.Identity.RadioIdentity
                ?? Lexicon.Get("facts.row.unknown_station");

            string state = fact.AutomaticPaused
                ? Lexicon.Get("facts.state.paused")
                : fact.Classification != DeliveryClassification.Message
                    ? Lexicon.Get("facts.state.silent_no_metadata")
                    : fact.Validity.State switch
                    {
                        ValidityState.Current => Lexicon.Get("facts.state.current"),
                        ValidityState.Unknown => Lexicon.Get("facts.state.unknown"),
                        _ => fact.Validity.IsResolved
                            ? Lexicon.Get("facts.state.resolved")
                            : Lexicon.Get("facts.state.historical"),
                    };

            return Lexicon.Get("facts.row.summary",
                ("station", station),
                ("when", fact.Provenance.ObservedUtc.ToLocalTime()
                    .ToString("t", CultureInfo.CurrentCulture)),
                ("state", state));
        }

        /// <summary>
        /// The detail for the selected record — regular selectable read-only
        /// text, exposed through the platform's own text pattern.
        /// </summary>
        private void ShowDetail(Fact? fact)
        {
            if (fact == null)
            {
                DetailText.Text = Lexicon.Get("facts.detail.no_selection");
                return;
            }

            var lines = new List<string> { Describe(fact) };

            if (!string.IsNullOrEmpty(fact.Detail)) lines.Add(fact.Detail);
            if (fact.DetailTruncated) lines.Add(Lexicon.Get("facts.detail.truncated"));
            if (fact.RestoredFromDisk) lines.Add(Lexicon.Get("facts.detail.restored"));

            lines.Add(DeliveryWords(fact));

            string? receipt = ReceiptWords(fact);
            if (receipt != null) lines.Add(receipt);

            DetailText.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
        }

        private static string DeliveryWords(Fact fact)
        {
            if (fact.Attempts.Count == 0) return Lexicon.Get("facts.delivery.not_attempted");

            return fact.Attempts[fact.Attempts.Count - 1].State switch
            {
                DeliveryState.Refused => Lexicon.Get("facts.delivery.refused"),
                DeliveryState.UnknownCompletion => Lexicon.Get("facts.delivery.unknown"),
                DeliveryState.PartialProgress => Lexicon.Get("facts.delivery.partial"),
                DeliveryState.Withdrawn => Lexicon.Get("facts.delivery.withdrawn"),
                DeliveryState.TrackedCompletion => Lexicon.Get("facts.delivery.completed"),
                _ => Lexicon.Get("facts.delivery.not_attempted"),
            };
        }

        /// <summary>
        /// What is known about the tone — and only what is known. A requested
        /// tone is not a tone anybody heard.
        /// </summary>
        private static string? ReceiptWords(Fact fact) => fact.Receipt switch
        {
            ReceiptState.Requested => Lexicon.Get("facts.receipt.requested"),
            ReceiptState.PlaybackReported => Lexicon.Get("facts.receipt.requested"),
            ReceiptState.Unavailable => Lexicon.Get("facts.receipt.unavailable"),
            ReceiptState.Suppressed => Lexicon.Get("facts.receipt.suppressed"),
            _ => null,
        };

        private void UpdateButtons()
        {
            Fact? fact = Selected;
            ReadButton.IsEnabled = fact != null;
            ReviewedButton.IsEnabled = fact != null && fact.IsPending;

            // Offered only when there is a pause to release, so the control is
            // never a button that does nothing.
            ResumeButton.IsEnabled = fact != null && fact.AutomaticPaused;
        }

        private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowDetail(Selected);
            UpdateButtons();
        }

        private void View_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            Rebuild();
        }

        /// <summary>
        /// Read the selected record.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately does nothing beyond moving focus to the text.</b>
        /// Asking to read a record is not a reason for this window to reach a
        /// speech backend on its own terms — the platform reads focused text,
        /// which is the route that keeps working when ours does not, and that
        /// is precisely the case this surface exists for. The scheduler-driven
        /// read, revalidated immediately before dispatch, belongs to the piece
        /// that owns the transport.
        /// </remarks>
        private void ReadButton_Click(object sender, RoutedEventArgs e)
        {
            DetailText.Focus();
            DetailText.CaretIndex = 0;
        }

        private void ReviewedButton_Click(object sender, RoutedEventArgs e)
        {
            Fact? fact = Selected;
            if (fact == null) return;

            // Scoped to the revision that was on screen. A newer one that
            // arrived behind the selected row while he was reading is not
            // covered by this press.
            _store.MarkReviewed(fact.Identity.EpisodeId, fact.MaterialRevision);
            Rebuild();
        }

        private void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            Fact? fact = Selected;
            if (fact == null) return;

            _store.ResumeAutomatic(fact.Identity.EpisodeId);
            Rebuild();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
