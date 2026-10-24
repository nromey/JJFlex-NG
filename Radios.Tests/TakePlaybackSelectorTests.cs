using System;
using Radios;
using Xunit;

namespace Radios.Tests;

public sealed class TakePlaybackSelectorTests
{
    private static readonly DateTime Yesterday = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = Yesterday.AddDays(1);

    [Theory]
    [InlineData(AudioCheckListenMethods.RecordPlayback, TakePlaybackChoice.Slice)]
    [InlineData(AudioCheckListenMethods.OffAir, TakePlaybackChoice.OffAir)]
    public void ChosenListenMethodNeverSubstitutesAnotherKind(AudioCheckListenMethods method, TakePlaybackChoice expected)
    {
        Assert.Equal(expected, TakePlaybackSelector.Choose(method, true, Yesterday, true, Today, true, Today));
        Assert.Equal(TakePlaybackChoice.None, TakePlaybackSelector.Choose(method,
            method != AudioCheckListenMethods.RecordPlayback, Yesterday,
            method != AudioCheckListenMethods.OffAir, Today, true, Today));
    }

    [Fact]
    public void MonitorChoosesNewestKnownTakeAcrossAllKinds()
    {
        Assert.Equal(TakePlaybackChoice.Slice, TakePlaybackSelector.Choose(
            AudioCheckListenMethods.Monitor, true, Today, true, Yesterday, true, Yesterday));
        Assert.Equal(TakePlaybackChoice.OffAir, TakePlaybackSelector.Choose(
            AudioCheckListenMethods.Monitor, true, Yesterday, true, Today, true, Yesterday));
        Assert.Equal(TakePlaybackChoice.Computer, TakePlaybackSelector.Choose(
            AudioCheckListenMethods.Monitor, true, Yesterday, true, Yesterday, true, Today));
    }

    [Fact]
    public void MonitorRefusesAnUnknowableComparisonButCanPlayItsOnlyTake()
    {
        Assert.Equal(TakePlaybackChoice.UnknownTime, TakePlaybackSelector.Choose(
            AudioCheckListenMethods.Monitor, true, null, false, null, true, Today));
        Assert.Equal(TakePlaybackChoice.Slice, TakePlaybackSelector.Choose(
            AudioCheckListenMethods.Monitor, true, null, false, null, false, null));
    }
}
