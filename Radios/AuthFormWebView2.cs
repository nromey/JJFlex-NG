using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Auth0 authentication form using WebView2 (Edge/Chromium).
    /// Replaces the legacy WebBrowser (IE) control for modern TLS and JS support.
    /// </summary>
    public class AuthFormWebView2 : Form
    {
        private WebView2 webView;
        private Label urlLabel;
        private bool isInitialized;

        public string[] Tokens { get; private set; }

        public AuthFormWebView2()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();

            // URL label for troubleshooting
            urlLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(4),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.LightYellow,
                ForeColor = Color.Black,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size, FontStyle.Bold)
            };

            // WebView2 control
            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "WebView",
                AccessibleRole = AccessibleRole.Window
            };

            this.Controls.Add(webView);
            this.Controls.Add(urlLabel);
            this.Controls.SetChildIndex(urlLabel, 0);

            this.AutoScaleDimensions = new SizeF(8F, 16F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(582, 653);
            this.Name = "AuthFormWebView2";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "SmartLink Authentication";
            this.Load += AuthFormWebView2_Load;

            this.ResumeLayout(false);
        }

        private async void AuthFormWebView2_Load(object sender, EventArgs e)
        {
            try
            {
                // TLS is handled by WebView2/Edge, no need for ServicePointManager config

                // Build Auth0 URL
                string uriString = BuildAuth0Url();
                Tracing.TraceLine("AuthFormWebView2 URI: " + uriString, TraceLevel.Info);

                if (urlLabel != null)
                {
                    urlLabel.Text = uriString;
                }

                // Initialize WebView2
                await webView.EnsureCoreWebView2Async(null);
                isInitialized = true;

                // Handle navigation completed
                webView.NavigationCompleted += WebView_NavigationCompleted;

                // Navigate to Auth0
                webView.CoreWebView2.Navigate(uriString);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AuthFormWebView2 Exception: " + ex.Message, TraceLevel.Error);
                MessageBox.Show(
                    $"Failed to initialize authentication browser:\n\n{ex.Message}\n\n" +
                    "Make sure WebView2 Runtime is installed.",
                    "Authentication Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                DialogResult = DialogResult.Abort;
            }
        }

        private string BuildAuth0Url()
        {
            string uriString = "https://frtest.auth0.com/authorize?";
            uriString += "response_type=token&";
            uriString += "client_id=4Y9fEIIsVYyQo5u6jr7yBWc4lV5ugC2m&";
            uriString += "redirect_uri=https://frtest.auth0.com/mobile&";
            uriString += "scope=openid offline_access email given_name family_name picture&";

            // Generate random state
            string state = "";
            Random r = new Random();
            for (int i = 0; i < 16; i++)
            {
                int j = r.Next(0x41, 0x5a);
                state += (char)j;
            }
            uriString += "state=" + state;
            uriString += "&device=JJFlexRadio";

            return uriString;
        }

        private void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!isInitialized) return;

            try
            {
                var uri = new Uri(webView.Source.ToString());
                Tracing.TraceLine("AuthFormWebView2 NavigationCompleted: " + uri.AbsolutePath, TraceLevel.Info);

                if (uri.AbsolutePath == "/mobile")
                {
                    string str = webView.Source.ToString();
                    Tracing.TraceLine("AuthFormWebView2 received: " + str, TraceLevel.Info);
                    Tokens = str.Split(new char[] { '&' });
                    DialogResult = DialogResult.OK;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AuthFormWebView2 NavigationCompleted error: " + ex.Message, TraceLevel.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                webView?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
