using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → System-wide keys (#307, Sprint 48 Track E). Hosts
    /// <see cref="SystemWideKeysPanel"/>, which holds the two switches, the
    /// three chord pickers and the four-layer conflict report; this partial
    /// only owns when the panel is built, how its key capture gets first
    /// refusal on a key, and when it commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why here and not a dialog of its own.</b> Noel, 2026-10-05: <i>"I'd
    /// have the ptt toggle stuff in a settings tab somewhere, no need for two
    /// nested dialogs do you?"</i> The code gave a sharper reason than
    /// nesting: Track B taught the Keys dialog to LIST these rows while the
    /// only place to CHANGE them was a dialog under Tools that nothing in the
    /// Keys dialog pointed at. The Tools item now deep-links here, as
    /// "Diagnostic Log" does, and the Keys dialog routes here from a
    /// system-wide row — so the keys are configured where they are listed.
    /// </para>
    /// <para>
    /// <b>Commits on OK and Apply</b>, with the rest of Settings, rather than
    /// live: switching a key that is taken from every program on the machine
    /// is a deliberate act and belongs in the deliberate pipeline, like the
    /// reflected-power cut on the PTT tab. Unlike the Diagnostics partial this
    /// one therefore needs one line in <c>ApplyAllSettings</c>.
    /// </para>
    /// <para>
    /// <b>Key capture needs the class handler.</b> <see cref="JJFlexDialog"/>
    /// closes on Escape from an instance <c>PreviewKeyDown</c> handler, and
    /// class handlers run before instance handlers, so the override below is
    /// the only place a capture can keep Escape for itself.
    /// </para>
    /// </remarks>
    public partial class SettingsDialog
    {
        private SystemWideKeysPanel? _systemWideKeysPanel;

        /// <summary>Built on first visit, so Settings opening on another tab pays nothing for it.</summary>
        private void SystemWideKeysPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (_systemWideKeysPanel != null) return;
            _systemWideKeysPanel = new SystemWideKeysPanel();
            SystemWideKeysHost.Content = _systemWideKeysPanel;
        }

        /// <summary>
        /// A capture in progress owns every key, Escape included. Everything
        /// else goes to the dialog as before.
        /// </summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_systemWideKeysPanel?.HandleCaptureKey(e) == true) return;
            base.OnPreviewKeyDown(e);
        }

        /// <summary>
        /// Save and apply the system-wide keys if the tab was visited and
        /// anything on it changed. The panel's one-sentence outcome joins the
        /// list of things Settings says it applied.
        /// </summary>
        private void CommitSystemWideKeysIfDirty(List<string> applied)
        {
            if (_systemWideKeysPanel == null) return;
            string? outcome = _systemWideKeysPanel.Commit();
            if (outcome != null) applied.Add(outcome);
        }
    }
}
