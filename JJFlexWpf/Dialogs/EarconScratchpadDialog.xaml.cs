using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The audio bench: a place to make a sound, hold it still, and decide
    /// whether it is any good.
    ///
    /// The half above the Voices heading is the original scratchpad — raw
    /// sines, sweeps and the sampled filter noises. The half below plays named
    /// voices through <see cref="VoicedToneSampleProvider"/>, the same engine
    /// the meters and (since Sprint 32 Track E) the earcons use, so a tone
    /// auditioned here is a tone that can ship without being rebuilt.
    ///
    /// The three things this gained in Sprint 32 all answer the same problem:
    /// a one-shot earcon cannot be judged. Hold keeps a note sounding while the
    /// sliders move, so it can be compared against real band noise rather than
    /// remembered. Scale walk plays the voice across its working range, because
    /// a timbre that reads at 800 Hz can vanish at 300. Harmonic series plays
    /// the partials one at a time, which is how a partial list stops being a
    /// row of numbers.
    ///
    /// Voices are imported, never authored here. Noel's ruling, and the reason
    /// is on the Import button.
    /// </summary>
    public partial class EarconScratchpadDialog : JJFlexDialog
    {
        private bool _updating;

        /// <summary>
        /// The voices offered in the picker, parallel to the combo's items.
        /// Rebuilt after an import so a freshly loaded voice is selectable
        /// without reopening the dialog.
        /// </summary>
        private readonly List<MeterVoice> _voices = new();

        public EarconScratchpadDialog()
        {
            InitializeComponent();
            ResizeMode = System.Windows.ResizeMode.CanResize;
            PopulateVoices();
            Closed += (s, e) => EarconPlayer.StopBenchTone();
        }

        // ------------------------------------------------------------------
        // Voice picker
        // ------------------------------------------------------------------

        /// <summary>
        /// Fill the picker: the built-in meter voices, then the alert voices
        /// the earcons themselves use, then anything imported.
        ///
        /// The alert voices are listed even though they are not in
        /// MeterVoiceLibrary.BuiltIns — that list is the operator-facing meter
        /// alphabet and stays sized for a picker, but on a bench you want to
        /// hear what the earcons are actually made of.
        /// </summary>
        private void PopulateVoices()
        {
            string? previous = VoiceCombo.SelectedItem as string;

            _voices.Clear();
            VoiceCombo.Items.Clear();

            foreach (var v in MeterVoiceLibrary.BuiltIns)
            {
                _voices.Add(v);
                VoiceCombo.Items.Add(Describe(v, "meter voice"));
            }

            foreach (var v in AlertVoices())
            {
                _voices.Add(v);
                VoiceCombo.Items.Add(Describe(v, "earcon voice"));
            }

            foreach (var v in MeterVoiceLibrary.GetUserVoices())
            {
                _voices.Add(v);
                VoiceCombo.Items.Add(Describe(v, "imported"));
            }

            int index = previous != null ? VoiceCombo.Items.IndexOf(previous) : -1;
            VoiceCombo.SelectedIndex = index >= 0 ? index : 0;
        }

        private static IEnumerable<MeterVoice> AlertVoices() => new[]
        {
            EarconVoices.Plain,
            EarconVoices.Press,
            EarconVoices.Chime,
            EarconVoices.Alarm,
            EarconVoices.WarningCalm,
            EarconVoices.WarningInsistent,
            EarconVoices.WarningUrgent,
        };

        /// <summary>
        /// One picker row. Name, then what the voice sounds like, then where it
        /// came from — a screen reader reads the row straight through, so the
        /// useful half has to come before the bookkeeping half.
        /// </summary>
        private static string Describe(MeterVoice v, string origin) =>
            string.IsNullOrWhiteSpace(v.Description)
                ? $"{v.Name} ({origin})"
                : $"{v.Name} — {v.Description} ({origin})";

        private MeterVoice? SelectedVoice
        {
            get
            {
                int i = VoiceCombo.SelectedIndex;
                return i >= 0 && i < _voices.Count ? _voices[i] : null;
            }
        }

        // ------------------------------------------------------------------
        // Slider and box mirroring (unchanged behaviour)
        // ------------------------------------------------------------------

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating) return;
            _updating = true;
            try
            {
                if (sender == StartFreqSlider && StartFreqBox != null)
                    StartFreqBox.Text = ((int)StartFreqSlider.Value).ToString();
                else if (sender == EndFreqSlider && EndFreqBox != null)
                    EndFreqBox.Text = ((int)EndFreqSlider.Value).ToString();
                else if (sender == DurationSlider && DurationBox != null)
                    DurationBox.Text = ((int)DurationSlider.Value).ToString();
                else if (sender == VolumeSlider && VolumeBox != null)
                    VolumeBox.Text = ((int)VolumeSlider.Value).ToString();
                else if (sender == PanSlider && PanBox != null)
                    PanBox.Text = ((int)PanSlider.Value).ToString();
            }
            finally { _updating = false; }

            RefreshHeldTone();
        }

        private void FreqBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            _updating = true;
            try
            {
                if (sender == StartFreqBox && int.TryParse(StartFreqBox.Text, out int sf))
                    StartFreqSlider.Value = sf;
                else if (sender == EndFreqBox && int.TryParse(EndFreqBox.Text, out int ef))
                    EndFreqSlider.Value = ef;
                else if (sender == DurationBox && int.TryParse(DurationBox.Text, out int d))
                    DurationSlider.Value = d;
                else if (sender == VolumeBox && int.TryParse(VolumeBox.Text, out int v))
                    VolumeSlider.Value = v;
                else if (sender == PanBox && int.TryParse(PanBox.Text, out int p))
                    PanSlider.Value = p;
            }
            finally { _updating = false; }

            RefreshHeldTone();
        }

        private (int startHz, int endHz, int durationMs, float volume, float pan) GetParams()
        {
            int startHz = int.TryParse(StartFreqBox.Text, out int s) ? s : 800;
            int endHz = int.TryParse(EndFreqBox.Text, out int en) ? en : 800;
            int durationMs = int.TryParse(DurationBox.Text, out int d) ? d : 200;
            float volume = int.TryParse(VolumeBox.Text, out int v) ? v / 100f : 0.6f;
            float pan = int.TryParse(PanBox.Text, out int p) ? p / 100f : 0f;
            return (startHz, endHz, durationMs, volume, pan);
        }

        private bool Decay => DecayCheck?.IsChecked == true;

        // ------------------------------------------------------------------
        // The held tone
        // ------------------------------------------------------------------

        private void HoldCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (HoldCheck.IsChecked == true)
            {
                var (startHz, _, _, volume, pan) = GetParams();
                var provider = EarconPlayer.StartBenchTone(SelectedVoice, startHz, volume, pan);
                Say(provider != null
                    ? $"Holding {VoiceName()} at {startHz} hertz. Move the sliders and it follows."
                    : "Could not start the tone. Earcons may be switched off.");
                if (provider == null) HoldCheck.IsChecked = false;
            }
            else
            {
                EarconPlayer.StopBenchTone();
                Say("Stopped holding.");
            }
        }

        /// <summary>
        /// Push the current slider values at a tone that is already sounding.
        /// Silent when nothing is held, which is why every slider handler can
        /// call it unconditionally.
        /// </summary>
        private void RefreshHeldTone()
        {
            if (HoldCheck == null || HoldCheck.IsChecked != true) return;
            if (!EarconPlayer.IsBenchToneRunning) return;
            var (startHz, _, _, volume, pan) = GetParams();
            EarconPlayer.StartBenchTone(SelectedVoice, startHz, volume, pan);
        }

        private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshHeldTone();
        }

        private string VoiceName() => SelectedVoice?.Name ?? "the default voice";

        // ------------------------------------------------------------------
        // Voiced playback
        // ------------------------------------------------------------------

        private void PlayVoicedNote_Click(object sender, RoutedEventArgs e)
        {
            var (startHz, _, durationMs, volume, pan) = GetParams();
            EarconPlayer.PlayScratchpadVoiced(SelectedVoice, startHz, durationMs, volume, pan, Decay);
            Say($"{VoiceName()}: {startHz} hertz, {durationMs} milliseconds, "
              + $"{(Decay ? "decaying" : "held level")}.");
        }

        private void ScaleWalk_Click(object sender, RoutedEventArgs e)
        {
            var (startHz, endHz, durationMs, volume, pan) = GetParams();
            if (startHz == endHz)
            {
                Say("Scale walk needs a different end frequency from the start frequency.");
                return;
            }
            EarconPlayer.PlayScratchpadScale(SelectedVoice, startHz, endHz, durationMs, volume, pan, Decay);
            Say($"{VoiceName()}: walking from {startHz} to {endHz} hertz in semitones.");
        }

        private void HarmonicSeries_Click(object sender, RoutedEventArgs e)
        {
            var (startHz, _, durationMs, volume, pan) = GetParams();
            int count = int.TryParse(HarmonicCountBox.Text, out int c) ? Math.Clamp(c, 1, 16) : 8;
            EarconPlayer.PlayScratchpadHarmonics(SelectedVoice, startHz, count, durationMs, volume, pan, Decay);
            Say($"{VoiceName()}: harmonics of {startHz} hertz, {count} steps, "
              + "stopping at 5 kilohertz.");
        }

        // ------------------------------------------------------------------
        // Import
        // ------------------------------------------------------------------

        private void ImportVoices_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import voices",
                Filter = "Voice packs (*.xml)|*.xml|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(this) != true) return;

            var result = MeterVoicePack.Import(ofd.FileName);
            if (result.AnyImported)
            {
                PopulateVoices();
                Persist(result);
            }
            Say(result.Summary());
        }

        /// <summary>
        /// Write the imported voices into the audio config so they survive a
        /// restart. The config object owns UserVoices, and Save serialises
        /// whatever is on the object — it does not go and ask the voice library
        /// — so the list has to be copied across first. An import that vanishes
        /// on the next launch would be worse than no import at all.
        /// </summary>
        private static void Persist(MeterVoicePack.ImportResult result)
        {
            var cfg = AudioWorkshopDialog.AudioConfigSource?.Invoke();
            if (cfg == null)
            {
                result.Skipped.Add("nothing was saved, so these voices last until you close the program");
                return;
            }
            cfg.UserVoices = MeterVoiceLibrary.GetUserVoices();
            AudioWorkshopDialog.AudioConfigSave?.Invoke();
        }

        // ------------------------------------------------------------------
        // The original raw-primitive half
        // ------------------------------------------------------------------

        private void PlayTone_Click(object sender, RoutedEventArgs e)
        {
            var (startHz, _, durationMs, volume, pan) = GetParams();
            EarconPlayer.PlayScratchpadTone(startHz, durationMs, volume, pan);
            Say($"Plain sine: {startHz}Hz {durationMs}ms vol={volume:P0} pan={pan:+0.0;-0.0;center}");
        }

        private void PlaySweep_Click(object sender, RoutedEventArgs e)
        {
            var (startHz, endHz, durationMs, volume, pan) = GetParams();
            EarconPlayer.PlayScratchpadChirp(startHz, endHz, durationMs, volume, pan);
            Say($"Sweep: {startHz} to {endHz}Hz {durationMs}ms vol={volume:P0} pan={pan:+0.0;-0.0;center}");
        }

        private void PlaySlide_Click(object sender, RoutedEventArgs e)
        {
            var (_, _, _, _, pan) = GetParams();
            EarconPlayer.FilterEdgeMoveTone(pan < 0);
            Say($"Slide: pan={pan:+0.0;-0.0;center}");
        }

        private void PlayZip_Click(object sender, RoutedEventArgs e)
        {
            var (_, _, _, _, pan) = GetParams();
            EarconPlayer.FilterBoundaryHitTone(false); // forward zip
            Say($"Zip forward: pan={pan:+0.0;-0.0;center}");
        }

        private void PlayZipReversed_Click(object sender, RoutedEventArgs e)
        {
            var (_, _, _, _, pan) = GetParams();
            EarconPlayer.FilterBoundaryHitTone(true); // reversed zip
            Say($"Zip reversed: pan={pan:+0.0;-0.0;center}");
        }

        private void PlaySqueeze_Click(object sender, RoutedEventArgs e)
        {
            EarconPlayer.FilterSqueezeTone();
            Say("Squeeze: 800 to 200Hz descending sweep, 300ms");
        }

        private void PlayStretch_Click(object sender, RoutedEventArgs e)
        {
            EarconPlayer.FilterStretchTone();
            Say("Stretch: 200 to 800Hz plus 300 to 900Hz dual sweep, 300ms");
        }

        /// <summary>
        /// Put a line in the status area. It is a polite live region, so a
        /// screen reader reads it without the operator leaving the control they
        /// just used — which matters here, where the point is to press a button
        /// repeatedly and listen.
        /// </summary>
        private void Say(string text)
        {
            if (StatusText != null) StatusText.Text = text;
        }
    }
}
