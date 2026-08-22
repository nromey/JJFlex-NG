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
                ? Lexicon.Get("settings.path_learning.status_on",
                    ("threshold", cfg.TrendThreshold))
                : Lexicon.Get("settings.path_learning.status_off");
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
                    Lexicon.Get("settings.path_learning.not_written", ("what", what)),
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

            // #128: immediate-apply toggle answers back (settings are
            // intents — the choice is live now even if the save below
            // fails, and CommitPathLearning reports that failure in words).
            EarconPlayer.ToggleTone(on);
            CommitPathLearning(cfg, on
                ? Lexicon.Get("settings.path_learning.turned_on", ("threshold", cfg.TrendThreshold))
                : Lexicon.Get("settings.path_learning.turned_off"));
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
                Lexicon.Get("settings.path_learning.threshold_set", ("wanted", wanted)));
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
                Lexicon.Get("settings.path_learning.forget_title"),
                Lexicon.Get("settings.path_learning.forget_body"),
                new[]
                {
                    Lexicon.Get("settings.path_learning.forget_warning_history"),
                    Lexicon.Get("settings.path_learning.forget_warning_support"),
                    Lexicon.Get("settings.path_learning.forget_warning_choices_kept"),
                    Lexicon.Get("settings.path_learning.forget_warning_relearns"),
                },
                question: Lexicon.Get("settings.path_learning.forget_question"),
                yesLabel: Lexicon.Get("settings.path_learning.forget_yes"),
                noLabel: Lexicon.Get("settings.path_learning.forget_no"))
            {
                Owner = this,
            };

            if (confirm.ShowDialog() != true)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("settings.path_learning.nothing_cleared"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var (cleared, failed) = ConnectionHistory.ClearAll();

            string plural = cleared == 1 ? string.Empty : Lexicon.Get("settings.plural_s");
            string result =
                cleared == 0 && failed == 0
                    ? Lexicon.Get("settings.path_learning.nothing_to_clear")
                : failed > 0
                    ? Lexicon.Get("settings.path_learning.cleared_with_failures",
                        ("cleared", cleared), ("plural", plural), ("failed", failed))
                    : Lexicon.Get("settings.path_learning.cleared",
                        ("cleared", cleared), ("plural", plural));

            PathLearningStatusText.Text = result;
            ScreenReaderOutput.Speak(result, VerbosityLevel.Terse, interrupt: true);

            // Focus back on the button the operator pressed. A confirmation
            // dialog closing leaves WPF to guess, and its guess is the top of
            // the tab order.
            PathLearningForgetButton.Focus();
        }
    }
}
