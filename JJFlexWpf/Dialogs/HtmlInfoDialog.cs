using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Long-form reader for structured reference content, hosted in WebView2 so the
/// screen reader gets a real document to browse.
///
/// <see cref="AdvisoryDialog"/> is still the right answer for plain prose that
/// fits on a screen: its text box is arrow-reviewable and costs nothing to open.
/// This dialog earns its extra weight only when the content has *structure* —
/// several sections a user will want to jump between. In browse mode the H key
/// moves heading to heading, which a flat text box cannot offer at any length.
/// Length alone is not the test; structure is.
///
/// Content is always locally generated markdown rendered by
/// <see cref="HelpMarkdown"/> — nothing remote is ever fetched into this dialog.
/// Links to the outside world open in the user's real browser instead, where
/// they have their own screen reader setup and their own back button.
///
/// The WebView2 runtime is not guaranteed present, so <see cref="Show"/> probes
/// for it and falls back to the same content as plain text in an AdvisoryDialog.
/// A missing browser runtime should cost the user formatting, not the document.
/// </summary>
public sealed class HtmlInfoDialog : JJFlexDialog
{
    private readonly WebView2 _web;
    private readonly string _html;
    private Action? _chosenAction;
    private bool _initFailed;

    /// <summary>
    /// Escape has to be caught inside the document. The WebView2 island keeps
    /// keystrokes typed in the page away from WPF, so the base dialog's Escape
    /// handler never sees them — which would leave a user whose focus is in the
    /// text with no way out, breaking the project rule that every dialog closes
    /// on Escape. The listener is registered on document creation, before any
    /// page content exists, so there is no window where Escape is dead.
    /// </summary>
    private const string EscapeScript = @"
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
        window.chrome.webview.postMessage('jjflex:close');
    }
}, true);";

    /// <summary>
    /// Put the caret in the document itself rather than leaving it on the
    /// container. NVDA then starts its virtual cursor at the top heading and
    /// reads down, instead of announcing an unhelpful embedded-object wrapper.
    /// </summary>
    private const string FocusDocumentScript = @"
