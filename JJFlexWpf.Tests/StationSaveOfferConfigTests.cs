using System;
using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #759, round two: the disconnect offer to save the station is OFF unless the
/// operator turns it on, and a configuration keeps whatever it stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why off.</b> #499's settled design saves the station automatically, and
/// this question is the fallback for an operator who turns that save off. On by
/// default it would ask nearly every session. Round one built a default-on with
/// a one-time migration; round two backed both out, and these tests replace
/// round one's migration tests.
/// </para>
/// <para>
/// <b>A file round one's build wrote still loads.</b> It carries a
/// <c>StationSaveOfferVersion</c> element that no longer exists on the type.
/// XmlSerializer skips an unknown element, so the file loads and its stored
/// value stands - there is deliberately no second migration to undo the first.
/// </para>
/// <para>
/// Windowless: the config is plain data and XML, read from real files in a
/// scratch folder. Each test asserts it runs on an MTA thread, where WPF cannot
/// construct an element.
/// </para>
/// </remarks>
[Collection("StationSaveOfferStatic")]
public sealed class StationSaveOfferConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-759-" + Guid.NewGuid().ToString("N"));
    private readonly bool _savedStatic = Radios.FlexBase.OfferStationSaveOnDisconnect;

    public StationSaveOfferConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Radios.FlexBase.OfferStationSaveOnDisconnect = _savedStatic;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_dir, "audioConfig.xml");

    private void WriteRaw(string inner) =>
        File.WriteAllText(ConfigPath,
            "<?xml version=\"1.0\"?>\r\n<AudioOutputConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
            + "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" + inner + "</AudioOutputConfig>");

    [Fact]
    public void A_first_run_and_a_file_from_before_the_setting_both_come_out_off()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.False(new AudioOutputConfig().OfferStationSaveOnDisconnect);
        Assert.False(AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);

        WriteRaw("");
        Assert.False(AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);
    }

    [Fact]
    public void An_existing_false_stays_false_across_round_trips()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        WriteRaw("<OfferStationSaveOnDisconnect>false</OfferStationSaveOnDisconnect>");

        var config = AudioOutputConfig.Load(_dir);
        Assert.False(config.OfferStationSaveOnDisconnect);

        config.Save(_dir);
        var again = AudioOutputConfig.Load(_dir);
        Assert.False(again.OfferStationSaveOnDisconnect);
        again.Save(_dir);
        Assert.False(AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);
    }

    [Fact]
    public void An_operators_on_stays_on()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        WriteRaw("<OfferStationSaveOnDisconnect>true</OfferStationSaveOnDisconnect>");

        var config = AudioOutputConfig.Load(_dir);
        Assert.True(config.OfferStationSaveOnDisconnect);
        config.Save(_dir);
        Assert.True(AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);
    }

    /// <summary>
    /// The operator who ran round one's build: its migration turned the offer
    /// on and stamped version 1, or they then turned it off. Either way the
    /// file loads, the stored value stands, and the next save drops the dead
    /// element rather than carrying it forward.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_file_written_by_the_default_on_build_loads_and_keeps_its_value(bool stored)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string value = stored ? "true" : "false";
        WriteRaw("<OfferStationSaveOnDisconnect>" + value + "</OfferStationSaveOnDisconnect>"
            + "<StationSaveOfferVersion>1</StationSaveOfferVersion>");

        var config = AudioOutputConfig.Load(_dir);
        Assert.Equal(stored, config.OfferStationSaveOnDisconnect);

        config.Save(_dir);
        Assert.DoesNotContain("StationSaveOfferVersion", File.ReadAllText(ConfigPath));
        Assert.Equal(stored, AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);
    }

    [Fact]
    public void Dont_ask_again_turns_it_off_live_and_on_disk_and_in_the_live_config()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        WriteRaw("<OfferStationSaveOnDisconnect>true</OfferStationSaveOnDisconnect>");
        var live = AudioOutputConfig.Load(_dir);
        Assert.True(live.OfferStationSaveOnDisconnect);
        Radios.FlexBase.OfferStationSaveOnDisconnect = true;

        AudioOutputConfig.RecordStationSaveOfferTurnedOff(live, _dir);

        Assert.False(Radios.FlexBase.OfferStationSaveOnDisconnect);
        // The live object is the one power-off saves again, so it must agree.
        Assert.False(live.OfferStationSaveOnDisconnect);
        Assert.False(AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);
    }
}
