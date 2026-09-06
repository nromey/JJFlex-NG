using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Opens <see cref="TNFDialog"/> on the radio's tracking notch filters.
///
/// <para><b>Why this exists at all.</b> The tracking notch filter has been
/// wired to the radio and unreachable by a person for two sprints (#482).
/// <c>FlexBase</c> subscribes to TNFAdded and TNFRemoved and sets
/// <c>TNFEnabled</c> on every connect, so the radio has been ready to place
/// notches the whole time; Jim's WinForms <c>FlexTNF</c> was deleted in
/// <c>074b2c78</c> once its replacement existed, and the replacement —
/// <see cref="TNFDialog"/>, written in Sprint 9 — was never constructed
/// anywhere. Neither step was wrong on its own, which is exactly why nothing
/// caught it: the feature fell through the seam between them.</para>
///
/// <para><b>Why a launcher rather than wiring at the button.</b> Same reason as
/// <see cref="EqualizerLauncher"/>. The dialog takes ten delegates and takes
/// them in plain ints, strings and bools on purpose, so that it references
/// neither <c>FlexBase</c> nor FlexLib's <c>TNF</c> type. Somewhere has to hold
/// the other end of that contract, and holding it once means a second door to
/// the notch list — the JJ key chord, a menu item, a future noise layer —
/// cannot grow its own slightly different idea of what Add does.</para>
/// </summary>
public static class TNFLauncher
{
    /// <summary>
    /// Open the tracking notch filter list, or say why it is not opening.
    ///
    /// <para><b>A control that cannot act must SAY it cannot act.</b> That is
    /// the whole of #430 and it applies twice as hard here, because a notch is
    /// placed by the RADIO rather than by us: Add asks, and the notch exists
    /// only once the status parser has read it back. Success, refusal and a
    /// radio that simply never answers are three different things that,
    /// without sight, produce the identical silence. Every one of them gets a
    /// sentence.</para>
    /// </summary>
    public static void Show(FlexBase? rig)
    {
        if (rig == null)
        {
            Speak(Lexicon.Get("audio.tnf.no_radio"));
            return;
        }

        // Not a model gate. Every 6000- and 8000-series radio has tracking
        // notches and the connect path enables them on all of them, so there is
        // no license or capability to check — what can be missing is a receive
        // slice with a panadapter behind it, because RequestTNF needs a
        // panadapter stream to hang the notch on. That is a not-yet, not a
        // never, so it is worded as one.
        if (!rig.CanAddTNF())
        {
            Speak(Lexicon.Get("audio.tnf.no_slice"));
            return;
        }

        var dialog = new TNFDialog
        {
            GetTNFCount = () => rig.TNFCount(),
            GetTNFFrequencyDisplay = i => rig.TNFFrequencyDisplay(i),

            GetTNFWidth = i => rig.TNFWidthHz(i),
            SetTNFWidth = (i, hz) => rig.SetTNFWidthHz(i, hz),

            GetTNFDepth = i => rig.TNFDepth(i),
            SetTNFDepth = (i, d) => rig.SetTNFDepth(i, d),

            GetTNFPermanent = i => rig.TNFPermanent(i),
            SetTNFPermanent = (i, p) => rig.SetTNFPermanent(i, p),

            AddTNF = () =>
            {
                string? display = rig.AddTNFAtReceiveFrequency();
                Speak(display != null
                    ? Lexicon.Get("audio.tnf.added", ("freq", display))
                    : Lexicon.Get("audio.tnf.add_failed"));
                return display;
            },

            RemoveTNF = i =>
            {
                bool gone = rig.RemoveTNFAt(i);
                Speak(gone
                    ? Lexicon.Get("audio.tnf.removed")
                    : Lexicon.Get("audio.tnf.remove_failed"));
                return gone;
            }
        };

        dialog.ShowModalDialog();
    }

    /// <summary>
    /// One keyed subject for the whole notch conversation, so that a refusal
    /// is as durable as the success it replaces. Unkeyed, these would expire on
    /// their own word count — and the refusal sentences are the long ones,
    /// which is precisely backwards (#503).
    /// </summary>
    private static void Speak(string message) =>
        ScreenReaderOutput.Speak(
            message,
            Radios.Speech.SpeechIntent.Queue,
            VerbosityLevel.Terse,
            subject: Radios.Speech.SpeechSubject.TrackingNotch);
}
