using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Audio → Countdown: the one countdown (#835, fix round
    /// 2026-10-28), for Check your audio and the Fix tool alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One group, ruled by Noel 21:40</b> ("yep agreed with all of it"):
    /// on or off; the length, 2 to 10 seconds; the pip's pitch, presets plus
    /// Custom the way Check your audio's test tone offers them, 200 to 2000
    /// hertz; the pip's length; the lead-in; and a Play sample button so a
    /// choice is heard before it is kept.
    /// </para>
    /// <para>
    /// <b>Where it sits is Claude's choice, not a ruling:</b> the Audio tab,
    /// after Recorder. Check your audio lives under Audio, and the countdown is
    /// a sound and a spoken count; F6 reaches it as its own section.
    /// </para>
    /// <para>
    /// <b>Only the on-off switch is Check your audio's alone.</b> The Fix tool
    /// always counts down before its tests (its count is the warning before a
    /// stage that keys with nobody touching anything), but at the length,
    /// pitch and pip length set here. So every control but the switch does
    /// something whether or not the switch is on, and none of them leaves the
    /// tab order when it is off.
    /// </para>
    /// <para>
    /// Committed on OK or Apply like its neighbours, through
    /// <c>AudioOutputConfig.Countdown</c>. Play sample plays what is on screen,
    /// uncommitted, which is the point of it.
    /// </para>
    /// </remarks>
    public partial class SettingsDialog
    {
        private CheckBox? _countdownEnabledCheck;
        private ValueFieldControl? _countdownSecondsControl;
        private CycleFieldControl? _countdownPitchControl;
        private ValueFieldControl? _countdownCustomPitchControl;
        private ValueFieldControl? _countdownPipMsControl;
        private CheckBox? _countdownLeadInCheck;
        private Button? _countdownSampleButton;
        private CountdownRun? _countdownSample;

        /// <summary>Build the Countdown group from the saved settings. Called once, from the constructor.</summary>
        private void InitializeCountdown()
        {
            // DRAFT PROSE (#835): awaiting Noel. Every label and Ctrl+F1 text
            // here is in Radios/Lexicon/audio.json under audio.countdown.
            string header = Lexicon.Get("audio.countdown.group");
            CountdownGroup.Header = header;
            AutomationProperties.SetName(CountdownGroup, header);

            var saved = (_audioConfig.Countdown ?? new CountdownSettings()).Clamped();

            _countdownEnabledCheck = new CheckBox
            {
                Content = Lexicon.Get("audio.countdown.enabled_label"),
                IsChecked = saved.Enabled,
                Margin = new Thickness(0, 0, 0, 8),
            };
            AutomationProperties.SetName(_countdownEnabledCheck, Lexicon.Get("audio.countdown.enabled_label"));
            JJFlexHelp.SetText(_countdownEnabledCheck, Lexicon.Get("audio.countdown.enabled_help"));
            CountdownPanel.Children.Add(_countdownEnabledCheck);

            _countdownSecondsControl = new ValueFieldControl { Margin = new Thickness(0, 0, 0, 8) };
            _countdownSecondsControl.Setup(Lexicon.Get("audio.countdown.seconds_label"),
                CountdownSettings.MinSeconds, CountdownSettings.MaxSeconds, 1, saved.Seconds, 0, "seconds");
            JJFlexHelp.SetText(_countdownSecondsControl, Lexicon.Get("audio.countdown.seconds_help"));
            CountdownPanel.Children.Add(_countdownSecondsControl);

            int presetIndex = IndexOfPreset(saved.PipHz);
            _countdownPitchControl = new CycleFieldControl { Margin = new Thickness(0, 0, 0, 8) };
            _countdownPitchControl.Setup(Lexicon.Get("audio.countdown.pitch_label"), PitchChoices(), presetIndex);
            JJFlexHelp.SetText(_countdownPitchControl, Lexicon.Get("audio.countdown.pitch_help"));
            _countdownPitchControl.SelectionChanged += (s, idx) => UpdateCountdownCustomPitchVisibility();
            CountdownPanel.Children.Add(_countdownPitchControl);

            _countdownCustomPitchControl = new ValueFieldControl { Margin = new Thickness(0, 0, 0, 8) };
            _countdownCustomPitchControl.Setup(Lexicon.Get("audio.countdown.custom_pitch_label"),
                CountdownSettings.MinPipHz, CountdownSettings.MaxPipHz, 10, saved.PipHz, 0, "hertz");
            JJFlexHelp.SetText(_countdownCustomPitchControl, Lexicon.Get("audio.countdown.pitch_help"));
            CountdownPanel.Children.Add(_countdownCustomPitchControl);

            _countdownPipMsControl = new ValueFieldControl { Margin = new Thickness(0, 0, 0, 8) };
            _countdownPipMsControl.Setup(Lexicon.Get("audio.countdown.pip_ms_label"),
                CountdownSettings.MinPipMs, CountdownSettings.MaxPipMs, 25, saved.PipMs, 0, "milliseconds");
            JJFlexHelp.SetText(_countdownPipMsControl, Lexicon.Get("audio.countdown.pip_ms_help"));
            CountdownPanel.Children.Add(_countdownPipMsControl);

            _countdownLeadInCheck = new CheckBox
            {
                Content = Lexicon.Get("audio.countdown.lead_in_label"),
                IsChecked = saved.LeadIn,
                Margin = new Thickness(0, 0, 0, 8),
            };
            AutomationProperties.SetName(_countdownLeadInCheck, Lexicon.Get("audio.countdown.lead_in_label"));
            JJFlexHelp.SetText(_countdownLeadInCheck, Lexicon.Get("audio.countdown.lead_in_help"));
            CountdownPanel.Children.Add(_countdownLeadInCheck);

            _countdownSampleButton = new Button
            {
                Content = Lexicon.Get("audio.countdown.sample_label"),
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
            };
            AutomationProperties.SetName(_countdownSampleButton, Lexicon.Get("audio.countdown.sample_label"));
            JJFlexHelp.SetText(_countdownSampleButton, Lexicon.Get("audio.countdown.sample_help"));
            _countdownSampleButton.Click += (s, e) => PlayCountdownSample();
            CountdownPanel.Children.Add(_countdownSampleButton);

            UpdateCountdownCustomPitchVisibility();

            // A sample still counting when the dialog goes must not count on
            // over whatever the operator does next.
            Closed += (s, e) => StopCountdownSample();
        }

        /// <summary>The pitch choices: each preset, the default marked, then Custom.</summary>
        private static string[] PitchChoices() =>
            CountdownSettings.PipHzPresets
                .Select(hz => hz == CountdownSettings.DefaultPipHz
                    ? Lexicon.Get("audio.countdown.pitch_preset_default", ("hz", hz))
                    : Lexicon.Get("audio.countdown.pitch_preset", ("hz", hz)))
                .Append(Lexicon.Get("audio.countdown.pitch_custom"))
                .ToArray();

        /// <summary>The preset's index for a pitch, or Custom's when it is not a preset.</summary>
        private static int IndexOfPreset(int hz)
        {
            for (int i = 0; i < CountdownSettings.PipHzPresets.Count; i++)
                if (CountdownSettings.PipHzPresets[i] == hz) return i;
            return CountdownSettings.PipHzPresets.Count;
        }

        private bool CountdownCustomChosen =>
            (_countdownPitchControl?.SelectedIndex ?? 0) >= CountdownSettings.PipHzPresets.Count;

        /// <summary>
        /// The custom pitch field is in the tab order only while Custom is
        /// chosen: a control that does nothing comes out of the ring, the same
        /// rule Check your audio's test tone keeps for its custom frequency.
        /// </summary>
        private void UpdateCountdownCustomPitchVisibility()
        {
            if (_countdownCustomPitchControl == null) return;
            _countdownCustomPitchControl.Visibility = CountdownCustomChosen ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The settings as they stand on screen, clamped.</summary>
        private CountdownSettings ReadCountdownFromUi()
        {
            int presetIndex = _countdownPitchControl?.SelectedIndex ?? 0;
            int hz = CountdownCustomChosen
                ? _countdownCustomPitchControl?.Value ?? CountdownSettings.DefaultPipHz
                : CountdownSettings.PipHzPresets[Math.Clamp(presetIndex, 0, CountdownSettings.PipHzPresets.Count - 1)];
            return new CountdownSettings
            {
                Enabled = _countdownEnabledCheck?.IsChecked == true,
                Seconds = _countdownSecondsControl?.Value ?? CountdownSettings.DefaultSeconds,
                PipHz = hz,
                PipMs = _countdownPipMsControl?.Value ?? CountdownSettings.DefaultPipMs,
                LeadIn = _countdownLeadInCheck?.IsChecked == true,
            }.Clamped();
        }

        /// <summary>Commit the group into the audio config. Called from ApplyAllSettings, before Apply.</summary>
        private void CommitCountdown() => _audioConfig.Countdown = ReadCountdownFromUi();

        /// <summary>
        /// Play the countdown as it is set on screen, the way a check would play
        /// it with the operator's sounds and verbosity as they are now. Played
        /// whether or not the switch is on, so a choice can be heard before the
        /// countdown is turned on. Nothing keys. A second press starts it over.
        /// </summary>
        private void PlayCountdownSample()
        {
            StopCountdownSample();
            var plan = CheckCountdown.SamplePlan(ReadCountdownFromUi(),
                EarconPlayer.IsOn(EarconPlayer.EarconCategory.Transmit),
                ScreenReaderOutput.CurrentVerbosity);
            _countdownSample = CountdownRun.Start(plan, FixerCountdown.Voice.Instance, "the Countdown sample");
            // No setup to wait for: the sample's long tone falls on its beat.
            _ = _countdownSample.ReadyToKeyAsync();
        }

        private void StopCountdownSample()
        {
            _countdownSample?.Cancel();
            _countdownSample = null;
        }
    }
}
