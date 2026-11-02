using System.IO;
using NAudio.Wave;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #408: asking the alert channel for the device it already holds does nothing,
/// and every case where the open is really needed still opens.
/// </summary>
/// <remarks>
/// <b>Windowless and silent:</b> an MTA thread, and no channel is opened. The
/// rule is a pure function, so no device is touched. That the rule is in the
/// path is pinned from source below.
/// </remarks>
public sealed class AlertDeviceGuardTests
{
    private static string Player()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.True(d != null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(d!.FullName, "JJFlexWpf", "EarconPlayer.cs"));
    }

    [Theory]
    [InlineData(-1)]    // Windows Default, what most operators run
    [InlineData(2)]
    public void The_device_held_and_playing_is_left_alone(int device)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(EarconPlayer.DeviceAlreadyHeld(device, device, PlaybackState.Playing));
    }

    [Fact]
    public void A_different_device_opens()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.False(EarconPlayer.DeviceAlreadyHeld(3, 2, PlaybackState.Playing));
    }

    [Fact]
    public void A_channel_that_never_opened_opens()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.False(EarconPlayer.DeviceAlreadyHeld(-1, -1, null));
    }

    [Theory]
    [InlineData(PlaybackState.Stopped)]   // the device failed, or was unplugged
    [InlineData(PlaybackState.Paused)]
    public void A_device_that_is_not_playing_is_rebuilt_even_when_the_number_matches(PlaybackState state)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.False(EarconPlayer.DeviceAlreadyHeld(-1, -1, state));
    }

    [Fact]
    public void After_falling_back_to_the_default_the_failed_device_is_tried_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // The fallback recorded -1, so the saved device does not read as held.
        Assert.False(EarconPlayer.DeviceAlreadyHeld(2, -1, PlaybackState.Playing));
    }

    [Fact]
    public void SetDevice_asks_the_rule_and_a_new_buffer_goes_around_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string player = Player();

        int set = player.IndexOf("public void SetDevice(int deviceNumber)", StringComparison.Ordinal);
        int guard = player.IndexOf("if (DeviceAlreadyHeld(deviceNumber, _deviceNumber, _waveOut?.PlaybackState)) return;", set, StringComparison.Ordinal);
        int open = player.IndexOf("OpenDevice(deviceNumber);", set, StringComparison.Ordinal);
        Assert.True(set > 0 && guard > set && open > guard, "the guard comes first in SetDevice");

        // The same device, new buffers: this is the one reopen the guard must
        // not swallow, or the Alert sound delay setting would do nothing live.
        int rebuffer = player.IndexOf("public void Rebuffer(int bufferMs, int bufferCount)", StringComparison.Ordinal);
        int dispose = player.IndexOf("public void Dispose()", rebuffer, StringComparison.Ordinal);
        string body = player.Substring(rebuffer, dispose - rebuffer);
        Assert.Contains("OpenDevice(_deviceNumber);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDevice(", body.Replace("SetDevice's", ""), StringComparison.Ordinal);
    }
}
