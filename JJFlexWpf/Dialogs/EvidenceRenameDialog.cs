using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The one-field prompt that gives a stored evidence record the operator's own
/// name. Empty clears the name, Escape cancels.
/// </summary>
/// <remarks>
/// <para>
/// <b>One prompt, every evidence family.</b> The signal captures grew this
/// first; the saved check runs needed exactly the same thing when the Fixer's
/// runs finally started persisting, and a second private copy would have been
/// the Sprint 36 duplication all over again — two implementations of one idea
/// in disjoint files, conflicting with nothing, building cleanly, and drifting
/// the moment either one's wording was improved.
/// </para>
/// <para>
/// It takes strings rather than a record type on purpose: the two families
/// share no base class, and the prompt only ever needed the noun, the id and
/// the current name.
/// </para>
/// </remarks>
internal sealed class EvidenceRenameDialog : JJFlexDialog
{
    private readonly TextBox _name = new();
    private bool _accepted;

    private EvidenceRenameDialog(string noun, string id, string currentLabel)
    {
        Title = "Rename " + noun + " " + id + " — JJ Flexible";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(12) };

        var label = new Label
        {
            Content = "_Name for this " + noun + ":",
            Target = _name,
        };
        panel.Children.Add(label);

        _name.Text = currentLabel;
        _name.Margin = new Thickness(0, 2, 0, 8);
        AutomationProperties.SetName(_name, "Name for this " + noun);
        JJFlexHelp.SetText(_name,
            "A name you will recognize later. Leave it empty to go back to the "
            + noun + "'s id.");
        panel.Children.Add(_name);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button
        {
            Content = "_OK", MinWidth = 80, Height = 26,
            Margin = new Thickness(0, 0, 8, 0), IsDefault = true,
        };
        AutomationProperties.SetName(ok, "OK");
        ok.Click += (_, _) => { _accepted = true; CloseWithResult(true); };
        var cancel = new Button
        {
            Content = "_Cancel", MinWidth = 80, Height = 26, IsCancel = true,
        };
        AutomationProperties.SetName(cancel, "Cancel");
        cancel.Click += (_, _) => CloseWithResult(false);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    protected override void FocusFirstControl()
    {
        _name.Focus();
        _name.SelectAll();
    }

    /// <param name="noun">What the thing is, lower case and singular:
    /// "capture", "run". It appears in the title, the field label and the
    /// help.</param>
    /// <param name="id">The record's permanent id — shown because renaming
    /// never changes it, and the id is what a support thread quotes.</param>
    /// <param name="currentLabel">The name it has now, empty for none.</param>
    /// <returns>The new name, or null when cancelled.</returns>
    internal static string? Ask(Window owner, string noun, string id, string currentLabel)
    {
        var dialog = new EvidenceRenameDialog(noun, id, currentLabel ?? "") { Owner = owner };
        dialog.ShowModalDialog();
        return dialog._accepted ? dialog._name.Text : null;
    }
}
