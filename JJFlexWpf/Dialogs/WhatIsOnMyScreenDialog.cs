using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// "What is on my screen right now" — every window on the desktop as a list
/// the operator reads at their own pace, the one with the keyboard first.
/// Sprint 44 Track Q (#154). Opened by <c>Ctrl+J, Alt+W</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a list and not speech.</b> The same rule Track K applied to the
/// key help: speech is for an answer, a navigable surface is for a search.
/// A census is a search — the operator is looking for the row that explains
/// what just happened — and every row is a sentence long. The count rides
/// in the title so it is the first thing said; the first row is the window
/// that has the keyboard, so it is the second.
/// </para>
/// <para>
/// <b>The snapshot is taken BEFORE this window exists</b>, so the list
/// describes the screen as it was when the key was pressed, not with this
/// dialog on top of it. Refresh takes another look, and that look does
/// include this window, which is honest: at that moment it is what has the
/// keyboard.
/// </para>
/// <para>
/// Read-only. It never closes, focuses or acts on anything it lists.
/// </para>
/// </remarks>
public sealed class WhatIsOnMyScreenDialog : JJFlexDialog
{
    private readonly ListBox _list = new();

    /// <summary>Take the census now and show it.</summary>
    public WhatIsOnMyScreenDialog() : this(Radios.DesktopWindowCensus.Take()) { }

    public WhatIsOnMyScreenDialog(Radios.DesktopWindowSnapshot snapshot)
    {
        Width = 680;
        Height = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        // A help surface in the key-layer sense: while it is open the
        // persistent layers stand down and the list owns the keyboard, so
        // arrowing through the rows never adjusts a volume underneath.
        KeyHelpSurfaces.Attach(this);

        var dock = new DockPanel { Margin = new Thickness(12) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        string refreshText = Radios.Lexicon.Get("leader.windows.refresh_button");
        var refresh = new Button
        {
            Content = refreshText, MinWidth = 100, Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetName(refresh, refreshText.Replace("_", ""));
        AutomationProperties.SetAccessKey(refresh, "Alt+R");
        AutomationProperties.SetAcceleratorKey(refresh, "Alt+R");
        refresh.Click += (_, _) => Refresh();

        string closeText = Radios.Lexicon.Get("connect.dialog.close");
        var close = new Button
        {
            Content = closeText, MinWidth = 80, Height = 28, IsCancel = true, IsDefault = true,
        };
        AutomationProperties.SetName(close, closeText.Replace("_", ""));
        AutomationProperties.SetAccessKey(close, "Alt+C");
        AutomationProperties.SetAcceleratorKey(close, "Alt+C");
        close.Click += (_, _) => CloseWithResult(false);

        buttons.Children.Add(refresh);
        buttons.Children.Add(close);
        dock.Children.Add(buttons);

        AutomationProperties.SetName(_list, Radios.Lexicon.Get("leader.windows.list_name"));
        JJFlexHelp.SetText(_list, Radios.Lexicon.Get("leader.windows.help"));
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                Refresh();
                e.Handled = true;
            }
        };
        dock.Children.Add(_list);

        Content = dock;
        Populate(snapshot);
    }

    private void Populate(Radios.DesktopWindowSnapshot snapshot)
    {
        var rows = snapshot.Windows;
        Title = Radios.DesktopWindowCensusSpeech.Title(rows.Count);

        _list.Items.Clear();
        for (int i = 0; i < rows.Count; i++)
            _list.Items.Add(Radios.DesktopWindowCensusSpeech.Row(rows[i], i + 1));
        if (rows.Count == 0)
            _list.Items.Add(Radios.Lexicon.Get("leader.windows.none"));

        // The watchdog's record, when there is one: the census is where the
        // operator comes to ask "what happened?", and this is the answer.
        var theft = Radios.DesktopWindowCensus.LastTheft;
        if (theft != null)
            _list.Items.Add(Radios.DesktopWindowCensusSpeech.LastTheftRow(theft));

        _list.SelectedIndex = 0;
    }

    private void Refresh()
    {
        Populate(Radios.DesktopWindowCensus.Take());
        FocusFirstControl();
        // The title changed under the reader; say the new count, and that
        // it is a fresh look rather than the same one re-read.
        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("leader.windows.refreshed", ("title", Title)),
            Radios.Speech.SpeechIntent.Interrupt, Radios.VerbosityLevel.Terse);
    }

    protected override void FocusFirstControl()
    {
        if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
        _list.Focus();
    }

    /// <summary>Take the census and show it modally.</summary>
    public static void Present()
    {
        new WhatIsOnMyScreenDialog().ShowModalDialog();
    }
}
