using System;
using System.Diagnostics;
using System.Windows.Controls;
using JJTrace;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Notifications: the three pickers that decide how the
    /// application SOUNDS rather than when it makes a noise.
    ///
    /// Sprint 33 Track F. Two of them (#145 the CW keying tone shape, #147 the
    /// alert tone set) are literally the same question asked about two
    /// different sound families — how rich should a sound be — so they are
    /// worded to rhyme: one is a "tone set", the other a "tone shape", and
    /// "Sine" in the CW picker means the same thing "Classic" means in the
    /// alert picker. An operator who learns one has learned the other.
    ///
    /// <para>
    /// <b>Every one of them previews on selection change, and that is the
    /// feature.</b> A blind operator cannot audition a sound by reading
    /// "Sawtooth". Arrowing a WPF combo moves the selection with each key, so
    /// arrowing this one plays each option in turn — which is exactly how a
    /// sound is chosen. It also means populating the combos has to be silent,
    /// hence <see cref="_suppressSoundPreviews"/>: a Settings dialog that
    /// played three sounds on the way open would be a bug, and an intrusive one
    /// for someone who opened it while listening to a band.
    /// </para>
    /// <para>
    /// The CW previews queue behind any real CW rather than interrupting it —
    /// see MainWindow.PreviewCwTone. Auditioning tone shapes during a connect
    /// must not be able to swallow the connect prosigns.
    /// </para>
    /// </summary>
    public partial class SettingsDialog
    {
        /// <summary>
        /// True while the sound pickers are being populated, so filling a combo
        /// does not play anything. Set and cleared inside the load method that
        /// populates them.
        /// </summary>
        private bool _suppressSoundPreviews;

        /// <summary>
        /// #147. Applies the choice LIVE, before OK, because that is the only
        /// way the preview can be truthful: the sampler renders whatever set is
        /// active, so the set has to already be the one being auditioned.
        ///
        /// Applying live and then cancelling would leave the app on the
        /// auditioned set, so Cancel restores the value the dialog opened with —
        /// see <see cref="RestoreAlertToneSetOnCancel"/>.
        /// </summary>
        private void AlertToneSetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSoundPreviews) return;
            try
            {
                ArmAlertToneSetRestore();
                EarconVoices.ActiveSet = AlertToneSetCombo.SelectedIndex == 1
                    ? EarconVoiceSet.Classic
                    : EarconVoiceSet.Modern;
                EarconPlayer.VoiceSetSampler();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"AlertToneSetCombo preview failed: {ex.Message}", TraceLevel.Warning);
            }
        }

        private bool _alertToneSetRestoreArmed;

        /// <summary>
        /// Arrange for the live tone set to go back to the last COMMITTED value
        /// if the dialog closes any way other than OK.
        ///
        /// Armed lazily, the first time the picker actually changes the live
        /// setting, so a dialog that never touched it registers nothing. It has
        /// to cover Escape and the title-bar close as well as the Cancel
        /// button, which is why it hangs off Closed rather than off the button
        /// handler: JJFlexDialog makes every dialog Escape-closable, and a
        /// setting that survives Escape but not Cancel would be the sort of
        /// difference nobody discovers until it has been wrong for a month.
        ///
        /// It reads the config AT CLOSE rather than capturing the opening value
        /// when armed, and that difference is Apply. Apply commits the combo
        /// into the config and leaves the dialog open; cancelling after that
        /// must leave the applied value standing, not roll back to whatever was
        /// showing when the dialog opened.
        /// </summary>
        private void ArmAlertToneSetRestore()
        {
            if (_alertToneSetRestoreArmed) return;
            _alertToneSetRestoreArmed = true;

            Closed += (_, _) =>
            {
                if (DialogResult == true) return;
                try
                {
                    int committed = _audioConfig?.EarconVoiceSet ?? (int)EarconVoiceSet.Modern;
                    EarconVoices.ActiveSet = committed == (int)EarconVoiceSet.Classic
                        ? EarconVoiceSet.Classic
                        : EarconVoiceSet.Modern;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"Alert tone set restore failed: {ex.Message}", TraceLevel.Warning);
                }
            };
        }

        /// <summary>#146 — follow the radio's sidetone, or use the configured tone.</summary>
        private void CwPitchSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSoundPreviews) return;
            PlayCwPreview();
        }

        /// <summary>#145 — the keying tone shape.</summary>
        private void CwWaveformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSoundPreviews) return;
            PlayCwPreview();
        }

        /// <summary>
        /// Sample the CW settings as they currently READ IN THE DIALOG, not as
        /// they are saved. Pitch and speed come out of the text boxes rather
        /// than out of the config, so an operator who has just typed a new
        /// sidetone frequency hears that one.
        ///
        /// Unparseable text falls back to the saved value rather than refusing
        /// to make a sound. Someone mid-edit with "7" in the box should still
        /// hear the waveform they just arrowed onto.
        /// </summary>
        private void PlayCwPreview()
        {
            try
            {
                int hz = _audioConfig?.CwSidetoneHz ?? 700;
                if (int.TryParse(CwSidetoneBox.Text, out int typedHz) && typedHz >= 400 && typedHz <= 1200)
                    hz = typedHz;

                int wpm = _audioConfig?.CwSpeedWpm ?? 20;
                if (int.TryParse(CwSpeedBox.Text, out int typedWpm) && typedWpm >= 10 && typedWpm <= 60)
                    wpm = typedWpm;

                int idx = CwWaveformCombo.SelectedIndex;
                string id = idx >= 0 && idx < EarconVoices.CwWaveforms.Count
                    ? EarconVoices.CwWaveforms[idx].Id
                    : EarconVoices.DefaultCwWaveformId;

                MainWindow.PreviewCwTone(id, CwPitchSourceCombo.SelectedIndex == 1, hz, wpm);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"PlayCwPreview failed: {ex.Message}", TraceLevel.Warning);
            }
        }
    }
}
