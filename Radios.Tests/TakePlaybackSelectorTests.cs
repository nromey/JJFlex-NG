using System;
using Radios;
using Xunit;

namespace Radios.Tests;

public sealed class TakePlaybackSelectorTests
{
    [Theory]
    [InlineData(AudioCheckListenMethods.RecordPlayback, TakePlaybackChoice.Slice)]
    [InlineData(AudioCheckListenMethods.OffAir, TakePlaybackChoice.OffAir)]
    public void ChosenListenMethodNeverSubstitutesAnotherKind(AudioCheckListenMethods method, TakePlaybackChoice expected)
    {
        Assert.Equal(expected, TakePlaybackSelector.Choose(method, true, true));
        Assert.Equal(TakePlaybackChoice.None, TakePlaybackSelector.Choose(method,
            method != AudioCheckListenMethods.RecordPlayback,
            method != AudioCheckListenMethods.OffAir));
    }

    [Fact]
    public void MonitorOffersNoTakePlayback()
    {
        Assert.Equal(TakePlaybackChoice.None, TakePlaybackSelector.Choose(
            AudioCheckListenMethods.Monitor, true, true));
        Assert.Equal(TakePlaybackChoice.None, TakePlaybackSelector.Choose(
            AudioCheckListenMethods.Monitor, false, false));
    }

    [Fact]
    public void PlayLabelUsesTheSelectedTakeTimeAndLocalDay()
    {
        var now = new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Local);
        Assert.Equal("Play last take", TakePlaybackSelector.PlayLabel(now.AddHours(-1).ToUniversalTime(), now));
        Assert.Equal("Play 8:35 PM take", TakePlaybackSelector.PlayLabel(
            new DateTime(2026, 10, 24, 20, 35, 0, DateTimeKind.Local).ToUniversalTime(), now));
        Assert.Equal("Play October 23 take", TakePlaybackSelector.PlayLabel(
            new DateTime(2026, 10, 23, 20, 35, 0, DateTimeKind.Local).ToUniversalTime(), now));
        Assert.Equal("Play last take", TakePlaybackSelector.PlayLabel(null, now));
    }

    [Fact]
    public void PlayLabelRefreshesAcrossTheHourAndMidnight()
    {
        var made = new DateTime(2026, 10, 23, 22, 59, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("Play last take", TakePlaybackSelector.PlayLabel(made,
            new DateTime(2026, 10, 23, 23, 59, 0, DateTimeKind.Local)));
        Assert.Equal("Play October 23 take", TakePlaybackSelector.PlayLabel(made,
            new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Local)));
    }
}
