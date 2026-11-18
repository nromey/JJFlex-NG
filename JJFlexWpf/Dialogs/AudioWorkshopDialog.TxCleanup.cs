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
/// Audio Workshop, TX Audio tab: the PC Cleanup section — transmit noise
/// reduction, the noise gate, and the residual monitor.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region PC Cleanup Section (Track I)

    /// <summary>
    /// PC-side transmit cleanup: noise reduction and the noise gate, with a
    /// monitor that can play WHAT WAS REMOVED. The radio's own processing
    /// (compander, EQ) sculpts the audio it is given; this cleans the room
    /// out of the audio before the radio ever hears it — the fan, the
    /// computer, the air conditioner.
    ///
    /// The removed-audio monitor is the tuning instrument: listening to the
    /// output can never reveal over-processing, because the missing parts
    /// are not there to hear. Hear your own words in the REMOVED audio and
    /// the cleanup is eating your voice — raise the noise floor (it was
    /// "turn the strength down" until #1019 flipped the control) while it
    /// plays until the words fade. The floor control is live for exactly
    /// that loop.
    ///
    /// The gate threshold is not a knob here: it is derived from the room's
    /// measured noise floor (the same figure the Microphone Check reports),
    /// floor plus a margin, so a quiet 3 AM shack and the same shack with
    /// the air conditioner on both land right without retuning.
    /// </summary>
    /// <summary>
    /// The transmit noise floor field's shape, as data a windowless test can read:
    /// the floor's range and step, its initial value from the one default (transmit
    /// RNNoise, floor 20, ruled S3), and whether Up lowers it, from
    /// <see cref="NoiseController.UpRaisesFloor"/>.
    /// </summary>
    internal static (int Min, int Max, int Step, int Initial, bool UpLowers) TxFloorField() =>
        (NoiseFloor.Min, NoiseFloor.Max, NoiseFloor.Step,
         NoiseFloor.DefaultFloor("rnnoise", DenoiseDirection.Transmit), !NoiseController.UpRaisesFloor);

    /// <summary>
    /// The PC Cleanup section, now one door (Sprint 49 Track H2; design part
    /// seven and departure D3, ruled "That's fine"): transmit noise reduction,
    /// the gate, "Listen to cleanup" and the gate's Return to defaults (once
    /// "Reset cleanup to recommended") all live in Home's Transmission
    /// expander, so there is one surface and one truth. The button opens it
    /// there; the Workshop keeps no second copy of any of those controls.
    /// </summary>
    private void BuildTxCleanupSection()
    {
        AddSectionHeader(ThisComputerContent, "PC Cleanup");
        var door = new Button
        {
            Content = Lexicon.Get("audio.cleanup.door"),
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(door, Lexicon.Get("audio.cleanup.door"));
        JJFlexHelp.SetText(door, Lexicon.Get("audio.cleanup.door_help"));
        door.Click += (s, e) => OpenTransmitNoiseOnHome?.Invoke();
        AddToSection(ThisComputerContent, door);
    }

    /// <summary>Opens Home's Transmission expander at its noise fields. Set by MainWindow (Sprint 49 Track H2).</summary>
    public static Action? OpenTransmitNoiseOnHome { get; set; }

    #endregion
}
