using System;
using System.Reflection;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The spoken mic peak and integrated loudness describe THIS transmission,
/// whoever keyed it (#651).
/// </summary>
/// <remarks>
/// Until #651 they restarted only when our own push to talk or lock keyed, so
/// a key-down from a foot switch, the radio's own PTT, CW or another client
/// spoke an earlier transmission's peak. The radio's rising edge now restarts
/// them, except when it is only the echo of our own key, which already did.
/// These drive the production Mox path (noteRadioMox) on a real FlexBase and
/// read the SC_MIC election the spoken figure comes from.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class AudioSampleKeyDownTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(AudioSampleKeyDownTests));
    public void Dispose() => _scope.Dispose();

    private static readonly MethodInfo NoteRadioMox =
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo ScMicElection =
        typeof(FlexBase).GetField("_scMicElection", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo OwnStart =
        typeof(FlexBase).GetField("_ownAudioSampleStartMs", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void Mox(FlexBase rig, bool on) => NoteRadioMox.Invoke(rig, new object[] { on });

    /// <summary>One SC_MIC copy on the rig that delivers <paramref name="db"/>.</summary>
    private static void MicSample(FlexBase rig, object copy, float db)
    {
        var election = (TransmitMeterElection)ScMicElection.GetValue(rig)!;
        if (election.CandidateCount == 0) election.Register(copy, "SC_MIC test copy", 0);
        election.Report(copy, db, Environment.TickCount);
    }

    [Fact]
    public void The_fixture_reaches_the_production_members()
    {
        Assert.NotNull(NoteRadioMox);
        Assert.NotNull(ScMicElection);
        Assert.NotNull(OwnStart);
    }

    [Fact]
    public void A_key_down_from_outside_starts_a_fresh_peak()
    {
        using var f = new RigOnVendorRadio("audio-sample-outside");
        object copy = new object();

        // An earlier transmission left a loud peak behind.
        MicSample(f.Rig, copy, -3f);
        Assert.True(f.Rig.ScMicReportedSinceReset, "positive control: the sample reached the election");
        Mox(f.Rig, false);

        // A foot switch keys the radio: nothing of ours ran.
        Mox(f.Rig, true);

        Assert.False(f.Rig.ScMicReportedSinceReset,
            "the radio's key-down must restart the sample; the old transmission's peak is still being held");
        MicSample(f.Rig, copy, -20f);
        Assert.Equal(-20f, f.Rig.ScMicMaxDb);
    }

    [Fact]
    public void Our_own_key_is_not_reset_again_by_its_echo()
    {
        using var f = new RigOnVendorRadio("audio-sample-own");
        object copy = new object();

        // Our push to talk keys: it restarts the sample at the press...
        f.Rig.ResetScMicMax();
        // ...the operator's first syllable arrives...
        MicSample(f.Rig, copy, -6f);
        // ...and only then does the radio report the key-down.
        Mox(f.Rig, true);

        Assert.True(f.Rig.ScMicReportedSinceReset, "the echo of our own key threw away the first syllable");
        Assert.Equal(-6f, f.Rig.ScMicMaxDb);
    }

    [Fact]
    public void An_own_reset_is_claimed_by_one_key_down_only()
    {
        using var f = new RigOnVendorRadio("audio-sample-claimed");
        object copy = new object();

        f.Rig.ResetScMicMax();
        Mox(f.Rig, true);      // our transmission's echo claims it
        MicSample(f.Rig, copy, -4f);
        Mox(f.Rig, false);

        // The next key-down is a foot switch, still inside the echo window.
        Mox(f.Rig, true);
        Assert.False(f.Rig.ScMicReportedSinceReset,
            "an own reset already claimed by an earlier key-down kept this transmission's peak from restarting");
    }

    [Theory]
    [InlineData(0L, 10_000L, true)]            // nothing of ours pending: a key-down from outside
    [InlineData(9_700L, 10_000L, false)]       // our key 300 ms ago: its echo
    [InlineData(7_000L, 10_000L, false)]       // the far edge of the window still counts
    [InlineData(6_999L, 10_000L, true)]        // past it: some earlier transmission's
    [InlineData(10_500L, 10_000L, true)]       // a clock that went backwards is not trusted
    public void The_rule(long ownStartMs, long nowMs, bool restart)
    {
        Assert.Equal(3000L, FlexBase.OwnAudioSampleEchoWindowMs);
        Assert.Equal(restart, FlexBase.ShouldRestartAudioSampleOnRadioKeyDown(ownStartMs, nowMs));
    }
}
