using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Auth0 authentication dialog using WebView2 (Edge/Chromium) with PKCE flow.
    /// WPF replacement for AuthFormWebView2.
    /// </summary>
    public partial class AuthDialog : JJFlexDialog
    {
        private bool _isInitialized;
        private bool _isExchangingCode;

        // PKCE values
        private string _codeVerifier = "";
        private string _codeChallenge = "";
        private string _state = "";

        // Auth0 configuration
        private const string Auth0Domain = "frtest.auth0.com";
        private const string Auth0ClientId = "4Y9fEIIsVYyQo5u6jr7yBWc4lV5ugC2m";
        private const string RedirectUri = "https://frtest.auth0.com/mobile";

        /// <summary>JWT identity token for SmartLink authentication.</summary>
        public string IdToken { get; private set; } = "";

        /// <summary>Refresh token for obtaining new tokens without re-authentication.</summary>
        public string RefreshToken { get; private set; } = "";

        /// <summary>Access token (if needed for API calls).</summary>
        public string AccessToken { get; private set; } = "";

        /// <summary>Token lifetime in seconds.</summary>
        public int ExpiresIn { get; private set; }

        /// <summary>Email address extracted from id_token claims.</summary>
        public string Email { get; private set; } = "";

        /// <summary>Legacy Tokens array for backward compatibility.</summary>
        public string[]? Tokens { get; private set; }

        /// <summary>When true, forces Auth0 to show a fresh login page.</summary>
        public bool ForceNewLogin { get; set; }

        private readonly Action<string, int>? _trace;
        private readonly Action<string, bool>? _screenReaderSpeak;

        /// <summary>
        /// Creates the auth dialog.
        /// </summary>
        /// <param name="trace">Optional trace delegate (message, level)</param>
        /// <param name="screenReaderSpeak">Optional screen reader speak delegate (message, interrupt)</param>
        public AuthDialog(Action<string, int>? trace = null, Action<string, bool>? screenReaderSpeak = null)
        {
            _trace = trace;
            _screenReaderSpeak = screenReaderSpeak;

            InitializeComponent();
            GeneratePkceValues();
            Loaded += AuthDialog_Loaded;
        }

        private void GeneratePkceValues()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            _codeVerifier = Base64UrlEncode(bytes);

            using (var sha256 = SHA256.Create())
            {
                var challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(_codeVerifier));
                _codeChallenge = Base64UrlEncode(challengeBytes);
            }

            var stateBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(stateBytes);
            _state = Base64UrlEncode(stateBytes);

            _trace?.Invoke($"PKCE generated - verifier length: {_codeVerifier.Length}", 1);
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private async void AuthDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string uriString = BuildAuth0Url();
                _trace?.Invoke("AuthDialog URI: " + uriString, 1);

                // Initialize WebView2
                string userDataFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "JJFlexRadio", "WebView2");

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await WebView.EnsureCoreWebView2Async(env);
                _isInitialized = true;

                // Clear cookies for fresh login
                if (ForceNewLogin)
                {
                    var cookieManager = WebView.CoreWebView2.CookieManager;
                    var cookies = await cookieManager.GetCookiesAsync($"https://{Auth0Domain}");
                    foreach (var cookie in cookies)
                        cookieManager.DeleteCookie(cookie);
                    _trace?.Invoke("AuthDialog: Cleared Auth0 cookies for new login", 1);
                }

                WebView.NavigationCompleted += WebView_NavigationCompleted;
                WebView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;
                WebView.CoreWebView2.Navigate(uriString);
            }
            catch (Exception ex)
            {
                _trace?.Invoke("AuthDialog Exception: " + ex.Message, 3);
                MessageBox.Show(
                    $"Failed to initialize authentication browser:\n\n{ex.Message}\n\n" +
                    "Make sure WebView2 Runtime is installed.",
                    "Authentication Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                DialogResult = false;
                Close();
            }
        }

        private string BuildAuth0Url()
        {
            var sb = new StringBuilder();
            sb.Append($"https://{Auth0Domain}/authorize?");
            sb.Append("response_type=code&");
            sb.Append($"client_id={Auth0ClientId}&");
            sb.Append($"redirect_uri={Uri.EscapeDataString(RedirectUri)}&");
            sb.Append("scope=openid%20offline_access%20email%20profile&");
            sb.Append($"state={_state}&");
            sb.Append($"code_challenge={_codeChallenge}&");
            sb.Append("code_challenge_method=S256&");
            sb.Append("device=JJFlexRadio");
            return sb.ToString();
        }

        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!_isInitialized || _isExchangingCode) return;

            try
            {
                var uri = new Uri(WebView.Source.ToString());
                _trace?.Invoke("AuthDialog NavigationCompleted: " + uri.AbsoluteUri, 1);

                if (uri.AbsolutePath == "/mobile")
                {
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    var code = query["code"];
                    var returnedState = query["state"];
                    var error = query["error"];

                    if (!string.IsNullOrEmpty(error))
                    {
                        var errorDescription = query["error_description"] ?? "Unknown error";
                        _trace?.Invoke($"Auth0 error: {error} - {errorDescription}", 3);
                        MessageBox.Show($"Authentication failed: {errorDescription}", "Authentication Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        DialogResult = false;
                        Close();
                        return;
                    }

                    if (!string.IsNullOrEmpty(code))
                    {
                        if (returnedState != _state)
                        {
                            _trace?.Invoke("State mismatch - possible CSRF attack", 3);
                            MessageBox.Show("Authentication failed: Security validation error.", "Authentication Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            DialogResult = false;
                            Close();
                            return;
                        }

                        _isExchangingCode = true;
                        StatusLabel.Text = "Exchanging authorization code for tokens...";
                        _screenReaderSpeak?.Invoke("Completing authentication, please wait.", true);

                        bool success = await ExchangeCodeForTokens(code);

                        if (success)
                        {
                            Tokens = new[] { $"id_token={IdToken}", $"access_token={AccessToken}" };
                            DialogResult = true;
                        }
                        else
                        {
                            DialogResult = false;
                        }
                        Close();
                    }
                    else if (!string.IsNullOrEmpty(uri.Fragment))
                    {
                        ParseLegacyFragmentTokens(uri.Fragment);
                        DialogResult = true;
                        Close();
                    }
                }
                else
                {
                    _screenReaderSpeak?.Invoke("Login page ready. Enter your email address.", true);
                    WebView.Focus();
                    await InjectLoginErrorDetector();
                }
            }
            catch (Exception ex)
            {
                _trace?.Invoke("AuthDialog NavigationCompleted error: " + ex.Message, 2);
            }
        }

        private async Task InjectLoginErrorDetector()
        {
            try
            {
                const string script = @"
(function() {
    if (window._jjErrorObserverInstalled) return;
    window._jjErrorObserverInstalled = true;
    var lastReported = '';

    function checkForErrors() {
        var selectors = [
            '#prompt-alert', '.ulp-alert', '[data-error-code]',
            'section[role=""alert""]', 'div[role=""alert""]', 'span[role=""alert""]'
        ];
        var errorElements = document.querySelectorAll(selectors.join(','));
        for (var i = 0; i < errorElements.length; i++) {
            var el = errorElements[i];
            if (el.offsetParent === null && el.style.display === 'none') continue;
            var text = (el.textContent || '').trim();
            if (text.length > 0 && text.length < 200 && text !== lastReported) {
                lastReported = text;
                window.chrome.webview.postMessage(JSON.stringify({ type: 'login_error', message: text }));
                return;
            }
        }
    }

    var observer = new MutationObserver(function(mutations) {
        var dominated = false;
        for (var i = 0; i < mutations.length; i++) {
            if (mutations[i].addedNodes.length > 0) { dominated = true; break; }
        }
        if (!dominated) return;
        setTimeout(checkForErrors, 300);
    });

    observer.observe(document.body, { childList: true, subtree: true });
})();";
                await WebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"AuthDialog: Failed to inject error detector: {ex.Message}", 2);
            }
        }

        private string _lastSpokenError = "";

        private void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json)) return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "login_error")
                {
                    var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "";
                    if (string.IsNullOrEmpty(message) || message == _lastSpokenError) return;
                    _lastSpokenError = message;

                    _trace?.Invoke($"AuthDialog: Login error detected: {message}", 1);
                    _screenReaderSpeak?.Invoke("Incorrect login. Please try again.", true);
                    StatusLabel.Text = "Login failed - please try again.";
                }
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"AuthDialog: WebMessage error: {ex.Message}", 2);
            }
        }

        private async Task<bool> ExchangeCodeForTokens(string code)
        {
            try
            {
                using var client = new HttpClient();
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("client_id", Auth0ClientId),
                    new KeyValuePair<string, string>("code", code),
                    new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                    new KeyValuePair<string, string>("code_verifier", _codeVerifier)
                });

                var response = await client.PostAsync($"https://{Auth0Domain}/oauth/token", content);
                var json = await response.Content.ReadAsStringAsync();

                _trace?.Invoke($"Token exchange response: {response.StatusCode}", 1);

                if (!response.IsSuccessStatusCode)
                {
                    _trace?.Invoke($"Token exchange failed: {json}", 3);
                    MessageBox.Show($"Failed to complete authentication.\n\nStatus: {response.StatusCode}",
                        "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
                if (tokenResponse == null)
                {
                    _trace?.Invoke("Token exchange returned null", 3);
                    return false;
                }

                IdToken = tokenResponse.IdToken ?? "";
                AccessToken = tokenResponse.AccessToken ?? "";
                RefreshToken = tokenResponse.RefreshToken ?? "";
                ExpiresIn = tokenResponse.ExpiresIn;

                ExtractEmailFromIdToken();

                _trace?.Invoke($"Token exchange successful - has refresh token: {!string.IsNullOrEmpty(RefreshToken)}, expires in: {ExpiresIn}s", 1);
                return true;
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"Token exchange exception: {ex.Message}", 3);
                MessageBox.Show($"Authentication error: {ex.Message}", "Authentication Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ExtractEmailFromIdToken()
        {
            if (string.IsNullOrEmpty(IdToken)) return;

            try
            {
                var parts = IdToken.Split('.');
                if (parts.Length != 3) return;

                var payload = parts[1];
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                payload = payload.Replace('-', '+').Replace('_', '/');

                var jsonBytes = Convert.FromBase64String(payload);
                var jsonString = Encoding.UTF8.GetString(jsonBytes);

                using var doc = JsonDocument.Parse(jsonString);
                if (doc.RootElement.TryGetProperty("email", out var emailElement))
                {
                    Email = emailElement.GetString() ?? "";
                    _trace?.Invoke($"Extracted email from id_token: {Email}", 1);
                }
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"Failed to extract email from id_token: {ex.Message}", 2);
            }
        }

        private void ParseLegacyFragmentTokens(string fragment)
        {
            _trace?.Invoke("Parsing legacy fragment tokens", 1);

            if (fragment.StartsWith("#"))
                fragment = fragment.Substring(1);

            var parts = fragment.Split('&');
            Tokens = parts;

            foreach (var part in parts)
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;

                switch (kv[0])
                {
                    case "id_token":
                        IdToken = kv[1];
                        ExtractEmailFromIdToken();
                        break;
                    case "access_token":
                        AccessToken = kv[1];
                        break;
                    case "expires_in":
                        int.TryParse(kv[1], out int exp);
                        ExpiresIn = exp;
                        break;
                }
            }
        }

        private class TokenResponse
        {
            [JsonPropertyName("id_token")]
            public string? IdToken { get; set; }

            [JsonPropertyName("access_token")]
            public string? AccessToken { get; set; }

            [JsonPropertyName("refresh_token")]
            public string? RefreshToken { get; set; }

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("token_type")]
            public string? TokenType { get; set; }
        }
    }
}
