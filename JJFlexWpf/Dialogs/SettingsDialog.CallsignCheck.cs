using System.Windows.Automation;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings, License, "Require a callsign to transmit" (#1306): the one
    /// computer-wide switch, kept in the root audio settings.
    /// </summary>
    /// <remarks>
    /// <para><b>The newer choice wins.</b> Settings is not the only writer:
    /// Check your audio's "Turn off the callsign check" button turns it off too,
    /// and that window can be opened FROM Settings (Audio, Audio Workshop, Check
    /// your audio) while Settings stays open behind it. Settings' checkbox was
    /// filled when Settings opened, so at OK it still said "on", and OK used to
    /// copy it into the shared settings, where <see cref="AudioOutputConfig.Apply"/>
    /// made it live again and <see cref="AudioOutputConfig.PersistUserScope"/>
    /// wrote it back to the root file: the button's choice was silently undone.</para>
    /// <para>So Settings remembers what it loaded. At OK, a checkbox the operator
    /// did not change says nothing new, and the switch keeps whatever it is NOW
    /// (<see cref="FlexBase.CallsignCheckOn"/>, the live value every writer
    /// sets); a checkbox the operator did change is the newest choice, and wins.
    /// The resolved value is written into <c>_audioConfig</c>, which is the one
    /// object Apply makes live and SettingsApplied persists, so all three get it.</para>
    /// <para>Chosen over making the open checkbox follow the change, which would
    /// need every writer to find and update an open Settings window, and would
    /// move a control under the operator while they might be on it.</para>
    /// <para><b>"What it loaded" means the last commit, not the opening
    /// (repair 2, Codex's recheck).</b> Apply commits and keeps Settings open, so
    /// a baseline taken only at opening goes stale at the first Apply: a later
    /// change back to the opening value read as "unchanged" and was lost, and a
    /// box left at its applied value read as "changed" forever and undid a later
    /// turn-off from the button. So once a commit has really happened (after
    /// SettingsApplied, in ApplyAllSettings, for Apply and OK alike), the
    /// baseline becomes the committed value and the checkbox shows it: the
    /// operator has just asked for a commit, so showing what was committed is
    /// the answer to that, not a control moving on its own.</para>
    /// </remarks>
    public partial class SettingsDialog
    {
        /// <summary>The value last committed: at opening, then after each successful Apply or OK.</summary>
        private bool _callsignCheckLoaded;

        /// <summary>Fill the checkbox, its name and its Ctrl+F1 text, and remember what it opened on.</summary>
        private void LoadCallsignCheck()
        {
            // DRAFT words (Lexicon).
            RequireCallsignCheckbox.Content = Lexicon.Get("settings.license.require_callsign_label");
            AutomationProperties.SetName(RequireCallsignCheckbox,
                Lexicon.Get("settings.license.require_callsign_label"));
            JJFlexHelp.SetText(RequireCallsignCheckbox, Lexicon.Get("settings.license.require_callsign_help"));
            RequireCallsignCheckbox.IsChecked = _audioConfig.RequireCallsignToTransmit;
            _callsignCheckLoaded = _audioConfig.RequireCallsignToTransmit;
        }

        /// <summary>
        /// At Apply and OK: into the audio settings, made live by <c>_audioConfig.Apply()</c>
        /// and kept in the root file by PersistUserScope, as the newer choice.
        /// </summary>
        private void SaveCallsignCheck()
        {
            _audioConfig.RequireCallsignToTransmit = NewerCallsignCheck(
                _callsignCheckLoaded, RequireCallsignCheckbox.IsChecked == true, FlexBase.CallsignCheckOn);
        }

        /// <summary>
        /// After a commit that really happened (ApplyAllSettings, once
        /// SettingsApplied has persisted it): the committed value becomes the
        /// baseline, and the checkbox shows it.
        /// </summary>
        private void CallsignCheckCommitted()
        {
            _callsignCheckLoaded = _audioConfig.RequireCallsignToTransmit;
            RequireCallsignCheckbox.IsChecked = _callsignCheckLoaded;
        }

        /// <summary>
        /// The checkbox when the operator changed it since the last commit;
        /// otherwise the switch as it stands now, so a change made elsewhere
        /// while Settings was open is not undone.
        /// </summary>
        internal static bool NewerCallsignCheck(bool loaded, bool ticked, bool liveNow) =>
            ticked != loaded ? ticked : liveNow;
    }
}
