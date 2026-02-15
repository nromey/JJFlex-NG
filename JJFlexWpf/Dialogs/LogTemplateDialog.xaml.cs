using System.ComponentModel;
using System.Windows;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Represents a single log field in the template.
    /// </summary>
    public class LogFieldItem : INotifyPropertyChanged
    {
        private string _value = "";

        public string Label { get; set; } = "";
        public string AdifTag { get; set; } = "";
        public bool IsReadOnly { get; set; }

        public string Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Generic log template dialog. Configure with field definitions
    /// to support different contest types (Default, Field Day, NA Sprint, SKCC, etc.)
    /// </summary>
    public partial class LogTemplateDialog : JJFlexDialog
    {
        /// <summary>The template name (e.g., "Default", "Field Day").</summary>
        public string TemplateName { get; set; } = "Default";

        /// <summary>Field definitions. Set before showing.</summary>
        public List<LogFieldItem> Fields { get; set; } = new();

        /// <summary>
        /// Called to validate the entry before saving.
        /// Returns error message or null on success.
        /// </summary>
        public Func<List<LogFieldItem>, string?>? ValidateEntry { get; set; }

        /// <summary>
        /// Called to save the entry. Receives the field values.
        /// </summary>
        public Action<List<LogFieldItem>>? SaveEntry { get; set; }

        /// <summary>
        /// Called after save to update scoring/statistics if applicable.
        /// </summary>
        public Action<List<LogFieldItem>>? PostSaveAction { get; set; }

        public LogTemplateDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Title = $"Log Entry - {TemplateName}";
            FieldsPanel.ItemsSource = Fields;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string? error = ValidateEntry?.Invoke(Fields);
            if (error != null)
            {
                MessageBox.Show(error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveEntry?.Invoke(Fields);
            PostSaveAction?.Invoke(Fields);
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Creates a standard set of fields for the Default log template.
        /// </summary>
        public static List<LogFieldItem> CreateDefaultFields()
        {
            return new List<LogFieldItem>
            {
                new() { Label = "Date", AdifTag = "QSO_DATE" },
                new() { Label = "Time", AdifTag = "TIME_ON" },
                new() { Label = "Call", AdifTag = "CALL" },
                new() { Label = "Mode", AdifTag = "MODE" },
                new() { Label = "Band", AdifTag = "BAND", IsReadOnly = true },
                new() { Label = "RX Freq", AdifTag = "FREQ" },
                new() { Label = "TX Freq", AdifTag = "FREQ_TX" },
                new() { Label = "His RST", AdifTag = "RST_RCVD" },
                new() { Label = "My RST", AdifTag = "RST_SENT" },
                new() { Label = "QTH", AdifTag = "QTH" },
                new() { Label = "State", AdifTag = "STATE" },
                new() { Label = "Country", AdifTag = "COUNTRY" },
                new() { Label = "Name", AdifTag = "NAME" },
                new() { Label = "Comments", AdifTag = "COMMENT" },
                new() { Label = "Serial", AdifTag = "SRX" },
                new() { Label = "Grid", AdifTag = "GRIDSQUARE" },
            };
        }

        /// <summary>
        /// Creates fields for Field Day template.
        /// </summary>
        public static List<LogFieldItem> CreateFieldDayFields()
        {
            return new List<LogFieldItem>
            {
                new() { Label = "Date", AdifTag = "QSO_DATE" },
                new() { Label = "Time", AdifTag = "TIME_ON" },
                new() { Label = "Call", AdifTag = "CALL" },
                new() { Label = "Class", AdifTag = "CLASS" },
                new() { Label = "Section", AdifTag = "ARRL_SECT" },
                new() { Label = "Freq", AdifTag = "FREQ" },
                new() { Label = "Mode", AdifTag = "MODE" },
                new() { Label = "Band", AdifTag = "BAND", IsReadOnly = true },
            };
        }

        /// <summary>
        /// Creates fields for NA Sprint template.
        /// </summary>
        public static List<LogFieldItem> CreateNASprintFields()
        {
            return new List<LogFieldItem>
            {
                new() { Label = "Date", AdifTag = "QSO_DATE" },
                new() { Label = "Time", AdifTag = "TIME_ON" },
                new() { Label = "Call", AdifTag = "CALL" },
                new() { Label = "His Serial", AdifTag = "SRX" },
                new() { Label = "State", AdifTag = "STATE" },
                new() { Label = "Name", AdifTag = "NAME" },
                new() { Label = "Freq", AdifTag = "FREQ" },
                new() { Label = "Mode", AdifTag = "MODE", IsReadOnly = true, Value = "CW" },
                new() { Label = "Band", AdifTag = "BAND", IsReadOnly = true },
            };
        }

        /// <summary>
        /// Creates fields for SKCC Weekend Sprint template.
        /// </summary>
        public static List<LogFieldItem> CreateSKCCFields()
        {
            return new List<LogFieldItem>
            {
                new() { Label = "Date", AdifTag = "QSO_DATE" },
                new() { Label = "Time", AdifTag = "TIME_ON" },
                new() { Label = "Call", AdifTag = "CALL" },
                new() { Label = "His RST", AdifTag = "RST_RCVD" },
                new() { Label = "My RST", AdifTag = "RST_SENT" },
                new() { Label = "SPC", AdifTag = "STATE" },
                new() { Label = "Name", AdifTag = "NAME" },
                new() { Label = "SKCC #", AdifTag = "SKCC" },
                new() { Label = "Freq", AdifTag = "FREQ" },
                new() { Label = "Mode", AdifTag = "MODE", IsReadOnly = true, Value = "CW" },
                new() { Label = "Band", AdifTag = "BAND", IsReadOnly = true },
                new() { Label = "Comments", AdifTag = "COMMENT" },
            };
        }
    }
}
