using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop toolbar handlers: load, save, export, import and reset
/// of the audio chain presets.
///
/// Split out of AudioWorkshopDialog.xaml.cs in Sprint 32 Track A, with no
/// change to any member.
/// </summary>
public partial class AudioWorkshopDialog
{
    #region Toolbar Handlers

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var presets = GetPresetsCallback?.Invoke();
        if (presets == null || presets.Presets.Count == 0)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.preset.none_available"), VerbosityLevel.Terse);
            return;
        }

        // Build a simple picker dialog
        var picker = new JJFlexDialog { Title = "Load Audio Preset", Width = 350, Height = 300 };
        picker.ResizeMode = ResizeMode.NoResize;
        var panel = new DockPanel { Margin = new Thickness(12) };

        var listBox = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(listBox, "Audio presets");
        foreach (var p in presets.Presets)
            listBox.Items.Add(p.Name);
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
        DockPanel.SetDock(listBox, Dock.Top);
        panel.Children.Add(listBox);

        // Delete lives here rather than on the toolbar because this is where
        // the selection is — the help page has promised it for a while and the
        // action never existed anywhere. Confirmed before doing anything: a
        // preset is small but there is no undo, and the built-in three only
        // come back by deleting the whole preset file.
        void DeleteSelected()
        {
            int idx = listBox.SelectedIndex;
            if (idx < 0) return;
            var preset = presets.Presets[idx];

            var confirm = new ConfirmActionDialog(
                Lexicon.Get("audio.preset.delete_title"),
                Lexicon.Get("audio.preset.delete_body", ("preset", preset.FormatForSpeech())),
                question: Lexicon.Get("audio.preset.delete_question"),
                yesLabel: Lexicon.Get("audio.preset.delete_yes_label"));
            if (confirm.ShowDialog() != true)
                return;

            presets.Presets.RemoveAt(idx);
            listBox.Items.RemoveAt(idx);

            // A delete that could not be written is a delete that undoes itself
            // the next time the list is read, so it must not be announced as
            // done. The list still updates — the operator asked for it and
            // seeing it linger would be its own lie — but the words say what
            // will actually be true tomorrow.
            bool saved = PersistPresets(presets);
            string outcome = saved
                ? Lexicon.Get("audio.preset.deleted", ("preset", preset.Name))
                : Lexicon.Get("audio.preset.deleted_but_not_saved",
                      ("preset", preset.Name), ("reason", PresetSaveFailed));
            var level = saved ? VerbosityLevel.Terse : VerbosityLevel.Critical;

            if (listBox.Items.Count == 0)
            {
                // Nothing left to load — the picker has no job now.
                ScreenReaderOutput.Speak(
                    saved
                        ? outcome + ". " + Lexicon.Get("audio.preset.none_left")
                        : outcome,
                    level);
                picker.Close();
                return;
            }
            listBox.SelectedIndex = Math.Min(idx, listBox.Items.Count - 1);
            listBox.Focus();
            ScreenReaderOutput.Speak(outcome, level);
        }

        // The Delete key on the list is the primary route; the button is the
        // discoverable one. The button carries NO Alt mnemonic on purpose —
        // WPF access keys match with Shift held, and Alt+D would shadow the
        // global Alt+Shift+D chord (same trap the toolbar's old Alt+S sprang
        // on Speak Transmit Status).
        listBox.PreviewKeyDown += (s2, e2) =>
        {
            if (e2.Key == Key.Delete)
            {
                DeleteSelected();
                e2.Handled = true;
            }
        };

        var okBtn = new Button { Content = "OK", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var deleteBtn = new Button { Content = "Delete", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80, Height = 28, IsCancel = true };
        AutomationProperties.SetName(okBtn, "OK");
        AutomationProperties.SetName(deleteBtn, "Delete preset");
        AutomationProperties.SetAcceleratorKey(deleteBtn, "Delete");
        AutomationProperties.SetName(cancelBtn, "Cancel");
        okBtn.Click += (s2, e2) =>
        {
            if (listBox.SelectedIndex >= 0 && _rig != null)
            {
                var preset = presets.Presets[listBox.SelectedIndex];
                // ApplyTo reports what could not be applied faithfully — an
                // EQ the radio has not reported, a preset tuned for a
                // different input (#51). Those ride the load announcement.
                string note = preset.ApplyTo(_rig);
                PollTxAudio();
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.preset.loaded", ("preset", preset.Name)) +
                    (string.IsNullOrEmpty(note) ? "" : ". " + note),
                    VerbosityLevel.Terse);
            }
            picker.Close();
        };
        deleteBtn.Click += (s2, e2) => DeleteSelected();
        cancelBtn.Click += (s2, e2) => picker.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(deleteBtn);
        buttons.Children.Add(cancelBtn);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        picker.Content = panel;
        picker.ShowDialog();
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical);
            return;
        }

        // Prompt for name with a simple input dialog
        var inputDialog = new JJFlexDialog { Title = "Save Audio Preset", Width = 350, Height = 180 };
        inputDialog.ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };

        var prompt = new TextBlock { Text = "Enter a name for this preset:", Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(prompt, "Enter a name for this preset");
        panel.Children.Add(prompt);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(nameBox, "Preset name");
        panel.Children.Add(nameBox);

        var okBtn = new Button { Content = "OK", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80, Height = 28, IsCancel = true };
        AutomationProperties.SetName(okBtn, "OK");
        AutomationProperties.SetName(cancelBtn, "Cancel");
        okBtn.Click += (s2, e2) =>
        {
            string name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.please_enter_a_name"), VerbosityLevel.Terse);
                return;
            }
            var preset = AudioChainPreset.CaptureFrom(_rig, name, ReadSavedPcInputName());
            var presets = GetPresetsCallback?.Invoke() ?? AudioChainPresets.CreateDefaults();
            presets.Presets.Add(preset);
            if (PersistPresets(presets))
                ScreenReaderOutput.Speak(Lexicon.Get("audio.preset.saved", ("preset", name)),
                    VerbosityLevel.Terse);
            else
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.preset.save_failed", ("preset", name), ("reason", PresetSaveFailed)),
                    VerbosityLevel.Critical);
            inputDialog.Close();
        };
        cancelBtn.Click += (s2, e2) => inputDialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        inputDialog.Content = panel;
        inputDialog.ShowDialog();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical);
            return;
        }

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Audio Preset (*.xml)|*.xml",
            DefaultExt = ".xml",
            FileName = "audio_preset.xml"
        };

        if (sfd.ShowDialog() == true)
        {
            var preset = AudioChainPreset.CaptureFrom(_rig,
                System.IO.Path.GetFileNameWithoutExtension(sfd.FileName),
                ReadSavedPcInputName());
            // Save reports whether the file landed. A failed write announced
            // as an export is the lying-receipt pattern this dialog keeps
            // finding — never say "exported" on faith.
            if (preset.Save(sfd.FileName))
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.preset.exported", ("file", System.IO.Path.GetFileName(sfd.FileName))),
                    VerbosityLevel.Terse);
            else
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.preset.export_failed", ("file", System.IO.Path.GetFileName(sfd.FileName))),
                    VerbosityLevel.Critical);
        }
    }

    /// <summary>
    /// The Windows capture device the operator chose, from audioDevices.xml —
    /// a file read, never a PortAudio enumeration (this dialog must not
    /// enumerate while a radio connection may be live). "" when unknown;
    /// recorded into presets so a preset tuned against one microphone can say
    /// so on another (#51).
    /// </summary>
    private string ReadSavedPcInputName() => ReadSavedPcInput().Name;

    /// <summary>
    /// Import a preset file into the saved collection — the missing half of
    /// Export, which for a while produced files nothing could read back,
    /// including on the friend's machine that is the whole point of exporting.
    /// Deliberately does NOT apply the preset to the radio: importing a file is
    /// not a request to retune a live transmitter. No rig required either — a
    /// preset is a file, and the callbacks are wired radio-or-not (see the
    /// MainWindow wiring note on GetPresetsCallback).
    /// </summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio Preset (*.xml)|*.xml|All Files (*.*)|*.*",
            DefaultExt = ".xml"
        };
        if (ofd.ShowDialog() != true) return;

        if (!AudioChainPreset.TryLoad(ofd.FileName, out var preset, out string fileNote))
        {
            // Honest failure: a bad file must never quietly become a blank
            // preset in the list.
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.preset.import_unreadable",
                    ("file", System.IO.Path.GetFileName(ofd.FileName))),
                VerbosityLevel.Critical);
            return;
        }

        if (string.IsNullOrWhiteSpace(preset.Name))
            preset.Name = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName);

        var presets = GetPresetsCallback?.Invoke() ?? AudioChainPresets.CreateDefaults();

        // Two presets with one name are indistinguishable by ear in the Load
        // picker, so a colliding import gets a numbered name instead.
        string baseName = preset.Name;
        int n = 2;
        while (presets.Presets.Exists(p => p.Name == preset.Name))
            preset.Name = $"{baseName} {n++}";

        presets.Presets.Add(preset);
        if (PersistPresets(presets))
        {
            // The file's own caveats (newer schema, pre-EQ vintage) ride the
            // import announcement — said once, here, rather than on every
            // future load (#50).
            string suffix = string.IsNullOrEmpty(fileNote) ? "" : " " + fileNote;
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.preset.imported", ("preset", preset.FormatForSpeech())) + suffix,
                VerbosityLevel.Terse);
        }
        else
            ScreenReaderOutput.Speak(
                Lexicon.Get("audio.preset.imported_but_not_saved",
                    ("preset", preset.Name), ("reason", PresetSaveFailed)),
                VerbosityLevel.Critical);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.no_radio_connected"), VerbosityLevel.Critical);
            return;
        }

        var defaults = new AudioChainPreset();
        _ = defaults.ApplyTo(_rig); // a default preset carries no EQ or input notes
        PollTxAudio();
        ScreenReaderOutput.Speak(Lexicon.Get("audio.preset.reset_to_defaults"), VerbosityLevel.Terse);
    }

    #endregion
}
