using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Flex.Smoothlake.FlexLib;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1127, ruled by Noel 2026-11-12 08:05 (J4): when the ATU is bypassed, or
/// the radio has none, the tune key sends a plain tune carrier and SAYS so,
/// never "ATU tuning". Automatic type is unchanged. J4 also kept Jim's design
/// that a Fail bypasses the ATU and leaves automatic; #1188's 2026-11-19
/// ruling (J1) superseded that half: a failed tune now stays automatic.
/// </summary>
/// <remarks>
/// What the press keys is read off the vendor radio fixture's transport, the
/// commands that really go out, as AtuTuneEndTests does.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TunePressTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(TunePressTests));
    public void Dispose() => _scope.Dispose();

    // ---- the rule, every tuner state ----

    [Theory]
    [InlineData(true, FlexBase.FlexTunerTypes.auto, TunePressOutcome.AtuTune)]      // unchanged
    [InlineData(true, FlexBase.FlexTunerTypes.manual, TunePressOutcome.TuneCarrier)] // after the operator's bypass
    [InlineData(false, FlexBase.FlexTunerTypes.manual, TunePressOutcome.TuneCarrier)] // no ATU (refused before)
    [InlineData(false, FlexBase.FlexTunerTypes.auto, TunePressOutcome.NoTuner)]     // refused, as before
    // None cannot arise (below). Were it to, it would mean an ATU switched off,
    // which the ruling treats as bypassed. Until repair round 1 these two rows
    // pinned a refusal and a fourth outcome, "sends nothing", that described a
    // state no rig reaches (Opus's N5).
    [InlineData(false, FlexBase.FlexTunerTypes.none, TunePressOutcome.TuneCarrier)]
    [InlineData(true, FlexBase.FlexTunerTypes.none, TunePressOutcome.TuneCarrier)]
    public void What_the_tune_key_does_in_each_tuner_state(bool hasAtu, FlexBase.FlexTunerTypes type, TunePressOutcome expected) =>
        Assert.Equal(expected, TunePress.For(hasAtu, type));

    /// <summary>
    /// Why the tuner type is never none (Opus's N5): asking for anything but
    /// automatic goes through setFlexTunerTypeNotAuto, which yields manual
    /// when the rig has the manual tune capability; every rig starts with it
    /// and nothing takes it away; and nothing else writes the type but that,
    /// automatic, or an automatic value through the setter.
    /// </summary>
    [Fact]
    public void The_tuner_type_none_cannot_arise()
    {
        Assert.Contains(RigCaps.Caps.ManualATGet, RigCaps.DefaultCapsList);

        using var c = new RadioCase();
        Assert.True(c.F.Rig.MyCaps.HasCap(RigCaps.Caps.ManualATGet));
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);
        typeof(FlexBase).GetMethod("setFlexTunerTypeNotAuto", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(c.F.Rig, null);
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);

        string radios = string.Join("\n", Directory.GetFiles(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios"), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));
        Assert.DoesNotMatch(new Regex(@"ResetCap\([^;]*ManualAT"), radios);
        var writes = Regex.Matches(radios, @"_FlexTunerType\s*=[^=][^;]*;").Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToList();
        Assert.NotEmpty(writes);
        Assert.All(writes, w => Assert.True(
            w == "_FlexTunerType = FlexTunerTypes.auto;"
            || w == "_FlexTunerType = value;"
            || w == "_FlexTunerType = (MyCaps.HasCap(RigCaps.Caps.ManualATGet)) ? FlexTunerTypes.manual : FlexTunerTypes.none;", w));
        Assert.Contains("if (value == FlexTunerTypes.auto) _FlexTunerType = value;", radios);
    }

    // ---- what actually goes out ----

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0)
            if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static void Wired(RadioCase c, FlexBase.FlexTunerTypes type)
    {
        typeof(FlexBase).GetField("_FlexTunerType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, type);
        var radio = c.F.Vendor.Radio;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, binding });
    }

    /// <summary>
    /// After a failed tune the type stays automatic and no bypass is sent
    /// (#1188's Fail case, ruled by Noel 2026-11-19 20:35, superseding J4's
    /// "after a Fail the ATU is bypassed and taken out of automatic"; dropping
    /// the bypass command is the orchestrator's decision), so the next press
    /// runs another ATU cycle: "atu start", not a plain "transmit tune 1".
    /// Until batch 17 this was
    /// <c>After_a_failed_tune_the_next_press_is_a_plain_tune_carrier</c>.
    /// </summary>
    [Fact]
    public void After_a_failed_tune_the_next_press_runs_another_ATU_cycle()
    {
        using var c = new RadioCase();
        Wired(c, FlexBase.FlexTunerTypes.auto);
        Assert.Equal(TunePressOutcome.AtuTune, TunePress.For(true, c.F.Rig.FlexTunerType));
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        int starts = Sent(c, "atu start");
        Assert.True(starts > 0, "positive control: automatic type sent atu start");
        c.F.Vendor.Status("atu status=TUNE_IN_PROGRESS");
        c.F.Vendor.Status("atu status=TUNE_FAIL");
        RunQueue(c);
        Assert.Equal(0, Sent(c, "atu bypass"));

        Assert.Equal(TunePressOutcome.AtuTune, TunePress.For(true, c.F.Rig.FlexTunerType));
        int keys = Sent(c, "transmit tune 1");
        c.F.Rig.FlexTunerOn = true;   // the next press
        RunQueue(c);
        Assert.True(Sent(c, "atu start") > starts, "the press runs another ATU cycle");
        Assert.Equal(keys, Sent(c, "transmit tune 1"));
    }

    /// <summary>
    /// A radio with no ATU: the rig's type is manual from its constructor and
    /// it has no antenna-tuner capability, so the press, refused until now,
    /// sends a plain tune carrier.
    /// </summary>
    [Fact]
    public void A_radio_with_no_ATU_sends_a_plain_tune_carrier()
    {
        using var c = new RadioCase();
        Assert.False(c.F.Rig.HasATU, "positive control: no ATU");
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);
        Assert.Equal(TunePressOutcome.TuneCarrier, TunePress.For(c.F.Rig.HasATU, c.F.Rig.FlexTunerType));

        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        Assert.True(Sent(c, "transmit tune 1") > 0);
        Assert.Equal(0, Sent(c, "atu start"));
    }

    // ---- the wiring: what the press says, and who refuses ----

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

    [Fact]
    public void The_press_says_tune_carrier_whenever_it_sends_one_and_ATU_tuning_only_otherwise()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int at = main.IndexOf("private void TunePressFor(bool ours)", StringComparison.Ordinal);   // the press's body since batch 14 repair 1
        string body = main.Substring(at, main.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
        // #1164: one "has an ATU" source, the radio's ATUEnabled, as the tune
        // key's handler reads it. Changed from the ATGet capability, which
        // lags the radio's report until the ATUEnabled handler runs.
        Assert.Contains("Radios.TunePress.For(RigControl.HasATU, RigControl.FlexTunerType)", body);
        Assert.DoesNotContain("Caps.ATGet", body);
        Assert.Contains("if (outcome == Radios.TunePressOutcome.NoTuner)", body);
        Assert.DoesNotContain("if (!RigControl.MyCaps.HasCap(Radios.RigCaps.Caps.ATGet))", body);
        // Repair round 1: the carrier outcome leaves for the carrier's own key
        // before anything else, and "ATU tuning" is all that is left here.
        Assert.Contains("if (outcome == Radios.TunePressOutcome.TuneCarrier)", body);
        Assert.Contains("(\"audio.tune.atu_tuning\"), VerbosityLevel.Terse, true,", body);
        Assert.DoesNotContain("audio.tune.tune_carrier_sent", body);
        Assert.Contains("subject: Radios.Speech.SpeechSubject.TunePressSent", body);

        // The carrier's sentence is said only after the toggle has asked for
        // the carrier, never ahead of a refusal.
        string press = Body(main, "private void TuneKeyCarrierPress()");
        int toggle = press.IndexOf("try { ToggleTuneCarrier(); }", StringComparison.Ordinal);
        int asked = press.IndexOf("if (rig.TuneAskedOrUp)", toggle, StringComparison.Ordinal);
        int said = press.IndexOf("\"audio.tune.tune_carrier_sent\"", StringComparison.Ordinal);
        Assert.True(toggle > 0 && asked > toggle && said > asked);
        Assert.Contains("subject: Radios.Speech.SpeechSubject.TunePressSent", press.Substring(said));
        Assert.Equal("A tune carrier was requested.", Lexicon.Get("audio.tune.tune_carrier_sent"));
    }

    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature);
        int open = src.IndexOf('{', at), depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new InvalidOperationException(signature);
    }

    /// <summary>
    /// The tune key's carrier is the toggle's (repair round 1): a stop is the
    /// toggle's explicit off, a start the toggle itself, so the radio's answer
    /// and its feedback are Ctrl+Shift+T's, and the gate Check your audio's
    /// tune key uses decides which.
    /// </summary>
    [Fact]
    public void The_tune_keys_carrier_is_the_toggles_own()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        // Batch 14 repair 1: the gate and its stop sit in TunePressFor, ahead
        // of the tuner type, deciding from the one read its caller passed in;
        // TuneKeyCarrierPress is the start alone.
        string gate = Body(main, "private void TunePressFor(bool ours)");
        // Repair round 5: live is our carrier OR the tuner latch, both read once.
        Assert.Contains(".Decide(TuneKeyClock(), isRepeat: _registryKeyIsRepeat, ours || latched, () => true)", gate);
        Assert.DoesNotContain("TuneAskedOrUp", gate);
        int stop = gate.IndexOf("case Radios.PressAct.Stop:", StringComparison.Ordinal);
        int nothing = gate.IndexOf("case Radios.PressAct.Nothing:", StringComparison.Ordinal);
        Assert.True(stop > 0 && nothing > stop);
        string stopArm = gate.Substring(stop, nothing - stop);
        Assert.Contains("TurnTuneCarrierOff();", stopArm);
        Assert.Contains("return;", stopArm);
        Assert.DoesNotContain("ToggleTuneCarrier", stopArm);
        Assert.True(nothing < gate.IndexOf("Radios.TunePress.For(", StringComparison.Ordinal), "the gate decides before the type is read");
        string press = Body(main, "private void TuneKeyCarrierPress()");
        Assert.Contains("try { ToggleTuneCarrier(); }", press);
        Assert.DoesNotContain("FlexTunerOn", press);
        Assert.DoesNotContain(".Decide(", press);

        // The radio's answer, "Tune on", queues behind the press's sentence
        // rather than cutting it, and only on this route.
        string answer = Body(main, "private void AnnounceTuneWhenTheRadioAnswers()");
        Assert.Contains("bool tuneOnInterrupts = !_tuneAnswerFollowsLead;", answer);
        Assert.Contains("(\"audio.tune.tune_on\"), VerbosityLevel.Terse, tuneOnInterrupts,", answer);   // its owner follows (repair round 2)
    }

    [Fact]
    public void The_tune_key_refuses_only_when_the_rule_says_no_tuner()
    {
        string keys = Source("JJFlexWpf", "KeyCommands.cs");
        int at = keys.IndexOf("private void ATUTuneHandler()", StringComparison.Ordinal);
        string body = keys.Substring(at, keys.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
        Assert.Contains("Radios.TunePress.For(rig.HasATU, rig.FlexTunerType) == Radios.TunePressOutcome.NoTuner", body);
        Assert.DoesNotContain("if (!rig.HasATU)", body);
    }
}
