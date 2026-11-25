#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Where SmartLink sign-in talks to: FlexRadio's Auth0 tenant (the domain),
    /// the client ID JJ Flexible signs in as, and the redirect URI the sign-in
    /// page returns to (#332). One owner, read by every sign-in, token refresh,
    /// password reset and sign-up path, so a change Flex makes to its Auth0
    /// settings is one edit rather than three copies, and does not need a new
    /// build when an operator can be told the new values.
    /// </summary>
    /// <remarks>
    /// <para><b>Today's values are the defaults, and a missing or broken file
    /// never breaks sign-in.</b> The optional file
    /// <see cref="FileName"/> under <see cref="RadioConfig.AppDataRoot"/> may
    /// override any of the three fields. A missing file is the normal case and
    /// gives the defaults. A file that cannot be read or parsed gives the
    /// defaults, with a trace line. A field that is missing, empty or not of its
    /// form (a domain that is not a bare host name, a client ID with anything
    /// but letters, digits, hyphen and underscore, a redirect that is not an
    /// absolute https address) falls back to today's value for that field
    /// alone, with a trace line. Failing closed would be wrong here: the
    /// failure it would cause is the one this exists to prevent.</para>
    /// <para><b>JJ Flexible never creates or writes this file.</b> A copy
    /// written by the app would pin today's values and hide a later build's new
    /// defaults, which is exactly the rotation case the file is for.</para>
    /// <para><b>These values are not secrets</b> (a public client ID and a
    /// redirect address, both visible in any capture of the sign-in), so the
    /// file needs no protection. Only the three named fields are read; anything
    /// else in it is ignored, and named in the trace by key only, never by
    /// value, so the file does not quietly become a place a secret is kept.</para>
    /// <para>Read through <see cref="RadioConfig.AppDataRoot"/>, never the
    /// Windows application data folder plus a literal, so a
    /// JJFLEX_CONFIG_DIR run reads its own tree.</para>
    /// </remarks>
    public sealed class SmartLinkAuth0Settings
    {
        /// <summary>The optional override file's name, under the settings root.</summary>
        public const string FileName = "smartlink-auth0.json";

        /// <summary>Today's Auth0 tenant.</summary>
        public const string DefaultDomain = "frtest.auth0.com";

        /// <summary>Today's client ID.</summary>
        public const string DefaultClientId = "4Y9fEIIsVYyQo5u6jr7yBWc4lV5ugC2m";

        /// <summary>Today's redirect URI.</summary>
        public const string DefaultRedirectUri = "https://frtest.auth0.com/mobile";

        /// <summary>The defaults, as a settings object.</summary>
        public static SmartLinkAuth0Settings Defaults { get; } =
            new SmartLinkAuth0Settings(DefaultDomain, DefaultClientId, DefaultRedirectUri);

        private SmartLinkAuth0Settings(string domain, string clientId, string redirectUri)
        {
            Domain = domain;
            ClientId = clientId;
            RedirectUri = redirectUri;
        }

        /// <summary>The tenant's host name, bare: no scheme, path or port.</summary>
        public string Domain { get; }

        /// <summary>The client ID JJ Flexible signs in as.</summary>
        public string ClientId { get; }

        /// <summary>The absolute https address the sign-in page returns to.</summary>
        public string RedirectUri { get; }

        /// <summary>
        /// The path of <see cref="RedirectUri"/>, which is how a sign-in page
        /// recognises that the redirect arrived ("/mobile" today).
        /// </summary>
        public string RedirectPath => new Uri(RedirectUri).AbsolutePath;

        /// <summary>
        /// The settings in force now, read from the settings root. Read once per
        /// sign-in or token request by its caller, so one exchange never mixes
        /// two readings.
        /// </summary>
        public static SmartLinkAuth0Settings Current => Load(RadioConfig.AppDataRoot, out _);

        /// <summary>
        /// Read the file in <paramref name="folder"/>. Never throws.
        /// <paramref name="notes"/> receives each fallback's reason, as traced,
        /// so a test can see it.
        /// </summary>
        public static SmartLinkAuth0Settings Load(string? folder, out IReadOnlyList<string> notes)
        {
            var said = new List<string>();
            notes = said;
            if (string.IsNullOrEmpty(folder)) return Defaults;

            string path;
            string text;
            try
            {
                path = Path.Combine(folder, FileName);
                if (!File.Exists(path)) return Defaults;
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Note(said, "could not be read (" + ex.GetType().Name + "); using today's values");
                return Defaults;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                root = doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                Note(said, "is not valid JSON (" + ex.GetType().Name + "); using today's values");
                return Defaults;
            }
            if (root.ValueKind != JsonValueKind.Object)
            {
                Note(said, "is not a JSON object; using today's values");
                return Defaults;
            }

            string? domain = null, clientId = null, redirect = null;
            foreach (JsonProperty p in root.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "domain": domain = StringOf(p); break;
                    case "clientId": clientId = StringOf(p); break;
                    case "redirectUri": redirect = StringOf(p); break;
                    default:
                        // By key only, never by value.
                        Note(said, "field \"" + p.Name + "\" is not one of domain, clientId, redirectUri; ignored");
                        break;
                }
            }

            return new SmartLinkAuth0Settings(
                Field(said, "domain", domain, IsBareHost, DefaultDomain),
                Field(said, "clientId", clientId, IsClientId, DefaultClientId),
                Field(said, "redirectUri", redirect, IsHttpsAddress, DefaultRedirectUri));
        }

        private static string? StringOf(JsonProperty p) =>
            p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : "";

        private static string Field(List<string> said, string name, string? value,
            Func<string, bool> valid, string fallback)
        {
            if (value == null) return fallback;
            string trimmed = value.Trim();
            if (trimmed.Length > 0 && valid(trimmed)) return trimmed;
            Note(said, "field \"" + name + "\" is empty or not of its form; using today's value for it");
            return fallback;
        }

        private static readonly Regex ClientIdForm = new Regex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);

        internal static bool IsClientId(string value) => ClientIdForm.IsMatch(value);

        internal static bool IsBareHost(string value) =>
            value.IndexOfAny(new[] { '/', ':', '@', '?', '#', ' ' }) < 0
            && Uri.CheckHostName(value) == UriHostNameType.Dns
            && value.Contains('.');

        internal static bool IsHttpsAddress(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo);

        private static void Note(List<string> said, string what)
        {
            string line = "SmartLinkAuth0Settings: " + FileName + " " + what;
            said.Add(line);
            try { Tracing.TraceLine(line, TraceLevel.Warning); }
            catch { /* a trace failure never stands between the operator and sign-in */ }
        }
    }
}
