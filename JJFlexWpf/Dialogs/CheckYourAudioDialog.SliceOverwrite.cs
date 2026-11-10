using System.Threading.Tasks;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// #999: the Radio recording audio check method records into the slice
/// recorder, so before it starts it asks the same question F11 asks when that
/// slice already holds a recording (<see cref="RecorderControls.ConfirmOverwriteAsync"/>).
/// Kept in its own file so the check's own flow changes only where it must.
/// </summary>
public partial class CheckYourAudioDialog
{
    /// <summary>
    /// Set by the overwrite warning's go-ahead, and spent by the very next
    /// <see cref="ToggleAudioCheck"/>, which then does not ask again.
    /// </summary>
    private bool _overwriteAnsweredForNextStart;

    /// <summary>
    /// The slice the Radio recording method records on: the transmit slice, by
    /// its letter, resolved as the check session resolves it when it arms.
    /// </summary>
    internal static SliceRecorderTarget CheckRecorderTarget(FlexBase? rig)
    {
        string letter = rig?.SliceRecorderLetter(SliceRecorderTarget.TransmitSlice) ?? "";
        return letter.Length > 0 ? SliceRecorderTarget.Letter(letter[0]) : SliceRecorderTarget.TransmitSlice;
    }

    /// <summary>
    /// Ask, then start the check from the top if the operator went ahead (after
    /// a backup, when Back up first was chosen). Nothing starts on Cancel, or
    /// when the window closed or the radio changed while the question was open.
    /// </summary>
    private async Task AskBeforeCheckOverwritesAsync(FlexBase? rig)
    {
        if (rig == null) return;
        var target = CheckRecorderTarget(rig);
        // Back up first that does not finish (#1040): the backup door says why
        // and then that the check did not start, in ONE utterance. Queued
        // behind the door's interrupting sentence, as round 1 had it, it was
        // withdrawn unspoken on the 8600 bench, two of two: NVDA carries out
        // the cancel for that interrupt after the call that asked for it has
        // returned, so it is reported against the door's own sentence (which
        // NVDA still speaks) and the speech queue then withdraws everything
        // behind it as the operator's silence. Cancel and Escape stay silent
        // (#999, C3): the door is never reached.
        // Sentence written by Codex 2026-11-09 (#1040); Noel reviews the wording.
        var go = await RecorderControls.ConfirmOverwriteAsync(rig, target, owner: this,
            afterUnfinishedBackup: Lexicon.Get("audio.check.not_started_backup_unfinished"));
        if (!go.Proceed || _closing || !ReferenceEquals(rig, _rig)) return;
        // Answered for this slice only: if the transmit slice moved meanwhile,
        // the next start asks about the slice it would really record over.
        _overwriteAnsweredForNextStart =
            rig.SliceRecorderLetter(go.Target) == rig.SliceRecorderLetter(CheckRecorderTarget(rig));
        ToggleAudioCheck();
    }
}
