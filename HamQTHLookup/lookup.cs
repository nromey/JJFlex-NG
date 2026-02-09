using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Timers;
//using System.Windows.Forms;
using System.Xml.Serialization;
//using JJTrace;

namespace HamQTHLookup
{
    public class CallbookLookup
    {
        public class LoginData
        {
            public string LoginID;
            public string Password;
            public string SessionID;
            public bool NeedLogin = true;
            private const int maxLoginErrors = 3;
            public int LoginErrors = 0;
            public bool TooManyErrors { get { return (LoginErrors > maxLoginErrors); } }
        }
        public LoginData LoginInfo;

        /// <summary>
        /// Can a lookup be attempted?
        /// </summary>
        public bool CanLookup
        {
            get
            {
                bool rv;
                lock (LoginInfo)
                {
                    rv = (!string.IsNullOrEmpty(LoginInfo.LoginID) &&
                          !string.IsNullOrEmpty(LoginInfo.Password) &&
                          !LoginInfo.TooManyErrors);
                }
                return rv;
            }
        }

        private const double loginTimeout = 59 * 60 * 1000; // 59 minutes
        private System.Timers.Timer loginTimer;

        private const string siteBaseAddress = "https://www.hamqth.com";
        private XmlRootAttribute hamqthRoot;

        private static States stateMap = new States();

        /// <summary>
        /// HamQTH Session
        /// </summary>
        public class Session
        {
            public string session_id;
            public string error;
        }
        /// <summary>
        /// HamQTH operator data returned from a search.
        /// </summary>
        public class Search
        {
            public string callsign;
            public string nick;
            public string qth;
            public string adr_city;
            public string adr_zip;
            public string adr_country;
            public string adr_adif;
            public string district;
            [XmlIgnore]
            public string State
            {
                get
                {
                    string rv = (!string.IsNullOrEmpty(district)) ? district : "";
                    if ((rv == "") && !string.IsNullOrEmpty(adr_zip) && (adr_zip.Length >= 5))
                    {
                        int n = 0;
                        if (System.Int32.TryParse(adr_zip.Substring(0, 3), out n))
                        {
                            rv = stateMap.State[n];
                        }
                    }
                    return rv;
                }
            }
            public string country;
            public string adif;
            public string itu;
            public string cq;
            public string grid;
            public string latitude;
            [XmlIgnore]
            public string lat
            {
                get
                {
                    string rv = latitude;
                    double l = 0;
                    if (System.Double.TryParse(latitude, out l))
                    {
                        char ns = (l < 0) ? 'S' : 'N';
                        l = Math.Abs(l) + 0.5;
                        rv = ((int)l).ToString() + ns;
                    }
                    return rv;
                }
            }
            public string longitude;
            [XmlIgnore]
            public string lgt
            {
                get
                {
                    string rv = longitude;
                    double l = 0;
                    if (System.Double.TryParse(longitude, out l))
                    {
                        char ew = (l < 0) ? 'W' : 'E';
                        l = Math.Abs(l) + 0.5;
                        rv = ((int)l).ToString() + ew;
                    }
                    return rv;
                }
            }
            [XmlIgnore]
            public string LatLong
            {
                get { return lat + '/' + lgt; }
            }
            public string continent;
            public string utc_offset;
            public string lotw;
            public string qsl;
            public string qsldirect;
            public string eqsl;
            public string email;
            public string jabber;
            public string skype;
            public string birth_year;
            public string lic_year;
            public string web;
            public string picture;
        }
        /// <summary>
        /// Data returned by HamQTH search.
        /// </summary>
        public class HamQTH
        {
            public Session session;
            public Search search;
        }

        private Dictionary<string, HamQTH> callDictionary;
        private Thread webThread;
        public delegate void CallsignSearchDel(HamQTH e);
        public event CallsignSearchDel CallsignSearchEvent;
        private void onCallsignSearch(HamQTH qth)
        {
            if (CallsignSearchEvent != null)
            {
                CallsignSearchEvent(qth);
            }
        }

        public CallbookLookup(string id, string password)
        {
            //Tracing.TraceLine("Lookup:" + id + ' ' + password, TraceLevel.Info);
            LoginInfo = new LoginData();
            lock (LoginInfo)
            {
                LoginInfo.LoginID = id;
                LoginInfo.Password = password;
            }

            hamqthRoot = new XmlRootAttribute();
            hamqthRoot.ElementName = "HamQTH";
            hamqthRoot.Namespace = "https://www.hamqth.com";
            hamqthRoot.IsNullable = true;

            callDictionary = new Dictionary<string, HamQTH>();

            loginTimer = new System.Timers.Timer();
            // Note:  The timer is started upon a lookup following the previous timer's experation.
            loginTimer.AutoReset = false;
            loginTimer.Elapsed += loginTimerHandler;
            loginTimer.Interval = loginTimeout;
            loginTimer.Enabled = false;
        }

