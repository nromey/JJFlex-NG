using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The Explain This text on a surface you can read at your own pace: a
    /// read-only edit, so the arrows move by line, word and character and a
    /// sentence can be heard again without hearing the ones around it.
    /// Sprint 44 Track K (#519). Opened by <see cref="ContextHelpSurface"/>.
    /// </summary>
    /// <remarks>
    /// A read-only edit rather than a list, on purpose: this is PROSE. A list
    /// row is the right control for a key and its meaning, which is why the
    /// key surfaces use one; an explanation is sentences, and Noel's own
    /// instinct for reading at leisure was the read-only edit (#158).
    /// </remarks>
    public partial class ContextHelpDialog : JJFlexDialog
    {
        /// <summary>The explanation, as Ctrl+F1 would have spoken it.</summary>
        public string Body { get; set; } = "";

        /// <summary>The accessible name of the control being explained, if any.</summary>
        public string Subject { get; set; } = "";

        public ContextHelpDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            KeyHelpSurfaces.Attach(this);
            AutomationProperties.SetName(BodyText, Radios.Lexicon.Get("help.context.body_name"));
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Title = string.IsNullOrWhiteSpace(Subject)
                ? Radios.Lexicon.Get("help.context.dialog_title")
                : Radios.Lexicon.Get("help.context.dialog_title_for", ("subject", Subject));
            AutomationProperties.SetName(this, Title);

            BodyText.Text = Body ?? "";
            BodyText.CaretIndex = 0;
            BodyText.Focus();
        }

        protected override void FocusFirstControl()
        {
            BodyText.Focus();
        }
    }
}
