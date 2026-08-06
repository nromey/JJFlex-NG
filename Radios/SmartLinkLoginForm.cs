using System;
using System.Drawing;
using System.Windows.Forms;
using JJTrace;
using TraceLevel = System.Diagnostics.TraceLevel;

namespace Radios
{
    /// <summary>
    /// Native SmartLink sign-in (2026-08-06, born from Don's lockout). Email and
    /// password in ordinary WinForms controls, exchanged directly with Auth0 via
    /// the resource-owner grant — SmartSDR's own sign-in mechanics, with a form a
    /// screen reader can actually drive. The WebView2 browser form survives only
    /// as a fallback, reached by the "Use Browser Instead" button or
    /// automatically when the account demands two-factor sign-in.
    ///
    /// DialogResult contract: OK = signed in (read Email/IdToken/RefreshToken/
    /// ExpiresIn); Retry = caller should open the browser form; Cancel = user
    /// backed out. Escape cancels, per the every-dialog-Escape rule.
    /// </summary>
    public class SmartLinkLoginForm : Form
    {
        private readonly SmartLinkAccountManager _manager;

        private readonly TextBox _emailBox;
        private readonly TextBox _passwordBox;
        private readonly Button _signInButton;
        private readonly Button _forgotButton;
        private readonly Button _browserButton;
        private readonly Button _cancelButton;
        private readonly Label _statusLabel;

        private bool _busy;

        /// <summary>Canonical email from the id_token after a successful sign-in.</summary>
        public string Email { get; private set; } = "";
        public string IdToken { get; private set; } = "";
        public string RefreshToken { get; private set; } = "";
        public int ExpiresIn { get; private set; }

        public SmartLinkLoginForm(SmartLinkAccountManager manager, string prefillEmail = "")
        {
            _manager = manager ?? new SmartLinkAccountManager();

            Text = "SmartLink Sign In";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(430, 260);
            // Runs ownerless on its own thread while the Connecting form (also
            // ownerless, also TopMost, also its own thread) is up. Without
            // TopMost this window opens BEHIND the connecting screen and a
            // screen reader user has no idea it exists — Noel hit exactly
            // that on 2026-08-06: "it stays on connecting but you don't know
            // a new window popped up."
            TopMost = true;

            var intro = new Label
            {
                Text = "Sign in with your SmartLink account. Your email and password go " +
                       "directly to Flex's sign-in service — no web page involved.",
                Left = 12, Top = 10, Width = 406, Height = 34,
                AccessibleName = "Sign in with your SmartLink account. Your email and password go directly to Flex's sign-in service, no web page involved.",
            };
            Controls.Add(intro);

            var emailLabel = new Label { Text = "&Email:", Left = 12, Top = 54, Width = 80 };
            Controls.Add(emailLabel);
            _emailBox = new TextBox
            {
                Left = 100, Top = 51, Width = 318, TabIndex = 0,
                Text = prefillEmail ?? "",
                AccessibleName = "SmartLink email address",
            };
            Controls.Add(_emailBox);

            var passwordLabel = new Label { Text = "&Password:", Left = 12, Top = 84, Width = 80 };
            Controls.Add(passwordLabel);
            _passwordBox = new TextBox
            {
                Left = 100, Top = 81, Width = 318, TabIndex = 1,
                UseSystemPasswordChar = true,
                AccessibleName = "SmartLink password",
            };
            Controls.Add(_passwordBox);

            _signInButton = new Button
            {
                Text = "&Sign In", Left = 100, Top = 116, Width = 100, TabIndex = 2,
                AccessibleName = "Sign in",
            };
            _signInButton.Click += async (_, _) => await SignInAsync();
            Controls.Add(_signInButton);

            _forgotButton = new Button
            {
                Text = "&Forgot Password", Left = 206, Top = 116, Width = 130, TabIndex = 3,
                AccessibleName = "Forgot password. Sends a password reset email to the address above.",
            };
            _forgotButton.Click += async (_, _) => await ForgotPasswordAsync();
            Controls.Add(_forgotButton);

            _browserButton = new Button
            {
                Text = "Use &Browser Instead", Left = 100, Top = 148, Width = 160, TabIndex = 4,
                AccessibleName = "Use the browser sign-in page instead",
            };
            _browserButton.Click += (_, _) => { DialogResult = DialogResult.Retry; Close(); };
            Controls.Add(_browserButton);

            _cancelButton = new Button
            {
                Text = "Cancel", Left = 266, Top = 148, Width = 100, TabIndex = 5,
                DialogResult = DialogResult.Cancel,
                AccessibleName = "Cancel sign in",
            };
            Controls.Add(_cancelButton);

            _statusLabel = new Label
            {
                Left = 12, Top = 184, Width = 406, Height = 64,
                AccessibleName = "Sign-in status",
                AccessibleRole = AccessibleRole.StaticText,
            };
            Controls.Add(_statusLabel);

            AcceptButton = _signInButton;
            CancelButton = _cancelButton;

            Shown += (_, _) =>
            {
                // Pull keyboard focus here from the Connecting form and SAY SO —
                // the whole point of this dialog is that a screen reader user
                // knows it exists the moment it appears (no-silent-state).
                Activate();
                BringToFront();

                bool havePrefill = _emailBox.Text.Trim().Length > 0;
                if (havePrefill) _passwordBox.Focus();
                else _emailBox.Focus();

                ScreenReaderOutput.Speak(
                    havePrefill
                        ? $"SmartLink sign in window. Enter the password for {_emailBox.Text.Trim()}."
                        : "SmartLink sign in window. Enter your SmartLink email and password.",
                    VerbosityLevel.Terse, interrupt: true);
            };
        }

