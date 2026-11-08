using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The meters panel's four panel-wide controls (auto-enable on tune, speak
/// periodically, the speech interval and transmit level alerts) show the
/// engine as it is now, and leaving one untouched writes nothing.
/// </summary>
/// <remarks>
/// <para>The defect: the panel is built with Home at start, before any
/// connect, and the four controls were filled from the engine once, in its
/// constructor. The saved values reach the engine later, when the config is
/// applied at power-on. So after a restart the interval box showed 3 while the
/// engine held the saved 5, and Tabbing past the box wrote 3 into the engine
/// and, since the panel saves on every change, onto disk.</para>
/// <para><b>Windowless.</b> A real <see cref="MetersPanel"/> is constructed,
/// as <c>MeterCategoryPickerTests</c> and <c>MetersReconnectTests</c> do, and
/// never put in a window: an unhosted UserControl has no presentation source
/// and cannot be shown (asserted in every test). The thread is STA because WPF
/// elements require it. Refuses unless <c>QuietRun</c> and
/// <c>TestSettingsRoot</c> have both engaged. The save is counted through the
/// panel's live hooks, so nothing is written anywhere.</para>
/// <para><b>What this cannot see:</b> real keyboard focus moving off the box,
/// which is simulated by raising LostFocus on it; and the screen reader's
/// reading of a checkbox whose state changed under it.</para>
/// </remarks>
public sealed class MetersPanelGlobalsTests
{
    [Fact]
    public void After_a_restart_Tabbing_past_the_interval_box_keeps_the_saved_interval()
        => Run((panel, saves) =>
        {
            // App start: the panel is built while the engine holds its defaults.
            Assert.Equal("3", Box(panel).Text);

            // Connect: the saved settings reach the engine. Not pumped, so the
            // panel learns of them only by being shown, the B1 sequence.
            ApplySaved(interval: 5, peakWatcher: false, autoTune: true);

            panel.ShowPanel();
            Assert.Equal("5", Box(panel).Text);
            Assert.False(Check(panel, "PeakWatcherCheck").IsChecked);
            Assert.True(Check(panel, "AutoTuneCheck").IsChecked);

            TabPast(Box(panel));

            Assert.Equal(5, MeterToneEngine.SpeechIntervalSeconds);
            Assert.Equal("5", Box(panel).Text);
            Assert.Equal(0, saves());
            Assert.False(MeterToneEngine.PeakWatcherEnabled);
            Assert.True(MeterToneEngine.AutoEnableOnTune);

            Pump();
            Assert.Equal(0, saves());
        });

    [Fact]
    public void An_open_panel_follows_the_engine_when_the_saved_settings_arrive()
        => Run((panel, saves) =>
        {
            Assert.Equal("3", Box(panel).Text);
            Assert.True(Check(panel, "PeakWatcherCheck").IsChecked);

            // A config applied, or Peak Watcher switched from the Home field
            // panel or the Meter Tones menu, while this panel is up.
            ApplySaved(interval: 8, peakWatcher: false, autoTune: true);
            Pump();

            Assert.Equal("8", Box(panel).Text);
            Assert.False(Check(panel, "PeakWatcherCheck").IsChecked);
            Assert.True(Check(panel, "AutoTuneCheck").IsChecked);
            Assert.Equal(0, saves());

            TabPast(Box(panel));
            Assert.Equal(8, MeterToneEngine.SpeechIntervalSeconds);
            Assert.Equal(0, saves());
        });

    [Fact]
    public void A_changed_interval_is_saved_once_and_garbage_is_put_back()
        => Run((panel, saves) =>
        {
            // Positive control: the handler CAN save, so the zero counts above
            // are the guard and not a dead route.
            panel.ShowPanel();
            Box(panel).Text = "7";
            TabPast(Box(panel));
            Assert.Equal(7, MeterToneEngine.SpeechIntervalSeconds);
            Assert.Equal(1, saves());

            Box(panel).Text = "seven";
            TabPast(Box(panel));
            Assert.Equal(7, MeterToneEngine.SpeechIntervalSeconds);
            Assert.Equal("7", Box(panel).Text);
            Assert.Equal(1, saves());
        });

