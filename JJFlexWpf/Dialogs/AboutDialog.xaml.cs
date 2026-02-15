using System.Windows;

namespace JJFlexWpf.Dialogs
{
    public partial class AboutDialog : JJFlexDialog
    {
        public string ProductName { get; set; } = "";
        public string VersionText { get; set; } = "";
        public string Copyright { get; set; } = "";
        public string CompanyName { get; set; } = "";
        public string Description { get; set; } = "";

        public AboutDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Title = $"About {ProductName}";
            ProductNameLabel.Text = ProductName;
            VersionLabel.Text = VersionText;
            CopyrightLabel.Text = Copyright;
            CompanyLabel.Text = CompanyName;
            DescriptionBox.Text = Description;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
