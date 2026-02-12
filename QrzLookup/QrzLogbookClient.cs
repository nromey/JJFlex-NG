using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using JJTrace;

namespace QrzLookup
{
    /// <summary>
    /// Uploads QSO records to the operator's QRZ.com logbook.
    /// API-key auth (no session management needed — unlike QrzCallbookLookup).
    /// Fire-and-forget: each upload runs on a background thread.
    /// </summary>
    public class QrzLogbookClient
    {
        #region Constants

        private const string ApiUrl = "https://logbook.qrz.com/api";
        private const int MaxConsecutiveErrors = 5;

        #endregion

        #region Result Classes

        /// <summary>
        /// Result from a static ValidateApiKey call.
        /// </summary>
        public class ValidateResult
        {
            public bool Success;
            public string ErrorMessage;
            public string LogbookCallSign;
            public int TotalQSOs;
            public int ConfirmedQSOs;
            public int DXCCCount;
        }

        #endregion

        #region Delegate / Event

        public delegate void UploadResultDel(bool success, string callSign, string errorMessage);

        /// <summary>
        /// Raised on a background thread after each upload attempt.
        /// The consumer must marshal to the UI thread if needed.
        /// </summary>
        public event UploadResultDel UploadResultEvent;

        private void OnUploadResult(bool success, string callSign, string errorMessage)
        {
            UploadResultEvent?.Invoke(success, callSign, errorMessage);
        }

        #endregion

        #region Fields

        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private int _consecutiveErrors;

        #endregion

        #region Constructor

        public QrzLogbookClient(string apiKey, string version)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("JJFlexRadio/" + version);
        }

        #endregion

        #region Upload

        /// <summary>
        /// Fire-and-forget upload of one QSO to QRZ Logbook.
        /// Runs on a background thread so the caller returns immediately.
        /// </summary>
        public void UploadQSO(string adifRecord, string callSign)
        {
            if (string.IsNullOrEmpty(adifRecord)) return;
            if (_consecutiveErrors >= MaxConsecutiveErrors)
            {
                Tracing.TraceLine("QrzLogbook: auto-disabled after " + MaxConsecutiveErrors +
                                  " consecutive errors", TraceLevel.Warning);
                return;
            }

            var thread = new Thread(DoUpload)
            {
                Name = "QrzLogbookUpload",
                IsBackground = true
            };
            thread.Start(new string[] { adifRecord, callSign });
        }

