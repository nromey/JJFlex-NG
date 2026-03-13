using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// Sprint 22 Phase 9: Interactive meter tone configuration panel.
/// Lets users configure MeterToneEngine slots: meter source, waveform,
/// pan position, base frequency, per-slot mute, test tone, and removal.
/// Global controls: add slot, auto-enable on tune, speech timer.
/// </summary>
public partial class MetersPanel : UserControl
{
    /// <summary>Fired when user presses Escape — wired to return focus to FreqOut.</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Callback to return focus to the FreqOut control.</summary>
    public Action? ReturnFocusToFreqOut { get; set; }

    // Per-slot UI control references
    private readonly List<SlotControls> _slotUIs = new();

    // Separator that marks the boundary between slot controls and global controls
    private Separator? _globalSeparator;

    private static readonly string[] MeterSourceNames =
    {
        "S-Meter", "ALC", "Mic", "Forward Power", "SWR",
        "Compression", "Voltage", "PA Temp"
    };

    private static readonly string[] WaveformNames =
    {
        "Sine", "Square", "Sawtooth", "Slow Pulse", "Fast Pulse", "Alternating"
    };

    private static readonly string[] PanNames = { "Left", "Center", "Right" };

    public MetersPanel()
    {
        InitializeComponent();
        BuildSlotControls();
        LoadFromEngine();
    }

    /// <summary>
    /// Toggle the meters panel expansion and meter tones in one action.
    /// Called by Ctrl+M from MainWindow.
    /// </summary>
    public void ToggleMeters()
    {
        if (MeterToneEngine.Enabled)
        {
            // Turn off meters
            MeterToneEngine.Enabled = false;
            EarconPlayer.FeatureOffTone();
            ScreenReaderOutput.Speak("Meter tones off");
        }
        else
        {
            // Turn on meters and expand panel
            MeterToneEngine.Enabled = true;
            MetersExpander.IsExpanded = true;

            // Show panel if hidden
            if (Visibility != Visibility.Visible)
                Visibility = Visibility.Visible;

            EarconPlayer.FeatureOnTone();
            ScreenReaderOutput.Speak("Meter tones on");
        }
    }

    #region Build Slot Controls

    private void BuildSlotControls()
    {
        // Find the global separator — it's the first Separator child in MetersContent
        _globalSeparator = null;
        foreach (var child in MetersContent.Children)
        {
            if (child is Separator sep)
            {
                _globalSeparator = sep;
                break;
            }
        }

        // Build UI for existing engine slots
        for (int i = 0; i < MeterToneEngine.Slots.Count; i++)
        {
            AddSlotUI(i);
        }
    }

