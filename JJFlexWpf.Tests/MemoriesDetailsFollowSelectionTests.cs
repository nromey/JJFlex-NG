using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1062: in Memories, the details panel always shows the memory the list has
/// selected, and leaving a text box whose value the memory already holds
/// writes nothing. Before the fix, Delete re-selected the first memory with
/// the selection handler suppressed, so the panel went on showing the DELETED
/// memory; Tab from the list into Frequency, and on through the panel, then
/// wrote its frequency, filters, name, owner and group into the memory now
/// selected, silently. A failed Add did the same. Also #996's open item: after
/// Add and Delete, focus lands on the selected memory's row, not on the list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> The dialog is an uninitialized object
/// (<see cref="RuntimeHelpers.GetUninitializedObject"/>): no constructor runs,
/// no XAML loads, no window is built, shown or sourced. Its named controls are
/// real WPF controls put into their fields by reflection, on a private STA
/// thread, and the list sits in a focus scope that is never shown; the first
/// check of every test is that the list has no presentation source. The
/// dialog's own handlers (Loaded, Add, Delete, each text box's LostFocus) are
/// called by name, the way WPF calls them in the app (the XAML wires each one),
/// and the radio is a set of fake delegates over plain objects. The duplicate
/// name check always answers "not a duplicate", so no question dialog can be
/// constructed on any path here.
/// </para>
/// <para>
/// <b>What this cannot see:</b> real keyboard focus and real Tab order, which
/// need a window. Focus is read as LOGICAL focus in the scope, cleared before
/// each Add or Delete so the handler's own choice is what the scope holds (as
/// <c>DialogLoadedFocusKeptTests</c> explains). Where keyboard focus lands
/// after Add and Delete, and what the screen reader says there, are the
/// harness's to read. The Delete KEY's own landing (<c>DeleteKeyRemoves</c>,
/// queued on the dispatcher) is not run here; it changes the selection
/// unsuppressed, so the selection handler shows the details on that path.
/// </para>
/// </remarks>
public sealed class MemoriesDetailsFollowSelectionTests
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>A memory on the fake radio.</summary>
    private sealed class FakeMemory
    {
        public string Id = "";
        public string Freq = "";
        public string Name = "";
        public string Owner = "";
        public string Group = "";
        public int Low;
        public int High;
        public int SquelchLevel;
        public int OffsetKHz;
        public override string ToString() => Id;
    }

    /// <summary>The dialog, its fake radio and every write the dialog made.</summary>
    private sealed class Rig
    {
        public MemoriesDialog Dialog = null!;
        public List<FakeMemory> Memories = new();
        public List<string> Writes = new();
        public StackPanel Scope = null!;
        public FakeMemory? ToAdd;
        public ListBox List => (ListBox)Field("MemoryList");
        public TextBox Box(string name) => (TextBox)Field(name);
        public object Field(string name) => typeof(MemoriesDialog).GetField(name, AnyInstance)!.GetValue(Dialog)!;
        public void Call(string handler) =>
            (typeof(MemoriesDialog).GetMethod(handler, AnyInstance)
                ?? throw new InvalidOperationException("MemoriesDialog has no handler " + handler))
            .Invoke(Dialog, new object?[] { null, null });
    }

    private static readonly string[] TextBoxHandlers =
    {
        "FreqBox_LostFocus", "FilterLowBox_LostFocus", "FilterHighBox_LostFocus",
        "SquelchLevelBox_LostFocus", "OffsetBox_LostFocus", "NameBox_LostFocus",
        "OwnerBox_LostFocus", "GroupBox_LostFocus",
    };

    [Fact]
    public void After_Delete_the_details_show_the_memory_now_selected()
    {
        OnSta(() =>
        {
            var rig = Open(Three());
            var (a, b) = (rig.Memories[0], rig.Memories[1]);
            rig.List.SelectedIndex = 1;   // the operator arrows to B
            AssertShows(rig, b, "before Delete");

            rig.Call("DeleteButton_Click");

            Assert.DoesNotContain(b, rig.Memories);
            Assert.Same(a, Selected(rig));
            AssertShows(rig, a, "after Delete, with the first memory selected");
        });
    }

    [Fact]
    public void After_Delete_tabbing_through_the_details_leaves_the_memory_now_selected_as_it_was()
    {
        OnSta(() =>
        {
            var rig = Open(Three());
            var a = rig.Memories[0];
            var before = Snapshot(a);
            rig.List.SelectedIndex = 1;
            rig.Call("DeleteButton_Click");

            foreach (var handler in TextBoxHandlers) rig.Call(handler);

            Assert.True(rig.Writes.Count == 0, "Tabbing through the details after Delete wrote: " + string.Join("; ", rig.Writes));
            Assert.Equal(before, Snapshot(a));
        });
    }

    [Fact]
    public void Leaving_each_box_unchanged_writes_nothing()
    {
        OnSta(() =>
        {
            var rig = Open(Three());

            foreach (var handler in TextBoxHandlers) rig.Call(handler);

            Assert.True(rig.Writes.Count == 0, "Leaving unchanged boxes wrote: " + string.Join("; ", rig.Writes));
        });
    }

    [Fact]
    public void A_deliberate_edit_still_writes_to_the_selected_memory()
    {
        OnSta(() =>
        {
            var rig = Open(Three());
            var a = rig.Memories[0];

            rig.Box("FreqBox").Text = "14.200000";
            rig.Call("FreqBox_LostFocus");
            rig.Box("FilterLowBox").Text = "-200";
            rig.Call("FilterLowBox_LostFocus");
            rig.Box("FilterHighBox").Text = "2900";
            rig.Call("FilterHighBox_LostFocus");
            rig.Box("SquelchLevelBox").Text = "40";
            rig.Call("SquelchLevelBox_LostFocus");
            rig.Box("OffsetBox").Text = "600";
            rig.Call("OffsetBox_LostFocus");
            rig.Box("NameBox").Text = "Net";
            rig.Call("NameBox_LostFocus");
            rig.Box("OwnerBox").Text = "Club";
            rig.Call("OwnerBox_LostFocus");
            rig.Box("GroupBox").Text = "Nets";
            rig.Call("GroupBox_LostFocus");

            Assert.Equal(new[]
            {
                "Freq A=14.200000", "Low A=-200", "High A=2900", "Squelch A=40", "Offset A=600",
                "Name A=Net", "Owner A=Club", "Group A=Nets",
            }, rig.Writes);
            Assert.Same(a, Selected(rig));
        });
    }

    [Fact]
    public void Deleting_the_last_memory_clears_the_details()
    {
        OnSta(() =>
        {
            var rig = Open(new List<FakeMemory> { Memory("A", "14.074000", "Calling", "Me", "FT8", -100, 3000, 10, 0) });

            rig.Call("DeleteButton_Click");

            Assert.Empty(rig.Memories);
            Assert.Equal(-1, rig.List.SelectedIndex);
            foreach (var box in new[] { "FreqBox", "FilterLowBox", "FilterHighBox", "SquelchLevelBox", "OffsetBox", "NameBox", "OwnerBox", "GroupBox" })
                Assert.True(rig.Box(box).Text.Length == 0, box + " still shows \"" + rig.Box(box).Text + "\" from the deleted memory");
            foreach (var handler in TextBoxHandlers) rig.Call(handler);
            Assert.Empty(rig.Writes);
        });
    }

    [Fact]
    public void A_failed_Add_shows_the_memory_now_selected()
    {
        OnSta(() =>
        {
            var rig = Open(Three());
            rig.List.SelectedIndex = 1;
            AssertShows(rig, rig.Memories[1], "before Add");

            rig.ToAdd = null;   // the radio refused the new memory
            rig.Call("AddButton_Click");

            var selected = Selected(rig);
            Assert.NotNull(selected);
            AssertShows(rig, selected!, "after a failed Add");
        });
    }

    [Fact]
    public void After_Add_the_details_and_focus_are_on_the_new_memory()
    {
        OnSta(() =>
        {
            var rig = Open(Three());
            var added = Memory("D", "28.400000", "Ten", "Me", "Beacons", -150, 2800, 0, 0);
            rig.ToAdd = added;
            ClearFocus(rig);

            rig.Call("AddButton_Click");

            Assert.Same(added, Selected(rig));
            AssertShows(rig, added, "after Add");
            AssertFocusOnSelectedRow(rig, "after Add");
        });
    }

    [Fact]
    public void After_Delete_focus_is_on_the_selected_row()
    {
        OnSta(() =>
        {
            var rig = Open(Three());
            rig.List.SelectedIndex = 1;
            ClearFocus(rig);

            rig.Call("DeleteButton_Click");

            AssertFocusOnSelectedRow(rig, "after Delete");
        });
    }

    [Fact]
    public void After_deleting_the_last_memory_focus_is_on_the_list()
    {
        OnSta(() =>
        {
            var rig = Open(new List<FakeMemory> { Memory("A", "14.074000", "Calling", "Me", "FT8", -100, 3000, 10, 0) });
            ClearFocus(rig);

            rig.Call("DeleteButton_Click");

            Assert.Same(rig.List, FocusManager.GetFocusedElement(rig.Scope));
        });
    }

    private static List<FakeMemory> Three() => new()
    {
        Memory("A", "14.074000", "Calling", "Me", "FT8", -100, 3000, 10, 0),
        Memory("B", "7.074000", "Forty", "Club", "FT4", -50, 2500, 20, 600),
        Memory("C", "146.520000", "Simplex", "Net", "FM", -6000, 6000, 30, 0),
    };

    private static FakeMemory Memory(string id, string freq, string name, string owner, string group,
        int low, int high, int squelch, int offset) => new()
        {
            Id = id, Freq = freq, Name = name, Owner = owner, Group = group,
            Low = low, High = high, SquelchLevel = squelch, OffsetKHz = offset,
        };

    /// <summary>The dialog over a fake radio, opened as WPF opens it: Loaded.</summary>
    private static Rig Open(List<FakeMemory> memories)
    {
        var rig = new Rig { Memories = memories };
        var dialog = (MemoriesDialog)RuntimeHelpers.GetUninitializedObject(typeof(MemoriesDialog));
        rig.Dialog = dialog;

        var list = new ListBox();
        Set(dialog, "MemoryList", list);
        foreach (var box in new[] { "FreqBox", "FilterLowBox", "FilterHighBox", "SquelchLevelBox", "OffsetBox", "NameBox", "OwnerBox", "GroupBox" })
            Set(dialog, box, new TextBox());
        foreach (var combo in new[] { "ModeCombo", "ToneModeCombo", "ToneFreqCombo", "SquelchCombo", "OffsetDirCombo" })
            Set(dialog, combo, new ComboBox());
        Set(dialog, "FMControlsPanel", new StackPanel());

        // XAML wires this in the app: SelectionChanged="MemoryList_SelectionChanged".
        var onSelection = typeof(MemoriesDialog).GetMethod("MemoryList_SelectionChanged", AnyInstance)!;
        list.SelectionChanged += (SelectionChangedEventHandler)Delegate.CreateDelegate(typeof(SelectionChangedEventHandler), dialog, onSelection);

        FakeMemory M(object o) => (FakeMemory)o;
        dialog.GetSortedMemories = () => rig.Memories
            .Select(m => new MemoriesDialog.MemoryDisplayItem { FullName = m.Group + "." + m.Name, MemoryRef = m })
            .ToList();
        dialog.AddMemory = () =>
        {
            if (rig.ToAdd == null) return null;
            rig.Memories.Add(rig.ToAdd);
            return new MemoriesDialog.MemoryDisplayItem { FullName = rig.ToAdd.Name, MemoryRef = rig.ToAdd };
        };
        dialog.RemoveMemory = o => rig.Memories.Remove(M(o));
        dialog.FormatFrequency = o => M(o).Freq;
        dialog.GetMode = _ => "usb";
        dialog.GetFilterLow = o => M(o).Low;
        dialog.GetFilterHigh = o => M(o).High;
        dialog.GetSquelchLevel = o => M(o).SquelchLevel;
        dialog.GetOffsetKHz = o => M(o).OffsetKHz;
        dialog.GetName = o => M(o).Name;
        dialog.GetOwner = o => M(o).Owner;
        dialog.GetGroup = o => M(o).Group;
        dialog.IsDuplicateName = (_, _) => false;   // never a question dialog
        dialog.SetFrequencyFromText = (o, v) => { rig.Writes.Add("Freq " + M(o).Id + "=" + v); M(o).Freq = v; };
        dialog.SetFilterLow = (o, v) => { rig.Writes.Add("Low " + M(o).Id + "=" + v); M(o).Low = v; };
        dialog.SetFilterHigh = (o, v) => { rig.Writes.Add("High " + M(o).Id + "=" + v); M(o).High = v; };
        dialog.SetSquelchLevel = (o, v) => { rig.Writes.Add("Squelch " + M(o).Id + "=" + v); M(o).SquelchLevel = v; };
        dialog.SetOffsetKHz = (o, v) => { rig.Writes.Add("Offset " + M(o).Id + "=" + v); M(o).OffsetKHz = v; };
        dialog.SetName = (o, v) => { rig.Writes.Add("Name " + M(o).Id + "=" + v); M(o).Name = v; };
        dialog.SetOwner = (o, v) => { rig.Writes.Add("Owner " + M(o).Id + "=" + v); M(o).Owner = v; };
        dialog.SetGroup = (o, v) => { rig.Writes.Add("Group " + M(o).Id + "=" + v); M(o).Group = v; };

        // The list in a focus scope that is never shown, after a button that
        // stands for Add (first in the dialog's tab order), measured and
        // arranged by hand so the list generates its row containers.
        var scope = new StackPanel();
        FocusManager.SetIsFocusScope(scope, true);
        scope.Children.Add(new Button());
        scope.Children.Add(list);
        scope.Measure(new Size(400, 400));
        scope.Arrange(new Rect(0, 0, 400, 400));
        rig.Scope = scope;
        Assert.Null(PresentationSource.FromVisual(list));

        rig.Call("MemoriesDialog_Loaded");
        Assert.Equal(memories.Count > 0 ? 0 : -1, list.SelectedIndex);
        return rig;
    }

    private static void Set(object dialog, string field, object value) =>
        (typeof(MemoriesDialog).GetField(field, AnyInstance)
            ?? throw new InvalidOperationException("MemoriesDialog has no field " + field))
        .SetValue(dialog, value);

    private static FakeMemory? Selected(Rig rig) =>
        (rig.List.SelectedItem as MemoriesDialog.MemoryDisplayItem)?.MemoryRef as FakeMemory;

    private static (string, string, string, string, int, int, int, int) Snapshot(FakeMemory m) =>
        (m.Freq, m.Name, m.Owner, m.Group, m.Low, m.High, m.SquelchLevel, m.OffsetKHz);

    private static void AssertShows(Rig rig, FakeMemory m, string when)
    {
        string shown = rig.Box("FreqBox").Text + " / " + rig.Box("NameBox").Text + " / " + rig.Box("OwnerBox").Text
            + " / " + rig.Box("GroupBox").Text + " / " + rig.Box("FilterLowBox").Text + " / " + rig.Box("FilterHighBox").Text
            + " / " + rig.Box("SquelchLevelBox").Text + " / " + rig.Box("OffsetBox").Text;
        string expected = m.Freq + " / " + m.Name + " / " + m.Owner + " / " + m.Group + " / " + m.Low + " / " + m.High
            + " / " + m.SquelchLevel + " / " + m.OffsetKHz;
        Assert.True(expected == shown, "The details " + when + " show " + shown + ", not the selected memory's " + expected);
    }

    private static void ClearFocus(Rig rig) => FocusManager.SetFocusedElement(rig.Scope, null);

    private static void AssertFocusOnSelectedRow(Rig rig, string when)
    {
        var list = rig.List;
        Assert.True(list.SelectedIndex >= 0, "Nothing is selected " + when);
        // The list re-reads its memories on Add and Delete, so its rows are
        // generated anew on the next layout pass. Run one here, after the
        // handler has already chosen focus, so the row exists to compare with
        // whichever choice the handler made.
        list.UpdateLayout();
        var row = list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem;
        Assert.True(row != null, "The unhosted list generated no row container " + when + ", so this test could not show where focus lands");
        object? focused = FocusManager.GetFocusedElement(rig.Scope);
        string what = focused switch
        {
            null => "nothing",
            ListBoxItem item => "row " + list.ItemContainerGenerator.IndexFromContainer(item),
            ListBox => "the list itself",
            _ => focused.GetType().Name,
        };
        Assert.True(ReferenceEquals(row, focused), "Focus " + when + " is on " + what + ", not on the selected row " + list.SelectedIndex);
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }
}
