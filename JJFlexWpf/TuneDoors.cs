using System;
using Radios;
using Radios.Speech;
using JJTrace;
using System.Diagnostics;

namespace JJFlexWpf;

/// <summary>
/// What a recorder or send door does while a tune carrier or an ATU cycle is
/// up (#860). The air then carries a plain carrier, so nothing the app starts
/// can modulate it: on 2026-10-31 a slice recording played under tune put the
/// recording in the transmit chain and a flat carrier on the air, and nothing
/// said so.
/// </summary>
/// <remarks>
/// <para>
/// Ruled by Noel 2026-11-01. <b>Recording is refused</b> (07:52: "you shouldn't
/// ever try to record in tune mode"). <b>Playing still plays</b> (08:04: "one
/// could play to speakers a slice recording if they really wanted to, it just
/// won't go out"), and says so at the press. A route that would put audio on
/// the air, a voice message or a send, starts nothing and says the same words.
/// </para>
/// <para>
/// <b>The carrier is never touched.</b> Tune stays unheld by his ruling of
/// 2026-10-22; every door here only decides what starts and what is said.
/// Stopping something already running is never refused.
/// </para>
/// </remarks>
internal static class TuneDoors
{
    /// <summary>For tests: stands in for the radio's report.</summary>
    internal static Func<FlexBase?, bool>? UpOverride { get; set; }

    /// <summary>A tune carrier or ATU cycle of ours is up. Unreadable reads as not.</summary>
    internal static bool Up(FlexBase? rig)
    {
        if (UpOverride != null) return UpOverride(rig);
        try { return rig?.TuningNow == true; }
        catch (Exception ex)
        {
            Tracing.TraceLine("TuneDoors: " + ex.Message, TraceLevel.Warning);
            return false;
        }
    }

    /// <summary>The sentence for a playback that will not reach the air.</summary>
    // DRAFT PROSE (#860): awaiting Noel.
    internal static string WontGoOut => Lexicon.Get("audio.tune.wont_go_out");

    private static void Refuse(string sentence)
    {
        Tracing.TraceLine("TuneDoors: refused under tune: " + sentence, TraceLevel.Info);
        EarconPlayer.ATUFailTone();   // #447's refusal tone
        ScreenReaderOutput.Speak(sentence, SpeechIntent.Interrupt, VerbosityLevel.Critical,
            subject: SpeechSubject.TuneRefused);
    }

    /// <summary>Recording, or a check that would key: nothing starts.</summary>
    // DRAFT PROSE (#860): awaiting Noel.
    internal static void RefuseStart() => Refuse(Lexicon.Get("audio.tune.turn_off_first"));

    /// <summary>A route that would put audio on the air: nothing starts.</summary>
    internal static void RefuseSend() => Refuse(WontGoOut);

    /// <summary>
    /// The answer to a Play press that started playing: under tune, the
    /// sentence that it will not go out, in place of the usual one.
    /// </summary>
    internal static RecorderOutcome ForPlay(RecorderOutcome outcome, bool underTune) =>
        underTune && outcome.Success && !outcome.AlreadyInState ? outcome with { Message = WontGoOut } : outcome;

    /// <summary>The sentence a playback starting or carrying on says, under tune or not.</summary>
    internal static string ForPlay(string usual, bool underTune) => underTune ? WontGoOut : usual;
}