    private void AddSlotUI(int slotIndex)
    {
        var slot = MeterToneEngine.Slots[slotIndex];

        var group = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 4)
        };
        AutomationProperties.SetName(group,
            $"Meter slot {slotIndex + 1}: {MeterSourceNames[(int)slot.Source]}");

        // Header label
        var header = new TextBlock
        {
            Text = $"Meter Slot {slotIndex + 1}",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 4)
        };
        group.Children.Add(header);

        // Meter source combo
        var sourceCombo = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 2),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(sourceCombo, $"Slot {slotIndex + 1} meter type");
        foreach (var name in MeterSourceNames)
            sourceCombo.Items.Add(name);
        sourceCombo.SelectedIndex = (int)slot.Source;
        int capturedIdx = slotIndex;
        sourceCombo.SelectionChanged += (s, e) =>
        {
            if (capturedIdx < MeterToneEngine.Slots.Count && sourceCombo.SelectedIndex >= 0)
            {
                MeterToneEngine.Slots[capturedIdx].Source = (MeterSource)sourceCombo.SelectedIndex;
                UpdateSlotAutomationName(capturedIdx);
            }
        };
        group.Children.Add(sourceCombo);

        // Waveform combo
        var waveCombo = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 2),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(waveCombo, $"Slot {slotIndex + 1} waveform");
        foreach (var name in WaveformNames)
            waveCombo.Items.Add(name);
        waveCombo.SelectedIndex = (int)slot.Waveform;
        waveCombo.SelectionChanged += (s, e) =>
        {
            if (capturedIdx < MeterToneEngine.Slots.Count && waveCombo.SelectedIndex >= 0)
            {
                var wf = (WaveformType)waveCombo.SelectedIndex;
                MeterToneEngine.Slots[capturedIdx].Waveform = wf;
                MeterToneEngine.Slots[capturedIdx].ToneProvider.Waveform = wf;
            }
        };
        group.Children.Add(waveCombo);

        // Pan combo
        var panCombo = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 2),
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(panCombo, $"Slot {slotIndex + 1} pan position");
        foreach (var name in PanNames)
            panCombo.Items.Add(name);
        // Map float pan to combo index: -1→Left, 0→Center, 1→Right
        panCombo.SelectedIndex = slot.Pan < -0.3f ? 0 : slot.Pan > 0.3f ? 2 : 1;
        panCombo.SelectionChanged += (s, e) =>
        {
            if (capturedIdx < MeterToneEngine.Slots.Count && panCombo.SelectedIndex >= 0)
            {
                float pan = panCombo.SelectedIndex switch { 0 => -1f, 2 => 1f, _ => 0f };
                MeterToneEngine.Slots[capturedIdx].Pan = pan;
            }
        };
        group.Children.Add(panCombo);

        // Base frequency
        var freqPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        freqPanel.Children.Add(new TextBlock
        {
            Text = "Base frequency (Hz):",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var freqBox = new TextBox
        {
            Width = 60,
            Text = slot.PitchLow.ToString(),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(freqBox, $"Slot {slotIndex + 1} base frequency in hertz");
        freqBox.LostFocus += (s, e) =>
        {
            if (capturedIdx < MeterToneEngine.Slots.Count &&
                int.TryParse(freqBox.Text, out int freq))
            {
                freq = Math.Clamp(freq, 100, 2000);
                freqBox.Text = freq.ToString();
                MeterToneEngine.Slots[capturedIdx].PitchLow = freq;
            }
        };
        freqPanel.Children.Add(freqBox);
        group.Children.Add(freqPanel);

        // Enabled checkbox
        var enabledCheck = new CheckBox
        {
            Content = "Enabled",
            IsChecked = slot.Enabled,
            Margin = new Thickness(0, 2, 0, 2)
        };
        AutomationProperties.SetName(enabledCheck, $"Slot {slotIndex + 1} enabled");
        enabledCheck.Checked += (s, e) =>
        {
            if (capturedIdx < MeterToneEngine.Slots.Count)
                MeterToneEngine.Slots[capturedIdx].Enabled = true;
        };
        enabledCheck.Unchecked += (s, e) =>
        {
            if (capturedIdx < MeterToneEngine.Slots.Count)
            {
                MeterToneEngine.Slots[capturedIdx].Enabled = false;
                MeterToneEngine.Slots[capturedIdx].ToneProvider.Active = false;
            }
        };
        group.Children.Add(enabledCheck);

        // Button row: Test and Remove
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var testBtn = new Button
        {
            Content = "Test",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(testBtn, $"Test slot {slotIndex + 1} tone");
        testBtn.Click += (s, e) => TestSlot(capturedIdx);
        btnPanel.Children.Add(testBtn);

        var removeBtn = new Button
        {
            Content = "Remove",
            Padding = new Thickness(8, 2, 8, 2)
        };
        AutomationProperties.SetName(removeBtn, $"Remove meter slot {slotIndex + 1}");
        removeBtn.Click += (s, e) => RemoveSlotAt(capturedIdx);
        btnPanel.Children.Add(removeBtn);

        group.Children.Add(btnPanel);

        // Insert before the global separator
        int insertIndex = _globalSeparator != null
            ? MetersContent.Children.IndexOf(_globalSeparator)
            : MetersContent.Children.Count;
        MetersContent.Children.Insert(insertIndex, group);

        _slotUIs.Add(new SlotControls
        {
            Panel = group,
            SourceCombo = sourceCombo,
            WaveCombo = waveCombo,
            PanCombo = panCombo,
            FreqBox = freqBox,
            EnabledCheck = enabledCheck,
            RemoveButton = removeBtn
        });

        UpdateRemoveButtonStates();
    }

    private void UpdateSlotAutomationName(int index)
    {
        if (index < _slotUIs.Count && index < MeterToneEngine.Slots.Count)
        {
            var slot = MeterToneEngine.Slots[index];
            AutomationProperties.SetName(_slotUIs[index].Panel,
                $"Meter slot {index + 1}: {MeterSourceNames[(int)slot.Source]}");
        }
    }

    private void UpdateRemoveButtonStates()
    {
        bool canRemove = _slotUIs.Count > 1;
        foreach (var ui in _slotUIs)
            ui.RemoveButton.IsEnabled = canRemove;

        AddSlotButton.IsEnabled = _slotUIs.Count < MeterToneEngine.MaxSlots;
    }

    #endregion

    #region Load/Save

    private void LoadFromEngine()
    {
        AutoTuneCheck.IsChecked = MeterToneEngine.AutoEnableOnTune;
        SpeechTimerCheck.IsChecked = MeterToneEngine.SpeechTimerActive;
        SpeechIntervalBox.Text = MeterToneEngine.SpeechIntervalSeconds.ToString();
        PeakWatcherCheck.IsChecked = MeterToneEngine.PeakWatcherEnabled;

        AutoTuneCheck.Checked += (s, e) => MeterToneEngine.AutoEnableOnTune = true;
        AutoTuneCheck.Unchecked += (s, e) => MeterToneEngine.AutoEnableOnTune = false;

        SpeechTimerCheck.Checked += (s, e) => MeterToneEngine.SpeechTimerActive = true;
        SpeechTimerCheck.Unchecked += (s, e) => MeterToneEngine.SpeechTimerActive = false;

        PeakWatcherCheck.Checked += (s, e) => MeterToneEngine.PeakWatcherEnabled = true;
        PeakWatcherCheck.Unchecked += (s, e) => MeterToneEngine.PeakWatcherEnabled = false;
    }

    /// <summary>
    /// Save current panel state to the AudioOutputConfig.
    /// Called when persisting settings.
    /// </summary>
    public void SaveToConfig(AudioOutputConfig config)
    {
        config.AutoEnableOnTune = MeterToneEngine.AutoEnableOnTune;
        config.MeterSpeechTimerActive = MeterToneEngine.SpeechTimerActive;
        config.MeterSpeechIntervalSeconds = MeterToneEngine.SpeechIntervalSeconds;
        config.PeakWatcherEnabled = MeterToneEngine.PeakWatcherEnabled;
        config.MeterTonesEnabled = MeterToneEngine.Enabled;
    }

    #endregion

    #region Event Handlers

    private void AddSlotButton_Click(object sender, RoutedEventArgs e)
    {
        var slot = MeterToneEngine.AddSlot();
        if (slot == null)
        {
            ScreenReaderOutput.Speak("Maximum meter slots reached");
            return;
        }
        AddSlotUI(MeterToneEngine.Slots.Count - 1);
        ScreenReaderOutput.Speak($"Meter slot {MeterToneEngine.Slots.Count} added");
    }

    private void RemoveSlotAt(int index)
    {
        if (!MeterToneEngine.RemoveSlot(index))
        {
            ScreenReaderOutput.Speak("Cannot remove the only meter slot");
            return;
        }

        // Remove UI
        if (index < _slotUIs.Count)
        {
            MetersContent.Children.Remove(_slotUIs[index].Panel);
            _slotUIs.RemoveAt(index);
        }

        // Renumber remaining slot headers
        for (int i = 0; i < _slotUIs.Count; i++)
        {
            var header = _slotUIs[i].Panel.Children[0] as TextBlock;
            if (header != null) header.Text = $"Meter Slot {i + 1}";
        }

        UpdateRemoveButtonStates();
        ScreenReaderOutput.Speak($"Slot removed, {MeterToneEngine.Slots.Count} slots remaining");
    }

    private void TestSlot(int index)
    {
        if (index >= MeterToneEngine.Slots.Count) return;
        var slot = MeterToneEngine.Slots[index];
        string sourceName = MeterSourceNames[(int)slot.Source];
        ScreenReaderOutput.Speak($"Testing {sourceName} tone");

        // Play a 2-second preview at mid-range
        slot.ToneProvider.Frequency = (slot.PitchLow + slot.PitchHigh) / 2f;
        slot.ToneProvider.Volume = slot.Volume * MeterToneEngine.MasterVolume;
        slot.ToneProvider.Waveform = slot.Waveform;
        slot.ToneProvider.Active = true;

        // Stop after 2 seconds
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            // Only deactivate if meters aren't globally enabled
            if (!MeterToneEngine.Enabled)
                slot.ToneProvider.Active = false;
        };
        timer.Start();
    }

    private void SpeechIntervalBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SpeechIntervalBox.Text, out int val))
        {
            val = Math.Clamp(val, 1, 10);
            SpeechIntervalBox.Text = val.ToString();
            MeterToneEngine.SpeechIntervalSeconds = val;
            MeterToneEngine.UpdateSpeechTimerInterval();
        }
    }

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    #endregion

    /// <summary>Track UI controls for a single slot.</summary>
    private class SlotControls
    {
        public StackPanel Panel { get; init; } = null!;
        public ComboBox SourceCombo { get; init; } = null!;
        public ComboBox WaveCombo { get; init; } = null!;
        public ComboBox PanCombo { get; init; } = null!;
        public TextBox FreqBox { get; init; } = null!;
        public CheckBox EnabledCheck { get; init; } = null!;
        public Button RemoveButton { get; init; } = null!;
    }
}
