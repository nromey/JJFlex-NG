using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Native SmartLink account creation. The hosted sign-in page's own signup
/// link half-works — it creates the account, then fails its post-signup
/// redirect and reports failure (live find, 2026-08-04) — and SmartSDR never
/// uses it either: it posts to Auth0's signup API directly. This dialog does
/// the same, with a form a screen reader can drive and validation errors
/// that are SPOKEN, not just painted.
///
/// On success the caller routes straight into the existing native sign-in
/// flow with the email prefilled, so "create account" flows into "signed in"
/// without the user retyping anything but the password.
/// </summary>
public sealed class SmartLinkSignUpDialog : JJFlexDialog
{
    private readonly SmartLinkAccountManager _manager;

    private readonly TextBox _emailBox;
    private readonly PasswordBox _passwordBox;
    private readonly PasswordBox _repeatBox;
    private readonly Button _createButton;
    private readonly Button _resetEmailButton;
    private readonly Button _cancelButton;
    private readonly TextBlock _statusText;

    private bool _busy;

    /// <summary>The email the account was created with, valid when DialogResult is true.</summary>
    public string SignedUpEmail { get; private set; } = "";

    public SmartLinkSignUpDialog(SmartLinkAccountManager manager)
    {
        _manager = manager ?? new SmartLinkAccountManager();

        Title = Lexicon.Get("connect.smartlink.signup.title");
        Width = 480;
        SizeToContent = SizeToContent.Height;

        var root = new StackPanel { Margin = new Thickness(12) };

        var intro = new TextBlock
        {
            Text = Lexicon.Get("connect.smartlink.signup.intro"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(intro);

        _emailBox = AddLabeledRow(root, Lexicon.Get("connect.smartlink.signup.email_label"), new TextBox(),
            Lexicon.Get("connect.smartlink.signup.email_name"));

        _passwordBox = AddLabeledRow(root, Lexicon.Get("connect.smartlink.signup.password_label"), new PasswordBox(),
            Lexicon.Get("connect.smartlink.signup.password_name"));

        _repeatBox = AddLabeledRow(root, Lexicon.Get("connect.smartlink.signup.repeat_label"), new PasswordBox(),
            Lexicon.Get("connect.smartlink.signup.repeat_name"));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        _createButton = new Button
        {
            Content = Lexicon.Get("connect.smartlink.signup.create_button"),
            MinWidth = 120,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        AutomationProperties.SetName(_createButton, Lexicon.Get("connect.smartlink.signup.create_button_name"));
        // IsDefault registers literal \r as an access key and NVDA reads it as
        // "carriage return" — explicit values preempt the phantom one.
        AutomationProperties.SetAccessKey(_createButton, "Alt+R");
        AutomationProperties.SetAcceleratorKey(_createButton, "Enter");
        _createButton.Click += async (_, _) => await CreateAsync();
        buttons.Children.Add(_createButton);

        // Appears only after "that email already has an account" — the person
        // most likely to hit that has forgotten they signed up once, and the
        // reset email is the way back in. The app does the walking.
        _resetEmailButton = new Button
        {
            Content = Lexicon.Get("connect.smartlink.signup.reset_email_button"),
            MinWidth = 130,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_resetEmailButton,
            Lexicon.Get("connect.smartlink.signup.reset_email_button_name"));
        AutomationProperties.SetAccessKey(_resetEmailButton, "Alt+E");
        _resetEmailButton.Click += async (_, _) => await SendResetAsync();
        buttons.Children.Add(_resetEmailButton);

        string cancelLabel = Lexicon.Get("connect.dialog.cancel");
        _cancelButton = new Button
        {
            Content = cancelLabel,
            MinWidth = 80,
            Height = 28,
            IsCancel = true,
        };
        AutomationProperties.SetName(_cancelButton, cancelLabel.Replace("_", ""));
        buttons.Children.Add(_cancelButton);

        root.Children.Add(buttons);

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        AutomationProperties.SetName(_statusText, Lexicon.Get("connect.smartlink.signup.status_name"));
        root.Children.Add(_statusText);

        Content = root;
    }

    private static T AddLabeledRow<T>(StackPanel root, string label, T box, string accessibleName)
        where T : Control
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var text = new TextBlock
        {
            Text = label,
            Width = 130,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        box.Height = 24;
        AutomationProperties.SetName(box, accessibleName);
        row.Children.Add(box);
        root.Children.Add(row);
        return box;
    }

    /// <summary>
    /// Every path out of validation or the server SPEAKS — a validation error
    /// that only changes pixels is invisible here.
    /// </summary>
    private void SetStatus(string text)
    {
        _statusText.Text = text;
        ScreenReaderOutput.Speak(text, VerbosityLevel.Terse, interrupt: true);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _createButton.IsEnabled = !busy;
        _resetEmailButton.IsEnabled = !busy;
        _emailBox.IsEnabled = !busy;
        _passwordBox.IsEnabled = !busy;
        _repeatBox.IsEnabled = !busy;
        // Cancel stays enabled — a stuck network call must not trap the user
        // in the dialog (stuck-modal rule). Escape works throughout.
    }

    private static bool LooksLikeEmail(string s)
    {
        // Same intent as SmartSDR's client-side check: catch typos, not
        // enforce the RFC. The server has the final say.
        int at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1 && s.IndexOf('.', at) > at + 1;
    }

    private async System.Threading.Tasks.Task CreateAsync()
    {
        if (_busy) return;

        string email = _emailBox.Text.Trim();
        string password = _passwordBox.Password;
        string repeat = _repeatBox.Password;

        if (email.Length == 0 || !LooksLikeEmail(email))
        {
            SetStatus(Lexicon.Get(email.Length == 0
                ? "connect.smartlink.signup.email_required"
                : "connect.smartlink.signup.email_malformed"));
            _emailBox.Focus();
            return;
        }
        if (password.Length == 0)
        {
            SetStatus(Lexicon.Get("connect.smartlink.signup.password_required"));
            _passwordBox.Focus();
            return;
        }
        if (repeat != password)
        {
            SetStatus(Lexicon.Get("connect.smartlink.signup.passwords_differ"));
            _repeatBox.Clear();
            _repeatBox.Focus();
            return;
        }

        SetBusy(true);
        SetStatus(Lexicon.Get("connect.smartlink.signup.creating"));
        try
        {
            var result = await _manager.SignUpAsync(email, password);
            if (result.Success)
            {
                SignedUpEmail = email;
                ScreenReaderOutput.Speak(
                    Lexicon.Get("connect.smartlink.signup.created", ("email", email)),
                    VerbosityLevel.Terse, interrupt: true);
                DialogResult = true;
                Close();
                return;
            }

            switch (result.Error)
            {
                case "user_exists":
                    SetStatus(Lexicon.Get("connect.smartlink.signup.user_exists"));
                    _resetEmailButton.Visibility = Visibility.Visible;
                    break;
                case "weak_password":
                    SetStatus(Lexicon.Get("connect.smartlink.signup.weak_password"));
                    _passwordBox.Clear();
                    _repeatBox.Clear();
                    _passwordBox.Focus();
                    break;
                case "network":
                    SetStatus(Lexicon.Get("connect.smartlink.signup.network"));
                    break;
                default:
                    SetStatus(Lexicon.Get("connect.smartlink.signup.failed"));
                    JJTrace.Tracing.TraceLine($"SmartLinkSignUpDialog: unmapped error: {result.ErrorDetail}",
                        System.Diagnostics.TraceLevel.Info);
                    break;
            }
        }
        finally
        {
            if (DialogResult == null) SetBusy(false);
        }
    }

    private async System.Threading.Tasks.Task SendResetAsync()
    {
        if (_busy) return;

        string email = _emailBox.Text.Trim();
        if (email.Length == 0)
        {
            SetStatus(Lexicon.Get("connect.smartlink.signup.reset_email_required"));
            _emailBox.Focus();
            return;
        }

        SetBusy(true);
        SetStatus(Lexicon.Get("connect.smartlink.signup.requesting_reset"));
        try
        {
            bool ok = await _manager.SendPasswordResetEmailAsync(email);
            SetStatus(ok
                ? Lexicon.Get("connect.smartlink.signup.reset_sent", ("email", email))
                : Lexicon.Get("connect.smartlink.signup.reset_failed"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    protected override void FocusFirstControl()
    {
        _emailBox.Focus();
    }
}
