using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// "Save current as setup..." (Sprint 49 Track H2, design part seven): a name
/// for the live transmit chain and gate, saved as one of this radio's transmit
/// noise setups. A name another setup holds asks before replacing it. Escape
/// cancels. Every rule is <see cref="NoiseController"/>'s; the store is Track H's.
/// </summary>
internal sealed class TransmitSetupSaveDialog : JJFlexDialog
{
    private readonly TextBox _name = new();
    private readonly NoiseController? _controller;

    internal TransmitSetupSaveDialog(NoiseController? controller = null)
    {
        _controller = controller;
        Title = Lexicon.Get("audio.noise_setups.save_title");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = Lexicon.Get("audio.noise_setups.name_label"), Target = _name });
        _name.Margin = new Thickness(0, 2, 0, 8);
        AutomationProperties.SetName(_name, Lexicon.Get("audio.noise_setups.name"));
        JJFlexHelp.SetText(_name, Lexicon.Get("audio.noise_help.setups.name"));
        panel.Children.Add(_name);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = Lexicon.Get("audio.noise_setups.save_button"), MinWidth = 80, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        AutomationProperties.SetName(save, Lexicon.Get("audio.noise_setups.save"));
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = Lexicon.Get("audio.noise_setups.cancel_button"), MinWidth = 80, Height = 26, IsCancel = true };
        AutomationProperties.SetName(cancel, Lexicon.Get("audio.noise_setups.cancel"));
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
        if (name.Length == 0)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise_setups.need_name"), VerbosityLevel.Terse, interrupt: true);
            return;
        }
        bool replace = false;
        if (_controller.TransmitSetupNames().Any(n => TxNoiseSetupStore.NameEquals(n, name)))
        {
            if (!TransmitSetupConfirm.Ask(this, Lexicon.Get("audio.noise_setups.save"), Lexicon.Get("audio.noise_setups.replace_confirm", ("setup", name)))) return;
            replace = true;
        }
        // "Saved for this radio" is said once this dialog has closed and focus
        // has landed (bench round, item 2): said at the close, the reader's own
        // flush on the window change swallowed it every time. A refusal keeps the
        // dialog open, so it is said at once.
        using var answer = _controller.HoldAnswer();
        if (_controller.SaveTransmitSetup(name, replace, "save setup dialog").Succeeded)
        {
            answer.AfterTheWindowCloses();
            CloseWithResult(true);
        }
    }

    internal static void Show(Window? owner, NoiseController controller)
    {
        // With no radio there is nowhere to keep a setup: say so before the name
        // is asked for, not after it is typed (bench round, item 9).
        if (!controller.TransmitSetupsReadyOrSay()) return;
        var dialog = new TransmitSetupSaveDialog(controller) { Owner = owner };
        dialog.ShowModalDialog();
    }
}

/// <summary>
/// "Manage setups..." (Sprint 49 Track H2): this radio's transmit noise
/// setups, with Apply, Rename, Delete and "Copy this setup to another
/// radio..." (ruled S10, 2026-11-18 08:28: nothing automatic). Delete asks
/// first. Escape closes.
/// </summary>
internal sealed class TransmitSetupManagerDialog : JJFlexDialog
{
    private readonly ListBox _list = new();
    private readonly NoiseController? _controller;

