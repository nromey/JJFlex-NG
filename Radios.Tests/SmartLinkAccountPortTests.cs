#nullable enable

using System.Text.Json;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 27 Track A / Phase A.1. Verifies the per-account SmartLink
    /// listen-port preference (<see cref="SmartLinkAccount.ConfiguredListenPort"/>)
    /// reads, writes, validates, and round-trips through JSON correctly —
    /// including backward-compat with pre-Sprint-27 on-disk files that omit
    /// the new field entirely (NG-8: existing accounts default to FlexLib's
    /// behavior until the user explicitly configures a port).
    /// </summary>
    public class SmartLinkAccountPortTests
    {
        [Fact]
        public void SmartLinkAccount_DefaultsToNullPort()
        {
            var a = new SmartLinkAccount();
            Assert.Null(a.ConfiguredListenPort);
        }

        [Fact]
        public void SmartLinkAccount_PortRoundTripsOnInstance()
        {
            var a = new SmartLinkAccount { ConfiguredListenPort = 5000 };
            Assert.Equal(5000, a.ConfiguredListenPort);
        }

        [Fact]
        public void SmartLinkAccount_PortCanBeCleared()
        {
            var a = new SmartLinkAccount { ConfiguredListenPort = 5000 };
            a.ConfiguredListenPort = null;
            Assert.Null(a.ConfiguredListenPort);
        }

        [Fact]
        public void StoredAccount_Serialization_PreservesPort()
        {
            var src = new SmartLinkAccount
            {
                FriendlyName = "Home Shack",
                Email = "test@example.com",
                ConfiguredListenPort = 4993
            };
            var stored = SmartLinkAccountManager.StoredAccount.FromSmartLinkAccount(src);
            var json = JsonSerializer.Serialize(stored);
            var restored = JsonSerializer.Deserialize<SmartLinkAccountManager.StoredAccount>(json)!;
            var final = restored.ToSmartLinkAccount();
            Assert.Equal(4993, final.ConfiguredListenPort);
        }

        [Fact]
        public void StoredAccount_Serialization_NullPortRoundTripsAsNull()
        {
            var src = new SmartLinkAccount
            {
                FriendlyName = "Home Shack",
                Email = "test@example.com",
                ConfiguredListenPort = null
            };
            var stored = SmartLinkAccountManager.StoredAccount.FromSmartLinkAccount(src);
            var json = JsonSerializer.Serialize(stored);
            var restored = JsonSerializer.Deserialize<SmartLinkAccountManager.StoredAccount>(json)!;
            var final = restored.ToSmartLinkAccount();
            Assert.Null(final.ConfiguredListenPort);
        }

        [Fact]
        public void StoredAccount_Deserialization_OldFormatDefaultsToNull()
        {
            // Simulates a SmartLinkAccounts.json written before Sprint 27 that
            // has no configuredListenPort field. Load must default to null so
            // existing users see zero behavior change until they opt in.
            const string oldJson = @"{
                ""friendlyName"": ""Legacy"",
                ""email"": ""old@example.com"",
                ""idTokenEncrypted"": """",
                ""refreshTokenEncrypted"": """",
                ""expiresAt"": ""2025-01-01T00:00:00"",
                ""lastUsed"": ""2025-01-01T00:00:00""
            }";
            var restored = JsonSerializer.Deserialize<SmartLinkAccountManager.StoredAccount>(oldJson)!;
            var final = restored.ToSmartLinkAccount();
            Assert.Null(final.ConfiguredListenPort);
        }

        [Fact]
        public void IsValidPort_RejectsBelowRange()
        {
            Assert.False(SmartLinkAccountManager.IsValidPort(1023));
            Assert.False(SmartLinkAccountManager.IsValidPort(0));
            Assert.False(SmartLinkAccountManager.IsValidPort(-1));
        }

        [Fact]
        public void IsValidPort_RejectsAboveRange()
        {
            Assert.False(SmartLinkAccountManager.IsValidPort(65536));
            Assert.False(SmartLinkAccountManager.IsValidPort(100000));
        }

        [Fact]
        public void IsValidPort_AcceptsBoundariesAndTypicalValues()
        {
            Assert.True(SmartLinkAccountManager.IsValidPort(1024));
            Assert.True(SmartLinkAccountManager.IsValidPort(4992));
            Assert.True(SmartLinkAccountManager.IsValidPort(65535));
        }

        [Fact]
        public void IsValidPort_AcceptsNullAsNoPreference()
        {
            Assert.True(SmartLinkAccountManager.IsValidPort(null));
        }

        [Fact]
        public void SetConfiguredPort_RejectsInvalidPortWithoutHittingDisk()
        {
            // Empty manager. An out-of-range port must fail validation before
            // the account lookup — so this returns false without needing an
            // account to exist and without invoking SaveAccounts (which would
            // touch disk).
            var mgr = new SmartLinkAccountManager();
            Assert.False(mgr.SetConfiguredPort("any@example.com", 50));
            Assert.False(mgr.SetConfiguredPort("any@example.com", 99999));
        }

        [Fact]
        public void SetConfiguredPort_ReturnsFalseForUnknownAccount()
        {
            var mgr = new SmartLinkAccountManager();
            // Valid port, but no such account exists in the empty manager.
            Assert.False(mgr.SetConfiguredPort("unknown@example.com", 5000));
        }

        [Fact]
        public void GetConfiguredPort_ReturnsNullForUnknownAccount()
        {
            var mgr = new SmartLinkAccountManager();
            Assert.Null(mgr.GetConfiguredPort("unknown@example.com"));
        }
    }
}
