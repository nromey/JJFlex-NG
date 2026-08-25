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
/// Audio Workshop, Meters category: which meters this radio actually has,
/// what each one reads, and which of them have gone quiet.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and deliberately shipped before any diagnostic rules exist. The
/// radio publishes over a hundred meters — a FLEX-8600 reported 102 — and until
/// now not one of them was visible anywhere in the app: the Meters page
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
/// failure mode the live readings' change-only assignment exists to avoid,
/// multiplied by a hundred. Refresh is a key press, and the tab refreshes itself
/// whenever it is not being read.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    #region Meters category: the full inventory

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

    /// <summary>The button that reveals the inventory, and afterwards refreshes
    /// it. Kept so its label can change once the report is showing.</summary>
    private Button? _inventoryRevealButton;

    /// <summary>Everything below the reveal button, hidden until asked for.</summary>
    private StackPanel? _inventoryBody;

    /// <summary>
    /// The full meter inventory, as a section of the Meters category rather
    /// than a category of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Collapsed until asked for, and that is the point of the merge.</b>
    /// It was a peer of the eight live readings until 2026-08-25, which put a
    /// hundred-line report and an eight-line readout side by side in the
    /// category list under two names that did not say which was which. The
    /// readings are what an operator wants almost every time; the full list is
    /// a deliberate act, so it costs one button press and nothing before that.
    /// </para>
    /// <para>
    /// It is still NOT a radio-only section. With no radio it has something
    /// true to say, and "no radio connected" is itself the answer to why the
    /// list is empty.
    /// </para>
    /// </remarks>
    private void BuildMeterInventorySection()
    {
        AddSectionHeader(MeterInventoryContent, "All meters");

        var intro = new TextBlock
        {
            Text = "The eight readings above are the ones worth watching. Your radio "
                 + "publishes around a hundred more, and this lists every one of them "
                 + "with what it currently reads.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        };
        AddToSection(MeterInventoryContent, intro);

        _inventoryRevealButton = new Button
        {
            Content = "Show All Meters",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(_inventoryRevealButton, "Show all meters");
        JJFlexHelp.SetText(_inventoryRevealButton,
            "Lists every meter this radio publishes, with its current reading. "
            + "Once it is showing, this button refreshes it.");
        _inventoryRevealButton.Click += (s, e) => RevealMeterInventory();
        AddToSection(MeterInventoryContent, _inventoryRevealButton);

        // Everything else lives in a panel that starts collapsed. Collapsed,
        // not merely hidden: a Hidden control keeps its tab stop, so the
        // operator would tab into an empty report they never asked for.
        _inventoryBody = new StackPanel { Visibility = Visibility.Collapsed };
        AddToSection(MeterInventoryContent, _inventoryBody);

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
        _inventoryBody.Children.Add(_inventorySummaryBox);

        var copy = new Button
        {
            Content = "Copy to clipboard",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 4, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(copy, "Copy the meter inventory to the clipboard");
        JJFlexHelp.SetText(copy,
            "The report below is an ordinary text box, so Control A then Control C "
            + "does the same thing. This is here for when that is not to hand.");
        copy.Click += (s, e) => CopyMeterInventory();
        _inventoryBody.Children.Add(copy);

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
        _inventoryBody.Children.Add(_inventoryReportBox);
    }

    /// <summary>True once the operator has asked for the inventory. Everything
    /// that refreshes it in the background checks this first — there is no
    /// point rebuilding a hundred lines nobody has looked at.</summary>
    private bool InventoryShowing => _inventoryBody?.Visibility == Visibility.Visible;

    /// <summary>
    /// Reveal the inventory, or refresh it if it is already showing.
    /// </summary>
    /// <remarks>
    /// Focus moves to the REPORT on the first reveal, not back to the button.
    /// The operator pressed a button called "Show All Meters" and the meters
    /// are the thing they asked for; leaving focus on the button would make
    /// them hunt for content that is now several tab stops away. On a refresh
    /// focus stays put, because they are already reading it.
    /// </remarks>
    private void RevealMeterInventory()
    {
        if (_inventoryBody == null) return;

        bool firstReveal = !InventoryShowing;
        _inventoryBody.Visibility = Visibility.Visible;

        if (_inventoryRevealButton != null)
        {
            _inventoryRevealButton.Content = "Refresh All Meters";
            AutomationProperties.SetName(_inventoryRevealButton, "Refresh all meters");
        }

        RefreshMeterInventory(announce: !firstReveal);

        if (firstReveal)
            _inventoryReportBox?.Focus();
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
                n == 0
                    ? Lexicon.Get("audio.meters.none_reported")
                    : Lexicon.Get("audio.meters.count", ("count", n)),
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
            ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.copied"), VerbosityLevel.Terse, interrupt: true);
        }
        catch
        {
            // Clipboard access fails when another process is holding it. Say so
            // rather than announcing a copy that did not happen.
            ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.copy_failed"), VerbosityLevel.Terse, interrupt: true);
        }
    }

    #endregion
}
