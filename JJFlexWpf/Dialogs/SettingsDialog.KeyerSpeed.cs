using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Tuning → Keyer speed, words per minute (#879).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruled by Noel 2026-11-09 20:36 (a Settings field, labelled "Keyer speed,
    /// words per minute"; the quick key and its layer come with the CW sprint)
    /// and 2026-11-11 11:44 (an operator-wide tab, not the Radios tab). The
    /// Tuning tab is the orchestrator's choice: the Audio tab gains controls
    /// from code elsewhere. Kept distinct from the Audio tab's CW notification
    /// speed, which is the app's own Morse, not the radio's keyer.
    /// </para>
    /// <para>
    /// <b>A setting is an intent</b>: it works with no radio, saving into the
    /// operator's own keyer file for the next connect to their own radio;
    /// while connected it goes through the radio layer, which writes the radio
    /// only when it is the operator's own (#686). See <see cref="KeyerSpeedStore"/>.
    /// Committed only when the operator changed the number, so opening and
    /// closing Settings never writes a keyer.
    /// </para>
    /// </remarks>
    public partial class SettingsDialog
    {
        /// <summary>The speed the field showed, to tell an edit from no edit.</summary>
        private int _keyerSpeedAtOpen;

        /// <summary>Label, name and help, and the saved speed. Called once, from the constructor.</summary>
        private void InitializeKeyerSpeed()
        {
            string label = Lexicon.Get("settings.keyer.speed_label");
            KeyerSpeedLabel.Text = label + ":";
            AutomationProperties.SetName(KeyerSpeedBox, label);
            // DRAFT PROSE (#879): Codex words it.
            JJFlexHelp.SetText(KeyerSpeedBox, Lexicon.Get("settings.keyer.speed_help",
                ("min", KeyerSpeedStore.MinWpm), ("max", KeyerSpeedStore.MaxWpm)));
            RefreshKeyerSpeedFromRig();
        }

        /// <summary>
        /// The speed to show: the connected rig's, or the saved file's. Called
        /// again when the rig is handed in after construction.
        /// </summary>
        private void RefreshKeyerSpeedFromRig()
        {
            if (KeyerSpeedBox == null) return;
            _keyerSpeedAtOpen = KeyerSpeedStore.Read(Rig, KeyerOperatorDirectory());
            KeyerSpeedBox.Text = _keyerSpeedAtOpen.ToString();
        }

        /// <summary>
        /// The operator's own folder for the keyer file: the one the
        /// application wires at startup, so it answers with no radio, or else
        /// the folder the connect handed the dialog.
        /// </summary>
        private string? KeyerOperatorDirectory()
        {
            string? dir = null;
            try { dir = KeyerSpeedStore.OperatorDirectory?.Invoke(); } catch { }
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(ConfigDirectory) && !string.IsNullOrEmpty(OperatorName))
                dir = Path.Combine(ConfigDirectory, OperatorName);
            return dir;
        }

        /// <summary>
        /// Validation, before anything is written: a whole number in the
        /// radio's range. False with the Tuning tab selected and the field
        /// focused, as every other field here does.
        /// </summary>
        private bool ValidateKeyerSpeed()
        {
            if (int.TryParse(KeyerSpeedBox.Text, out int wpm)
                && wpm >= KeyerSpeedStore.MinWpm && wpm <= KeyerSpeedStore.MaxWpm)
                return true;
            // DRAFT PROSE (#879): Codex words it.
            MessageBox.Show(Lexicon.Get("settings.keyer.speed_out_of_range",
                    ("min", KeyerSpeedStore.MinWpm), ("max", KeyerSpeedStore.MaxWpm)),
                Lexicon.Get("settings.dialog_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            SelectTabByHeader("Tuning");  // Track C papercut: focusing a field on an unselected tab fails silently
            KeyerSpeedBox.Focus();
            return false;
        }

        /// <summary>
        /// Commit the speed when the operator changed it, and report a change
        /// that cannot reach a radio now the way every queued intent here is
        /// reported. Nothing is said when it reached the operator's own radio.
        /// </summary>
        private void CommitKeyerSpeedIfChanged(List<string> queued, List<string> applied)
        {
            // Validated by SaveSettings before anything was written.
            if (!int.TryParse(KeyerSpeedBox.Text, out int wpm) || wpm == _keyerSpeedAtOpen) return;
            switch (KeyerSpeedStore.Commit(Rig, KeyerOperatorDirectory(), wpm))
            {
                case KeyerSpeedStore.Outcome.WrittenToRadio:
                    break;
                case KeyerSpeedStore.Outcome.SavedForNextConnect:
                    // DRAFT PROSE (#879).
                    queued.Add(Lexicon.Get("settings.keyer.speed_waits_for_radio"));
                    break;
                case KeyerSpeedStore.Outcome.SavedNotThisRadio:
                    // DRAFT PROSE (#879, #686): the radio was left alone.
                    queued.Add(Lexicon.Get("settings.keyer.speed_not_this_radio"));
                    break;
                default:
                    // DRAFT PROSE (#879).
                    applied.Add(Lexicon.Get("settings.keyer.speed_not_saved"));
                    return;
            }
            _keyerSpeedAtOpen = wpm;
        }
    }
}