        private void DoUpload(object state)
        {
            var args = (string[])state;
            var adifRecord = args[0];
            var callSign = args[1];

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("KEY", _apiKey),
                    new KeyValuePair<string, string>("ACTION", "INSERT"),
                    new KeyValuePair<string, string>("ADIF", adifRecord)
                });

                var response = _httpClient.PostAsync(ApiUrl, content).Result;
                var body = response.Content.ReadAsStringAsync().Result;
                var parsed = ParseResponse(body);

                if (parsed.TryGetValue("RESULT", out var result) && result == "OK")
                {
                    Interlocked.Exchange(ref _consecutiveErrors, 0);
                    Tracing.TraceLine("QrzLogbook: uploaded " + callSign, TraceLevel.Info);
                    OnUploadResult(true, callSign, null);
                }
                else
                {
                    var errMsg = parsed.TryGetValue("REASON", out var reason) ? reason : body;
                    Interlocked.Increment(ref _consecutiveErrors);
                    Tracing.TraceLine("QrzLogbook: upload failed for " + callSign + ": " + errMsg,
                                      TraceLevel.Warning);
                    OnUploadResult(false, callSign, errMsg);
                }
            }
            catch (AggregateException aex) when (aex.InnerException is HttpRequestException)
            {
                Interlocked.Increment(ref _consecutiveErrors);
                Tracing.TraceLine("QrzLogbook: network error uploading " + callSign + ": " +
                                  aex.InnerException.Message, TraceLevel.Warning);
                OnUploadResult(false, callSign, "Network error: " + aex.InnerException.Message);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consecutiveErrors);
                Tracing.TraceLine("QrzLogbook: exception uploading " + callSign + ": " +
                                  ex.Message, TraceLevel.Warning);
                OnUploadResult(false, callSign, "Error: " + ex.Message);
            }
        }

        #endregion

        #region Validate

        /// <summary>
        /// Validate an API key by calling the STATUS action. Blocking; meant for
        /// the Settings dialog (called on the UI thread with a wait cursor).
        /// </summary>
        public static ValidateResult ValidateApiKey(string apiKey)
        {
            var result = new ValidateResult();

            if (string.IsNullOrEmpty(apiKey))
            {
                result.Success = false;
                result.ErrorMessage = "API key is required.";
                return result;
            }

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("KEY", apiKey),
                    new KeyValuePair<string, string>("ACTION", "STATUS")
                });

                var response = client.PostAsync(ApiUrl, content).Result;
                var body = response.Content.ReadAsStringAsync().Result;
                var parsed = ParseResponse(body);

                if (parsed.TryGetValue("RESULT", out var res) && res == "OK")
                {
                    result.Success = true;
                    parsed.TryGetValue("CALLSIGN", out var cs);
                    result.LogbookCallSign = cs ?? "";

                    if (parsed.TryGetValue("COUNT", out var countStr) &&
                        int.TryParse(countStr, out var count))
                        result.TotalQSOs = count;

                    if (parsed.TryGetValue("CONFIRMED", out var confStr) &&
                        int.TryParse(confStr, out var conf))
                        result.ConfirmedQSOs = conf;

                    if (parsed.TryGetValue("DXCC", out var dxccStr) &&
                        int.TryParse(dxccStr, out var dxcc))
                        result.DXCCCount = dxcc;
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = parsed.TryGetValue("REASON", out var reason)
                        ? reason : body;
                }
            }
            catch (AggregateException aex) when (aex.InnerException is HttpRequestException)
            {
                result.Success = false;
                result.ErrorMessage = "Could not reach QRZ.com. Check your internet connection.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "Connection error: " + ex.Message;
            }

            return result;
        }

        #endregion

        #region ADIF Helpers

        /// <summary>
        /// Convert a LogSession field dictionary into a single ADIF record string
        /// suitable for upload to QRZ Logbook.
        /// </summary>
        public static string FieldsToAdifRecord(
            Dictionary<string, adif.LogFieldElement> fields, string stationCallSign)
        {
            if (fields == null || fields.Count == 0) return null;

            var sb = new StringBuilder();

            foreach (var kvp in fields)
            {
                var tag = kvp.Key;
                var data = kvp.Value?.Data;

                // Skip internal pseudo-fields (start with '$').
                if (tag.StartsWith("$")) continue;
                if (string.IsNullOrEmpty(data)) continue;

                // Normalize dates from MM/dd/yyyy to ADIF yyyyMMdd.
                if (tag == adif.AdifTags.ADIF_DateOn || tag == adif.AdifTags.ADIF_DateOff)
                {
                    if (DateTime.TryParseExact(data, "MM/dd/yyyy",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        data = dt.ToString("yyyyMMdd");
                    }
                }

                // Normalize modes for QRZ compatibility.
                if (tag == adif.AdifTags.ADIF_Mode)
                {
                    switch (data.ToUpperInvariant())
                    {
                        case "LSB":
                        case "USB":
                            data = "SSB";
                            break;
                        case "CWR":
                            data = "CW";
                            break;
                        case "FSK":
                        case "FSKR":
                            data = "RTTY";
                            break;
                    }
                }

                sb.Append(BuildAdifTag(tag, data));
            }

            // Add STATION_CALLSIGN if not already present.
            if (!string.IsNullOrEmpty(stationCallSign) && !fields.ContainsKey("STATION_CALLSIGN"))
            {
                sb.Append(BuildAdifTag("STATION_CALLSIGN", stationCallSign));
            }

            sb.Append("<EOR>");
            return sb.ToString();
        }

        /// <summary>
        /// Build a single ADIF tag: &lt;TAG:len&gt;value
        /// </summary>
        public static string BuildAdifTag(string tag, string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return "<" + tag.ToUpperInvariant() + ":" + value.Length + ">" + value;
        }

        #endregion

        #region Response Parser

        /// <summary>
        /// Parse the QRZ Logbook API response (URL-encoded key=value pairs separated by &amp;).
        /// Avoids a dependency on System.Web.
        /// </summary>
        internal static Dictionary<string, string> ParseResponse(string body)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(body)) return result;

            var pairs = body.Split('&');
            foreach (var pair in pairs)
            {
                var idx = pair.IndexOf('=');
                if (idx < 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, idx)).Trim();
                var val = Uri.UnescapeDataString(pair.Substring(idx + 1)).Trim();
                result[key] = val;
            }

            return result;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Dispose the HttpClient. Called when leaving Logging Mode.
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
