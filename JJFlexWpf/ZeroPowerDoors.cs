using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Radios;
using Radios.Speech;
using JJTrace;
using System.Diagnostics;

namespace JJFlexWpf;

/// <summary>
/// The operator's presses that would key a rig reading 0 watts (#859). On such a
/// rig nothing goes out at zero and a recording comes back silent, and without
/// this the operator is told nothing. The rule is
/// <see cref="RadioPowerFloor.RefusesPress"/>; this is what a door does with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A door answers for an operator's press, and nothing else.</b> A check's
/// own keying (<see cref="PttSafetyController.KeyForCheck"/>), the Fix tool's and
/// a send's own key after its door has passed do not come through here, so Test
/// your microphone stays at zero by ruling. Nothing here belongs in
/// <see cref="FlexBase"/>'s setters.
/// </para>
/// <para>
/// <b>Only ever stops RF from starting.</b> A caller asks this for a press that
/// would start something, never for one that stops, unkeys or turns tune off.
/// This is not the hold after a reflected-power cut and shares none of its
/// state: Tune is not held, by ruling of 2026-10-22, and a tune door here is a
/// different thing, about a power that cannot work.
/// </para>
/// <para>
/// <b>Told the usual way</b> (ruled by Noel 2026-11-01 13:06: "we notify the usual
/// way, verbosity gated for tone and text"). The tone goes through the earcon's
/// own gate, the Transmit family, and the sentence is Terse, so each answers to
/// the operator's own setting and either can be silenced. The trace line is
/// written regardless, so a silenced refusal is still on record.
/// </para>
/// <para>
/// <b>One press gets one answer.</b> A refusal leaves the state idle, so under a
/// reader that delivers a held key as pairs, or a key handler that sees repeats,
/// every repeat reaches the door again. Each door keeps a
/// <see cref="FreshPressGate"/>; every repeat is still refused, with nothing said.
/// </para>
/// </remarks>
internal static class ZeroPowerDoors
{
    /// <summary>
    /// How long a sentence waits for a menu to close, the wait the menu's own
    /// sentences use: spoken sooner, the menu closing flushes it.
    /// </summary>
    private const int AfterMenuMs = 500;

    private static readonly Dictionary<string, FreshPressGate> Gates = new();

    /// <summary>For tests: stands in for the radio's report. The flag is true for a tune door.</summary>
    internal static Func<FlexBase?, bool, bool>? AtZeroOverride { get; set; }

    /// <summary>For tests: the clock the presses are told apart by.</summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// For tests: told each door that answers a press. The speech history
    /// cannot count answers, because it folds a sentence said twice into one.
    /// </summary>
    internal static Action<string>? Answered { get; set; }

    /// <summary>For tests: every door starts with no press in hand.</summary>
    internal static void Forget()
    {
        lock (Gates) Gates.Clear();
    }

    /// <summary>
    /// For tests: stands in for the radio's answer to "has the operator a
    /// callsign?" (#335). True means none.
    /// </summary>
    internal static Func<FlexBase?, bool>? NoCallsignOverride { get; set; }

    /// <summary>
    /// No callsign, no transmit (#335): a press that would key, asked BEFORE
    /// anything is prepared. True when it is refused: the caller must not key.
    /// </summary>
    /// <remarks>
    /// <para>Asked first at every door that would key, and the one rule a
    /// check's own keying is NOT exempt from: Noel ruled (2026-11-08, nap
    /// question J1) that the gate covers every keying JJ Flexible starts, the
    /// checks included, where the zero-power rule leaves Test your microphone
    /// alone. So <see cref="PttSafetyController.ToggleLock"/> asks this
    /// directly, ahead of its check exemption, and the doors ask it through
    /// <see cref="RefuseTransmitPress"/> and <see cref="RefuseTunePress"/>.</para>
    /// <para>NOT folded into <see cref="RefuseTransmit"/>: one caller of that,
    /// the slice recorder's door, asks it about a recording started while a
    /// hardware push to talk is down, which keys nothing.</para>
    /// <para>The answer is the radio's (<see cref="FlexBase.OperatorCallsignMissing"/>),
    /// which reads the operator's callsign at this moment, and the sentence
    /// is <see cref="FlexBase.NoCallsignRefusal"/>, the same one the radio's
    /// chokepoints speak if a route ever reaches them without a door.</para>
    /// <para>This class is named for its first rule and now holds two; its
    /// name is reported for the orchestrator rather than changed here.</para>
    /// </remarks>
    /// <param name="rig">The radio. None reads as not refused (nothing can key).</param>
    /// <param name="door">The door, for the trace. One press answers once per door.</param>
    /// <param name="afterMenu">The press came from a menu, which is about to close.</param>
    internal static bool RefuseNoCallsign(FlexBase? rig, string door, bool afterMenu = false)
    {
        if (!NoCallsign(rig)) return false;
        // Told the usual way, as the zero-power refusal is (Noel, 2026-11-01:
        // "we notify the usual way, verbosity gated for tone and text"): the
        // tone through its own gate, the sentence at Terse, the trace always.
        Answer("callsign: " + door, FlexBase.NoCallsignRefusal, afterMenu,
            SpeechSubject.TransmitKeyRefused, "refused with no operator callsign: ");
        return true;
    }

