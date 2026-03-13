using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Radios;

namespace JJFlexWpf.Dialogs
{
    public partial class AboutDialog : JJFlexDialog
    {
        /// <summary>The connected radio, or null if not connected.</summary>
        public FlexBase? Rig { get; set; }

        /// <summary>Callback to launch connection tester for the current radio.</summary>
        public Action? LaunchConnectionTest { get; set; }

        /// <summary>Screen reader speak callback.</summary>
        public Action<string, bool>? SpeakCallback { get; set; }

        public AboutDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PopulateAboutTab();
            PopulateRadioTab();
            PopulateSystemTab();
            PopulateDiagnosticsTab();

            ConnectionTestButton.IsEnabled = Rig != null && Rig.IsConnected;
        }

        private void PopulateAboutTab()
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown";
            var sb = new StringBuilder();
            sb.AppendLine("JJ Flexible Radio Access");
            sb.AppendLine($"Version {version}");
            sb.AppendLine("Copyright 2024-2026");
            sb.AppendLine();
            sb.AppendLine("Originally created by Jim Shaffer");
            sb.AppendLine("Maintained by Noel Romey, K5NER");
            sb.AppendLine();
            sb.AppendLine("JJ Flexible Radio Access is an accessible alternative to SmartSDR for controlling FlexRadio 6000 and 8000 series transceivers. Designed for screen reader users.");
            sb.AppendLine();
            sb.AppendLine("Library versions:");

