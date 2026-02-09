using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Xml.Serialization;
using JJTrace;

namespace QrzLookup
{
    /// <summary>
    /// QRZ.com XML API client for callsign lookups.
    /// Pattern mirrors HamQTHLookup.CallbookLookup — session auth, background thread,
    /// event callback, in-memory callsign cache.
    /// </summary>
    public class QrzCallbookLookup
    {
        #region Login State

        public class LoginData
        {
            public string Username;
            public string Password;
            public string SessionKey;
            public bool NeedLogin = true;
            private const int maxLoginErrors = 3;
            public int LoginErrors = 0;
            public bool TooManyErrors => LoginErrors > maxLoginErrors;
        }

        public LoginData LoginInfo;

        /// <summary>
        /// Can a lookup be attempted? Checks credentials exist and we haven't
        /// exceeded the login failure threshold.
        /// </summary>
        public bool CanLookup
        {
            get
            {
                lock (LoginInfo)
                {
                    return !string.IsNullOrEmpty(LoginInfo.Username) &&
                           !string.IsNullOrEmpty(LoginInfo.Password) &&
                           !LoginInfo.TooManyErrors;
                }
            }
        }

        #endregion

        #region XML Response Models

        /// <summary>QRZ session element — contains session key or error.</summary>
        public class QrzSession
        {
            [XmlElement("Key")]
            public string Key { get; set; }

            [XmlElement("Error")]
            public string Error { get; set; }

            [XmlElement("Count")]
            public string Count { get; set; }

            [XmlElement("SubExp")]
            public string SubExp { get; set; }

            [XmlElement("GMTime")]
            public string GMTime { get; set; }

            [XmlElement("Remark")]
            public string Remark { get; set; }
        }

        /// <summary>QRZ callsign record — fields returned from a callsign lookup.</summary>
        public class QrzCallsign
        {
            [XmlElement("call")]
            public string Call { get; set; }

            [XmlElement("fname")]
            public string FirstName { get; set; }

            [XmlElement("name")]
            public string LastName { get; set; }

            [XmlElement("addr2")]
            public string City { get; set; }

            [XmlElement("state")]
            public string State { get; set; }

            [XmlElement("country")]
            public string Country { get; set; }

            [XmlElement("grid")]
            public string Grid { get; set; }

            [XmlElement("lat")]
            public string Latitude { get; set; }

            [XmlElement("lon")]
            public string Longitude { get; set; }

            [XmlElement("cqzone")]
            public string CQZone { get; set; }

            [XmlElement("ituzone")]
            public string ITUZone { get; set; }

            [XmlElement("lotw")]
            public string LOTW { get; set; }

            [XmlElement("eqsl")]
            public string EQSL { get; set; }

            [XmlElement("qslmgr")]
            public string QSLManager { get; set; }

            [XmlElement("county")]
            public string County { get; set; }

            [XmlElement("zip")]
            public string Zip { get; set; }
        }

        /// <summary>Root element of the QRZ XML response.</summary>
        [XmlRoot("QRZDatabase", Namespace = "http://xmldata.qrz.com")]
        public class QrzDatabase
        {
            [XmlElement("Session")]
            public QrzSession Session { get; set; }

            [XmlElement("Callsign")]
            public QrzCallsign Callsign { get; set; }
        }

        #endregion

        #region Event / Delegate

        public delegate void CallsignSearchDel(QrzDatabase result);
        public event CallsignSearchDel CallsignSearchEvent;

        private void OnCallsignSearch(QrzDatabase result)
        {
            CallsignSearchEvent?.Invoke(result);
        }

        #endregion

        #region Private Fields

        private const string BaseUrl = "https://xmldata.qrz.com/xml/current/";
        private const string AgentName = "JJFlexRadio";

        private readonly Dictionary<string, QrzDatabase> _callCache;
        private readonly HttpClient _httpClient;
        private Thread _webThread;

        #endregion

        #region Constructor

