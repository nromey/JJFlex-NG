using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The one countdown's JJFlexWpf side (#835, fix round 2026-10-28): the Fix
/// tool's earcon figure reads the operator's Countdown settings, and the
/// settings are saved and loaded with the audio config.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> Every test runs on the test runner's MTA thread
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>), where WPF
/// throws on constructing any element, so nothing here can put a window on the
/// operator's desktop. It builds step lists and serialises a plain config
/// object; no sound is played (the step builder renders nothing).
/// </para>
/// <para>
/// <b>What it cannot see:</b> the sound itself, and the Fix tool's boundary
/// keying at the derived moment. The figure and the derived numbers are what
/// is pinned; the bench hears it.
/// </para>
/// </remarks>
public sealed class CountdownWiringTests
{
    [Fact]
    public void The_fix_tool_figure_follows_the_countdown_settings()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var original = CountdownSettings.Current;
        try
        {
            CountdownSettings.Current = new CountdownSettings { Seconds = 7, PipHz = 500, PipMs = 200 };
            var steps = EarconPlayer.CountdownSteps(transmit: true);

            var tones = steps.Where(s => s.freq > 0).ToArray();
            Assert.Equal(7, tones.Length);                       // six pips and the long tone
            Assert.All(tones.Take(6), t => Assert.Equal((500, 200), t));
            Assert.Equal((1000, 400), tones[^1]);                // the octave, twice the pip
            Assert.All(steps.Where(s => s.freq == 0), g => Assert.Equal(800, g.ms)); // one a second

            Assert.Equal(6, EarconPlayer.CountdownCounts);
            Assert.Equal(5000, EarconPlayer.CountdownLastDitAtMs); // the Fix tool's key-up
            Assert.Equal(6000, EarconPlayer.CountdownLandingAtMs);
            Assert.Equal(6 * 1000 + 400, EarconPlayer.CountdownDurationMs(transmit: true));
            Assert.Equal(6 * 1000 + 400, EarconPlayer.CountdownDurationMs(transmit: false));

            // The defaults: five seconds is four pips and the long tone.
            CountdownSettings.Current = new CountdownSettings();
            Assert.Equal(4, EarconPlayer.CountdownCounts);
            Assert.Equal(3000, EarconPlayer.CountdownLastDitAtMs);

            // The shortest: one pip, at zero. The Fix tool keys on the landing then.
            CountdownSettings.Current = new CountdownSettings { Seconds = 2 };
            Assert.Equal(0, EarconPlayer.CountdownLastDitAtMs);
            Assert.Equal(1000, EarconPlayer.CountdownLandingAtMs);
        }
        finally { CountdownSettings.Current = original; }
    }

    [Fact]
    public void The_countdown_is_saved_and_loaded_with_the_audio_settings()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var config = new AudioOutputConfig
        {
            Countdown = new CountdownSettings { Enabled = false, Seconds = 9, PipHz = 1234, PipMs = 75, LeadIn = false },
        };
        var back = RoundTrip(config);
        Assert.False(back.Countdown.Enabled);
        Assert.Equal(9, back.Countdown.Seconds);
        Assert.Equal(1234, back.Countdown.PipHz);
        Assert.Equal(75, back.Countdown.PipMs);
        Assert.False(back.Countdown.LeadIn);

        // A file written before the countdown existed loads the ruled defaults.
        var older = (AudioOutputConfig)new XmlSerializer(typeof(AudioOutputConfig))
            .Deserialize(new StringReader("<AudioOutputConfig />"))!;
        Assert.True(older.Countdown.Enabled);
        Assert.Equal(CountdownSettings.DefaultSeconds, older.Countdown.Seconds);
        Assert.Equal(CountdownSettings.DefaultPipHz, older.Countdown.PipHz);
        Assert.True(older.Countdown.LeadIn);
    }

    private static AudioOutputConfig RoundTrip(AudioOutputConfig config)
    {
        var xs = new XmlSerializer(typeof(AudioOutputConfig));
        using var w = new StringWriter();
        xs.Serialize(w, config);
        using var r = new StringReader(w.ToString());
        return (AudioOutputConfig)xs.Deserialize(r)!;
    }
}
