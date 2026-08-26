using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>What the operator answered at the exit boundary.</summary>
    public enum StillRunningChoice
    {
        /// <summary>Escape, Stay open, or the window closed by other means.</summary>
        StayOpen = 0,

        /// <summary>Close, leaving everything exactly as it is.</summary>
        CloseAnyway = 1,

        /// <summary>Stop what can be stopped, then close.</summary>
        StopThenClose = 2
    }

    /// <summary>
    /// The exit read of the running-cost register (#253) — Noel's ruled
    /// priority boundary.
    /// </summary>
    /// <remarks>
    /// <para><b>Why exit, of all the boundaries.</b> Because it is the boundary
    /// where a persisted switch stops being this session's problem and becomes
    /// the NEXT session's. Meter-stream recording was on across the 2026-08-25
    /// session and nothing said so; it would have been on again the next
    /// morning. Exit is the last moment anyone can be told.</para>
    ///
    /// <para><b>Why it is not a ConfirmActionDialog.</b> That dialog answers a
    /// yes-or-no question, and this question has three answers. Dropping "turn
    /// these off" would leave a prompt that reminds the operator of something
    /// and then offers no way to act on it — the friction tax this project
    /// exists to refuse. Everything else about it follows the same house
    /// pattern: one caret-readable body, no default button, Escape cancels.</para>
    ///
    /// <para><b>No "don't show this again".</b> ConfirmActionDialog offers one
    /// and it is right there for the taking, but a suppression key on THIS
    /// prompt would switch off the only thing that tells an operator their
    /// instrumentation is still on — which is the entire defect. Suppression is
    /// for teaching text whose outcome is reported some other re-readable way.
    /// This prompt IS the report.</para>
    ///
    /// <para><b>It only ever appears when something Notable is running.</b> The
    /// always-on diagnostic log and the audible meter tones are Routine and
    /// never raise it, so a normal exit is still a silent exit. A prompt every
    /// operator sees every time is a prompt every operator learns to dismiss
    /// without reading, which would destroy the one channel this feature
    /// has.</para>
    /// </remarks>
    public partial class StillRunningDialog : JJFlexDialog
    {
        /// <summary>What the operator chose. Defaults to the safe answer.</summary>
        public StillRunningChoice Choice { get; private set; } = StillRunningChoice.StayOpen;

        /// <summary>
        /// True when at least one of the listed things survives a restart, so
        /// the caller knows whether the persistence note was worth printing.
        /// </summary>
        public bool AnySurvivesRestart { get; }

        public StillRunningDialog(IReadOnlyList<RunningCostReading> readings)
        {
            if (readings == null) throw new ArgumentNullException(nameof(readings));

            InitializeComponent();

            Title = Lexicon.Get("logging.running.exit_title");
            AutomationProperties.SetName(this, Title);
            StopButton.Content = Lexicon.Get("logging.running.stop_and_close");
            CloseButton.Content = Lexicon.Get("logging.running.close_anyway");
            StayButton.Content = Lexicon.Get("logging.running.stay_open");

            var body = new StringBuilder(Lexicon.Get("logging.running.exit_intro"));
            bool anyPersist = false;
            bool anyStoppable = false;
            foreach (RunningCostReading r in readings)
            {
                if (r.SurvivesRestart) anyPersist = true;
                if (r.CanStop) anyStoppable = true;

                // One line each, and each line is a whole sentence. A reader
                // arrowing down this box gets one complete fact per press —
                // which is the only reading mode that works when the list is
                // the reason the dialog opened.
                body.AppendLine();
                body.AppendLine();
                body.Append(r.Sentence());
                if (!string.IsNullOrWhiteSpace(r.StopHow))
                    body.Append(' ').Append(Lexicon.Get("logging.running.threshold_stop", ("how", r.StopHow!)));
            }

            AnySurvivesRestart = anyPersist;
            if (anyPersist)
            {
                body.AppendLine();
                body.AppendLine();
                body.Append(Lexicon.Get("logging.running.exit_persist"));
            }

            body.AppendLine();
            body.AppendLine();
            body.Append(Lexicon.Get("logging.running.exit_question"));

            BodyText.Text = ScreenReaderText.NormalizeLineBreaks(body.ToString());

            // Nothing here can be turned off from this dialog — every listed
            // thing is a report rather than a switch — so the button that
            // promises to turn them off would be a button that does nothing.
            // Hide it rather than let it lie.
            if (!anyStoppable)
                StopButton.Visibility = Visibility.Collapsed;

            Loaded += (s, e) =>
            {
                BodyText.CaretIndex = 0;
                BodyText.Focus();
            };
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = StillRunningChoice.StopThenClose;
            CloseWithResult(true);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = StillRunningChoice.CloseAnyway;
            CloseWithResult(true);
        }

        private void StayButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = StillRunningChoice.StayOpen;
            CloseWithResult(false);
        }
    }
}
