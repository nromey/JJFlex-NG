using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>Which other radio a transmit noise setup is copied to: every radio this computer has settings for, except the connected one. Escape cancels.</summary>
internal sealed class TransmitSetupCopyDialog : JJFlexDialog
{
    private readonly ListBox _list = new();
    private readonly IReadOnlyList<(string RadioId, string Name)> _radios;
    private (string RadioId, string Name)? _chosen;

    private TransmitSetupCopyDialog(string setup, IReadOnlyList<(string RadioId, string Name)> radios)
    {
        _radios = radios;
        Title = Lexicon.Get("audio.noise_setups.copy_title", ("setup", setup));
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = Lexicon.Get("audio.noise_setups.copy_list_label"), Target = _list });
        _list.Height = 140;
        AutomationProperties.SetName(_list, Lexicon.Get("audio.noise_setups.copy_list"));
        foreach (var r in radios) _list.Items.Add(r.Name);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        panel.Children.Add(_list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var copy = new Button { Content = Lexicon.Get("audio.noise_setups.copy_button"), MinWidth = 80, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        AutomationProperties.SetName(copy, Lexicon.Get("audio.noise_setups.copy"));
        copy.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _chosen = _radios[_list.SelectedIndex];
            CloseWithResult(true);
        };
        var cancel = new Button { Content = Lexicon.Get("audio.noise_setups.cancel_button"), MinWidth = 80, Height = 26, IsCancel = true };
        AutomationProperties.SetName(cancel, Lexicon.Get("audio.noise_setups.cancel"));
        cancel.Click += (_, _) => CloseWithResult(false);
        buttons.Children.Add(copy);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    protected override void FocusFirstControl() => _list.Focus();

    /// <summary>The radios this computer keeps settings for, except the connected one, each with the name the operator knows it by.</summary>
    internal static IReadOnlyList<(string RadioId, string Name)> OtherKnownRadios(NoiseController controller)
    {
        string? current = controller.TransmitSetupRadioId;
        return RadioConfig.LoadAllKnown()
            .Where(c => RadioConfig.IsWellFormedRadioId(c.RadioId) && !string.Equals(c.RadioId, current, StringComparison.OrdinalIgnoreCase))
            .Select(c => (c.RadioId, RadioName(c)))
            .ToList();
    }

    private static string RadioName(RadioConfig c)
    {
        string name = !string.IsNullOrWhiteSpace(c.UserNickname) ? c.UserNickname
            : !string.IsNullOrWhiteSpace(c.Nickname) ? c.Nickname
            : !string.IsNullOrWhiteSpace(c.Model) ? c.Model : c.RadioId;
        return name == c.RadioId ? name : name + ", " + c.RadioId;
    }

    internal static (string RadioId, string Name)? Ask(Window owner, string setup, IReadOnlyList<(string RadioId, string Name)> radios)
    {
        var dialog = new TransmitSetupCopyDialog(setup, radios) { Owner = owner };
        return dialog.ShowModalDialog() == true ? dialog._chosen : null;
    }
}
