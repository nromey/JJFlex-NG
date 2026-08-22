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
        private readonly TextBox _friendlyNameBox;
        private readonly CheckBox _rememberCheck;
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

        /// <summary>
        /// The display name the user chose for this account, or empty for
        /// "use the email". Naming happens where the account is born — a
        /// friendly name defaulting to the email is what made every account
        /// read as "email (email)" through a screen reader.
        /// </summary>
        public string FriendlyName { get; private set; } = "";

        /// <summary>
        /// Whether the user wants this sign-in saved. Answered on the form,
        /// before signing in — never as a popup afterward.
        /// </summary>
        public bool RememberSignIn { get; private set; } = true;

        public SmartLinkLoginForm(SmartLinkAccountManager manager, string prefillEmail = "")
        {
            _manager = manager ?? new SmartLinkAccountManager();

            Text = Lexicon.Get("connect.smartlink.login.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(430, 322);
            // Runs ownerless on its own thread while the Connecting form (also
            // ownerless, also TopMost, also its own thread) is up. Without
            // TopMost this window opens BEHIND the connecting screen and a
            // screen reader user has no idea it exists — Noel hit exactly
            // that on 2026-08-06: "it stays on connecting but you don't know
            // a new window popped up."
            TopMost = true;

            var intro = new Label
            {
                Text = Lexicon.Get("connect.smartlink.login.intro"),
                Left = 12, Top = 10, Width = 406, Height = 34,
                AccessibleName = Lexicon.Get("connect.smartlink.login.intro_name"),
            };
            Controls.Add(intro);

            var emailLabel = new Label { Text = Lexicon.Get("connect.smartlink.login.email_label"), Left = 12, Top = 54, Width = 80 };
            Controls.Add(emailLabel);
            _emailBox = new TextBox
            {
                Left = 100, Top = 51, Width = 318, TabIndex = 0,
                Text = prefillEmail ?? "",
                AccessibleName = Lexicon.Get("connect.smartlink.login.email_name"),
            };
            Controls.Add(_emailBox);

            var passwordLabel = new Label { Text = Lexicon.Get("connect.smartlink.login.password_label"), Left = 12, Top = 84, Width = 80 };
            Controls.Add(passwordLabel);
            _passwordBox = new TextBox
            {
                Left = 100, Top = 81, Width = 318, TabIndex = 1,
                UseSystemPasswordChar = true,
                AccessibleName = Lexicon.Get("connect.smartlink.login.password_name"),
            };
            Controls.Add(_passwordBox);

            var nameLabel = new Label { Text = Lexicon.Get("connect.smartlink.login.name_label"), Left = 12, Top = 114, Width = 86 };
            Controls.Add(nameLabel);
            _friendlyNameBox = new TextBox
            {
                Left = 100, Top = 111, Width = 318, TabIndex = 2,
                AccessibleName = Lexicon.Get("connect.smartlink.login.name_name"),
            };
            Controls.Add(_friendlyNameBox);

            // The remember choice lives HERE, on the form, answered before
            // sign-in — never as a popup afterward. Round 27 lesson (Don,
            // 2026-08-06): the old "Save this account?" MessageBox appeared
            // ownerless behind the TopMost Connecting form, unannounced, and
            // the SmartLink thread blocked on it forever. "It says connecting
            // and sits there." A question nobody can perceive is a deadlock,
            // not a choice.
            _rememberCheck = new CheckBox
            {
                Text = Lexicon.Get("connect.smartlink.login.remember_label"),
                Left = 100, Top = 143, Width = 318, TabIndex = 3,
                Checked = true,
                AccessibleName = Lexicon.Get("connect.smartlink.login.remember_name"),
            };
            Controls.Add(_rememberCheck);

            _signInButton = new Button
            {
                Text = Lexicon.Get("connect.smartlink.login.signin_button"), Left = 100, Top = 174, Width = 100, TabIndex = 4,
                AccessibleName = Lexicon.Get("connect.smartlink.login.signin_button_name"),
            };
            _signInButton.Click += async (_, _) => await SignInAsync();
            Controls.Add(_signInButton);

            _forgotButton = new Button
            {
                Text = Lexicon.Get("connect.smartlink.login.forgot_button"), Left = 206, Top = 174, Width = 130, TabIndex = 5,
                AccessibleName = Lexicon.Get("connect.smartlink.login.forgot_button_name"),
            };
            _forgotButton.Click += async (_, _) => await ForgotPasswordAsync();
            Controls.Add(_forgotButton);

            _browserButton = new Button
            {
                Text = Lexicon.Get("connect.smartlink.login.browser_button"), Left = 100, Top = 206, Width = 160, TabIndex = 6,
                AccessibleName = Lexicon.Get("connect.smartlink.login.browser_button_name"),
            };
            _browserButton.Click += (_, _) => { DialogResult = DialogResult.Retry; Close(); };
            Controls.Add(_browserButton);

            _cancelButton = new Button
            {
                Text = Lexicon.Get("connect.smartlink.login.cancel_button"), Left = 266, Top = 206, Width = 100, TabIndex = 7,
                DialogResult = DialogResult.Cancel,
                AccessibleName = Lexicon.Get("connect.smartlink.login.cancel_button_name"),
            };
            Controls.Add(_cancelButton);

            _statusLabel = new Label
            {
                Left = 12, Top = 242, Width = 406, Height = 66,
                AccessibleName = Lexicon.Get("connect.smartlink.login.status_name"),
                AccessibleRole = AccessibleRole.StaticText,
            };
            Controls.Add(_statusLabel);

            AcceptButton = _signInButton;
            CancelButton = _cancelButton;

            // Register with the armistice flag for our whole lifetime: the
            // ConnectingForm's own focus-reclaim timer yields while any
            // sign-in window is open, instead of fighting our watchdog for
            // the foreground four times a second.
            WindowFocusForcer.PushSignInWindow();
            FormClosed += (_, _) => WindowFocusForcer.PopSignInWindow();

            Shown += (_, _) =>
            {
                // Pull keyboard focus here from the Connecting form and SAY SO —
                // the whole point of this dialog is that a screen reader user
                // knows it exists the moment it appears (no-silent-state).
                // Activate() alone loses to Windows' foreground lock when the
                // Connecting form (another thread) holds foreground — Noel had
                // to Alt+Tab to find this window. The forcer uses the
                // AttachThreadInput recipe proven in Civ VI Access.
                bool tookFocus = WindowFocusForcer.ForceForeground(Handle);
                // The Connecting form can appear ~half a second AFTER this
                // window verifiably took focus and squash it — guard the win.
                WindowFocusForcer.KeepForegroundWhileVisible(this);

                bool havePrefill = _emailBox.Text.Trim().Length > 0;
                if (havePrefill) _passwordBox.Focus();
                else _emailBox.Focus();

                string where = tookFocus
                    ? ""
                    : Lexicon.Get("connect.smartlink.login.no_focus_suffix");
                ScreenReaderOutput.Speak(
                    (havePrefill
                        ? Lexicon.Get("connect.smartlink.login.opened_with_email",
                            ("email", _emailBox.Text.Trim()))
                        : Lexicon.Get("connect.smartlink.login.opened"))
                    + where,
                    VerbosityLevel.Terse, interrupt: true);
            };
        }

        /// <summary>
        /// Update the status line, and say it.
        ///
        /// QUEUED, because sign-in is a SERIES: "Signing in..." then the
        /// verdict. Under interrupt the verdict cut the progress line off, so
        /// an operator who had just pressed Sign In heard a fragment of
        /// "Signing in" and then a result, with no sense that the two were
        /// related. Surveyed 2026-08-18.
        /// </summary>
        private void SetStatus(string text, bool speak = true)
        {
            _statusLabel.Text = text;
            if (speak)
            {
                ScreenReaderOutput.Speak(
                    text, Speech.SpeechIntent.Queue, VerbosityLevel.Terse);
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
            _friendlyNameBox.Enabled = !busy;
            _rememberCheck.Enabled = !busy;
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
                SetStatus(Lexicon.Get("connect.smartlink.login.email_required"));
                _emailBox.Focus();
                return;
            }
            if (password.Length == 0)
            {
                SetStatus(Lexicon.Get("connect.smartlink.login.password_required"));
                _passwordBox.Focus();
                return;
            }

            SetBusy(true);
            SetStatus(Lexicon.Get("connect.smartlink.login.signing_in"));
            try
            {
                var result = await _manager.LoginWithPasswordAsync(email, password);
                if (result.Success)
                {
                    Email = result.Email;
                    IdToken = result.IdToken;
                    RefreshToken = result.RefreshToken;
                    ExpiresIn = result.ExpiresIn;
                    FriendlyName = _friendlyNameBox.Text.Trim();
                    RememberSignIn = _rememberCheck.Checked;
                    ScreenReaderOutput.Speak(Lexicon.Get("connect.smartlink.login.signed_in"), VerbosityLevel.Terse, interrupt: true);
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                switch (result.Error)
                {
                    case "wrong_credentials":
                        SetStatus(Lexicon.Get("connect.smartlink.login.wrong_credentials"));
                        _passwordBox.SelectAll();
                        _passwordBox.Focus();
                        break;
                    case "mfa_required":
                        // ROPG cannot carry a second factor; the browser page can.
                        SetStatus(Lexicon.Get("connect.smartlink.login.mfa_required"));
                        DialogResult = DialogResult.Retry;
                        Close();
                        break;
                    case "too_many_attempts":
                        SetStatus(Lexicon.Get("connect.smartlink.login.too_many_attempts"));
                        break;
                    case "network":
                        SetStatus(Lexicon.Get("connect.smartlink.login.network"));
                        break;
                    default:
                        SetStatus(Lexicon.Get("connect.smartlink.login.failed"));
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
                SetStatus(Lexicon.Get("connect.smartlink.login.forgot_email_required"));
                _emailBox.Focus();
                return;
            }

            SetBusy(true);
            SetStatus(Lexicon.Get("connect.smartlink.login.requesting_reset"));
            try
            {
                bool ok = await _manager.SendPasswordResetEmailAsync(email);
                SetStatus(ok
                    ? Lexicon.Get("connect.smartlink.login.reset_sent", ("email", email))
                    : Lexicon.Get("connect.smartlink.login.reset_failed"));
            }
            finally
            {
                SetBusy(false);
            }
        }
    }
}
