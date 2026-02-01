using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

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
                    ["refresh_token"] = account.RefreshToken
                });

                var response = await client.PostAsync($"https://{Auth0Domain}/oauth/token", content);

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Trace.WriteLine($"SmartLinkAccountManager: Token refresh failed: {response.StatusCode}");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var tokenResponse = JsonSerializer.Deserialize<TokenRefreshResponse>(json);

                if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.IdToken))
                    return false;

                // Update the account with new tokens
                account.IdToken = tokenResponse.IdToken;
                account.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

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
                System.Diagnostics.Trace.WriteLine($"SmartLinkAccountManager: Token refresh exception: {ex.Message}");
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
