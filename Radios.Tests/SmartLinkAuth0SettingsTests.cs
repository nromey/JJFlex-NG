#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Auth0 domain, client ID and redirect URI come from one owner with
    /// today's values as defaults, overridable by an optional file the app
    /// never writes (#332). Windowless; no sign-in, no network. Each test reads
    /// its own temporary folder through <see cref="SmartLinkAuth0Settings.Load"/>,
    /// never the operator's settings root.
    /// </summary>
    public sealed class SmartLinkAuth0SettingsTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "jjflex-auth0-" + Guid.NewGuid().ToString("N"));

        public SmartLinkAuth0SettingsTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private void Write(string json) => File.WriteAllText(Path.Combine(_dir, SmartLinkAuth0Settings.FileName), json);

        private static void AssertDefaults(SmartLinkAuth0Settings s)
        {
            Assert.Equal(SmartLinkAuth0Settings.DefaultDomain, s.Domain);
            Assert.Equal(SmartLinkAuth0Settings.DefaultClientId, s.ClientId);
            Assert.Equal(SmartLinkAuth0Settings.DefaultRedirectUri, s.RedirectUri);
        }

        [Fact]
        public void A_missing_file_gives_todays_values_and_is_not_created()
        {
            var s = SmartLinkAuth0Settings.Load(_dir, out var notes);
            AssertDefaults(s);
            Assert.Empty(notes);
            Assert.Equal("/mobile", s.RedirectPath);
            // The app never writes the file: a written copy would pin today's
            // values over a later build's new defaults.
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Theory]
        [InlineData("{ this is not json")]
        [InlineData("[\"domain\", \"example.com\"]")]
        [InlineData("")]
        public void A_corrupt_file_gives_todays_values_with_a_trace_line(string json)
        {
            Write(json);
            var s = SmartLinkAuth0Settings.Load(_dir, out var notes);
            AssertDefaults(s);
            Assert.NotEmpty(notes);
            Assert.All(notes, n => Assert.Contains(SmartLinkAuth0Settings.FileName, n));
        }

        [Fact]
        public void A_valid_file_gives_its_values_and_is_left_unchanged()
        {
            const string json = "{ \"domain\": \"auth.example.com\", \"clientId\": \"Abc123_xyz-9\", \"redirectUri\": \"https://auth.example.com/native\" }";
            Write(json);
            var s = SmartLinkAuth0Settings.Load(_dir, out var notes);
            Assert.Equal("auth.example.com", s.Domain);
            Assert.Equal("Abc123_xyz-9", s.ClientId);
            Assert.Equal("https://auth.example.com/native", s.RedirectUri);
            Assert.Equal("/native", s.RedirectPath);
            Assert.Empty(notes);
            Assert.Equal(json, File.ReadAllText(Path.Combine(_dir, SmartLinkAuth0Settings.FileName)));
        }

        [Theory]
        [InlineData("domain", "https://auth.example.com")]
        [InlineData("domain", "auth.example.com/path")]
        [InlineData("domain", "auth.example.com:443")]
        [InlineData("domain", "localhost")]
        [InlineData("domain", "")]
        [InlineData("clientId", "has a space")]
        [InlineData("clientId", "semi;colon")]
        [InlineData("redirectUri", "http://auth.example.com/mobile")]
        [InlineData("redirectUri", "/mobile")]
        [InlineData("redirectUri", "https://user:pw@auth.example.com/mobile")]
        public void A_bad_field_falls_back_alone(string field, string value)
        {
            var fields = new Dictionary<string, string>
            {
                ["domain"] = "auth.example.com",
                ["clientId"] = "Abc123",
                ["redirectUri"] = "https://auth.example.com/native",
            };
            fields[field] = value;
            Write("{" + string.Join(",", fields.Select(kv =>
                "\"" + kv.Key + "\": " + System.Text.Json.JsonSerializer.Serialize(kv.Value))) + "}");

            var s = SmartLinkAuth0Settings.Load(_dir, out var notes);
            Assert.Single(notes);
            Assert.Contains("\"" + field + "\"", notes[0]);
            Assert.Equal(field == "domain" ? SmartLinkAuth0Settings.DefaultDomain : "auth.example.com", s.Domain);
            Assert.Equal(field == "clientId" ? SmartLinkAuth0Settings.DefaultClientId : "Abc123", s.ClientId);
            Assert.Equal(field == "redirectUri" ? SmartLinkAuth0Settings.DefaultRedirectUri : "https://auth.example.com/native", s.RedirectUri);
        }

        [Fact]
        public void A_field_left_out_keeps_todays_value_and_says_nothing()
        {
            Write("{ \"clientId\": \"Abc123\" }");
            var s = SmartLinkAuth0Settings.Load(_dir, out var notes);
            Assert.Equal(SmartLinkAuth0Settings.DefaultDomain, s.Domain);
            Assert.Equal("Abc123", s.ClientId);
            Assert.Equal(SmartLinkAuth0Settings.DefaultRedirectUri, s.RedirectUri);
            Assert.Empty(notes);
        }

        [Fact]
        public void Another_field_is_ignored_and_named_by_key_never_by_value()
        {
            Write("{ \"clientId\": \"Abc123\", \"client_secret\": \"hunter2-do-not-trace\" }");
            var s = SmartLinkAuth0Settings.Load(_dir, out var notes);
            Assert.Equal("Abc123", s.ClientId);
            string note = Assert.Single(notes);
            Assert.Contains("client_secret", note);
            Assert.DoesNotContain("hunter2", note);
        }

        // ── The three sites hold no copy ─────────────────────────────────

        private static readonly string[] Sites =
        {
            "Radios/AuthFormWebView2.cs",
            "JJFlexWpf/Dialogs/AuthDialog.xaml.cs",
            "Radios/SmartLinkAccountManager.cs",
        };

        private static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string full = Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "not found: " + full);
            return File.ReadAllText(full);
        }

        [Fact]
        public void None_of_the_three_sign_in_sites_holds_its_own_domain_or_client_id()
        {
            // Positive control: the owner itself does hold them, so the scan
            // is reading real files and looking for the right strings.
            string owner = RepoFile("Radios/SmartLinkAuth0Settings.cs");
            Assert.Contains(SmartLinkAuth0Settings.DefaultDomain, owner);
            Assert.Contains(SmartLinkAuth0Settings.DefaultClientId, owner);

            foreach (string site in Sites)
            {
                string text = RepoFile(site);
                Assert.False(text.Contains(SmartLinkAuth0Settings.DefaultDomain, StringComparison.OrdinalIgnoreCase),
                    site + " holds the Auth0 domain itself; read SmartLinkAuth0Settings");
                Assert.False(text.Contains(SmartLinkAuth0Settings.DefaultClientId, StringComparison.Ordinal),
                    site + " holds the Auth0 client ID itself; read SmartLinkAuth0Settings");
                Assert.False(text.Contains("\"/mobile\"", StringComparison.Ordinal),
                    site + " recognises the redirect by a literal path; read SmartLinkAuth0Settings.RedirectPath");
                Assert.Contains("SmartLinkAuth0Settings.Current", text);
            }
        }
    }
}
