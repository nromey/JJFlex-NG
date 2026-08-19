using System;
using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The path-learning controls on the Network tab (task #102).
    ///
    /// <para>Task #79 shipped the learning with a hardwired threshold of three
    /// and no way to turn it off or take it back. Three surfaces here: how much
    /// evidence it takes, whether it happens at all, and a way to make JJ
    /// Flexible forget what it worked out.</para>
    ///
    /// <para><b>What "forget" clears, stated once so every message can be
    /// honest about it:</b> the per-radio connection history ring, and that is
    /// the whole of it — because a learned path is not stored anywhere. It is
    /// derived from that ring every time it is asked for. "Clear the learned
    /// path but keep the history" is not a smaller option that we declined to
    /// offer; it is not a thing that can exist. The cost is real and gets named
    /// in the confirmation: the ring is also the record of how long connects
    /// have been taking, which is a support tool in its own right.</para>
    ///
    /// <para>These commit LIVE rather than waiting for OK/Apply, deliberately
    /// and unlike the rest of this dialog. Forget is an irreversible action
    /// behind its own confirmation — there is nothing coherent for Cancel to
    /// undo once the files are gone — and leaving the two switches queued while
    /// the button beside them acts immediately would be the worst of both.
    /// Every one of them says out loud that it took effect.</para>
    /// </summary>
    public partial class SettingsDialog
    {
        /// <summary>The thresholds offered, in combo order. The ceiling is a
        /// property of the store — see ConnectPathLearningConfig.MaxThreshold,
        /// which explains why five and not six.</summary>
        private static readonly int[] PathLearningThresholds = { 3, 4, 5 };

        private bool _suppressPathLearningEvents;

        /// <summary>
        /// Fill the controls from the saved setting. Called from LoadSettings,
        /// with events suppressed so opening Settings does not announce a
        /// change nobody made.
        /// </summary>
        private void LoadPathLearningSettings()
        {
            var cfg = ConnectPathLearningConfig.Current;

            _suppressPathLearningEvents = true;
            try
            {
                PathLearningThresholdCombo.Items.Clear();
                foreach (int n in PathLearningThresholds)
                    PathLearningThresholdCombo.Items.Add(n.ToString());

                int idx = Array.IndexOf(PathLearningThresholds, cfg.TrendThreshold);
                PathLearningThresholdCombo.SelectedIndex = idx >= 0 ? idx : 0;
                PathLearningEnabledCheck.IsChecked = cfg.LearnFromHistory;
            }
            finally
            {
                _suppressPathLearningEvents = false;
            }

            SyncPathLearningAffordances(cfg);
        }

        /// <summary>
        /// Keep the threshold control's availability and the status line
        /// truthful about the current state — <b>including the off state</b>,
        /// which is the one most easily left unsaid. A disabled control drops
        /// out of the tab order in WPF, which is the house rule, so the status
        /// line is what tells a keyboard user why the stop they expected is not
        /// there.
        /// </summary>
        private void SyncPathLearningAffordances(ConnectPathLearningConfig cfg)
        {
            PathLearningThresholdPanel.IsEnabled = cfg.LearnFromHistory;
            PathLearningStatusText.Text = cfg.LearnFromHistory
                ? $"On. A radio has to connect the same way {cfg.TrendThreshold} times running "
                  + "before JJ Flexible tries that way first, and only for radios you have not "
                  + "already chosen a path for."
                : "Off. Nothing is prefilled from history, and the number of connects is not "
                  + "used, so it is switched off above. Connection paths you chose yourself are "
                  + "unaffected; radios without a choice try the local network first.";
        }

        /// <summary>Save and report. Returns the setting actually in force,
        /// which on a declined write is still the operator's choice — applied
        /// in memory, honest about not surviving a restart.</summary>
        private void CommitPathLearning(ConnectPathLearningConfig cfg, string what)
        {
            // RadioConfig's root, NOT this dialog's ConfigDirectory property.
            // ConnectPathLearningConfig.Current reads from the former, and a
            // save that targets a different directory reports success while
            // landing somewhere nothing reads — the worst kind of failure.
            var dir = RadioConfig.ResolvedBaseDirectory;
            bool saved = !string.IsNullOrEmpty(dir) && cfg.Save(dir!);
            if (!saved)
            {
                // Apply it anyway. Refusing an intent because a file was locked
                // hands our problem to the operator; what we owe them is the
                // truth about how long it will last.
                ConnectPathLearningConfig.Invalidate();
                ScreenReaderOutput.Speak(
                    what + " This could not be written to disk, so it may not be here next "
                    + "time you start. Your trace file has the reason.",
                    VerbosityLevel.Terse, interrupt: true);
            }
            else
            {
                ScreenReaderOutput.Speak(what, VerbosityLevel.Terse, interrupt: true);
            }

            SyncPathLearningAffordances(cfg);
        }

        private void PathLearningEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressPathLearningEvents) return;

            var cfg = ConnectPathLearningConfig.Current;
            bool on = PathLearningEnabledCheck.IsChecked == true;
            if (cfg.LearnFromHistory == on) return;
            cfg.LearnFromHistory = on;

            CommitPathLearning(cfg, on
                ? $"Learning the connection path is on, after {cfg.TrendThreshold} connects in a row the same way."
                : "Learning the connection path is off. Nothing will be prefilled from history. "
                  + "Paths you chose yourself are unaffected.");
        }

        private void PathLearningThresholdCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPathLearningEvents) return;

            int idx = PathLearningThresholdCombo.SelectedIndex;
            if (idx < 0 || idx >= PathLearningThresholds.Length) return;
            int wanted = PathLearningThresholds[idx];

            var cfg = ConnectPathLearningConfig.Current;
            if (cfg.TrendThreshold == wanted) return;
            cfg.TrendThreshold = wanted;

            // The combo item is a bare number, so the screen reader's own
            // reading of it says "4" and nothing else — this is the case where
            // an utterance is carrying something the UI genuinely does not.
            // Contrast the connection-path combo (#107), where the item text
            // says everything and the extra sentence was pure repetition.
            CommitPathLearning(cfg,
                $"{wanted} connects in a row before JJ Flexible tries a radio's usual path first.");
        }

        /// <summary>
        /// Forget every radio's learned path — which means clearing every
        /// radio's connection history ring, because that ring IS the learned
        /// path one derivation later. Confirmed first, and the confirmation
        /// names what else goes with it.
        /// </summary>
        private void PathLearningForgetButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new ConfirmActionDialog(
                "Forget What Has Been Learned",
                "JJ Flexible works out a radio's usual connection path by reading that radio's "
                + "own connection history. There is nowhere else it is written down, so forgetting "
                + "what was learned means clearing that history.",
                new[]
                {
                    "Every radio this computer knows loses its record of the last ten connection "
                    + "attempts: which way each one went, whether it worked, and how long it took.",
                    "That record is also what answers 'how long is my connect actually taking', "
                    + "so a support conversation about a slow connection starts from nothing again.",
                    "Connection paths you chose yourself are NOT touched. Only what JJ Flexible "
                    + "worked out on its own goes away.",
                    "It starts learning again from the next connection.",
                },
                question: "Clear the connection history for every radio?",
                yesLabel: "_Forget it",
                noLabel: "_Keep it")
            {
                Owner = this,
            };

            if (confirm.ShowDialog() != true)
            {
                ScreenReaderOutput.Speak("Nothing was cleared.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var (cleared, failed) = ConnectionHistory.ClearAll();

            string result =
                cleared == 0 && failed == 0
                    ? "There was no connection history to clear."
                : failed > 0
                    ? $"Cleared the connection history for {cleared} radio{(cleared == 1 ? "" : "s")}. "
                      + $"{failed} could not be cleared — your trace file has the reason, and "
                      + "those radios may still follow their old habit."
                    : $"Cleared the connection history for {cleared} radio{(cleared == 1 ? "" : "s")}. "
                      + "Learning starts again from the next connection.";

            PathLearningStatusText.Text = result;
            ScreenReaderOutput.Speak(result, VerbosityLevel.Terse, interrupt: true);

            // Focus back on the button the operator pressed. A confirmation
            // dialog closing leaves WPF to guess, and its guess is the top of
            // the tab order.
            PathLearningForgetButton.Focus();
        }
    }
}
