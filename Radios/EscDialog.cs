        using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    public partial class EscDialog : Form
    {
        private const double GainMin = 0.0;
        private const double GainMax = 4.0;
        private const double GainStep = 0.05;
        private static readonly int GainSliderMax = (int)(GainMax / GainStep);

        private readonly FlexBase rig;
        private Radio Radio => rig?.theRadio;
        private Slice trackedSlice;
        private bool suppressSliceNotifications;
        private bool suppressControlEvents;

        public EscDialog(FlexBase rig)
        {
            InitializeComponent();
            this.rig = rig ?? throw new ArgumentNullException(nameof(rig));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            AttachToRadio();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DetachFromSlice();
            DetachFromRadio();
            base.OnFormClosed(e);
        }

        private void AttachToRadio()
        {
            if (Radio != null)
            {
                Radio.PropertyChanged += Radio_PropertyChanged;
            }
            AttachToSlice();
        }

        private void DetachFromRadio()
        {
            if (Radio != null)
            {
                Radio.PropertyChanged -= Radio_PropertyChanged;
            }
        }

        private void AttachToSlice()
        {
            DetachFromSlice();
            var slice = GetTargetSlice();
            if (slice != null)
            {
                trackedSlice = slice;
                trackedSlice.PropertyChanged += TrackedSlice_PropertyChanged;
            }
            UpdateControlsFromSlice();
        }

        private void DetachFromSlice()
        {
            if (trackedSlice != null)
            {
                trackedSlice.PropertyChanged -= TrackedSlice_PropertyChanged;
                trackedSlice = null;
            }
        }

        private Slice GetTargetSlice()
        {
            var radio = Radio;
            if (radio?.ActiveSlice == null)
            {
                return null;
            }

            var active = radio.ActiveSlice;
            if (active.DiversityChild && (active.DiversitySlicePartner != null))
            {
                return active.DiversitySlicePartner;
            }

            return active;
        }

        private void Radio_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null) return;
            if (!string.Equals(e.PropertyName, "ActiveSlice", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.PropertyName, "DiversityOn", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke((Action)AttachToSlice);
            }
            else
            {
                AttachToSlice();
            }
        }

        private void TrackedSlice_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (suppressSliceNotifications) return;
            if (e.PropertyName is null) return;

            switch (e.PropertyName)
            {
                case nameof(Slice.ESCEnabled):
                case nameof(Slice.ESCPhaseShift):
                case nameof(Slice.ESCGain):
                case nameof(Slice.DiversityOn):
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke((Action)UpdateControlsFromSlice);
                    }
                    else
                    {
                        UpdateControlsFromSlice();
                    }
                    break;
            }
        }

        private void UpdateControlsFromSlice()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke((Action)UpdateControlsFromSlice);
                return;
            }

            bool hasSlice = trackedSlice != null;
            bool diversityReady = rig?.DiversityReady == true;
            bool diversityActive = rig?.DiversityOn == true;
            bool controlsEnabled = hasSlice && diversityReady && diversityActive;
            bool escEnabled = hasSlice && trackedSlice.ESCEnabled;

            suppressControlEvents = true;
            try
            {
                escEnabledCheckBox.Checked = escEnabled;
                escEnabledCheckBox.Enabled = hasSlice && diversityActive;
                phaseTrackBar.Enabled = controlsEnabled;
                phaseValueTextBox.Enabled = controlsEnabled;
                gainTrackBar.Enabled = controlsEnabled;
                gainValueTextBox.Enabled = controlsEnabled;
                preset90Button.Enabled = controlsEnabled;
                preset180Button.Enabled = controlsEnabled;

                if (hasSlice)
                {
                    UpdatePhaseDisplay(trackedSlice.ESCPhaseShift);
                    UpdateGainDisplay(trackedSlice.ESCGain);
                }
                else
                {
                    UpdatePhaseDisplay(0);
                    UpdateGainDisplay(GainMin);
                }

                if (!hasSlice)
                {
                    statusLabel.Text = "Select an active slice to adjust ESC.";
                }
                else if (!diversityReady)
                {
                    var gate = rig.DiversityGateMessage;
                    statusLabel.Text = string.IsNullOrEmpty(gate) ? "Diversity not ready." : gate;
                }
                else if (!diversityActive)
                {
                    statusLabel.Text = "Enable diversity to apply ESC.";
                }
                else
                {
                    statusLabel.Text = escEnabled ? "ESC is enabled." : "ESC is disabled.";
                }
            }
            finally
            {
                suppressControlEvents = false;
            }
        }

        private void UpdatePhaseDisplay(double value)
        {
            WithControlUpdates(() =>
            {
                double normalized = NormalizePhase(value);
                int sliderValue = (int)Math.Round(normalized);
                sliderValue = Math.Max(phaseTrackBar.Minimum, Math.Min(phaseTrackBar.Maximum, sliderValue));
                phaseTrackBar.Value = sliderValue;
                phaseValueTextBox.Text = normalized.ToString("0.0", CultureInfo.InvariantCulture);
            });
        }

        private void UpdateGainDisplay(double value)
        {
            WithControlUpdates(() =>
            {
                double normalized = NormalizeGain(value);
                int sliderValue = GainToSlider(normalized);
                sliderValue = Math.Max(gainTrackBar.Minimum, Math.Min(gainTrackBar.Maximum, sliderValue));
                gainTrackBar.Value = sliderValue;
                gainValueTextBox.Text = normalized.ToString("0.00", CultureInfo.InvariantCulture);
            });
        }

        private static double NormalizePhase(double degrees)
        {
            double normalized = degrees % 360.0;
            if (normalized < 0) normalized += 360.0;
            return normalized;
        }

        private static double NormalizeGain(double gain)
        {
            if (double.IsNaN(gain) || double.IsInfinity(gain))
            {
                return GainMin;
            }

            return Math.Min(GainMax, Math.Max(GainMin, gain));
        }

        private void WithControlUpdates(Action action)
        {
            bool previous = suppressControlEvents;
            suppressControlEvents = true;
            try
            {
                action();
            }
            finally
            {
                suppressControlEvents = previous;
            }
        }

        private static int GainToSlider(double gain)
        {
            return (int)Math.Round(gain / GainStep);
        }

        private static double SliderToGain(int value)
        {
            return value * GainStep;
        }

        private void SetPhaseShift(double value)
        {
            double normalized = NormalizePhase(value);
            if (trackedSlice != null)
            {
                suppressSliceNotifications = true;
                try
                {
                    trackedSlice.ESCPhaseShift = normalized;
                }
                finally
                {
                    suppressSliceNotifications = false;
                }
            }
            UpdatePhaseDisplay(normalized);
        }

        private void SetGain(double value)
        {
            double normalized = NormalizeGain(value);
            if (trackedSlice != null)
            {
                suppressSliceNotifications = true;
                try
                {
                    trackedSlice.ESCGain = normalized;
                }
                finally
                {
                    suppressSliceNotifications = false;
                }
            }
            UpdateGainDisplay(normalized);
        }

        private void AdjustPhaseBy(double delta)
        {
            double current = trackedSlice?.ESCPhaseShift ?? NormalizePhase(ParsePhaseText());
            SetPhaseShift(current + delta);
        }

        private void AdjustGainBy(double delta)
        {
            double current = trackedSlice?.ESCGain ?? ParseGainText();
            SetGain(current + delta);
        }

        private double ParsePhaseText()
        {
            if (double.TryParse(phaseValueTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return 0.0;
        }

        private double ParseGainText()
        {
            if (double.TryParse(gainValueTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return GainMin;
        }

        private void ApplyPhaseFromText()
        {
            if (double.TryParse(phaseValueTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                SetPhaseShift(value);
            }
            else if (trackedSlice != null)
            {
                UpdatePhaseDisplay(trackedSlice.ESCPhaseShift);
            }
            else
            {
                UpdatePhaseDisplay(0);
            }
        }

        private void ApplyGainFromText()
        {
            if (double.TryParse(gainValueTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                SetGain(value);
            }
            else if (trackedSlice != null)
            {
                UpdateGainDisplay(trackedSlice.ESCGain);
            }
            else
            {
                UpdateGainDisplay(GainMin);
            }
        }

        private void EscEnabledCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (suppressControlEvents || trackedSlice == null) return;

            suppressSliceNotifications = true;
            try
            {
                trackedSlice.ESCEnabled = escEnabledCheckBox.Checked;
            }
            finally
            {
                suppressSliceNotifications = false;
            }

            UpdateControlsFromSlice();
        }

        private void PhaseTrackBar_Scroll(object sender, EventArgs e)
        {
            if (suppressControlEvents) return;
            SetPhaseShift(phaseTrackBar.Value);
        }

        private void PhaseValueTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (suppressControlEvents) return;

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    ApplyPhaseFromText();
                    break;
                case Keys.Up:
                    e.SuppressKeyPress = true;
                    AdjustPhaseBy(1);
                    break;
                case Keys.Down:
                    e.SuppressKeyPress = true;
                    AdjustPhaseBy(-1);
                    break;
                case Keys.PageUp:
                    e.SuppressKeyPress = true;
                    AdjustPhaseBy(10);
                    break;
                case Keys.PageDown:
                    e.SuppressKeyPress = true;
                    AdjustPhaseBy(-10);
                    break;
            }
        }

        private void PhaseValueTextBox_Leave(object sender, EventArgs e)
        {
            if (suppressControlEvents) return;
            ApplyPhaseFromText();
        }

        private void GainTrackBar_Scroll(object sender, EventArgs e)
        {
            if (suppressControlEvents) return;
            SetGain(SliderToGain(gainTrackBar.Value));
        }

        private void GainValueTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (suppressControlEvents) return;

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    ApplyGainFromText();
                    break;
                case Keys.Up:
                    e.SuppressKeyPress = true;
                    AdjustGainBy(GainStep);
                    break;
                case Keys.Down:
                    e.SuppressKeyPress = true;
                    AdjustGainBy(-GainStep);
                    break;
                case Keys.PageUp:
                    e.SuppressKeyPress = true;
                    AdjustGainBy(GainStep * 5);
                    break;
                case Keys.PageDown:
                    e.SuppressKeyPress = true;
                    AdjustGainBy(-GainStep * 5);
                    break;
            }
        }

        private void GainValueTextBox_Leave(object sender, EventArgs e)
        {
            if (suppressControlEvents) return;
            ApplyGainFromText();
        }

        private void Preset90Button_Click(object sender, EventArgs e)
        {
            SetPhaseShift(90);
        }

        private void Preset180Button_Click(object sender, EventArgs e)
        {
            SetPhaseShift(180);
        }
    }
}
