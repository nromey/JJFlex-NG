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
                    ResultText.Text = Lexicon.Get("audio.filter.calculator.computed_high",
                        ("low", low), ("high", high));
                }
                else if (hasHigh && hasWidth && !hasLow)
                {
                    low = high - width;
                    ResultLow = low;
                    ResultHigh = high;
                    ResultText.Text = Lexicon.Get("audio.filter.calculator.computed_low",
                        ("low", low), ("high", high));
                }
                else if (hasLow && hasHigh)
                {
                    width = high - low;
                    ResultLow = low;
                    ResultHigh = high;
                    ResultText.Text = Lexicon.Get("audio.filter.calculator.computed_width",
                        ("width", width), ("low", low), ("high", high));
                }
                else
                {
                    // All three filled — just show width
                    ResultLow = low;
                    ResultHigh = high;
                    ResultText.Text = Lexicon.Get("audio.filter.calculator.all_three",
                        ("low", low), ("high", high), ("width", high - low));
                }

                bool valid = ResultLow >= 0 && ResultHigh > ResultLow;
                ApplyButton.IsEnabled = valid;
                System.Windows.Automation.AutomationProperties.SetName(ResultText, ResultText.Text);
            }
            else
            {
                ResultLow = null;
                ResultHigh = null;
                ResultText.Text = filled == 0 ? "" : Lexicon.Get("audio.filter.calculator.need_one_more");
                ApplyButton.IsEnabled = false;
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (ResultLow.HasValue && ResultHigh.HasValue)
            {
                DialogResult = true;
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.filter.calculator.applied", ("low", ResultLow), ("high", ResultHigh)),
                    VerbosityLevel.Terse, true);
                Close();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
