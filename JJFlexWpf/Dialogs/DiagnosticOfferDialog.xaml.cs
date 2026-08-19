using System;
using System.IO;
using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The offer that appears at the moment something fails: here is what went
    /// wrong, and here is the evidence, one keystroke away.
    ///
    /// Everything about this window is shaped by one fact — a screen reader
    /// flushes its speech queue when a window opens. So the failure is named in
    /// the TITLE, the consequence is dialog body text (read straight after the
    /// title), and focus lands on the action. Nothing is spoken before the
    /// window appears, because nothing spoken before the window appears would
    /// survive.
    ///
    /// The policy about WHEN this appears lives entirely in DiagnosticOffer.
    /// This class only knows how to present an offer it has been handed.
    /// </summary>
    public partial class DiagnosticOfferDialog : JJFlexDialog
    {
        private readonly string _logPath;

        /// <summary>True when the operator asked not to be offered again.</summary>
        public bool Declined { get; private set; }

        public DiagnosticOfferDialog(string title, string detail, string logPath)
        {
            InitializeComponent();
            Title = title;
            _logPath = logPath ?? "";
            Loaded += (_, _) =>
            {
                DetailText.Text = detail ?? "";
                System.Windows.Automation.AutomationProperties.SetName(DetailText, DetailText.Text);
                if (string.IsNullOrEmpty(_logPath))
                {
                    // Be honest rather than offering a button that cannot work.
                    OfferText.Text =
                        "No diagnostic log is running, so there is nothing to save. " +
                        "You can turn one on in Settings, Diagnostics.";
                    SaveButton.Visibility = Visibility.Collapsed;
                    CopyPathButton.Visibility = Visibility.Collapsed;
                }
                try { SaveButton.Focus(); } catch { }
            };
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = $"jjflex-diagnostic-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Title = "Save the diagnostic log"
            };
            if (dlg.ShowDialog(this) != true)
            {
                DiagnosticsBridge.Speak?.Invoke("Not saved.");
                return;
            }

            try
            {
                long bytes = CopyLiveLog(_logPath, dlg.FileName);
                string size = "unknown size";
                try { size = DiagnosticsBridge.DescribeBytes?.Invoke(bytes) ?? size; } catch { }
                // Size is spoken because the next thing the operator does with
                // this file is usually attach it to something.
                DiagnosticsBridge.Speak?.Invoke($"Diagnostic log saved, about {size}.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke(
                    "The diagnostic log could not be copied. It is still in the settings folder.");
            }

            try { DialogResult = true; } catch (InvalidOperationException) { }
            Close();
        }

        /// <summary>
        /// Copy the log while it is still open for writing.
        ///
        /// FileShare.ReadWrite is not optional: the live listener holds the file
        /// right now, and a plain File.Copy fails with a sharing violation —
        /// which is exactly how the crash bundler used to end up with no trace
        /// in it at all. Returns bytes written.
        /// </summary>
        private static long CopyLiveLog(string source, string destination)
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete);
            using var dest = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dest);
            return dest.Length;
        }

        private void CopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_logPath);
                DiagnosticsBridge.Speak?.Invoke("Path copied.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke("The path could not be copied to the clipboard.");
            }
        }

        private void NotNowButton_Click(object sender, RoutedEventArgs e)
        {
            // An explicit "not now" is information, not just a dismissal: it
            // means this operator does not want to be interrupted about
            // failures in this session. DiagnosticOffer honours that.
            Declined = true;
            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();
        }
    }
}
