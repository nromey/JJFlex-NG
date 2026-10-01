#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;
using Radios.Alarms;

namespace JJFlexWpf.Dialogs;

/// <summary>A titled list of lines with Close. History uses it.</summary>
public sealed class SimpleListDialog : JJFlexDialog
{
    /// <summary>An empty history, for a window built with nothing attached.</summary>
    public SimpleListDialog()
        : this(Lexicon.Get("alarms.dialog.history_title"), Lexicon.Get("alarms.dialog.history_title"),
            Array.Empty<string>(), Lexicon.Get("alarms.dialog.history_empty")) { }

    private SimpleListDialog(string title, string listName, IReadOnlyList<string> rows, string emptyText)
    {
        Title = title;
        Width = 640;
        Height = 420;
        ResizeMode = ResizeMode.CanResize;

        var root = new DockPanel { Margin = new Thickness(12) };
        var close = new Button { Content = Lexicon.Get("alarms.dialog.close"), MinWidth = 96, Height = 28, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(close, Lexicon.Get("alarms.dialog.close"));
        close.Click += (_, _) => CloseWithResult(true);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var list = new ListBox();
        AutomationProperties.SetName(list, listName);
        if (rows.Count == 0) list.Items.Add(emptyText);
        else foreach (string r in rows) list.Items.Add(r);
        root.Children.Add(list);
        Content = root;
        Loaded += (_, _) => { if (list.Items.Count > 0) list.SelectedIndex = 0; };
    }

    public static void Show(Window owner, string title, string listName, IReadOnlyList<string> rows, string emptyText)
    {
        var d = new SimpleListDialog(title, listName, rows, emptyText) { Owner = owner };
        d.ShowModalDialog();
    }
}
