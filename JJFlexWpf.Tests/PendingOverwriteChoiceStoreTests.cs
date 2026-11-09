using System;
using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #999 round 3, C5a with the orchestrator's correction: a "warn before a
/// recording on the radio is overwritten" choice made before any radio
/// connected waits in audioConfig.xml across restarts, Settings keeps showing
/// it while it waits, and only a CONNECTED radio receives it; a radio last
/// used or merely discovered does not.
/// </summary>
/// <remarks>
/// Windowless: every test asserts an MTA thread
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>). The audio
/// settings live in a scratch folder; per-radio configs in the test process's
/// throwaway settings root (<see cref="TestSettingsRoot"/>), never the live
/// %AppData%. Settings' own refresh is reached through
/// <see cref="SliceOverwriteSettingEdits.ResolveTarget"/>, the one decision it
/// makes; no dialog is built.
/// </remarks>
[Collection("SliceOverwriteWarning")]
public sealed class PendingOverwriteChoiceStoreTests : IDisposable
{
    // Fresh serials per test: the throwaway settings root is shared by the whole run.
    private readonly string Connected = "1234-5678-8600-" + Random.Shared.Next(1000, 9999);
    private readonly string Discovered = "1234-5678-8601-" + Random.Shared.Next(1000, 9999);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-999-r3-" + Guid.NewGuid().ToString("N"));

    public PendingOverwriteChoiceStoreTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Describe());
        Directory.CreateDirectory(_dir);
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
        LastConnectedRadioSettingTests.ForgetEveryConnect();
    }

    public void Dispose()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
        LastConnectedRadioSettingTests.ForgetEveryConnect();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_dir, "audioConfig.xml");

    /// <summary>What startup does, against the scratch folder: a fresh store, nothing held in memory.</summary>
    private AudioOutputConfig.PendingSliceOverwriteWarningStore Restart()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
        var store = new AudioOutputConfig.PendingSliceOverwriteWarningStore(() => _dir);
        RadioConfig.PendingWarnStore = store;
        return store;
    }

    [Fact]
    public void The_choice_survives_a_restart_in_audioConfig_and_changes_nothing_else_there()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var before = AudioOutputConfig.Load(_dir);
        before.RecorderBeepsEnabled = false;
        before.Save(_dir);

        Restart();
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Assert.Contains("<PendingWarnBeforeSliceRecordingOverwritten>false</PendingWarnBeforeSliceRecordingOverwritten>",
            File.ReadAllText(ConfigPath));

        Restart();
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.False(AudioOutputConfig.Load(_dir).RecorderBeepsEnabled);
    }

    [Fact]
    public void A_live_config_saved_later_never_writes_a_spent_or_a_newer_choice_back()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var store = Restart();
        Assert.True(store.Write(false));
        // The live config is loaded once (at connect) and saved again at power-off.
        var live = AudioOutputConfig.Load(_dir);
        Assert.Equal("false", live.PendingSliceOverwriteWarning);
        Assert.True(store.Write(null));             // the connect spent it
        live.Save(_dir);                            // power-off
        Assert.Null(store.Read());

        var older = AudioOutputConfig.Load(_dir);   // loaded before a newer choice
        Assert.True(store.Write(true));
        older.Save(_dir);
        Assert.True(store.Read());
    }

    [Fact]
    public void A_discovered_radio_does_not_receive_it_and_the_box_keeps_showing_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Restart();
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;

        // Settings refreshes with nothing connected, after discovery has seen a
        // radio. No radio has ever connected, so it names none (round 4, C3).
        Assert.True(KnownRadioRoster.RecordSighting(Discovered, "seen on the LAN", "", false, ""));
        var edits = new SliceOverwriteSettingEdits();
        string target = edits.ResolveTarget(connectedSerial: "");
        Assert.Equal("", target);
        Assert.False(edits.Show(target, _ => throw new InvalidOperationException("no radio's config is read"),
            RadioConfig.PendingWarnBeforeSliceRecordingOverwritten));
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.True(RadioConfig.LoadForRadio(Discovered).WarnBeforeSliceRecordingOverwritten);

        // Restarted, still waiting.
        Restart();
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void An_unsaved_choice_for_the_next_radio_does_not_move_to_a_discovered_one()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Restart();
        var edits = new SliceOverwriteSettingEdits();
        Assert.True(edits.Show(edits.ResolveTarget(""), _ => true, null));
        edits.Edit(false);
        // A radio is discovered while Settings is open: the edit stays for the first radio that connects.
        Assert.True(KnownRadioRoster.RecordSighting(Discovered, "seen on the LAN", "", false, ""));
        string target = edits.ResolveTarget("");
        Assert.Equal("", target);
        Assert.False(edits.Show(target, _ => true, null));
        edits.Commit((_, _) => throw new InvalidOperationException("nothing is saved to a radio"),
            warn => RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = warn);
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void The_first_radio_that_connects_receives_it_once()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Restart();
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Assert.True(KnownRadioRoster.RecordSighting(Discovered, "seen on the LAN", "", false, ""));

        // What the connect does (FlexBase.ApplyChoicesWaitingForAConnect), then records it.
        Assert.True(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(Connected, KnownRadioRoster.LastConnectedSerial()));
        LastConnectedRadioSettingTests.RecordConnect(Connected, DateTime.UtcNow);
        Assert.False(RadioConfig.LoadForRadio(Connected).WarnBeforeSliceRecordingOverwritten);
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);

        // Spent, across a restart: nothing waits, and the box names the radio last connected, not the one seen.
        Restart();
        Assert.True(KnownRadioRoster.RecordSighting(Discovered, "seen again", "", false, ""));
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.Equal(Connected, new SliceOverwriteSettingEdits().ResolveTarget(""));
        Assert.True(RadioConfig.LoadForRadio(Discovered).WarnBeforeSliceRecordingOverwritten);
    }
}
