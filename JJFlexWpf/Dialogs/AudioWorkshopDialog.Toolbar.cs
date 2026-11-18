using System;
using System.Collections.Generic;
using System.Linq;
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
        // Every preset the store holds, the chain presets first and then the
        // noise-only ones this toolbar's own Save can make (Sprint 49 Track H2
        // repair round 2, item 3; part five: "this toolbar lists all presets").
        var all = presets == null ? new List<AudioChainPreset>() : WorkshopPresetList(presets);
        if (presets == null || all.Count == 0)
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
        foreach (var p in all)
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
            var preset = all[idx];

            var confirm = new ConfirmActionDialog(
                Lexicon.Get("audio.preset.delete_title"),
                Lexicon.Get("audio.preset.delete_body", ("preset", preset.FormatForSpeech())),
                question: Lexicon.Get("audio.preset.delete_question"),
                yesLabel: Lexicon.Get("audio.preset.delete_yes_label"));
            if (confirm.ShowDialog() != true)
                return;

            if (!presets.Presets.Remove(preset)) presets.NoisePresets.Remove(preset);
            all.RemoveAt(idx);
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
            var preset = listBox.SelectedIndex >= 0 ? all[listBox.SelectedIndex] : null;
            // A noise-only preset touches no radio setting, so it needs no radio;
            // a chain preset does, as it always has.
            if (preset != null && (_rig != null || !preset.HoldsTransmitChain))
            {
                // ApplyTo reports what could not be applied faithfully — an
                // EQ the radio has not reported, a preset tuned for a
                // different input (#51). Those ride the load announcement. A
                // noise-only preset never applies a transmit chain of defaults.
                string note = preset.HoldsTransmitChain && _rig != null ? preset.ApplyTo(_rig) : "";
                // The noise sections a preset holds (Sprint 49 Track H2 repair
                // round 1, item 6). A preset made before they existed has none,
                // and loads exactly as it always did.
                string noise = NoiseSource?.Invoke()?.ApplyNoiseSectionsForWorkshop(preset, "audio workshop") ?? "";
                PollTxAudio();
                ScreenReaderOutput.Speak(
                    Lexicon.Get("audio.preset.loaded", ("preset", preset.Name)) +
                    (string.IsNullOrEmpty(note) ? "" : ". " + note) +
                    (string.IsNullOrEmpty(noise) ? "" : ". " + noise),
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
        var inputDialog = new JJFlexDialog { Title = "Save Audio Preset", Width = 350, SizeToContent = SizeToContent.Height };
        inputDialog.ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };

        var prompt = new TextBlock { Text = "Enter a name for this preset:", Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(prompt, "Enter a name for this preset");
        panel.Children.Add(prompt);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(nameBox, "Preset name");
        panel.Children.Add(nameBox);

        // Part five: the Workshop's save holds all three sections, each
        // pre-ticked (Sprint 49 Track H2 repair round 1, item 6). The words are
        // the noise save dialog's own.
        CheckBox Section(string key)
        {
            var box = new CheckBox { Content = Lexicon.Get(key), IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetName(box, Lexicon.Get(key));
            panel.Children.Add(box);
            return box;
        }
        var includeNoise = Section("audio.noise_presets.include_noise");
        var includeTxNoise = Section("audio.noise_presets.include_tx_noise");
        var includeChain = Section("audio.noise_presets.include_chain");

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
            bool chain = includeChain.IsChecked == true, rx = includeNoise.IsChecked == true, tx = includeTxNoise.IsChecked == true;
            var noise = NoiseSource?.Invoke();
            if (!chain)
            {
                // Noise sections only: the noise presets' own store, so an older
                // build can never apply it as a transmit chain of defaults.
                if ((!rx && !tx) || noise == null)
                {
                    ScreenReaderOutput.Speak(Lexicon.Get("audio.noise_presets.need_section"), VerbosityLevel.Terse);
                    return;
                }
                // Said once this name dialog has closed and focus is back in the
                // Workshop (Sprint 49 Track H2 bench round, item 2): said at the
                // close, it was cut at its first word.
                using (var answer = noise.HoldAnswer())
                {
                    answer.AfterTheWindowCloses();
                    noise.SavePreset(name, rx, false, tx, PresetCollision.KeepBoth, "audio workshop");
                }
                inputDialog.Close();
                return;
            }
            var preset = AudioChainPreset.CaptureFrom(_rig, name, ReadSavedPcInputName());
            WithNoiseSections(preset, rx, tx);
            var presets = GetPresetsCallback?.Invoke() ?? AudioChainPresets.CreateDefaults();
            presets.Presets.Add(preset);
            // Said once this name dialog has closed and focus is back in the
            // Workshop (bench round, item 2), the same way as the noise-only save.
            bool persisted = PersistPresets(presets);
            string said = persisted
                ? Lexicon.Get("audio.preset.saved", ("preset", name))
                : Lexicon.Get("audio.preset.save_failed", ("preset", name), ("reason", PresetSaveFailed));
            var level = persisted ? VerbosityLevel.Terse : VerbosityLevel.Critical;
            WindowSettledSpeech.After(Dispatcher, () => ScreenReaderOutput.Speak(said, Radios.Speech.SpeechIntent.Queue, level));
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
            // An export carries the noise sections too, so a shared preset is
            // the whole picture (Sprint 49 Track H2 repair round 1, item 6).
            WithNoiseSections(preset, receive: true, transmit: true);
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
    /// What the Workshop's Load lists: the chain presets in their saved order,
    /// then the noise-only presets (receive, transmit or both, with no transmit
    /// chain), which Save puts in their own list so an older build never sees
    /// them. Old chain presets are listed exactly as before, first.
    /// </summary>
    internal static List<AudioChainPreset> WorkshopPresetList(AudioChainPresets presets) =>
        presets.Presets.Concat(presets.NoisePresets).ToList();

    /// <summary>The app's noise controller, for the noise sections of a preset (Sprint 49 Track H2 repair round 1, item 6). Set by MainWindow.</summary>
    public static Func<NoiseController?>? NoiseSource { get; set; }

    /// <summary>Add the receive and transmit noise sections, as the noise controller captures them, to a preset the Workshop made.</summary>
    private static void WithNoiseSections(AudioChainPreset preset, bool receive, bool transmit)
    {
        if (NoiseSource?.Invoke() is not { } noise) return;
        preset.SchemaVersion = AudioChainPreset.CurrentSchemaVersion;
        if (receive) preset.ReceiveNoise = noise.CaptureReceiveNoise();
        if (transmit) preset.TransmitNoise = noise.CaptureTransmitSetup(preset.Name);
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
