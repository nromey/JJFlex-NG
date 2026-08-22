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
                // The problem count is live for the same reason the log state
                // is: a failure can happen while this tab is open, and a count
                // that was true when the tab was drawn is exactly the kind of
                // cached fiction the retired trace dialog shipped.
                ProblemLog.Changed += OnDiagnosticStateChanged;
                Closed += (_, _) =>
                {
                    DiagnosticsBridge.StateChanged -= OnDiagnosticStateChanged;
                    ProblemLog.Changed -= OnDiagnosticStateChanged;
                };
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
            try
            {
                // The problem count leads when there IS one. Someone arriving on
                // this tab has almost always come because something went wrong,
                // and "2 problems recorded this session" answers that before the
                // log's own state does.
                string spoken = DiagnosticsBridge.State();
                if (ProblemLog.Count > 0) spoken = ProblemLog.Summary() + ". " + spoken;
                DiagnosticsBridge.Speak?.Invoke(spoken);
            }
            catch { }
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

                bool meterStream = false;
                try { meterStream = DiagnosticsBridge.MeterStream?.Invoke() ?? false; } catch { }
                DiagMeterStreamCheck.IsChecked = meterStream;

                bool transcript = false;
                try { transcript = DiagnosticsBridge.SpokenTranscript?.Invoke() ?? false; } catch { }
                DiagTranscriptCheck.IsChecked = transcript;

                string live = SafeCall(DiagnosticsBridge.LiveLogPath);
                string folder = SafeCall(DiagnosticsBridge.LogFolder);
                if (!string.IsNullOrEmpty(live))
                {
                    // The real, resolved path — not the %AppData% template.
                    // An operator asked for "the trace" has to be able to produce
                    // it without sighted help and without knowing AppData folklore.
                    DiagPathText.Text = Radios.Lexicon.Get("settings.diagnostics.live_log_path",
                        ("live", live));
                }
                else if (!string.IsNullOrEmpty(folder))
                {
                    DiagPathText.Text = Radios.Lexicon.Get("settings.diagnostics.log_folder_when_running",
                        ("folder", folder));
                }
                else
                {
                    DiagPathText.Text = Radios.Lexicon.Get("settings.diagnostics.log_location_unavailable");
                }
                DiagPathText.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                    DiagPathText.Text);

                UpdateProblemCount();
                UpdateCaptureButton();
            }
            finally
            {
                _diagSuppressEvents = false;
            }
        }

        /// <summary>
        /// The problem count, and whether there is anything to open.
        ///
        /// This is the discoverability half of #100: the chord is the fast door,
        /// this is the one an operator finds without being told a key exists.
        /// The button is COLLAPSED rather than disabled when the list is empty —
        /// a disabled control stays in the tab order, and there is nothing
        /// behind it to reach.
        /// </summary>
        private void UpdateProblemCount()
        {
            if (DiagProblemCountText == null) return;

            int n = ProblemLog.Count;
            DiagProblemCountText.Text = n == 0
                ? Radios.Lexicon.Get("settings.diagnostics.no_problems")
                : Radios.Lexicon.Get("settings.diagnostics.problems_summary",
                    ("summary", ProblemLog.Summary()));
            System.Windows.Automation.AutomationProperties.SetName(
                DiagProblemCountText, DiagProblemCountText.Text);

            DiagShowProblemsButton.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void DiagShowProblemsButton_Click(object sender, RoutedEventArgs e)
        {
            // Same door as the chord, so the two can never disagree about what
            // an empty list means or what the window says.
            ProblemsDialog.ShowOrSpeakEmpty(this);
        }

        /// <summary>
        /// Content AND accessible name change together, always. The frozen
        /// AutomationProperties.Name on the retired dialog's button — same words
        /// in both states — is the anti-pattern this surface exists to replace.
        /// </summary>
        private void UpdateCaptureButton()
        {
            bool capturing = DiagnosticsBridge.Capturing();
            string label = capturing
                ? Radios.Lexicon.Get("settings.diagnostics.capture_stop")
                : Radios.Lexicon.Get("settings.diagnostics.capture_start");
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

            // #128 sweep audit (2026-08-21): this checkbox applies immediately
            // (settings are intents), so it answers back like every other live
            // toggle — it is not covered by the batched-on-OK exception. Tone
            // before the sentence, which is long by design.
            EarconPlayer.ToggleTone(keep);

            // Turning it OFF speaks a consequence, not just a state. "Off" tells
            // the operator what they pressed; it does not tell them what they
            // have given up, and this is the one setting whose cost only shows
            // up later, when something has already gone wrong.
            DiagnosticsBridge.Speak?.Invoke(keep
                ? Radios.Lexicon.Get("settings.diagnostics.log_on", ("detail", DetailWord()))
                : Radios.Lexicon.Get("settings.diagnostics.log_off"));
        }

        private void DiagDetailRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_diagSuppressEvents) return;
            bool keep = DiagKeepLogCheckbox.IsChecked == true;
            ApplyDiagnosticChoice(keep, CurrentDetailChoice());
            if (keep)
                DiagnosticsBridge.Speak?.Invoke(
                    Radios.Lexicon.Get("settings.diagnostics.log_on", ("detail", DetailWord())));
            else
                DiagnosticsBridge.Speak?.Invoke(
                    Radios.Lexicon.Get("settings.diagnostics.detail_for_later", ("detail", DetailWord())));
        }

        private int CurrentDetailChoice() => DiagDetailDetailedRadio.IsChecked == true ? 1 : 0;

        private string DetailWord() => CurrentDetailChoice() == 1
            ? Radios.Lexicon.Get("settings.diagnostics.detail_detailed")
            : Radios.Lexicon.Get("settings.diagnostics.detail_normal");

        private void ApplyDiagnosticChoice(bool keep, int detail)
        {
            try { DiagnosticsBridge.ApplySettings?.Invoke(keep, detail); }
            catch { DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.setting_not_saved")); }
        }

        /// <summary>
        /// The bench-session meter stream switch (task #170). Speaks what was
        /// actually chosen and what it costs, both ways: "on" names the once-a-
        /// second summary so the operator knows the log will not balloon the
        /// way the old raw stream did, and "off" says the readings stop, so
        /// meter lines ending mid-bench is never a mystery.
        /// </summary>
        private void DiagTranscriptCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_diagSuppressEvents) return;
            bool record = DiagTranscriptCheck.IsChecked == true;
            try { DiagnosticsBridge.ApplySpokenTranscript?.Invoke(record); }
            catch { DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.setting_not_saved")); return; }

            // #128: immediate-apply toggle answers back; the failed path above
            // returns first, so a declined change never chimes.
            EarconPlayer.ToggleTone(record);

            // Turning it ON says where it went, because the operator's next
            // move is to find that file and attach it. Turning it OFF says the
            // transcript is closed rather than merely "off", so nobody assumes
            // a half-written file is still growing.
            DiagnosticsBridge.Speak?.Invoke(record
                ? Radios.Lexicon.Get("settings.diagnostics.transcript_on")
                : Radios.Lexicon.Get("settings.diagnostics.transcript_off"));
        }

        private void DiagMeterStreamCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_diagSuppressEvents) return;
            bool record = DiagMeterStreamCheck.IsChecked == true;
            try { DiagnosticsBridge.ApplyMeterStream?.Invoke(record); }
            catch { DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.setting_not_saved")); return; }

            // #128: immediate-apply toggle answers back; the failed-save path
            // above returns before this, so a declined change never chimes.
            EarconPlayer.ToggleTone(record);
            DiagnosticsBridge.Speak?.Invoke(record
                ? Radios.Lexicon.Get("settings.diagnostics.meter_stream_on")
                : Radios.Lexicon.Get("settings.diagnostics.meter_stream_off"));
        }

        private void DiagCopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            string live = SafeCall(DiagnosticsBridge.LiveLogPath);
            if (string.IsNullOrEmpty(live))
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.no_live_log_to_copy"));
                return;
            }
            try
            {
                Clipboard.SetText(live);
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.path_copied"));
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.path_copy_failed"));
            }
        }

        private void DiagOpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = SafeCall(DiagnosticsBridge.LogFolder);
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.folder_not_found"));
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.folder_opened"));
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.folder_open_failed"));
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
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.capture_missing"));
                UpdateCaptureButton();
                return;
            }

            long bytes = 0;
            try { bytes = new System.IO.FileInfo(archive).Length; } catch { }
            string size = DescribeBytes(bytes);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Radios.Lexicon.Get("settings.diagnostics.export_filter"),
                DefaultExt = "zip",
                FileName = System.IO.Path.GetFileName(archive),
                Title = Radios.Lexicon.Get("settings.diagnostics.export_title")
            };
            if (dlg.ShowDialog(this) != true)
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.export_cancelled"));
                return;
            }

            try
            {
                System.IO.File.Copy(archive, dlg.FileName, overwrite: true);
                // Size is spoken so an upload never surprises anyone — the same
                // rule the feedback picker sets.
                DiagnosticsBridge.Speak?.Invoke(
                    Radios.Lexicon.Get("settings.diagnostics.export_done", ("size", size)));
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.export_failed"));
            }
        }

        private static string DescribeBytes(long bytes)
        {
            try
            {
                return DiagnosticsBridge.DescribeBytes?.Invoke(bytes)
                    ?? Radios.Lexicon.Get("settings.diagnostics.bytes_fallback", ("bytes", bytes));
            }
            catch { return Radios.Lexicon.Get("settings.diagnostics.bytes_fallback", ("bytes", bytes)); }
        }

        // ── Saved logs group ─────────────────────────────────────────────

        private void DiagBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsBridge.OpenSavedLogs == null)
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.saved_logs_unavailable"));
                return;
            }
            try { DiagnosticsBridge.OpenSavedLogs.Invoke(); }
            catch { DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.saved_logs_open_failed")); }
        }

        private void DiagProblemReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsBridge.SaveProblemReport == null)
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.problem_report_unavailable"));
                return;
            }
            try { DiagnosticsBridge.SaveProblemReport.Invoke(); }
            catch { DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.problem_report_failed")); }
            RefreshDiagnosticsTab();
        }

        // ── Disk space group ─────────────────────────────────────────────

        private void DiagMeasureButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.measuring"));
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
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.not_available"));
                return;
            }
            var answer = MessageBox.Show(this,
                Radios.Lexicon.Get("settings.diagnostics.delete_loose_body"),
                Radios.Lexicon.Get("settings.diagnostics.delete_loose_title"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.nothing_deleted"));
                return;
            }

            try
            {
                var (files, bytes) = DiagnosticsBridge.DeleteLooseLogs.Invoke();
                DiagnosticsBridge.Speak?.Invoke(files == 0
                    ? Radios.Lexicon.Get("settings.diagnostics.no_loose_logs")
                    : Radios.Lexicon.Get("settings.diagnostics.loose_logs_deleted",
                        ("files", files),
                        ("fileWord", files == 1
                            ? Radios.Lexicon.Get("settings.diagnostics.word_file")
                            : Radios.Lexicon.Get("settings.diagnostics.word_files")),
                        ("size", DescribeBytes(bytes))));
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.loose_logs_delete_failed"));
            }
            DiagMeasureButton_Click(sender, e);
        }

        private void DiagDeleteSentCrashButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticsBridge.DeleteResolvedCrashReports == null)
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.not_available"));
                return;
            }
            var answer = MessageBox.Show(this,
                Radios.Lexicon.Get("settings.diagnostics.delete_crash_body"),
                Radios.Lexicon.Get("settings.diagnostics.delete_crash_title"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.nothing_deleted"));
                return;
            }

            try
            {
                var (files, bytes) = DiagnosticsBridge.DeleteResolvedCrashReports.Invoke();
                DiagnosticsBridge.Speak?.Invoke(files == 0
                    ? Radios.Lexicon.Get("settings.diagnostics.no_resolved_crash_reports")
                    : Radios.Lexicon.Get("settings.diagnostics.crash_reports_deleted",
                        ("files", files),
                        ("reportWord", files == 1
                            ? Radios.Lexicon.Get("settings.diagnostics.word_report")
                            : Radios.Lexicon.Get("settings.diagnostics.word_reports")),
                        ("size", DescribeBytes(bytes))));
            }
            catch
            {
                DiagnosticsBridge.Speak?.Invoke(Radios.Lexicon.Get("settings.diagnostics.crash_reports_delete_failed"));
            }
            DiagMeasureButton_Click(sender, e);
        }
    }
}
