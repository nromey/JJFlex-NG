using System.Windows;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Item representing a pan range.
    /// </summary>
    public class PanRangeItem
    {
        public string Display { get; set; } = "";
        public object? Tag { get; set; }
        public override string ToString() => Display;
    }

    public partial class PanListDialog : JJFlexDialog
    {
        /// <summary>Pan range items to display. Set before showing.</summary>
        public List<PanRangeItem>? Ranges { get; set; }

        /// <summary>The selected range item. Null if canceled.</summary>
        public PanRangeItem? SelectedRange { get; private set; }

        public PanListDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RangeList.ItemsSource = Ranges;
            if (Ranges != null && Ranges.Count > 0)
                RangeList.SelectedIndex = 0;
            RangeList.Focus();
        }

        private void SelectAndClose()
        {
            if (RangeList.SelectedItem is PanRangeItem item)
            {
                SelectedRange = item;
                DialogResult = true;
                Close();
            }
        }

        private void RangeList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            SelectAndClose();
        }

        private void RangeList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SelectAndClose();
                e.Handled = true;
            }
        }
    }
}
