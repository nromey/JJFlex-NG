using System.Windows;
using Radios;

namespace JJFlexWpf.Dialogs
{
    public partial class FilterCalculatorDialog : JJFlexDialog
    {
        public int? ResultLow { get; private set; }
        public int? ResultHigh { get; private set; }

        private bool _updating;

        public FilterCalculatorDialog()
        {
            InitializeComponent();
        }

        private void OnValueChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_updating) return;
            _updating = true;
            try
            {
                Compute();
            }
            finally
            {
                _updating = false;
            }
        }

        private void Compute()
        {
            bool hasLow = int.TryParse(LowBox.Text, out int low);
            bool hasHigh = int.TryParse(HighBox.Text, out int high);
            bool hasWidth = int.TryParse(WidthBox.Text, out int width);

            int filled = (hasLow ? 1 : 0) + (hasHigh ? 1 : 0) + (hasWidth ? 1 : 0);

            if (filled >= 2)
            {
                if (hasLow && hasWidth && !hasHigh)
                {
                    high = low + width;
                    ResultLow = low;
                    ResultHigh = high;
                    ResultText.Text = $"Computed high: {high} Hz (filter {low} to {high})";
                }
                else if (hasHigh && hasWidth && !hasLow)
                {
                    low = high - width;
                    ResultLow = low;
                    ResultHigh = high;
                    ResultText.Text = $"Computed low: {low} Hz (filter {low} to {high})";
                }
                else if (hasLow && hasHigh)
                {
                    width = high - low;
                    ResultLow = low;
                    ResultHigh = high;
                    ResultText.Text = $"Width: {width} Hz (filter {low} to {high})";
                }
                else
                {
                    // All three filled — just show width
                    ResultLow = low;
                    ResultHigh = high;
                    ResultText.Text = $"Filter {low} to {high}, width {high - low} Hz";
                }

                bool valid = ResultLow >= 0 && ResultHigh > ResultLow;
                ApplyButton.IsEnabled = valid;
                System.Windows.Automation.AutomationProperties.SetName(ResultText, ResultText.Text);
            }
            else
            {
                ResultLow = null;
                ResultHigh = null;
                ResultText.Text = filled == 0 ? "" : "Enter one more value to compute.";
                ApplyButton.IsEnabled = false;
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (ResultLow.HasValue && ResultHigh.HasValue)
            {
                DialogResult = true;
                ScreenReaderOutput.Speak($"Filter set to {ResultLow} to {ResultHigh}", true);
                Close();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
