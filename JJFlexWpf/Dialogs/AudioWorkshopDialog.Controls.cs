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
/// Audio Workshop control factories: the section headers and the toggle,
/// value, cycle and meter-reading controls every tab in this dialog is
/// built from.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Control Factories

    /// <summary>
    /// Open a section; everything added afterwards goes into it. Returns the
    /// GroupBox so a caller can retitle it later.
    /// </summary>
    private GroupBox AddSectionHeader(StackPanel parent, string text) => _sections.Open(parent, text);

    /// <summary>Add a control to the section currently being built (see <see cref="DialogSections.Add"/>).</summary>
    private void AddToSection(StackPanel fallback, UIElement child) => _sections.Add(fallback, child);

    /// <summary>
    /// Open a section whose every control needs a radio: it disables, and drops
    /// out of the tab order with everything inside it, whenever no rig is
    /// attached. Enrolling the SECTION rather than each control is the point —
    /// five checkboxes were once enumerated and the four value controls beside
    /// them were not.
    /// </summary>
    private GroupBox AddRadioSection(StackPanel parent, string text) => _sections.OpenRadio(parent, text);

    /// <summary>
    /// A control that needs a radio, in a section that also holds PC-side
    /// controls. Only for genuinely mixed sections.
    /// </summary>
    private void AddRadioControl(StackPanel fallback, UIElement child) => _sections.AddRadio(fallback, child);

    /// <summary>
    /// The sections, the section being filled, and the radio-only register —
    /// shared with Check your audio as one implementation since #835
    /// (<see cref="DialogSections"/>, which keeps the history of why sections
    /// are GroupBoxes and why sections enrol rather than controls).
    /// </summary>
    private readonly DialogSections _sections = new();

    /// <summary>The panel of the section being built, for builders that add directly.</summary>
    private StackPanel? _section => _sections.Current;

    internal static CheckBox MakeToggle(string label)
    {
        var cb = new CheckBox
        {
            Content = label,
            Margin = new Thickness(2),
            FontSize = 12
        };
        AutomationProperties.SetName(cb, label);
        return cb;
    }

    private static ValueFieldControl MakeValue(string label, int min, int max, int step)
    {
        var ctl = new ValueFieldControl();
        ctl.Setup(label, min, max, step);
        return ctl;
    }

    internal static CycleFieldControl MakeCycle(string label, string[] options)
    {
        var ctl = new CycleFieldControl();
        ctl.Setup(label, options);
        return ctl;
    }

    /// <summary>
    /// One live meter reading: a read-only EDIT, not a label (Track D1,
    /// 2026-08-16). Same idiom and same reasoning as the mic reading box —
    /// focusable, arrowable at the operator's own pace, and the screen
    /// reader's read-current-control command speaks it on demand. The
    /// accessible name is the meter's name, set once; the 2 Hz poll touches
    /// only the text.
    /// </summary>
    /// <remarks>
    /// This replaced MakeMeterLabel, a plain TextBlock with
    /// AutomationLiveSetting.Polite — and the live region did NOT move
    /// across. That setting was the only channel the old readout had: nothing
    /// was focusable, so narration-on-change was compensation for a broken
    /// tree (memory/feedback_speak_only_when_ui_does_not_convey — fix the
    /// tree, don't narrate around it). Kept on a focusable box it would be
    /// strictly worse: eight polite announcers at 2 Hz, led by an S-meter
    /// that moves on nearly every tick of a live band, queue endless chatter
    /// that starves the reading the operator cares about and talks over the
    /// very review commands this control exists to serve. Continuous
    /// monitoring stays a real need with purpose-built channels that don't
    /// collide with speech: the meter tones today, and per-meter "audible"
    /// as an explicit operator choice in the unified meter model (Tracks
    /// D2/D3) — not a hardcoded all-eight firehose.
    /// </remarks>
    private static TextBox MakeMeterReading(string meterName)
    {
        var box = new TextBox
        {
            Text = meterName + ": no reading yet",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Margin = new Thickness(2),
            FontSize = 12
        };
        AutomationProperties.SetName(box, meterName);
        return box;
    }

    private void SetToggle(string label, Action<FlexBase.OffOnValues> setter, bool isOn)
    {
        if (_polling || _rig == null) return;
        setter(isOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off);
        ToggleFeedback.Announce(label, isOn);
    }

    #endregion
}
