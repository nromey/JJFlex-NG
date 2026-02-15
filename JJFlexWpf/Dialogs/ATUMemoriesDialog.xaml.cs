using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class ATUMemoriesDialog : JJFlexDialog
    {
        /// <summary>Returns current ATU memories enabled state (true=on).</summary>
        public Func<bool>? GetEnabled { get; set; }

        /// <summary>Called to set ATU memories enabled state.</summary>
        public Action<bool>? SetEnabled { get; set; }

        /// <summary>Called to clear all ATU memories.</summary>
        public Action? ClearMemories { get; set; }

        public ATUMemoriesDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnableControl.Items.Add("Off");
            EnableControl.Items.Add("On");
            bool enabled = GetEnabled?.Invoke() ?? false;
            EnableControl.SelectedIndex = enabled ? 1 : 0;
            EnableControl.SelectionChanged += (s, _) =>
            {
                SetEnabled?.Invoke(EnableControl.SelectedIndex == 1);
            };
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearMemories?.Invoke();
        }
    }
}
