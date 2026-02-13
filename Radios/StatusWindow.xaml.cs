using System.Windows;
using System.Windows.Input;

namespace Radios
{
    /// <summary>
    /// Radio status dialog showing a plain-English snapshot of radio state.
    /// Non-modal, single-instance. Static snapshot — close and reopen to refresh.
    /// </summary>
    public partial class StatusWindow : Window
    {
        public StatusWindow(FlexBase radio)
        {
            InitializeComponent();

            var snap = RadioStatusBuilder.BuildDetailedStatus(radio);
            PopulateFields(snap);

            Loaded += (s, e) => ScreenReaderOutput.Speak("Radio status", true);
        }

        private void PopulateFields(RadioStatusSnapshot snap)
        {
            if (!snap.IsConnected)
            {
                txtConnection.Text = "Not connected";
                return;
            }

            txtConnection.Text = "Connected";
            txtRadioModel.Text = string.IsNullOrEmpty(snap.RadioModel) ? "-" : snap.RadioModel;
            txtNickname.Text = string.IsNullOrEmpty(snap.RadioNickname) ? "-" : snap.RadioNickname;

            if (!snap.HasActiveSlice)
            {
                txtFrequency.Text = "No active slice";
                txtMode.Text = "-";
                txtBand.Text = "-";
                txtSlice.Text = "None";
                txtTXState.Text = snap.IsTransmitting ? "Transmitting" : "Receiving";
                txtSignal.Text = "-";
                txtRemote.Text = snap.IsRemote ? "Remote (SmartLink)" : "Local";
                return;
            }

            txtFrequency.Text = $"{snap.FrequencyDisplay} MHz";
            txtMode.Text = snap.Mode;
            txtBand.Text = string.IsNullOrEmpty(snap.BandName) ? "Out of band" : snap.BandName;
            txtSlice.Text = string.IsNullOrEmpty(snap.SliceLetter) ? "-" : snap.SliceLetter;
            txtTXState.Text = snap.IsTransmitting ? "Transmitting" : "Receiving";
            txtSignal.Text = string.IsNullOrEmpty(snap.SignalDisplay) ? "-" : snap.SignalDisplay;
            txtRemote.Text = snap.IsRemote ? "Remote (SmartLink)" : "Local";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
