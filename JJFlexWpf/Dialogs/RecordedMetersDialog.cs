#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;
using Radios.Alarms;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The recorded set (#566, first ruling): every published meter, ticked when
/// it is recorded — by an alarm, which cannot be unticked here, or by the
/// operator for the capture alone.
/// </summary>
public sealed class RecordedMetersDialog : JJFlexDialog
{
    /// <summary>Over no radio, for a window built with nothing attached.</summary>
    public RecordedMetersDialog() : this(OperatorAlarmsModel.Detached()) { }

    private RecordedMetersDialog(OperatorAlarmsModel model)
    {
        Title = Lexicon.Get("alarms.dialog.recorded_title");
        Width = 640;
        Height = 480;
        ResizeMode = ResizeMode.CanResize;

        var root = new DockPanel { Margin = new Thickness(12) };
        var intro = new TextBlock { Text = Lexicon.Get("alarms.dialog.recorded_intro"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var close = new Button { Content = Lexicon.Get("alarms.dialog.close"), MinWidth = 96, Height = 28, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(close, Lexicon.Get("alarms.dialog.close"));
        close.Click += (_, _) => CloseWithResult(true);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var panel = new StackPanel();
        foreach (var (meter, byAlarm, byOperator) in model.RecordedChoices())
        {
            string label = byAlarm ? Lexicon.Get("alarms.dialog.recorded_by_alarm", ("meter", meter.Label)) : meter.Label + ", " + meter.SourceLabel;
            var box = new CheckBox { Content = label, IsChecked = byAlarm || byOperator, IsEnabled = !byAlarm, Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetName(box, label);
            MeterDescriptor m = meter;
            // Checked and Unchecked, never Click alone: keyboard toggles raise these.
            box.Checked += (_, _) => model.SetRecorded(m, true);
            box.Unchecked += (_, _) => model.SetRecorded(m, false);
            panel.Children.Add(box);
        }
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        Content = root;
    }

    public static void Show(Window owner, OperatorAlarmsModel model)
    {
        var d = new RecordedMetersDialog(model) { Owner = owner };
        d.ShowModalDialog();
    }
}
