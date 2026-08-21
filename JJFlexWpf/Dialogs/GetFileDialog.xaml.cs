using System.Windows;
using Microsoft.Win32;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// File picker wrapper with optional replace confirmation.
    /// Wraps OpenFileDialog with "file exists" checking.
    /// </summary>
    public partial class GetFileDialog : JJFlexDialog
    {
        /// <summary>Dialog title for the file picker.</summary>
        public string PickerTitle { get; set; } = "Select File";

        /// <summary>Default file extension (e.g., "txt").</summary>
        public string DefaultExtension { get; set; } = "";

        /// <summary>File filter (e.g., "Text files (*.txt)|*.txt").</summary>
        public string Filter { get; set; } = "All files (*.*)|*.*";

        /// <summary>If true, warn when selected file already exists.</summary>
        public bool CheckReplace { get; set; }

        /// <summary>The selected file path. Empty if canceled.</summary>
        public string FileName { get; private set; } = "";

        public GetFileDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // Early exits here go through CloseWithResult, never a bare
        // DialogResult assignment: assigning from Loaded threw on windows
        // realised with Show() and aborted the Tier 1 dialog suite on
        // 2026-08-20/21 — see JJFlexDialog.CloseWithResult (#159).
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            bool selected = false;
            while (!selected)
            {
                var ofd = new OpenFileDialog
                {
                    Title = PickerTitle,
                    DefaultExt = DefaultExtension,
                    Filter = Filter,
                    CheckFileExists = false,
                    CheckPathExists = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (ofd.ShowDialog() != true)
                {
                    CloseWithResult(false);
                    return;
                }

                if (CheckReplace && System.IO.File.Exists(ofd.FileName))
                {
                    // ConfirmActionDialog rather than a raw message box: the
                    // file name is worth reviewing before agreeing to replace,
                    // and replace must never be the muscle-memory default.
                    var confirm = new ConfirmActionDialog(
                        "File Exists",
                        $"{ofd.FileName} already exists.",
                        question: "Replace it?",
                        yesLabel: "_Replace",
                        noLabel: "Choose _another");
                    if (confirm.ShowDialog() != true)
                        continue; // Loop back to file picker
                }

                FileName = ofd.FileName;
                selected = true;
            }

            CloseWithResult(true);
        }
    }
}
