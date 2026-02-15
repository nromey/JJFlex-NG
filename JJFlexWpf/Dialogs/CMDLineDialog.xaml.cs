using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class CMDLineDialog : JJFlexDialog
    {
        /// <summary>
        /// Delegate to encode the command string (e.g., Escapes.Escapes.Encode).
        /// If null, raw text is returned.
        /// </summary>
        public Func<string, string>? EncodeCommand { get; set; }

        /// <summary>
        /// Optional delegate to trace/log. Wire to Tracing.TraceLine if desired.
        /// </summary>
        public Action<string>? TraceAction { get; set; }

        /// <summary>
        /// The encoded command result. Set after Send is clicked.
        /// </summary>
        public string ResultCommand { get; private set; } = "";

        public CMDLineDialog()
        {
            InitializeComponent();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            string encoded = EncodeCommand != null
                ? EncodeCommand(CommandBox.Text)
                : CommandBox.Text;
            TraceAction?.Invoke("cmdline:" + encoded);
            ResultCommand = encoded;
            DialogResult = true;
            Close();
        }
    }
}
