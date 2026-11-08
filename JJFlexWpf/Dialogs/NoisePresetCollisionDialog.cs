using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>A preset of that name exists: replace it, or keep both. Escape cancels the save.</summary>
internal sealed class NoisePresetCollisionDialog : JJFlexDialog
{
    private PresetCollision? _answer;
    private readonly Button _replace;

    internal NoisePresetCollisionDialog(string name = "")
    {
        Title = Lexicon.Get("audio.noise_presets.collision_title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };
        var text = new TextBlock { Text = Lexicon.Get("audio.noise_presets.collision", ("name", name)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Focusable = true };
        AutomationProperties.SetName(text, text.Text);
        panel.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _replace = new Button { Content = Lexicon.Get("audio.noise_presets.replace_button"), MinWidth = 90, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(_replace, Lexicon.Get("audio.noise_presets.replace"));
        _replace.Click += (_, _) => { _answer = PresetCollision.Replace; CloseWithResult(true); };
        var both = new Button { Content = Lexicon.Get("audio.noise_presets.keep_both_button"), MinWidth = 90, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(both, Lexicon.Get("audio.noise_presets.keep_both"));
        both.Click += (_, _) => { _answer = PresetCollision.KeepBoth; CloseWithResult(true); };
        var cancel = new Button { Content = Lexicon.Get("audio.noise_presets.cancel_button"), MinWidth = 80, Height = 26, IsCancel = true };
        AutomationProperties.SetName(cancel, Lexicon.Get("audio.noise_presets.cancel"));
        cancel.Click += (_, _) => CloseWithResult(false);
        buttons.Children.Add(_replace); buttons.Children.Add(both); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    protected override void FocusFirstControl() => _replace.Focus();

    internal static PresetCollision? Ask(Window owner, string name)
    {
        var dialog = new NoisePresetCollisionDialog(name) { Owner = owner };
        dialog.ShowModalDialog();
        return dialog._answer;
    }
}
