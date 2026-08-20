using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf;

/// <summary>
/// NVDA-style category navigation for a dialog built out of tabs
/// (Sprint 32 Track G, task #134).
///
/// <para>A list of categories down the left, the selected category filling the
/// rest of the dialog, and <b>Ctrl+Tab / Ctrl+Shift+Tab stepping between them
/// from anywhere inside</b> — the shape NVDA's own settings dialog uses, which
/// is the shape our operators already know. Noel's words: "they have a category
/// list... ctrl tab goes to the next category, ctrl+shift tab goes to the
/// previous category. That's cleaner than a leaky tab strip."</para>
///
/// <para><b>It enumerates; it never names.</b> Nothing in here knows how many
/// categories a dialog has or what they are called — the list is built by
/// walking the TabControl's items and rebuilt whenever that collection or any
/// item's visibility changes. Sprint 32 had three other tracks adding tabs to
/// the Audio Workshop while this was written, and the point of enumerating is
/// that not one of them had to know this file exists. Adding a section is still
/// one <c>TabItem</c> block of XAML.</para>
///
/// <para><b>Why the tab strip goes away entirely</b> (the CategoryTabControl
/// style templates it out). A strip alongside a list is two controls that
/// select the same thing: two tab stops, two places focus can land, and a
/// second movement gesture — Left and Right on a focused strip — that nothing
/// announces or documents. Keeping both would be the "leak" rather than a
/// belt-and-braces.</para>
///
/// <para><b>Why moving the category moves FOCUS to the list</b> rather than
/// leaving it in the content. A section change with focus left behind is a
/// change the operator is not told about: the screen reader has no reason to
/// speak, because nothing it is looking at moved. Putting focus on the list
/// item makes the arrival a real UIA focus change, which every screen reader
/// announces by name and position ("Network, 5 of 11") without this class
/// speaking a word of its own. That is the house rule — speak only what the UI
/// cannot convey — satisfied by making the UI convey it.</para>
/// </summary>
public sealed class CategoryNavigator
{
    private readonly Window _window;
    private readonly TabControl _tabs;
    private readonly ListBox _list;

    /// <summary>Category list position → the TabItem it selects. Rebuilt with
    /// the list, so a collapsed tab simply is not in either.</summary>
    private readonly List<TabItem> _visible = new();

    /// <summary>TabItems whose Visibility we are watching, so the hooks can be
    /// dropped when the window closes. DependencyPropertyDescriptor holds a
    /// strong reference to the handler and would otherwise keep the dialog
    /// alive for the life of the process.</summary>
    private readonly List<TabItem> _watched = new();

    /// <summary>Re-entrancy guard: each side of the sync sets the other.</summary>
    private bool _syncing;

    private CategoryNavigator(Window window, TabControl tabs, ListBox list)
    {
        _window = window;
        _tabs = tabs;
        _list = list;
    }

    /// <summary>
    /// Wire a category list to a TabControl and install the Ctrl+Tab pair on
    /// the window. Call once, from the dialog's constructor.
    /// </summary>
    /// <param name="window">The dialog. Ctrl+Tab is handled here, in the
    /// tunnel phase, so it works with focus in any control including a text
    /// box, and so the TabControl's own built-in Ctrl+Tab never also fires.</param>
    /// <param name="tabs">The TabControl holding the categories. Give it the
    /// CategoryTabControl style so its header strip is templated away.</param>
    /// <param name="list">The category list. Give it the CategoryList style.</param>
    public static CategoryNavigator Attach(Window window, TabControl tabs, ListBox list)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(list);

