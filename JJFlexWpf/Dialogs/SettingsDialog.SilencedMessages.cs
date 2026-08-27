using System.Windows;
using System.Windows.Automation;
using JJTrace;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Notifications → Messages You Have Silenced (task #267).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this section exists at all.</b> Every "Don't show this
    /// again" checkbox in the application used to be a one-way door. The store
    /// behind them offered exactly two operations — is it silenced, silence it
    /// — with no unsuppress, no clear, and no way to ask what had been
    /// silenced. So there was nothing Settings COULD have shown: not a list,
    /// not even a count. One tick removed a message for the life of the
    /// install, and on the confirmation dialog that meant a destructive action
    /// could be made to stop asking, for good, by one keypress.</para>
    ///
    /// <para><b>Why the count is here and the list is one door further in.</b>
    /// A count answers the only question this tab is likely to be asked in
    /// passing — "have I silenced anything?" — and it answers it without
    /// opening anything. Somebody who wants the detail is somebody who came
    /// looking, and the detail is a list that can run to a dozen lines. Putting
    /// it inline would make the tab longer for everyone to serve the rare
    /// visit. The Diagnostics tab already does exactly this with Saved
    /// Diagnostic Logs.</para>
    ///
    /// <para>The section reloads on every Loaded, so the count is right after a
    /// restore and after any advisory silenced elsewhere in this same
    /// session.</para>
    /// </remarks>
    public partial class SettingsDialog
    {
        private void SilencedSection_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshSilencedCount();
        }

        private void RefreshSilencedCount()
        {
            int count;
            try
            {
                count = AdvisorySuppression.Count;
            }
            catch (System.Exception ex)
            {
                // The store failing is not a reason to break the tab. Say
                // nothing rather than a number that might be a lie.
                Tracing.TraceLine("SettingsDialog: could not count silenced messages: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
                SilencedCountText.Text = string.Empty;
                SilencedCountText.Focusable = false;
                ReviewSilencedButton.IsEnabled = false;
                return;
            }

            SilencedCountText.Text =
                count == 0 ? Lexicon.Get("settings.silenced.count_none") :
                count == 1 ? Lexicon.Get("settings.silenced.count_one") :
                Lexicon.Get("settings.silenced.count_many", ("count", count));
            AutomationProperties.SetName(SilencedCountText, SilencedCountText.Text);
            SilencedCountText.Focusable = true;

            // Nothing silenced means nothing to review. A disabled button is
            // skipped by Tab, so the tab order does not carry a control that
            // would open an empty window.
            ReviewSilencedButton.IsEnabled = count > 0;
        }

        private void ReviewSilencedButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SilencedMessagesDialog { Owner = this };
                dlg.ShowDialog();
            }
            catch (System.Exception ex)
            {
                Tracing.TraceLine("SettingsDialog: silenced messages window failed to open: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }

            // Re-read rather than assume. The window may have restored one, all
            // or none of them, and the count on this tab is the only place the
            // answer shows once that window has gone.
            RefreshSilencedCount();
        }
    }
}
