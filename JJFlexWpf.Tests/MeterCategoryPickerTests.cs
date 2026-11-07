using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The meters panel chooses a meter by category: a category combo ahead of
/// the source list, "every category" first so nothing is out of reach, and a
/// category that narrows the list without touching the meter being edited.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> A real <see cref="MetersPanel"/> is constructed, as
/// <c>MetersReconnectTests</c> does, and never put in a window: a
/// UserControl outside a Window has no presentation source and cannot be
/// shown. The thread is STA because WPF elements require it. With no radio
/// the panel offers the eight meters the hardware family reports
/// (<c>LegacyMeterCatalog</c>), which is the list these tests read. Refuses
/// unless <c>QuietRun</c> and <c>TestSettingsRoot</c> have both engaged.
/// </para>
/// <para>
/// <b>What this cannot see:</b> a meter being edited, because the test
/// engine holds no meter slots (checked with a probe assertion): that a
/// category choice leaves the edited meter alone rests on the handler
/// running with <c>_loading</c> set, read in the code. Nor a live radio's
/// hundred meters arriving and
/// the panel rebuilding as they do. That path is
/// <c>OnInventoryChanged</c>, which calls the same rebuild these tests call
/// through the constructor; the categories themselves are pinned meter by
/// meter in <c>Radios.Tests.MeterCategoryTests</c>.
/// </para>
/// </remarks>
public sealed class MeterCategoryPickerTests
{
    [Fact]
    public void Every_category_comes_first_then_each_category_with_a_meter_in_it()
        => Run(panel =>
        {
            var categories = Items(Combo(panel, "CategoryCombo"));
            Assert.Equal(new[]
            {
                "Every category, 8 meters",
                "Transmit, 1 meter",
                "Receive, 1 meter",
                "Power and SWR, 2 meters",
                "Temperature and voltage, 2 meters",
                "Audio, 2 meters",
            }, categories);
            Assert.Equal(0, Combo(panel, "CategoryCombo").SelectedIndex);
        });

    [Fact]
    public void Every_category_lists_them_all_grouped_with_the_category_in_front()
        => Run(panel =>
        {
            var sources = Items(Combo(panel, "SourceCombo"));
            Assert.Equal(8, sources.Length);
            Assert.StartsWith("Transmit: ", sources[0]);
            Assert.StartsWith("Receive: ", sources[1]);
            Assert.StartsWith("Power and SWR: ", sources[2]);
            Assert.StartsWith("Power and SWR: ", sources[3]);
            Assert.StartsWith("Audio: ", sources[7]);
        });

    [Fact]
    public void Choosing_a_category_narrows_the_list_and_every_category_brings_it_back()
        => Run(panel =>
        {
            var category = Combo(panel, "CategoryCombo");
            var source = Combo(panel, "SourceCombo");

            category.SelectedIndex = Items(category).ToList().FindIndex(s => s.StartsWith("Power and SWR"));
            var power = Items(source);
            Assert.Equal(2, power.Length);
            Assert.All(power, s => Assert.DoesNotContain("Power and SWR:", s));
            Assert.Contains(power, s => s.Contains("FWDPWR"));
            Assert.Contains(power, s => s.Contains("SWR"));

            category.SelectedIndex = 0;
            Assert.Equal(8, Items(source).Length);
        });

    private static ComboBox Combo(MetersPanel panel, string name) => (ComboBox)panel.FindName(name);

    private static string[] Items(ComboBox combo) =>
        combo.Items.Cast<object>().Select(o => o.ToString() ?? "").ToArray();

    private static void Run(Action<MetersPanel> body)
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Lexicon.Load(Lexicon.Partitions);
                var panel = new MetersPanel();
                body(panel);
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The panel test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
