using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using JJBrailleTrial.Model;

namespace JJBrailleTrial;

/// <summary>
/// The trial's one window: the Home-like line, the carrier beside it, the
/// bridge that receives the reader side's posted messages, and the trace list.
/// All decisions live in <see cref="TrialLine"/> and <see cref="BridgeProtocol"/>;
/// this class shows them, forwards keys and messages, and speaks.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TrialLine _line = new();
    private readonly TrialTrace _trace = new();
    private readonly SilentHomeBox _home;
    private readonly BrailleCarrier _carrier = new();
    private readonly DispatcherTimer _walkTimer;

    private IntPtr _hwnd;
    private uint _helloMsg;
    private uint _routeMsg;
    private uint _commandMsg;
    private bool _programmaticCaret;
    private string _published = "";

    public MainWindow()
    {
        Title = "JJ Flexible braille trial";
        Width = 760;
        Height = 520;

        _home = new SilentHomeBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 22,
            Margin = new Thickness(8),
        };
        AutomationProperties.SetAutomationId(_home, "JJFlexHome");
        AutomationProperties.SetName(_home, "Trial Home");

        var keys = new TextBlock
        {
            Margin = new Thickness(8, 0, 8, 8),
            TextWrapping = TextWrapping.Wrap,
            Text = "Left and Right move the caret. Up and Down tune. Space flips VOX or tuning. "
                 + "F5 starts or stops the tuning walk, F6 takes one step, F7 switches between the Home view and the field view. "
                 + "Tab reaches the trace.",
        };

        var list = new ListBox
        {
            ItemsSource = _trace.Lines,
            Margin = new Thickness(8),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };
        AutomationProperties.SetName(list, "Trace");

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_home, 0);
        Grid.SetRow(_carrier, 0);
        Grid.SetRow(keys, 1);
        Grid.SetRow(list, 2);
        panel.Children.Add(_home);
        panel.Children.Add(_carrier);
        panel.Children.Add(keys);
        panel.Children.Add(list);
        Content = panel;

        _walkTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _walkTimer.Tick += (_, _) => DoWalkStep("walk");

        _home.SelectionChanged += OnHomeSelectionChanged;
        _home.PreviewKeyDown += OnHomePreviewKeyDown;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Loaded += (_, _) =>
        {
            Keyboard.Focus(_home);
        };
        Closed += (_, _) =>
        {
            _walkTimer.Stop();
            _trace.Write("session", "trial closed");
            _trace.Dispose();
        };

        _trace.Write("session", "trial started; trace file " + _trace.Path + (_trace.FileOpen ? "" : " (COULD NOT BE OPENED, list only)"));
        RefreshScreen();
        Publish("start");
    }

    // ------------------------------------------------------------------ bridge

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this);
        _hwnd = source.Handle;
        _helloMsg = NativeMethods.RegisterWindowMessage(BridgeProtocol.HelloMessageName);
        _routeMsg = NativeMethods.RegisterWindowMessage(BridgeProtocol.RouteMessageName);
        _commandMsg = NativeMethods.RegisterWindowMessage(BridgeProtocol.CommandMessageName);

        // Let the messages through even if this program ever runs at a higher
        // integrity level than the reader. Harmless otherwise; the result is traced.
        bool f1 = NativeMethods.AllowMessage(_hwnd, _helloMsg);
        bool f2 = NativeMethods.AllowMessage(_hwnd, _routeMsg);
        bool f3 = NativeMethods.AllowMessage(_hwnd, _commandMsg);
        source.AddHook(WndProc);

        _trace.Write("bridge", string.Format(CultureInfo.InvariantCulture,
            "listening on window 0x{0:X}; {1}=0x{2:X} {3}=0x{4:X} {5}=0x{6:X}; message filter allowed {7}/{8}/{9}",
            _hwnd.ToInt64(),
            BridgeProtocol.HelloMessageName, _helloMsg,
            BridgeProtocol.RouteMessageName, _routeMsg,
            BridgeProtocol.CommandMessageName, _commandMsg,
            f1, f2, f3));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint m = unchecked((uint)msg);
        if (m == 0)
        {
            return IntPtr.Zero;
        }
        ulong w = unchecked((ulong)wParam.ToInt64());
        ulong l = unchecked((ulong)lParam.ToInt64());
        if (m == _helloMsg)
        {
            OnHello(BridgeProtocol.DecodeHello(w, l), w, l);
            handled = true;
        }
        else if (m == _routeMsg)
        {
            OnRoute(BridgeProtocol.DecodeRoute(w, l), w, l);
            handled = true;
        }
        else if (m == _commandMsg)
        {
            OnCommand(BridgeProtocol.DecodeCommand(w, l));
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnHello(HelloMessage hello, ulong w, ulong l)
    {
        string version = hello.Version == BridgeProtocol.ProtocolVersion
            ? "protocol " + hello.Version.ToString(CultureInfo.InvariantCulture)
            : "UNSUPPORTED protocol " + hello.Version.ToString(CultureInfo.InvariantCulture);
        _trace.Write("hello", string.Format(CultureInfo.InvariantCulture,
            "{0}, {1}, display {2} row(s) of {3} cells (wParam 0x{4:X}, lParam 0x{5:X})",
            BridgeProtocol.ReaderName(hello.Reader), version, hello.Rows, hello.Columns, w, l));
    }

    private void OnRoute(RouteMessage route, ulong w, ulong l)
    {
        string received = string.Format(CultureInfo.InvariantCulture,
            "layout {0}, field {1}, character {2} (wParam 0x{3:X}, lParam 0x{4:X})",
            route.Layout, route.Ordinal, route.CharPos is int c ? c.ToString(CultureInfo.InvariantCulture) : "not known", w, l);

        // Design section 3.5: act only while Home has the keyboard focus and
        // this program is the foreground application.
        if (!_home.IsKeyboardFocused)
        {
            _trace.Write("route", received + "; dropped: Home does not have the keyboard focus");
            return;
        }
        if (NativeMethods.GetForegroundWindow() != _hwnd)
        {
            _trace.Write("route", received + "; dropped: the trial is not the foreground window");
            return;
        }

        var decision = BridgeProtocol.DecideRoute(route, _line);
        if (!decision.Accepted)
        {
            _trace.Write("route", received + "; dropped: " + decision.Reason);
            return;
        }
        var before = _line.GetField(decision.Ordinal);
        string decoded = string.Format(CultureInfo.InvariantCulture, "decoded {0} \"{1}\", {2}",
            before.Label, before.Text,
            decision.CharPos is int ch ? "character " + ch.ToString(CultureInfo.InvariantCulture) + " '" + before.Text[ch] + "'" : "character not known");
        if (decision.Reason.Length > 0)
        {
            decoded += " (" + decision.Reason + ")";
        }

        var outcome = _line.ApplyRoute(decision);
        _trace.Write("route", received + "; " + decoded + "; did: " + outcome.TraceText
            + (outcome.Spoken is null ? "" : "; said \"" + outcome.Spoken + "\""));
        RefreshScreen();
        Publish("route");
        if (outcome.Spoken is not null)
        {
            Speak(outcome.Spoken);
        }
    }

    private void OnCommand(CommandMessage command)
    {
        if (command.Command != BridgeProtocol.CommandSwitchView)
        {
            _trace.Write("command", string.Format(CultureInfo.InvariantCulture, "unknown command {0} from {1}; ignored",
                command.Command, BridgeProtocol.ReaderName(command.Reader)));
            return;
        }
        var view = _line.SwitchView();
        _trace.Write("command", "switch view from " + BridgeProtocol.ReaderName(command.Reader) + "; now " + TrialLine.ViewWord(view));
        Publish("command");
        Speak(view == BrailleView.Home ? "Home view" : "Field view");
    }

    // -------------------------------------------------------------------- keys

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F5:
                if (_walkTimer.IsEnabled)
                {
                    _walkTimer.Stop();
                    _trace.Write("walk", "stopped");
                    Speak("Walk stopped");
                }
                else
                {
                    _walkTimer.Start();
                    _trace.Write("walk", "started, a 100 hertz step every 200 milliseconds");
                    Speak("Walk started");
                }
                e.Handled = true;
                break;
            case Key.F6:
                DoWalkStep("one step");
                e.Handled = true;
                break;
            case Key.F7:
                var view = _line.SwitchView();
                _trace.Write("command", "switch view from the keyboard; now " + TrialLine.ViewWord(view));
                Publish("view");
                Speak(view == BrailleView.Home ? "Home view" : "Field view");
                e.Handled = true;
                break;
        }
    }

    private void OnHomePreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                {
                    var outcome = _line.Tune(e.Key == Key.Up ? 1 : -1);
                    _trace.Write("tune", (e.Key == Key.Up ? "Up" : "Down") + ": " + outcome.TraceText);
                    if (outcome.Done)
                    {
                        RefreshScreen();
                        Publish("tune");
                    }
                    Speak(outcome.Spoken);
                    e.Handled = true;
                    break;
                }
            case Key.Space:
                {
                    var outcome = _line.PressSpace();
                    _trace.Write("key", outcome.TraceText);
                    if (outcome.Done)
                    {
                        RefreshScreen();
                        Publish("space");
                    }
                    if (outcome.Spoken is not null)
                    {
                        Speak(outcome.Spoken);
                    }
                    e.Handled = true;
                    break;
                }
        }
    }

    private void OnHomeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_programmaticCaret)
        {
            return;
        }
        int oldOrdinal = _line.CaretOrdinal;
        if (!_line.SetCaretFromIndex(_home.CaretIndex))
        {
            return;
        }
        bool fieldChanged = oldOrdinal != _line.CaretOrdinal;
        _trace.Write("caret", string.Format(CultureInfo.InvariantCulture,
            "keyboard moved the caret to {0} character {1} (screen index {2})",
            _line.GetField(_line.CaretOrdinal).Label, _line.CaretChar, _home.CaretIndex));
        Publish("caret");
        Speak(_line.DescribeCaret(fieldChanged));
    }

    // ----------------------------------------------------------------- helpers

    private void DoWalkStep(string why)
    {
        string now = _line.WalkStep();
        _trace.Write("tune", why + ": " + now);
        RefreshScreen();
        Publish(why);
    }

    /// <summary>Put the line on the screen and the caret where the model says, without the caret handler reacting.</summary>
    private void RefreshScreen()
    {
        _programmaticCaret = true;
        try
        {
            string text = _line.DisplayText;
            if (!string.Equals(_home.Text, text, StringComparison.Ordinal))
            {
                _home.Text = text;
            }
            if (_home.CaretIndex != _line.CaretIndex)
            {
                int from = _home.CaretIndex;
                _home.CaretIndex = _line.CaretIndex;
                _trace.Write("caret", string.Format(CultureInfo.InvariantCulture,
                    "program moved the caret to {0} character {1} (screen index {2} to {3})",
                    _line.GetField(_line.CaretOrdinal).Label, _line.CaretChar, from, _line.CaretIndex));
            }
        }
        finally
        {
            _programmaticCaret = false;
        }
    }

    /// <summary>Publish the view string on the carrier when it changed, and trace it.</summary>
    private void Publish(string why)
    {
        string view = _line.BuildViewString();
        if (string.Equals(view, _published, StringComparison.Ordinal))
        {
            return;
        }
        _published = view;
        bool listening = _carrier.SetViewText(view);
        _trace.Write("carrier", string.Format(CultureInfo.InvariantCulture,
            "revision {0} ({1}), {2} characters, name-change event {3}: {4}",
            _line.Revision, why, view.Length,
            listening ? "raised" : "not raised, no UI Automation client was listening for property changes",
            view));
    }

    private void Speak(string text)
    {
        var peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent, text, "JJBrailleTrial");
    }

    private static class NativeMethods
    {
        private const uint MsgfltAllow = 1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

        public static bool AllowMessage(IntPtr hwnd, uint message)
            => message != 0 && ChangeWindowMessageFilterEx(hwnd, message, MsgfltAllow, IntPtr.Zero);
    }
}
