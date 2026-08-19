using System;
using System.IO;
using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The Problems window — everything that has gone wrong this session, on
    /// demand, from Ctrl+J, Ctrl+R or the Diagnostics tab.
    ///
    /// This is the half of the old failure-moment offer that was worth keeping.
    /// The offer's real content — "here is what went wrong, and here is the
    /// evidence, one keystroke away" — is all still here, including the Save
    /// button and its live-file copy. What is gone is the part that arrived
    /// uninvited, stole focus, and flushed the screen reader's speech queue at
    /// the exact moment the operator was being told what had happened.
    ///
    /// This window opens because the operator asked for it, so there is nothing
    /// in flight to protect. It still names its whole subject in the Title,
    /// because that is what a screen reader reads when a window arrives.
    ///
    /// Never opened when the list is empty — the chord answers that in speech
    /// instead. A window whose entire content is "nothing to see" is a window
    /// the operator has to close for no reason.
    /// </summary>
    public partial class ProblemsDialog : JJFlexDialog
    {
        private readonly string _logPath;

        public ProblemsDialog()
        {
            InitializeComponent();

            string live = "";
            try { live = DiagnosticsBridge.LiveLogPath?.Invoke() ?? ""; } catch { }
            _logPath = live;

            Title = ProblemLog.Summary();

            Loaded += (_, _) =>
            {
                var entries = ProblemLog.NewestFirst();
                ProblemList.ItemsSource = entries;

                LeadText.Text = ProblemLog.Truncated
                    ? "Newest first. The oldest entries have been dropped from this list to keep it manageable, but the diagnostic log still has them."
                    : "Newest first. Each line gives the time, what failed, and what it means.";
                System.Windows.Automation.AutomationProperties.SetName(LeadText, LeadText.Text);

                if (string.IsNullOrEmpty(_logPath))
                {
                    // Be honest rather than offering a button that cannot work.
                    LogText.Text =
                        "No diagnostic log is running, so this list is all there is about these problems. " +
                        "You can turn a log on in Settings, Diagnostics, and it will record what happens from then on.";
                    SaveButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    LogText.Text =
                        "The diagnostic log recorded far more about each of these than this list shows. " +
                        "It stays on this computer. Nothing is sent anywhere.";
                }
                System.Windows.Automation.AutomationProperties.SetName(LogText, LogText.Text);

                if (entries.Count > 0) ProblemList.SelectedIndex = 0;
            };
        }

        /// <summary>
        /// Focus the newest problem, not a button.
        ///
        /// The base class's default is MoveFocus(First), which would land on
        /// whatever is first in the tab order. The operator came to READ, so the
        /// first thing they hear after the window title should be the thing they
        /// came for. Focusing the item CONTAINER rather than the ListBox matters:
        /// focus on the list itself announces the list, focus on the item
        /// announces the problem.
        ///
        /// Overriding rather than calling Focus() from Loaded is not a style
        /// choice — the base class calls this again from OnContentRendered,
        /// after the window is genuinely active, and that second pass would
        /// otherwise undo anything Loaded had arranged.
        /// </summary>
        protected override void FocusFirstControl()
        {
            try
            {
                ProblemList.UpdateLayout();
                if (ProblemList.SelectedIndex >= 0 &&
                    ProblemList.ItemContainerGenerator.ContainerFromIndex(ProblemList.SelectedIndex)
                        is System.Windows.Controls.ListBoxItem item &&
                    item.Focus())
                {
                    return;
                }
                if (ProblemList.Focus()) return;
            }
            catch { }
            base.FocusFirstControl();
        }

        /// <summary>
        /// Open the window if there is anything to show; otherwise say so.
        ///
        /// Both doors (the chord and the Diagnostics tab button) come through
        /// here so the two can never disagree about what an empty list means.
        /// </summary>
        public static void ShowOrSpeakEmpty(Window? owner = null)
        {
            if (ProblemLog.Count == 0)
            {
                Radios.ScreenReaderOutput.Speak(
                    "No problems recorded this session.",
                    Radios.VerbosityLevel.Critical, true);
                return;
            }

            try
            {
                var dlg = new ProblemsDialog();
                if (owner != null)
                {
                    try { dlg.Owner = owner; } catch { }
                }
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                try
                {
                    JJTrace.Tracing.TraceLine(
                        "ProblemsDialog.ShowOrSpeakEmpty failed: " + ex.Message,
                        System.Diagnostics.TraceLevel.Warning);
                }
                catch { }
                Radios.ScreenReaderOutput.Speak(
                    "The problems list could not be opened.",
                    Radios.VerbosityLevel.Critical, true);
            }
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

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ProblemLog.AsText());
                DiagnosticsBridge.Speak?.Invoke("Problems copied.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke("The problems could not be copied to the clipboard.");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();
        }
    }
}
