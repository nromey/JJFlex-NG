using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>Settings, Advanced: the Measured delays rows reachable from the keyboard (Sprint 49 Track N2).</summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    // ── Settings, Advanced: the Measured delays rows ──

    [Fact]
    public void A_row_taking_focus_no_longer_refills_the_list_under_itself()
    {
        RunOnSta(() =>
        {
            var rows = new[] { "NR2: about 105 milliseconds", "NR4: about 11 milliseconds", "RNNoise: about 20 milliseconds" };
            var list = new ListBox();
            foreach (var r in rows) list.Items.Add(r);
            var (tabs, tab) = InATab(list);
            var row = list.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
            Assert.NotNull(row);

            int reachedTheTab = 0, refills = 0;
            tab.GotFocus += (s, e) => reachedTheTab++;
            JJFlexWpf.Dialogs.SettingsDialog.RefillWhenTheTabTakesFocus(tab, () => refills++);

            // The cause, shown: a row's GotFocus bubbles to the tab's handler.
            row!.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent, row));
            Assert.Equal(1, reachedTheTab);
            Assert.Equal(0, refills);
            // So does the list's own (Tab onto it); neither refills now.
            list.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent, list));
            Assert.Equal(0, refills);
            // The tab itself taking focus still refreshes the figures.
            tab.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent, tab));
            Assert.Equal(1, refills);
        });
    }

    [Fact]
    public void A_refill_that_changes_nothing_leaves_the_row_under_the_keyboard_in_place()
    {
        RunOnSta(() =>
        {
            var rows = new[] { "NR2: about 105 milliseconds", "NR4: measured once it is running", "RNNoise: about 20 milliseconds" };
            var list = new ListBox();
            JJFlexWpf.Dialogs.SettingsDialog.FillRowsKeepingPlace(list, rows);
            Assert.Equal(rows, list.Items.Cast<string>());
            var (tabs, _) = InATab(list);
            list.SelectedIndex = 1;
            var row = list.ItemContainerGenerator.ContainerFromIndex(1);
            Assert.NotNull(row);

            JJFlexWpf.Dialogs.SettingsDialog.FillRowsKeepingPlace(list, rows.ToArray());
            tabs.UpdateLayout();
            Assert.Same(row, list.ItemContainerGenerator.ContainerFromIndex(1));
            Assert.Equal(1, list.SelectedIndex);

            // A figure that changed is shown, and the place is kept.
            var newer = new[] { "NR2: about 105 milliseconds", "NR4: about 11 milliseconds", "RNNoise: about 20 milliseconds" };
            JJFlexWpf.Dialogs.SettingsDialog.FillRowsKeepingPlace(list, newer);
            Assert.Equal(newer, list.Items.Cast<string>());
            Assert.Equal(1, list.SelectedIndex);
        });
    }

    /// <summary>The Advanced tab's shape, unhosted and laid out: a list in a panel in a scroller in a tab of a tab control.</summary>
    private static (TabControl Tabs, TabItem Tab) InATab(ListBox list)
    {
        var panel = new StackPanel();
        panel.Children.Add(list);
        var tab = new TabItem { Header = "Advanced", Content = new ScrollViewer { Content = panel } };
        var tabs = new TabControl();
        tabs.Items.Add(tab);
        tabs.SelectedIndex = 0;
        tabs.Measure(new Size(400, 400));
        tabs.Arrange(new Rect(0, 0, 400, 400));
        tabs.UpdateLayout();
        return (tabs, tab);
    }
}
