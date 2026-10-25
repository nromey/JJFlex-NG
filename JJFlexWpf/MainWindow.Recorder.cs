using System;
using System.Windows;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf;

public partial class MainWindow
{
    private DispatcherTimer? _disconnectedRecorderPoll;

    private void ShowDisconnectedRecorderStop()
    {
        RefreshDisconnectedRecorderStop();
        if (!ComputerRecorder.Current.IsRecording) return;
        _disconnectedRecorderPoll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background, (_, _) => RefreshDisconnectedRecorderStop(), Dispatcher);
        _disconnectedRecorderPoll.Start();
    }

    private void RefreshDisconnectedRecorderStop()
    {
        // Layout-only tests construct MainWindow without InitializeComponent.
        if (DisconnectedRecorderStop == null) return;
        if (!ComputerRecorder.Current.IsRecording) { HideDisconnectedRecorderStop(); return; }
        string label = Lexicon.Get("audio.recorder.stop_record_to_file_button");
        DisconnectedRecorderStop.Content = label;
        System.Windows.Automation.AutomationProperties.SetName(DisconnectedRecorderStop, label);
        DisconnectedRecorderStop.Visibility = Visibility.Visible;
    }

    private void HideDisconnectedRecorderStop()
    {
        _disconnectedRecorderPoll?.Stop();
        if (DisconnectedRecorderStop == null) return;
        bool hadFocus = DisconnectedRecorderStop.IsKeyboardFocusWithin;
        DisconnectedRecorderStop.Visibility = Visibility.Collapsed;
        if (hadFocus) FocusHome();
    }

    private async void DisconnectedRecorderStop_Click(object sender, RoutedEventArgs e)
    {
        // This is only a stop door: a stale click must never start a new take.
        if (ComputerRecorder.Current.IsRecording)
            await RecorderControls.ToggleComputerRecordAsync(null);
        RefreshDisconnectedRecorderStop();
    }
}
