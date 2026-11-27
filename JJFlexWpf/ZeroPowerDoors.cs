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
/// The callsign refusal is the one exception: its sentence is Critical
/// (ruled 2026-11-09, night question J9), see <see cref="RefuseNoCallsign"/>.
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
    /// <param name="sentence">A window's own form of the sentence, when it
    /// shows the operator something the shared one cannot name (Check your
    /// audio's button that turns the check off, #1306). Null for the shared
    /// <see cref="FlexBase.NoCallsignRefusal"/>.</param>
    internal static bool RefuseNoCallsign(FlexBase? rig, string door, bool afterMenu = false, string? sentence = null)
    {
        if (!NoCallsign(rig)) return false;
        // Said at Critical, so no verbosity setting silences it (ruled by Noel
        // 2026-11-09 20:29, night question J9): no key works until a callsign
        // is entered, so the operator must always hear why. The zero-power
        // refusal stays at Terse (2026-11-01); this differs on purpose. The
        // tone still answers to its own gate, and the trace is always written.
        Answer("callsign: " + door, sentence ?? FlexBase.NoCallsignRefusal, afterMenu,
            SpeechSubject.TransmitKeyRefused, "refused with no operator callsign: ",
            VerbosityLevel.Critical);
        return true;
    }

    /// <summary>For tests: stands in for <see cref="FlexBase.VoxOnWouldTurnOnSimpleVox"/>.</summary>
    internal static Func<FlexBase?, bool>? SimpleVoxOverride { get; set; }

    /// <summary>For tests: stands in for <see cref="FlexBase.VoxOnWouldTurnOnBreakIn"/>.</summary>
    internal static Func<FlexBase?, bool>? BreakInOverride { get; set; }

    /// <summary>
    /// Turning VOX ON with no callsign (#335, ruled by Noel 2026-11-09 20:28,
    /// night question J8), and in CW turning break-in ON (ruled 2026-11-11
    /// 11:51, night question J4): refused with the same sentence, asked by
    /// each door before it writes, so a menu press speaks after the menu
    /// closes and a checkbox can be put back. True when refused: the caller
    /// must not turn VOX on. Ask it only on the way ON; turning VOX or
    /// break-in off is never refused.
    /// </summary>
    /// <remarks>
    /// <para><b>Both of the control's meanings.</b> In CW the same control
    /// switches break-in. Which of the two a press would switch is the
    /// radio's answer, <see cref="FlexBase.VoxOnWouldTurnOnSimpleVox"/> and
    /// its sibling <see cref="FlexBase.VoxOnWouldTurnOnBreakIn"/>, read from
    /// the TRANSMIT slice, the slice <see cref="FlexBase.Vox"/>'s setter
    /// writes through, and never from the selected slice's mode. With no
    /// transmit slice neither is true and nothing is refused (the setter
    /// writes nothing).</para>
    /// <para><b>The callsign refusal comes first, at every door.</b> Each
    /// door asks this before its "tone running" refusal, and the setter asks
    /// in the same order, so when both apply the operator hears the callsign
    /// sentence whichever way the press came.</para>
    /// <para>The setter asks the same question again
    /// (<c>RefusedForNoCallsign</c>), so a route with no door of its own is
    /// refused too. The radio's own front-panel VOX and break-in are left
    /// alone.</para>
    /// </remarks>
    internal static bool RefuseVoxOn(FlexBase? rig, string door, bool afterMenu = false)
    {
        if (!NoCallsign(rig)) return RefuseVoxOnForTransmitRules(rig, door, afterMenu);
        if (!WouldTurnOnSimpleVox(rig) && !WouldTurnOnBreakIn(rig)) return false;
        return RefuseNoCallsign(rig, door, afterMenu);
    }

    /// <summary>For tests: stands in for the radio's transmit rules verdict at a door. The
    /// flag is whether the 60 m rule was asked (false at the antenna tuner's door), so a
    /// test can show which question a door asked (repair round 1).</summary>
    internal static Func<FlexBase?, bool, TransmitVerdict>? TransmitRulesOverride { get; set; }

    /// <summary>
    /// The transmit rules' verdict for a door: the override in a test, else the
    /// radio's own judge (<see cref="FlexBase.TransmitRulesVerdict"/>). Allowed for no
    /// radio, or a judge that throws (traced): the chokepoint behind the door asks again.
    /// </summary>
    private static TransmitVerdict RulesVerdict(FlexBase? rig, bool sixtyMeterRule, string door)
    {
        try
        {
            return TransmitRulesOverride?.Invoke(rig, sixtyMeterRule)
                ?? rig?.TransmitRulesVerdict(sixtyMeterRule) ?? TransmitVerdict.Allowed;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("ZeroPowerDoors: transmit rules at " + door + ": " + ex.Message, TraceLevel.Warning);
            return TransmitVerdict.Allowed;
        }
    }

    /// <summary>
    /// The transmit rules' refusal sentence for a door that has the PTT controller
    /// in hand, or null when it may go ahead (repair round 1 of junk box batch 31).
    /// The controller's own answer (<see cref="PttSafetyController.TransmitRulesRefusal"/>)
    /// when there is one; when there is none, the radio's judge itself, the one the
    /// controller would ask, so a door never skips the rules because the controller
    /// is null and then announces a send the radio refuses. Says nothing.
    /// </summary>
    internal static string? TransmitRulesRefusalAt(PttSafetyController? ptt, FlexBase? rig)
        => ptt != null ? ptt.TransmitRulesRefusal() : RulesVerdict(rig, sixtyMeterRule: true, "a door with no controller").RefusalSentence();

    /// <summary>
    /// An operator's press that would start the ANTENNA TUNER (repair round 1 of
    /// junk box batch 31, the Opus checker's #1307 B1): an Audio Check teardown,
    /// then no callsign, then tune power at 0 watts, as
    /// <see cref="RefuseTunePress"/> asks, then the TX band lockout alone (the 60 m
    /// rule is not asked for the tuner, which raises a tune carrier: the
    /// orchestrator's decision, night question J4). The tuner's setter refuses in
    /// the same order and returns nothing, so without this its door said "ATU
    /// tuning" over the setter's refusal. Never inside <see cref="RefuseTunePress"/>,
    /// which the tune carrier's doors share: tune is never asked the rules.
    /// </summary>
    internal static bool RefuseTunerStart(FlexBase? rig, string door, bool afterMenu = false)
        => RefuseTeardown(rig, door, afterMenu)
        || RefuseTunePress(rig, door, afterMenu)
        || RefuseTransmitRules(rig, door, sixtyMeterRule: false, afterMenu);

    /// <summary>
    /// The transmit rules refusing a press, said at the door: the reason's own
    /// sentence, the refusal tone, Critical, as the chokepoint would say it. True
    /// when refused: the caller must not go on, show or say anything else.
    /// </summary>
    internal static bool RefuseTransmitRules(FlexBase? rig, string door, bool sixtyMeterRule, bool afterMenu = false)
    {
        var verdict = RulesVerdict(rig, sixtyMeterRule, door);
        if (verdict.IsAllowed) return false;
        Answer("transmit rules: " + door, verdict.RefusalSentence() ?? "", afterMenu,
            SpeechSubject.TransmitKeyRefused, "refused by the transmit rules: ", VerbosityLevel.Critical);
        return true;
    }

    /// <summary>
    /// An Audio Check teardown is putting things back (#835): a press that would
    /// start the tuner is refused at its door, with the sentence its setter would
    /// say (repair round 1: <c>FlexTunerOn</c>'s teardown refusal was said and then
    /// followed by "ATU tuning"). Only for a start; stopping is never refused.
    /// </summary>
    private static bool RefuseTeardown(FlexBase? rig, string door, bool afterMenu)
    {
        bool tearingDown;
        try { tearingDown = TeardownOverride?.Invoke(rig) ?? rig?.TeardownInProgress == true; }
        catch (Exception ex)
        {
            Tracing.TraceLine("ZeroPowerDoors: teardown: " + ex.Message, TraceLevel.Warning);
            return false;   // the setter asks again
        }
        if (!tearingDown) return false;
        Answer("teardown: " + door, Lexicon.Get("audio.check.teardown_refuses"), afterMenu,
            SpeechSubject.TransmitKeyRefused, "refused during an Audio Check teardown: ", VerbosityLevel.Critical);
        return true;
    }

    /// <summary>For tests: stands in for the radio's teardown flag at the tuner's door.</summary>
    internal static Func<FlexBase?, bool>? TeardownOverride { get; set; }

    /// <summary>
    /// Turning VOX or break-in ON where the transmit rules (the TX band lockout
    /// and the 60 m rules) refuse (#1307): asked by every VOX door after the
    /// callsign, so the field does not announce "VOX on" for a setter that then
    /// refuses. Says that VOX (or break-in) stays off, then the reason's
    /// sentence. The setter asks again, so a route with no door is refused too.
    /// </summary>
    private static bool RefuseVoxOnForTransmitRules(FlexBase? rig, string door, bool afterMenu)
    {
        bool breakIn = WouldTurnOnBreakIn(rig);
        if (!breakIn && !WouldTurnOnSimpleVox(rig)) return false;
        var verdict = RulesVerdict(rig, sixtyMeterRule: true, door);   // the setter asks again on a failure
        if (verdict.IsAllowed) return false;
        string stays = breakIn ? Lexicon.Get("audio.ptt.break_in_stays_off") : Lexicon.Get("audio.ptt.vox_stays_off");
        Answer("transmit rules: " + door, stays + " " + verdict.RefusalSentence(), afterMenu,
            SpeechSubject.TransmitKeyRefused, "refused by the transmit rules: ", VerbosityLevel.Critical);
        return true;
    }

    private static bool WouldTurnOnSimpleVox(FlexBase? rig)
    {
        if (SimpleVoxOverride != null) return SimpleVoxOverride(rig);
        if (rig == null) return false;
        try { return rig.VoxOnWouldTurnOnSimpleVox; }
        catch (Exception ex)
        {
            Tracing.TraceLine("ZeroPowerDoors: VOX: " + ex.Message, TraceLevel.Warning);
            return false;   // the setter asks again, from the radio's own answer
        }
    }

    private static bool WouldTurnOnBreakIn(FlexBase? rig)
    {
        if (BreakInOverride != null) return BreakInOverride(rig);
        if (rig == null) return false;
        try { return rig.VoxOnWouldTurnOnBreakIn; }
        catch (Exception ex)
        {
            Tracing.TraceLine("ZeroPowerDoors: break-in: " + ex.Message, TraceLevel.Warning);
            return false;   // the setter asks again, from the radio's own answer
        }
    }

    /// <summary>
    /// Whether the operator's callsign is missing, asked without saying
    /// anything. For a door that must know whether the refusal was already
    /// said elsewhere (Check your audio's keyed refusal, #1023 item 2).
    /// </summary>
    /// <remarks>
    /// The operator's switch is asked FIRST (#1306): with the callsign check
    /// off, nothing is refused and the override below is never consulted, so a
    /// test cannot pass through the override without the switch having been
    /// seen. The radio's own answer asks the same switch.
    /// </remarks>
    internal static bool NoCallsign(FlexBase? rig)
    {
        if (!FlexBase.CallsignCheckOn) return false;
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

    /// <param name="level">The sentence's verbosity: Terse, the usual way, for
    /// the zero-power refusals; Critical for the callsign refusal (J9).</param>
    private static void Answer(string door, string sentence, bool afterMenu,
        string subject = SpeechSubject.ZeroPowerRefused, string traceWhy = "refused at 0 watts: ",
        VerbosityLevel level = VerbosityLevel.Terse)
    {
        if (!NewPress(door)) return;
        Answered?.Invoke(door);
        Tracing.TraceLine("ZeroPowerDoors: " + traceWhy + door, TraceLevel.Info);
        EarconPlayer.ATUFailTone();   // the refusal tone, through its own gate, with no admission
        if (afterMenu) _ = SayAfterMenuAsync(sentence, subject, level);
        else Say(sentence, subject, level);
    }

    private static async Task SayAfterMenuAsync(string sentence, string subject, VerbosityLevel level)
    {
        await Task.Delay(AfterMenuMs);
        Say(sentence, subject, level);
    }

    private static void Say(string sentence, string subject, VerbosityLevel level) =>
        ScreenReaderOutput.Speak(sentence, SpeechIntent.Interrupt, level,
            subject: subject);
}
