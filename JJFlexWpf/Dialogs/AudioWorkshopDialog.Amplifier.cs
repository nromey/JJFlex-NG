using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, Amplifier tab: what the radio says about an EXTERNAL
/// amplifier or antenna tuner, their meters, and the one control that acts on
/// them — standby versus operate.
/// </summary>
/// <remarks>
/// <para>
/// <b>External.</b> An 8000-series radio has a built-in amplifier stage reached
/// through a separate API, present on every one of them whether or not anything
/// is bolted to the back. This tab never counts that as an amplifier; where its
/// meters exist they are named as the radio's own, at the bottom of the report,
/// so nobody reads amplifier-shaped meters as proof of an amplifier they do not
/// own.
/// </para>
/// <para>
/// <b>One reading, not a hundred controls.</b> The whole picture — model,
/// serial, network, antenna map, state, and every meter the amplifier publishes
/// — renders into a single read-only multi-line edit. A meter set of unknown
/// size cannot become a control apiece: that would be a hundred tab stops, and a
/// grid of it is unreadable aloud. As text it is ONE tab stop a screen reader
/// walks at the operator's own pace, and select-all and copy work for free,
/// which is exactly what an operator needs when someone asks them what their
/// amplifier is reporting.
/// </para>
/// <para>
/// <b>What is not here.</b> The tuner is shown and cannot be commanded. FlexLib
/// exposes operate, bypass and autotune for a Tuner Genius XL and there has
/// never been one on this bench, so wiring those would be shipping behaviour
/// nobody has watched happen.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    #region Tab: Amplifier

    private TextBox? _ampReportBox;
    private CheckBox? _ampOperateCheck;

    /// <summary>Set while the checkbox is being written to match the radio, so
    /// the click handler does not read a programmatic sync as an operator
    /// asking for a state change.</summary>
    private bool _ampSyncing;

    private DispatcherTimer? _ampTimer;

    /// <summary>
    /// Its own timer rather than a tick on the shared meter poll: this tab
    /// refreshes at 1 Hz (an amplifier's state and temperatures are not audio
    /// rate), it runs only while its own tab is on screen, and it starts and
    /// stops without another tab's poll having to know about it.
    /// </summary>
    private static readonly TimeSpan AmpPollInterval = TimeSpan.FromSeconds(1);

    private void BuildAmplifierTab()
    {
        AddRadioSection(AmplifierContent, "Amplifier controls");

        _ampOperateCheck = MakeToggle("Operate");
        // Disabled until an amplifier is actually reported. A checkbox that
        // announces "checked" and sends nothing is the confident lie this
        // dialog has already been fixed for once.
        _ampOperateCheck.IsEnabled = false;
        _ampOperateCheck.Click += AmpOperate_Click;
        AddToSection(AmplifierContent, _ampOperateCheck);

        AddRadioSection(AmplifierContent, "Amplifier and tuner report");

        _ampReportBox = new TextBox
        {
            Text = "No radio is connected, so nothing can be said about an "
                 + "amplifier or a tuner.",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 260,
            Margin = new Thickness(2),
            FontSize = 12
        };
        AutomationProperties.SetName(_ampReportBox, "Details");
        AddToSection(AmplifierContent, _ampReportBox);

        _ampTimer = new DispatcherTimer { Interval = AmpPollInterval };
        _ampTimer.Tick += AmplifierTimer_Tick;
        _ampTimer.Start();

        // Refresh the moment the tab is entered, so an operator never lands on
        // a reading up to a second old. SelectionChanged is multicast; this
        // adds a handler rather than owning the event.
        MainTabs.SelectionChanged += (s, e) =>
        {
            if (AmplifierTab.IsSelected) RefreshAmplifierTab();
        };

        // The timer must not outlive the dialog. Closed is multicast too, so
        // this file cleans up after itself without editing the shell's handler.
        Closed += (s, e) => _ampTimer?.Stop();
    }

    private void AmplifierTimer_Tick(object? sender, EventArgs e)
    {
        if (!AmplifierTab.IsSelected) return;
        RefreshAmplifierTab();
    }

    /// <summary>
    /// Re-read the amplifier picture and put it on screen.
    /// </summary>
    /// <remarks>
    /// The service is event-driven and does not need poking — it follows
    /// FlexLib's amplifier, tuner and meter-inventory events. Refresh is called
    /// anyway, once a second while this tab is visible, for two reasons: meter
    /// VALUES move continuously and no event marks that, and a tab an operator
    /// is staring at should not depend on every event path having been wired
    /// correctly. A refresh is a copy of two short lists.
    /// </remarks>
    private void RefreshAmplifierTab()
    {
        FlexBase? rig = _rig;

        if (rig == null)
        {
            SetAmpReport("No radio is connected, so nothing can be said about an "
                + "amplifier or a tuner.");
            SetAmpOperateSilently(false, enabled: false);
            return;
        }

        AmplifierInventory amps = rig.Amplifiers;
        amps.Refresh();

        SetAmpReport(amps.ToText());

        AmplifierInfo? active = amps.ActiveAmplifier;
        if (active == null)
            SetAmpOperateSilently(false, enabled: false);
        else
            SetAmpOperateSilently(active.IsOperate, enabled: true);
    }

    /// <summary>
    /// Assign the report only on change: rewriting identical text once a second
    /// would reset a screen reader's review position in a block of text the
    /// operator is in the middle of reading, which is the one thing this control
    /// exists to let them do.
    /// </summary>
    private void SetAmpReport(string text)
    {
        if (_ampReportBox == null || _ampReportBox.Text == text) return;
        _ampReportBox.Text = text;
    }

    /// <summary>Write the Operate box to match the radio without the click
    /// handler treating it as an operator request.</summary>
    private void SetAmpOperateSilently(bool isOperate, bool enabled)
    {
        if (_ampOperateCheck == null) return;
        _ampSyncing = true;
        try
        {
            _ampOperateCheck.IsEnabled = enabled;
            if (_ampOperateCheck.IsChecked != isOperate)
                _ampOperateCheck.IsChecked = isOperate;
        }
        finally
        {
            _ampSyncing = false;
        }
    }

    /// <summary>
    /// Ask the amplifier to switch between standby and operate.
    /// </summary>
    /// <remarks>
    /// The command is fire-and-forget by nature: FlexLib sends
    /// <c>amplifier set &lt;handle&gt; operate=0/1</c> and the amplifier answers
    /// with its own status line, so the state shown here comes from the next
    /// refresh rather than from what we asked for. When nothing could be sent,
    /// the box goes back to where the radio has it and the reason is spoken —
    /// the screen reader has already said "checked" by then, and leaving that
    /// standing would be a lie about a transmitter.
    /// </remarks>
    private void AmpOperate_Click(object sender, RoutedEventArgs e)
    {
        if (_ampSyncing) return;

        bool want = _ampOperateCheck?.IsChecked == true;
        FlexBase? rig = _rig;
        AmplifierInfo? active = rig?.Amplifiers.ActiveAmplifier;

        if (rig == null || active == null)
        {
            SetAmpOperateSilently(false, enabled: false);
            ScreenReaderOutput.Speak("There is no amplifier to switch.",
                VerbosityLevel.Terse, true);
            return;
        }

        if (active.IsOperate == want)
        {
            // Already there — a race with the amplifier's own status, not a
            // failure. Say nothing; the box is right.
            SetAmpOperateSilently(active.IsOperate, enabled: true);
            return;
        }

        if (!rig.Amplifiers.SetOperate(active.Handle, want))
        {
            SetAmpOperateSilently(active.IsOperate, enabled: true);
            ScreenReaderOutput.Speak("That did not reach the amplifier — nothing was sent.",
                VerbosityLevel.Terse, true);
            return;
        }

        if (want) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
    }

    #endregion
}