    /// <summary>
    /// Whether the operator's callsign is missing, asked without saying
    /// anything. For a door that must know whether the refusal was already
    /// said elsewhere (Check your audio's keyed refusal, #1023 item 2).
    /// </summary>
    internal static bool NoCallsign(FlexBase? rig)
    {
        if (NoCallsignOverride != null) return NoCallsignOverride(rig);
        if (rig == null) return false;
        try { return rig.OperatorCallsignMissing; }
        catch (Exception ex)
        {
            Tracing.TraceLine("ZeroPowerDoors: callsign: " + ex.Message, TraceLevel.Warning);
            return true;   // refusing a key is the safe failure
        }
    }

    /// <summary>
    /// An operator's press that would KEY the radio: no callsign first (#335),
    /// then 0 watts (#859). True when refused: the caller must not key.
    /// </summary>
    internal static bool RefuseTransmitPress(FlexBase? rig, string door, bool afterMenu = false)
        => RefuseNoCallsign(rig, door, afterMenu) || RefuseTransmit(rig, door, afterMenu);

    /// <summary>
    /// An operator's press that would start a tune carrier or the tuner: no
    /// callsign first (#335), then tune power at 0 watts (#859).
    /// </summary>
    internal static bool RefuseTunePress(FlexBase? rig, string door, bool afterMenu = false)
        => RefuseNoCallsign(rig, door, afterMenu) || RefuseTune(rig, door, afterMenu);

    /// <summary>A press that would transmit. True when it is refused: the caller must not key.</summary>
    /// <param name="rig">The radio. None or unreadable reads as not refused.</param>
    /// <param name="door">The door, for the trace. One press answers once per door.</param>
    /// <param name="afterMenu">The press came from a menu, which is about to close.</param>
    internal static bool RefuseTransmit(FlexBase? rig, string door, bool afterMenu = false)
    {
        if (!AtZero(rig, tune: false)) return false;
        Answer(door, RadioPowerFloor.TransmitRefusal, afterMenu);
        return true;
    }

    /// <summary>A press that would start a tune, which reads tune power. True when it is refused.</summary>
    /// <param name="rig">The radio. None or unreadable reads as not refused.</param>
    /// <param name="door">The door, for the trace. One press answers once per door.</param>
    /// <param name="afterMenu">The press came from a menu, which is about to close.</param>
    internal static bool RefuseTune(FlexBase? rig, string door, bool afterMenu = false)
    {
        if (!AtZero(rig, tune: true)) return false;
        Answer(door, RadioPowerFloor.TuneRefusal, afterMenu);
        return true;
    }

    private static bool AtZero(FlexBase? rig, bool tune)
    {
        if (AtZeroOverride != null) return AtZeroOverride(rig, tune);
        if (rig == null) return false;
        try { return tune ? rig.TunePressRefused : rig.TransmitPressRefused; }
        catch (Exception ex)
        {
            Tracing.TraceLine("ZeroPowerDoors: " + ex.Message, TraceLevel.Warning);
            return false;
        }
    }

    private static bool NewPress(string door)
    {
        lock (Gates)
        {
            if (!Gates.TryGetValue(door, out FreshPressGate? gate))
                Gates[door] = gate = new FreshPressGate();
            return gate.NewRefusal(Clock());
        }
    }

    private static void Answer(string door, string sentence, bool afterMenu,
        string subject = SpeechSubject.ZeroPowerRefused, string traceWhy = "refused at 0 watts: ")
    {
        if (!NewPress(door)) return;
        Answered?.Invoke(door);
        Tracing.TraceLine("ZeroPowerDoors: " + traceWhy + door, TraceLevel.Info);
        EarconPlayer.ATUFailTone();   // the refusal tone, through its own gate, with no admission
        if (afterMenu) _ = SayAfterMenuAsync(sentence, subject);
        else Say(sentence, subject);
    }

    private static async Task SayAfterMenuAsync(string sentence, string subject)
    {
        await Task.Delay(AfterMenuMs);
        Say(sentence, subject);
    }

    private static void Say(string sentence, string subject) =>
        ScreenReaderOutput.Speak(sentence, SpeechIntent.Interrupt, VerbosityLevel.Terse,
            subject: subject);
}
