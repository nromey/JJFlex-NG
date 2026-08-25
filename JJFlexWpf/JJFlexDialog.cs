using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// Base class for all JJFlexRadio WPF dialogs.
    /// Provides standard behavior: ESC to close, focus management,
    /// accessibility defaults, and consistent styling.
    /// </summary>
    public class JJFlexDialog : Window
    {
        /// <summary>
        /// Callback invoked after a dialog closes to announce focus-return context.
        /// Set by MainWindow to speak compact status (e.g., "Slice A, 14.175, USB").
        /// </summary>
        public static Action? FocusReturnCallback { get; set; }

        public JJFlexDialog()
        {
            // Load shared dialog styles
            var styles = new ResourceDictionary();
            styles.Source = new Uri("pack://application:,,,/JJFlexWpf;component/Styles/DialogStyles.xaml");
            Resources.MergedDictionaries.Add(styles);

            // Center on parent window
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Standard dialog chrome
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            // Tab wraps at both ends. WPF's default (Continue) stops dead at
            // the edges of the tab order, so Shift+Tab from the first control
            // went nowhere and content late in the order was effectively
            // unreachable backward — found at the keyboard 2026-08-10 in the
            // radio selector, but inherited by every dialog built on this
            // base. A dialog is a cycle, not a corridor.
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

            // MainWindow is a UserControl hosted in ElementHost, so
            // Application.Current.MainWindow is null. Use the process main
            // window handle as Owner for proper modality and centering.
            try
            {
                var mainHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (mainHandle != nint.Zero)
                    new WindowInteropHelper(this).Owner = mainHandle;
            }
            catch { /* non-critical — dialog still works, just without modality lock */ }

            // Wire up standard events
            PreviewKeyDown += JJFlexDialog_PreviewKeyDown;
            Loaded += JJFlexDialog_Loaded;
        }

        /// <summary>
        /// ESC closes the dialog with DialogResult = false.
        /// Subclasses can override for custom key handling but should call base.
        /// </summary>
        private void JJFlexDialog_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseWithResult(false);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Close the dialog, recording <paramref name="result"/> when the window
        /// is modal and simply closing when it is not. Use this instead of
        /// assigning <see cref="Window.DialogResult"/> directly.
        ///
        /// <para><b>Why it exists (#159).</b> WPF's DialogResult setter throws
        /// InvalidOperationException on any window that was not opened with
        /// ShowDialog(). A dialog cannot know how it was opened: ConnectingDialog
        /// is legitimately shown non-modally, and the Tier 1 accessibility suite
        /// realises every dialog with Show(). Several dialogs assigned
        /// DialogResult from their Loaded handler as an early exit — a WinForms
        /// idiom ported literally — and on 2026-08-20 and again on 2026-08-21
        /// the resulting throw during window realisation aborted the whole
        /// suite, the second time leaving Export Log and ATU Memories windows
        /// stranded on the operator's screen mid-session. The try/catch is the
        /// same guard the Escape handler above has carried since
        /// ConnectingDialog went non-modal; this puts it in one named place.</para>
        /// </summary>
        protected void CloseWithResult(bool result)
        {
            try { DialogResult = result; } catch (InvalidOperationException) { }
            Close();
        }

        /// <summary>
        /// On load: sync AutomationProperties.Name with Title,
        /// then focus the first interactive control.
        /// </summary>
        private void JJFlexDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Set automation name from title for screen readers
            // A dialog announcing itself is real speech arriving, which is
            // exactly what a progress voice exists to stand in for until it
            // does. Stop it HERE — at the announcement — rather than anywhere
            // earlier.
            //
            // It was in DiscoveringRadiosWindow's constructor for a few hours
            // on 2026-08-25 and that was wrong in the way that matters: the
            // window is CONSTRUCTED, then discovery runs for five and a half
            // seconds on the same thread, and only THEN is it shown. Measured
            // that morning — constructed at 1295 ms, spoke at 6899 ms. So the
            // voice fell silent one second in and the operator got the entire
            // wait in silence anyway, with zero repeats. Noel heard exactly
            // that: one line, then nothing.
            //
            // Here it also covers every other dialog for free, which is right:
            // whatever a progress voice was covering, a dialog opening has
            // superseded it.
            Radios.ProgressVoice.Stop("dialog announced: " + (Title ?? "(untitled)"));

            if (!string.IsNullOrEmpty(Title))
            {
                AutomationProperties.SetName(this, Title);

                // Speak the title explicitly - NVDA may read the focused control
                // instead of the window title.
                //
                // QUEUED, not interrupting. This one line is inherited by 74
                // dialogs, so as an interrupt it meant that opening ANY dialog
                // anywhere destroyed whatever was being spoken at that instant.
                // Confirmed victims included "The radio disconnected", the
                // update-check error, "Opening SmartLink login" and the connect
                // sequence itself - which is why a successful connect emitted
                // eight utterances and the operator heard about two.
                //
                // A dialog opening is the START of a series, never a supersession
                // of one. Surveyed and re-bucketed 2026-08-18.
                Radios.ScreenReaderOutput.Speak(
                    Title, Radios.Speech.SpeechIntent.Queue, Radios.VerbosityLevel.Terse);
            }

            // Focus first interactive control
            FocusFirstControl();
        }

        /// <summary>
        /// Take the foreground once the window is actually on screen.
        ///
        /// **Not at Loaded.** Loaded fires while ShowDialog is still bringing
        /// the window up, before it is visible, and SetForegroundWindow on a
        /// window that is not yet shown fails silently - which is exactly the
        /// silent refusal this exists to defeat. Reported 2026-08-18: the
        /// discovering window took focus correctly, the picker opening behind
        /// it did not, and the operator had to Alt-Tab to find it.
        ///
        /// The window-to-window transition is the hard case. When one of our
        /// windows closes and the next opens there is a moment with nothing of
        /// ours on screen, so the foreground escapes to whatever was behind us.
        /// ContentRendered runs after that has settled and after we are
        /// visible, which is the first point SetForegroundWindow can succeed.
        ///
        /// Activate() as well: the grab makes us the foreground WINDOW, and
        /// Activate makes WPF treat us as the active one - which is what makes
        /// keyboard focus inside the dialog stick rather than being restored to
        /// whatever WPF last remembered.
        /// </summary>
        protected override void OnContentRendered(System.EventArgs e)
        {
            base.OnContentRendered(e);

            Radios.WindowActivation.EnsureForeground(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            try { Activate(); } catch { }

            // Focus again now that we are genuinely active. The pass in Loaded
            // set logical focus on a window Windows had not activated yet, so
            // keyboard focus never landed - and on Alt-Tab the operator arrived
            // on whatever WPF fell back to, which in the reported case was the
            // network identity card at the BOTTOM of the dialog.
            FocusFirstControl();
        }

        /// <summary>
        /// On close: schedule deferred focus-return context announcement.
        /// Uses ApplicationIdle priority so it fires after focus settles back to main window.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            // Deferred context announcement — fires after focus returns to main window
            if (FocusReturnCallback != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, FocusReturnCallback);
            }
        }

        /// <summary>
        /// Finds and focuses the first focusable interactive control in the dialog.
        /// Skips labels, group boxes, and other non-interactive elements.
        /// </summary>
        protected virtual void FocusFirstControl()
        {
            // MoveFocus will find the first focusable element in tab order
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }

        /// <summary>
        /// Creates a standard OK/Cancel button panel.
        /// Call this from subclass constructors or XAML code-behind to add
        /// a consistent button row at the bottom of the dialog.
        /// </summary>
        /// <param name="okText">Text for the OK button (default "OK")</param>
        /// <param name="cancelText">Text for the Cancel button (default "Cancel")</param>
        /// <param name="onOk">Action to run when OK is clicked. If it returns without
        /// setting DialogResult, the dialog sets DialogResult = true and closes.</param>
        /// <param name="onCancel">Optional action for Cancel. Default just closes.</param>
        /// <returns>A StackPanel containing the buttons, ready to add to your layout.</returns>
        protected StackPanel CreateButtonPanel(
            Action? onOk = null,
            Action? onCancel = null,
            string? okText = null,
            string? cancelText = null)
        {
            okText ??= Radios.Lexicon.Get("connect.dialog.ok");
            cancelText ??= Radios.Lexicon.Get("connect.dialog.cancel");

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            string okAccessName = okText.Replace("_", "");
            var okButton = new Button
            {
                Content = okText,
                MinWidth = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true  // Enter key triggers this
            };
            AutomationProperties.SetName(okButton, okAccessName);
            SetAccessKeyProperty(okButton, okText);
            okButton.Click += (s, e) =>
            {
                onOk?.Invoke();
                // If the onOk handler didn't set DialogResult, record it now.
                // Via CloseWithResult, so a dialog realised with Show() closes
                // instead of throwing — see that method's comment (#159).
                if (DialogResult == null)
                {
                    CloseWithResult(true);
                }
            };

            string cancelAccessName = cancelText.Replace("_", "");
            var cancelButton = new Button
            {
                Content = cancelText,
                MinWidth = 80,
                Height = 28,
                IsCancel = true  // ESC also triggers this (backup to PreviewKeyDown)
            };
            AutomationProperties.SetName(cancelButton, cancelAccessName);
            SetAccessKeyProperty(cancelButton, cancelText);
            cancelButton.Click += (s, e) =>
            {
                onCancel?.Invoke();
                if (DialogResult == null)
                {
                    CloseWithResult(false);
                }
            };

            panel.Children.Add(okButton);
            panel.Children.Add(cancelButton);

            return panel;
        }

        /// <summary>
        /// Creates a button panel with OK, Cancel, and Apply buttons.
        /// </summary>
        protected StackPanel CreateButtonPanelWithApply(
            Action? onOk = null,
            Action? onApply = null,
            Action? onCancel = null,
            string? okText = null,
            string? applyText = null,
            string? cancelText = null)
        {
            applyText ??= Radios.Lexicon.Get("connect.dialog.apply");

            var panel = CreateButtonPanel(onOk, onCancel, okText, cancelText);

            // Insert Apply button before Cancel
            string applyAccessName = applyText.Replace("_", "");
            var applyButton = new Button
            {
                Content = applyText,
                MinWidth = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0)
            };
            AutomationProperties.SetName(applyButton, applyAccessName);
            SetAccessKeyProperty(applyButton, applyText);
            applyButton.Click += (s, e) =>
            {
                onApply?.Invoke();
                // Apply doesn't close — user stays in dialog
            };

            // Insert before the Cancel button (last child)
            panel.Children.Insert(panel.Children.Count - 1, applyButton);

            return panel;
        }

        /// <summary>
        /// Extract the access key letter from underscore-prefixed text (e.g. "_OK" → "Alt+O")
        /// and set both AccessKey and AcceleratorKey for screen reader compatibility.
        /// NVDA reads AcceleratorKey, JAWS reads the underscore access key from Content.
        /// </summary>
        private static void SetAccessKeyProperty(System.Windows.UIElement element, string text)
        {
            int idx = text.IndexOf('_');
            if (idx >= 0 && idx < text.Length - 1)
            {
                char key = char.ToUpper(text[idx + 1]);
                string combo = $"Alt+{key}";
                AutomationProperties.SetAccessKey(element, combo);
                AutomationProperties.SetAcceleratorKey(element, combo);
            }
        }

        /// <summary>
        /// Helper to show this dialog modally and return the result.
        /// Wraps ShowDialog() with standard error handling.
        /// </summary>
        public bool? ShowModalDialog()
        {
            try
            {
                return ShowDialog();
            }
            catch (InvalidOperationException)
            {
                // Can happen if window was already closed or owner is invalid
                return false;
            }
        }
    }
}