            var entryAsm = Assembly.GetEntryAssembly();
            if (entryAsm != null)
            {
                string[] dllNames = { "flexlib", "jjloglib", "radios", "radioboxes", "jjflexwpf", "jjtrace" };
                foreach (var an in entryAsm.GetReferencedAssemblies())
                {
                    if (Array.Exists(dllNames, d => string.Equals(d, an.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Try FileVersionInfo for accurate versions (assembly version can be 0.0.0.0)
                        string ver = an.Version?.ToString() ?? "?";
                        try
                        {
                            var loaded = Assembly.Load(an);
                            if (!string.IsNullOrEmpty(loaded.Location) && File.Exists(loaded.Location))
                            {
                                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(loaded.Location);
                                ver = fvi.ProductVersion ?? fvi.FileVersion ?? ver;
                            }
                        }
                        catch { /* use assembly version as fallback */ }
                        sb.AppendLine($"  {an.Name}: {ver}");
                    }
                }
            }

            PopulateListBox(AboutList, sb.ToString());
        }

        private void PopulateRadioTab()
        {
            if (Rig == null || !Rig.IsConnected)
            {
                PopulateListBox(RadioList, "Not connected to a radio.\r\n\r\nConnect to a radio to see its details here.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Radio: {Rig.RadioModel}");
            sb.AppendLine($"Serial: {Rig.ConnectedSerial}");
            sb.AppendLine($"Nickname: {Rig.RadioNickname}");
            sb.AppendLine();

            // Firmware version — decode from FlexLib's packed ulong
            try
            {
                var infoMethod = Rig.GetType().GetMethod("infoForAbout",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (infoMethod != null)
                {
                    var info = infoMethod.Invoke(Rig, null) as System.Collections.Generic.List<string>;
                    if (info != null)
                    {
                        foreach (var line in info)
                        {
                            if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine($"Firmware: {line.Substring(8)}");
                            else if (line.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine($"IP Address: {line.Substring(3)}");
                        }
                    }
                }
            }
            catch { /* reflection fallback — not critical */ }

            sb.AppendLine();
            sb.AppendLine($"Active slices: {Rig.TotalNumSlices} of {Rig.MaxSlices}");
            sb.AppendLine($"Diversity: {(Rig.DiversityHardwareSupported ? "Available" : "Not available")}");

            PopulateListBox(RadioList, sb.ToString());
        }

        private void PopulateSystemTab()
        {
            var sb = new StringBuilder();
            sb.AppendLine($".NET Runtime: {Environment.Version}");
            sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
            sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine();

            // FlexLib version — use FileVersionInfo (assembly version may be 0.0.0.0)
            try
            {
                var flexAsm = Assembly.Load("FlexLib");
                var flexPath = flexAsm.Location;
                if (!string.IsNullOrEmpty(flexPath) && File.Exists(flexPath))
                {
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(flexPath);
                    sb.AppendLine($"FlexLib: {fvi.ProductVersion ?? fvi.FileVersion ?? "Unknown"}");
                }
                else
                {
                    sb.AppendLine($"FlexLib: {flexAsm.GetName().Version}");
                }
            }
            catch
            {
                sb.AppendLine("FlexLib: Unknown");
            }

            // WebView2 version
            try
            {
                var wv2Version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                sb.AppendLine($"WebView2: {wv2Version}");
            }
            catch
            {
                sb.AppendLine("WebView2: Not available");
            }

            sb.AppendLine();

            // Screen reader
            var srName = ScreenReaderOutput.ScreenReaderName;
            if (!string.IsNullOrEmpty(srName))
                sb.AppendLine($"Screen reader: {srName} detected");
            else if (ScreenReaderOutput.IsAvailable)
                sb.AppendLine("Screen reader: SAPI (no screen reader detected)");
            else
                sb.AppendLine("Screen reader: None detected");

            if (ScreenReaderOutput.HasBraille)
                sb.AppendLine("Braille: Available");

            PopulateListBox(SystemList, sb.ToString());
        }

        private void PopulateDiagnosticsTab()
        {
            var sb = new StringBuilder();
            if (Rig != null && Rig.IsConnected)
            {
                sb.AppendLine($"Connection: Active — {Rig.RadioModel}");
                sb.AppendLine($"Serial: {Rig.ConnectedSerial}");
            }
            else
            {
                sb.AppendLine("Connection: Not connected");
            }

            sb.AppendLine();
            sb.AppendLine("Use the buttons below to check for updates or test your connection.");

            PopulateListBox(DiagnosticsList, sb.ToString());
        }

        private string BuildFullReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== JJ Flexible Radio Access — Diagnostic Report ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("--- About ---");
            sb.AppendLine(ListBoxToText(AboutList));
            sb.AppendLine();
            sb.AppendLine("--- Radio ---");
            sb.AppendLine(ListBoxToText(RadioList));
            sb.AppendLine();
            sb.AppendLine("--- System ---");
            sb.AppendLine(ListBoxToText(SystemList));
            sb.AppendLine();
            sb.AppendLine("--- Diagnostics ---");
            sb.AppendLine(ListBoxToText(DiagnosticsList));
            return sb.ToString();
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            SpeakCallback?.Invoke("Checking for updates", true);

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("JJFlexibleRadioAccess");
                client.Timeout = TimeSpan.FromSeconds(10);

                var response = await client.GetStringAsync(
                    "https://api.github.com/repos/nromey/JJFlex-NG/releases/latest");

                using var doc = JsonDocument.Parse(response);
                var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";

                // Strip leading 'v' if present
                var latestVersion = tagName.TrimStart('v');
                var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

                // Compare versions
                if (Version.TryParse(latestVersion, out var latest) &&
                    Version.TryParse(currentVersion, out var current))
                {
                    if (latest > current)
                    {
                        var msg = $"Update available: version {latestVersion} (you have {currentVersion})";
                        DiagnosticsList.Items.Add(msg);
                        SpeakCallback?.Invoke(msg, true);
                    }
                    else
                    {
                        var msg = $"You're up to date (version {currentVersion})";
                        DiagnosticsList.Items.Add(msg);
                        SpeakCallback?.Invoke(msg, true);
                    }
                }
                else
                {
                    var msg = $"Latest release: {latestVersion}, current: {currentVersion}";
                    DiagnosticsList.Items.Add(msg);
                    SpeakCallback?.Invoke(msg, true);
                }
            }
            catch (Exception ex)
            {
                var msg = $"Could not check for updates: {ex.Message}";
                DiagnosticsList.Items.Add(msg);
                SpeakCallback?.Invoke("Could not check for updates", true);
            }
        }

        private void ConnectionTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (LaunchConnectionTest != null)
            {
                LaunchConnectionTest();
            }
            else
            {
                SpeakCallback?.Invoke("Connection test not available", true);
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var report = BuildFullReport();
            Clipboard.SetText(report);
            SpeakCallback?.Invoke("Copied to clipboard", true);
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                FileName = $"JJFlex-Diagnostic-{DateTime.Now:yyyy-MM-dd}.txt",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Export Diagnostic Report"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var report = BuildFullReport();
                    File.WriteAllText(dialog.FileName, report);
                    SpeakCallback?.Invoke("Diagnostic report saved", true);
                }
                catch (Exception ex)
                {
                    SpeakCallback?.Invoke($"Could not save report: {ex.Message}", true);
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Populate a ListBox with lines from a multi-line string.
        /// Each non-empty line becomes a ListBoxItem — screen readers announce
        /// each item on arrow up/down, unlike TextBox which has UIA issues.
        /// </summary>
        private static void PopulateListBox(System.Windows.Controls.ListBox listBox, string text)
        {
            listBox.Items.Clear();
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    listBox.Items.Add(line.Trim());
            }
        }

        /// <summary>
        /// Reconstruct text from a ListBox for clipboard/export.
        /// </summary>
        private static string ListBoxToText(System.Windows.Controls.ListBox listBox)
        {
            var sb = new StringBuilder();
            foreach (var item in listBox.Items)
                sb.AppendLine(item.ToString());
            return sb.ToString().TrimEnd();
        }
    }
}
