using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The tune key (Ctrl+T) out of automatic type is the tune carrier's own key
/// (#1127, batch 12 repair round 1): a second press turns off the carrier the
/// first started, and a held key keys once and never flutters.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, an unconnected <see cref="FlexBase"/>
/// whose writes go nowhere, and a window built as an uninitialized object, as
/// <see cref="ZeroPowerDoorsTests"/> builds it. What a press asked for is read
/// off the rig's own record of its tune writes (<see cref="FlexBase.TuneAskedOrUp"/>
/// and the request it notes before queueing). The parts of the toggle's
/// feedback this window lacks (the Tune button, the tune reminder) throw after
/// the write, so a press is made through <see cref="Press"/>. Whether the radio
/// keys, and what is heard, are bench steps.
/// </remarks>
[Collection("TuneDoors")]
public sealed class TuneKeyCarrierTests : IDisposable
{
    private const string Tune = "This rig's tune power is at 0 watts. Set your tune power first.";
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityWas;
    private bool _tuneAtZero;
    private long _now = 1_000_000;

    public TuneKeyCarrierTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.AtZeroOverride = (rig, tune) => ReferenceEquals(rig, _rig) && tune && _tuneAtZero;
        ZeroPowerDoors.Clock = () => _now;
        MainWindow.TuneKeyClock = () => _now;
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
    }

    public void Dispose()
    {
        MainWindow.TuneKeyClock = () => Environment.TickCount64;
        ZeroPowerDoors.AtZeroOverride = null;
        ZeroPowerDoors.Clock = () => Environment.TickCount64;
        ZeroPowerDoors.Forget();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityWas;
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, null);
        try { _rig.Dispose(); } catch { }
    }

    private void RadioFor()
    {
        var field = typeof(FlexBase).GetField("theRadio", Any)!;
        field.SetValue(_rig, Activator.CreateInstance(field.FieldType, Any, null, new object[] { false }, null)!);
    }

    private MainWindow Window()
    {
        var main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetField("_pttController", Any)!.SetValue(main, _controller);
        typeof(MainWindow).GetField("_radioPowerOn", Any)!.SetValue(main, true);
        typeof(MainWindow).GetProperty("RigControl", Any)!.SetValue(main, _rig);
        return main;
    }

    /// <summary>One key-down of the tune key. The feedback may reach parts this window lacks, after the write.</summary>
    private static void Press(MainWindow main) => Record.Exception(main.StartATUTuneCycle);

    /// <summary>
    /// The toggle's own 500 ms debounce, spent: the worst case, so that what
    /// holds a held key is shown to be the press gate and not the debounce.
    /// </summary>
    private static void DebounceSpent(MainWindow main) =>
        typeof(MainWindow).GetField("_lastTuneToggleTicks", Any)!.SetValue(main, 0L);

    /// <summary>The last tune write asked: true an ON, false an OFF (noted before the write is queued).</summary>
    private bool LastWriteWasOn() => (bool)typeof(FlexBase).GetField("_tuneRequested", Any)!.GetValue(_rig)!;

    private bool TuneCarrierNoted() => (bool)typeof(PttSafetyController).GetField("_tuneCarrierOn", Any)!.GetValue(_controller)!;

    private static List<string> Said(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Record.Exception(press);
        return ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).Reverse().ToList();
    }

    private void ManualTypeWithNoAtu()
    {
        // The rig's type from its constructor, and no ATU: the carrier outcome.
        Assert.Equal(FlexBase.FlexTunerTypes.manual, _rig.FlexTunerType);
        Assert.Equal(TunePressOutcome.TuneCarrier,
            TunePress.For(_rig.MyCaps.HasCap(RigCaps.Caps.ATGet), _rig.FlexTunerType));
    }

    /// <summary>
    /// Opus's B1: press, press again. The second press is the natural way to
    /// stop the carrier the first started, and it now writes the carrier off,
    /// where it used to find the tuner latch set, say no new request was sent,
    /// and leave the carrier on the air.
    /// </summary>
    [Fact]
    public void A_second_press_turns_off_the_carrier_the_first_started()
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();

        Press(main);
        Assert.True(_rig.TuneAskedOrUp, "the first press asks for a tune carrier");
        Assert.True(LastWriteWasOn());
        Assert.True(TuneCarrierNoted(), "through the toggle, which tells the transmit episode");
        Assert.False(_rig.FlexTunerOn, "the carrier is the toggle's, not the tuner latch's");

        _now += 400;                     // a quick, deliberate second press
        var said = Said(() => main.StartATUTuneCycle());
        Assert.False(_rig.TuneAskedOrUp, "the second press writes the carrier off");
        Assert.False(LastWriteWasOn());
        Assert.False(TuneCarrierNoted());
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_already_tuning"), said);
    }

    /// <summary>
    /// A held key, delivered as JAWS delivers one (down/up pairs about 250 ms
    /// apart, the first at the keyboard's repeat delay) and as NVDA does
    /// (repeat key-downs about 33 ms apart after the delay; this path cannot
    /// tell a repeat from a press). It keys once; the first pair or repeat
    /// turns the carrier off; nothing after that keys it again.
    /// </summary>
    [Theory]
    [InlineData(512, 250)]   // JAWS's pairs
    [InlineData(500, 33)]    // NVDA's repeats
    [InlineData(1000, 600)]  // the slowest repeat delay, pairs spaced past the toggle's debounce
    public void A_held_key_keys_once_and_never_flutters(int firstRepeatMs, int spacingMs)
    {
        RadioFor();
        ManualTypeWithNoAtu();
        var main = Window();

        var states = new List<bool>();
        long start = _now;
        for (long t = 0; t < 6000; t = t == 0 ? firstRepeatMs : t + spacingMs)
        {
            _now = start + t;
            DebounceSpent(main);
            Press(main);
            states.Add(_rig.TuneAskedOrUp);
        }

        int keyed = states.Where((up, i) => up && (i == 0 || !states[i - 1])).Count();
        Assert.Equal(1, keyed);
        Assert.True(states[0], "the press keyed");
        Assert.All(states.Skip(1), up => Assert.False(up));
    }

    /// <summary>
    /// The positive control for the held key: once the key has rested past the
    /// gate's window, a new press keys again. The gate does not simply refuse.
    /// </summary>
    [Fact]
    public void After_the_key_rests_a_new_press_keys_again()
    {
        RadioFor();
        var main = Window();
        Press(main);                                   // on
        _now += 300;
        Press(main);                                   // off
        Assert.False(_rig.TuneAskedOrUp);

        _now += FreshPressGate.SameChordWindowMs + 1;
        DebounceSpent(main);
        Press(main);
        Assert.True(_rig.TuneAskedOrUp, "a rested press starts the carrier again");
    }

    /// <summary>
    /// A start the toggle refuses (here tune power at zero) says the refusal
    /// and nothing about a carrier: the press's own sentence is said only once
    /// the toggle has asked.
    /// </summary>
    [Fact]
    public void A_refused_start_says_the_refusal_and_never_that_a_carrier_was_requested()
    {
        RadioFor();
        var main = Window();
        _tuneAtZero = true;
        var said = Said(() => main.StartATUTuneCycle());
        Assert.False(_rig.TuneAskedOrUp);
        Assert.Contains(Tune, said);
        Assert.DoesNotContain(Lexicon.Get("audio.tune.tune_carrier_sent"), said);
    }
}
