using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf
{
    /// <summary>
    /// Base class for all JJFlexRadio WPF dialogs.
    /// Provides standard behavior: ESC to close, focus management,
    /// accessibility defaults, and consistent styling.
    /// </summary>
    public class JJFlexDialog : Window
    {
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

            // Set owner to MainWindow if available
            if (Application.Current?.MainWindow != null &&
                Application.Current.MainWindow.IsLoaded &&
                Application.Current.MainWindow != this)
            {
                try
                {
                    Owner = Application.Current.MainWindow;
                }
                catch
                {
                    // Owner assignment can fail if MainWindow is in a different thread
                    // or not fully initialized — safe to ignore
                }
            }

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
                DialogResult = false;
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
            }

            // Focus first interactive control
            FocusFirstControl();
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
            string okText = "OK",
            string cancelText = "Cancel")
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var okButton = new Button
            {
                Content = okText,
                MinWidth = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true  // Enter key triggers this
            };
            AutomationProperties.SetName(okButton, okText);
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

            var cancelButton = new Button
            {
                Content = cancelText,
                MinWidth = 80,
                Height = 28,
                IsCancel = true  // ESC also triggers this (backup to PreviewKeyDown)
            };
            AutomationProperties.SetName(cancelButton, cancelText);
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
            string okText = "OK",
            string applyText = "Apply",
            string cancelText = "Cancel")
        {
            var panel = CreateButtonPanel(onOk, onCancel, okText, cancelText);

            // Insert Apply button before Cancel
            var applyButton = new Button
            {
                Content = applyText,
                MinWidth = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0)
            };
            AutomationProperties.SetName(applyButton, applyText);
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
