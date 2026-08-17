using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
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

        private bool _webViewReady;
        private bool _fallbackActive;
        // Plain-text versions of each tab for clipboard/export
        private readonly string[] _plainText = new string[4];

        /// <summary>
        /// Everything the System tab and the version lines report, captured
        /// once per dialog load. The SAME structure the crash reporter and the
        /// debug bundle embed — one assembler, so the page and the reports can
        /// never disagree about what is running.
        /// </summary>
        private DiagnosticSnapshot _snapshot;

        /// <summary>
        /// Escape must be caught inside the document: the WebView2 island keeps
        /// keystrokes typed in the page away from WPF, so the Close button's
        /// IsCancel never fires while focus is in the content — which would
        /// strand a user whose focus is in the text, breaking the project rule
        /// that every dialog closes on Escape. Same pattern as HtmlInfoDialog
        /// (the canonical copy of this script lives there too).
        /// </summary>
        private const string EscapeScript = @"
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
        window.chrome.webview.postMessage('jjflex:close');
    }
}, true);";

        public AboutDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            ConnectionTestButton.IsEnabled = Rig != null && Rig.IsConnected;

            // Build all tab content (plain text + HTML)
            BuildAllContent();

            // Initialize WebView2
            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JJFlexRadio", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await ContentWebView.EnsureCoreWebView2Async(env);

                // Security: minimal JS (needed for dynamic status updates), no dev tools
                ContentWebView.CoreWebView2.Settings.IsScriptEnabled = true;
                ContentWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                ContentWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                // Intercept external navigation — open in browser instead,
                // except for jjflex:// scheme which routes to in-app destinations.
                ContentWebView.CoreWebView2.NavigationStarting += (s, args) =>
                {
                    if (args.Uri != null && !args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        && !args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Cancel = true;
                        if (args.Uri.StartsWith("jjflex://", StringComparison.OrdinalIgnoreCase))
                        {
                            HandleJJFlexUri(args.Uri);
                            return;
                        }
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri) { UseShellExecute = true }); }
                        catch { }
                    }
                };

                // Close on Escape even when focus is inside the document.
                ContentWebView.CoreWebView2.WebMessageReceived += (s, args) =>
                {
                    try
                    {
                        if (args.TryGetWebMessageAsString() == "jjflex:close") Close();
                    }
                    catch (ArgumentException) { /* not a string message — nothing we sent */ }
                };
                await ContentWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EscapeScript);

                _webViewReady = true;
                LoadingLabel.Visibility = Visibility.Collapsed;
                ContentWebView.Visibility = Visibility.Visible;

                // Show the first tab
                ShowTab(0);
            }
            catch (Exception ex)
            {
                // No WebView2 runtime (or it failed to start). The user still
                // gets every fact, as selectable plain text — a missing browser
                // runtime costs formatting and browse-mode navigation, never
                // the content itself.
                System.Diagnostics.Trace.WriteLine($"AboutDialog WebView2 init failed: {ex.Message}");
                _fallbackActive = true;
                LoadingLabel.Visibility = Visibility.Collapsed;
                FallbackText.Visibility = Visibility.Visible;
                ShowTab(AboutTabs.SelectedIndex < 0 ? 0 : AboutTabs.SelectedIndex);
            }
        }

        private void AboutTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_webViewReady && !_fallbackActive) return;
            ShowTab(AboutTabs.SelectedIndex);
        }

        private void ShowTab(int index)
        {
            if (index < 0 || index >= 4) return;

            // Show/hide diagnostics buttons
            DiagButtonsPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (_webViewReady)
            {
                // Load HTML content
                string html = index switch
                {
                    0 => BuildAboutHtml(),
                    1 => BuildRadioHtml(),
                    2 => BuildSystemHtml(),
                    3 => BuildDiagnosticsHtml(),
                    _ => ""
                };
                ContentWebView.NavigateToString(html);
            }
            else if (_fallbackActive)
            {
                // Plain fallback: same facts, same source, no browser runtime.
                FallbackText.Text = _plainText[index];
            }
        }

        #region Content Building

        private void BuildAllContent()
        {
            // One snapshot per refresh: every version and support fact below is
            // queried live from the running process, never typed in. This is
            // the same structure CrashReporter embeds in crash reports.
            _snapshot = DiagnosticSnapshot.Capture();

            // Pre-build plain text for all tabs (used by clipboard/export)
            _plainText[0] = BuildAboutPlainText();
            _plainText[1] = BuildRadioPlainText();
            _plainText[2] = BuildSystemPlainText();
            _plainText[3] = BuildDiagnosticsPlainText();
        }

        private string LoadHtmlTemplate(string resourceName)
        {
            var assembly = typeof(AboutDialog).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return $"<html><body><p>Template not found: {resourceName}</p></body></html>";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // --- About tab ---

        private string BuildAboutHtml()
        {
            var html = LoadHtmlTemplate("JJFlexWpf.Resources.AboutGeneral.html");
            // The snapshot's display version prefers the executable's 4-part
            // FileVersion — the build number tester zips and the NAS history
            // are keyed by — over the bare assembly version.
            html = html.Replace("{{Version}}", Escape(_snapshot?.AppDisplayVersion ?? "Unknown"));
            html = html.Replace("{{LibraryVersions}}", BuildLibraryVersionsHtml());
            return html;
        }

        private string BuildLibraryVersionsHtml()
        {
            var sb = new StringBuilder();
            var entryAsm = Assembly.GetEntryAssembly();
            if (entryAsm != null)
            {
                string[] dllNames = { "flexlib", "jjloglib", "radios", "radioboxes", "jjflexwpf", "jjtrace" };
                foreach (var an in entryAsm.GetReferencedAssemblies())
                {
                    if (Array.Exists(dllNames, d => string.Equals(d, an.Name, StringComparison.OrdinalIgnoreCase)))
                    {
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
                        catch { }
                        sb.AppendLine($"<li>{Escape(an.Name ?? "")}: {Escape(ver)}</li>");
                    }
                }
            }
            return sb.ToString();
        }

        private string BuildAboutPlainText()
        {
            var sb = new StringBuilder();
            var version = _snapshot?.AppDisplayVersion ?? "Unknown";
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
                        catch { }
                        sb.AppendLine($"  {an.Name}: {ver}");
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }

        // --- Radio tab ---

        private string BuildRadioHtml()
        {
            var html = LoadHtmlTemplate("JJFlexWpf.Resources.AboutRadio.html");
            html = html.Replace("{{RadioContent}}", BuildRadioContentHtml());
            return html;
        }

        private string BuildRadioContentHtml()
        {
            if (Rig == null || !Rig.IsConnected)
                return "<p>Not connected to a radio.</p><p>Connect to a radio to see its details here.</p>";

            var sb = new StringBuilder();
            sb.AppendLine($"<h2>Identity</h2>");
            sb.AppendLine($"<p>Radio: {Escape(Rig.RadioModel)}</p>");
            sb.AppendLine($"<p>Serial: {Escape(Rig.ConnectedSerial)}</p>");
            sb.AppendLine($"<p>Nickname: {Escape(Rig.RadioNickname)}</p>");

            // Firmware via reflection
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
                                sb.AppendLine($"<p>Firmware: {Escape(line.Substring(8).Trim())}</p>");
                            else if (line.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine($"<p>IP Address: {Escape(line.Substring(3).Trim())}</p>");
                        }
                    }
                }
            }
            catch { }

            sb.AppendLine("<h2>Capabilities</h2>");
            sb.AppendLine($"<p>Active slices: {Rig.TotalNumSlices} of {Rig.MaxSlices}</p>");
            sb.AppendLine($"<p>Diversity: {(Rig.DiversityHardwareSupported ? "Available" : "Not available")}</p>");

            return sb.ToString();
        }

        private string BuildRadioPlainText()
        {
            if (Rig == null || !Rig.IsConnected)
                return "Not connected to a radio.\r\n\r\nConnect to a radio to see its details here.";

            var sb = new StringBuilder();
            sb.AppendLine($"Radio: {Rig.RadioModel}");
            sb.AppendLine($"Serial: {Rig.ConnectedSerial}");
            sb.AppendLine($"Nickname: {Rig.RadioNickname}");

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
                                sb.AppendLine($"Firmware: {line.Substring(8).Trim()}");
                            else if (line.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine($"IP Address: {line.Substring(3).Trim()}");
                        }
                    }
                }
            }
            catch { }

            sb.AppendLine();
            sb.AppendLine($"Active slices: {Rig.TotalNumSlices} of {Rig.MaxSlices}");
            sb.AppendLine($"Diversity: {(Rig.DiversityHardwareSupported ? "Available" : "Not available")}");
            return sb.ToString().TrimEnd();
        }

        // --- System tab ---

        // The System tab is a pure rendering of DiagnosticSnapshot.Items — the
        // HTML and the plain text iterate the SAME sectioned list, so the page,
        // the clipboard, the exported report, and the crash reporter (which
        // embeds the same snapshot) agree by construction. The version-probing
        // helpers that used to live here moved into Radios.DiagnosticSnapshot,
        // where the crash reporter can reach them too.

        private string BuildSystemHtml()
        {
            var html = LoadHtmlTemplate("JJFlexWpf.Resources.AboutSystem.html");
            var sb = new StringBuilder();
            string section = null;
            if (_snapshot != null)
            {
                foreach (var item in _snapshot.Items)
                {
                    if (item.Section != section)
                    {
                        // Real headings: this is what browse mode's H key jumps
                        // between, and the reason this page is a web page.
                        sb.AppendLine($"<h2>{Escape(item.Section)}</h2>");
                        section = item.Section;
                    }
                    sb.AppendLine($"<p>{Escape(item.Label)}: {Escape(item.Value)}</p>");
                }
            }
            html = html.Replace("{{SystemContent}}", sb.ToString());
            return html;
        }

        private string BuildSystemPlainText()
        {
            return _snapshot?.ToPlainText() ?? "System information unavailable.";
        }

        // --- Diagnostics tab ---

        private string BuildDiagnosticsHtml()
        {
            var html = LoadHtmlTemplate("JJFlexWpf.Resources.AboutDiagnostics.html");
            html = html.Replace("{{ConnectionStatus}}", BuildConnectionStatusHtml());
            return html;
        }

        private string BuildConnectionStatusHtml()
        {
            if (Rig != null && Rig.IsConnected)
                return $"<p>Connection: Active — {Escape(Rig.RadioModel)}</p><p>Serial: {Escape(Rig.ConnectedSerial)}</p>";
            return "<p>Connection: Not connected</p>";
        }

        private string BuildDiagnosticsPlainText()
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
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Button Handlers

        private string BuildFullReport()
        {
            // Refresh plain text
            BuildAllContent();
            var sb = new StringBuilder();
            sb.AppendLine("=== JJ Flexible Radio Access — Diagnostic Report ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Version: {_snapshot?.AppDisplayVersion ?? "unknown"}");
            sb.AppendLine();
            sb.AppendLine("--- About ---");
            sb.AppendLine(_plainText[0]);
            sb.AppendLine();
            sb.AppendLine("--- Radio ---");
            sb.AppendLine(_plainText[1]);
            sb.AppendLine();
            sb.AppendLine("--- System ---");
            sb.AppendLine(_plainText[2]);
            sb.AppendLine();
            sb.AppendLine("--- Diagnostics ---");
            sb.AppendLine(_plainText[3]);
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
                var latestVersion = tagName.TrimStart('v');
                var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

                string msg;
                if (Version.TryParse(latestVersion, out var latest) &&
                    Version.TryParse(currentVersion, out var current))
                {
                    msg = latest > current
                        ? $"Update available: version {latestVersion} (you have {currentVersion})"
                        : $"You're up to date (version {currentVersion})";
                }
                else
                {
                    msg = $"Latest release: {latestVersion}, current: {currentVersion}";
                }

                SpeakCallback?.Invoke(msg, true);

                // Append to diagnostics HTML
                if (_webViewReady && AboutTabs.SelectedIndex == 3)
                    await ContentWebView.ExecuteScriptAsync(
                        $"document.getElementById('status').innerHTML += '<p>{EscapeJs(msg)}</p>'");
            }
            catch (Exception ex)
            {
                var msg = $"Could not check for updates: {ex.Message}";
                SpeakCallback?.Invoke("Could not check for updates", true);

                if (_webViewReady && AboutTabs.SelectedIndex == 3)
                    await ContentWebView.ExecuteScriptAsync(
                        $"document.getElementById('status').innerHTML += '<p>{EscapeJs(msg)}</p>'");
            }
        }

        private void ConnectionTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (LaunchConnectionTest != null)
                LaunchConnectionTest();
            else
                SpeakCallback?.Invoke("Connection test not available", true);
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            // Copy EVERYTHING — all four tabs in one report — so nobody reads
            // hex aloud on a support call. (This used to copy only the current
            // tab while its accessible name promised "all information"; the
            // behavior now matches the name.)
            try
            {
                Clipboard.SetText(BuildFullReport());
                SpeakCallback?.Invoke("Copied full report to clipboard", true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AboutDialog copy failed: {ex.Message}");
                SpeakCallback?.Invoke("Could not copy to clipboard", true);
            }
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

        private void WhatsNewButton_Click(object sender, RoutedEventArgs e)
        {
            HelpLauncher.ShowHelp("WhatsNew");
        }

        private void HandleJJFlexUri(string uri)
        {
            // jjflex://whats-new  -> open the What's New help topic
            if (uri.Equals("jjflex://whats-new", StringComparison.OrdinalIgnoreCase)
                || uri.Equals("jjflex://whats-new/", StringComparison.OrdinalIgnoreCase))
            {
                HelpLauncher.ShowHelp("WhatsNew");
            }
        }

        #endregion

        #region Helpers

        private static string Escape(string text)
        {
            return System.Net.WebUtility.HtmlEncode(text);
        }

        private static string EscapeJs(string text)
        {
            return text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"")
                       .Replace("\r", "").Replace("\n", "");
        }

        #endregion
    }
}
