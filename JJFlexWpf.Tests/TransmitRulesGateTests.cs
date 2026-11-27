using System;
using System.Runtime.CompilerServices;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The app's own gate for the transmit rules, <see cref="FreqOutHandlers.JudgeTransmitHere"/>,
/// driven itself (repair round 1 of junk box batch 31). Every door test stands a
/// verdict in for the radio's judge delegate, so until this class no test reached
/// this method, and putting back its old early return (admit whenever the TX band
/// lockout is off) switched the 60 m rules off in the running app with every test
/// green. With the lockout off and "Enforce 60 meter transmit rules" on, which is
/// every US operator's default, 60 m is still judged here.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>),
/// a <see cref="MainWindow"/> built as an uninitialized object with only
/// <c>RigControl</c> set, and an unconnected <see cref="FlexBase"/>. The transmit
/// reading comes through the test-only <see cref="FreqOutHandlers.TransmitReadingForTests"/>,
/// because a radio object built for a test has no slices to read.
/// </remarks>
public sealed class TransmitRulesGateTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly FreqOutHandlers _handlers;
    private TransmitSliceReading? _reading;

    public TransmitRulesGateTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        main.RigControl = _rig;
        _handlers = new FreqOutHandlers(main)
        {
            // Every US operator's defaults: the lockout off, the 60 m rules on.
            License = new LicenseConfig { LicenseClass = HamBands.Bands.Licenses.extra, Country = "US" },
            GetRXFrequency = () => _reading?.Hz ?? 0UL,
        };
        _handlers.TransmitReadingForTests = () => (_reading, 100, 2800);
    }

    public void Dispose()
    {
        try { _rig.Dispose(); } catch { }
    }

    private TransmitVerdict At(ulong hz, string mode, bool sixtyMeterRule = true)
    {
        _reading = new TransmitSliceReading(hz, mode, 100, 2800) { TxFilterLow = 100, TxFilterHigh = 2900 };
        return _handlers.JudgeTransmitHere(sixtyMeterRule);
    }

    [Fact]
    public void The_defaults_are_the_lockout_off_and_the_60_metre_rules_on()
    {
        Assert.False(_handlers.License!.TxLockout);
        Assert.True(_handlers.License.EnforceTxRules);
    }

    [Fact]
    public void With_the_lockout_off_and_the_box_on_60_metres_between_the_channels_is_refused_as_outside()
    {
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Outside), At(5_340_000, "USB"));
        Assert.False(_handlers.CanTransmitHere());   // the yes or no the PTT controller asks
    }

    [Fact]
    public void Off_60_metres_nothing_is_refused()
    {
        Assert.True(At(14_200_000, "USB").IsAllowed);
        Assert.True(_handlers.CanTransmitHere());
        Assert.True(At(5_330_500, "USB").IsAllowed);   // positive control: a channel's own dial
    }

    [Fact]
    public void The_box_off_lets_60_metres_through()
    {
        _handlers.License!.EnforceTxRules = false;
        Assert.True(At(5_340_000, "USB").IsAllowed);
    }

    /// <summary>
    /// Repair round 2 (the Opus #1161 re-check's Noted 2): with the lockout off,
    /// the gate's early return admits the tuner's ask before the judge is reached,
    /// so the case below could not see the flag dropped on its way to the judge.
    /// With the lockout and the box both on, the judge is reached, and the
    /// tuner's ask must still leave the 60 m rule out.
    /// </summary>
    [Fact]
    public void With_the_lockout_and_the_box_on_the_tuner_s_ask_still_leaves_the_60_metre_rule_out()
    {
        _handlers.License!.TxLockout = true;
        _handlers.License.LicenseClass = HamBands.Bands.Licenses.general;
        // LSB at the mirrored dial fills Channel 1 exactly: the lockout admits it, the 60 m rule does not.
        Assert.True(At(5_333_500, "LSB", sixtyMeterRule: false).IsAllowed);
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.ChannelMode), At(5_333_500, "LSB"));
    }

    [Fact]
    public void The_antenna_tuner_s_ask_leaves_the_60_metre_rule_out()
    {
        Assert.True(At(5_340_000, "USB", sixtyMeterRule: false).IsAllowed);
        Assert.False(At(5_340_000, "USB").IsAllowed);   // positive control: the same place, the rule asked
    }
}
