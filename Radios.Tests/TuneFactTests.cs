using System;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Whether a tune is up, from what the radio reported (#860). FlexLib's
/// <c>Radio.TXTune</c> is our own request written back, so these build the
/// states that tell a request from a carrier.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TuneFactTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint Ours = 0x10u;
    private const uint Theirs = 0x20u;

    private readonly FlexBase _rig;
    private readonly Radio _radio;
    private long _now = 100_000;

    public TuneFactTests()
    {
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
        typeof(Radio).GetField("_model", Private)!.SetValue(_radio, "FLEX-8600");
        _rig.theRadio = _radio;
        typeof(FlexBase).GetField("clientHandle", Private | BindingFlags.Public)!.SetValue(_rig, Ours);
        _rig.TuneFactClock = () => _now;
    }

    public void Dispose()
    {
        _rig.theRadio = null;
        try { _rig.Dispose(); } catch { }
    }

    private void Put(string field, object value) => typeof(Radio).GetField(field, Private)!.SetValue(_radio, value);

    private void RadioIs(InterlockState state, uint txClient = Ours, PTTSource source = PTTSource.SW)
    {
        Put("_interlockState", state);
        Put("_txClientHandle", txClient);
        Put("_pttSource", source);
    }

    private void FlagUp() => Put("_txTune", true);

    private void Asked() => _rig.TxTune = true;

    // ── The reminder's fact ─────────────────────────────────────────────

    [Theory]
    [InlineData(0u)]     // the radio names no client
    [InlineData(Ours)]
    public void A_requested_tune_the_radio_is_not_transmitting_is_no_carrier(uint handle)
    {
        Asked();
        FlagUp();
        RadioIs(InterlockState.Ready, handle);
        Assert.False(_rig.TuneCarrierUp);
    }

    [Fact]
    public void A_tune_the_radio_reports_transmitting_is_a_carrier()
    {
        Asked();
        FlagUp();
        RadioIs(InterlockState.Transmitting, Ours, PTTSource.TUNE);
        Assert.True(_rig.TuneCarrierUp);    // the positive control for the two above
    }

    [Fact]
    public void Another_clients_transmission_is_not_our_carrier()
    {
        Asked();
        FlagUp();
        RadioIs(InterlockState.Transmitting, Theirs, PTTSource.TUNE);
        Assert.False(_rig.TuneCarrierUp);
    }

    [Fact]
    public void A_flag_left_up_over_someone_elses_key_is_not_this_tune()
    {
        // Already on the air at the press, on a source that does not say tune.
        RadioIs(InterlockState.Transmitting, Ours, PTTSource.SW);
        Asked();
        FlagUp();
        Assert.False(_rig.TuneCarrierUp);
    }

    [Fact]
    public void Tune_off_is_no_carrier_whatever_the_radio_is_doing()
    {
        RadioIs(InterlockState.Transmitting, Ours, PTTSource.TUNE);
        Assert.False(_rig.TuneCarrierUp);
    }

    // ── The doors' fact ─────────────────────────────────────────────────

    [Fact]
    public void A_tune_counts_for_the_doors_from_the_moment_it_is_asked_for()
    {
        RadioIs(InterlockState.Ready);
        Assert.False(_rig.TuningNow);       // positive control
        Asked();                            // queued: FlexLib has not run it
        Assert.False(_radio.TXTune);
        Assert.True(_rig.TuningNow);
    }

    [Fact]
    public void A_denied_tune_stops_counting_once_the_radio_has_had_time_to_answer()
    {
        Asked();
        FlagUp();
        RadioIs(InterlockState.NotReady);
        Assert.True(_rig.TuningNow);
        _now += TuneAnswer.WaitMs;
        Assert.False(_rig.TuningNow);       // the flag is still up, and an ordinary send goes
        Assert.True(_radio.TXTune);
    }

    [Fact]
    public void A_keyed_tune_outlasts_the_wait()
    {
        Asked();
        FlagUp();
        RadioIs(InterlockState.Transmitting, Ours, PTTSource.TUNE);
        _now += TuneAnswer.WaitMs * 10;
        Assert.True(_rig.TuningNow);
    }

    [Fact]
    public void Asking_tune_off_ends_it_at_once()
    {
        Asked();
        _rig.TxTune = false;
        Assert.False(_rig.TuningNow);
    }

    [Fact]
    public void Another_clients_tune_does_not_refuse_our_send()
    {
        FlagUp();
        Put("_atuTuneStatus", ATUTuneStatus.InProgress);
        RadioIs(InterlockState.Transmitting, Theirs, PTTSource.TUNE);
        Assert.False(_rig.TuningNow);
    }

    [Fact]
    public void An_ATU_cycle_of_ours_counts()
    {
        RadioIs(InterlockState.Transmitting, Ours, PTTSource.SW);
        Put("_atuTuneStatus", ATUTuneStatus.InProgress);
        Assert.True(_rig.TuningNow);
    }

    // ── Who a carrier belongs to ────────────────────────────────────────

    [Fact]
    public void A_box_sends_arm_does_not_own_a_tune()
    {
        using (TransmitKillSwitch.Arm(_rig, "a box send", escapeStops: false))
        {
            Assert.True(TransmitKillSwitch.RaiseCarrier(_rig, TransmitKillSwitch.Carrier.Mox));
            Assert.True(TransmitKillSwitch.Armed);
            Assert.False(TransmitKillSwitch.HoldsTuneCarrier(_rig));
        }
    }

    [Fact]
    public void A_check_owns_the_tune_it_raised_on_its_own_rig_until_it_drops_it()
    {
        var other = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        try
        {
            using (TransmitKillSwitch.Arm(_rig, "the transmitter check"))
            {
                Assert.True(TransmitKillSwitch.RaiseCarrier(_rig, TransmitKillSwitch.Carrier.Tune));
                Assert.True(TransmitKillSwitch.HoldsTuneCarrier(_rig));
                Assert.False(TransmitKillSwitch.HoldsTuneCarrier(other));
                TransmitKillSwitch.DropCarrier(_rig, TransmitKillSwitch.Carrier.Tune);
                Assert.False(TransmitKillSwitch.HoldsTuneCarrier(_rig));
            }
        }
        finally { try { other.Dispose(); } catch { } }
    }

    [Fact]
    public void Disarming_forgets_the_owner_even_if_nothing_dropped_the_carrier()
    {
        using (TransmitKillSwitch.Arm(_rig, "the transmitter check"))
            Assert.True(TransmitKillSwitch.RaiseCarrier(_rig, TransmitKillSwitch.Carrier.Tune));
        Assert.False(TransmitKillSwitch.HoldsTuneCarrier(_rig));
    }
}
