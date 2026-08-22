using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class FreqInputDialog : JJFlexDialog
    {
        /// <summary>
        /// Delegate to validate/format frequency input.
        /// Returns formatted frequency string on success, null on failure.
        /// Wire to FormatFreqForRadio from globals.vb.
        /// </summary>
        public Func<string, string?>? ValidateFrequency { get; set; }

        /// <summary>
        /// Error message to show on invalid frequency.
        /// Wire to BadFreqMSG from globals.vb.
        /// </summary>
        public string ErrorMessage { get; set; } = Radios.Lexicon.Get("settings.tuning.freq_input_error");

        /// <summary>
        /// The validated frequency result. Set after OK is clicked and validation passes.
        /// </summary>
        public string ResultFrequency { get; private set; } = "";

        public FreqInputDialog()
        {
            InitializeComponent();
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            string? formatted = ValidateFrequency?.Invoke(FreqBox.Text);
            if (formatted == null)
            {
                MessageBox.Show(ErrorMessage, Radios.Lexicon.Get("settings.tuning.freq_input_error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ResultFrequency = formatted;
            DialogResult = true;
            Close();
        }
    }
}
