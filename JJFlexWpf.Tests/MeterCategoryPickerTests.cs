using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Codex B1 on f05fc1f2: inside a category, a first letter must find the
    /// meter by its own name. Live entries start with their hardware group
    /// ("TX-: FWDPWR ..."), so matching the entry text made F find nothing.
    /// Live-shaped entries are put into the panel's own list and the same
    /// rebuild the inventory change runs is called; the search is WPF's own
    /// (TextSearch, the code a typed letter reaches), with a plain combo as
    /// the positive control that the call really searches.
    /// </summary>
    [Fact]
    public void A_first_letter_finds_the_meter_by_its_own_name_inside_a_category()
        => Run(panel =>
        {
            Assert.Empty(MeterToneEngine.Slots);   // a search must not retarget a configured meter
            var control = new ComboBox();
            control.Items.Add("Forward power");
            Search(control, "F");
            Assert.Equal(0, control.SelectedIndex);

            LiveShaped(panel,
                ("TX-", "FWDPWR", "Forward power", MeterCategory.PowerAndSwr),
                ("TX-", "REFPWR", "Reflected power", MeterCategory.PowerAndSwr),
                ("TX-", "SWR", "SWR", MeterCategory.PowerAndSwr),
                ("RAD", "PATEMP", "PA temperature", MeterCategory.TemperatureAndVoltage));
            var category = Combo(panel, "CategoryCombo");
            var source = Combo(panel, "SourceCombo");
            category.SelectedIndex = Items(category).ToList().FindIndex(x => x.StartsWith("Power and SWR"));
            Assert.Equal(3, source.Items.Count);
            Assert.All(Items(source), x => Assert.StartsWith("TX-: ", x));   // read in full, group first

            Search(source, "F");
            Assert.Contains("FWDPWR", Items(source)[source.SelectedIndex]);
            Search(source, "S");
            Assert.Contains("SWR", Items(source)[source.SelectedIndex]);
            Search(source, "R");
            Assert.Contains("REFPWR", Items(source)[source.SelectedIndex]);

            // Each entry's accessible name is still the whole line.
            var style = source.ItemContainerStyle;
            var name = style.Setters.OfType<System.Windows.Setter>()
                .Single(x => x.Property == System.Windows.Automation.AutomationProperties.NameProperty);
            Assert.Equal("ListDisplay", ((System.Windows.Data.Binding)name.Value).Path.Path);
        });

    /// <summary>
    /// Codex B2 on f05fc1f2: with a meter configured on LEVEL (Receive), the
    /// operator browses Audio, then the list is rebuilt (the "show every
    /// meter" fold, or the radio's meter set changing). The category must
    /// stay Audio. Choosing another meter to edit lets it follow that
    /// meter's source again, the control that following still works.
    /// </summary>
    [Fact]
    public void A_category_the_operator_chose_survives_a_rebuild_until_they_edit_another_meter()
    {
        var added = new List<MeterSlot>();
        try
        {
            Run(panel =>
            {
                // Two meters to edit, built here and removed after.
                foreach (var key in new[] { "LEVEL", "FWDPWR" })
                {
                    var slot = MeterToneEngine.AddSlot()!;
                    added.Add(slot);
                    slot.Retarget(key, key, new MeterRange(), MeterActivation.Always, -1);
                }
                Invoke(panel, "RefreshSlotList");
                var slots = Combo(panel, "SlotCombo");
                var category = Combo(panel, "CategoryCombo");
                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[0]);
                Assert.StartsWith("Every category", (string)category.SelectedItem);   // LEVEL is in view

                category.SelectedIndex = Items(category).ToList().FindIndex(x => x.StartsWith("Audio"));

                // The fold's own two calls (AllMetersCheck_Changed), and the
                // inventory change's, which are the same two.
                Invoke(panel, "RefreshSourceChoices");
                Invoke(panel, "LoadSelectedSlot");
                Assert.StartsWith("Audio", (string)category.SelectedItem);
                Assert.Equal("LEVEL", added[0].Definition.Source.Key);   // nothing retargeted

                // Editing another meter: the list follows its source again.
                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[1]);
                Assert.StartsWith("Power and SWR", (string)category.SelectedItem);
            });
        }
        finally
        {
            foreach (var slot in added)
            {
                EarconPlayer.UnregisterContinuousTone(slot.ToneProvider);
                MeterToneEngine.Slots.Remove(slot);
            }
        }
    }

    /// <summary>
    /// Codex, recheck of f05fc1f2's repair: the category chosen while editing
    /// meter A must not outlive leaving A. Select A (LEVEL), browse Audio,
    /// select B (FWDPWR), select A again: A's own source must be shown, in
    /// its own category. It came back on Power and SWR with no source shown.
    /// </summary>
    [Fact]
    public void Returning_to_a_meter_shows_its_own_source_not_a_category_chosen_before_leaving_it()
    {
        var added = new List<MeterSlot>();
        try
        {
            Run(panel =>
            {
                foreach (var key in new[] { "LEVEL", "FWDPWR" })
                {
                    var slot = MeterToneEngine.AddSlot()!;
                    added.Add(slot);
                    slot.Retarget(key, key, new MeterRange(), MeterActivation.Always, -1);
                }
                Invoke(panel, "RefreshSlotList");
                var slots = Combo(panel, "SlotCombo");
                var category = Combo(panel, "CategoryCombo");
                var source = Combo(panel, "SourceCombo");

                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[0]);
                category.SelectedIndex = Items(category).ToList().FindIndex(x => x.StartsWith("Audio"));
                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[1]);
                Assert.StartsWith("Power and SWR", (string)category.SelectedItem);

                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[0]);
                Assert.StartsWith("Receive", (string)category.SelectedItem);
                Assert.True(source.SelectedIndex >= 0, "meter A's own source is not shown");
                Assert.Contains("LEVEL", Items(source)[source.SelectedIndex]);
            });
        }
        finally
        {
            foreach (var slot in added)
            {
                EarconPlayer.UnregisterContinuousTone(slot.ToneProvider);
                MeterToneEngine.Slots.Remove(slot);
            }
        }
    }

    /// <summary>
    /// Codex, third recheck: a saved meter outside the short list (+13.8B)
    /// showed no source when selected, because the list keeps such a meter
    /// only for the meter being edited when it was built, and selecting
    /// another meter did not rebuild it. Here A (LEVEL) is selected when the
    /// list is built, then the saved +13.8B meter is selected, with "Show
    /// every meter" off. With no radio the short list is the eight catalog
    /// meters; the kept-meter rule is the same code for the live short list.
    /// </summary>
    [Fact]
    public void Selecting_a_saved_meter_outside_the_short_list_shows_its_source()
    {
        var added = new List<MeterSlot>();
        try
        {
            Run(panel =>
            {
                foreach (var key in new[] { "LEVEL", "+13.8B" })
                {
                    var slot = MeterToneEngine.AddSlot()!;
                    added.Add(slot);
                    slot.Retarget(key, key, new MeterRange(), MeterActivation.Always, -1);
                }
                var slots = Combo(panel, "SlotCombo");
                var source = Combo(panel, "SourceCombo");
                var every = (CheckBox)panel.FindName("AllMetersCheck");
                Assert.NotEqual(true, every.IsChecked);   // the default short list

                Invoke(panel, "RefreshSlotList");
                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[0]);
                Invoke(panel, "RefreshSourceChoices");   // the list built while A is edited
                Assert.DoesNotContain(Items(source), x => x.Contains("+13.8B"));

                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[1]);
                Assert.True(source.SelectedIndex >= 0, "the saved +13.8B meter shows no source");
                Assert.Contains("+13.8B", Items(source)[source.SelectedIndex]);
                Assert.Equal("+13.8B", added[1].Definition.Source.Key);   // nothing retargeted

                // And back: A's own source, still there.
                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[0]);
                Assert.Contains("LEVEL", Items(source)[source.SelectedIndex]);
            });
        }
        finally
        {
            foreach (var slot in added)
            {
                EarconPlayer.UnregisterContinuousTone(slot.ToneProvider);
                MeterToneEngine.Slots.Remove(slot);
            }
        }
    }

    /// <summary>
    /// Codex, fourth recheck: the saved +13.8B meter was shown, but as
    /// "+13.8B (not reported by this radio)" while the radio WAS reporting it,
    /// because the kept-meter entry always took the missing-meter text. Run
    /// against an inventory holding +13.8B, with the default short list (which
    /// leaves +13.8B out), the entry must be the radio's own, with its own text.
    /// The control: a saved meter the inventory does not hold still says
    /// "not reported".
    /// </summary>
    [Fact]
    public void A_saved_meter_the_radio_reports_reads_as_reported_and_a_missing_one_as_missing()
    {
        var added = new List<MeterSlot>();
        using var radio = FakeInventory(
            ("RAD", 0, "+13.8A", "Main radio input voltage", "Volts"),
            ("RAD", 0, "+13.8B", "+13.8V at PA", "Volts"),
            ("SLC", 0, "LEVEL", "Signal strength", "Dbm"),
            ("TX-", 0, "FWDPWR", "Forward power", "Watts"));
        try
        {
            Run(panel =>
            {
                foreach (var key in new[] { "LEVEL", "+13.8B", "NOSUCH" })
                {
                    var slot = MeterToneEngine.AddSlot()!;
                    added.Add(slot);
                    slot.Retarget(key, key, new MeterRange(), MeterActivation.Always, -1);
                }
                var slots = Combo(panel, "SlotCombo");
                var source = Combo(panel, "SourceCombo");
                var detail = (TextBlock)panel.FindName("SourceDetailText");
                Assert.NotEqual(true, ((CheckBox)panel.FindName("AllMetersCheck")).IsChecked);

                Invoke(panel, "RefreshSlotList");
                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[0]);
                Invoke(panel, "RefreshSourceChoices");   // the live short list, built while LEVEL is edited
                Assert.DoesNotContain(Items(source), x => x.Contains("+13.8B"));   // the short list leaves it out

                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[1]);
                Assert.True(source.SelectedIndex >= 0, "the saved +13.8B meter shows no source");
                string shown = Items(source)[source.SelectedIndex];
                Assert.Contains("+13.8B", shown);
                Assert.Contains("+13.8V at PA", shown);                 // the radio's own description
                Assert.DoesNotContain("not reported", shown);
                Assert.DoesNotContain("not reporting", detail.Text);

                // The control: a saved meter the radio does not report.
                slots.SelectedIndex = MeterToneEngine.Slots.IndexOf(added[2]);
                Assert.True(source.SelectedIndex >= 0, "the missing meter shows no source");
                Assert.Contains("not reported", Items(source)[source.SelectedIndex]);
                Assert.Contains("not reporting", detail.Text);
            });
        }
        finally
        {
            foreach (var slot in added)
            {
                EarconPlayer.UnregisterContinuousTone(slot.ToneProvider);
                MeterToneEngine.Slots.Remove(slot);
            }
        }
    }

    /// <summary>
    /// Stand a meter inventory up behind <see cref="MeterToneEngine.Inventory"/>
    /// without a radio: FlexLib meters as uninitialized objects with their
    /// fields set, read through the inventory's own reading and group types.
    /// Restores the engine's radio on dispose. Nothing connects or sounds.
    /// </summary>
    private static IDisposable FakeInventory(params (string Source, int Index, string Name, string Description, string Units)[] meters)
    {
        var any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Public;
        var readingCtor = typeof(MeterReading).GetConstructors(any).Single();
        Type meterType = readingCtor.GetParameters()[0].ParameterType;
        void Set(object o, string field, object? value) =>
            (o.GetType().GetField(field, any) ?? throw new InvalidOperationException(field)).SetValue(o, value);

        var readings = new List<MeterReading>();
        var byName = new Dictionary<string, MeterReading>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var m in meters)
        {
            object meter = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(meterType);
            Set(meter, "_index", index++);
            Set(meter, "_source", m.Source);
            Set(meter, "_source_index", m.Index);
            Set(meter, "_name", m.Name);
            Set(meter, "_description", m.Description);
            Set(meter, "_low", 0.0);
            Set(meter, "_high", 20.0);
            var unitsField = meterType.GetField("_units", any)!;
            unitsField.SetValue(meter, Enum.Parse(unitsField.FieldType, m.Units));
            var r = (MeterReading)readingCtor.Invoke(new[] { meter });
            readings.Add(r);
            if (!byName.ContainsKey(r.Name)) byName[r.Name] = r;
        }

        var groupCtor = typeof(MeterGroup).GetConstructors(any).Single();
        var groups = readings.GroupBy(r => (r.Source, r.SourceIndex))
            .Select(g => (MeterGroup)groupCtor.Invoke(new object[] { g.Key.Source, g.Key.SourceIndex, g.ToList() }))
            .ToList();

        var inv = (MeterInventory)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MeterInventory));
        Set(inv, "_all", readings);
        Set(inv, "_groups", groups);
        Set(inv, "_byName", byName);

        var rig = (FlexBase)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FlexBase));
        Set(rig, "<MeterInventory>k__BackingField", inv);

        var engineRig = typeof(MeterToneEngine).GetField("_rig",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        object? saved = engineRig.GetValue(null);
        engineRig.SetValue(null, rig);
        return new Restore(() => engineRig.SetValue(null, saved));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) { _undo = undo; }
        public void Dispose() => _undo();
    }

    private const System.Reflection.BindingFlags Private =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static void Invoke(MetersPanel panel, string name) =>
        typeof(MetersPanel).GetMethod(name, Private)!.Invoke(panel, null);

    /// <summary>WPF's own first-letter search, the code a typed letter reaches.</summary>
    private static void Search(ItemsControl control, string text)
    {
        var flags = System.Reflection.BindingFlags.NonPublic;
        object search = typeof(TextSearch).GetMethod("EnsureInstance", flags | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { control })!;
        // A fresh prefix each time, as after the search's own timeout.
        typeof(TextSearch).GetMethod("ResetState", flags | System.Reflection.BindingFlags.Instance)?
            .Invoke(search, null);
        typeof(TextSearch).GetMethod("DoSearch", flags | System.Reflection.BindingFlags.Instance)!
            .Invoke(search, new object[] { text });
    }

    /// <summary>
    /// Put entries shaped like a live radio's into the panel's own list, as
    /// FromReading builds them ("group: name, description"), and rebuild the
    /// categories and the source list the way an inventory change does.
    /// </summary>
    private static void LiveShaped(MetersPanel panel,
        params (string Group, string Name, string Description, MeterCategory Category)[] meters)
    {
        var choiceType = typeof(MetersPanel).GetNestedType("SourceChoice", System.Reflection.BindingFlags.NonPublic)!;
        var all = (System.Collections.IList)typeof(MetersPanel).GetField("_allChoices", Private)!.GetValue(panel)!;
        all.Clear();
        foreach (var m in meters)
        {
            object c = Activator.CreateInstance(choiceType)!;
            choiceType.GetProperty("Key")!.SetValue(c, m.Name);
            choiceType.GetProperty("Display")!.SetValue(c, m.Group + ": " + m.Name + ", " + m.Description);
            choiceType.GetProperty("Category")!.SetValue(c, m.Category);
            all.Add(c);
        }
        Invoke(panel, "RefreshCategoryChoices");
        Invoke(panel, "ShowCategory");
    }

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
