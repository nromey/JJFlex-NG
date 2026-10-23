using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using UIMode = JJFlexWpf.MainWindow.UIMode;

namespace JJFlexWpf.Tests;

/// <summary>
/// Actual disconnect/restore methods and actual meters toggle, without a Window,
/// radio, keyboard hook or presentation source. MainWindow's constructor installs
/// global hooks, so use the uninitialized-host precedent in DialogTransmitKeysTests
/// and supply real unhosted controls only. No remembered meters field is set here.
/// QuietRun and TestSettingsRoot must both have succeeded before constructing any
/// control. This tests layout, not physical Tab navigation or screen-reader delivery.
/// </summary>
public sealed class MetersReconnectTests
{
    [Theory]
    [InlineData(false, UIMode.Modern)]
    [InlineData(true, UIMode.Modern)]
    [InlineData(false, UIMode.Classic)]
    [InlineData(true, UIMode.Classic)]
    public void Open_meters_return_silently_by_either_reconnect_route(bool rescue, UIMode mode)
        => Run(h =>
        {
            h.Main.ApplyUIMode(mode);
            h.Toggle(open: true);
            h.Drop();
            if (rescue) h.Rescue();
            h.Reconnect();
            h.AssertMeters(open: true);
            Assert.Equal(Visibility.Visible, h.Fields.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Panadapter.Visibility);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closed_meters_stay_closed(bool rescue)
        => Run(h =>
        {
            h.Toggle(open: true);
            h.Toggle(open: false);
            h.Drop();
            if (rescue) h.Rescue();
            h.Reconnect();
            h.AssertMeters(open: false);
        });

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 2)]
    public void Ctrl_M_while_gone_wins_over_the_pre_disconnect_choice(bool rescue, bool initiallyOpen, int presses)
        => Run(h =>
        {
            if (initiallyOpen) h.Toggle(open: true);
            h.Drop();
            if (rescue) h.Rescue();
            // The real toggle sees a collapsed panel and OPENS it on the first
            // press. The old failed fix called this a cancellation. Two presses
            // close it; closed/drop/one press and open/drop/two discriminate the
            // newer choice from the pre-drop snapshot in the mutation control.
            for (int i = 0; i < presses; i++) h.Toggle(open: i % 2 == 0);
            if (rescue) h.Main.ApplyUIMode(h.Main.ActiveUIMode); // rescue hides it again
            h.Reconnect();
            h.AssertMeters(open: presses % 2 == 1);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_disconnect_and_rescue_collapses_keep_the_original_choice(bool rescue)
        => Run(h =>
        {
            h.Toggle(open: true);
            h.Drop();
            h.Drop();
            if (rescue)
            {
                h.Rescue();
                h.Main.ApplyUIMode(UIMode.Classic);
            }
            h.Reconnect();
            h.AssertMeters(open: true);
        });

    [Fact]
    public void A_mode_change_during_grace_does_not_reopen_the_hidden_meters()
        => Run(h =>
        {
            h.Toggle(open: true);
            h.Drop();
            h.Main.ApplyUIMode(UIMode.Classic);
            Assert.Equal(Visibility.Collapsed, h.Meters.Visibility);
            h.Reconnect();
            h.AssertMeters(open: true);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Each_disconnect_takes_a_fresh_choice_and_fields_preference_still_applies(bool rescue)
        => Run(h =>
        {
            h.Main.FieldsPanelUserVisible = false;
            h.Toggle(open: true);
            h.Drop();
            if (rescue) h.Rescue();
            h.Reconnect();
            h.AssertMeters(open: true);
            Assert.Equal(Visibility.Collapsed, h.Fields.Visibility);
            h.Toggle(open: false);
            h.Drop();
            if (rescue) h.Rescue();
            h.Reconnect();
            h.AssertMeters(open: false);
            Assert.Equal(Visibility.Collapsed, h.Fields.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Panadapter.Visibility);
        });

    [Fact]
    public void A_cold_connect_and_startup_rescue_do_not_invent_an_open_panel()
        => Run(h =>
        {
            h.Reconnect();
            h.AssertMeters(open: false);
            h.Rescue();
            h.Main.ApplyUIMode(UIMode.Classic);
            h.Reconnect();
            h.AssertMeters(open: false);
        });

    [Fact]
    public void A_visible_but_folded_expander_keeps_its_state_without_a_new_expand_earcon()
        => Run(h =>
        {
            h.Toggle(open: true);
            h.Expander.IsExpanded = false; // the operator folds the header, not Ctrl+M
            int expanded = 0;
            h.Expander.Expanded += (_, _) => expanded++;
            h.Drop();
            h.Rescue();
            h.Reconnect();
            Assert.Equal(Visibility.Visible, h.Meters.Visibility);
            Assert.False(h.Expander.IsExpanded);
            Assert.Equal(0, expanded);
        });

    private static void Run(Action<Layout> body)
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var h = new Layout();
                body(h);
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Unhosted layout test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Layout : IDisposable
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public readonly MainWindow Main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        public readonly MetersPanel Meters = new() { Visibility = Visibility.Collapsed };
        public readonly ScreenFieldsPanel Fields = new();
        public readonly Grid Panadapter = new() { Visibility = Visibility.Visible };
        private readonly StackPanel _rescue = new() { Visibility = Visibility.Collapsed };
        private readonly VerbosityLevel _oldVerbosity;
        private int _inputPosts, _focusReturns;
        public Expander Expander => (Expander)Meters.FindName("MetersExpander");

        public Layout()
        {
            Lexicon.Load(Lexicon.Partitions);
            _oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            Put("MetersPanel", Meters);
            Put("FieldsPanel", Fields);
            Put("PanadapterPanel", Panadapter);
            Put("RadioControlsPanel", new StackPanel());
            Put("ContentArea", new Grid());
            Put("LoggingPanel", new Grid());
            Put("RescuePanel", _rescue);
            Put("RescueConnectButton", new Button());
            Put("StatusText", new TextBlock());
            Put("FreqOut", new FrequencyDisplay());
            Put("ReceiveLabel", new TextBlock());
            Put("SendLabel", new TextBlock());
            Put("ReceivedTextBox", new TextBox());
            Put("SentTextBox", new TextBox());
            Main.FieldsPanelUserVisible = true;
            Main.ApplyUIMode(UIMode.Modern);
            Meters.ReturnFocusToFreqOut = () => _focusReturns++;
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted += OnPosted;
            Assert.Null(PresentationSource.FromVisual(Meters));
            Assert.Null(Window.GetWindow(Meters));
            Assert.False(SystemWideKeys.Installed);
        }

        private void OnPosted(object? sender, DispatcherHookEventArgs e)
        {
            if (e.Operation.Priority == DispatcherPriority.Input) _inputPosts++;
        }

        public void Toggle(bool open)
        {
            int inputBefore = _inputPosts, returnBefore = _focusReturns;
            Main.ToggleMetersPanel(); // real panel TogglePanelVisibility, never a memory-field write
            AssertMeters(open);
            Assert.Equal(Lexicon.Get(open ? "audio.meters.panel.opened" : "audio.meters.panel.closed"),
                ScreenReaderOutput.RecentMessages.First());
            Assert.Equal(inputBefore + (open ? 1 : 0), _inputPosts);
            Assert.Equal(returnBefore + (open ? 0 : 1), _focusReturns);
            Drain(); // finish the operator's queued focus request before testing automatic restore
        }

        public void Drop()
        {
            Call("RestoreNoRadioShell");
            Assert.Equal(Visibility.Collapsed, Meters.Visibility);
            Assert.Equal(Visibility.Collapsed, Fields.Visibility);
            Assert.Equal(Visibility.Collapsed, Panadapter.Visibility);
            Assert.True((bool)typeof(MainWindow).GetField("_disconnectShellUp", Any)!.GetValue(Main)!);
            var timer = (DispatcherTimer?)typeof(MainWindow).GetField("_rescueGraceTimer", Any)!.GetValue(Main);
            Assert.NotNull(timer); // proves the catch in RestoreNoRadioShell did not hide a fixture failure
            Assert.True(timer!.IsEnabled);
        }

        public void Rescue()
        {
            // Existing production entry sets rescue state and calls the same visibility
            // method as expiry. No manual _rescueMode or meters-memory assignment.
            Main.EnterRescueModeIfNoRadio();
            Assert.True(Main.InRescueMode);
            Assert.Equal(Visibility.Visible, _rescue.Visibility);
            Assert.Equal(Visibility.Collapsed, Meters.Visibility);
            Assert.Equal(Visibility.Collapsed, Fields.Visibility);
            Assert.Equal(Visibility.Collapsed, Panadapter.Visibility);
        }

        public void Reconnect()
        {
            string marker = "meters reconnect marker " + Guid.NewGuid().ToString("N");
            ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
            Assert.Equal(marker, ScreenReaderOutput.RecentMessages.First());
            int inputBefore = _inputPosts, returnBefore = _focusReturns;
            IInputElement? focusBefore = Keyboard.FocusedElement;
            Main.ExitRescueMode();
            Drain();
            Assert.Equal(marker, ScreenReaderOutput.RecentMessages.First());
            Assert.Equal(inputBefore, _inputPosts);
            Assert.Equal(returnBefore, _focusReturns);
            Assert.Same(focusBefore, Keyboard.FocusedElement);
            Assert.False(Main.InRescueMode);
            Assert.Equal(Visibility.Collapsed, _rescue.Visibility);
            Assert.Null(PresentationSource.FromVisual(Meters));
            Assert.False(SystemWideKeys.Installed);
        }

        public void AssertMeters(bool open)
        {
            Assert.Equal(open ? Visibility.Visible : Visibility.Collapsed, Meters.Visibility);
            Assert.Equal(open, Expander.IsExpanded);
        }

        private void Put(string name, object value) => typeof(MainWindow).GetField(name, Any)!.SetValue(Main, value);
        private void Call(string name) => typeof(MainWindow).GetMethod(name, Any)!.Invoke(Main, null);
        private static void Drain()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        public void Dispose()
        {
            Call("CancelRescueCountdown");
            Meters.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            // ScreenFieldsPanel builds this static subscription even before Loaded.
            NoiseCaptureNarrator.StateChanged -= (Action)typeof(ScreenFieldsPanel)
                .GetMethod("UpdateCaptureButtonLabel", Any)!.CreateDelegate(typeof(Action), Fields);
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= OnPosted;
            ScreenReaderOutput.CurrentVerbosity = _oldVerbosity;
        }
    }
}
