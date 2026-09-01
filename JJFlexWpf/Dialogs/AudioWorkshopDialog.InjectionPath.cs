using System;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, Hear Yourself: getting the radio ready to carry something
/// this computer is sending in place of the microphone — the test tone, or a
/// reference recording (task #458).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> Noel, 2026-09-01: <i>"in the audio workshop having
/// the adding a tone thing and separating the transmission tab from actual
/// tests doesn't make sense — to get a tone or do injected stuff you have to
/// set it and then go to another tab."</i>
/// </para>
/// <para>
/// The trip is real and this is where it came from. Arming an injected source
/// needs three things true at once: PC audio running, the radio's transmit
/// input set to this computer, and a voice mode. The arm boxes live on Hear
/// Yourself; the transmit-input picker lives on Transmit Settings. So an
/// operator ticked the box, was refused, and was sent to another category to
/// change a setting — and a screen-reader operator who navigates away from a
/// control is holding it in memory from that moment, with nothing to glance
/// back at. A tab boundary costs far more without sight than with it.
/// </para>
/// <para>
/// <b>The transmit input is the one of the three that is ours to set, so the
/// arm sets it.</b> The radio's transmit input is a plain radio setting this
/// dialog's own picker already writes directly; it is saved, changed, restored
/// on release, and announced both ways — exactly what the Audio Check beside it
/// already does with the monitor, the dummy load and transmit power. PC audio
/// is NOT set here: turning it on runs a device check that can put a picker on
/// screen, and it carries a remembered per-radio choice, so it belongs to the
/// road that owns those. CW mode is not touched either — changing an operator's
/// operating mode to run a test is not a courtesy.
/// </para>
/// <para>
/// <b>It waits for the radio rather than assuming.</b> Setting the input is
/// queued to the radio and confirmed by the radio's own echo, so the arm holds
/// for up to a second until the radio says the path is clear. Arming on the
/// strength of a request we had not seen answered would be the lying receipt
/// this dialog has been fixed for twice already.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    #region Injection path (task #458)

    /// <summary>The radio's transmit input value that means "this computer".</summary>
    private const string PcMicSource = "PC";

    /// <summary>
    /// The transmit input the operator had before an arm switched it, or empty
    /// when we have changed nothing. One slot is enough: only one thing can
    /// replace the microphone at a time.
    /// </summary>
    private string _injectionSavedMicSource = "";

    /// <summary>
    /// True while an arm is waiting for the radio to confirm the transmit
    /// input. The 2 Hz housekeeping must not touch the arm boxes during that
    /// window.
    /// </summary>
    /// <remarks>
    /// Without this the fix would have broken itself. <c>SyncToneArmUi</c> and
    /// <c>SyncReferenceUi</c> run twice a second and correct an arm box that
    /// disagrees with the engine — which is exactly the state a pending arm is
    /// in: the operator has ticked the box and the tone is not running yet. The
    /// sync would have unticked it, restored the input, and left the operator
    /// looking at a checkbox that had silently undone itself, within half a
    /// second of them ticking it.
    /// </remarks>
    private bool _injectionArmPending;

    /// <summary>
    /// True while an arm is mid-flight, so the periodic arm-box housekeeping
    /// can stand out of the way.
    /// </summary>
    private bool InjectionArmPending => _injectionArmPending;

    /// <summary>How long to wait for the radio to confirm the input change.</summary>
    private static readonly TimeSpan InjectionPathTick = TimeSpan.FromMilliseconds(50);
    private const int InjectionPathTries = 20;   // 20 x 50 ms = one second

    /// <summary>
    /// Make the radio able to carry an injected source, then continue.
    /// </summary>
    /// <param name="rig">The radio. Never null at any call site.</param>
    /// <param name="whenReady">
    /// Run once the path is clear, with a sentence describing anything that was
    /// changed to clear it (empty when nothing was) so the caller can fold it
    /// into its own arm announcement rather than speaking twice.
    /// </param>
    /// <param name="whenRefused">
    /// Run with the reason when the path cannot be cleared from here. The
    /// reason is the radio's own words, unchanged — it already names the
    /// remedy.
    /// </param>
    private void WithInjectionPath(FlexBase rig, Action<string> whenReady, Action<string> whenRefused)
    {
        string trouble = rig.TxTonePathTrouble;
        if (string.IsNullOrEmpty(trouble))
        {
            whenReady("");
            return;
        }

        if (!CanSwitchTransmitInput(rig, out string pcOption))
        {
            whenRefused(trouble);
            return;
        }

        string had = rig.MicSource ?? "";
        if (string.IsNullOrEmpty(_injectionSavedMicSource))
            _injectionSavedMicSource = had;

        // Through the app-initiated scope: this is the arm's own mechanical
        // act, not the operator editing a setting, so it must not arm the
        // provisional-change receipt or the disconnect save offer (#225).
        using (rig.AppInitiatedSettingChanges())
            rig.MicSource = pcOption;

        int tries = 0;
        _injectionArmPending = true;
        var wait = new DispatcherTimer { Interval = InjectionPathTick };
        wait.Tick += (s, e) =>
        {
            tries++;
            string now = rig.TxTonePathTrouble;
            if (string.IsNullOrEmpty(now))
            {
                wait.Stop();
                _injectionArmPending = false;
                whenReady(Lexicon.Get("audio.inject.source_switched", ("source", had)));
                return;
            }
            if (tries < InjectionPathTries) return;

            // The radio never took it. Put back what we asked to change, and
            // refuse in the radio's own words with the failure named.
            wait.Stop();
            _injectionArmPending = false;
            RestoreInjectionPath(rig, out _);
            whenRefused(trouble + " " + Lexicon.Get("audio.inject.source_switch_failed"));
        };
        wait.Start();
    }

    /// <summary>
    /// True when an arm that is now mid-flight is still wanted: the operator
    /// has not untied it, and the radio has not changed underneath it.
    /// </summary>
    /// <remarks>
    /// The wait for the radio is up to a second, and a second is plenty of time
    /// for somebody to change their mind or for a radio to go away. Arming
    /// after either would put a signal in place of the microphone that nobody
    /// currently wants there.
    /// </remarks>
    private bool StillWantsToArm(FlexBase rig, System.Windows.Controls.CheckBox? armBox)
    {
        if (!ReferenceEquals(_rig, rig)) return false;
        if (!IsLoaded) return false;
        return armBox?.IsChecked == true;
    }

    /// <summary>
    /// True when the only thing standing between this radio and an injected
    /// source is its transmit input, and the radio offers "this computer" as a
    /// choice.
    /// </summary>
    /// <remarks>
    /// Asks the three underlying facts rather than reading
    /// <c>TxTonePathTrouble</c>'s sentence. The sentence is operator-facing
    /// prose that gets edited — and the lexicon is editable by operators — so
    /// matching on it would make a translation or a reword silently change what
    /// this dialog does to a transmitter.
    /// </remarks>
    private static bool CanSwitchTransmitInput(FlexBase rig, out string pcOption)
    {
        pcOption = "";
        if (!rig.PCAudio) return false;                       // a different road owns this
        string mode = rig.Mode ?? "";
        if (mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(rig.MicSource, PcMicSource, StringComparison.OrdinalIgnoreCase))
            return false;                                     // already there; trouble is elsewhere

        foreach (string option in rig.MicSourceList)
        {
            if (string.Equals(option, PcMicSource, StringComparison.OrdinalIgnoreCase))
            {
                pcOption = option;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Put the transmit input back where the operator had it, if an arm moved
    /// it. Returns false, with an empty note, when there was nothing to undo.
    /// </summary>
    /// <remarks>
    /// It restores only while the input is still where we left it. An operator
    /// who changed it themselves while something was armed has said what they
    /// want, and undoing that on release would be this dialog overruling them.
    /// </remarks>
    private bool RestoreInjectionPath(FlexBase? rig, out string note)
    {
        note = "";
        string had = _injectionSavedMicSource;
        _injectionSavedMicSource = "";
        if (string.IsNullOrEmpty(had) || rig == null) return false;
        if (!string.Equals(rig.MicSource, PcMicSource, StringComparison.OrdinalIgnoreCase))
            return false;

        using (rig.AppInitiatedSettingChanges())
            rig.MicSource = had;
        note = Lexicon.Get("audio.inject.source_restored", ("source", had));
        return true;
    }

    #endregion
}