    [Theory]
    [InlineData("3", 3, null)]
    [InlineData("5", 3, 5)]
    [InlineData("12", 3, 10)]
    [InlineData("0", 3, 1)]
    [InlineData("10", 10, null)]
    [InlineData("abc", 3, null)]
    [InlineData("", 3, null)]
    public void Leaving_the_box_writes_only_a_changed_number(string text, int current, int? expected)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(expected, MetersPanel.IntervalToSave(text, current));
    }

    private static void ApplySaved(int interval, bool peakWatcher, bool autoTune)
    {
        // As AudioOutputConfig.Apply does it: a restore, not an operator toggle.
        MeterToneEngine.QuietStateRestore = true;
        try
        {
            MeterToneEngine.PeakWatcherEnabled = peakWatcher;
            MeterToneEngine.SpeechIntervalSeconds = interval;
            MeterToneEngine.AutoEnableOnTune = autoTune;
        }
        finally
        {
            MeterToneEngine.QuietStateRestore = false;
        }
    }

    private static TextBox Box(MetersPanel panel) => (TextBox)panel.FindName("SpeechIntervalBox");

    private static CheckBox Check(MetersPanel panel, string name) => (CheckBox)panel.FindName(name);

    private static void TabPast(UIElement element) =>
        element.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, element));

    /// <summary>Run everything the dispatcher has queued, down to Background.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Run(Action<MetersPanel, Func<int>> body)
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            int oldInterval = MeterToneEngine.SpeechIntervalSeconds;
            bool oldPeak = MeterToneEngine.PeakWatcherEnabled;
            bool oldAuto = MeterToneEngine.AutoEnableOnTune;
            bool oldTimer = MeterToneEngine.SpeechTimerActive;
            var oldSource = MetersPanel.AudioConfigSource;
            var oldSave = MetersPanel.AudioConfigSave;
            bool oldLoaded = AudioOutputConfig.EngineHoldsSavedConfig;
            MetersPanel? panel = null;
            try
            {
                Lexicon.Load(Lexicon.Partitions);
                // The engine as it stands at app start.
                MeterToneEngine.QuietStateRestore = true;
                MeterToneEngine.SpeechTimerActive = false;
                MeterToneEngine.QuietStateRestore = false;
                ApplySaved(interval: 3, peakWatcher: true, autoTune: false);

                AudioOutputConfig.EngineHoldsSavedConfig = true;   // a connect has applied the saved config
                var live = new AudioOutputConfig();
                int saves = 0;
                MetersPanel.AudioConfigSource = () => live;
                MetersPanel.AudioConfigSave = () => saves++;

                panel = new MetersPanel { Visibility = Visibility.Collapsed };
                Assert.Null(PresentationSource.FromVisual(panel));
                Assert.Null(Window.GetWindow(panel));

                body(panel, () => saves);

                Assert.Null(PresentationSource.FromVisual(panel));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (panel != null)
                    typeof(MetersPanel).GetMethod("UnsubscribeFromEngine",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(panel, null);
                MetersPanel.AudioConfigSource = oldSource;
                MetersPanel.AudioConfigSave = oldSave;
                AudioOutputConfig.EngineHoldsSavedConfig = oldLoaded;
                MeterToneEngine.QuietStateRestore = true;
                MeterToneEngine.SpeechIntervalSeconds = oldInterval;
                MeterToneEngine.PeakWatcherEnabled = oldPeak;
                MeterToneEngine.AutoEnableOnTune = oldAuto;
                MeterToneEngine.SpeechTimerActive = oldTimer;
                MeterToneEngine.QuietStateRestore = false;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The panel test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
