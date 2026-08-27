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
    /// <para><b>"Don't ask me this again", added in Sprint 36 (#267) and
    /// deliberately not before.</b> This dialog shipped without one, on the
    /// reasoning that a suppression key here would switch off the only thing
    /// telling an operator his instrumentation is still on — which is the
    /// entire defect the prompt exists to fix. That reasoning was sound while
    /// suppression was a ONE-WAY door: the store could silence a message and
    /// nothing anywhere could bring it back, so the checkbox would have been a
    /// permanent, unrecoverable loss of the report.
    ///
    /// Noel overruled the omission, and the sequencing is what makes it safe.
    /// Settings → Notifications → Messages You Have Silenced now lists what has
    /// been silenced, by name and date, and restores any of it. The checkbox
    /// went in only after that existed. This is exactly the message somebody
    /// silences in a hurry on the way out of the shack and wants back a month
    /// later, when a meter capture has been quietly filling the disk since —
    /// so it needed the way back more than most, not less.</para>
    ///
    /// <para><b>Honoured on "Close anyway" only.</b> Same rule
    /// ConfirmActionDialog applies to Yes, and for a sharper reason here.
    /// "Turn these off and close" with the box ticked would be a standing
    /// instruction to change the operator's persisted settings at every future
    /// exit without saying so — a much larger promise than "stop asking", and
    /// not one a checkbox on an exit prompt should be able to make. "Stay
    /// open" with the box ticked is not an answer at all. So the box means
    /// what it says: stop asking, and close.</para>
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

        public StillRunningDialog(IReadOnlyList<RunningCostReading> readings)
        {
            if (readings == null) throw new ArgumentNullException(nameof(readings));

            InitializeComponent();

            Title = Lexicon.Get("logging.running.exit_title");
            AutomationProperties.SetName(this, Title);
            StopButton.Content = Lexicon.Get("logging.running.stop_and_close");
            CloseButton.Content = Lexicon.Get("logging.running.close_anyway");
            StayButton.Content = Lexicon.Get("logging.running.stay_open");
            DontAskAgainCheck.Content = Lexicon.Get("logging.running.dont_ask_again");
            AutomationProperties.SetName(DontAskAgainCheck,
                Lexicon.Get("logging.running.dont_ask_again").Replace("_", ""));
            // What this checkbox does HERE, then the one sentence that owns
            // where suppression is undone. Every dialog offering the checkbox
            // reads that second sentence from the same key, so if the surface
            // ever moves there is one string to change rather than three.
            JJFlexHelp.SetText(DontAskAgainCheck,
                Lexicon.Get("logging.running.dont_ask_again_help") + " " +
                Lexicon.Get("settings.silenced.reversible_help"));

            var body = new StringBuilder(Lexicon.Get("logging.running.exit_intro"));
            bool anyStoppable = false;
            foreach (RunningCostReading r in readings)
            {
                if (r.CanStop) anyStoppable = true;

                // One line each, and each line is a whole sentence. A reader
                // arrowing down this box gets one complete fact per press —
                // which is the only reading mode that works when the list is
                // the reason the dialog opened.
                //
                // No separate "some of these will still be on next time"
                // paragraph: every line that persists says so itself, and a
                // summary of a two-item list is a second thing to read that
                // adds nothing to the first.
                body.AppendLine();
                body.AppendLine();
                body.Append(r.Sentence());
                if (!string.IsNullOrWhiteSpace(r.StopHow))
                    body.Append(' ').Append(Lexicon.Get("logging.running.threshold_stop", ("how", r.StopHow!)));
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
            // Only here, and only on this button — see the class remarks. The
            // key is versioned, so re-wording what this prompt says brings it
            // back for everyone who silenced the older wording.
            if (DontAskAgainCheck.IsChecked == true)
                AdvisorySuppression.Suppress(AdvisoryKeys.StillRunningAtExit);

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
