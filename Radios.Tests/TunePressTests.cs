using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Flex.Smoothlake.FlexLib;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1127, ruled by Noel 2026-11-12 08:05 (J4): keep Jim's design (after a Fail
/// the ATU is bypassed and taken out of automatic), and when the ATU has
/// failed, is bypassed, or the radio has none, the tune key sends a plain tune
/// carrier and SAYS so, never "ATU tuning". Automatic type is unchanged.
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
    [InlineData(true, FlexBase.FlexTunerTypes.manual, TunePressOutcome.TuneCarrier)] // after a Fail or a bypass
    [InlineData(false, FlexBase.FlexTunerTypes.manual, TunePressOutcome.TuneCarrier)] // no ATU (refused before)
    [InlineData(false, FlexBase.FlexTunerTypes.auto, TunePressOutcome.NoTuner)]     // refused, as before
    [InlineData(false, FlexBase.FlexTunerTypes.none, TunePressOutcome.NoTuner)]     // refused, as before
    [InlineData(true, FlexBase.FlexTunerTypes.none, TunePressOutcome.TunerOff)]     // not ruled; as before
    public void What_the_tune_key_does_in_each_tuner_state(bool hasAtu, FlexBase.FlexTunerTypes type, TunePressOutcome expected) =>
        Assert.Equal(expected, TunePress.For(hasAtu, type));

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
    /// After a failed tune the ATU is bypassed and the type leaves automatic
    /// (Jim's design, kept), so the next press is a plain tune carrier: the
    /// rule says TuneCarrier and the radio is sent "transmit tune 1", not
    /// "atu start".
    /// </summary>
    [Fact]
    public void After_a_failed_tune_the_next_press_is_a_plain_tune_carrier()
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
        Assert.True(Sent(c, "atu bypass") > 0, "Jim's design: a Fail bypasses the ATU");

        Assert.Equal(TunePressOutcome.TuneCarrier, TunePress.For(true, c.F.Rig.FlexTunerType));
        int keys = Sent(c, "transmit tune 1");
        c.F.Rig.FlexTunerOn = true;   // the next press
        RunQueue(c);
        Assert.True(Sent(c, "transmit tune 1") > keys, "the press keys a plain tune carrier");
        Assert.Equal(starts, Sent(c, "atu start"));
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
        Assert.False(c.F.Rig.MyCaps.HasCap(RigCaps.Caps.ATGet), "positive control: no ATU capability");
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);
        Assert.Equal(TunePressOutcome.TuneCarrier, TunePress.For(c.F.Rig.MyCaps.HasCap(RigCaps.Caps.ATGet), c.F.Rig.FlexTunerType));

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
        int at = main.IndexOf("public void StartATUTuneCycle()", StringComparison.Ordinal);
        string body = main.Substring(at, main.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
        Assert.Contains("Radios.TunePress.For(RigControl.MyCaps.HasCap(Radios.RigCaps.Caps.ATGet), RigControl.FlexTunerType)", body);
        Assert.Contains("if (outcome == Radios.TunePressOutcome.NoTuner)", body);
        Assert.DoesNotContain("if (!RigControl.MyCaps.HasCap(Radios.RigCaps.Caps.ATGet))", body);
        Assert.Matches(new Regex(@"Lexicon\.Get\(outcome == Radios\.TunePressOutcome\.TuneCarrier\s*\?\s*""audio\.tune\.tune_carrier_sent""[^:]*:\s*""audio\.tune\.atu_tuning""\)"), body);
        Assert.Contains("subject: Radios.Speech.SpeechSubject.TunePressSent", body);
        Assert.False(string.IsNullOrWhiteSpace(Lexicon.Get("audio.tune.tune_carrier_sent")));
        Assert.DoesNotContain("ATU", Lexicon.Get("audio.tune.tune_carrier_sent"), StringComparison.Ordinal);
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
