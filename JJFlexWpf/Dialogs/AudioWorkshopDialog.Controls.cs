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
    /// Open a section. Returns the GroupBox so a caller can retitle it later;
    /// everything added afterwards goes into <see cref="_section"/>, the panel
    /// created inside it.
    /// </summary>
    /// <remarks>
    /// These were bold TextBlocks until 2026-08-13. That LOOKED like a section
    /// and was not one. A named TextBlock is not a UIA group and not a
    /// heading, so tabbing between controls never announced crossing from one
    /// section into the next, and NVDA's H key had nothing to jump to. Noel,
    /// testing 4.1.16.829: "I'm not hearing any group / I'm not able to get
    /// from group to group."
    ///
    /// <para>
    /// That made the walk-through ordering built the day before invisible to
    /// the only audience it exists for. The sections were correctly SEQUENCED
    /// and their boundaries were not perceivable, which is the whole feature —
    /// a walk-through you cannot feel yourself moving through is just a list.
    /// </para>
    ///
    /// <para>
    /// A GroupBox is announced on entry and exit while simply tabbing, which
    /// is how the dialog is actually crossed. HeadingLevel restores H /
    /// Shift+H jumping on top of that, for an operator who knows where they
    /// are going.
    /// </para>
    /// </remarks>
    private GroupBox AddSectionHeader(StackPanel parent, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(6, 2, 2, 2) };
        var group = new GroupBox
        {
            Header = text,
            Margin = new Thickness(0, 8, 0, 4),
            Content = panel
        };
        AutomationProperties.SetName(group, text);
        AutomationProperties.SetHeadingLevel(group, AutomationHeadingLevel.Level2);
        parent.Children.Add(group);
        _section = panel;
        return group;
    }

    /// <summary>
    /// Add a control to the section currently being built. Falls back to the
    /// tab's outer panel if no section has been opened — a control that
    /// escapes its group is a layout bug, but a control that vanishes is a
    /// missing feature, and the second is worse.
    /// </summary>
    private void AddToSection(StackPanel fallback, UIElement child)
    {
        (_section ?? fallback).Children.Add(child);
    }

    /// <summary>
    /// Open a section whose every control needs a radio. Identical to
    /// <see cref="AddSectionHeader"/> except that the GroupBox enrols in
    /// <see cref="_radioOnlyElements"/>, so it disables — and drops out of the
    /// tab order with everything inside it — whenever no rig is attached.
    ///
    /// <para>Enrolling the SECTION rather than each control is the whole
    /// point: a control added to one of these sections a year from now is
    /// covered on the day it is written, with nobody remembering to add it to
    /// a list. That is the failure this replaces — five checkboxes were
    /// enumerated and the four value controls beside them were not.</para>
    /// </summary>
    private GroupBox AddRadioSection(StackPanel parent, string text)
    {
        var group = AddSectionHeader(parent, text);
        _radioOnlyElements.Add(group);
        return group;
    }

    /// <summary>
    /// Add a control that needs a radio to a section that also holds PC-side
    /// controls. Only for genuinely mixed sections — anywhere the whole
    /// section is radio-side, use <see cref="AddRadioSection"/> instead so
    /// later additions are covered automatically.
    /// </summary>
    private void AddRadioControl(StackPanel fallback, UIElement child)
    {
        AddToSection(fallback, child);
        _radioOnlyElements.Add(child);
    }

    private static CheckBox MakeToggle(string label)
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

    private static CycleFieldControl MakeCycle(string label, string[] options)
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
        if (isOn) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();

        // DELETED: CheckBox ToggleState, announced by the screen reader. Note
        // this fired BEFORE the radio confirmed anything, so it never carried
        // radio truth the checkbox lacked.
    }

    #endregion
}
