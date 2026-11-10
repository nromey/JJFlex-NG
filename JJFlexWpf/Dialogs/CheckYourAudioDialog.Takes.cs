using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio's door to Play takes (Unified takes, #864). Kept in its
/// own file so the check's own flow is not touched for it.
/// </summary>
public partial class CheckYourAudioDialog
{
    private Button? _playTakesButton;

    /// <summary>
    /// The Play takes button, always shown: takes of every kind and from every
    /// radio can be played and compared whatever method is chosen, with no
    /// radio and over SmartLink. Opening it never starts a check or keys.
    /// </summary>
    private void AddPlayTakesDoor()
    {
        _playTakesButton = new Button
        {
            Content = Lexicon.Get("audio.takes.open_button"),
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
        };
        AutomationProperties.SetName(_playTakesButton, Lexicon.Get("audio.takes.open_button"));
        JJFlexHelp.SetText(_playTakesButton, Lexicon.Get("audio.takes.help.open_button"));
        _playTakesButton.Click += (s, e) => PlayTakesDialog.Show(LiveRig);
        _sections.Add(CheckContent, _playTakesButton);
    }
}
