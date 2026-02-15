using System.Diagnostics;
using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class ReverseBeaconDialog : JJFlexDialog
    {
        private const string WebBaseAddress = "http://www.reversebeacon.net/dxsd1/dxsd1.php?f=0&t=dx&c=";

        /// <summary>
        /// Initial call sign to populate. Set before calling ShowDialog().
        /// </summary>
        public string InitialCallSign { get; set; } = "";

        /// <summary>
        /// Optional delegate to trace/log. Wire to Tracing.TraceLine if desired.
        /// </summary>
        public Action<string>? TraceAction { get; set; }

        public ReverseBeaconDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            CallBox.Text = InitialCallSign;
            CallBox.SelectAll();
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            string addr = WebBaseAddress + CallBox.Text;
            TraceAction?.Invoke("beacon:" + addr);
            try
            {
                Process.Start(new ProcessStartInfo(addr) { UseShellExecute = true });
            }
            catch
            {
                // Browser launch failure — not critical
            }
            DialogResult = true;
            Close();
        }
    }
}
