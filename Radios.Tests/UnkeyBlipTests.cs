using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The radio's momentary Mox:True inside our own unkey (#957). The 8600's
/// status batch for an unkey of ours reads InterlockState UnkeyRequested, then
/// Mox:True, Ready, Mox:False. #651 stopped that blip restarting the mic peak
/// sample; the same rule now covers every key-down hook in noteRadioMox: the
/// blip begins no transmit episode, does not move the falling tone's origin,
/// and opens no new txMeters evidence window. An ordinary key-down reported
/// while the radio is ready still does all three (the control).
/// </summary>
/// <remarks>
/// Drives the production Mox path on a real FlexBase over the vendor fixture's
/// fake transport: no radio, nothing keys. What the blip still does, by
/// design here, is set _Transmit and raise TransmitChange; changing that is
/// reported, not built.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnkeyBlipTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(UnkeyBlipTests));
    public void Dispose() => _scope.Dispose();

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly MethodInfo NoteRadioMox = typeof(FlexBase).GetMethod("noteRadioMox", Private)!;
    private static readonly FieldInfo EpisodeMark = typeof(FlexBase).GetField("_txEpisodeMark", Private)!;
    private static readonly FieldInfo CarrierOrigin = typeof(FlexBase).GetField("_lastCarrierOrigin", Private)!;
    private static readonly FieldInfo LineWindow = typeof(FlexBase).GetField("_txLineWindow", Private)!;

    private static void Mox(FlexBase rig, bool on) => NoteRadioMox.Invoke(rig, new object[] { on });

    private sealed record Marks(long Episode, long Origin, int Window);

    private static Marks Read(FlexBase rig) =>
        new((long)EpisodeMark.GetValue(rig)!, (long)CarrierOrigin.GetValue(rig)!, (int)LineWindow.GetValue(rig)!);

    /// <summary>Plant values a key-down would overwrite, so a hook that runs is seen to run.</summary>
    private static Marks Plant(RigOnVendorRadio f)
    {
        EpisodeMark.SetValue(f.Rig, -7L);
        CarrierOrigin.SetValue(f.Rig, -7L);
        return Read(f.Rig);
    }

    [Fact]
    public void The_fixture_reaches_the_production_members()
    {
        Assert.NotNull(NoteRadioMox);
        Assert.NotNull(EpisodeMark);
        Assert.NotNull(CarrierOrigin);
        Assert.NotNull(LineWindow);
    }

    [Fact]
    public void The_momentary_Mox_in_the_radios_unkey_batch_begins_no_transmission()
    {
        using var f = new RigOnVendorRadio("unkey-blip");
        Mox(f.Rig, true);                      // our key's echo
        Mox(f.Rig, false);                     // our release: the Mox cache falls at once
        var before = Plant(f);
        f.Vendor.Status("interlock state=UNKEY_REQUESTED source=SW tx_client_handle=0x" + RigOnVendorRadio.OurHandle.ToString("X8"));
        Mox(f.Rig, true);                      // the momentary Mox:True in the unkey batch
        f.Vendor.Status("interlock state=READY");
        Mox(f.Rig, false);

        Assert.Equal(before, Read(f.Rig));
    }

    [Fact]
    public void A_key_down_reported_while_the_radio_is_ready_still_begins_one()
    {
        // The control: the rule is narrow, so an ordinary key-down, whose Mox
        // can arrive before its own interlock report, runs every hook.
        using var f = new RigOnVendorRadio("unkey-blip-control");
        var before = Plant(f);
        f.Vendor.Status("interlock state=READY");
        Mox(f.Rig, true);

        var after = Read(f.Rig);
        Assert.NotEqual(before.Episode, after.Episode);
        Assert.NotEqual(before.Origin, after.Origin);
        Assert.NotEqual(before.Window, after.Window);
    }

    [Fact]
    public void Every_key_down_hook_asks_the_one_rule_and_the_reference_restart_with_them()
    {
        string src = Source("Radios", "FlexBase.cs");
        string body = Between(src, "private void noteRadioMox(bool mox)", "\n        }\n");
        Assert.Contains("bool keyDown = mox && !oldTransmit && !RadioReportsUnkeying();", body, StringComparison.Ordinal);
        Assert.Contains("if (keyDown) NoteTransmitEpisodeBegins();", body, StringComparison.Ordinal);
        Assert.Contains("if (keyDown) NoteCarrierRose();", body, StringComparison.Ordinal);
        Assert.Contains("if (keyDown && !_tuneCycleActive) beginTxLineWindow();", body, StringComparison.Ordinal);
        Assert.Contains("if (keyDown && TxReferenceArmed)", body, StringComparison.Ordinal);
        // The mic sample asks the same rule itself (#651), through the same question.
        Assert.Contains("if (RadioReportsUnkeying())", Between(src, "private void restartAudioSampleOnRadioKeyDown()", "\n        }\n"),
            StringComparison.Ordinal);
    }

    private static string Between(string text, string start, string end)
    {
        int at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + start);
        int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        Assert.True(stop > at, "no end after: " + start);
        return text.Substring(at, stop - at);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }
}