        /// <summary>
        /// Login to the site.
        /// LoginInfo should be locked.
        /// </summary>
        /// <returns>True on success</returns>
        private bool siteLogin()
        {
            //Tracing.TraceLine("siteLogin:" + siteBaseAddress, TraceLevel.Info);
            bool rv = true;
            WebClient web = null;
            web = new WebClient();
            web.BaseAddress = siteBaseAddress;
            Stream page = null;
            HamQTH dat = null;
            try
            {
                page = web.OpenRead("/xml.php?u=" + LoginInfo.LoginID + "&p=" + LoginInfo.Password);
                XmlSerializer xs = new XmlSerializer(typeof(HamQTH), hamqthRoot);
                dat = (HamQTH)xs.Deserialize(page);
                if (dat.session.error != null)
                {
                    throw (new Exception(dat.session.error));
                }
                else
                {
                    LoginInfo.SessionID = dat.session.session_id;
                    loginTimer.Start();
                }
            }
            catch (Exception ex)
            {
                //Tracing.TraceLine("siteLogin:exception:" + ex.Message + Environment.NewLine + ex.InnerException.Message, TraceLevel.Error);
                rv = false;
            }
            finally
            {
                if (page != null) page.Dispose();
                if (web != null) web.Dispose();
            }
            if (rv) LoginInfo.LoginErrors = 0;
            else LoginInfo.LoginErrors++;
            return rv;
        }

        private void siteLookupCall(object o)
        {
            if (!CanLookup) return;
            string callSign = (string)o;
            HamQTH rv = null;
            WebClient web = null;
            web = new WebClient();
            web.BaseAddress = siteBaseAddress;
            Stream page = null;
            lock (LoginInfo)
            {
                // login if needed.
                if (LoginInfo.NeedLogin) LoginInfo.NeedLogin = !siteLogin();

                if (!LoginInfo.NeedLogin)
                {
                    try
                    {
                        page = web.OpenRead("/xml.php?id=" + LoginInfo.SessionID + "&callsign=" + callSign + "&prg=JJRadio");
                        XmlSerializer xs = new XmlSerializer(typeof(HamQTH), hamqthRoot);
                        rv = (HamQTH)xs.Deserialize(page);
                        if (((rv.session != null) &&
                            !string.IsNullOrEmpty(rv.session.error)) ||
                            (rv.search == null)) rv = null;
                    }
                    catch (Exception ex)
                    {
                        //Tracing.TraceLine("siteLookupCall:exception:" + ex.Message, TraceLevel.Error);
                        rv = null;
                    }
                    finally
                    {
                        if (page != null) page.Dispose();
                        if (web != null) web.Dispose();
                    }
                }
            }
            if (rv != null)
            {
                rv.search.callsign = rv.search.callsign.ToUpper();
                if (!callDictionary.Keys.Contains(rv.search.callsign))
                {
                    callDictionary.Add(rv.search.callsign, rv);
                }
            }
            onCallsignSearch(rv);
        }

        /// <summary>
        /// Lookup the call using HamQTH.
        /// </summary>
        /// <param name="callSign"></param>
        public void LookupCall(string callSign)
        {
            HamQTH rv = null;
            if (!callDictionary.TryGetValue(callSign.ToUpper(), out rv))
            {
                webThread = new Thread(siteLookupCall);
                webThread.Name = "webThread";
                // webThread posts the event.
                webThread.Start(callSign);
            }
            else onCallsignSearch(rv);
        }

        private void loginTimerHandler(object sender, ElapsedEventArgs e)
        {
            //Tracing.TraceLine("loginTimerHandler:" + e.SignalTime.ToString(), TraceLevel.Info);
            lock (LoginInfo)
            {
                LoginInfo.NeedLogin = true;
            }
        }

        public void Finished()
        {
            if (loginTimer != null) loginTimer.Dispose();
            try
            {
                if ((webThread != null) && webThread.IsAlive) webThread.Abort();
            }
            catch { }
        }

        #region Credential Validation

        /// <summary>
        /// Result of a credential test — success/failure and error detail.
        /// </summary>
        public class TestLoginResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Test HamQTH credentials by attempting a login. Returns immediately with a result
        /// object. Does NOT create a persistent session — use this for one-shot validation
        /// in the settings dialog.
        /// </summary>
        public static TestLoginResult TestLogin(string username, string password)
        {
            var result = new TestLoginResult();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                result.Success = false;
                result.ErrorMessage = "Username and password are required.";
                return result;
            }

            WebClient web = null;
            Stream page = null;
            try
            {
                web = new WebClient();
                web.BaseAddress = siteBaseAddress;
                page = web.OpenRead("/xml.php?u=" + username + "&p=" + password);

                var root = new XmlRootAttribute
                {
                    ElementName = "HamQTH",
                    Namespace = "https://www.hamqth.com",
                    IsNullable = true
                };
                var xs = new XmlSerializer(typeof(HamQTH), root);
                var dat = (HamQTH)xs.Deserialize(page);

                if (dat?.session == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "No response from HamQTH.";
                    return result;
                }

                if (!string.IsNullOrEmpty(dat.session.error))
                {
                    result.Success = false;
                    result.ErrorMessage = dat.session.error;
                    return result;
                }

                // Login succeeded — we got a session ID.
                result.Success = true;
                return result;
            }
            catch (WebException)
            {
                result.Success = false;
                result.ErrorMessage = "Could not reach HamQTH.com. Check your internet connection.";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "Connection error: " + ex.Message;
                return result;
            }
            finally
            {
                if (page != null) page.Dispose();
                if (web != null) web.Dispose();
            }
        }

        #endregion
    }
}
