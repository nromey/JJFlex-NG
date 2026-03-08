using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    public partial class EarconScratchpadDialog : JJFlexDialog
    {
        private bool _updating;

        public EarconScratchpadDialog()
        {
            InitializeComponent();
            ResizeMode = System.Windows.ResizeMode.NoResize;
        }

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

        private void PlayTone_Click(object sender, RoutedEventArgs e)
        {
            var (startHz, _, durationMs, volume, pan) = GetParams();
            EarconPlayer.PlayScratchpadTone(startHz, durationMs, volume, pan);
            StatusText.Text = $"Tone: {startHz}Hz {durationMs}ms vol={volume:P0} pan={pan:+0.0;-0.0;center}";
        }

        private void PlaySweep_Click(object sender, RoutedEventArgs e)
        {
            var (startHz, endHz, durationMs, volume, pan) = GetParams();
            EarconPlayer.PlayScratchpadChirp(startHz, endHz, durationMs, volume, pan);
            StatusText.Text = $"Sweep: {startHz}→{endHz}Hz {durationMs}ms vol={volume:P0} pan={pan:+0.0;-0.0;center}";
        }

        private void PlaySlide_Click(object sender, RoutedEventArgs e)
        {
            var (_, _, _, _, pan) = GetParams();
            EarconPlayer.FilterEdgeMoveTone(pan < 0);
            StatusText.Text = $"Slide: pan={pan:+0.0;-0.0;center}";
        }

        private void PlayZip_Click(object sender, RoutedEventArgs e)
        {
            var (_, _, _, _, pan) = GetParams();
            EarconPlayer.FilterBoundaryHitTone(false); // forward zip
            StatusText.Text = $"Zip forward: pan={pan:+0.0;-0.0;center}";
        }

        private void PlayZipReversed_Click(object sender, RoutedEventArgs e)
        {
            var (_, _, _, _, pan) = GetParams();
            EarconPlayer.FilterBoundaryHitTone(true); // reversed zip
            StatusText.Text = $"Zip reversed: pan={pan:+0.0;-0.0;center}";
        }

        private void PlaySqueeze_Click(object sender, RoutedEventArgs e)
        {
            EarconPlayer.FilterSqueezeTone();
            StatusText.Text = "Squeeze: 800→200Hz descending sweep, 300ms";
        }

        private void PlayStretch_Click(object sender, RoutedEventArgs e)
        {
            EarconPlayer.FilterStretchTone();
            StatusText.Text = "Stretch: 200→800Hz + 300→900Hz dual sweep, 300ms";
        }
    }
}
