using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Legacy Auth0 form using WebBrowser (IE) control.
    /// Use CreateAuthForm() factory method which returns WebView2 version.
    /// </summary>
    [Obsolete("Use AuthForm.CreateAuthForm() which returns the WebView2 version")]
    public partial class AuthForm : Form
    {
        public string[] Tokens;
        private Label urlLabel;

        /// <summary>
        /// Factory method to create the appropriate auth form.
        /// Returns WebView2-based form for modern browser support.
        /// </summary>
        public static Form CreateAuthForm()
        {
            return new AuthFormWebView2();
        }

        public AuthForm()
        {
            InitializeComponent();
            // Keep the embedded browser quiet and sandboxed.
            Browser.ScriptErrorsSuppressed = true;
            Browser.IsWebBrowserContextMenuEnabled = false;
            Browser.WebBrowserShortcutsEnabled = false;
            Browser.AllowWebBrowserDrop = false;
            // Add a small banner to show the auth URL for troubleshooting.
            urlLabel = new Label();
            urlLabel.AutoSize = false;
            urlLabel.Dock = DockStyle.Top;
            urlLabel.Height = 32;
            urlLabel.Padding = new Padding(4);
            urlLabel.TextAlign = ContentAlignment.MiddleLeft;
            urlLabel.BackColor = Color.LightYellow;
            urlLabel.ForeColor = Color.Black;
            urlLabel.Font = new Font(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size, FontStyle.Bold);
            Controls.Add(urlLabel);
            Controls.SetChildIndex(urlLabel, 0); // keep it above the browser
        }

        private void AuthForm_Load(object sender, EventArgs e)
        {
            //DialogResult = DialogResult.None;
            try
            {
                // Force the embedded browser to emulate IE11 so Auth0 pages run modern JS.
                SetBrowserEmulation();

                // Enforce TLS 1.2+ before hitting Auth0 and ensure we use HTTPS endpoints.
                ServicePointManager.Expect100Continue = true;
#if NET48
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
#else
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
#endif

                string uriString = "https://frtest.auth0.com/authorize?";
                uriString += "response_type=token&";
                uriString += "client_id=4Y9fEIIsVYyQo5u6jr7yBWc4lV5ugC2m&";
                uriString += "redirect_uri=https://frtest.auth0.com/mobile&";
                uriString += "scope=openid offline_access email given_name family_name picture&";
                string state = "";
                Random r = new Random();
                for (int i = 0; i < 16; i++)
                {
                    int j = r.Next(0x41, 0x5a);
                    state += (char)j;
                }
                //uriString += "state=ypfolheqwpezryrc&";
                uriString += "state=" + state;
                uriString += "&device=JJFlexRadio";
                Tracing.TraceLine("AuthForm URI:" + uriString, TraceLevel.Info);
                if (urlLabel != null)
                {
                    urlLabel.Text = uriString;
                }
                Uri uri = new Uri(uriString);
                Browser.DocumentCompleted += documentLoadedHandler;
                Browser.Navigate(uri);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AuthForm Exception: " + ex.Message, TraceLevel.Error);
                DialogResult = DialogResult.Abort;
            }
        }

        /// <summary>
        /// Ensure the WebBrowser control runs in IE11 emulation to avoid script compatibility errors.
        /// </summary>
        private void SetBrowserEmulation()
        {
            try
            {
                string exeName = Process.GetCurrentProcess().ProcessName + ".exe";
                const string featureKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(featureKey, true))
                {
                    const int ie11Mode = 11001;
                    object current = key.GetValue(exeName);
                    if (current == null || (int)current != ie11Mode)
                    {
                        key.SetValue(exeName, ie11Mode, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AuthForm SetBrowserEmulation failed: " + ex.Message, TraceLevel.Warning);
            }
        }

        private void documentLoadedHandler(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            Tracing.TraceLine("AuthForm documentLoadedHandler:", TraceLevel.Info);
            //DialogResult = DialogResult.Abort; // default on error. (doesn't work!)
            if (Browser.Url.AbsolutePath == "/mobile")
            {
                string str = Browser.Url.ToString();
                Tracing.TraceLine("AuthForm received:" + str, TraceLevel.Info);
                Tokens = str.Split(new char[] { '&' });
                DialogResult = DialogResult.OK;
                //this.Close();
            }
        }
    }
}