        private void SetStatus(string text, bool speak = true)
        {
            _statusLabel.Text = text;
            if (speak)
            {
                ScreenReaderOutput.Speak(text, VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _signInButton.Enabled = !busy;
            _forgotButton.Enabled = !busy;
            _browserButton.Enabled = !busy;
            _emailBox.Enabled = !busy;
            _passwordBox.Enabled = !busy;
            // Cancel stays enabled — a stuck network call must not trap the
            // user in the dialog (stuck-modal rule).
        }

        private async System.Threading.Tasks.Task SignInAsync()
        {
            if (_busy) return;

            string email = _emailBox.Text.Trim();
            string password = _passwordBox.Text;
            if (email.Length == 0)
            {
                SetStatus("Enter your SmartLink email address first.");
                _emailBox.Focus();
                return;
            }
            if (password.Length == 0)
            {
                SetStatus("Enter your SmartLink password.");
                _passwordBox.Focus();
                return;
            }

            SetBusy(true);
            SetStatus("Signing in...");
            try
            {
                var result = await _manager.LoginWithPasswordAsync(email, password);
                if (result.Success)
                {
                    Email = result.Email;
                    IdToken = result.IdToken;
                    RefreshToken = result.RefreshToken;
                    ExpiresIn = result.ExpiresIn;
                    ScreenReaderOutput.Speak("Signed in.", VerbosityLevel.Terse, interrupt: true);
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                switch (result.Error)
                {
                    case "wrong_credentials":
                        SetStatus("That email and password combination was not accepted. Check both and try again, or use Forgot Password.");
                        _passwordBox.SelectAll();
                        _passwordBox.Focus();
                        break;
                    case "mfa_required":
                        // ROPG cannot carry a second factor; the browser page can.
                        SetStatus("This account uses two-factor sign-in, which needs the browser page. Opening it.");
                        DialogResult = DialogResult.Retry;
                        Close();
                        break;
                    case "too_many_attempts":
                        SetStatus("Too many sign-in attempts — the account is temporarily locked by the sign-in service. Wait a few minutes, or use Forgot Password.");
                        break;
                    case "network":
                        SetStatus("Could not reach the sign-in service. Check your internet connection and try again.");
                        break;
                    default:
                        SetStatus("Sign-in failed. You can try again, or choose Use Browser Instead.");
                        Tracing.TraceLine($"SmartLinkLoginForm: unmapped error: {result.ErrorDetail}", TraceLevel.Info);
                        break;
                }
            }
            finally
            {
                if (DialogResult == DialogResult.None)
                {
                    SetBusy(false);
                }
            }
        }

        private async System.Threading.Tasks.Task ForgotPasswordAsync()
        {
            if (_busy) return;

            string email = _emailBox.Text.Trim();
            if (email.Length == 0)
            {
                SetStatus("Enter your SmartLink email address first, then choose Forgot Password.");
                _emailBox.Focus();
                return;
            }

            SetBusy(true);
            SetStatus("Requesting a password reset email...");
            try
            {
                bool ok = await _manager.SendPasswordResetEmailAsync(email);
                SetStatus(ok
                    ? $"Done. A password reset email is on its way to {email}. Follow its link, set a new password, then come back here and sign in."
                    : "The reset request was not accepted. Check the email address, or try again in a moment.");
            }
            finally
            {
                SetBusy(false);
            }
        }
    }
}
