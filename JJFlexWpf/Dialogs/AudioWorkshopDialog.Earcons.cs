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
/// Audio Workshop, Earcon Explorer tab: a button per earcon so an
/// operator can hear each sound on demand.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Tab 3: Earcon Explorer

    private void BuildEarconExplorerTab()
    {
        // Meter Tones
        AddSectionHeader(EarconExplorerContent, "Meter Tones");
        AddEarconButton(_section ?? EarconExplorerContent, "Beep", () => EarconPlayer.Beep());
        AddEarconButton(_section ?? EarconExplorerContent, "Warning Beep", () => EarconPlayer.Warning1Beep());
        AddEarconButton(_section ?? EarconExplorerContent, "Warning 2 Beep", () => EarconPlayer.Warning2Beep());
        AddEarconButton(_section ?? EarconExplorerContent, "Oh Crap Beep", () => EarconPlayer.OhCrapBeep());
        AddEarconButton(_section ?? EarconExplorerContent, "Confirm Tone", () => EarconPlayer.ConfirmTone());

        // PTT & Transmission
        AddSectionHeader(EarconExplorerContent, "PTT and Transmission");
        AddEarconButton(_section ?? EarconExplorerContent, "TX Start Tone", () => EarconPlayer.TxStartTone());
        AddEarconButton(_section ?? EarconExplorerContent, "TX Stop Tone", () => EarconPlayer.TxStopTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Hard Kill Tone", () => EarconPlayer.HardKillTone());

        // Filter Sounds
        AddSectionHeader(EarconExplorerContent, "Filter Sounds");
        AddEarconButton(_section ?? EarconExplorerContent, "Filter Edge Enter", () => EarconPlayer.FilterEdgeEnterTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Filter Edge Exit", () => EarconPlayer.FilterEdgeExitTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Filter Edge Move", () => EarconPlayer.FilterEdgeMoveTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Filter Boundary Hit (Low)", () => EarconPlayer.FilterBoundaryHitTone(true));
        AddEarconButton(_section ?? EarconExplorerContent, "Filter Boundary Hit (High)", () => EarconPlayer.FilterBoundaryHitTone(false));
        AddEarconButton(_section ?? EarconExplorerContent, "Filter Squeeze", () => EarconPlayer.FilterSqueezeTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Filter Stretch", () => EarconPlayer.FilterStretchTone());

        // Warnings — the two "something is wrong" sounds, with the calm
        // feature toggles directly beneath them. The order is the point: the
        // alarm has to be obviously not-a-toggle, and the only way to know is
        // to hear them back to back. Sprint 31, #111.
        AddSectionHeader(EarconExplorerContent, "Warnings");
        AddEarconButton(_section ?? EarconExplorerContent, "Warning Alarm", () => EarconPlayer.WarningAlarmTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Problem Recorded", () => EarconPlayer.ProblemRecordedTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Feature On (for comparison)", () => EarconPlayer.FeatureOnTone());
        AddEarconButton(_section ?? EarconExplorerContent, "Feature Off (for comparison)", () => EarconPlayer.FeatureOffTone());

        // Alerts
        AddSectionHeader(EarconExplorerContent, "Alerts");
        AddEarconButton(_section ?? EarconExplorerContent, "Band Boundary Beep", () => EarconPlayer.BandBoundaryBeep());
        AddEarconButton(_section ?? EarconExplorerContent, "Chirp (400 to 800 Hz)", () => EarconPlayer.Chirp(400, 800, 200));
        AddEarconButton(_section ?? EarconExplorerContent, "Chirp (800 to 400 Hz)", () => EarconPlayer.Chirp(800, 400, 200));
    }

    private static void AddEarconButton(StackPanel parent, string label, Action playAction)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var button = new Button
        {
            Content = $"Play: {label}",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(button, $"Play {label}");
        button.Click += (s, e) =>
        {
            ScreenReaderOutput.Speak(label, VerbosityLevel.Terse);
            playAction();
        };

        panel.Children.Add(button);
        parent.Children.Add(panel);
    }

    #endregion
}
