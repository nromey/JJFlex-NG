using System;
using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Diagnostics. The configuration half of the diagnostic log
    /// surface (docs/planning/active/diagnostic-log-surface.md); the archive
    /// half is the Saved Diagnostic Logs window, one button away.
    ///
    /// This partial deliberately touches nothing in SettingsDialog.xaml.cs. It
    /// hangs itself off the tab panel's own Loaded / IsVisibleChanged events, so
    /// it needs no constructor line and no entry in ApplyAllSettings — every
    /// control here takes effect the moment it is used, which is what a setting
    /// that governs whether the app is recording anything has to do. Waiting for
    /// OK would mean the operator turns on detailed logging, reproduces the
    /// problem, and only then discovers the setting had not applied yet.
    ///
    /// Nothing here caches the log's state. It reads it through DiagnosticsBridge
    /// on every refresh and re-reads it whenever the plumbing says it changed —
    /// the retired trace dialog cached its state and spent months announcing a
    /// state that was fiction.
    /// </summary>
    public partial class SettingsDialog
    {
        private bool _diagWired;
        private bool _diagSuppressEvents;

        private void DiagnosticsPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_diagWired)
            {
                DiagnosticsBridge.StateChanged += OnDiagnosticStateChanged;
                Closed += (_, _) => DiagnosticsBridge.StateChanged -= OnDiagnosticStateChanged;
                _diagWired = true;
            }
            RefreshDiagnosticsTab();
        }

        /// <summary>
        /// Tab entry always starts with orientation, never with control soup.
        /// A screen reader reads a tab's static text when the tab is first
        /// realized but not on every return to it, so the status sentence is
        /// spoken here — it is the one thing that changes between visits.
        /// </summary>
        private void DiagnosticsPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true) return;
            RefreshDiagnosticsTab();
            try { DiagnosticsBridge.Speak?.Invoke(DiagnosticsBridge.State()); } catch { }
        }

        private void OnDiagnosticStateChanged(object? sender, EventArgs e)
        {
            try
            {
                if (Dispatcher.CheckAccess()) RefreshDiagnosticsTab();
                else Dispatcher.BeginInvoke(RefreshDiagnosticsTab);
            }
            catch { /* a closing dialog refusing a refresh is not an error */ }
        }

        /// <summary>
        /// Pull every visible value from the plumbing. Cheap — it reads flags
        /// and formats strings; the expensive folder walk is behind its own
        /// button.
        /// </summary>
        private void RefreshDiagnosticsTab()
        {
            if (DiagStatusText == null) return; // called before the tab realized

            _diagSuppressEvents = true;
            try
            {
                DiagStatusText.Text = DiagnosticsBridge.State();
                DiagStatusText.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                    DiagStatusText.Text);

                bool keep = true;
                int detail = 0;
                try { keep = DiagnosticsBridge.KeepLog?.Invoke() ?? true; } catch { }
                try { detail = DiagnosticsBridge.DetailLevel?.Invoke() ?? 0; } catch { }

                DiagKeepLogCheckbox.IsChecked = keep;
                DiagDetailNormalRadio.IsChecked = detail == 0;
                DiagDetailDetailedRadio.IsChecked = detail == 1;

                string live = SafeCall(DiagnosticsBridge.LiveLogPath);
                string folder = SafeCall(DiagnosticsBridge.LogFolder);
                if (!string.IsNullOrEmpty(live))
                {
                    // The real, resolved path — not the %AppData% template.
                    // An operator asked for "the trace" has to be able to produce
                    // it without sighted help and without knowing AppData folklore.
                    DiagPathText.Text =
                        $"The live log is {live}. Older sessions are in the Traces folder next to it.";
                }
                else if (!string.IsNullOrEmpty(folder))
                {
                    DiagPathText.Text =
                        $"No log is running. When one is, it lands in {folder}.";
                }
                else
                {
                    DiagPathText.Text = "The live log location is not available.";
                }
                DiagPathText.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                    DiagPathText.Text);

                UpdateCaptureButton();
            }
            finally
            {
                _diagSuppressEvents = false;
            }
        }

        /// <summary>
        /// Content AND accessible name change together, always. The frozen
        /// AutomationProperties.Name on the retired dialog's button — same words
        /// in both states — is the anti-pattern this surface exists to replace.
        /// </summary>
        private void UpdateCaptureButton()
        {
            bool capturing = DiagnosticsBridge.Capturing();
            string label = capturing ? "Stop detailed capture" : "Start detailed capture";
            DiagCaptureButton.Content = label;
            System.Windows.Automation.AutomationProperties.SetName(DiagCaptureButton, label);

            // The export offer belongs to the capture that just stopped, so it
            // appears when one has, and disappears when a new one starts.
            bool haveExport = !capturing && !string.IsNullOrEmpty(SafeCall(DiagnosticsBridge.LastCaptureArchivePath));
            DiagExportCaptureButton.Visibility = haveExport ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string SafeCall(Func<string>? f)
        {
            try { return f?.Invoke() ?? string.Empty; }
            catch { return string.Empty; }
        }

        // ── Diagnostic log group ─────────────────────────────────────────

        private void DiagKeepLogCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_diagSuppressEvents) return;
            bool keep = DiagKeepLogCheckbox.IsChecked == true;
            ApplyDiagnosticChoice(keep, CurrentDetailChoice());

            // Turning it OFF speaks a consequence, not just a state. "Off" tells
            // the operator what they pressed; it does not tell them what they
            // have given up, and this is the one setting whose cost only shows
            // up later, when something has already gone wrong.
            DiagnosticsBridge.Speak?.Invoke(keep
                ? $"Diagnostic log on, {DetailWord()} detail."
                : "Diagnostic log off. If something goes wrong, JJ Flex will have no record to show you or the developer.");
        }

        private void DiagDetailRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_diagSuppressEvents) return;
            bool keep = DiagKeepLogCheckbox.IsChecked == true;
            ApplyDiagnosticChoice(keep, CurrentDetailChoice());
            if (keep)
                DiagnosticsBridge.Speak?.Invoke($"Diagnostic log on, {DetailWord()} detail.");
            else
                DiagnosticsBridge.Speak?.Invoke($"{DetailWord()} detail, for when you turn the log back on.");
        }

        private int CurrentDetailChoice() => DiagDetailDetailedRadio.IsChecked == true ? 1 : 0;

        private string DetailWord() => CurrentDetailChoice() == 1 ? "detailed" : "normal";

        private void ApplyDiagnosticChoice(bool keep, int detail)
        {
            try { DiagnosticsBridge.ApplySettings?.Invoke(keep, detail); }
            catch { DiagnosticsBridge.Speak?.Invoke("That diagnostic setting could not be saved."); }
        }

        private void DiagCopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            string live = SafeCall(DiagnosticsBridge.LiveLogPath);
            if (string.IsNullOrEmpty(live))
            {
                DiagnosticsBridge.Speak?.Invoke("There is no live log file to copy a path for.");
                return;
            }
            try
            {
                Clipboard.SetText(live);
                DiagnosticsBridge.Speak?.Invoke("Path copied.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke("The path could not be copied to the clipboard.");
            }
        }

        private void DiagOpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = SafeCall(DiagnosticsBridge.LogFolder);
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            {
                DiagnosticsBridge.Speak?.Invoke("The log folder could not be found.");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                DiagnosticsBridge.Speak?.Invoke("Log folder opened.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke("The log folder could not be opened.");
            }
        }

        // ── Detailed capture group ───────────────────────────────────────

        private void DiagCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            bool wasCapturing = DiagnosticsBridge.Capturing();
            DiagnosticsBridge.ToggleCapture("Settings, Diagnostics tab");
            RefreshDiagnosticsTab();

            // After a STOP, the common next act is getting the file somewhere
            // sendable, so focus goes to the export button rather than leaving
            // the operator to find it. After a START it stays put — the next act
            // is reproducing the problem, which is not in this dialog.
            if (wasCapturing && DiagExportCaptureButton.Visibility == Visibility.Visible)
            {
                try { DiagExportCaptureButton.Focus(); } catch { }
            }
        }

        private void DiagExportCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            string archive = SafeCall(DiagnosticsBridge.LastCaptureArchivePath);
            if (string.IsNullOrEmpty(archive) || !System.IO.File.Exists(archive))
            {
                DiagnosticsBridge.Speak?.Invoke("That capture is no longer where it was saved.");
                UpdateCaptureButton();
                return;
            }

            long bytes = 0;
            try { bytes = new System.IO.FileInfo(archive).Length; } catch { }
            string size = DescribeBytes(bytes);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Zip archive (*.zip)|*.zip",
                DefaultExt = "zip",
                FileName = System.IO.Path.GetFileName(archive),
                Title = "Export this capture"
            };
            if (dlg.ShowDialog(this) != true)
            {
                DiagnosticsBridge.Speak?.Invoke("Export cancelled.");
                return;
            }

            try
            {
                System.IO.File.Copy(archive, dlg.FileName, overwrite: true);
                // Size is spoken so an upload never surprises anyone — the same
                // rule the feedback picker sets.
                DiagnosticsBridge.Speak?.Invoke($"Capture exported, about {size}.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke("The capture could not be exported.");
            }
        }

        private static string DescribeBytes(long bytes)
        {
            try { return DiagnosticsBridge.DescribeBytes?.Invoke(bytes) ?? $"{bytes} bytes"; }
            catch { return $"{bytes} bytes"; }
        }

        // ── Saved logs group ─────────────────────────────────────────────

        private void DiagBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsBridge.OpenSavedLogs == null)
            {
                DiagnosticsBridge.Speak?.Invoke("Saved diagnostic logs are not available.");
                return;
            }
            try { DiagnosticsBridge.OpenSavedLogs.Invoke(); }
            catch { DiagnosticsBridge.Speak?.Invoke("Saved diagnostic logs could not be opened."); }
        }

        private void DiagProblemReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsBridge.SaveProblemReport == null)
            {
                DiagnosticsBridge.Speak?.Invoke("The problem report bundle is not available.");
                return;
            }
            try { DiagnosticsBridge.SaveProblemReport.Invoke(); }
            catch { DiagnosticsBridge.Speak?.Invoke("The problem report bundle could not be saved."); }
            RefreshDiagnosticsTab();
        }

        // ── Disk space group ─────────────────────────────────────────────

        private void DiagMeasureButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsBridge.Speak?.Invoke("Measuring.");
            string storage = SafeCall(DiagnosticsBridge.DescribeStorage);
            string crashes = SafeCall(DiagnosticsBridge.DescribeCrashReports);

            if (!string.IsNullOrEmpty(storage))
            {
                DiagStorageText.Text = storage;
                DiagStorageText.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, storage);
            }
            if (!string.IsNullOrEmpty(crashes))
            {
                DiagCrashText.Text = crashes;
                DiagCrashText.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, crashes);
            }
            DiagnosticsBridge.Speak?.Invoke(storage + " " + crashes);
        }

        private void DiagDeleteLooseButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsBridge.DeleteLooseLogs == null)
            {
                DiagnosticsBridge.Speak?.Invoke("That is not available.");
                return;
            }
            var answer = MessageBox.Show(this,
                "Delete the loose log text files in the settings folder?\r\n\r\n" +
                "The compressed sessions in Saved Diagnostic Logs are not touched, so nothing is lost. " +
                "This includes files newer than the automatic one-day window.",
                "Delete loose log text",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                DiagnosticsBridge.Speak?.Invoke("Nothing deleted.");
                return;
            }

            try
            {
                var (files, bytes) = DiagnosticsBridge.DeleteLooseLogs.Invoke();
                DiagnosticsBridge.Speak?.Invoke(files == 0
                    ? "There were no loose log text files to delete."
                    : $"Deleted {files} log {(files == 1 ? "file" : "files")}, about {DescribeBytes(bytes)} reclaimed.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke("The loose log files could not be deleted.");
            }
            DiagMeasureButton_Click(sender, e);
        }

        private void DiagDeleteSentCrashButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsBridge.DeleteResolvedCrashReports == null)
            {
                DiagnosticsBridge.Speak?.Invoke("That is not available.");
                return;
            }
            var answer = MessageBox.Show(this,
                "Delete the crash reports you have already sent or dismissed?\r\n\r\n" +
                "A report you have never answered about is kept, because support may still ask you for it.",
                "Delete crash reports",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                DiagnosticsBridge.Speak?.Invoke("Nothing deleted.");
                return;
            }

            try
            {
                var (files, bytes) = DiagnosticsBridge.DeleteResolvedCrashReports.Invoke();
                DiagnosticsBridge.Speak?.Invoke(files == 0
                    ? "There were no sent or dismissed crash reports to delete."
                    : $"Deleted {files} crash {(files == 1 ? "report" : "reports")}, about {DescribeBytes(bytes)} reclaimed.");
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke("The crash reports could not be deleted.");
            }
            DiagMeasureButton_Click(sender, e);
        }
    }
}