    internal TransmitSetupManagerDialog(NoiseController? controller = null)
    {
        _controller = controller;
        Title = Lexicon.Get("audio.noise_setups.manage_title");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = Lexicon.Get("audio.noise_setups.list_label"), Target = _list });
        _list.Height = 180;
        AutomationProperties.SetName(_list, Lexicon.Get("audio.noise_setups.list"));
        panel.Children.Add(_list);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(MakeButton("audio.noise_setups.apply_button", "audio.noise_setups.apply",
            () => { if (Selected is string n) _controller?.ApplyTransmitSetup(n, "manage setups"); }));
        buttons.Children.Add(MakeButton("audio.noise_setups.rename_button", "audio.noise_setups.rename", Rename));
        buttons.Children.Add(MakeButton("audio.noise_setups.delete_button", "audio.noise_setups.delete", Delete));
        buttons.Children.Add(MakeButton("audio.noise_setups.copy_button", "audio.noise_setups.copy", Copy));
        var close = new Button { Content = Lexicon.Get("audio.noise_setups.close_button"), MinWidth = 80, Height = 26, IsCancel = true };
        AutomationProperties.SetName(close, Lexicon.Get("audio.noise_setups.close"));
        close.Click += (_, _) => CloseWithResult(true);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
        Fill();
    }

    private static Button MakeButton(string buttonKey, string nameKey, Action act)
    {
        var b = new Button { Content = Lexicon.Get(buttonKey), MinWidth = 80, Height = 26, Margin = new Thickness(0, 0, 8, 4) };
        AutomationProperties.SetName(b, Lexicon.Get(nameKey));
        b.Click += (_, _) => act();
        return b;
    }

    private string? Selected => _list.SelectedItem as string;

    private void Fill()
    {
        _list.Items.Clear();
        if (_controller == null) return;
        foreach (var name in _controller.TransmitSetupNames()) _list.Items.Add(name);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    protected override void FocusFirstControl() => _list.Focus();

    private void Rename()
    {
        if (Selected is not string old || _controller == null) return;
        string? name = EvidenceRenameDialog.Ask(this, Lexicon.Get("audio.noise_setups.noun"), old, old,
            Lexicon.Get("audio.noise_help.setups.rename"));
        if (string.IsNullOrWhiteSpace(name) || name == old) return;
        // The rename prompt has just closed and focus is coming back here: the
        // answer waits for that (bench round, item 2).
        using (var answer = _controller.HoldAnswer())
        {
            answer.AfterTheWindowCloses();
            _controller.RenameTransmitSetup(old, name);
        }
        Fill();
    }

    private void Delete()
    {
        if (Selected is not string name || _controller == null) return;
        if (!TransmitSetupConfirm.Ask(this, Lexicon.Get("audio.noise_setups.delete"), Lexicon.Get("audio.noise_setups.delete_confirm", ("setup", name)))) return;
        // The confirmation has just closed: the answer waits for focus to land back here.
        using (var answer = _controller.HoldAnswer())
        {
            answer.AfterTheWindowCloses();
            _controller.DeleteTransmitSetup(name);
        }
        Fill();
    }

    /// <summary>"Copy this setup to another radio...": pick one of the other radios this computer knows, then copy; a name it already holds asks first.</summary>
    private void Copy()
    {
        if (Selected is not string name || _controller == null) return;
        var radios = TransmitSetupCopyDialog.OtherKnownRadios(_controller);
        if (radios.Count == 0)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.noise_setups.no_other_radios"), VerbosityLevel.Terse, interrupt: true);
            return;
        }
        var target = TransmitSetupCopyDialog.Ask(this, name, radios);
        if (target is not { } radio) return;
        TxNoiseSetupResult result;
        // The radio picker has just closed: the answer waits for focus to land back here.
        using (var answer = _controller.HoldAnswer())
        {
            result = _controller.CopyTransmitSetupToRadio(name, radio.RadioId, radio.Name, replace: false);
            if (result.Outcome != TxNoiseSetupOutcome.NameTaken) answer.AfterTheWindowCloses();
        }
        if (result.Outcome == TxNoiseSetupOutcome.NameTaken
            && TransmitSetupConfirm.Ask(this, Lexicon.Get("audio.noise_setups.copy"), Lexicon.Get("audio.noise_setups.copy_replace_confirm", ("setup", name), ("radio", radio.Name))))
        {
            using var answer = _controller.HoldAnswer();
            answer.AfterTheWindowCloses();
            _controller.CopyTransmitSetupToRadio(name, radio.RadioId, radio.Name, replace: true);
        }
    }

    internal static void Show(Window? owner, NoiseController controller)
    {
        var dialog = new TransmitSetupManagerDialog(controller) { Owner = owner };
        dialog.ShowModalDialog();
    }
}

/// <summary>
/// The setups' yes-or-no questions (Sprint 49 Track H2 bench round, item 9):
/// the project's own confirmation dialog, which Escape closes as No. They
/// were Windows message boxes, and a Yes/No message box has no Cancel, so
/// Escape did nothing on them.
/// </summary>
internal static class TransmitSetupConfirm
{
    internal static bool Ask(Window owner, string title, string body)
    {
        var confirm = new ConfirmActionDialog(title, body,
            question: Lexicon.Get("audio.noise_setups.confirm_question")) { Owner = owner };
        return confirm.ShowDialog() == true;
    }
}
