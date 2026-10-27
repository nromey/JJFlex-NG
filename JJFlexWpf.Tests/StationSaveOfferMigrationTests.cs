using System;
using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #759: the disconnect offer to save the station defaults ON, and that has to
/// reach the operators who already have a configuration, not only new ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the migration is the load-bearing half.</b> Every audioConfig.xml
/// written before #759 already holds <c>OfferStationSaveOnDisconnect</c> as
/// <c>false</c> - the serializer writes an unset bool - and the file beats any
/// code default. So changing the initializer alone would compile, review clean
/// and change nothing for anyone who has run the application, while the code
/// claimed otherwise. These tests read real files from a scratch folder.
/// </para>
/// <para>
/// Windowless: the config is plain data and XML. Each test asserts it runs on
/// an MTA thread, where WPF cannot construct an element.
/// </para>
/// </remarks>
[Collection("StationSaveOfferStatic")]
public sealed class StationSaveOfferMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-759-" + Guid.NewGuid().ToString("N"));
    private readonly bool _savedStatic = Radios.FlexBase.OfferStationSaveOnDisconnect;

    public StationSaveOfferMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Radios.FlexBase.OfferStationSaveOnDisconnect = _savedStatic;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void WriteRaw(string inner) =>
        File.WriteAllText(Path.Combine(_dir, "audioConfig.xml"),
            "<?xml version=\"1.0\"?>\r\n<AudioOutputConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
            + "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" + inner + "</AudioOutputConfig>");

    [Fact]
    public void An_existing_config_holding_false_with_no_recorded_choice_comes_out_on()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        WriteRaw("<OfferStationSaveOnDisconnect>false</OfferStationSaveOnDisconnect>");

        var loaded = AudioOutputConfig.Load(_dir);

        Assert.True(loaded.OfferStationSaveOnDisconnect);
        Assert.Equal(AudioOutputConfig.CurrentStationSaveOfferVersion, loaded.StationSaveOfferVersion);
    }

    [Fact]
    public void An_operators_off_once_recorded_stays_off()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        WriteRaw("<OfferStationSaveOnDisconnect>false</OfferStationSaveOnDisconnect>");

        // Migrated on the way in, then the operator turns it off in Settings
        // and the config is saved - which stamps the version with it.
        var config = AudioOutputConfig.Load(_dir);
        config.OfferStationSaveOnDisconnect = false;
        config.Save(_dir);

        var again = AudioOutputConfig.Load(_dir);
        Assert.False(again.OfferStationSaveOnDisconnect);

        // And it survives a second round trip, so it is not a one-launch effect.
        again.Save(_dir);
        Assert.False(AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);
    }

    [Fact]
    public void A_file_from_before_the_setting_existed_and_a_first_run_both_come_out_on()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        WriteRaw("");
        Assert.True(AudioOutputConfig.Load(_dir).OfferStationSaveOnDisconnect);

        File.Delete(Path.Combine(_dir, "audioConfig.xml"));
        var fresh = AudioOutputConfig.Load(_dir);
        Assert.True(fresh.OfferStationSaveOnDisconnect);
        Assert.Equal(AudioOutputConfig.CurrentStationSaveOfferVersion, fresh.StationSaveOfferVersion);
    }

    [Fact]
    public void Dont_ask_again_turns_it_off_live_and_on_disk_and_in_the_live_config()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        WriteRaw("<OfferStationSaveOnDisconnect>false</OfferStationSaveOnDisconnect>");
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
