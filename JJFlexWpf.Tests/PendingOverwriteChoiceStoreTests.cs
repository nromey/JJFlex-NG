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
        Assert.True(store.Write(new PendingWarnChoice(false, "")));
        // The live config is loaded once (at connect) and saved again at power-off.
        var live = AudioOutputConfig.Load(_dir);
        Assert.Equal("false", live.PendingSliceOverwriteWarning);
        Assert.True(store.Write(null));             // the connect spent it
        live.Save(_dir);                            // power-off
        Assert.Null(store.Read());

        var older = AudioOutputConfig.Load(_dir);   // loaded before a newer choice
        Assert.True(store.Write(new PendingWarnChoice(true, "")));
        older.Save(_dir);
        Assert.True(store.Read()?.Warn);
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

    // #999 N2: the waiting choice keeps the radio it is for.

    [Fact]
    public void The_radio_a_choice_is_for_is_saved_beside_it_and_survives_a_restart_and_a_later_live_save()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var store = Restart();
        var live = AudioOutputConfig.Load(_dir);              // loaded before the choice was pinned
        RadioConfig.PendingWarnChoiceWaiting = new PendingWarnChoice(false, Connected);
        string xml = File.ReadAllText(ConfigPath);
        Assert.Contains("<PendingWarnBeforeSliceRecordingOverwritten>false</PendingWarnBeforeSliceRecordingOverwritten>", xml);
        Assert.Contains("<PendingWarnBeforeSliceRecordingOverwrittenFor>" + Connected + "</PendingWarnBeforeSliceRecordingOverwrittenFor>", xml);

        live.Save(_dir);                                       // power-off: the choice and its radio are carried over
        Restart();
        Assert.Equal(new PendingWarnChoice(false, Connected), RadioConfig.PendingWarnChoiceWaiting);

        // Clearing clears both.
        Assert.True(store.Write(null));
        Assert.Null(store.Read());
        Assert.Contains("<PendingWarnBeforeSliceRecordingOverwrittenFor />", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void An_audioConfig_from_before_N2_reads_as_a_choice_that_names_no_radio()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var store = Restart();
        Assert.True(store.Write(new PendingWarnChoice(false, Connected)));
        // What an older build leaves: the setting, and no element for the radio.
        var doc = System.Xml.Linq.XDocument.Load(ConfigPath);
        doc.Root!.Element("PendingWarnBeforeSliceRecordingOverwrittenFor")!.Remove();
        doc.Save(ConfigPath);

        Restart();
        Assert.Equal(new PendingWarnChoice(false, ""), RadioConfig.PendingWarnChoiceWaiting);
    }

    [Fact]
    public void Settings_shows_a_choice_that_names_its_radio_against_that_radio_only_and_saves_it_there()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Restart();
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(Connected, true));
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(Discovered, true));
        RadioConfig.PendingWarnChoiceWaiting = new PendingWarnChoice(false, Connected);
        Func<string, bool> saved = s => RadioConfig.LoadForRadio(s).WarnBeforeSliceRecordingOverwritten;
        Func<string, bool, bool> save = (s, w) => saved(s) == w || RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(s, w);

        // Another radio is the one last connected: the box shows ITS setting, and OK leaves the choice waiting.
        var edits = new SliceOverwriteSettingEdits();
        string other = edits.ResolveTarget("", Discovered);
        Assert.True(edits.ShowWithChoice(other, saved, RadioConfig.PendingWarnChoiceWaiting));
        edits.Commit(save, warn => RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = warn,
            () => RadioConfig.PendingWarnChoiceWaiting = null);
        Assert.Equal(new PendingWarnChoice(false, Connected), RadioConfig.PendingWarnChoiceWaiting);
        Assert.True(saved(Discovered));

        // With no radio at all named, it is not offered as the first radio's value either.
        edits = new SliceOverwriteSettingEdits();
        Assert.True(edits.ShowWithChoice(edits.ResolveTarget("", ""), saved, RadioConfig.PendingWarnChoiceWaiting));

        // Its own radio, even connected, shows it, and OK saves it there and spends it.
        edits = new SliceOverwriteSettingEdits();
        string own = edits.ResolveTarget(Connected, "");
        Assert.False(edits.ShowWithChoice(own, saved, RadioConfig.PendingWarnChoiceWaiting));
        edits.Commit(save, warn => RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = warn,
            () => RadioConfig.PendingWarnChoiceWaiting = null);
        Assert.False(saved(Connected));
        Assert.True(saved(Discovered));
        Assert.Null(RadioConfig.PendingWarnChoiceWaiting);
    }
}
