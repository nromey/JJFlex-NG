#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Radios.DiscoveryChain;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #999 N2, Codex's check of round 4: a waiting "warn before a recording on the
/// radio is overwritten" choice keeps the radio it is for once a save to that
/// radio has been refused, so a later connect of a different radio cannot take
/// it. The ruling it keeps (C2 and C3, Noel 2026-11-09): a choice belongs to the
/// radio last CONNECTED, and waits for the first radio that connects only when
/// no radio ever had; then it goes to that radio, once.
/// </summary>
/// <remarks>
/// Every test runs in a private settings root
/// (<see cref="RadioConfigStaticsScope"/>), never the live %AppData%. A connect
/// is what <c>FlexBase</c> does once it works: apply the waiting choice
/// (<see cref="FlexBase.ApplyChoicesWaitingForAConnect"/>), then record the
/// radio as last connected, written here the way
/// <see cref="RadioConnectionCache.RecordConnectedRadio"/> writes it. A locked
/// config is an exclusive handle on that radio's <c>config.xml</c>, which is how
/// Codex's probe refused the save.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class PendingChoiceTargetTests : IDisposable
{
    private const string A = "1234-5678-8600-0961", B = "1234-5678-8600-0962", C = "1234-5678-8600-0963";
    private readonly RadioConfigStaticsScope _scope = new(nameof(PendingChoiceTargetTests));

    public PendingChoiceTargetTests()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnChoiceWaiting = null;
    }

    public void Dispose()
    {
        try
        {
            RadioConfig.PendingWarnStore = null;
            RadioConfig.PendingWarnChoiceWaiting = null;
        }
        finally { _scope.Dispose(); }
    }

    /// <summary>A stand-in for audioConfig.xml: the choice and its radio, in one text file.</summary>
    private sealed class FileStore : IPendingChoiceStore
    {
        private readonly string _path;
        public FileStore(string path) => _path = path;
        public PendingWarnChoice? Read()
        {
            if (!File.Exists(_path)) return null;
            var parts = File.ReadAllText(_path).Split('|');
            string target = parts.Length > 1 ? parts[1] : "";
            return parts[0] switch { "true" => new(true, target), "false" => new(false, target), _ => null };
        }
        public bool Write(PendingWarnChoice? value)
        {
            File.WriteAllText(_path, value is PendingWarnChoice c
                ? (c.Warn ? "true" : "false") + (c.Target.Length > 0 ? "|" + c.Target : "")
                : "");
            return true;
        }
    }

    private string StorePath => _scope.PathTo("pending-choice.txt");

    /// <summary>A restart: memory is gone, and a fresh store is wired over the same file.</summary>
    private void Restart()
    {
        RadioConfig.PendingWarnStore = null;
        RadioConfig.PendingWarnChoiceWaiting = null;
        RadioConfig.PendingWarnStore = new FileStore(StorePath);
    }

    private string CachePath => Path.Combine(_scope.Directory, "radioConnectionCacheV1.xml");
    private DateTime _clock = DateTime.UtcNow.AddHours(-1);

    private void RecordConnect(string serial)
    {
        var ser = new XmlSerializer(typeof(RadioConnectionCacheFile));
        RadioConnectionCacheFile file;
        if (File.Exists(CachePath))
            using (var read = File.OpenRead(CachePath)) file = (RadioConnectionCacheFile)ser.Deserialize(read)!;
        else file = new RadioConnectionCacheFile();
        var entry = file.Entries.FirstOrDefault(e => string.Equals(e.Serial, serial, StringComparison.OrdinalIgnoreCase));
        if (entry == null) file.Entries.Add(entry = new RadioConnectionCacheEntry { Serial = serial, Model = "FLEX-8600" });
        // Strictly later than any earlier connect in the same test.
        entry.LanLastSeenUtc = _clock = _clock.AddMinutes(1);
        using var write = File.Create(CachePath);
        ser.Serialize(write, file);
    }

    private static FlexBase Rig() => new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };

    /// <summary>A working connect: apply what waits, then record the radio as last connected.</summary>
    private static void Connect(string serial, Action record)
    {
        Rig().ApplyChoicesWaitingForAConnect(serial);
        record();
    }

    private static bool Saved(string serial) => RadioConfig.LoadForRadio(serial).WarnBeforeSliceRecordingOverwritten;

    private string ConfigFile(string serial) => Path.Combine(_scope.Directory, "radios", serial, "config.xml");

    /// <summary>Hold a radio's config the way another writer would: nothing else may open it.</summary>
    private FileStream Lock(string serial) =>
        File.Open(ConfigFile(serial), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void Codex_N2_a_choice_refused_by_the_radio_last_connected_still_reaches_it_after_two_other_connects()
    {
        Restart();
        // A was the radio last connected; its config exists, warning on.
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(A, true));
        RecordConnect(A);
        // A choice waits beside it, naming no radio (an older build's leftover).
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;

        // B connects while A's config is locked: the save to A is refused.
        using (Lock(A)) Connect(B, () => RecordConnect(B));
        Assert.Equal(B, KnownRadioRoster.LastConnectedSerial());     // B is the radio last connected now
        Assert.Equal(new PendingWarnChoice(false, A), RadioConfig.PendingWarnChoiceWaiting);
        Assert.True(Saved(B));
        Assert.False(File.Exists(ConfigFile(B)));

        // A is unlocked, the program restarts, and C connects with no gate retry in between.
        Restart();
        Assert.Equal(new PendingWarnChoice(false, A), RadioConfig.PendingWarnChoiceWaiting);
        Connect(C, () => RecordConnect(C));

        Assert.False(Saved(A));   // A receives OFF
        Assert.True(Saved(B));    // B untouched
        Assert.True(Saved(C));    // C untouched
        Assert.False(File.Exists(ConfigFile(B)));
        Assert.False(File.Exists(ConfigFile(C)));
        Assert.Null(RadioConfig.PendingWarnChoiceWaiting);
        Assert.Equal("", File.ReadAllText(StorePath));

        // Once: a later connect finds nothing waiting.
        Connect(B, () => RecordConnect(B));
        Assert.True(Saved(B));
    }

    [Fact]
    public void The_same_sequence_without_a_restart_and_with_no_store_wired()
    {
        // Null-safety: the store is not wired, the choice lives in memory for the run.
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(A, true));
        RecordConnect(A);
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;

        using (Lock(A)) Connect(B, () => RecordConnect(B));
        Assert.Equal(A, RadioConfig.PendingWarnChoiceWaiting?.Target);
        Connect(C, () => RecordConnect(C));

        Assert.False(Saved(A));
        Assert.True(Saved(B));
        Assert.True(Saved(C));
        Assert.Null(RadioConfig.PendingWarnChoiceWaiting);
    }

    [Fact]
    public void The_first_radio_ever_to_connect_keeps_its_choice_when_its_save_is_refused()
    {
        Restart();
        // No radio has ever connected: the choice waits for the first one.
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = false;
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(A, true));   // A's config exists (seen, never connected)
        Assert.Equal("", KnownRadioRoster.LastConnectedSerial());

        // A is the first to connect, and its config is locked.
        using (Lock(A)) Connect(A, () => RecordConnect(A));
        Assert.Equal(new PendingWarnChoice(false, A), RadioConfig.PendingWarnChoiceWaiting);

        // B and then C connect. Neither takes A's choice; A's own retry does.
        Connect(B, () => RecordConnect(B));
        Assert.False(Saved(A));
        Assert.True(Saved(B));
        Assert.Null(RadioConfig.PendingWarnChoiceWaiting);
        Connect(C, () => RecordConnect(C));
        Assert.True(Saved(C));
    }

    [Fact]
    public void A_choice_that_names_its_radio_ignores_the_radio_last_connected()
    {
        RadioConfig.PendingWarnChoiceWaiting = new PendingWarnChoice(false, A);
        // The gate's retry or a connect passes whatever was last connected; the named radio wins.
        Assert.True(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(C, lastConnectedBefore: B));
        Assert.False(Saved(A));
        Assert.True(Saved(B));
        Assert.True(Saved(C));
        Assert.Null(RadioConfig.PendingWarnChoiceWaiting);
    }

    [Fact]
    public void A_choice_that_names_its_radio_waits_on_while_that_radio_stays_locked()
    {
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(A, true));
        RadioConfig.PendingWarnChoiceWaiting = new PendingWarnChoice(false, A);
        using (Lock(A))
        {
            Assert.False(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(B, lastConnectedBefore: C));
            Assert.False(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(A, lastConnectedBefore: B));
        }
        Assert.Equal(new PendingWarnChoice(false, A), RadioConfig.PendingWarnChoiceWaiting);
        Assert.False(File.Exists(ConfigFile(B)));
        Assert.False(File.Exists(ConfigFile(C)));
    }

    [Fact]
    public void A_choice_saved_by_an_older_build_names_no_radio_and_follows_the_round_four_rule()
    {
        // An older build wrote the setting alone.
        File.WriteAllText(StorePath, "false");
        RadioConfig.PendingWarnStore = new FileStore(StorePath);
        Assert.Equal(new PendingWarnChoice(false, ""), RadioConfig.PendingWarnChoiceWaiting);

        // With a radio last connected, it is that radio's, and the radio connecting first is untouched.
        RecordConnect(A);
        Connect(B, () => RecordConnect(B));
        Assert.False(Saved(A));
        Assert.True(Saved(B));
        Assert.Null(RadioConfig.PendingWarnChoiceWaiting);

        // With no radio ever connected, the first to connect receives it, once.
        File.Delete(CachePath);
        File.WriteAllText(StorePath, "false");
        Assert.Equal("", KnownRadioRoster.LastConnectedSerial());
        Connect(C, () => RecordConnect(C));
        Assert.False(Saved(C));
        Assert.Null(RadioConfig.PendingWarnChoiceWaiting);
    }

    [Fact]
    public void A_malformed_radio_id_is_never_recorded_as_the_target()
    {
        RadioConfig.PendingWarnBeforeSliceRecordingOverwritten = true;
        Assert.False(RadioConfig.ApplyPendingWarnBeforeSliceRecordingOverwritten(B, lastConnectedBefore: "not-a-serial"));
        Assert.Equal(new PendingWarnChoice(true, ""), RadioConfig.PendingWarnChoiceWaiting);
    }

    [Fact]
    public void The_choice_and_its_radio_read_back_trimmed_and_a_default_names_no_radio()
    {
        Assert.Equal("", default(PendingWarnChoice).Target);
        Assert.Equal(A, new PendingWarnChoice(true, "  " + A + " ").Target);
        Assert.True(new PendingWarnChoice(true, A).IsFor(A.ToLowerInvariant()));
        Assert.False(new PendingWarnChoice(true, "").IsFor(""));
        Assert.Equal("", RadioConfig.PendingWarnRadioFor(new PendingWarnChoice(true, ""), "", ""));
        Assert.Equal(B, RadioConfig.PendingWarnRadioFor(new PendingWarnChoice(true, ""), C, B));
        Assert.Equal(C, RadioConfig.PendingWarnRadioFor(new PendingWarnChoice(true, ""), C, ""));
        Assert.Equal(A, RadioConfig.PendingWarnRadioFor(new PendingWarnChoice(true, A), C, B));
    }
}
