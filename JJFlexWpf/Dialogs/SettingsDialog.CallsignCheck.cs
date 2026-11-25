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
    /// </remarks>
    public partial class SettingsDialog
    {
        /// <summary>The checkbox's value when Settings opened.</summary>
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
        /// At OK: into the audio settings, made live by <c>_audioConfig.Apply()</c>
        /// and kept in the root file by PersistUserScope, as the newer choice.
        /// </summary>
        private void SaveCallsignCheck()
        {
            _audioConfig.RequireCallsignToTransmit = NewerCallsignCheck(
                _callsignCheckLoaded, RequireCallsignCheckbox.IsChecked == true, FlexBase.CallsignCheckOn);
        }

        /// <summary>
        /// The checkbox when the operator changed it in this Settings session;
        /// otherwise the switch as it stands now, so a change made elsewhere
        /// while Settings was open is not undone.
        /// </summary>
        internal static bool NewerCallsignCheck(bool loaded, bool ticked, bool liveNow) =>
            ticked != loaded ? ticked : liveNow;
    }
}
