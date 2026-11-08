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
        var go = await RecorderControls.ConfirmOverwriteAsync(rig, target, owner: this);
        if (!go.Proceed || _closing || !ReferenceEquals(rig, _rig)) return;
        // Answered for this slice only: if the transmit slice moved meanwhile,
        // the next start asks about the slice it would really record over.
        _overwriteAnsweredForNextStart =
            rig.SliceRecorderLetter(go.Target) == rig.SliceRecorderLetter(CheckRecorderTarget(rig));
        ToggleAudioCheck();
    }
}
