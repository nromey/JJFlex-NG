using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// How the Audio Workshop and Check your audio build their sections: a named
/// GroupBox with a heading level holding a panel, the "section being filled"
/// that each added control goes into, and the register of radio-only sections
/// and controls that disable, and leave the tab order, with no radio.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, two windows (#835).</b> Check your audio was the
/// Workshop's Hear Yourself page and builds its sections the same way, so F6,
/// the group announcements and the no-radio rule behave alike in both. The
/// integration pass found the first cut of #835 had copied these helpers into
/// the new window; this is the one place they live now.
/// </para>
/// <para>
/// <b>Why a GroupBox (2026-08-13).</b> Bold TextBlocks LOOKED like sections and
/// were not: a named TextBlock is neither a UIA group nor a heading, so tabbing
/// never announced crossing into a new section. A GroupBox is announced on
/// entry while simply tabbing, which is how a dialog is actually crossed.
/// </para>
/// <para>
/// <b>Why sections enrol, not controls (Sprint 30 Track A).</b> A control added
/// to a radio-side section a year from now is covered on the day it is written,
/// with nobody remembering to add it to a list. Disabling a GroupBox cascades
/// to its children without touching their own IsEnabled, so state a section
/// manages for its own reasons survives and reappears on connect.
/// </para>
/// </remarks>
internal sealed class DialogSections
{
    private readonly List<UIElement> _radioOnly = new();

    /// <summary>The panel of the section most recently opened; controls added now go here.</summary>
    public StackPanel? Current { get; private set; }

    /// <summary>Open a section in <paramref name="parent"/>; later additions go into it.</summary>
    public GroupBox Open(StackPanel parent, string text)
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
        Current = panel;
        return group;
    }

    /// <summary>Open a section whose every control needs a radio.</summary>
    public GroupBox OpenRadio(StackPanel parent, string text)
    {
        var group = Open(parent, text);
        _radioOnly.Add(group);
        return group;
    }

    /// <summary>
    /// Add a control to the section being built. Falls back to the outer panel
    /// if no section is open: a control that escapes its group is a layout bug,
    /// but one that vanishes is a missing feature, and the second is worse.
    /// </summary>
    public void Add(StackPanel fallback, UIElement child) => (Current ?? fallback).Children.Add(child);

    /// <summary>A radio-side control in a section that also holds PC-side ones.</summary>
    public void AddRadio(StackPanel fallback, UIElement child)
    {
        Add(fallback, child);
        _radioOnly.Add(child);
    }

    /// <summary>
    /// Enable or disable everything that cannot act without a radio. IsEnabled
    /// false drops it from the tab order and tells UIA it is unavailable, so a
    /// review cursor still learns the feature exists.
    /// </summary>
    public void SetRadioAvailable(bool live)
    {
        foreach (var element in _radioOnly) element.IsEnabled = live;
    }

    /// <summary>
    /// Hand a radio change to a non-modal singleton window on its own thread.
    /// Both windows outlive the radio, so the shell tells them; safe from any
    /// thread.
    /// </summary>
    /// <param name="onlyIfLoaded">True for an arriving radio (a closed window has nothing to show it in); false for a departing one, which must always stop the poll.</param>
    public static void PostRigChange<T>(T? window, Action<T> change, bool onlyIfLoaded) where T : Window
    {
        if (window == null || (onlyIfLoaded && !window.IsLoaded)) return;
        if (window.Dispatcher.CheckAccess()) change(window);
        else window.Dispatcher.BeginInvoke(() => change(window));
    }
}
