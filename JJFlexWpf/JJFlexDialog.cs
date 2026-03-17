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
                // DialogResult throws InvalidOperationException on non-modal windows (Show vs ShowDialog).
                // ConnectingDialog is non-modal, so guard with try/catch.
                try { DialogResult = false; } catch (InvalidOperationException) { }
                Close();
                e.Handled = true;
            }
        }

        /// <summary>
        /// On load: sync AutomationProperties.Name with Title,
        /// then focus the first interactive control.
        /// </summary>
        private void JJFlexDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Set automation name from title for screen readers
            if (!string.IsNullOrEmpty(Title))
            {
                AutomationProperties.SetName(this, Title);
                // Speak title explicitly — NVDA may read focused control instead of window title
                Radios.ScreenReaderOutput.Speak(Title, Radios.VerbosityLevel.Terse, interrupt: true);
            }

            // Focus first interactive control
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
            string okText = "_OK",
            string cancelText = "_Cancel")
        {
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
                // If the onOk handler didn't set DialogResult, set it now
                if (DialogResult == null)
                {
                    DialogResult = true;
                    Close();
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
                    DialogResult = false;
                    Close();
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
            string okText = "_OK",
            string applyText = "_Apply",
            string cancelText = "_Cancel")
        {
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
        /// and set AutomationProperties.AccessKey so screen readers announce it.
        /// </summary>
        private static void SetAccessKeyProperty(System.Windows.UIElement element, string text)
        {
            int idx = text.IndexOf('_');
            if (idx >= 0 && idx < text.Length - 1)
            {
                char key = char.ToUpper(text[idx + 1]);
                AutomationProperties.SetAccessKey(element, $"Alt+{key}");
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