        public QrzCallbookLookup(string username, string password)
        {
            LoginInfo = new LoginData();
            lock (LoginInfo)
            {
                LoginInfo.Username = username;
                LoginInfo.Password = password;
            }

            _callCache = new Dictionary<string, QrzDatabase>(StringComparer.OrdinalIgnoreCase);
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        #endregion

        #region Login

        /// <summary>
        /// Authenticate with QRZ and obtain a session key.
        /// Must be called with LoginInfo locked.
        /// </summary>
        private bool SiteLogin()
        {
            try
            {
                var url = $"{BaseUrl}?username={Uri.EscapeDataString(LoginInfo.Username)}" +
                          $"&password={Uri.EscapeDataString(LoginInfo.Password)}" +
                          $"&agent={AgentName}";

                var response = _httpClient.GetStreamAsync(url).Result;
                var serializer = new XmlSerializer(typeof(QrzDatabase));
                var result = (QrzDatabase)serializer.Deserialize(response);

                if (result?.Session == null || !string.IsNullOrEmpty(result.Session.Error))
                {
                    var err = result?.Session?.Error ?? "No session returned";
                    Tracing.TraceLine("QRZ login error: " + err, TraceLevel.Warning);
                    LoginInfo.LoginErrors++;
                    return false;
                }

                LoginInfo.SessionKey = result.Session.Key;
                LoginInfo.LoginErrors = 0;
                Tracing.TraceLine("QRZ login OK, key=" + LoginInfo.SessionKey?.Substring(0, 4) + "...",
                                  TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("QRZ login exception: " + ex.Message, TraceLevel.Warning);
                LoginInfo.LoginErrors++;
                return false;
            }
        }

        #endregion

        #region Lookup

        /// <summary>
        /// Initiate a callsign lookup. Returns immediately — result arrives via
        /// CallsignSearchEvent on a background thread.
        /// </summary>
        public void LookupCall(string callSign)
        {
            if (string.IsNullOrWhiteSpace(callSign)) return;
            var upperCall = callSign.Trim().ToUpperInvariant();

            // Check cache first.
            if (_callCache.TryGetValue(upperCall, out var cached))
            {
                OnCallsignSearch(cached);
                return;
            }

            // Background lookup.
            _webThread = new Thread(SiteLookupCall) { Name = "QrzLookupThread", IsBackground = true };
            _webThread.Start(upperCall);
        }

        /// <summary>
        /// Background worker — performs login (if needed) then callsign lookup.
        /// </summary>
        private void SiteLookupCall(object state)
        {
            if (!CanLookup) return;
            var callSign = (string)state;

            lock (LoginInfo)
            {
                // Login if needed.
                if (LoginInfo.NeedLogin)
                {
                    LoginInfo.NeedLogin = !SiteLogin();
                }

                if (LoginInfo.NeedLogin)
                {
                    // Login failed — fire event with null so caller knows lookup didn't work.
                    OnCallsignSearch(null);
                    return;
                }
            }

            QrzDatabase result = null;
            try
            {
                string sessionKey;
                lock (LoginInfo) { sessionKey = LoginInfo.SessionKey; }

                var url = $"{BaseUrl}?s={Uri.EscapeDataString(sessionKey)}&callsign={Uri.EscapeDataString(callSign)}";
                var response = _httpClient.GetStreamAsync(url).Result;
                var serializer = new XmlSerializer(typeof(QrzDatabase));
                result = (QrzDatabase)serializer.Deserialize(response);

                // Check for session timeout — re-login once and retry.
                if (result?.Session != null && !string.IsNullOrEmpty(result.Session.Error))
                {
                    var error = result.Session.Error;
                    if (error.Contains("Session Timeout", StringComparison.OrdinalIgnoreCase) ||
                        error.Contains("Invalid session key", StringComparison.OrdinalIgnoreCase))
                    {
                        Tracing.TraceLine("QRZ session expired, re-authenticating", TraceLevel.Info);
                        lock (LoginInfo)
                        {
                            LoginInfo.NeedLogin = true;
                            LoginInfo.NeedLogin = !SiteLogin();
                            if (!LoginInfo.NeedLogin)
                            {
                                sessionKey = LoginInfo.SessionKey;
                            }
                            else
                            {
                                OnCallsignSearch(null);
                                return;
                            }
                        }

                        // Retry with new session key.
                        url = $"{BaseUrl}?s={Uri.EscapeDataString(sessionKey)}&callsign={Uri.EscapeDataString(callSign)}";
                        response = _httpClient.GetStreamAsync(url).Result;
                        result = (QrzDatabase)serializer.Deserialize(response);
                    }
                }

                // Validate we got callsign data.
                if (result?.Callsign == null)
                {
                    result = null;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("QRZ lookup exception for " + callSign + ": " + ex.Message,
                                  TraceLevel.Warning);
                result = null;
            }

            // Cache successful lookups.
            if (result?.Callsign != null)
            {
                result.Callsign.Call = result.Callsign.Call?.ToUpperInvariant();
                var key = result.Callsign.Call ?? callSign;
                if (!_callCache.ContainsKey(key))
                {
                    _callCache[key] = result;
                }
            }

            OnCallsignSearch(result);
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Clean up resources. Call when leaving Logging Mode.
        /// </summary>
        public void Finished()
        {
            try
            {
                _httpClient?.Dispose();
            }
            catch { }
        }

        #endregion
    }
}
