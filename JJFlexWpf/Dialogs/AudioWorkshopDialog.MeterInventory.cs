using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, Meter Inventory tab: which meters this radio actually has,
/// what each one reads, and which of them have gone quiet.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and deliberately shipped before any diagnostic rules exist. The
/// radio publishes over a hundred meters — a FLEX-8600 reported 102 — and until
/// now not one of them was visible anywhere in the app: the Live Meters tab
/// shows eight hand-picked readings and the full list appeared only in a trace
/// file nobody reads mid-QSO. An operator asking "does my radio even have a
/// meter for that?" had no way to find out. This tab answers that, and the
/// answers it produces are what good rules get written from later.
/// </para>
/// <para>
/// The whole inventory is one read-only multi-line edit rather than a grid or a
/// list of controls, and that is an accessibility decision rather than a lazy
/// one. A hundred meters as a hundred focusable controls is a hundred tab stops;
/// as a table it is unreadable aloud. As text it is one tab stop that a screen
/// reader walks line by line, word by word, at the operator's own pace, with
/// select-all and copy already working the way they work everywhere else.
/// </para>
/// <para>
/// Values are a SNAPSHOT taken when the report was built, not a live readout,
/// and the summary line says so. Rewriting a hundred lines twice a second would
/// throw a screen reader's review position away on every tick — the exact
/// failure mode the Live Meters tab's change-only assignment exists to avoid,
/// multiplied by a hundred. Refresh is a key press, and the tab refreshes itself
/// whenever it is not being read.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    #region Tab 4: Meter Inventory

    /// <summary>How many meters, from when, and whether anything has changed
    /// since. Never focus-stealing: this is where a change that arrives while
    /// the operator is reading the report gets announced quietly.</summary>
    private TextBox? _inventorySummaryBox;

    /// <summary>The inventory itself, as text.</summary>
    private TextBox? _inventoryReportBox;

    /// <summary>The inventory changed while the report was being read, so the
    /// refresh was held back rather than yanking the review position away.</summary>
    private bool _inventoryPending;

    /// <summary>The rig's inventory we are currently subscribed to, so the
    /// subscription can be dropped when the rig changes. A workshop outlives
    /// several radios.</summary>
    private MeterInventory? _boundInventory;

    /// <summary>When the report text was last built.</summary>
    private DateTime _inventoryBuiltAt;

    private void BuildMeterInventoryTab()
    {
        // NOT a radio-only section. With no radio this tab still has something
        // true to say, and "no radio connected" is itself the answer to why the
        // list is empty — disabling it would leave an operator guessing.
        AddSectionHeader(MeterInventoryContent, "Summary");

        _inventorySummaryBox = new TextBox
        {
            Text = "No radio connected, so there is no meter list to show.",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2),
            FontSize = 12
        };
        AutomationProperties.SetName(_inventorySummaryBox, "Meter inventory summary");
        AddToSection(MeterInventoryContent, _inventorySummaryBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 4, 2, 2)
        };

        var refresh = new Button
        {
            Content = "Refresh",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 0)
        };
        AutomationProperties.SetName(refresh, "Refresh the meter inventory");
        refresh.Click += (s, e) => RefreshMeterInventory(announce: true);
        buttons.Children.Add(refresh);

        var copy = new Button
        {
            Content = "Copy to clipboard",
            Padding = new Thickness(8, 4, 8, 4)
        };
        AutomationProperties.SetName(copy, "Copy the meter inventory to the clipboard");
        copy.Click += (s, e) => CopyMeterInventory();
        buttons.Children.Add(copy);

        AddToSection(MeterInventoryContent, buttons);

        AddSectionHeader(MeterInventoryContent, "The meters");

        _inventoryReportBox = new TextBox
        {
            Text = "Connect a radio and the meters it publishes appear here.",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 260,
            Margin = new Thickness(2),
            FontSize = 12
        };
        AutomationProperties.SetName(_inventoryReportBox, "Meter inventory");
        AutomationProperties.SetHelpText(_inventoryReportBox,
            "Read-only. Arrow through it line by line, or select all and copy.");
        AddToSection(MeterInventoryContent, _inventoryReportBox);
    }

    /// <summary>
    /// Follow this rig's inventory, and stop following the last one. Called from
    /// <see cref="SetRig"/>, including with no rig at all.
    /// </summary>
    private void BindMeterInventory(FlexBase? rig)
    {
        MeterInventory? next = rig?.MeterInventory;
        if (ReferenceEquals(next, _boundInventory))
        {
            RefreshMeterInventory(announce: false);
            return;
        }

        if (_boundInventory != null)
            _boundInventory.InventoryChanged -= OnMeterInventoryChanged;
        _boundInventory = next;
        if (_boundInventory != null)
            _boundInventory.InventoryChanged += OnMeterInventoryChanged;

        _inventoryPending = false;
        RefreshMeterInventory(announce: false);
    }

    /// <summary>
    /// The radio's meter set changed. This arrives on FlexLib's meter thread, so
    /// it is marshalled before it touches a control.
    /// </summary>
    private void OnMeterInventoryChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) MeterInventoryChangedOnUi();
        else Dispatcher.BeginInvoke(MeterInventoryChangedOnUi);
    }

    private void MeterInventoryChangedOnUi()
    {
        // Do not rebuild the report out from under someone reading it. Meters
        // arrive in a rush during registration, and a screen reader's review
        // position lives in that text box — replacing the text while the
        // operator is walking it would drop them back at the top, repeatedly,
        // for a list that is still growing.
        if (_inventoryReportBox?.IsKeyboardFocusWithin == true)
        {
            _inventoryPending = true;
            UpdateInventorySummary();
            return;
        }
        RefreshMeterInventory(announce: false);
    }

    /// <summary>
    /// Rebuild the report from the current inventory.
    /// </summary>
    /// <param name="announce">True when the operator asked for it — a refresh
    /// nobody requested should not speak over whatever they were reading.</param>
    private void RefreshMeterInventory(bool announce)
    {
        if (_inventoryReportBox == null) return;

        MeterInventory? inv = _boundInventory;
        string text;
        if (_rig == null || inv == null)
        {
            text = "No radio is connected, so the radio has not told us what it "
                 + "can measure. Connect a radio and press Refresh.";
        }
        else
        {
            text = inv.ToText();
        }

        _inventoryPending = false;
        _inventoryBuiltAt = DateTime.Now;

        // Assign only on change: identical text would still reset the caret.
        if (_inventoryReportBox.Text != text)
            _inventoryReportBox.Text = text;

        UpdateInventorySummary();

        if (announce)
        {
            int n = inv?.Count ?? 0;
            ScreenReaderOutput.Speak(
                n == 0 ? "No meters reported." : n + " meters.",
                VerbosityLevel.Terse, interrupt: true);
        }
    }

    /// <summary>
    /// The one-line state above the report: how many, how fresh, and whether the
    /// radio has said anything new since it was built.
    /// </summary>
    private void UpdateInventorySummary()
    {
        if (_inventorySummaryBox == null) return;

        string text;
        MeterInventory? inv = _boundInventory;
        if (_rig == null || inv == null)
        {
            text = "No radio connected, so there is no meter list to show.";
        }
        else
        {
            int groups = inv.Groups.Count;
            var sb = new StringBuilder();
            sb.Append(inv.Count).Append(inv.Count == 1 ? " meter" : " meters");
            sb.Append(" in ").Append(groups).Append(groups == 1 ? " group" : " groups");
            sb.Append(". Readings below are from ")
              .Append(_inventoryBuiltAt.ToString("h:mm:ss tt"))
              .Append(" — press Refresh for current ones.");
            if (_inventoryPending)
                sb.Append(" The radio has reported more meters since; Refresh to include them.");
            text = sb.ToString();
        }

        if (_inventorySummaryBox.Text != text)
            _inventorySummaryBox.Text = text;
    }

    /// <summary>
    /// The whole inventory onto the clipboard: the seed of an evidence block in
    /// an email to Flex, or in a message to whoever is helping. Copies what is on
    /// screen, freshened first, so the pasted text and the read text agree.
    /// </summary>
    private void CopyMeterInventory()
    {
        RefreshMeterInventory(announce: false);

        string body = _inventoryReportBox?.Text ?? "";
        string header = "Meter inventory";
        if (_rig != null)
        {
            string model = _rig.RadioModel ?? "";
            if (model.Length != 0) header += " — " + model;
        }
        header += ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        try
        {
            Clipboard.SetText(header + Environment.NewLine + Environment.NewLine + body);
            ScreenReaderOutput.Speak("Copied.", VerbosityLevel.Terse, interrupt: true);
        }
        catch
        {
            // Clipboard access fails when another process is holding it. Say so
            // rather than announcing a copy that did not happen.
            ScreenReaderOutput.Speak("Copy failed.", VerbosityLevel.Terse, interrupt: true);
        }
    }

    #endregion
}