(function () {
    var target = document.querySelector('h1') || document.body;
    if (!target) return;
    target.setAttribute('tabindex', '-1');
    target.focus();
})();";

    private HtmlInfoDialog(string title, string html, Window? owner,
        IReadOnlyList<AdvisoryDialog.AdvisoryAction> actions)
    {
        Title = title;

        // The base class owns this to the main window, which is right for a
        // top-level dialog but wrong when this one opens over another modal — the
        // dialog underneath could surface on top of the document the user just
        // asked for. Re-point the owner when a caller names one.
        if (owner != null)
        {
            try
            {
                var handle = new WindowInteropHelper(owner).Handle;
                if (handle != nint.Zero)
                    new WindowInteropHelper(this).Owner = handle;
            }
            catch { /* non-critical — modality still holds, only z-order suffers */ }
        }

        Width = 660;
        Height = 580;
        // Reference material gets read at the user's preferred size, unlike the
        // fixed-size advisories — long documents are the case where resizing and
        // zooming actually matter.
        ResizeMode = ResizeMode.CanResize;
        _html = html;

        var root = new DockPanel { Margin = new Thickness(12) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        foreach (var action in actions)
        {
            var button = new Button
            {
                Content = action.Label,
                MinWidth = 110,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
            };
            AutomationProperties.SetName(button, action.Label.Replace("_", ""));
            button.Click += (_, _) =>
            {
                // Same reasoning as AdvisoryDialog: let this dialog close before
                // the action runs, so whatever it opens owns the foreground.
                _chosenAction = action.OnChosen;
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(button);
        }

        var close = new Button
        {
            Content = "_Close",
            MinWidth = 80,
            Height = 28,
            IsDefault = true,
            IsCancel = true,
        };
        AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) =>
        {
            if (DialogResult == null) DialogResult = false;
            Close();
        };
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        _web = new WebView2();
        AutomationProperties.SetName(_web, title);
        root.Children.Add(_web);

        Content = root;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// The base class focuses the first control in tab order on load; here that
    /// would be a button, and the user would have to find their way back into
    /// the document. Focus is placed in the document instead, once the content
    /// has actually rendered — see <see cref="OnNavigationCompleted"/>.
    /// </summary>
    protected override void FocusFirstControl()
    {
        // Deliberately empty: the document is not focusable until WebView2 has
        // finished starting, so focus is placed later rather than here.
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Shares AboutDialog's user-data folder deliberately: one folder means
            // one browser process for all of the app's document views.
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JJFlexRadio", "WebView2");

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            await _web.EnsureCoreWebView2Async(env);

            var settings = _web.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            // Browser accelerators stay enabled on purpose: Ctrl+F to find in a
            // reference document, and Ctrl+plus to zoom, are worth keeping.

            _web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _web.NavigationCompleted += OnNavigationCompleted;

            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EscapeScript);
            _web.CoreWebView2.NavigateToString(_html);
        }
        catch (Exception ex)
        {
            // Nothing readable has been shown yet, so close and let Show() put the
            // same content up as plain text rather than stranding the user in an
            // empty window.
            System.Diagnostics.Trace.WriteLine($"HtmlInfoDialog: WebView2 init failed: {ex.Message}");
            _initFailed = true;
            // Never a bare DialogResult assignment from a Loaded path: that
            // threw on windows realised with Show() and aborted the Tier 1
            // dialog suite on 2026-08-20/21 — see JJFlexDialog.CloseWithResult
            // (#159). Worse here: this handler is async, so the throw would
            // surface as an unhandled dispatcher exception.
            if (DialogResult == null) CloseWithResult(false);
            else Close();
        }
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Only the document we generated is allowed to render in here. A link to
        // the outside world opens in the user's browser, which is both safer and
        // where they expect to end up.
        if (e.Uri == null) return;
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        if (e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"HtmlInfoDialog: could not open {e.Uri}: {ex.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.TryGetWebMessageAsString() != "jjflex:close") return;
        }
        catch (ArgumentException)
        {
            return; // not a string message — nothing we sent
        }

        if (DialogResult == null) DialogResult = false;
        Close();
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            _web.Focus();
            await _web.CoreWebView2.ExecuteScriptAsync(FocusDocumentScript);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"HtmlInfoDialog: focus handoff failed: {ex.Message}");
        }
    }

    /// <summary>True when the WebView2 runtime is present and this dialog can render.</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Show <paramref name="markdown"/> as a browsable document, falling back to
    /// plain text in an <see cref="AdvisoryDialog"/> when WebView2 is missing or
    /// fails to start. Any chosen action runs after the dialog closes.
    ///
    /// Pass <paramref name="owner"/> when opening this over another modal dialog —
    /// reference material consulted while answering a question should sit on top of
    /// the question, and hand focus back to it on close.
    /// </summary>
    public static void Show(string title, string markdown, Window? owner = null,
        params AdvisoryDialog.AdvisoryAction[] actions)
    {
        if (IsAvailable)
        {
            var dialog = new HtmlInfoDialog(title, HelpMarkdown.ToHtml(markdown, title), owner, actions);
            dialog.ShowModalDialog();

            if (!dialog._initFailed)
            {
                dialog._chosenAction?.Invoke();
                return;
            }
        }

        AdvisoryDialog.Show(title, HelpMarkdown.ToPlainText(markdown), null, actions);
    }

    /// <summary>
    /// Show content that is ALREADY HTML — a saved Fixer run's report is the
    /// founding case: its HTML form was rendered by FixerReport when the run
    /// was recorded, and pushing it through the markdown pipeline would be a
    /// second renderer wearing a disguise. The caller supplies the plain-text
    /// form for the no-WebView2 fallback, for the same reason: both forms
    /// already exist, made by one renderer, and must not be re-derived here.
    /// </summary>
    public static void ShowHtml(string title, string html, string plainTextFallback,
        Window? owner = null, params AdvisoryDialog.AdvisoryAction[] actions)
    {
        if (IsAvailable)
        {
            var dialog = new HtmlInfoDialog(title, html, owner, actions);
            dialog.ShowModalDialog();

            if (!dialog._initFailed)
            {
                dialog._chosenAction?.Invoke();
                return;
            }
        }

        AdvisoryDialog.Show(title, plainTextFallback, null, actions);
    }
}