        var nav = new CategoryNavigator(window, tabs, list);
        nav.Wire();
        return nav;
    }

    private void Wire()
    {
        Rebuild();

        _list.SelectionChanged += (_, e) =>
        {
            e.Handled = true;              // never let it bubble out as the dialog's own event
            if (_syncing) return;
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _visible.Count) return;
            _syncing = true;
            try { _tabs.SelectedItem = _visible[i]; }
            finally { _syncing = false; }
        };

        _tabs.SelectionChanged += (s, e) =>
        {
            // A TabControl raises SelectionChanged for selectors nested inside
            // its content too (a combo box on the current page). Only the
            // TabControl's own change means the category moved.
            if (!ReferenceEquals(e.OriginalSource, _tabs)) return;
            if (_syncing) return;
            SyncListToTabs();
        };

        // Categories can appear, disappear, or be collapsed at runtime — a tab
        // that only exists for a licensed feature, say. Watch for both, so the
        // list is never a stale picture of the dialog.
        if (_tabs.Items is INotifyCollectionChanged incc)
            incc.CollectionChanged += (_, _) => Rebuild();

        _window.PreviewKeyDown += OnWindowPreviewKeyDown;
        _window.Closed += (_, _) => Unwatch();
    }

    /// <summary>
    /// Ctrl+Tab forward, Ctrl+Shift+Tab back, wrapping at both ends.
    /// </summary>
    /// <remarks>
    /// Ctrl+Tab arrives as a plain <c>Key.Tab</c> with Control in
    /// Keyboard.Modifiers — it is ALT that turns a chord into
    /// <c>Key.System</c> with the real key hiding in <c>e.SystemKey</c>, the
    /// trap that shipped a dead Alt+L binding on 2026-08-13. No SystemKey
    /// handling is needed here, and adding it "just in case" would match
    /// Alt+Tab, which belongs to Windows.
    ///
    /// <para>Wrapping rather than stopping: eleven categories with a hard stop
    /// at each end means an operator on the last one has to reverse to reach
    /// the first, and there is no cue that they are at an end. A dialog is a
    /// cycle, not a corridor — the same reasoning that made JJFlexDialog's own
    /// tab order Cycle.</para>
    /// </remarks>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
        if (_visible.Count == 0) return;

        bool back = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        Move(forward: !back);
        e.Handled = true;
    }

    /// <summary>Step one category, wrapping, and land focus on it.</summary>
    public void Move(bool forward)
    {
        if (_visible.Count == 0) return;

        int current = _list.SelectedIndex;
        if (current < 0) current = forward ? -1 : 0;

        int next = forward ? current + 1 : current - 1;
        if (next >= _visible.Count) next = 0;
        else if (next < 0) next = _visible.Count - 1;

        // Focus first, then select. Selecting first would tear down the
        // content that currently holds focus, and WPF's fallback for focus
        // inside a disappearing subtree is not worth relying on.
        FocusSelectedCategory();
        _list.SelectedIndex = next;
        FocusSelectedCategory();
    }

    /// <summary>
    /// Put keyboard focus on the currently selected category, so a screen
    /// reader announces it by name and position.
    /// </summary>
    /// <remarks>
    /// This is what deep links call. Before the category list existed they
    /// called <c>TabItem.Focus()</c>, which worked because the header strip was
    /// a real, focusable visual. With the strip templated away that call would
    /// silently do nothing — the arrival would be announced as plain "Settings"
    /// and an operator who cannot see would have no evidence they landed
    /// anywhere in particular. Route every such caller through here.
    /// </remarks>
    public bool FocusSelectedCategory()
    {
        if (_list.SelectedIndex < 0 && _visible.Count > 0)
            _list.SelectedIndex = 0;
        if (_list.SelectedIndex < 0) return false;

        // The container may not be generated yet on a freshly loaded dialog;
        // focusing the ListBox itself then puts focus on its selected item.
        if (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex)
            is ListBoxItem item && item.Focus())
            return true;
        return _list.Focus();
    }

    /// <summary>Rebuild the category list from whatever tabs currently exist.</summary>
    public void Rebuild()
    {
        TabItem? keep = _tabs.SelectedItem as TabItem;

        Unwatch();
        _visible.Clear();

        _syncing = true;
        try
        {
            _list.Items.Clear();
            foreach (object? o in _tabs.Items)
            {
                if (o is not TabItem tab) continue;
                Watch(tab);
                if (tab.Visibility != Visibility.Visible) continue;

                _visible.Add(tab);
                _list.Items.Add(BuildListItem(tab));
            }
        }
        finally { _syncing = false; }

        int idx = keep != null ? _visible.IndexOf(keep) : -1;
        if (idx < 0 && _visible.Count > 0) idx = 0;
        _syncing = true;
        try { _list.SelectedIndex = idx; }
        finally { _syncing = false; }

        // A tab that was selected and then collapsed leaves the TabControl
        // pointing at something the list cannot show. Move it somewhere real.
        if (idx >= 0 && !ReferenceEquals(_tabs.SelectedItem, _visible[idx]))
            _tabs.SelectedItem = _visible[idx];
    }

    /// <summary>
    /// One row. The VISIBLE text is the tab's short header, because that is
    /// what the dialog has always called this section and what a sighted
    /// operator describing it over the air will say. The ACCESSIBLE name is the
    /// tab's fuller AutomationProperties.Name where it has one — "PTT" reads as
    /// three letters, "Push to talk settings" reads as what it is.
    /// </summary>
    private static ListBoxItem BuildListItem(TabItem tab)
    {
        string header = tab.Header as string ?? string.Empty;
        string spoken = AutomationProperties.GetName(tab);
        if (string.IsNullOrWhiteSpace(spoken)) spoken = header;
        if (string.IsNullOrWhiteSpace(header)) header = spoken;

        var item = new ListBoxItem { Content = header };
        AutomationProperties.SetName(item, spoken);
        return item;
    }

    private void SyncListToTabs()
    {
        int idx = _tabs.SelectedItem is TabItem t ? _visible.IndexOf(t) : -1;
        if (idx < 0) return;
        _syncing = true;
        try { _list.SelectedIndex = idx; }
        finally { _syncing = false; }
    }

    private void Watch(TabItem tab)
    {
        var dpd = DependencyPropertyDescriptor.FromProperty(
            UIElement.VisibilityProperty, typeof(TabItem));
        if (dpd == null) return;
        dpd.AddValueChanged(tab, OnTabVisibilityChanged);
        _watched.Add(tab);
    }

    private void Unwatch()
    {
        var dpd = DependencyPropertyDescriptor.FromProperty(
            UIElement.VisibilityProperty, typeof(TabItem));
        if (dpd != null)
        {
            foreach (TabItem tab in _watched)
                dpd.RemoveValueChanged(tab, OnTabVisibilityChanged);
        }
        _watched.Clear();
    }

    private void OnTabVisibilityChanged(object? sender, EventArgs e)
    {
        // Rebuild clears _watched and re-adds, so it must not run from inside
        // the enumeration that raised this. Queue it.
        _window.Dispatcher.BeginInvoke(new Action(Rebuild));
    }
}
