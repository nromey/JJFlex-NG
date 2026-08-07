using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Sprint 27 Track F — SmartLink connection mode per account. Three
    /// cumulative tiers: Tier 1 alone (sovereign, recommended default),
    /// Tier 1 + 2 (UPnP convenience), Tier 1 + 2 + 3 (Flex-coordinated UDP
    /// hole-punch for restrictive NATs). Each higher tier includes the
    /// lower tiers' behaviors. Cast to <c>int</c> produces an ordinal usable
    /// for 'mode ≥ ManualPlusUpnp' checks.
    /// </summary>
    public enum SmartLinkConnectionMode
    {
        /// <summary>Tier 1. Manual router port-forwarding only. No UPnP, no hole-punch. Default.</summary>
        ManualPortForwardOnly = 0,

        /// <summary>Tier 1 + 2. UPnP attempts to open the configured port automatically. No hole-punch.</summary>
        ManualPlusUpnp = 1,

        /// <summary>Tier 1 + 2 + 3. Flex's SmartLink coordinates UDP hole-punch for restrictive NATs. Fallback to Tier 2 then Tier 1 on failure.</summary>
        AutomaticHolePunch = 2,
    }

    /// <summary>
    /// Manages saved SmartLink accounts with secure token storage using Windows DPAPI.
    /// Tokens are encrypted per-user and cannot be decrypted on other machines.
    /// </summary>
    public class SmartLinkAccountManager
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JJFlexRadio");

        private static readonly string AccountsFilePath = Path.Combine(AppDataFolder, "SmartLinkAccounts.json");

        // Auth0 configuration (same as AuthFormWebView2)
        private const string Auth0Domain = "frtest.auth0.com";
        private const string Auth0ClientId = "4Y9fEIIsVYyQo5u6jr7yBWc4lV5ugC2m";

        private List<SmartLinkAccount> _accounts = new();
        private static readonly object _fileLock = new();

        /// <summary>
        /// Gets all saved accounts (tokens remain encrypted in memory).
        /// </summary>
        public IReadOnlyList<SmartLinkAccount> Accounts => _accounts.AsReadOnly();

        /// <summary>
        /// Loads accounts from disk. Call this at startup.
        /// </summary>
        public void LoadAccounts()
        {
            lock (_fileLock)
            {
                if (!File.Exists(AccountsFilePath))
                {
                    _accounts = new List<SmartLinkAccount>();
                    return;
                }

                try
                {
                    var json = File.ReadAllText(AccountsFilePath);
                    var stored = JsonSerializer.Deserialize<List<StoredAccount>>(json);
                    _accounts = stored?.Select(s => s.ToSmartLinkAccount()).ToList() ?? new List<SmartLinkAccount>();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"SmartLinkAccountManager: Failed to load accounts: {ex.Message}");
                    _accounts = new List<SmartLinkAccount>();
                }
            }
        }

        /// <summary>
        /// True when at least one SmartLink account has ever been saved on this
        /// computer. Cheap enough to call casually — the accounts file is tiny —
        /// and reads through <see cref="LoadAccounts"/> rather than just testing
        /// file existence, so an empty or unreadable file counts as "none".
        /// </summary>
        public static bool AnySavedAccounts()
        {
            var mgr = new SmartLinkAccountManager();
            mgr.LoadAccounts();
            return mgr.Accounts.Count > 0;
        }

        /// <summary>
        /// Saves all accounts to disk with encrypted tokens.
        /// </summary>
        public void SaveAccounts()
        {
            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(AppDataFolder);

                    var stored = _accounts.Select(a => StoredAccount.FromSmartLinkAccount(a)).ToList();
                    var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(AccountsFilePath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"SmartLinkAccountManager: Failed to save accounts: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Opens the SmartLink Account Selector dialog for standalone account management
        /// (rename, delete, view saved accounts). Called from the Modern UI menu.
        /// Sprint 10: Uses delegate to decouple from WinForms SmartLinkAccountSelector form.
        /// </summary>
        public static Action<SmartLinkAccountManager> ShowAccountManagerDialog { get; set; }

        public static void ShowAccountManager(System.Windows.Forms.IWin32Window owner, string configDir, string callSign)
        {
            var mgr = new SmartLinkAccountManager();
            mgr.LoadAccounts();
            ShowAccountManagerDialog?.Invoke(mgr);
        }

        /// <summary>
        /// Adds or updates an account. If an account with the same email exists, it is updated.
        /// </summary>
        public void SaveAccount(SmartLinkAccount account)
        {
            var existing = _accounts.FirstOrDefault(a =>
                string.Equals(a.Email, account.Email, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _accounts.Remove(existing);
            }

            account.LastUsed = DateTime.UtcNow;
            _accounts.Add(account);
            SaveAccounts();
        }

        /// <summary>
        /// Deletes an account by friendly name.
        /// </summary>
        public bool DeleteAccount(string friendlyName)
        {
            var account = _accounts.FirstOrDefault(a =>
                string.Equals(a.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

            if (account == null)
                return false;

            _accounts.Remove(account);
            SaveAccounts();
            return true;
        }

        /// <summary>
        /// Reset an account's sign-in data by friendly name (Noel, 2026-08-06):
        /// clears the tokens so the next connection walks through a fresh
        /// sign-in, while the account itself — name, email, port preferences,
        /// connection mode — stays exactly as it was. The recovery tool for
        /// "my login data got screwed up" that deleting the JSON used to be,
        /// minus losing everything else. With sign-in now native, clearing
        /// tokens IS the complete reset; no browser cookie participates.
        /// </summary>
        public bool ResetAccountSignIn(string friendlyName)
        {
            var account = _accounts.FirstOrDefault(a =>
                string.Equals(a.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

            if (account == null)
                return false;

            account.IdToken = string.Empty;
            account.RefreshToken = string.Empty;
            account.ExpiresAt = DateTime.MinValue;
            SaveAccounts();
            Tracing.TraceLine($"ResetAccountSignIn: cleared tokens for {account.Email}", TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Gets an account by friendly name.
        /// </summary>
        public SmartLinkAccount? GetAccountByName(string friendlyName)
        {
            return _accounts.FirstOrDefault(a =>
                string.Equals(a.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets an account by email address.
        /// Used for auto-connect to find the saved account for a remote radio.
        /// </summary>
        public SmartLinkAccount? GetAccountByEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return null;

            return _accounts.FirstOrDefault(a =>
                string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Renames an account.
        /// </summary>
        public bool RenameAccount(string oldName, string newName)
        {
            var account = GetAccountByName(oldName);
            if (account == null)
                return false;

            // Check if new name already exists
            if (GetAccountByName(newName) != null)
                return false;

            account.FriendlyName = newName;
            SaveAccounts();
            return true;
        }

        /// <summary>
        /// Checks if an account's tokens are expired.
        /// </summary>
        public bool IsTokenExpired(SmartLinkAccount account)
        {
            // Give 5-minute buffer before expiration
            return account.ExpiresAt <= DateTime.UtcNow.AddMinutes(5);
        }

        /// <summary>
        /// Checks whether the id_token JWT's own exp claim has passed.
        /// Auth0's frtest tenant does not return a new id_token on refresh,
        /// so the saved JWT may have expired even if the refresh_token is valid.
        /// Returns true if the JWT is expired or cannot be parsed.
        /// </summary>
        public static bool IsJwtExpired(string idToken)
        {
            if (string.IsNullOrEmpty(idToken))
            {
                Tracing.TraceLine("IsJwtExpired: token is null/empty", TraceLevel.Info);
                return true;
            }

            try
            {
                var parts = idToken.Split('.');
                if (parts.Length != 3)
                {
                    Tracing.TraceLine("IsJwtExpired: token doesn't have 3 parts", TraceLevel.Warning);
                    return true;
                }

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
                if (doc.RootElement.TryGetProperty("exp", out var expElement))
                {
                    var expUnix = expElement.GetInt64();
                    var expTime = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                    var now = DateTime.UtcNow;
                    var delta = expTime - now;
                    // Buffer must be far under the token's total lifetime.
                    // Decoded live 2026-08-04: this tenant issues id_tokens
                    // that expire 60 SECONDS after issue, so the old 2-minute
                    // buffer declared every token expired at birth — forcing an
                    // interactive login on every SmartLink operation all night.
                    // SmartSDR uses a 10-second threshold; match it.
                    var bufferTime = now.AddSeconds(10);
                    bool expired = expTime <= bufferTime;

                    Tracing.TraceLine($"IsJwtExpired: exp={expTime:yyyy-MM-dd HH:mm:ss}Z, now={now:yyyy-MM-dd HH:mm:ss}Z, delta={delta.TotalSeconds:F0}s, buffer=10s, expired={expired}", TraceLevel.Info);
                    return expired;
                }

                Tracing.TraceLine("IsJwtExpired: no exp claim in JWT", TraceLevel.Warning);
                return true; // no exp claim = treat as expired
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"IsJwtExpired: parse exception: {ex.Message}", TraceLevel.Error);
                return true;
            }
        }

        /// <summary>
        /// Attempts to refresh tokens using the refresh token.
        /// Returns true if successful, false if user must re-authenticate.
        /// </summary>
        public async Task<bool> RefreshTokenAsync(SmartLinkAccount account)
        {
            if (string.IsNullOrEmpty(account.RefreshToken))
                return false;

            try
            {
                using var client = new HttpClient();
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = Auth0ClientId,
                    ["refresh_token"] = account.RefreshToken,
                    // Exactly SmartSDR's refresh scope (decompile, Auth0Client.
                    // RefreshIdToken): they get a fresh id_token back with this.
                    // Our old scope included offline_access/email and we never
                    // saw an id_token — match the vendor recipe precisely.
                    ["scope"] = "openid profile"
                });

                var response = await client.PostAsync($"https://{Auth0Domain}/oauth/token", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Tracing.TraceLine($"SmartLinkAccountManager: Token refresh failed: {response.StatusCode} - {errorBody}", TraceLevel.Error);
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                Tracing.TraceLine($"SmartLinkAccountManager: Token refresh response received, parsing", TraceLevel.Info);

                // Diagnostic (2026-08-05): refresh keeps returning success WITHOUT
                // an id_token, forcing an interactive login every time. Log which
                // keys came back — names only, never values; these are live
                // credentials.
                try
                {
                    using var probe = JsonDocument.Parse(json);
                    var keys = string.Join(",", probe.RootElement.EnumerateObject().Select(p => p.Name));
                    Tracing.TraceLine($"SmartLinkAccountManager: refresh response keys: [{keys}]", TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"SmartLinkAccountManager: refresh key probe failed: {ex.Message}", TraceLevel.Warning);
                }
                var tokenResponse = JsonSerializer.Deserialize<TokenRefreshResponse>(json);

                if (tokenResponse == null)
                {
                    Tracing.TraceLine("SmartLinkAccountManager: Token refresh returned null response", TraceLevel.Error);
                    return false;
                }

                // With scope "openid profile" the tenant DOES return a fresh
                // id_token (verified against SmartSDR's shipping code, 2026-08-05;
                // the old claim that frtest never returns one was an artifact of
                // our old scope). A response without one still proves the session
                // is alive — keep the saved id_token and update expiry.
                if (!string.IsNullOrEmpty(tokenResponse.IdToken))
                {
                    account.IdToken = tokenResponse.IdToken;
                    Tracing.TraceLine("SmartLinkAccountManager: Token refresh returned new id_token", TraceLevel.Info);
                }
                else if (!string.IsNullOrEmpty(account.IdToken))
                {
                    // No id_token in response, but we have a saved one — keep it
                    Tracing.TraceLine("SmartLinkAccountManager: No id_token in refresh response, keeping saved id_token", TraceLevel.Info);
                }
                else
                {
                    // No id_token anywhere — can't authenticate
                    Tracing.TraceLine("SmartLinkAccountManager: Token refresh has no id_token and no saved id_token", TraceLevel.Error);
                    return false;
                }

                if (tokenResponse.ExpiresIn > 0)
                {
                    account.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
                }

                // Refresh token may be rotated
                if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
                {
                    account.RefreshToken = tokenResponse.RefreshToken;
                }

                account.LastUsed = DateTime.UtcNow;
                SaveAccounts();

                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SmartLinkAccountManager: Token refresh exception: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Outcome of a native password sign-in attempt. Error is one of the
        /// well-known kinds so the dialog can react specifically (wrong
        /// password gets a retry, MFA gets the browser); ErrorDetail carries
        /// Auth0's human text for the trace, never shown raw to the user.
        /// </summary>
        public sealed class PasswordLoginResult
        {
            public bool Success;
            public string Error = "";        // "", wrong_credentials, mfa_required, too_many_attempts, network, other
            public string ErrorDetail = "";
            public string Email = "";
            public string IdToken = "";
            public string RefreshToken = "";
            public int ExpiresIn;
        }

        /// <summary>
        /// Native SmartLink sign-in: the resource-owner password grant, exactly
        /// as SmartSDR ships it (decompile `Auth0Client.LoginAsync`,
        /// ResourceOwnerTokenRequest, scope "openid profile offline_access").
        /// This is the fix for the 2026-08-06 lockout class: refresh tokens
        /// minted by THIS grant return fresh id_tokens on refresh, so the
        /// silent JIT path finally works, and no browser, cookie, or WebView2
        /// profile is involved in signing in. The password is exchanged
        /// immediately and never stored.
        /// </summary>
        public async Task<PasswordLoginResult> LoginWithPasswordAsync(string email, string password)
        {
            var result = new PasswordLoginResult { Email = email?.Trim() ?? "" };
            try
            {
                using var client = new HttpClient();
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["client_id"] = Auth0ClientId,
                    ["username"] = result.Email,
                    ["password"] = password ?? "",
                    ["scope"] = "openid profile offline_access",
                });

                var response = await client.PostAsync($"https://{Auth0Domain}/oauth/token", content);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string error = "other", detail = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString() ?? "other";
                        if (doc.RootElement.TryGetProperty("error_description", out var d)) detail = d.GetString() ?? "";
                    }
                    catch { /* non-JSON error body; keep generic */ }

                    result.Error = error switch
                    {
                        "invalid_grant" => "wrong_credentials",
                        "mfa_required" => "mfa_required",
                        "too_many_attempts" => "too_many_attempts",
                        _ => "other",
                    };
                    result.ErrorDetail = detail;
                    // Status and error kind only — never the description verbatim
                    // at higher levels, and never any credential material.
                    Tracing.TraceLine(
                        $"LoginWithPasswordAsync: {response.StatusCode} error={error}",
                        TraceLevel.Warning);
                    return result;
                }

                using var ok = JsonDocument.Parse(json);
                if (ok.RootElement.TryGetProperty("id_token", out var idTok)) result.IdToken = idTok.GetString() ?? "";
                if (ok.RootElement.TryGetProperty("refresh_token", out var refTok)) result.RefreshToken = refTok.GetString() ?? "";
                if (ok.RootElement.TryGetProperty("expires_in", out var exp)) result.ExpiresIn = exp.GetInt32();

                if (string.IsNullOrEmpty(result.IdToken))
                {
                    result.Error = "other";
                    result.ErrorDetail = "sign-in succeeded but no id_token came back";
                    Tracing.TraceLine("LoginWithPasswordAsync: 200 but no id_token in response", TraceLevel.Error);
                    return result;
                }

                // The token's email claim is the authoritative identity —
                // Auth0 canonicalizes what the user typed.
                var claimEmail = TryGetJwtClaim(result.IdToken, "email");
                if (!string.IsNullOrEmpty(claimEmail)) result.Email = claimEmail;

                result.Success = true;
                Tracing.TraceLine($"LoginWithPasswordAsync: success for {result.Email}", TraceLevel.Info);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "network";
                result.ErrorDetail = ex.Message;
                Tracing.TraceLine($"LoginWithPasswordAsync: exception: {ex.Message}", TraceLevel.Error);
                return result;
            }
        }

        /// <summary>
        /// Ask Auth0 to email a password-reset link. Fire-and-report: a true
        /// return means Auth0 accepted the request, not that the user finished
        /// resetting. Connection name is SmartSDR's own
        /// (decompile ChangePasswordRequest: "Username-Password-Authentication").
        /// </summary>
        public async Task<bool> SendPasswordResetEmailAsync(string email)
        {
            try
            {
                using var client = new HttpClient();
                var body = JsonSerializer.Serialize(new
                {
                    client_id = Auth0ClientId,
                    email = email?.Trim() ?? "",
                    connection = "Username-Password-Authentication",
                });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"https://{Auth0Domain}/dbconnections/change_password", content);
                Tracing.TraceLine($"SendPasswordResetEmailAsync: {response.StatusCode}", TraceLevel.Info);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SendPasswordResetEmailAsync: exception: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Outcome of a native account-signup attempt. Error is a well-known
        /// kind so the dialog can speak something specific; ErrorDetail is
        /// Auth0's own text, for the trace only.
        /// </summary>
        public sealed class SignUpResult
        {
            public bool Success;
            public string Error = "";        // "", user_exists, weak_password, network, other
            public string ErrorDetail = "";
        }

        /// <summary>
        /// Create a SmartLink account natively: POST to Auth0's
        /// dbconnections/signup, exactly as SmartSDR ships it (decompile
        /// Auth0Client ~2475: client_id + connection
        /// "Username-Password-Authentication" + email + password). SmartSDR
        /// never uses the hosted page's signup link — and that link half-works
        /// at best (creates the account, then fails its redirect and REPORTS
        /// failure; live find 2026-08-04), which is why this exists. The
        /// password is sent once and never stored; sign-in afterward goes
        /// through <see cref="LoginWithPasswordAsync"/> as usual.
        /// </summary>
        public async Task<SignUpResult> SignUpAsync(string email, string password)
        {
            var result = new SignUpResult();
            try
            {
                using var client = new HttpClient();
                var body = JsonSerializer.Serialize(new
                {
                    client_id = Auth0ClientId,
                    email = email?.Trim() ?? "",
                    password = password ?? "",
                    connection = "Username-Password-Authentication",
                });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"https://{Auth0Domain}/dbconnections/signup", content);
                var json = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    result.Success = true;
                    Tracing.TraceLine("SignUpAsync: account created", TraceLevel.Info);
                    return result;
                }

                // Auth0 signup errors carry "code" (and sometimes "name");
                // map the ones SmartSDR maps, trace the rest.
                string code = "", name = "", detail = "";
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("description", out var d))
                        detail = d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : d.GetRawText();
                }
                catch { /* non-JSON error body; keep generic */ }

                result.Error = code switch
                {
                    "user_exists" => "user_exists",
                    "username_exists" => "user_exists",
                    "invalid_password" => "weak_password",
                    _ when name == "PasswordStrengthError" => "weak_password",
                    _ => "other",
                };
                result.ErrorDetail = detail;
                // Status and error kind only — never credential material.
                Tracing.TraceLine(
                    $"SignUpAsync: {response.StatusCode} code={code} name={name}",
                    TraceLevel.Warning);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "network";
                result.ErrorDetail = ex.Message;
                Tracing.TraceLine($"SignUpAsync: exception: {ex.Message}", TraceLevel.Error);
                return result;
            }
        }

        /// <summary>
        /// Reads a single string claim from a JWT payload without validating
        /// the signature — fine for our own just-received tokens; the server
        /// is the authority on validity.
        /// </summary>
        public static string TryGetJwtClaim(string jwt, string claim)
        {
            try
            {
                var parts = jwt?.Split('.');
                if (parts == null || parts.Length != 3) return "";
                var payload = parts[1];
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                payload = payload.Replace('-', '+').Replace('_', '/');
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                return doc.RootElement.TryGetProperty(claim, out var v) ? v.GetString() ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Updates the LastUsed timestamp for an account.
        /// </summary>
        public void MarkAccountUsed(SmartLinkAccount account)
        {
            account.LastUsed = DateTime.UtcNow;
            SaveAccounts();
        }

        /// <summary>
        /// Sprint 27 Track A / Tier 1. Returns the saved SmartLink listen-port
        /// preference for the account with the given <paramref name="email"/>,
        /// or null if the account has no preference set or the email is unknown.
        /// Reads the in-memory cache; does not touch disk.
        /// </summary>
        public int? GetConfiguredPort(string email)
        {
            return GetAccountByEmail(email)?.ConfiguredListenPort;
        }

        /// <summary>
        /// Sprint 27 Track A / Tier 1. Persists the listen-port preference for
        /// the account with the given <paramref name="email"/>. Pass null to
        /// clear the preference (revert to FlexLib default). Returns false if
        /// the port is out of the manual range (1024–65535) or the email is
        /// unknown. Saves to disk on success.
        /// </summary>
        public bool SetConfiguredPort(string email, int? port)
        {
            if (!IsValidPort(port)) return false;
            var account = GetAccountByEmail(email);
            if (account == null) return false;
            account.ConfiguredListenPort = port;
            SaveAccounts();
            return true;
        }

        /// <summary>
        /// Sprint 27 Track A. Shared validator for listen-port preferences.
        /// Null is always valid (= "no preference"). Non-null must be in
        /// 1024–65535. Exposed so UI code can validate before calling
        /// <see cref="SetConfiguredPort"/> and present a clear announcement.
        /// </summary>
        public static bool IsValidPort(int? port)
        {
            if (!port.HasValue) return true;
            return port.Value >= 1024 && port.Value <= 65535;
        }

        /// <summary>
        /// Sprint 27 Track F. Returns the SmartLink connection mode for the
        /// given account, or <see cref="SmartLinkConnectionMode.ManualPortForwardOnly"/>
        /// when the email is unknown. In-memory read; does not touch disk.
        /// </summary>
        public SmartLinkConnectionMode GetConnectionMode(string email)
        {
            return GetAccountByEmail(email)?.ConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly;
        }

        /// <summary>
        /// Sprint 27 Track F. Persists the SmartLink connection mode for the
        /// given account. Returns false if the email is unknown. Saves to
        /// disk on success.
        /// </summary>
        public bool SetConnectionMode(string email, SmartLinkConnectionMode mode)
        {
            var account = GetAccountByEmail(email);
            if (account == null) return false;
            account.ConnectionMode = mode;
            SaveAccounts();
            return true;
        }

        #region DPAPI Encryption Helpers

        private static string EncryptWithDpapi(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }

        private static string DecryptWithDpapi(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64))
                return string.Empty;

            try
            {
                var encryptedBytes = Convert.FromBase64String(encryptedBase64);
                var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // Decryption failed - likely different user or machine
                return string.Empty;
            }
        }

        #endregion

        #region Internal Storage Classes

        /// <summary>
        /// JSON serialization DTO with encrypted tokens. Internal (not private)
        /// so Radios.Tests can exercise the round-trip + old-file backward-compat.
        /// </summary>
        internal class StoredAccount
        {
            [JsonPropertyName("friendlyName")]
            public string FriendlyName { get; set; } = string.Empty;

            [JsonPropertyName("email")]
            public string Email { get; set; } = string.Empty;

            [JsonPropertyName("idTokenEncrypted")]
            public string IdTokenEncrypted { get; set; } = string.Empty;

            [JsonPropertyName("refreshTokenEncrypted")]
            public string RefreshTokenEncrypted { get; set; } = string.Empty;

            [JsonPropertyName("expiresAt")]
            public DateTime ExpiresAt { get; set; }

            [JsonPropertyName("lastUsed")]
            public DateTime LastUsed { get; set; }

            // Nullable so pre-Sprint-27 JSON (which omits this field) deserializes
            // to null = "use FlexLib default". Satisfies NG-8.
            [JsonPropertyName("configuredListenPort")]
            public int? ConfiguredListenPort { get; set; }

            // Sprint 27 Track F. Serialized as the enum's string name (e.g.
            // "ManualPortForwardOnly") for readability in the on-disk JSON.
            // Absent field = enum default (ManualPortForwardOnly). Satisfies
            // the same NG-8 backward-compat guarantee as ConfiguredListenPort.
            [JsonPropertyName("connectionMode")]
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public SmartLinkConnectionMode ConnectionMode { get; set; }

            // Absent field = false: pre-existing JSON deserializes unchanged.
            [JsonPropertyName("autoStartRemote")]
            public bool AutoStartRemote { get; set; }

            public static StoredAccount FromSmartLinkAccount(SmartLinkAccount account)
            {
                return new StoredAccount
                {
                    FriendlyName = account.FriendlyName,
                    Email = account.Email,
                    IdTokenEncrypted = EncryptWithDpapi(account.IdToken),
                    RefreshTokenEncrypted = EncryptWithDpapi(account.RefreshToken),
                    ExpiresAt = account.ExpiresAt,
                    LastUsed = account.LastUsed,
                    ConfiguredListenPort = account.ConfiguredListenPort,
                    ConnectionMode = account.ConnectionMode,
                    AutoStartRemote = account.AutoStartRemote
                };
            }

            public SmartLinkAccount ToSmartLinkAccount()
            {
                return new SmartLinkAccount
                {
                    FriendlyName = FriendlyName,
                    Email = Email,
                    IdToken = DecryptWithDpapi(IdTokenEncrypted),
                    RefreshToken = DecryptWithDpapi(RefreshTokenEncrypted),
                    ExpiresAt = ExpiresAt,
                    LastUsed = LastUsed,
                    ConfiguredListenPort = ConfiguredListenPort,
                    ConnectionMode = ConnectionMode,
                    AutoStartRemote = AutoStartRemote
                };
            }
        }

        private class TokenRefreshResponse
        {
            [JsonPropertyName("id_token")]
            public string IdToken { get; set; } = string.Empty;

            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = string.Empty;

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("token_type")]
            public string TokenType { get; set; } = string.Empty;
        }

        #endregion
    }

    /// <summary>
    /// Represents a saved SmartLink account.
    /// </summary>
    public class SmartLinkAccount
    {
        /// <summary>
        /// User-assigned friendly name (e.g., "W1ABC Home Shack").
        /// </summary>
        public string FriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// Email address from Auth0 profile.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// JWT identity token (decrypted, in memory only).
        /// </summary>
        public string IdToken { get; set; } = string.Empty;

        /// <summary>
        /// Refresh token for obtaining new tokens (decrypted, in memory only).
        /// </summary>
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>
        /// When the IdToken expires.
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// When this account was last used.
        /// </summary>
        public DateTime LastUsed { get; set; }

        /// <summary>
        /// Sprint 27 Track A / Tier 1 — user-chosen SmartLink listen port.
        /// Null means "no preference set; use the FlexLib/radio default (4992)".
        /// A non-null value is applied to the radio post-connect via
        /// <c>FlexBase.SetSmartLinkPortForwarding</c> so the router's manually
        /// forwarded port matches what the radio listens on.
        /// </summary>
        public int? ConfiguredListenPort { get; set; }

        /// <summary>
        /// Sprint 27 Track F — the SmartLink connection mode for this account
        /// (cumulative tier model). Default <see cref="SmartLinkConnectionMode.ManualPortForwardOnly"/>.
        /// Tier 2 + Tier 3 behaviors are both gated on
        /// <see cref="ConfiguredListenPort"/> being set (UPnP and hole-punch
        /// both need a port). Replaces Sprint 27 Phase B.2's UPnPEnabled bool
        /// with a three-state enum so Tier 3 can be represented.
        /// </summary>
        public SmartLinkConnectionMode ConnectionMode { get; set; }

        /// <summary>
        /// Remote-first startup (Noel, 2026-08-06, for remote-only operators):
        /// when true and this account is the one that will be used for
        /// SmartLink, the radio selector kicks off Remote discovery the moment
        /// it opens instead of waiting for the Remote button. Opt-in, default
        /// off, per-account. Only safe now that sign-in is native — the worst
        /// startup surprise is a self-announcing dialog, never a browser page.
        /// </summary>
        public bool AutoStartRemote { get; set; }

        /// <summary>
        /// Display string for UI.
        /// </summary>
        public override string ToString()
        {
            return $"{FriendlyName} ({Email})";
        }
    }
}
