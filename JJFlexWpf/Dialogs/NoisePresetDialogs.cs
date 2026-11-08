using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Save the current settings as a preset (Sprint 49 Track E, E10): a name,
/// and which sections it holds — receive noise reduction and the transmit
/// chain, pre-ticked by where it was opened from. A collision asks whether
/// to replace the old preset or keep both. Escape cancels. Every rule is
/// <see cref="NoiseController"/>'s.
/// </summary>
internal sealed class NoisePresetSaveDialog : JJFlexDialog
{
    private readonly TextBox _name = new();
    private readonly CheckBox _noise;
    private readonly CheckBox _chain;
    private readonly NoiseController? _controller;

    internal NoisePresetSaveDialog(NoiseController? controller = null, bool fromNoise = true)
    {
        _controller = controller;
        Title = Lexicon.Get("audio.noise_presets.save_title");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = Lexicon.Get("audio.noise_presets.name_label"), Target = _name });
        _name.Margin = new Thickness(0, 2, 0, 8);
        AutomationProperties.SetName(_name, Lexicon.Get("audio.noise_presets.name"));
        JJFlexHelp.SetText(_name, Lexicon.Get("audio.noise_help.presets.name"));
        panel.Children.Add(_name);

        _noise = new CheckBox { Content = Lexicon.Get("audio.noise_presets.include_noise"), IsChecked = fromNoise, Margin = new Thickness(0, 2, 0, 2) };
        AutomationProperties.SetName(_noise, Lexicon.Get("audio.noise_presets.include_noise"));
        JJFlexHelp.SetText(_noise, Lexicon.Get("audio.noise_help.presets.include_noise"));
        panel.Children.Add(_noise);
        _chain = new CheckBox { Content = Lexicon.Get("audio.noise_presets.include_chain"), IsChecked = !fromNoise, Margin = new Thickness(0, 2, 0, 8) };
        AutomationProperties.SetName(_chain, Lexicon.Get("audio.noise_presets.include_chain"));
        JJFlexHelp.SetText(_chain, Lexicon.Get("audio.noise_help.presets.include_chain"));
        panel.Children.Add(_chain);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = Lexicon.Get("audio.noise_presets.save_button"), MinWidth = 80, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        AutomationProperties.SetName(save, Lexicon.Get("audio.noise_presets.save"));
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = Lexicon.Get("audio.noise_presets.cancel_button"), MinWidth = 80, Height = 26, IsCancel = true };
        AutomationProperties.SetName(cancel, Lexicon.Get("audio.noise_presets.cancel"));
        cancel.Click += (_, _) => CloseWithResult(false);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    protected override void FocusFirstControl() => _name.Focus();

    private void Save()
    {
        if (_controller == null) { CloseWithResult(false); return; }
        string name = _name.Text.Trim();
        bool noise = _noise.IsChecked == true, chain = _chain.IsChecked == true;
        if (name.Length == 0 || (!noise && !chain))
        {
            ScreenReaderOutput.Speak(Lexicon.Get(name.Length == 0 ? "audio.noise_presets.need_name" : "audio.noise_presets.need_section"),
                VerbosityLevel.Terse, interrupt: true);
            return;
        }
        var collision = PresetCollision.Replace;
        if (_controller.PresetExists(name))
        {
            var answer = NoisePresetCollisionDialog.Ask(this, name);
            if (answer == null) return;
            collision = answer.Value;
        }
        if (_controller.SavePreset(name, noise, chain, collision, "save dialog") != null) CloseWithResult(true);
    }

    internal static void Show(Window? owner, NoiseController controller, bool fromNoise)
    {
        var dialog = new NoisePresetSaveDialog(controller, fromNoise) { Owner = owner };
        dialog.ShowModalDialog();
    }
}

/// <summary>
/// Manage presets (Sprint 49 Track E, E10): every preset in the operator's
/// store, with Apply, Rename and Delete. Rename is new (ruled with the
/// presets design); delete asks first. Escape closes.
/// </summary>
internal sealed class NoisePresetManagerDialog : JJFlexDialog
{
    private readonly ListBox _list = new();
    private readonly NoiseController? _controller;

    internal NoisePresetManagerDialog(NoiseController? controller = null)
    {
        _controller = controller;
        Title = Lexicon.Get("audio.noise_presets.manage_title");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = Lexicon.Get("audio.noise_presets.list_label"), Target = _list });
        _list.Height = 180;
        AutomationProperties.SetName(_list, Lexicon.Get("audio.noise_presets.list"));
        panel.Children.Add(_list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(MakeButton("audio.noise_presets.apply_button", "audio.noise_presets.apply", () => { if (Selected is string n) { _controller?.ApplyPreset(n, "manage presets"); } }));
        buttons.Children.Add(MakeButton("audio.noise_presets.rename_button", "audio.noise_presets.rename", Rename));
        buttons.Children.Add(MakeButton("audio.noise_presets.delete_button", "audio.noise_presets.delete", Delete));
        var close = new Button { Content = Lexicon.Get("audio.noise_presets.close_button"), MinWidth = 80, Height = 26, IsCancel = true };
        AutomationProperties.SetName(close, Lexicon.Get("audio.noise_presets.close"));
        close.Click += (_, _) => CloseWithResult(true);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
        Fill();
    }

    private static Button MakeButton(string buttonKey, string nameKey, Action act)
    {
        var b = new Button { Content = Lexicon.Get(buttonKey), MinWidth = 80, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(b, Lexicon.Get(nameKey));
        b.Click += (_, _) => act();
        return b;
    }

    private string? Selected => _list.SelectedItem as string;

    private void Fill()
    {
        _list.Items.Clear();
        if (_controller == null) return;
        foreach (var p in _controller.AllPresetNames()) _list.Items.Add(p);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    protected override void FocusFirstControl() => _list.Focus();

    private void Rename()
    {
        if (Selected is not string old || _controller == null) return;
        string? name = EvidenceRenameDialog.Ask(this, Lexicon.Get("audio.noise_presets.noun"), old, old);
        if (string.IsNullOrWhiteSpace(name) || name == old) return;
        if (!_controller.RenamePreset(old, name))
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise_presets.rename_taken", ("name", name.Trim())), VerbosityLevel.Terse, interrupt: true);
        Fill();
    }

    private void Delete()
    {
        if (Selected is not string name || _controller == null) return;
        var sure = MessageBox.Show(this, Lexicon.Get("audio.noise_presets.delete_confirm", ("name", name)),
            Lexicon.Get("audio.noise_presets.delete"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (sure != MessageBoxResult.Yes) return;
        _controller.DeletePreset(name);
        Fill();
    }

    internal static void Show(Window? owner, NoiseController controller)
    {
        var dialog = new NoisePresetManagerDialog(controller) { Owner = owner };
        dialog.ShowModalDialog();
    }
}
