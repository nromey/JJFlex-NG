#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Radios;
using Radios.Facts;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Where the operator reads what the radio told him that did not land,
    /// the history of the things that could not wait, and the store's own
    /// problems.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the store, not the radio.</b> The status surface returns
    /// early both when there is no radio and when the radio is disconnected,
    /// so the backlog is a separate window that asks nothing of a rig.
    /// </para>
    /// <para>
    /// <b>It binds to immutable snapshots.</b> Rows are the presenter's
    /// <see cref="ItemSnapshot"/>s, updated in place by stable identity; the
    /// detail is a <see cref="RenderedDetailSnapshot"/> whose token becomes the
    /// shown token only after its text is in the control. The review button
    /// sends THAT token — it never reads the current fact at click time, so a
    /// newer revision arriving while the operator reads the older one stays
    /// owed.
    /// </para>
    /// <para>
    /// <b>Nothing here acknowledges anything by accident, and nothing here
    /// speaks.</b> Opening, arrowing, focusing, Ctrl and background updates
    /// review nothing; only the explicit button does. Updates arrive marshalled
    /// onto this window's thread, coalesced, and are unsubscribed on close.
    /// </para>
    /// </remarks>
    public partial class UndeliveredDetailsDialog : JJFlexDialog
    {
        private readonly FactListPresenter _presenter;
        private readonly FactListView _view;
        private readonly ObservableCollection<RowItem> _rows = new();
        private RenderedDetailSnapshot? _shown;
        private string? _shownSignature;
        private FactListSnapshot? _latest;
        private int _updatePending;
        private bool _closed;

        /// <summary>
        /// Open the surface against a store. Defaults to the application's,
        /// which exists for the whole run and outlives every reader binding.
        /// </summary>
        public UndeliveredDetailsDialog(FactStore? store = null)
        {
            _presenter = new FactListPresenter(store ?? ApplicationFacts.Store);
            _view = _presenter.OpenView();

            InitializeComponent();

            Title = Lexicon.Get("facts.window.title");
            PendingView.Content = Lexicon.Get("facts.window.pending_label");
            HistoryView.Content = Lexicon.Get("facts.window.history_label");
            ReadButton.Content = Lexicon.Get("facts.action.read");
            ReviewedButton.Content = Lexicon.Get("facts.action.reviewed");
            ResumeButton.Content = Lexicon.Get("facts.action.resume");
            RefreshButton.Content = Lexicon.Get("facts.action.refresh");
            CloseButton.Content = Lexicon.Get("facts.window.close");
            ChangedNotice.Text = Lexicon.Get("facts.detail.changed");

            AutomationProperties.SetName(this, Title);
            AutomationProperties.SetName(Items, Lexicon.Get("facts.window.pending_label"));
            AutomationProperties.SetName(DetailText, Lexicon.Get("facts.window.detail_label"));
            AutomationProperties.SetName(ReadButton, ReadButton.Content?.ToString() ?? string.Empty);
            AutomationProperties.SetName(ReviewedButton, ReviewedButton.Content?.ToString() ?? string.Empty);
            AutomationProperties.SetName(ResumeButton, ResumeButton.Content?.ToString() ?? string.Empty);
            AutomationProperties.SetName(RefreshButton, RefreshButton.Content?.ToString() ?? string.Empty);
            AutomationProperties.SetName(CloseButton, CloseButton.Content?.ToString() ?? string.Empty);

            Items.ItemsSource = _rows;

            _presenter.Store.ProjectionChanged += OnProjectionChanged;
            Closed += (_, _) =>
            {
                _closed = true;
                _presenter.Store.ProjectionChanged -= OnProjectionChanged;
                _view.Close();
            };

            Rebuild();
        }

        /// <summary>The view this window acts through. For the surface tests.</summary>
        internal FactListView View => _view;

        /// <summary>The detail snapshot currently installed in the text control, or null.</summary>
        internal RenderedDetailSnapshot? ShownDetail => _shown;

        /// <summary>The newest projection this window has applied.</summary>
        internal FactListSnapshot? LatestProjection => _latest;

        private FactView CurrentView => HistoryView.IsChecked == true ? FactView.History : FactView.Pending;

        /// <summary>The selected row's snapshot, or null.</summary>
        public ItemSnapshot? Selected => (Items.SelectedItem as RowItem)?.Item;

        // ────────────────────────────────────────────────────────────────
        //  Building and updating the list
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A full rebuild — only on an explicit act: opening, changing view,
        /// refreshing. Selection returns to the same item by identity.
        /// </summary>
        public void Rebuild()
        {
            string? keep = Selected?.ItemId;
            FactListSnapshot snapshot = _view.Snapshot(CurrentView);
            _latest = snapshot;

            _rows.Clear();
            foreach (ItemSnapshot item in snapshot.Items) _rows.Add(new RowItem(item));

            AutomationProperties.SetName(Items, Lexicon.Get(CurrentView == FactView.History
                ? "facts.window.history_label" : "facts.window.pending_label"));
            ApplyExcludedNotice(snapshot);

            RowItem? target = null;
            foreach (RowItem row in _rows) if (row.Item.ItemId == keep) target = row;
            target ??= _rows.Count > 0 ? _rows[0] : null;

            if (target != null)
            {
                if (ReferenceEquals(Items.SelectedItem, target)) ShowDetail(target.Item);
                else Items.SelectedItem = target;   // SelectionChanged shows it
            }
            else
            {
                ShowEmptyState(snapshot);
            }
            UpdateButtons();
        }

        /// <summary>Raised on whatever thread changed the store. Marshal, coalesce, never block.</summary>
        private void OnProjectionChanged()
        {
            if (_closed) return;
            if (Interlocked.Exchange(ref _updatePending, 1) == 1) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyUpdate));
        }

        /// <summary>
        /// Apply a background change without moving anything the operator is
        /// using. Rows are updated in place by identity; the selected row is
        /// kept even if it has left the default set, until he navigates or
        /// refreshes; the detail text is never replaced here.
        /// </summary>
        internal void ApplyUpdate()
        {
            Interlocked.Exchange(ref _updatePending, 0);
            if (_closed) return;

            FactListSnapshot snapshot = _view.Snapshot(CurrentView);
            if (_latest != null && snapshot.ProjectionRevision == _latest.ProjectionRevision) return;
            _latest = snapshot;

            var incoming = new Dictionary<string, ItemSnapshot>(StringComparer.Ordinal);
            foreach (ItemSnapshot item in snapshot.Items) incoming[item.ItemId] = item;

            RowItem? selected = Items.SelectedItem as RowItem;
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                RowItem row = _rows[i];
                if (incoming.TryGetValue(row.Item.ItemId, out ItemSnapshot? now)) row.Update(now);
                else if (!ReferenceEquals(row, selected)) _rows.RemoveAt(i);
            }

            int at = 0;
            foreach (ItemSnapshot item in snapshot.Items)
            {
                int existing = IndexOf(item.ItemId);
                if (existing < 0) _rows.Insert(Math.Min(at, _rows.Count), new RowItem(item));
                at++;
            }

            ApplyExcludedNotice(snapshot);

            if (_shown != null)
            {
                bool gone = !incoming.TryGetValue(_shown.ItemId, out ItemSnapshot? current);
                if (gone || FactListPresenter.DetailSignature(current!) != _shownSignature) MarkDetailChanged();
            }
            else if (_rows.Count == 0)
            {
                ShowEmptyState(snapshot);
            }
            UpdateButtons();
        }

        private int IndexOf(string itemId)
        {
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].Item.ItemId == itemId) return i;
            return -1;
        }

        private void ApplyExcludedNotice(FactListSnapshot snapshot)
        {
            string? excluded = FactListView.ExcludedText(snapshot);
            ExcludedNotice.Text = excluded ?? string.Empty;
            ExcludedNotice.Visibility = excluded == null ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(ExcludedNotice, excluded ?? string.Empty);
        }

        // ────────────────────────────────────────────────────────────────
        //  The detail — replaced only by selection or explicit refresh
        // ────────────────────────────────────────────────────────────────

        private void ShowDetail(ItemSnapshot? item)
        {
            ClearDetailChanged();
            if (item == null)
            {
                _shown = null;
                _shownSignature = null;
                DetailText.Text = Lexicon.Get("facts.detail.no_selection");
                return;
            }

            RenderedDetailSnapshot? detail = _view.RenderDetail(item);
            if (detail == null)
            {
                _shown = null;
                _shownSignature = null;
                DetailText.Text = Lexicon.Get("facts.detail.no_selection");
                return;
            }

            // Text and token change together, and the token becomes the shown
            // one only once the text is actually in the control.
            DetailText.Text = detail.Text;
            DetailText.CaretIndex = 0;
            _shown = _view.Installed(detail) ? detail : null;
            _shownSignature = FactListPresenter.DetailSignature(item);
        }

        private void ShowEmptyState(FactListSnapshot snapshot)
        {
            ClearDetailChanged();
            _shown = null;
            _shownSignature = null;
            DetailText.Text = FactListView.EmptyStateText(snapshot);
        }

        private void MarkDetailChanged()
        {
            ChangedNotice.Visibility = Visibility.Visible;
            RefreshButton.IsEnabled = true;
            AutomationProperties.SetItemStatus(DetailText, ChangedNotice.Text);
        }

        private void ClearDetailChanged()
        {
            ChangedNotice.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = false;
            AutomationProperties.SetItemStatus(DetailText, string.Empty);
        }

        private void UpdateButtons()
        {
            ItemSnapshot? item = _shown == null ? null : Selected;
            bool fact = item?.Kind == ItemKind.Fact;
            ReadButton.IsEnabled = Selected != null;
            ReviewedButton.IsEnabled = _shown != null;

            // Offered only when the shown record has a pause with a live owner
            // to release, so the control is never a button that does nothing.
            ResumeButton.IsEnabled = fact && item!.Fact!.IsLive && item.Fact.AutomaticPaused;
        }

        // ────────────────────────────────────────────────────────────────
        //  Handlers
        // ────────────────────────────────────────────────────────────────

        private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Navigation is explicit: a selection the operator makes shows its
            // own new snapshot. Drop a kept row that has already left the set.
            PruneStaleRows();
            ShowDetail(Selected);
            UpdateButtons();
        }

        private void PruneStaleRows()
        {
            if (_latest == null) return;
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (ItemSnapshot item in _latest.Items) live.Add(item.ItemId);
            for (int i = _rows.Count - 1; i >= 0; i--)
                if (!live.Contains(_rows[i].Item.ItemId) && !ReferenceEquals(_rows[i], Items.SelectedItem)) _rows.RemoveAt(i);
        }

        private void View_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            Rebuild();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // The explicit refresh path: rows and detail are brought up to date
            // together, and the new detail gets a new token.
            Rebuild();
            DetailText.Focus();
        }

        /// <summary>
        /// Read the selected record: move to its detail. The platform reads
        /// focused text, which is the route that keeps working when ours does
        /// not — the case this window exists for.
        /// </summary>
        private void ReadButton_Click(object sender, RoutedEventArgs e)
        {
            DetailText.Focus();
            DetailText.CaretIndex = 0;
        }

        private void ReviewedButton_Click(object sender, RoutedEventArgs e)
        {
            RenderedDetailSnapshot? shown = _shown;
            if (shown == null) return;

            // The token of the detail ON SCREEN. Nothing here reads the
            // record's current revision.
            _view.Review(shown.Token);
            ApplyUpdate();
            UpdateButtons();
        }

        private void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            RenderedDetailSnapshot? shown = _shown;
            if (shown == null) return;
            _view.Resume(shown.Token);
            ApplyUpdate();
            UpdateButtons();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>One list row: an immutable snapshot, replaceable in place without moving selection.</summary>
        internal sealed class RowItem : INotifyPropertyChanged
        {
            public RowItem(ItemSnapshot item) => Item = item;

            public ItemSnapshot Item { get; private set; }
            public string Text => Item.RowText;

            public event PropertyChangedEventHandler? PropertyChanged;

            public void Update(ItemSnapshot item)
            {
                bool changed = item.RowText != Item.RowText;
                Item = item;
                if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }

            public override string ToString() => Text;
        }
    }
}
