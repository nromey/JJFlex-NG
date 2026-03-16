using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Accessible radio status dialog. Shows a live snapshot of radio state
/// in a ListBox for screen reader navigation. Auto-refreshes every 2 seconds.
/// Sprint 24 Phase 9A.
/// </summary>
public partial class StatusDialog : JJFlexDialog
{
    /// <summary>Radio instance. Null when not connected.</summary>
    public FlexBase? Rig { get; set; }

    private readonly DispatcherTimer _refreshTimer;

    public StatusDialog()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (s, e) => RefreshStatus();

        Loaded += StatusDialog_Loaded;
        Closing += StatusDialog_Closing;
    }

    private void StatusDialog_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        _refreshTimer.Start();
    }

    private void StatusDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
    }

    /// <summary>
    /// Rebuild the status list from current radio state.
    /// </summary>
    private void RefreshStatus()
    {
        StatusList.Items.Clear();

        if (Rig == null)
        {
            StatusList.Items.Add("Not connected to a radio.");
            StatusList.Items.Add("Connect to a radio to see status here.");
            return;
        }

        var snap = RadioStatusBuilder.BuildDetailedStatus(Rig);
        if (!snap.IsConnected)
        {
            StatusList.Items.Add("Not connected to a radio.");
            return;
        }

        // Radio info section
        AddSection("Radio");
        AddItem($"{snap.RadioModel}");
        if (!string.IsNullOrEmpty(snap.RadioNickname))
            AddItem($"Name: {snap.RadioNickname}");
        AddItem(snap.IsRemote ? "Connected via SmartLink (remote)" : "Connected on local network");

        // Slice section
        int numSlices = Rig.MyNumSlices;
        AddSection($"Slices ({numSlices} active)");

        if (numSlices == 0)
        {
            AddItem("No active slices");
        }
        else
        {
            string fullSliceStatus = RadioStatusBuilder.BuildFullSliceStatus(Rig);
            // Split the multi-sentence status into individual items
            foreach (string part in fullSliceStatus.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    AddItem(trimmed);
            }
        }

        // Meters section
        AddSection("Meters");
        string meterSummary = MeterToneEngine.GetMeterSpeechSummary();
        if (!string.IsNullOrWhiteSpace(meterSummary))
        {
            foreach (string part in meterSummary.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    AddItem(trimmed);
            }
        }

        // TX state
        AddSection("Transmit");
        AddItem(snap.IsTransmitting ? "Transmitting" : "Receiving");
        if (Rig.CanTransmit)
        {
            string txLetter = Rig.TXSliceLetter;
            if (!string.IsNullOrEmpty(txLetter))
                AddItem($"TX slice: {txLetter}");
        }

        // ATU section
        if (Rig.HasATU)
        {
            AddSection("Antenna Tuner");
            string tunerState = Rig.FlexTunerType switch
            {
                FlexBase.FlexTunerTypes.auto => "ATU: Automatic",
                FlexBase.FlexTunerTypes.manual => "ATU: Manual (bypass)",
                _ => "ATU: Not available"
            };
            AddItem(tunerState);
        }
    }

    private void AddSection(string heading)
    {
        // Blank line before section (except first)
        if (StatusList.Items.Count > 0)
            StatusList.Items.Add("");
        StatusList.Items.Add($"--- {heading} ---");
    }

    private void AddItem(string text)
    {
        StatusList.Items.Add(text);
    }

    /// <summary>
    /// Build a plain-text version of the status for clipboard.
    /// </summary>
    private string BuildClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("JJ Flexible Radio Access — Status Snapshot");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var item in StatusList.Items)
        {
            sb.AppendLine(item?.ToString() ?? "");
        }

        return sb.ToString();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildClipboardText());
            Radios.ScreenReaderOutput.Speak("Status copied to clipboard", VerbosityLevel.Terse, true);
        }
        catch
        {
            Radios.ScreenReaderOutput.Speak("Could not copy to clipboard", VerbosityLevel.Critical, true);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
