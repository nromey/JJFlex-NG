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

            // The defaults: four seconds is three pips and the long tone (2026-10-29).
            CountdownSettings.Current = new CountdownSettings();
            Assert.Equal(3, EarconPlayer.CountdownCounts);
            Assert.Equal(2000, EarconPlayer.CountdownLastDitAtMs);

            // The shortest: one pip, at zero. The Fix tool keys on the landing then.
            CountdownSettings.Current = new CountdownSettings { Seconds = 2 };
            Assert.Equal(0, EarconPlayer.CountdownLastDitAtMs);
            Assert.Equal(1000, EarconPlayer.CountdownLandingAtMs);
        }
        finally { CountdownSettings.Current = original; }
    }

    [Fact]
    public void The_fix_tool_asks_the_one_countdown_switch()
    {
        // Fix round 1030 P2 (#835, ruled 2026-10-29 23:59): the switch covers
        // the Fix tool too. Only the answer is read here; the tones are never
        // called, so a broken guard could not sound on the desk.
        NoiseReductionPerChannelTests.AssertWindowless();
        var original = CountdownSettings.Current;
        try
        {
            CountdownSettings.Current = new CountdownSettings { Enabled = false };
            Assert.False(JJFlexWpf.Dialogs.FixerCountdown.On);
            CountdownSettings.Current = new CountdownSettings();
            Assert.True(JJFlexWpf.Dialogs.FixerCountdown.On);
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

    [Fact]
    public void Every_pip_sound_is_a_CW_waveform_and_the_countdown_plays_it()
    {
        // Ruled 2026-10-29 00:51: the six existing CW waveforms, the Fix tool
        // following the choice. One definition of each spectrum: the countdown
        // resolves the CW set's, it does not declare its own.
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(EarconVoices.CwWaveforms.Select(w => w.Id), CountdownSettings.PipSounds);

        foreach (var id in CountdownSettings.PipSounds)
        {
            var voice = EarconPlayer.CountdownVoiceFor(id);
            Assert.NotNull(voice);
            var cw = EarconVoices.ResolveCwWaveform(id);
            Assert.Equal(id, cw.Id); // resolved, not fallen back
            if (cw.Voice != null)
                Assert.Same(cw.Voice, voice);
            else
                Assert.Equal(new[] { 1f }, voice.Partials); // Sine: one pure partial, not some other voice
        }

        // The live voice follows the operator's committed choice, which is
        // what the Fix tool's figure plays.
        var original = CountdownSettings.Current;
        try
        {
            CountdownSettings.Current = new CountdownSettings { PipSound = "Bell" };
            Assert.Same(EarconVoices.ResolveCwWaveform("Bell").Voice, EarconPlayer.CountdownVoice);
            CountdownSettings.Current = new CountdownSettings();
            Assert.Same(EarconVoices.ResolveCwWaveform("Hollow").Voice, EarconPlayer.CountdownVoice);
        }
        finally { CountdownSettings.Current = original; }
    }

    [Fact]
    public void The_pip_sound_is_saved_and_loaded_with_the_audio_settings()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var back = RoundTrip(new AudioOutputConfig { Countdown = new CountdownSettings { PipSound = "Reed" } });
        Assert.Equal("Reed", back.Countdown.PipSound);

        var older = (AudioOutputConfig)new XmlSerializer(typeof(AudioOutputConfig))
            .Deserialize(new StringReader("<AudioOutputConfig><Countdown><Seconds>6</Seconds></Countdown></AudioOutputConfig>"))!;
        Assert.Equal(CountdownSettings.DefaultPipSound, older.Countdown.PipSound);
    }

    /// <summary>
    /// The default moved from 5 beats to 4 (Noel, 2026-10-29 20:46). A file
    /// from before that holds 5 because every save wrote the default, so it
    /// moves to 4, once. Any other stored length was chosen and stays, and a
    /// file already brought forward keeps whatever it says, 5 included:
    /// "We still want to save the value regardless the default" (23:11).
    /// </summary>
    [Fact]
    public void The_old_default_of_five_moves_to_four_once_and_a_chosen_length_stays()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(4, CountdownSettings.DefaultSeconds);

        // Noel's own file on 2026-10-29: <Seconds>5</Seconds>, no version.
        var old = Read("<AudioOutputConfig><Countdown><Seconds>5</Seconds></Countdown></AudioOutputConfig>");
        Assert.Equal(0, old.CountdownDefaultsVersion);
        old.UpgradeCountdownDefaults();
        Assert.Equal(4, old.Countdown.Seconds);
        Assert.Equal(CountdownSettings.CurrentDefaultsVersion, old.CountdownDefaultsVersion);

        // A length someone chose before the move is theirs.
        var chose = Read("<AudioOutputConfig><Countdown><Seconds>7</Seconds></Countdown></AudioOutputConfig>");
        chose.UpgradeCountdownDefaults();
        Assert.Equal(7, chose.Countdown.Seconds);

        // After the move, 5 is a choice, and it is saved and loaded as 5.
        // (Every save after the move is stamped current: CaptureFromEngine.)
        var now = new AudioOutputConfig
        {
            CountdownDefaultsVersion = CountdownSettings.CurrentDefaultsVersion,
            Countdown = new CountdownSettings { Seconds = 5 },
        };
        var back = RoundTrip(now);
        Assert.Equal(CountdownSettings.CurrentDefaultsVersion, back.CountdownDefaultsVersion);
        back.UpgradeCountdownDefaults();
        Assert.Equal(5, back.Countdown.Seconds);
        back.UpgradeCountdownDefaults();                    // and again, still 5
        Assert.Equal(5, back.Countdown.Seconds);

        // And a config built with no version at all (the no-folder fallback in
        // the menu bar) is stamped when it is written, so a 5 chosen there survives.
        string dir = Path.Combine(Path.GetTempPath(), "jjf-countdown-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var bare = new AudioOutputConfig { Countdown = new CountdownSettings { Seconds = 5 } };
            bare.Save(dir);
            var loaded = AudioOutputConfig.Load(dir);
            Assert.Equal(5, loaded.Countdown.Seconds);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static AudioOutputConfig Read(string xml) =>
        (AudioOutputConfig)new XmlSerializer(typeof(AudioOutputConfig)).Deserialize(new StringReader(xml))!;

    private static AudioOutputConfig RoundTrip(AudioOutputConfig config)
    {
        var xs = new XmlSerializer(typeof(AudioOutputConfig));
        using var w = new StringWriter();
        xs.Serialize(w, config);
        using var r = new StringReader(w.ToString());
        return (AudioOutputConfig)xs.Deserialize(r)!;
    }
}
