using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Auth0 authentication form using WebView2 (Edge/Chromium) with PKCE flow.
    /// Returns both id_token and refresh_token for persistent login.
    /// </summary>
    public class AuthFormWebView2 : Form
    {
        private WebView2 webView;
        private Label urlLabel;
        private bool isInitialized;
        private bool isExchangingCode;

        // PKCE values (generated per auth attempt)
        private string _codeVerifier;
        private string _codeChallenge;
        private string _state;

        // Auth0 configuration
        private const string Auth0Domain = "frtest.auth0.com";
        private const string Auth0ClientId = "4Y9fEIIsVYyQo5u6jr7yBWc4lV5ugC2m";
        private const string RedirectUri = "https://frtest.auth0.com/mobile";

        /// <summary>
        /// Raw tokens array (legacy compatibility).
        /// </summary>
        [Obsolete("Use IdToken, RefreshToken, and ExpiresIn properties instead")]
        public string[] Tokens { get; private set; }

        /// <summary>
        /// JWT identity token for SmartLink authentication.
        /// </summary>
        public string IdToken { get; private set; }

        /// <summary>
        /// Refresh token for obtaining new tokens without re-authentication.
        /// </summary>
        public string RefreshToken { get; private set; }

        /// <summary>
        /// Access token (if needed for API calls).
        /// </summary>
        public string AccessToken { get; private set; }

        /// <summary>
        /// Token lifetime in seconds.
        /// </summary>
        public int ExpiresIn { get; private set; }

        /// <summary>
        /// Email address from the authenticated user (extracted from id_token claims).
        /// </summary>
        public string Email { get; private set; }

        public AuthFormWebView2()
        {
            InitializeComponent();
            GeneratePkceValues();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();

            // URL label for troubleshooting
            urlLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(4),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.LightYellow,
                ForeColor = Color.Black,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size, FontStyle.Bold)
            };

            // WebView2 control
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "WebView",
                AccessibleName = "SmartLink login page",
                AccessibleDescription = "Web browser for FlexRadio SmartLink authentication. Use Tab to navigate form fields.",
                AccessibleRole = AccessibleRole.Client,
                TabIndex = 0
            };

            this.Controls.Add(webView);
            this.Controls.Add(urlLabel);
            this.Controls.SetChildIndex(urlLabel, 0);

            this.AutoScaleDimensions = new SizeF(8F, 16F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(582, 653);
            this.Name = "AuthFormWebView2";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "SmartLink Authentication";
            this.Load += AuthFormWebView2_Load;

            this.ResumeLayout(false);
        }

        /// <summary>
        /// Generates PKCE code_verifier and code_challenge for secure auth flow.
        /// </summary>
        private void GeneratePkceValues()
        {
            // Generate 32-byte random code verifier (will be base64url encoded to ~43 chars)
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            _codeVerifier = Base64UrlEncode(bytes);

            // Generate code challenge = SHA256(code_verifier), base64url encoded
            using (var sha256 = SHA256.Create())
            {
                var challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(_codeVerifier));
                _codeChallenge = Base64UrlEncode(challengeBytes);
            }

            // Generate random state for CSRF protection
            var stateBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(stateBytes);
            }
            _state = Base64UrlEncode(stateBytes);

            Tracing.TraceLine($"PKCE generated - verifier length: {_codeVerifier.Length}, challenge: {_codeChallenge.Substring(0, 10)}...", TraceLevel.Info);
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private async void AuthFormWebView2_Load(object sender, EventArgs e)
        {
            try
            {
                // Build Auth0 URL with PKCE
                string uriString = BuildAuth0Url();
                Tracing.TraceLine("AuthFormWebView2 URI: " + uriString, TraceLevel.Info);

                if (urlLabel != null)
                {
                    urlLabel.Text = "Authenticating with SmartLink...";
                }

                // Initialize WebView2 with a user data folder in AppData
                string userDataFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "JJFlexRadio", "WebView2");

                // Async initialization keeps UI thread responsive for screen readers
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await webView.EnsureCoreWebView2Async(env);
                isInitialized = true;

                // Handle navigation completed
                webView.NavigationCompleted += WebView_NavigationCompleted;

                // Navigate to Auth0
                webView.CoreWebView2.Navigate(uriString);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AuthFormWebView2 Exception: " + ex.Message, TraceLevel.Error);
                MessageBox.Show(
                    $"Failed to initialize authentication browser:\n\n{ex.Message}\n\n" +
                    "Make sure WebView2 Runtime is installed.",
                    "Authentication Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                DialogResult = DialogResult.Abort;
            }
        }

        private string BuildAuth0Url()
        {
            // Authorization Code + PKCE flow
            var sb = new StringBuilder();
            sb.Append($"https://{Auth0Domain}/authorize?");
            sb.Append("response_type=code&");  // Request authorization code, not token
            sb.Append($"client_id={Auth0ClientId}&");
            sb.Append($"redirect_uri={Uri.EscapeDataString(RedirectUri)}&");
            sb.Append("scope=openid%20offline_access%20email%20profile&");  // offline_access = refresh token
            sb.Append($"state={_state}&");
            sb.Append($"code_challenge={_codeChallenge}&");
            sb.Append("code_challenge_method=S256&");
            sb.Append("device=JJFlexRadio");

            return sb.ToString();
        }

        private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!isInitialized || isExchangingCode) return;

            try
            {
                var uri = new Uri(webView.Source.ToString());
                Tracing.TraceLine("AuthFormWebView2 NavigationCompleted: " + uri.AbsoluteUri, TraceLevel.Info);

                if (uri.AbsolutePath == "/mobile")
                {
                    // Check for authorization code in query string
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    var code = query["code"];
                    var returnedState = query["state"];
                    var error = query["error"];

                    if (!string.IsNullOrEmpty(error))
                    {
                        var errorDescription = query["error_description"] ?? "Unknown error";
                        Tracing.TraceLine($"Auth0 error: {error} - {errorDescription}", TraceLevel.Error);
                        MessageBox.Show($"Authentication failed: {errorDescription}", "Authentication Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        DialogResult = DialogResult.Cancel;
                        return;
                    }

                    if (!string.IsNullOrEmpty(code))
                    {
                        // Validate state to prevent CSRF
                        if (returnedState != _state)
                        {
                            Tracing.TraceLine("State mismatch - possible CSRF attack", TraceLevel.Error);
                            MessageBox.Show("Authentication failed: Security validation error.", "Authentication Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            DialogResult = DialogResult.Cancel;
                            return;
                        }

                        // Exchange code for tokens
                        isExchangingCode = true;
                        urlLabel.Text = "Exchanging authorization code for tokens...";
                        ScreenReaderOutput.Speak("Completing authentication, please wait.", true);

                        bool success = await ExchangeCodeForTokens(code);

                        if (success)
                        {
                            // Legacy compatibility - populate Tokens array
                            #pragma warning disable CS0618
                            Tokens = new[] { $"id_token={IdToken}", $"access_token={AccessToken}" };
                            #pragma warning restore CS0618

                            DialogResult = DialogResult.OK;
                        }
                        else
                        {
                            DialogResult = DialogResult.Cancel;
                        }
                    }
                    else
                    {
                        // Check for legacy implicit flow tokens in fragment (fallback)
                        if (!string.IsNullOrEmpty(uri.Fragment))
                        {
                            ParseLegacyFragmentTokens(uri.Fragment);
                            DialogResult = DialogResult.OK;
                        }
                    }
                }
                else
                {
                    // Login page loaded - announce to screen reader and set focus
                    ScreenReaderOutput.Speak("Login page ready. Enter your email address.", true);
                    webView.Focus();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AuthFormWebView2 NavigationCompleted error: " + ex.Message, TraceLevel.Warning);
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

                Tracing.TraceLine($"Token exchange response: {response.StatusCode}", TraceLevel.Info);

                if (!response.IsSuccessStatusCode)
                {
                    Tracing.TraceLine($"Token exchange failed: {json}", TraceLevel.Error);
                    MessageBox.Show($"Failed to complete authentication.\n\nStatus: {response.StatusCode}",
                        "Authentication Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
                if (tokenResponse == null)
                {
                    Tracing.TraceLine("Token exchange returned null", TraceLevel.Error);
                    return false;
                }

                IdToken = tokenResponse.IdToken ?? string.Empty;
                AccessToken = tokenResponse.AccessToken ?? string.Empty;
                RefreshToken = tokenResponse.RefreshToken ?? string.Empty;
                ExpiresIn = tokenResponse.ExpiresIn;

                // Extract email from id_token (JWT payload is the middle segment)
                ExtractEmailFromIdToken();

                Tracing.TraceLine($"Token exchange successful - has refresh token: {!string.IsNullOrEmpty(RefreshToken)}, expires in: {ExpiresIn}s", TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Token exchange exception: {ex.Message}", TraceLevel.Error);
                MessageBox.Show($"Authentication error: {ex.Message}", "Authentication Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void ExtractEmailFromIdToken()
        {
            if (string.IsNullOrEmpty(IdToken))
                return;

            try
            {
                // JWT format: header.payload.signature
                var parts = IdToken.Split('.');
                if (parts.Length != 3)
                    return;

                // Decode payload (middle part) - it's base64url encoded
                var payload = parts[1];
                // Add padding if needed
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
                    Email = emailElement.GetString() ?? string.Empty;
                    Tracing.TraceLine($"Extracted email from id_token: {Email}", TraceLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Failed to extract email from id_token: {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Fallback for legacy implicit flow (if server returns tokens in fragment).
        /// </summary>
        private void ParseLegacyFragmentTokens(string fragment)
        {
            Tracing.TraceLine("Parsing legacy fragment tokens", TraceLevel.Info);

            // Remove leading # if present
            if (fragment.StartsWith("#"))
                fragment = fragment.Substring(1);

            var parts = fragment.Split('&');
            #pragma warning disable CS0618
            Tokens = parts;
            #pragma warning restore CS0618

            foreach (var part in parts)
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;

                var key = kv[0];
                var value = kv[1];

                switch (key)
                {
                    case "id_token":
                        IdToken = value;
                        ExtractEmailFromIdToken();
                        break;
                    case "access_token":
                        AccessToken = value;
                        break;
                    case "expires_in":
                        int.TryParse(value, out int exp);
                        ExpiresIn = exp;
                        break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                webView?.Dispose();
            }
            base.Dispose(disposing);
        }

        private class TokenResponse
        {
            [JsonPropertyName("id_token")]
            public string IdToken { get; set; }

            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; }

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; }

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("token_type")]
            public string TokenType { get; set; }
        }
    }
}
