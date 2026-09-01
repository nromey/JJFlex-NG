using System;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Opens <see cref="EqualizerDialog"/> on the radio's transmit or receive
/// equalizer.
///
/// <para><b>Why this exists rather than the wiring living at the button.</b>
/// The two sides differ by three method calls and a title; everything else —
/// the working snapshot, the refusals, the fact that a band change goes to the
/// radio the moment it is made — is identical. Written twice, the two copies
/// would be free to drift, and a receive equalizer that behaved subtly
/// differently from the transmit one is the kind of difference nobody notices
/// until an operator does. Any future surface that wants an equalizer (a menu
/// item, the Audio Workshop) calls in here too.</para>
/// </summary>
public static class EqualizerLauncher
{
    /// <summary>Open the transmit equalizer.</summary>
    public static void ShowTransmit(FlexBase? rig)
    {
        if (!Ready(rig, transmit: true)) return;

        FlexBase.TxEqSettings? working = rig!.GetTxEq();
        if (working == null) return;   // Ready() already said so
        FlexBase.TxEqSettings snapshot = working;

        Show(Lexicon.Get("audio.eq.title_transmit"), snapshot,
             () => rig.ApplyTxEq(snapshot));
    }

    /// <summary>Open the receive equalizer.</summary>
    public static void ShowReceive(FlexBase? rig)
    {
        if (!Ready(rig, transmit: false)) return;

        FlexBase.RxEqSettings? working = rig!.GetRxEq();
        if (working == null) return;
        FlexBase.RxEqSettings snapshot = working;

        Show(Lexicon.Get("audio.eq.title_receive"), snapshot,
             () => rig.ApplyRxEq(snapshot));
    }

    /// <summary>
    /// Say why nothing is going to open, when nothing is going to open.
    ///
    /// <para>This is the whole of #430. Those two buttons sat enabled and in
    /// the tab order with no handler at all, so pressing Enter produced no
    /// dialog, no error and no speech — which, without sight, is exactly what
    /// missing the keystroke produces. A control that cannot act must SAY it
    /// cannot act.</para>
    ///
    /// <para>The not-answered-yet case also fixes itself: the equalizer is
    /// requested at the moment the operator asks for it, so the second press
    /// works even if nothing had asked the radio before.</para>
    /// </summary>
    private static bool Ready(FlexBase? rig, bool transmit)
    {
        if (rig == null)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.eq.no_radio"), VerbosityLevel.Terse);
            return false;
        }

        bool answered = transmit ? rig.GetTxEq() != null : rig.GetRxEq() != null;
        if (!answered)
        {
            if (transmit) rig.RequestTxEqualizer(); else rig.RequestRxEqualizer();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.eq.not_reported_yet"), VerbosityLevel.Terse);
            return false;
        }

        return true;
    }

    /// <summary>
    /// One working snapshot, re-sent whole on every change.
    ///
    /// <para>Sending all nine bands for a one-band change looks wasteful and
    /// is not: FlexLib's equalizer only puts a command on the wire when a
    /// level actually differs from what it holds, so the eight unchanged bands
    /// cost nothing. The alternative — a per-band write path — would be a
    /// second way to set an equalizer, which is how two ways to do one thing
    /// start.</para>
    /// </summary>
    private static void Show(string title, FlexBase.EqSettings working, Func<bool> apply)
    {
        var dialog = new EqualizerDialog
        {
            GetEqTitle = () => title,
            GetBandLevel = i => FlexBase.GetEqBand(working, i),
            SetBandLevel = (i, level) =>
            {
                FlexBase.SetEqBand(working, i, level);
                apply();
            },
            GetEqEnabled = () => working.Enabled,
            SetEqEnabled = on =>
            {
                working.Enabled = on;
                apply();
            }
        };

        dialog.ShowModalDialog();
    }
}
