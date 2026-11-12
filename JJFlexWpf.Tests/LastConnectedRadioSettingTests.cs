using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.DiscoveryChain;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #999 round 4, ruled C2 and C3 by Noel 2026-11-09, through the Settings box's
/// own decisions (<see cref="SliceOverwriteSettingEdits"/>): with nothing
/// connected the box names the radio this program last CONNECTED to, across
/// restarts, shows that radio's own value and saves an edit to it; a radio only
/// discovered is never named; and only when no radio has ever connected does a
/// choice wait for the first radio that connects.
/// </summary>
/// <remarks>
/// <para>Windowless: every test asserts an MTA thread
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>). No dialog is
/// built; <see cref="SliceOverwriteSettingEdits.ResolveTarget(string)"/> is
/// the call Settings makes, reading the real record.</para>
/// <para>Settings live in the test run's throwaway root
/// (<see cref="TestSettingsRoot"/>), never the live %AppData%; the audio
/// settings in a scratch folder. The connection record is that root's
/// <c>radioConnectionCacheV1.xml</c>, written the way a working connect writes
/// it (FlexLib's <c>Radio</c> cannot be built here) and removed before and
/// after every test. Nothing else in this assembly reads it, and the classes
/// that do share this collection, so they never run at once.</para>
/// </remarks>
[Collection("SliceOverwriteWarning")]
public sealed class LastConnectedRadioSettingTests : IDisposable
{
    // Fresh serials per test: the throwaway settings root is shared by the whole run,
    // and each radio's config.xml in it is never cleared between tests.
    private readonly string A = FreshSerial("8610");
    private readonly string B = FreshSerial("8611");
    private readonly string Seen = FreshSerial("8612");

    private static int s_lastSerialSuffix = 4999;

    /// <summary>
    /// A radio serial no other test in this run has used. A random suffix could
    /// repeat within one run, and a repeat inherits the earlier test's saved
    /// per-radio choice. A counter cannot repeat, and cannot meet a previous
    /// run's files either, because <see cref="TestSettingsRoot"/> is new for
    /// every run. It starts at 5000, clear of the fixed serials other classes in
    /// this collection use (8600-0995, 8600-1040).
    /// </summary>
    internal static string FreshSerial(string model) =>
        "1234-5678-" + model + "-" + System.Threading.Interlocked.Increment(ref s_lastSerialSuffix);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-999-r4-" + Guid.NewGuid().ToString("N"));

    public LastConnectedRadioSettingTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Describe());
        Directory.CreateDirectory(_dir);
        ForgetEveryConnect();
        Restart();
    }

    public void Dispose()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
        ForgetEveryConnect();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string CachePath
    {
        get
        {
            string dir = KnownRadioRoster.CacheDirectory ?? "";
            Assert.True(dir.Length > 0 && TestSettingsRoot.Isolated, "the connection record must be in the throwaway root");
            Assert.StartsWith(Path.GetTempPath(), dir, StringComparison.OrdinalIgnoreCase);
            return Path.Combine(dir, "radioConnectionCacheV1.xml");
        }
    }

    /// <summary>No radio has ever connected, as far as the record knows.</summary>
    internal static void ForgetEveryConnect()
    {
        if (File.Exists(CachePath)) File.Delete(CachePath);
    }

    /// <summary>What a working connect leaves in the record, as RadioConnectionCache.RecordConnectedRadio writes it.</summary>
    internal static void RecordConnect(string serial, DateTime atUtc)
    {
        var ser = new XmlSerializer(typeof(RadioConnectionCacheFile));
        RadioConnectionCacheFile file;
        if (File.Exists(CachePath))
            using (var read = File.OpenRead(CachePath)) file = (RadioConnectionCacheFile)ser.Deserialize(read)!;
        else file = new RadioConnectionCacheFile();
        var entry = file.Entries.FirstOrDefault(e => string.Equals(e.Serial, serial, StringComparison.OrdinalIgnoreCase));
        if (entry == null) file.Entries.Add(entry = new RadioConnectionCacheEntry { Serial = serial, Model = "FLEX-8600" });
        entry.LanLastSeenUtc = atUtc;
        using var write = File.Create(CachePath);
        ser.Serialize(write, file);
    }

    /// <summary>A program start: a fresh store over the same audio settings, nothing held in memory.</summary>
    private void Restart()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null;
        RadioConfig.PendingWarnStore = new AudioOutputConfig.PendingSliceOverwriteWarningStore(() => _dir);
    }

    /// <summary>What a connect does with a waiting choice (FlexBase.ApplyChoicesWaitingForAConnect), then records the radio.</summary>
    private static void Connect(string serial)
    {
        RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(serial, KnownRadioRoster.LastConnectedSerial());
        RecordConnect(serial, DateTime.UtcNow);
    }

    private static bool SavedFor(string serial) => RadioConfig.LoadForRadio(serial).WarnBeforeSliceRecordingOverwritten;

    /// <summary>What OK or Apply does (SettingsDialog.CommitSliceOverwriteSetting).</summary>
    private static void Ok(SliceOverwriteSettingEdits edits) =>
        edits.Commit(
            (serial, warn) => SavedFor(serial) == warn || RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(serial, warn),
            warn => RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = warn,
            () => RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = null);

    /// <summary>What a Settings refresh does with nothing connected: the target and the value shown.</summary>
    private static (string Target, bool Shown) Open(SliceOverwriteSettingEdits edits)
    {
        string target = edits.ResolveTarget("");
        return (target, edits.Show(target, SavedFor, RadioConfig.PendingWarnBeforeSliceRecordingOverwritten));
    }

    [Fact]
    public void A_radio_only_seen_is_never_named()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(KnownRadioRoster.RecordSighting(Seen, "seen on the LAN", "", false, ""));
        Assert.True(KnownRadioRoster.RecordSighting(A, "seen remotely", "", true, "someone@example.com"));
        // Positive control: the roster knows them.
        Assert.Contains(KnownRadioRoster.Load(), e => e.Serial == Seen);

        var edits = new SliceOverwriteSettingEdits();
        var (target, shown) = Open(edits);
        Assert.Equal("", target);            // "applies to the first radio that connects"
        Assert.False(edits.TargetIsConnected);
        Assert.True(shown);
    }

    /// <summary>
    /// Codex's created state from its check of round 3 (B1): discovery first,
    /// THEN the edit, with nothing waiting beforehand.
    /// </summary>
    [Fact]
    public void A_choice_made_after_a_radio_was_only_discovered_waits_for_the_first_radio_to_connect()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(KnownRadioRoster.RecordSighting(A, "seen on the LAN", "", false, ""));
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);

        var edits = new SliceOverwriteSettingEdits();
        Assert.Equal(("", true), Open(edits));
        edits.Edit(false);
        Ok(edits);
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Assert.True(SavedFor(A));

        Restart();
        Connect(B);
        Assert.False(SavedFor(B));
        Assert.True(SavedFor(A));
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void After_a_connect_and_a_restart_Settings_names_that_radio_and_an_edit_saves_to_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Connect(A);
        // A newer sighting of another radio does not move it.
        Assert.True(KnownRadioRoster.RecordSighting(Seen, "seen on the LAN", "", false, ""));

        Restart();
        var edits = new SliceOverwriteSettingEdits();
        Assert.Equal((A, true), Open(edits));
        edits.Edit(false);
        Ok(edits);
        Assert.False(SavedFor(A));
        Assert.True(SavedFor(Seen));
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);   // nothing waits: it is A's own setting

        // Reopened: A's own saved value.
        Assert.Equal((A, false), Open(new SliceOverwriteSettingEdits()));
    }

    [Fact]
    public void A_different_radio_connecting_first_is_untouched()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        RecordConnect(A, DateTime.UtcNow.AddHours(-1));
        var edits = new SliceOverwriteSettingEdits();
        Assert.Equal((A, true), Open(edits));
        edits.Edit(false);
        Ok(edits);

        Restart();
        Connect(B);
        Assert.True(SavedFor(B));
        Assert.False(SavedFor(A));
        // B is now the radio last connected, with its own value.
        Assert.Equal((B, true), Open(new SliceOverwriteSettingEdits()));
    }

    /// <summary>
    /// Round 4 item 3: a choice found waiting beside a last-connected radio
    /// (left by a round 3 build, or a connect whose save failed) is shown
    /// against that radio, and OK saves it there and clears it, even untouched.
    /// </summary>
    [Fact]
    public void A_waiting_choice_beside_the_radio_last_connected_is_shown_against_it_and_OK_saves_it_there()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        RecordConnect(A, DateTime.UtcNow.AddHours(-1));
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;

        Restart();
        var edits = new SliceOverwriteSettingEdits();
        Assert.Equal((A, false), Open(edits));   // A's own config still says on
        Assert.True(SavedFor(A));
        Ok(edits);
        Assert.False(SavedFor(A));
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);

        Restart();
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
        Connect(B);
        Assert.True(SavedFor(B));
    }

    [Fact]
    public void Cancel_leaves_that_choice_waiting_and_the_connect_still_gives_it_to_the_radio_last_connected()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        RecordConnect(A, DateTime.UtcNow.AddHours(-1));
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;

        Restart();
        Assert.Equal((A, false), Open(new SliceOverwriteSettingEdits()));   // shown, then cancelled: no commit
        Restart();
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);

        Connect(B);
        Assert.True(SavedFor(B));
        Assert.False(SavedFor(A));
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void An_operator_edit_for_the_radio_last_connected_supersedes_the_waiting_choice()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        RecordConnect(A, DateTime.UtcNow.AddHours(-1));
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        var edits = new SliceOverwriteSettingEdits();
        Assert.Equal((A, false), Open(edits));
        edits.Edit(true);
        Ok(edits);
        Assert.True(SavedFor(A));
        Assert.Null(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);
    }

    [Fact]
    public void A_waiting_choice_is_never_shown_against_a_connected_radio()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        var edits = new SliceOverwriteSettingEdits();
        string target = edits.ResolveTarget(B);
        Assert.True(edits.TargetIsConnected);
        Assert.True(edits.Show(target, SavedFor, RadioConfig.PendingWarnBeforeSliceRecordingOverwritten));
        Ok(edits);
        Assert.True(SavedFor(B));
        Assert.False(RadioConfig.PendingWarnBeforeSliceRecordingOverwritten);   // left for the connect's own rule
    }

    /// <summary>
    /// The Settings box takes its radio only from <see cref="SliceOverwriteSettingEdits.ResolveTarget(string)"/>:
    /// no sighting stamp, and no <c>SelectedRadioSerial</c>, which the connect
    /// sets BEFORE it knows whether it worked (FlexBase.cs, "Remembered so the
    /// network diagnostic still has something to probe after a failed or
    /// dropped connect").
    /// </summary>
    [Fact]
    public void Settings_names_a_radio_by_connects_only()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string root = RepoRoot();
        string dialog = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs", "SettingsDialog.SliceOverwrite.cs"));
        string edits = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "SliceOverwriteSettingEdits.cs"));
        foreach (var source in new[] { dialog, edits })
        {
            Assert.DoesNotContain("LastSeenUtc", source);
            Assert.DoesNotContain("SelectedRadioSerial", source);
        }
        Assert.Contains("_warnOverwriteEdits.ResolveTarget(connected);", dialog);
        Assert.Contains("KnownRadioRoster.LastConnectedSerial()", edits);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
