using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Every category name is shown whole (#1068, batch 12 repair round 2). On the
/// 8600 bench three Settings names were cut off at the edge of the 170-wide
/// category list: the items took their natural width, up to 244, and the list
/// scrolled sideways. The CategoryList style now wraps a long name instead,
/// and the list never scrolls sideways.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> A lone <see cref="ListBox"/> with the real style from
/// <c>DialogStyles.xaml</c>, rows built by <see cref="CategoryNavigator"/>'s
/// own row builder, measured and arranged by hand on a private STA thread: no
/// window, no presentation source, nothing on the desktop. The test thread is
/// asserted MTA and <see cref="QuietRun"/> silenced.
/// </remarks>
public sealed class CategoryListWholeNameTests
{
    /// <summary>The three names the bench saw cut, and one longer than any name today, so the rule holds for whatever comes.</summary>
    public static IEnumerable<object[]> Names => new[]
    {
        new object[] { "Verbosity and notifications settings" },
        new object[] { "Accessibility timing and input preferences" },
        new object[] { "Update channel and auto-check preferences" },
        new object[] { "A category name a good deal longer than any Settings has today, which must still be shown whole" },
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void A_category_name_is_shown_whole_in_the_list(string name) => OnSta(() =>
    {
        var (list, viewer, rows) = Laid(name, "PTT", "Diagnostics");
        Assert.True(viewer.ExtentWidth <= viewer.ViewportWidth + 0.5,
            $"the list scrolls sideways: extent {viewer.ExtentWidth:F0} against a viewport of {viewer.ViewportWidth:F0}");

        foreach (var (item, text) in rows)
        {
            // The row and its text sit inside what the list shows.
            double right = item.TranslatePoint(new Point(item.ActualWidth, 0), viewer).X;
            Assert.True(right <= viewer.ViewportWidth + 0.5, $"'{text.Text}' runs {right - viewer.ViewportWidth:F0} past the list's edge");
            double textRight = text.TranslatePoint(new Point(text.ActualWidth, 0), viewer).X;
            Assert.True(textRight <= viewer.ViewportWidth + 0.5, $"the text of '{text.Text}' runs past the list's edge");

            // All of its words are laid out: a name wider than the row wraps.
            double natural = NaturalWidth(text);
            Assert.True(natural <= text.ActualWidth + 0.5 || text.TextWrapping != TextWrapping.NoWrap,
                $"'{text.Text}' needs {natural:F0} and has {text.ActualWidth:F0}, unwrapped");
            Assert.Equal(TextTrimming.None, text.TextTrimming);
            if (natural > text.ActualWidth + 0.5)
                Assert.True(text.ActualHeight >= 1.8 * LineHeight(text), $"'{text.Text}' does not take a second line");
        }

        // The list kept its width: the settings beside it lose nothing.
        Assert.Equal(170, list.ActualWidth, 0);
    });

    /// <summary>
    /// The positive control: the same row with the list's old style (a fixed
    /// 170 and nothing else) scrolls sideways, so this instrument sees the cut
    /// the bench saw.
    /// </summary>
    [Fact]
    public void Without_the_wrap_the_longest_name_runs_past_the_lists_edge() => OnSta(() =>
    {
        var old = new Style(typeof(ListBox));
        old.Setters.Add(new Setter(FrameworkElement.WidthProperty, 170.0));
        old.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0)));
        old.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var (_, viewer, _) = Laid(old, "Accessibility timing and input preferences");
        Assert.True(viewer.ExtentWidth > viewer.ViewportWidth + 20,
            $"the old style did not overflow (extent {viewer.ExtentWidth:F0}, viewport {viewer.ViewportWidth:F0}), so this test cannot see the defect");
    });

    /// <summary>What a screen reader reads is unchanged: one row, named by the tab's name, with the name as its one text.</summary>
    [Fact]
    public void A_wrapped_row_is_still_one_item_with_one_text() => OnSta(() =>
    {
        var (_, _, rows) = Laid("Accessibility timing and input preferences");
        var (item, text) = Assert.Single(rows);
        Assert.Equal("Accessibility timing and input preferences", System.Windows.Automation.AutomationProperties.GetName(item));
        Assert.Equal("Accessibility timing and input preferences", text.Text);
        Assert.Single(Descendants(item).OfType<TextBlock>());
    });

    private static (ListBox, ScrollViewer, List<(ListBoxItem, TextBlock)>) Laid(params string[] names) =>
        Laid((Style)DialogStyles()["CategoryList"], names);

    private static (ListBox, ScrollViewer, List<(ListBoxItem, TextBlock)>) Laid(Style style, params string[] names)
    {
        var list = new ListBox { Style = style, Height = 459 };   // the bench's measured height
        foreach (string name in names) list.Items.Add(Row(name));
        list.Measure(new Size(double.PositiveInfinity, 459));
        list.Arrange(new Rect(new Point(0, 0), list.DesiredSize));
        list.UpdateLayout();
        Assert.Null(PresentationSource.FromVisual(list));   // unhosted: nothing on the desk

        var viewer = Descendants(list).OfType<ScrollViewer>().First();
        var rows = new List<(ListBoxItem, TextBlock)>();
        foreach (object o in list.Items)
        {
            var item = (ListBoxItem)o;
            rows.Add((item, Descendants(item).OfType<TextBlock>().Single()));
        }
        return (list, viewer, rows);
    }

    /// <summary>A row exactly as <see cref="CategoryNavigator"/> builds it for a tab with this header and name.</summary>
    private static ListBoxItem Row(string name)
    {
        var tab = new TabItem { Header = name };
        System.Windows.Automation.AutomationProperties.SetName(tab, name);
        var build = typeof(CategoryNavigator).GetMethod("BuildListItem", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ListBoxItem)build.Invoke(null, new object[] { tab })!;
    }

    /// <summary>The shipped style dictionary, loaded as the dialogs load it, by the assembly's own resource.</summary>
    private static ResourceDictionary DialogStyles() =>
        (ResourceDictionary)Application.LoadComponent(new Uri("/JJFlexWpf;component/Styles/DialogStyles.xaml", UriKind.Relative));

    private static double NaturalWidth(TextBlock shown)
    {
        var probe = new TextBlock
        {
            Text = shown.Text, FontFamily = shown.FontFamily, FontSize = shown.FontSize,
            FontWeight = shown.FontWeight, FontStyle = shown.FontStyle, TextWrapping = TextWrapping.NoWrap,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static double LineHeight(TextBlock shown)
    {
        var probe = new TextBlock { Text = "X", FontFamily = shown.FontFamily, FontSize = shown.FontSize };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static void OnSta(Action body)
    {
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The category list test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
