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
                return true;

            try
            {
                var parts = idToken.Split('.');
                if (parts.Length != 3)
                    return true;

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
                    // 2-minute buffer
                    return expTime <= DateTime.UtcNow.AddMinutes(2);
                }

                return true; // no exp claim = treat as expired
            }
            catch
            {
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
                    ["scope"] = "openid offline_access email profile"
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
                var tokenResponse = JsonSerializer.Deserialize<TokenRefreshResponse>(json);

                if (tokenResponse == null)
                {
                    Tracing.TraceLine("SmartLinkAccountManager: Token refresh returned null response", TraceLevel.Error);
                    return false;
                }

                // Auth0's frtest tenant does not return id_token on refresh_token grant.
                // If we got a successful response (access_token + expires_in), the session
                // is still valid — keep using the saved id_token and update expiry.
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
        /// Updates the LastUsed timestamp for an account.
        /// </summary>
        public void MarkAccountUsed(SmartLinkAccount account)
        {
            account.LastUsed = DateTime.UtcNow;
            SaveAccounts();
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
        /// Internal class for JSON serialization with encrypted tokens.
        /// </summary>
        private class StoredAccount
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

            public static StoredAccount FromSmartLinkAccount(SmartLinkAccount account)
            {
                return new StoredAccount
                {
                    FriendlyName = account.FriendlyName,
                    Email = account.Email,
                    IdTokenEncrypted = EncryptWithDpapi(account.IdToken),
                    RefreshTokenEncrypted = EncryptWithDpapi(account.RefreshToken),
                    ExpiresAt = account.ExpiresAt,
                    LastUsed = account.LastUsed
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
                    LastUsed = LastUsed
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
        /// Display string for UI.
        /// </summary>
        public override string ToString()
        {
            return $"{FriendlyName} ({Email})";
        }
    }
}
